// Publication copy modified by repository maintainer on 2026-11-10.
// Original authors and licenses: ../../docs/ATTRIBUTION.md.
// NX_MCP_Loader — NXOpen .NET resident plugin (V2)
// Loaded automatically by NX via a "startup" folder (ufsta/Startup user exit).
// NamedPipe command channel -> thread-safe queue -> SendMessage scheduler on
// the NX main thread executes NXOpen operations. No journal, no Alt+F8, no
// keyboard simulation.
//
// Scheduler: NX's MFC main thread does not pump WinForms messages, so a
// System.Windows.Forms.Timer never fires here. A plain Win32 hidden window
// (NativeWindow) receives our custom message via SendMessage (verified: the NX
// main thread dispatches it synchronously) and runs the NXOpen ops on the NX
// main thread. The pipe/background thread NEVER calls NXOpen directly.
//
// Command protocol (UTF-8 line over named pipe "nx_mcp_loader"):
//   ping
//   nx_status
//   nx_create_part <prt-path>
//   nx_save_part
//   nx_release
//   nx_create_sketch <XY|XZ|YZ>
//   nx_sketch_line <sketch_id> <x1> <y1> <x2> <y2>
//   nx_sketch_rectangle <sketch_id> <cx> <cy> <w> <h>
//   nx_sketch_circle <sketch_id> <cx> <cy> <diameter>
//   nx_sketch_arc <sketch_id> <cx> <cy> <radius> <start_deg> <end_deg>
//   nx_finish_sketch <sketch_id>
//   nx_extrude <sketch_id> <distance> [reverse=0|1] [start_offset] [create|subtract] [body_id]
//   nx_hole <body_id> <cx> <cy> <diameter> <depth> [start_offset]
//   nx_edge_blend <body_id> <radius> [edge_indices csv]
//   nx_chamfer <body_id> <offset> [edge_indices csv]
//   nx_unite <target_body_id> <tool_body_ids csv>
//   nx_revolve <sketch_id> <ax> <ay> <bx> <by> <angle> [reverse=0|1]
//   nx_mirror <body_id> <XY|XZ|YZ> [offset]
//   nx_linear_pattern <body_id> <X|Y|Z> <count> <spacing> [reverse=0|1]
//   nx_fit_view
//   nx_export_step <step-path>
//   nx_build_plate_test           (single-command acceptance part)
//   create_block <w> <h> <d>      (kept for backward compatibility)
//
// Responses are one line of JSON: {"ok":true,...} or {"ok":false,"error":"..."}.
//
// The loader stays resident for the whole NX session. nx_release only clears
// the current task state and restores normal NX interaction; it never stops
// the pipe server or unloads the plugin.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using NXOpen;
using NXOpen.Features;
using NXOpen.GeometricUtilities;
using NXOpen.Drawings;
using NXOpen.Annotations;

public static partial class NX_MCP_Loader
{
    private const string PipeName = "nx_mcp_loader";
    private const int WmSelfTest = 0x8000 + 1; // WM_APP+1

    private static readonly ConcurrentQueue<string> CmdQueue = new ConcurrentQueue<string>();
    private static readonly ConcurrentQueue<string> RespQueue = new ConcurrentQueue<string>();
    private static NamedPipeServerStream _pipe;
    private static System.Threading.Thread _pipeThread;
    private static TimerWindow _timerWindow;
    private static volatile bool _started;

    // ---- task state ----------------------------------------------------
    private static Session _session;
    private static NXOpen.Part _part;
    private static readonly Dictionary<string, Sketch> _sketches = new Dictionary<string, Sketch>();
    private static readonly Dictionary<string, string> _sketchPlanes = new Dictionary<string, string>();
    private static readonly Dictionary<string, Body> _bodies = new Dictionary<string, Body>();
    private static int _sketchCounter;
    private static int _bodyCounter;
    private static string _partPath;

    private static string LogPath
    {
        get
        {
            string ws = null;
            try { ws = Environment.GetEnvironmentVariable("NX_MCP_WORKSPACE"); } catch { }
            if (string.IsNullOrEmpty(ws))
            {
                ws = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "NX_MCP_WORKSPACE");
            }
            try { Directory.CreateDirectory(ws); } catch { }
            return Path.Combine(ws, "nx_mcp_loader.log");
        }
    }

    private static void Log(string msg)
    {
        try
        {
            File.AppendAllText(LogPath,
                DateTime.Now.ToString("HH:mm:ss") + " " + msg + Environment.NewLine,
                Encoding.UTF8);
        }
        catch { }
    }

    // ---- JSON helpers --------------------------------------------------
    private static string Esc(string s)
    {
        if (s == null) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static string OkJson(string result)
    {
        return "{\"ok\":true,\"result\":\"" + Esc(result) + "\"}";
    }

    private static string OkJson(string key, string value, string result)
    {
        return "{\"ok\":true,\"" + Esc(key) + "\":\"" + Esc(value) + "\",\"result\":\"" + Esc(result) + "\"}";
    }

    private static string OkJsonList(string key, List<string> values, string result)
    {
        var sb = new StringBuilder();
        sb.Append("{\"ok\":true,\"").Append(Esc(key)).Append("\":[");
        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0) sb.Append(",");
            sb.Append("\"").Append(Esc(values[i])).Append("\"");
        }
        sb.Append("],\"result\":\"").Append(Esc(result)).Append("\"}");
        return sb.ToString();
    }

    private static string ErrJson(string msg)
    {
        return "{\"ok\":false,\"error\":\"" + Esc(msg) + "\"}";
    }

    // ---- NX user exits -------------------------------------------------
    public static int GetUnloadOption(string dummy)
    {
        // AtTermination (UF_UNLOAD_UG_TERMINATE = 3): keep resident for the whole NX session
        return 3;
    }

    public static int Startup()
    {
        if (_started) return 0;
        _started = true;
        try
        {
            Log("Startup() called; thread=" + System.Threading.Thread.CurrentThread.ManagedThreadId +
                " messageLoop=" + Application.MessageLoop);
            _pipeThread = new System.Threading.Thread(PipeLoop) { IsBackground = true };
            _pipeThread.Start();
            _timerWindow = new TimerWindow();
            _timerWindow.Start();
            Log("pipe thread + scheduler window started");
        }
        catch (Exception e)
        {
            Log("Startup ERR: " + e);
        }
        return 0;
    }

    // ---- main-thread scheduler ----------------------------------------
    private sealed class TimerWindow : NativeWindow
    {
        public void Start()
        {
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmSelfTest)
            {
                DrainQueue();
            }
            base.WndProc(ref m);
        }
    }

    private static void DrainQueue()
    {
        string cmd;
        while (CmdQueue.TryDequeue(out cmd))
        {
            try
            {
                string resp = Execute(cmd);
                RespQueue.Enqueue(resp);
                Log("exec ok: " + cmd + " -> " + resp);
            }
            catch (Exception ex)
            {
                RespQueue.Enqueue(ErrJson(ex.Message));
                Log("exec ERR: " + cmd + " -> " + ex);
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    // Restrict local automation to this Windows user and LocalSystem.
    // Run NX and the client at the same, ordinary integrity level; this DACL
    // does not disable Windows integrity isolation or grant Everyone access.
    private static PipeSecurity CreatePipeSecurity()
    {
        SecurityIdentifier userSid = WindowsIdentity.GetCurrent().User;
        if (userSid == null) throw new InvalidOperationException("Current Windows user SID is unavailable");
        PipeSecurity security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(userSid);
        security.AddAccessRule(new PipeAccessRule(userSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    // ---- pipe server loop (background, never calls NXOpen) -------------
    private static void PipeLoop()
    {
        while (true)
        {
            NamedPipeServerStream inst = null;
            try
            {
                inst = new NamedPipeServerStream(
                    PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, CreatePipeSecurity());
                _pipe = inst;
                inst.WaitForConnection();
                Log("pipe client connected");
                byte[] lineBytes = ReadLineBytes(inst);
                if (lineBytes == null || lineBytes.Length == 0) continue;
                string line = Encoding.UTF8.GetString(lineBytes).Trim();
                if (line.Length == 0) continue;
                CmdQueue.Enqueue(line);
                Log("cmd queued: " + line);
                SendMessage(_timerWindow.Handle, WmSelfTest, IntPtr.Zero, IntPtr.Zero);
                string resp = null;
                DateTime deadline = DateTime.UtcNow.AddSeconds(300);
                while (DateTime.UtcNow < deadline)
                {
                    if (RespQueue.TryDequeue(out resp)) break;
                    System.Threading.Thread.Sleep(50);
                }
                if (resp == null) resp = "{\"ok\":false,\"error\":\"timeout\"}";
                byte[] outBytes = Encoding.UTF8.GetBytes(resp + "\n");
                inst.Write(outBytes, 0, outBytes.Length);
                inst.Flush();
                Log("resp sent: " + resp);
            }
            catch (Exception e)
            {
                Log("pipe ERR: " + e.Message);
                System.Threading.Thread.Sleep(300);
            }
            finally
            {
                try { if (inst != null) inst.Dispose(); } catch { }
                _pipe = null;
            }
        }
    }

    private static byte[] ReadLineBytes(System.IO.Stream s)
    {
        using (var ms = new MemoryStream())
        {
            int b;
            while ((b = s.ReadByte()) >= 0)
            {
                if (b == (byte)'\n') break;
                ms.WriteByte((byte)b);
            }
            return ms.ToArray();
        }
    }

    // ---- command dispatch ----------------------------------------------

    // Each core modeling command is one explicitly owned NX undo transaction.
    // No frame can survive a part open/create/close/release or drafting mutation.
    private sealed class ModelUndoFrame
    {
        public Session.UndoMarkId Mark;
        public Tag PartTag;
        public string Command;
        public Dictionary<string, Tag> Bodies = new Dictionary<string, Tag>();
        public Dictionary<string, Tag> Sketches = new Dictionary<string, Tag>();
        public Dictionary<string, string> Planes = new Dictionary<string, string>();
        public int BodyCounter;
        public int SketchCounter;
    }
    private static readonly List<ModelUndoFrame> _modelUndo = new List<ModelUndoFrame>();

    private static bool IsCoreModelMutation(string command)
    {
        switch (command)
        {
            case "nx_create_sketch": case "nx_sketch_line":
            case "nx_sketch_rectangle": case "nx_sketch_circle":
            case "nx_sketch_arc": case "nx_finish_sketch":
            case "nx_extrude": case "nx_hole": case "nx_counterbore_hole":
            case "nx_countersink_hole": case "nx_shell":
            case "nx_edge_blend": case "nx_chamfer": case "nx_unite":
            case "nx_revolve": case "nx_mirror":
            case "nx_linear_pattern": case "nx_circular_pattern": return true;
            default: return false;
        }
    }

    private static ModelUndoFrame CaptureModelFrame(string command)
    {
        if (_session == null) _session = Session.GetSession();
        if (_part == null || _session.Parts.Work == null ||
            _session.Parts.Work.Tag != _part.Tag)
            throw new Exception("active work part changed; explicitly open the intended part before modeling");
        ModelUndoFrame f = new ModelUndoFrame();
        f.PartTag = _part.Tag;
        f.Command = command;
        f.BodyCounter = _bodyCounter;
        f.SketchCounter = _sketchCounter;
        foreach (KeyValuePair<string, Body> item in _bodies) f.Bodies[item.Key] = item.Value.Tag;
        foreach (KeyValuePair<string, Sketch> item in _sketches) f.Sketches[item.Key] = item.Value.Tag;
        foreach (KeyValuePair<string, string> item in _sketchPlanes) f.Planes[item.Key] = item.Value;
        f.Mark = _session.SetUndoMark(Session.MarkVisibility.Visible, "NX MCP " + command);
        return f;
    }

    private static void RestoreModelFrame(ModelUndoFrame f)
    {
        Dictionary<Tag, Body> bodies = new Dictionary<Tag, Body>();
        foreach (Body b in _part.Bodies) bodies[b.Tag] = b;
        Dictionary<Tag, Sketch> sketches = new Dictionary<Tag, Sketch>();
        foreach (Sketch sk in _part.Sketches) sketches[sk.Tag] = sk;
        _bodies.Clear(); _sketches.Clear(); _sketchPlanes.Clear();
        foreach (KeyValuePair<string, Tag> item in f.Bodies)
        {
            if (!bodies.ContainsKey(item.Value)) throw new Exception("undo restored geometry but body id could not be recovered: " + item.Key);
            _bodies[item.Key] = bodies[item.Value];
        }
        foreach (KeyValuePair<string, Tag> item in f.Sketches)
        {
            if (!sketches.ContainsKey(item.Value)) throw new Exception("undo restored geometry but sketch id could not be recovered: " + item.Key);
            _sketches[item.Key] = sketches[item.Value];
        }
        foreach (KeyValuePair<string, string> item in f.Planes) _sketchPlanes[item.Key] = item.Value;
        _bodyCounter = f.BodyCounter; _sketchCounter = f.SketchCounter;
    }

    private static void RollBackModelFrame(ModelUndoFrame f)
    {
        // A failed sketch command may leave the sketch active. Deactivation is
        // required for NX to accept undo of the transaction that created it.
        try
        {
            Sketch active = _session.ActiveSketch;
            if (active != null) active.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
        }
        catch { }
        _session.UndoToMark(f.Mark, null);
        RestoreModelFrame(f);
        _session.DeleteUndoMark(f.Mark, null);
    }

    private static string Execute(string cmd)
    {
        string[] tokens = cmd.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return ErrJson("empty command");
        string command = tokens[0];
        if (!IsCoreModelMutation(command))
        {
            // Do not allow nx_undo to undo old geometry across later untracked
            // drafting/annotation/attribute edits. File operations reset state.
            if (command.StartsWith("nx_create_", StringComparison.Ordinal) ||
                command == "nx_delete_annotation" || command == "nx_set_attribute" ||
                command == "nx_update_views") _modelUndo.Clear();
            return Dispatch(cmd);
        }
        ModelUndoFrame frame;
        try { frame = CaptureModelFrame(command); }
        catch (Exception ex) { return ErrJson(ex.Message); }
        string result;
        try { result = Dispatch(cmd); }
        catch (Exception ex)
        {
            try { RollBackModelFrame(frame); }
            catch (Exception rollback) { return ErrJson(command + " failed: " + ex.Message + "; rollback failed: " + rollback.Message + "; do not repeat command"); }
            return ErrJson(command + " failed and was rolled back: " + ex.Message);
        }
        if (result.Contains("\"ok\":false"))
        {
            try { RollBackModelFrame(frame); }
            catch (Exception rollback) { return ErrJson(command + " returned an error; rollback failed: " + rollback.Message + "; do not repeat command"); }
            return result;
        }
        _modelUndo.Add(frame);
        if (_modelUndo.Count > 128) _modelUndo.RemoveAt(0);
        return result;
    }


    // Internal image-model command: one batch, one rollback boundary, no public
    // arbitrary-code surface and no permanent sketch-preference alteration.
    private static string ImagePreferences(string[] parts)
    {
        try
        {
            var payload=APayload(parts);_session=Session.GetSession();
            string expected=AString(payload,"expected_part","");
            if(!string.IsNullOrEmpty(expected))
            {
                AGuard();string expectedPath=APath(expected,false);
                if(!string.Equals(Path.GetFullPath(_part.FullPath),expectedPath,StringComparison.OrdinalIgnoreCase))
                    throw new Exception("active part does not match expected_part");
            }
            return AResponse(ADict("continuous_auto_dimensioning",_session.Preferences.Sketch.ContinuousAutoDimensioning,
                "active_part",_session.Parts.Work==null?null:_session.Parts.Work.FullPath,"read_only",true));
        }
        catch(Exception e)
        {return AJson.Serialize(ADict("ok",false,"code","NX12_IMAGE_PREFERENCES_FAILED","error",e.Message,
            "nx_code",e is NXException?(object)((NXException)e).ErrorCode:null));}
    }
    private static List<double[]> ImagePolylinePoints(Dictionary<string,object> payload)
    {
        object raw;if(!payload.TryGetValue("points",out raw))throw new Exception("points are required");
        System.Collections.IList values=raw as System.Collections.IList;
        if(values==null||values.Count<3||values.Count>2048)throw new Exception("closed polyline requires 3..2048 unique XY vertices, without a repeated closing vertex");
        var points=new List<double[]>();
        foreach(object item in values)
        {
            System.Collections.IList xy=item as System.Collections.IList;
            if(xy==null||xy.Count!=2)throw new Exception("each point must contain exactly two XY numbers");
            double x=Convert.ToDouble(xy[0],CultureInfo.InvariantCulture),y=Convert.ToDouble(xy[1],CultureInfo.InvariantCulture);
            if(double.IsNaN(x)||double.IsInfinity(x)||double.IsNaN(y)||double.IsInfinity(y))throw new Exception("polyline points must be finite");
            // Explicit service budget, not a claim about NX's absolute modeling limit.
            if(Math.Abs(x)>1000000||Math.Abs(y)>1000000)throw new Exception("polyline coordinate exceeds service budget of +/-1000000 mm");
            foreach(double[] previous in points)
            {
                double dx=x-previous[0],dy=y-previous[1];
                if(dx*dx+dy*dy<1.0e-18)throw new Exception("polyline contains duplicate or near-duplicate vertices (1e-9 mm)");
            }
            points.Add(new double[]{x,y});
        }
        return points;
    }
    private static void ImageRestoreAutoDimension(bool before,ref bool? after,ref bool restored)
    {
        _session.Preferences.Sketch.ContinuousAutoDimensioning=before;
        after=_session.Preferences.Sketch.ContinuousAutoDimensioning;
        restored=after.HasValue&&after.Value==before;
        if(!restored)throw new Exception("continuous auto-dimension preference restore verification failed");
    }
    private static string ImagePolylineSketch(string[] parts)
    {
        ModelUndoFrame frame=null;bool before=false,beforeKnown=false,preferenceTouched=false,restored=false;
        bool? after=null;string sketchId=null,rollback="not_needed";bool rolledBack=false;int pointCount=0,geometryCount=0;
        Exception failure=null;
        try
        {
            var payload=APayload(parts);AGuard();
            string expected=AString(payload,"expected_part","");
            if(string.IsNullOrWhiteSpace(expected))throw new Exception("expected_part is required");
            string expectedPath=APath(expected,false);
            if(!string.Equals(Path.GetFullPath(_part.FullPath),expectedPath,StringComparison.OrdinalIgnoreCase))
                throw new Exception("active part does not match expected_part; no sketch created");
            if(_session.ActiveSketch!=null)throw new Exception("another sketch is active; finish it explicitly before creating the image-model polyline");
            List<double[]> points=ImagePolylinePoints(payload);pointCount=points.Count;
            before=_session.Preferences.Sketch.ContinuousAutoDimensioning;beforeKnown=true;
            frame=CaptureModelFrame("nx_image_polyline_sketch");
            preferenceTouched=true;_session.Preferences.Sketch.ContinuousAutoDimensioning=false;
            if(_session.Preferences.Sketch.ContinuousAutoDimensioning)throw new Exception("could not temporarily disable continuous auto-dimensioning");
            string response=CreateSketch(new string[]{"nx_create_sketch","XY"});
            var created=AJson.Deserialize<Dictionary<string,object>>(response);object ok;
            if(!created.TryGetValue("ok",out ok)||!(bool)ok)throw new Exception("native sketch creation returned failure: "+response);
            sketchId=AString(created,"sketch_id","");
            if(string.IsNullOrEmpty(sketchId)||!_sketches.ContainsKey(sketchId))throw new Exception("created sketch was not registered");
            Sketch sketch=_sketches[sketchId];
            string name=AString(payload,"name","");if(!string.IsNullOrWhiteSpace(name))sketch.SetName(name);
            if(_session.ActiveSketch==null||_session.ActiveSketch.Tag!=sketch.Tag)sketch.Activate(Sketch.ViewReorient.False);
            if(_session.ActiveSketch==null||_session.ActiveSketch.Tag!=sketch.Tag)throw new Exception("could not activate the newly created sketch");
            // Match the already-tested scalar SketchLine NX12 path. Keep the
            // command/preference/rollback boundary, but interleave native curve
            // creation and scalar AddGeometry instead of the array overload.
            for(int i=0;i<points.Count;i++)
            {
                double[] first=points[i],second=points[(i+1)%points.Count];
                Curve curve=_part.Curves.CreateLine(
                    SketchPoint(sketchId,first[0],first[1]),
                    SketchPoint(sketchId,second[0],second[1]));
                sketch.AddGeometry(curve,Sketch.InferConstraintsOption.InferNoConstraints);
            }
            geometryCount=sketch.GetAllGeometry().Length;
            if(geometryCount!=points.Count)throw new Exception("polyline sketch geometry count does not match input vertices");
            sketch.Deactivate(Sketch.ViewReorient.True,Sketch.UpdateLevel.Model);
            if(_session.ActiveSketch!=null)throw new Exception("native sketch did not leave sketch-edit mode");
        }
        catch(Exception e)
        {
            failure=e;
            // Roll back while continuous auto-dimensioning is still disabled, so
            // failure deactivation cannot re-open the performance-warning modal.
            if(frame!=null)
            {
                try{RollBackModelFrame(frame);rollback="restored";rolledBack=true;}
                catch(Exception undo){rollback="FAILED: "+undo.Message;}
            }
        }
        finally
        {
            if(beforeKnown)
            {
                try{ImageRestoreAutoDimension(before,ref after,ref restored);}
                catch(Exception preference)
                {
                    restored=false;
                    failure=new Exception((failure==null?"":failure.Message+"; ")+"preference restore failed: "+preference.Message,failure);
                }
            }
        }
        // A preference restoration failure after otherwise successful geometry
        // is still a failed command. Undo its geometry and then verify restoration
        // again; never leave an apparently successful partial sketch in the cache.
        if(failure!=null)
        {
            if(frame!=null&&!rolledBack&&rollback=="not_needed")
            {
                try{RollBackModelFrame(frame);rollback="restored";rolledBack=true;}
                catch(Exception undo){rollback="FAILED: "+undo.Message;}
                if(beforeKnown)
                {
                    try{ImageRestoreAutoDimension(before,ref after,ref restored);}
                    catch(Exception preference){restored=false;failure=new Exception(failure.Message+"; final restore failed: "+preference.Message,failure);}
                }
            }
            return AJson.Serialize(ADict("ok",false,"code","NX12_IMAGE_SKETCH_FAILED","error",failure.Message,
                "nx_code",failure is NXException?(object)((NXException)failure).ErrorCode:null,
                "details",ADict("rollback",rollback,"preference_touched",preferenceTouched,"auto_dim_before",beforeKnown?(object)before:null,
                    "auto_dim_after",after,"restored",restored,"sketch_id",sketchId,"point_count",pointCount)));
        }
        _modelUndo.Add(frame);if(_modelUndo.Count>128)_modelUndo.RemoveAt(0);
        return AResponse(ADict("sketch_id",sketchId,"point_count",pointCount,"segment_count",geometryCount,"plane","XY",
            "auto_dim_before",before,"auto_dim_after",after,"restored",restored,"preference_touched",preferenceTouched));
    }

    private static string Dispatch(string cmd)
    {
        string[] parts = cmd.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return ErrJson("empty command");
        if (parts[0].StartsWith("nx_assembly_", StringComparison.Ordinal)) return DispatchAssembly(parts);
        if (parts[0].StartsWith("nx_drafting_", StringComparison.Ordinal)) return DispatchDraftingExtension(parts);
        if (parts[0] == "nx_image_preferences") return ImagePreferences(parts);
        if (parts[0] == "nx_image_polyline_sketch") return ImagePolylineSketch(parts);
        switch (parts[0])
        {
            case "ping":
                return OkJson("pong");
            case "nx_status":
                return Status();
            case "nx_create_part":
                return CreatePart(parts);
            case "nx_save_part":
                return SavePart();
            case "nx_release":
                return ReleaseTask();
            case "nx_create_sketch":
                return CreateSketch(parts);
            case "nx_sketch_line":
                return SketchLine(parts);
            case "nx_sketch_rectangle":
                return SketchRectangle(parts);
            case "nx_sketch_circle":
                return SketchCircle(parts);
            case "nx_sketch_arc":
                return SketchArc(parts);
            case "nx_finish_sketch":
                return FinishSketch(parts);
            case "nx_extrude":
                return Extrude(parts);
            case "nx_hole":
                return Hole(parts);
            case "nx_counterbore_hole":
                return CounterboreHole(parts);
            case "nx_countersink_hole":
                return CountersinkHole(parts);
            case "nx_shell":
                return Shell(parts);
            case "nx_edge_blend":
                return EdgeBlend(parts);
            case "nx_chamfer":
                return Chamfer(parts);
            case "nx_unite":
                return Unite(parts);
            case "nx_revolve":
                return Revolve(parts);
            case "nx_mirror":
                return Mirror(parts);
            case "nx_linear_pattern":
                return LinearPattern(parts);
            case "nx_circular_pattern":
                return CircularPattern(parts);
            case "nx_fit_view":
                return FitView();
            // ---- drafting (2D drawing) ---------------------------------
            // Ported from CAD-Agent-Hub's proven NXOpen sequences; every call
            // below was verified present in the NX 12 NXOpen.xml member list.
            case "nx_create_drawing_sheet":
                return CreateDrawingSheet(parts);
            case "nx_create_projected_view":
                return CreateProjectedView(parts);
            case "nx_list_drawing_sheets":
                return ListDrawingSheets(parts);
            case "nx_list_drafting_views":
                return ListDraftingViews(parts);
            case "nx_list_dimensions":
                return ListDimensions(parts);
            case "nx_list_annotations":
                return ListAnnotations(parts);
            case "nx_create_note":
                return CreateNote(parts);
            case "nx_create_diameter_dimension":
                return CreateDiameterDimension(parts);
            case "nx_create_radius_dimension":
                return CreateRadiusDimension(parts);
            case "nx_delete_annotation":
                return DeleteAnnotation(parts);
            case "nx_export_pdf":
                return ExportPdf(parts);
            case "nx_measure_distance":
                return MeasureDistance(parts);
            case "nx_create_centerline":
                return CreateCenterline(parts);
            case "nx_create_roughness":
                return CreateRoughness(parts);
            case "nx_create_detail_view":
                return CreateDetailView(parts);
            case "nx_create_title_block":
                return CreateTitleBlock(parts);
            case "nx_set_attribute":
                return SetAttributeCmd(parts);
            case "nx_list_attributes":
                return ListAttributes(parts);
            case "nx_create_sheet_border":
                return CreateSheetBorder(parts);
            case "nx_export_dwg":
                return ExportDwg(parts);
            // ---- drafting: view update + dimension annotation ------------
            case "nx_update_views":
                return UpdateViewsCmd();
            case "nx_create_dimension":
                return CreateDimension(parts);
            case "nx_export_step":
                return ExportStep(parts);
            case "nx_open_part":
                return OpenPart(parts);
            case "nx_close_part":
                return ClosePart(parts);
            case "nx_list_sketches":
                return ListObjects("Sketches", "sketch");
            case "nx_list_bodies":
                return ListObjects("Bodies", "body");
            case "nx_list_features":
                return ListObjects("Features", "feature");
            case "nx_list_edges":
                return ListEdges(parts);
            case "nx_list_faces":
                return ListFaces(parts);
            case "nx_undo":
                return Undo();
            case "nx_build_plate_test":
                return BuildPlateTest();
            case "create_block":
                return CreateBlock(parts);
            default:
                return ErrJson("unknown command: " + cmd);
        }
    }

    // ---- commands ------------------------------------------------------
    private static string Status()
    {
        if (_session == null) _session = Session.GetSession();
        try
        {
            // The NX work part is authoritative. _part may become an inactive
            // wrapper when the user closes/switches parts outside the Loader.
            NXOpen.Part work = _session.Parts.Work;
            if (work == null)
            {
                if (_part != null || _partPath != null) ResetTaskState();
                _part = null;
                _partPath = null;
                return "{\"ok\":true,\"ready\":true,\"loader\":\"nx_mcp_loader\",\"nx\":\"" + Esc(NxVersion()) + "\"," +
                       "\"part\":\"\",\"sketches\":0,\"bodies\":0}";
            }

            string fullPath = "";
            try { fullPath = work.FullPath; } catch { }
            _part = work;
            _partPath = fullPath;

            // Keep an unsaved active part visible to Runner preflight so it is
            // never mistaken for "no active part".
            string partName = string.IsNullOrEmpty(fullPath)
                ? "<unsaved-active-part>"
                : fullPath;

            return "{\"ok\":true,\"ready\":true,\"loader\":\"nx_mcp_loader\",\"nx\":\"" + Esc(NxVersion()) + "\"," +
                   "\"part\":\"" + Esc(partName) + "\",\"sketches\":" + _sketches.Count +
                   ",\"bodies\":" + _bodies.Count + "}";
        }
        catch (Exception e)
        {
            Log("status ERR: " + e.Message);
            return ErrJson("status failed: " + e.Message);
        }
    }

    /// <summary>
    /// [NX12 port] Report the ACTUAL running NX version instead of the hardcoded
    /// "2506" marketing string the upstream project compiled in. Upstream only ever
    /// validated on NX 2506, so clients would otherwise be told the wrong version.
    /// GetEnvironmentVariableValue exists since NX7.5.0, so it is safe on NX 12.
    /// </summary>
    private static string NxVersion()
    {
        try
        {
            if (_session != null)
            {
                string v = _session.GetEnvironmentVariableValue("UGII_VERSION");
                if (!string.IsNullOrEmpty(v)) return v.Trim();
            }
        }
        catch { }
        try
        {
            string v2 = Environment.GetEnvironmentVariable("UGII_VERSION");
            if (!string.IsNullOrEmpty(v2)) return v2.Trim();
        }
        catch { }
        return "unknown";
    }

    // ---- drafting (2D drawing) ---------------------------------------
    // Ported from CAD-Agent-Hub (MCP/UG/nx_remote_ops.py :: _op_create_drawing_sheet
    // / _op_create_projected_view). Those sequences were validated by that project;
    // the exact members were then confirmed against the NX 12 NXOpen.xml.
    // Every NXOpen call is guarded so a failure returns JSON instead of killing NX.

    private static DrawingSheet CurrentSheet()
    {
        if (_part == null) throw new Exception("no active part; run nx_create_part first");
        DrawingSheet s = _part.DrawingSheets.CurrentDrawingSheet;
        if (s == null) throw new Exception("no drawing sheet is currently open");
        return s;
    }

    private static DrawingSheet.StandardSheetSize ParseSheetSize(string token)
    {
        switch (token.Trim().ToUpperInvariant())
        {
            case "A0": return DrawingSheet.StandardSheetSize.A0;
            case "A1": return DrawingSheet.StandardSheetSize.A1;
            case "A2": return DrawingSheet.StandardSheetSize.A2;
            case "A3": return DrawingSheet.StandardSheetSize.A3;
            case "A4": return DrawingSheet.StandardSheetSize.A4;
            default: throw new Exception("size must be A0/A1/A2/A3/A4, got '" + token + "'");
        }
    }

    // nx_create_drawing_sheet <name> [size] [first|third] [scale] [model_view]
    private static string CreateDrawingSheet(string[] parts)
    {
        if (parts.Length < 2)
            return ErrJson("usage: nx_create_drawing_sheet <name> [A0..A4] [first|third] [scale] [model_view]");
        try
        {
            _session = Session.GetSession();
            if (_part == null) return ErrJson("no active part; run nx_create_part first");

            string name = parts[1];
            string sizeToken = parts.Length > 2 ? parts[2] : "A4";
            DrawingSheet.StandardSheetSize size = ParseSheetSize(sizeToken);

            string projToken = parts.Length > 3 ? parts[3].Trim().ToLowerInvariant() : "first";
            DrawingSheet.ProjectionAngleType proj =
                (projToken == "third") ? DrawingSheet.ProjectionAngleType.ThirdAngle
                                       : DrawingSheet.ProjectionAngleType.FirstAngle;
            if (projToken != "first" && projToken != "third")
                return ErrJson("projection must be 'first' or 'third'");

            double numerator = parts.Length > 4 ? D(parts[4]) : 1.0;
            if (numerator <= 0) return ErrJson("scale must be > 0");
            double denominator = 1.0;
            // Validate before inserting the sheet: a typo must not silently
            // substitute the current isometric work view or create duplicates.
            string mvName = parts.Length > 5 ? parts[5].Trim() : "Top";
            ModelingView mv;
            try { mv = _part.ModelingViews.FindObject(mvName); }
            catch { return ErrJson("model_view not found; sheet not created: " + mvName); }
            foreach (DrawingSheet existing in _part.DrawingSheets)
                if (string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase))
                    return ErrJson("drawing sheet already exists; inspect/reuse it instead of retrying creation: " + name);

            // [NX12 port] DrawingSheetCollection.InsertSheet is deprecated exactly in
            // NX12.0.0; use the recommended DraftingDrawingSheetCollection instead.
            // The InsertSheet signatures are identical, so this is a drop-in swap.
            DrawingSheet sheet = _part.DraftingDrawingSheets.InsertSheet(name, size, numerator, denominator, proj);
            sheet.Open();

            // Place a base view at roughly the left-of-centre of the sheet, mirroring
            // what CAD-Agent-Hub does with an explicit A4 coordinate.
            string baseNote = "";
            bool baseCreated = false;
            try
            {
                double sx, sy;
                try { sx = sheet.Length; sy = sheet.Height; }
                catch { sx = 297.0; sy = 210.0; }
                if (sx <= 0 || sy <= 0) { sx = 297.0; sy = 210.0; }

                DraftingView bv = sheet.SheetDraftingViews.CreateBaseView(
                    mv, new Point3d(sx * 0.42, sy * 0.5, 0.0), numerator / denominator, false);
                baseCreated = true;
                try { baseNote = " base=" + bv.JournalIdentifier; } catch { }
            }
            catch (Exception e2)
            {
                baseNote = " base_view_failed: " + e2.Message;
            }

            int count = 0;
            try { count = sheet.GetDraftingViews().Length; } catch { }
            if (!baseCreated || count == 0)
                return ErrJson("sheet was created but base view failed; inspect the existing sheet before retrying: " + sheet.Name + baseNote);

            try { ForceUpdateSheet(sheet); } catch { }

            return OkJson("sheet", sheet.Name, "created size=" + sizeToken.ToUpperInvariant()
                + " views=" + count + baseNote);
        }
        catch (Exception e)
        {
            return ErrJson("create_drawing_sheet failed: " + e.Message);
        }
    }

    // nx_create_projected_view <x> <y> [parent_view_id] [view_name]
    private static string CreateProjectedView(string[] parts)
    {
        if (parts.Length < 3)
            return ErrJson("usage: nx_create_projected_view <x> <y> [parent_view_id] [view_name]");
        try
        {
            DrawingSheet sheet = CurrentSheet();
            DraftingView[] views = sheet.GetDraftingViews();
            if (views == null || views.Length == 0) return ErrJson("sheet has no drafting views");

            string token = parts.Length > 3 ? parts[3].Trim() : "";
            DraftingView parent = views[0];
            if (!string.IsNullOrEmpty(token))
            {
                bool found = false;
                for (int i = 0; i < views.Length; i++)
                {
                    string nm = ""; try { nm = views[i].Name; } catch { }
                    string ji = ""; try { ji = views[i].JournalIdentifier; } catch { }
                    if (token == i.ToString() || token == nm || token == ji)
                    {
                        parent = views[i];
                        found = true;
                        break;
                    }
                }
                if (!found) return ErrJson("parent_view_id did not match a drafting view: " + token);
            }

            double x = D(parts[1]);
            double y = D(parts[2]);
            Point3d requestedOrigin = new Point3d(x, y, 0.0);
            // Choose a documented explicit orthographic direction instead of the
            // legacy CreateProjectedView inference, which yielded a collapsed side view.
            NXOpen.UF.UFSession projectionUf = NXOpen.UF.UFSession.GetUFSession();
            double[] parentBorder = new double[4];
            projectionUf.Draw.AskViewBorders(parent.Tag, parentBorder);
            double parentCenterX = 0.5 * (parentBorder[0] + parentBorder[2]);
            double parentCenterY = 0.5 * (parentBorder[1] + parentBorder[3]);
            double deltaX = x - parentCenterX;
            double deltaY = y - parentCenterY;
            if (Math.Abs(deltaX) < 1.0e-6 && Math.Abs(deltaY) < 1.0e-6)
                return ErrJson("projected view origin must be displaced from its parent center; no view was created");
            // uf_draw_types.h UF_DRAW_proj_dir_e: above=1,right=2,below=3,left=4.
            int explicitDirection = Math.Abs(deltaX) >= Math.Abs(deltaY)
                ? (deltaX >= 0.0 ? 2 : 4) : (deltaY >= 0.0 ? 1 : 3);
            NXOpen.Tag projectedTag;
            projectionUf.Draw.AddOrthographicView(sheet.Tag, parent.Tag,
                (NXOpen.UF.UFDraw.ProjDir)explicitDirection, new double[] { x, y }, out projectedTag);
            DraftingView pv = null;
            foreach (DraftingView candidate in sheet.GetDraftingViews())
                if (candidate.Tag == projectedTag) { pv = candidate; break; }
            if (pv == null)
                return ErrJson("orthographic view was created but could not be resolved; inspect nx_list_drafting_views before retrying");
            // The legacy creation call can apply an inferred projection alignment.
            // Explicitly set and read back the requested sheet origin before claiming success.
            try
            {
                pv.MoveView(requestedOrigin);
                ForceUpdateSheet(sheet);
                Point3d actualOrigin = pv.GetDrawingReferencePoint();
                if (Math.Abs(actualOrigin.X - x) > 1.0e-6 || Math.Abs(actualOrigin.Y - y) > 1.0e-6)
                    return ErrJson("projected view was created but its requested origin did not match: requested="
                        + GNum(x) + "," + GNum(y) + " actual=" + GNum(actualOrigin.X) + "," + GNum(actualOrigin.Y)
                        + "; inspect nx_list_drafting_views before retrying; no duplicate view should be created");
            }
            catch (Exception ep)
            {
                return ErrJson("projected view was created but explicit placement failed: " + ep.Message
                    + "; inspect nx_list_drafting_views before retrying; no duplicate view should be created");
            }

            if (parts.Length > 4 && !string.IsNullOrEmpty(parts[4].Trim()))
            {
                try { pv.SetName(parts[4].Trim()); } catch { }
            }
            try { ForceUpdateSheet(sheet); } catch { }

            string id = ""; try { id = pv.JournalIdentifier; } catch { }
            int count = 0;
            try { count = sheet.GetDraftingViews().Length; } catch { }
            return OkJson("view", id, "projected view created views=" + count);
        }
        catch (Exception e)
        {
            return ErrJson("create_projected_view failed: " + e.Message);
        }
    }

    // nx_list_drawing_sheets
    private static string ListDrawingSheets(string[] parts)
    {
        try
        {
            if (_part == null) return ErrJson("no active part");
            var names = new List<string>();
            foreach (DrawingSheet s in _part.DrawingSheets.ToArray())
            {
                string ji = ""; try { ji = s.JournalIdentifier; } catch { }
                names.Add(s.Name + " (" + ji + ")");
            }
            return OkJsonList("sheets", names, "found " + names.Count + " sheet(s)");
        }
        catch (Exception e)
        {
            return ErrJson("list_drawing_sheets failed: " + e.Message);
        }
    }

    // nx_list_drafting_views
    private static string ListDraftingViews(string[] parts)
    {
        try
        {
            DrawingSheet sheet = GetSheetToUse();
            var ids = new List<string>();
            foreach (DraftingView v in sheet.GetDraftingViews())
            {
                string nm = ""; try { nm = v.Name; } catch { }
                string ji = ""; try { ji = v.JournalIdentifier; } catch { }
                bool ood = false; try { ood = v.IsOutOfDate; } catch { }
                // format: journalIdentifier | name | out_of_date(0/1)
                string location = "";
                try
                {
                    Point3d reference = v.GetDrawingReferencePoint();
                    location += "|reference_mm=" + GNum(reference.X) + "," + GNum(reference.Y);
                }
                catch (Exception e) { location += "|reference_unavailable=" + e.Message; }
                try
                {
                    double[] border = new double[4];
                    NXOpen.UF.UFSession.GetUFSession().Draw.AskViewBorders(v.Tag, border);
                    location += "|border_mm=" + GNum(border[0]) + "," + GNum(border[1])
                        + "," + GNum(border[2]) + "," + GNum(border[3]);
                }
                catch (Exception e) { location += "|border_unavailable=" + e.Message; }
                try
                {
                    Matrix3x3 rotation = v.Matrix;
                    location += "|model_to_view_rotation="
                        + GNum(rotation.Xx) + "," + GNum(rotation.Xy) + "," + GNum(rotation.Xz)
                        + "," + GNum(rotation.Yx) + "," + GNum(rotation.Yy) + "," + GNum(rotation.Yz)
                        + "," + GNum(rotation.Zx) + "," + GNum(rotation.Zy) + "," + GNum(rotation.Zz);
                }
                catch (Exception e) { location += "|rotation_unavailable=" + e.Message; }
                ids.Add(ji + "|" + nm + "|" + (ood ? "1" : "0") + location);
            }
            return OkJsonList("views", ids, "found " + ids.Count + " view(s)");
        }
        catch (Exception e)
        {
            return ErrJson("list_drafting_views failed: " + e.Message);
        }
    }

    // nx_update_views  — force every drafting view on the current sheet to a
    // fully-up-to-date state. Previously CreateProjectedView only called
    // RefreshCurrentSheet(), which leaves the views flagged out-of-date (the
    // "clock" icon in the Part Navigator). A real update is what clears it.
    private static string UpdateViewsCmd()
    {
        try
        {
            _session = Session.GetSession();
            if (_part == null) return ErrJson("no active part");
            DrawingSheet sheet = GetSheetToUse();
            ForceUpdateSheet(sheet);
            int n = 0;
            try { n = sheet.GetDraftingViews().Length; } catch { }
            return OkJson("updated", n.ToString(), "updated " + n + " view(s) on sheet " + sheet.Name);
        }
        catch (Exception e)
        {
            return ErrJson("update_views failed: " + e.Message);
        }
    }

    // Performs a genuine view update (not just a sheet refresh) and then
    // refreshes the sheet. Used after every drafting operation so the Part
    // Navigator never shows an out-of-date / clock status.
    private static void ForceUpdateSheet(DrawingSheet sheet)
    {
        Session.UndoMarkId mark = _session.SetUndoMark(Session.MarkVisibility.Invisible, "MCP Force Update Views");
        foreach (DraftingView v in sheet.GetDraftingViews())
        {
            try { v.Update(); }
            catch (Exception e) { Log("view update warn: " + e.Message); }
        }
        try { _session.UpdateManager.DoUpdate(mark); } catch { }
        try { _part.DrawingSheets.RefreshCurrentSheet(); } catch { }
    }

    private static DraftingView ResolveView(DrawingSheet sheet, string token)
    {
        DraftingView[] views = sheet.GetDraftingViews();
        if (views == null || views.Length == 0) throw new Exception("sheet has no drafting views");
        if (string.IsNullOrEmpty(token)) return views[0];
        foreach (DraftingView v in views)
        {
            string nm = ""; try { nm = v.Name; } catch { }
            string ji = ""; try { ji = v.JournalIdentifier; } catch { }
            string tg = ""; try { tg = v.Tag.ToString(); } catch { }
            if (token == nm || token == ji || token == tg) return v;
        }
        Log("ResolveView: '" + token + "' did not match; using first view " + views[0].Name);
        return views[0];
    }

    // Returns a pick point that lies on the geometry (midpoint of an edge, the
    // coordinate of a point). Used as the dimension associativity PickPoint so
    // the dimension stays associative to real model geometry.
    private static Point3d MidpointOfObject(NXOpen.TaggedObject obj)
    {
        try
        {
            NXOpen.Edge e = obj as NXOpen.Edge;
            if (e != null)
            {
                Point3d s, en;
                e.GetVertices(out s, out en);
                return new Point3d((s.X + en.X) / 2.0, (s.Y + en.Y) / 2.0, (s.Z + en.Z) / 2.0);
            }
            NXOpen.Point pt = obj as NXOpen.Point;
            if (pt != null)
            {
                Point3d c;
                try { c = pt.Coordinates; return c; } catch { }
            }
        }
        catch (Exception e) { Log("MidpointOfObject warn: " + e.Message); }
        return new Point3d(0.0, 0.0, 0.0);
    }

    // nx_create_dimension <view_id> <body_id> <first_edge_index> <second_edge_index>
    //   <pos_x> <pos_y> [measurement=horizontal|vertical|parallel] [upper=0.1] [lower=-0.1] [dim_name]
    // Creates an associative linear dimension between two model edges (selected
    // by their 0-based index from nx_list_edges, on the given body) inside the
    // specified drafting view, then applies a bilateral (two-line) tolerance.
    // Default tolerance is ±0.1 mm, matching the user's "标注尺寸公差±0.1".
    // Returns a usable drawing sheet: the current one if set, otherwise the
    // first sheet that has drafting views (and opens it so it becomes current).
    private static DrawingSheet GetSheetToUse()
    {
        DrawingSheet s = null;
        try { s = _part.DrawingSheets.CurrentDrawingSheet; } catch { }
        if (s != null) return s;
        foreach (DrawingSheet sh in _part.DrawingSheets)
        {
            try { if (sh.GetDraftingViews().Length > 0) { sh.Open(); return sh; } } catch { }
        }
        throw new Exception("no drawing sheet available");
    }

    // Resolves a drafting view by token across ALL sheets (not just the current
    // one) and returns its owning sheet, so dimensioning works even when no
    // sheet is flagged "current" in the session.
    private static DraftingView ResolveViewAnySheet(string token, out DrawingSheet sheet)
    {
        sheet = null;
        try
        {
            DrawingSheet cur = _part.DrawingSheets.CurrentDrawingSheet;
            if (cur != null)
            {
                DraftingView v = TryResolveInSheet(cur, token);
                if (v != null) { sheet = cur; return v; }
            }
        }
        catch { }
        foreach (DrawingSheet sh in _part.DrawingSheets)
        {
            DraftingView v = TryResolveInSheet(sh, token);
            if (v != null) { sheet = sh; return v; }
        }
        // A nonempty, invalid token must not annotate another view silently.
        throw new Exception("no drafting view matched (token=" + token + "); call nx_list_drafting_views and use its current name or journal identifier");
    }

    private static DraftingView TryResolveInSheet(DrawingSheet sheet, string token)
    {
        DraftingView[] views = sheet.GetDraftingViews();
        if (views == null || views.Length == 0) return null;
        if (string.IsNullOrEmpty(token)) return views[0];
        int index;
        if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out index)
            && index >= 0 && index < views.Length) return views[index];
        foreach (DraftingView v in views)
        {
            string nm = ""; try { nm = v.Name; } catch { }
            string ji = ""; try { ji = v.JournalIdentifier; } catch { }
            string tg = ""; try { tg = v.Tag.ToString(); } catch { }
            if (token == nm || token == ji || token == tg) return v;
        }
        return null;
    }

    private static string CreateDimension(string[] parts)
    {
        if (parts.Length < 7)
            return ErrJson("usage: nx_create_dimension <view_id> <body_id> <first_edge_index> <second_edge_index> <pos_x> <pos_y> [horizontal|vertical|parallel] [upper=0.1] [lower=-0.1] [dim_name]");
        try
        {
            _session = Session.GetSession();
            if (_part == null) return ErrJson("no active part; run nx_create_part first");

            DrawingSheet sheet;
            DraftingView view = ResolveViewAnySheet(parts[1].Trim(), out sheet);
            try { sheet.Open(); } catch { }

            Body body = ResolveBody(parts[2].Trim());
            Edge[] edges = body.GetEdges();
            int ei1 = int.Parse(parts[3], CultureInfo.InvariantCulture);
            int ei2 = int.Parse(parts[4], CultureInfo.InvariantCulture);
            if (ei1 < 0 || ei1 >= edges.Length) return ErrJson("first_edge_index out of range: " + ei1 + " (have " + edges.Length + ")");
            if (ei2 < 0 || ei2 >= edges.Length) return ErrJson("second_edge_index out of range: " + ei2 + " (have " + edges.Length + ")");
            Edge e1 = edges[ei1];
            Edge e2 = edges[ei2];

            double posx = D(parts[5]);
            double posy = D(parts[6]);

            string measurement = (parts.Length > 7 && !string.IsNullOrEmpty(parts[7].Trim()))
                ? parts[7].Trim().ToLowerInvariant() : "horizontal";
            double upper = (parts.Length > 8 && !string.IsNullOrEmpty(parts[8].Trim())) ? D(parts[8]) : 0.1;
            double lower = (parts.Length > 9 && !string.IsNullOrEmpty(parts[9].Trim())) ? D(parts[9]) : -0.1;
            string dimName = (parts.Length > 10 && !string.IsNullOrEmpty(parts[10].Trim()))
                ? parts[10].Trim() : "MCP_DIMENSION";

            NXOpen.Annotations.DimensionData ddd = _part.Annotations.NewDimensionData();
            NXOpen.Annotations.Associativity a1 = _part.Annotations.NewAssociativity();
            NXOpen.Annotations.Associativity a2 = _part.Annotations.NewAssociativity();

            a1.FirstObject = e1;
            a1.SecondObject = null;
            a1.ObjectView = view;
            a1.PickPoint = MidpointOfObject(e1);
            a1.PointOption = NXOpen.Annotations.AssociativityPointOption.OnCurve;
            a1.LineOption = NXOpen.Annotations.AssociativityLineOption.None;

            a2.FirstObject = e2;
            a2.SecondObject = null;
            a2.ObjectView = view;
            a2.PickPoint = MidpointOfObject(e2);
            a2.PointOption = NXOpen.Annotations.AssociativityPointOption.OnCurve;
            a2.LineOption = NXOpen.Annotations.AssociativityLineOption.None;

            ddd.SetAssociativity(1, new NXOpen.Annotations.Associativity[] { a1 });
            ddd.SetAssociativity(2, new NXOpen.Annotations.Associativity[] { a2 });

            Point3d origin = new Point3d(posx, posy, 0.0);
            NXOpen.Annotations.Dimension dim;
            switch (measurement)
            {
                case "vertical":
                    dim = _part.Dimensions.CreateVerticalDimension(ddd, origin);
                    break;
                case "parallel":
                    dim = _part.Dimensions.CreateParallelDimension(ddd, origin);
                    break;
                default:
                    dim = _part.Dimensions.CreateHorizontalDimension(ddd, origin);
                    break;
            }
            try { dim.SetName(dimName); } catch { }

            // Apply bilateral (two-line) tolerance. Best-effort: if the
            // dimension type does not support linear tolerance we skip it.
            try
            {
                NXOpen.Annotations.ILinearTolerance ltObj = (NXOpen.Annotations.ILinearTolerance)dim;
                NXOpen.Annotations.LinearTolerance lt = ltObj.GetTolerance();
                lt.ToleranceType = NXOpen.Annotations.ToleranceType.BilateralTwoLines;
                lt.SetUpperToleranceMm(new NXOpen.Annotations.Value(upper, null, 3));
                lt.SetLowerToleranceMm(new NXOpen.Annotations.Value(lower, null, 3));
                ltObj.SetTolerance(lt);
            }
            catch (Exception et) { Log("tolerance warn: " + et.Message); }

            try { ForceUpdateSheet(sheet); } catch { }

            string jid = ""; try { jid = dim.JournalIdentifier; } catch { }
            double computed = 0.0; try { computed = dim.ComputedSize; } catch { }
            return OkJson("dimension", jid,
                "created " + measurement + " dim name=" + dimName
                + " value=" + F(computed) + " tol=+" + F(upper) + "/" + F(lower));
        }
        catch (Exception e)
        {
            return ErrJson("create_dimension failed: " + e.Message);
        }
    }

    // Resolves a body by its MCP id (from _bodies) or falls back to the first
    // solid body in the work part — matching the fallback used by nx_list_edges.
    private static Body ResolveBody(string id)
    {
        if (!string.IsNullOrEmpty(id))
        {
            Body b;
            if (_bodies.TryGetValue(id, out b)) return b;
        }
        Body[] all = _part.Bodies.ToArray();
        foreach (Body b in all) if (b.IsSolidBody) return b;
        if (all.Length > 0) return all[0];
        throw new Exception("no body in work part");
    }

    // nx_list_dimensions
    // Read-back of every dimension in the part: measured size + bilateral tolerance.
    // This closes the write/verify loop and lets an agent "read" an existing 2D
    // drawing (values and tolerances), not just create one.
    // Members confirmed present in NX 12: Part.Dimensions, Dimension.ComputedSize,
    // Dimension.UpperMetricToleranceValue / LowerMetricToleranceValue, ToleranceType.
    private static string ListDimensions(string[] parts)
    {
        try
        {
            if (_part == null) return ErrJson("no active part");
            var items = new List<string>();
            foreach (NXOpen.Annotations.Dimension dim in _part.Dimensions.ToArray())
            {
                string ji = "";  try { ji = dim.JournalIdentifier; } catch { }
                string nm = "";  try { nm = dim.Name; } catch { }
                double size = 0; try { size = dim.ComputedSize; } catch { }
                double up = 0, lo = 0;
                try { up = dim.UpperMetricToleranceValue; lo = dim.LowerMetricToleranceValue; } catch { }
                string tt = "";
                try { tt = dim.ToleranceType.ToString(); } catch { }
                items.Add(
                    ji + "|" + nm +
                    "|size=" + size.ToString("0.###", CultureInfo.InvariantCulture) +
                    "|tol=" + up.ToString("0.###", CultureInfo.InvariantCulture) +
                    "/" + lo.ToString("0.###", CultureInfo.InvariantCulture) +
                    "|" + tt);
            }
            return OkJsonList("dimensions", items, "found " + items.Count + " dimension(s)");
        }
        catch (Exception e)
        {
            return ErrJson("list_dimensions failed: " + e.Message);
        }
    }

    // nx_create_note <pos_x> <pos_y> <text...>
    // Technical-requirement / process text on the current drawing sheet.
    // Text is everything after the position (spaces allowed); use '|' for line breaks.
    // Ported from CAD-Agent-Hub _op_create_drafting_note (validated sequence).
    private static string CreateNote(string[] parts)
    {
        if (parts.Length < 4)
            return ErrJson("usage: nx_create_note <pos_x> <pos_y> <text...> (use | for line breaks)");
        try
        {
            _session = Session.GetSession();
            if (_part == null) return ErrJson("no active part; run nx_create_part first");

            double posx = D(parts[1]);
            double posy = D(parts[2]);

            // Text == all remaining tokens re-joined, so it may contain spaces.
            var sb = new StringBuilder();
            for (int i = 3; i < parts.Length; i++)
            {
                if (i > 3) sb.Append(" ");
                sb.Append(parts[i]);
            }
            string raw = sb.ToString().Trim();
            if (raw.Length == 0) return ErrJson("note text is empty");

            string[] lines = raw.Split('|');
            for (int i = 0; i < lines.Length; i++) lines[i] = lines[i].Trim();

            string jid = "";
            NXOpen.Annotations.DraftingNoteBuilder nb =
                // C# has no SimpleDraftingAid.Null (that is the Python spelling);
                // passing null creates a brand-new note, same as the other builders here.
                _part.Annotations.CreateDraftingNoteBuilder(null);
            try
            {
                nb.Text.TextBlock.SetText(lines);
                nb.Origin.OriginPoint = new Point3d(posx, posy, 0.0);
                nb.Origin.Anchor = NXOpen.Annotations.OriginBuilder.AlignmentPosition.TopLeft;
                NXOpen.Annotations.Note note = (NXOpen.Annotations.Note)nb.Commit();
                try { note.SetName("MCP_NOTE"); } catch { }
                try { jid = note.JournalIdentifier; } catch { }
            }
            finally { try { nb.Destroy(); } catch { } }

            try { _part.DrawingSheets.RefreshCurrentSheet(); } catch { }
            return OkJson("note", jid, "created note lines=" + lines.Length);
        }
        catch (Exception e)
        {
            return ErrJson("create_note failed: " + e.Message);
        }
    }

    // nx_create_diameter_dimension <view_id> <body_id> <edge_index> <pos_x> <pos_y> [upper] [lower] [name]
    // Needed for shafts: a linear dimension between two edges cannot express a diameter.
    private static string CreateDiameterDimension(string[] parts)
    {
        if (parts.Length < 6)
            return ErrJson("usage: nx_create_diameter_dimension <view_id> <body_id> <edge_index> <pos_x> <pos_y> [upper=0.1] [lower=-0.1] [name]");
        try
        {
            _session = Session.GetSession();
            if (_part == null) return ErrJson("no active part; run nx_create_part first");

            DrawingSheet sheet;
            DraftingView view = ResolveViewAnySheet(parts[1].Trim(), out sheet);
            try { sheet.Open(); } catch { }

            Body body = ResolveBody(parts[2].Trim());
            Edge[] edges = body.GetEdges();
            int ei = int.Parse(parts[3], CultureInfo.InvariantCulture);
            if (ei < 0 || ei >= edges.Length)
                return ErrJson("edge_index out of range: " + ei + " (have " + edges.Length + ")");
            Edge e1 = edges[ei];

            double posx = D(parts[4]);
            double posy = D(parts[5]);
            double upper = (parts.Length > 6 && !string.IsNullOrEmpty(parts[6].Trim())) ? D(parts[6]) : 0.1;
            double lower = (parts.Length > 7 && !string.IsNullOrEmpty(parts[7].Trim())) ? D(parts[7]) : -0.1;
            string dimName = (parts.Length > 8 && !string.IsNullOrEmpty(parts[8].Trim()))
                ? parts[8].Trim() : "MCP_DIAMETER";

            NXOpen.Annotations.DimensionData ddd = _part.Annotations.NewDimensionData();
            NXOpen.Annotations.Associativity a1 = _part.Annotations.NewAssociativity();
            a1.FirstObject = e1;
            a1.SecondObject = null;
            a1.ObjectView = view;
            a1.PickPoint = MidpointOfObject(e1);
            a1.PointOption = NXOpen.Annotations.AssociativityPointOption.OnCurve;
            a1.LineOption = NXOpen.Annotations.AssociativityLineOption.None;
            ddd.SetAssociativity(1, new NXOpen.Annotations.Associativity[] { a1 });

            NXOpen.Annotations.Dimension dim =
                _part.Dimensions.CreateDiameterDimension(ddd, new Point3d(posx, posy, 0.0));
            try { dim.SetName(dimName); } catch { }

            try
            {
                NXOpen.Annotations.ILinearTolerance ltObj = (NXOpen.Annotations.ILinearTolerance)dim;
                NXOpen.Annotations.LinearTolerance lt = ltObj.GetTolerance();
                lt.ToleranceType = NXOpen.Annotations.ToleranceType.BilateralTwoLines;
                lt.SetUpperToleranceMm(new NXOpen.Annotations.Value(upper, null, 3));
                lt.SetLowerToleranceMm(new NXOpen.Annotations.Value(lower, null, 3));
                ltObj.SetTolerance(lt);
            }
            catch (Exception et) { Log("diameter tolerance warn: " + et.Message); }

            try { ForceUpdateSheet(sheet); } catch { }

            string jid = ""; try { jid = dim.JournalIdentifier; } catch { }
            double computed = 0.0; try { computed = dim.ComputedSize; } catch { }
            return OkJson("dimension", jid,
                "created diameter dim name=" + dimName
                + " value=" + F(computed) + " tol=+" + F(upper) + "/" + F(lower));
        }
        catch (Exception e)
        {
            return ErrJson("create_diameter_dimension failed: " + e.Message);
        }
    }

    // nx_create_radius_dimension <view_id> <body_id> <edge_index> <pos_x> <pos_y> [name]
    // For fillets and arcs; a linear dimension cannot express a radius.
    private static string CreateRadiusDimension(string[] parts)
    {
        if (parts.Length < 6)
            return ErrJson("usage: nx_create_radius_dimension <view_id> <body_id> <edge_index> <pos_x> <pos_y> [name]");
        try
        {
            _session = Session.GetSession();
            if (_part == null) return ErrJson("no active part; run nx_create_part first");

            DrawingSheet sheet;
            DraftingView view = ResolveViewAnySheet(parts[1].Trim(), out sheet);
            try { sheet.Open(); } catch { }

            Body body = ResolveBody(parts[2].Trim());
            Edge[] edges = body.GetEdges();
            int ei = int.Parse(parts[3], CultureInfo.InvariantCulture);
            if (ei < 0 || ei >= edges.Length)
                return ErrJson("edge_index out of range: " + ei + " (have " + edges.Length + ")");
            Edge e1 = edges[ei];

            double posx = D(parts[4]);
            double posy = D(parts[5]);
            string dimName = (parts.Length > 6 && !string.IsNullOrEmpty(parts[6].Trim()))
                ? parts[6].Trim() : "MCP_RADIUS";

            NXOpen.Annotations.DimensionData ddd = _part.Annotations.NewDimensionData();
            NXOpen.Annotations.Associativity a1 = _part.Annotations.NewAssociativity();
            a1.FirstObject = e1;
            a1.SecondObject = null;
            a1.ObjectView = view;
            a1.PickPoint = MidpointOfObject(e1);
            a1.PointOption = NXOpen.Annotations.AssociativityPointOption.OnCurve;
            a1.LineOption = NXOpen.Annotations.AssociativityLineOption.None;
            ddd.SetAssociativity(1, new NXOpen.Annotations.Associativity[] { a1 });

            NXOpen.Annotations.Dimension dim = _part.Dimensions.CreateRadiusDimension(
                ddd, NXOpen.Annotations.RadiusDimensionType.ToCenter,
                new Point3d(posx, posy, 0.0));
            try { dim.SetName(dimName); } catch { }

            try { ForceUpdateSheet(sheet); } catch { }

            string jid = ""; try { jid = dim.JournalIdentifier; } catch { }
            double computed = 0.0; try { computed = dim.ComputedSize; } catch { }
            return OkJson("dimension", jid,
                "created radius dim name=" + dimName + " value=" + F(computed));
        }
        catch (Exception e)
        {
            return ErrJson("create_radius_dimension failed: " + e.Message);
        }
    }

    // nx_export_pdf <path.pdf> [sheet_name]
    // Exports the current display (or a chosen sheet) to PDF.
    // Entry point is PlotManager.CreatePrintPdfbuilder() - note the lowercase 'b'
    // in "Pdfbuilder"; searching for "PdfBuilder" finds nothing and makes PDF
    // export look impossible. Available since NX5.0.1, Windows only.
    private static string ExportPdf(string[] parts)
    {
        if (parts.Length < 2)
            return ErrJson("usage: nx_export_pdf <path.pdf> [sheet_name]");
        try
        {
            _session = Session.GetSession();
            if (_part == null) return ErrJson("no active part; run nx_create_part first");

            string path = parts[1].Trim();
            if (File.Exists(path) || Directory.Exists(path))
                return ErrJson("refusing to overwrite existing PDF: " + path);
            string dir = null;
            try { dir = Path.GetDirectoryName(path); } catch { }
            if (!string.IsNullOrEmpty(dir))
            {
                try { Directory.CreateDirectory(dir); } catch { }
            }

            // The PDF builder exports the CURRENT DISPLAY. Manually this works because
            // opening a drawing sheet puts NX into the Drafting application; headless
            // we must switch explicitly, otherwise Commit() succeeds but writes nothing.
            try { _session.ApplicationSwitchImmediate("UG_APP_DRAFTING"); }
            catch (Exception ea) { Log("app switch warn: " + ea.Message); }

            // Resolve the sheet to export: by name if given, else the current one.
            DrawingSheet target = null;
            string want = (parts.Length > 2) ? parts[2].Trim() : "";
            try
            {
                if (!string.IsNullOrEmpty(want))
                {
                    foreach (DrawingSheet s in _part.DrawingSheets.ToArray())
                    {
                        string nm = "";
                        try { nm = s.Name; } catch { }
                        if (nm == want) { target = s; break; }
                    }
                }
                if (target == null && !string.IsNullOrEmpty(want))
                    return ErrJson("requested PDF sheet was not found: " + want);
                if (target == null) target = GetSheetToUse();
                target.Open();
                ForceUpdateSheet(target);
            }
            catch (Exception er) { return ErrJson("PDF sheet selection failed: " + er.Message); }

            NXOpen.PrintPDFBuilder pb = _part.PlotManager.CreatePrintPdfbuilder();
            try
            {
                pb.Filename = path;
                try { pb.Append = false; } catch { }
                // PlotSourceBuilder.SetSheets(NXObject[]) is what actually selects the
                // sheets to export. Without it Commit() succeeds but writes no file.
                if (target != null)
                {
                    pb.SourceBuilder.SetSheets(new NXObject[] { target });
                }
                pb.Commit();
            }
            finally { try { pb.Destroy(); } catch { } }

            bool exists = File.Exists(path);
            long size = 0;
            if (exists) { try { size = new FileInfo(path).Length; } catch { } }
            if (!exists || size < 5)
                return ErrJson("PDF commit did not produce a nonempty file; inspect display/sheet state before retrying: " + path);
            byte[] magic = new byte[5];
            using (FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                if (input.Read(magic, 0, magic.Length) != magic.Length || Encoding.ASCII.GetString(magic) != "%PDF-")
                    return ErrJson("exported file has no PDF header; file preserved for diagnosis: " + path);
            return OkJson("pdf", path, "exported " + size + " bytes; PDF header verified");
        }
        catch (Exception e)
        {
            return ErrJson("export_pdf failed: " + e.Message);
        }
    }

    // nx_measure_distance <body_id> <edge_index_1> <edge_index_2>
    // 3D QUERY (no annotation is created) - returns the minimum distance between two
    // edges of a body so an agent can verify geometry numerically.
    // The NXOpen Measure classes do not expose the measured value, so this uses UFUN
    // (NXOpen.UF.UFSession.Sf.FindMinimumDistance), which returns a real number.
    // Requires NXOpen.UF.dll, added to build.bat.
    private static string MeasureDistance(string[] parts)
    {
        if (parts.Length < 4)
            return ErrJson("usage: nx_measure_distance <body_id> <edge_index_1> <edge_index_2>");
        try
        {
            _session = Session.GetSession();
            if (_part == null) return ErrJson("no active part; run nx_create_part first");

            Body body = ResolveBody(parts[1].Trim());
            Edge[] edges = body.GetEdges();
            int i1 = int.Parse(parts[2], CultureInfo.InvariantCulture);
            int i2 = int.Parse(parts[3], CultureInfo.InvariantCulture);
            if (i1 < 0 || i1 >= edges.Length)
                return ErrJson("edge_index_1 out of range: " + i1 + " (have " + edges.Length + ")");
            if (i2 < 0 || i2 >= edges.Length)
                return ErrJson("edge_index_2 out of range: " + i2 + " (have " + edges.Length + ")");

            Edge e1 = edges[i1];
            Edge e2 = edges[i2];

            NXOpen.UF.UFSession uf = NXOpen.UF.UFSession.GetUFSession();
            double dist = 0.0;
            double[] p1 = new double[3];
            double[] p2 = new double[3];
            // UFSf.FindMinimumDistance returned 0 with (0,0,0) points - it does not
            // work for edge-to-edge. UFModl.AskMinimumDist is the general one.
            uf.Modl.AskMinimumDist(e1.Tag, e2.Tag, 0, new double[3], 0, new double[3],
                                   out dist, p1, p2);

            return OkJson(
                "distance", dist.ToString("0.####", CultureInfo.InvariantCulture),
                "min distance=" + F(dist) + " mm  p1=(" + F(p1[0]) + "," + F(p1[1]) + "," + F(p1[2])
                + ")  p2=(" + F(p2[0]) + "," + F(p2[1]) + "," + F(p2[2]) + ")");
        }
        catch (Exception e)
        {
            return ErrJson("measure_distance failed: " + e.Message);
        }
    }

    // nx_create_centerline
    // Auto centreline for cylindrical/conical faces - very common on shaft drawings.
    // Entry: Part.Annotations.Centerlines.CreateAutomaticCenterlineBuilder().
    private static string CreateCenterline(string[] parts)
    {
        try
        {
            _session = Session.GetSession();
            if (_part == null) return ErrJson("no active part; run nx_create_part first");
            try { _session.ApplicationSwitchImmediate("UG_APP_DRAFTING"); }
            catch (Exception ea) { Log("app switch warn: " + ea.Message); }

            int before = 0;
            try { before = _part.Annotations.Centerlines.ToArray().Length; } catch { }

            NXOpen.Annotations.AutomaticCenterlineBuilder cb =
                _part.Annotations.Centerlines.CreateAutomaticCenterlineBuilder();
            // Views is a read-only SelectDraftingViewList - the view must be ADDED to
            // it. Without a view the builder fails with NX status 1175325.
            DraftingView clView = null;
            try
            {
                if (parts.Length > 1 && !string.IsNullOrEmpty(parts[1].Trim()))
                {
                    DrawingSheet sh;
                    clView = ResolveViewAnySheet(parts[1].Trim(), out sh);
                }
                if (clView == null)
                {
                    DrawingSheet cs = _part.DrawingSheets.CurrentDrawingSheet;
                    if (cs != null)
                    {
                        try { cs.Open(); } catch { }
                        DraftingView[] vs = cs.GetDraftingViews();
                        if (vs != null && vs.Length > 0) clView = vs[0];
                    }
                }
            }
            catch { }

            try
            {
                if (clView != null)
                {
                    try { cb.Views.Add(clView); }
                    catch (Exception ev) { Log("view add warn: " + ev.Message); }
                }
                cb.Commit();
            }
            finally { try { cb.Destroy(); } catch { } }

            try { _part.DrawingSheets.RefreshCurrentSheet(); } catch { }
            int after = 0;
            try { after = _part.Annotations.Centerlines.ToArray().Length; } catch { }

            return OkJson("centerlines", after.ToString(CultureInfo.InvariantCulture),
                "centerlines " + before + " -> " + after);
        }
        catch (Exception e)
        {
            return ErrJson("create_centerline failed: " + e.Message);
        }
    }

    // nx_create_roughness <pos_x> <pos_y> [value] [text]
    // Surface finish (Ra) symbol - very common on machining drawings.
    // Entry: Part.Annotations.DraftingSurfaceFinishSymbols.CreateDraftingSurfaceFinishBuilder.
    private static string CreateRoughness(string[] parts)
    {
        if (parts.Length < 3)
            return ErrJson("usage: nx_create_roughness <pos_x> <pos_y> [value] [text]");
        try
        {
            _session = Session.GetSession();
            if (_part == null) return ErrJson("no active part; run nx_create_part first");
            try { _session.ApplicationSwitchImmediate("UG_APP_DRAFTING"); }
            catch (Exception ea) { Log("app switch warn: " + ea.Message); }

            double posx = D(parts[1]);
            double posy = D(parts[2]);
            string value = (parts.Length > 3) ? parts[3].Trim() : "1.6";

            NXOpen.Annotations.DraftingSurfaceFinishBuilder rb =
                _part.Annotations.DraftingSurfaceFinishSymbols.CreateDraftingSurfaceFinishBuilder(null);
            string jid = "";
            try
            {
                rb.Origin.OriginPoint = new Point3d(posx, posy, 0.0);
                rb.Origin.Anchor = NXOpen.Annotations.OriginBuilder.AlignmentPosition.TopLeft;
                // A1 is the roughness value field (the "a1" slot of the symbol).
                rb.SingleRoughnessValue = true;
                rb.A1 = value;
                NXOpen.Annotations.DraftingSurfaceFinish sf =
                    (NXOpen.Annotations.DraftingSurfaceFinish)rb.Commit();
                try { sf.SetName("MCP_ROUGHNESS"); } catch { }
                try { jid = sf.JournalIdentifier; } catch { }
            }
            finally { try { rb.Destroy(); } catch { } }

            try { _part.DrawingSheets.RefreshCurrentSheet(); } catch { }
            return OkJson("roughness", jid, "created surface finish value=" + value);
        }
        catch (Exception e)
        {
            return ErrJson("create_roughness failed: " + e.Message);
        }
    }

    // nx_export_dwg <path.dwg|.dxf> [sheet_name]
    // DXF/DWG export. Entry is Session.DexManager.CreateDxfdwgCreator() - a NO-ARG
    // method, so its XML name has no "(" and is easy to miss when grepping for
    // signatures with parameters.
    private static string ExportDwg(string[] parts)
    {
        if (parts.Length < 2)
            return ErrJson("usage: nx_export_dwg <path.dwg|.dxf> [sheet_name]");
        try
        {
            _session = Session.GetSession();
            if (_part == null) return ErrJson("no active part; run nx_create_part first");

            string path = parts[1].Trim();
            string dir = null;
            try { dir = Path.GetDirectoryName(path); } catch { }
            if (!string.IsNullOrEmpty(dir)) { try { Directory.CreateDirectory(dir); } catch { } }

            // OutputTo must be set to Drafting, otherwise Commit() succeeds but
            // writes no file - same class of trap as SourceBuilder.SetSheets for PDF.
            try { _session.ApplicationSwitchImmediate("UG_APP_DRAFTING"); }
            catch (Exception ea) { Log("app switch warn: " + ea.Message); }

            NXOpen.DxfdwgCreator dx = _session.DexManager.CreateDxfdwgCreator();
            try
            {
                dx.OutputFile = path;
                dx.OutputTo = NXOpen.DxfdwgCreator.OutputToOption.Drafting;
                dx.OutputFileType = path.ToLowerInvariant().EndsWith(".dxf")
                    ? NXOpen.DxfdwgCreator.OutputFileTypeOption.Dxf
                    : NXOpen.DxfdwgCreator.OutputFileTypeOption.Dwg;
                // Try Explicit source part instead of DisplayPart: headless the
                // display-part route produced no file at all.
                dx.InputFile = _partPath ?? _part.FullPath;
                dx.ExportFrom = NXOpen.DxfdwgCreator.ExportFromOption.ExistingPart;
                dx.ExportAs = NXOpen.DxfdwgCreator.ExportAsOption.TwoD;
                try { dx.ExportData = NXOpen.DxfdwgCreator.ExportDataOption.Drawing; }
                catch (Exception ed) { Log("export data warn: " + ed.Message); }
                if (parts.Length > 2 && !string.IsNullOrEmpty(parts[2].Trim()))
                {
                    try { dx.DrawingList = parts[2].Trim(); }
                    catch (Exception el) { Log("drawing list warn: " + el.Message); }
                }
                dx.Commit();
            }
            finally { try { dx.Destroy(); } catch { } }

            bool exists = File.Exists(path);
            long size = 0;
            if (exists) { try { size = new FileInfo(path).Length; } catch { } }
            return OkJson("dwg", path,
                exists ? ("exported " + size + " bytes")
                       : "commit ok but file not found - check sheet name and display part");
        }
        catch (Exception e)
        {
            return ErrJson("export_dwg failed: " + e.Message);
        }
    }

    /// compiled with the legacy csc, which predates C# 7 local functions.
    // nx_create_detail_view <parent_view_id> <x> <y> [radius] [scale] [view_name]
    // Detail (enlarged) view: circular boundary around a point in the parent view.
    private static string CreateDetailView(string[] parts)
    {
        if (parts.Length < 4)
            return ErrJson("usage: nx_create_detail_view <parent_view_id> <x> <y> [radius] [scale] [view_name]");
        try
        {
            _session = Session.GetSession();
            if (_part == null) return ErrJson("no active part; run nx_create_part first");
            try { _session.ApplicationSwitchImmediate("UG_APP_DRAFTING"); }
            catch (Exception ea) { Log("app switch warn: " + ea.Message); }

            DrawingSheet sheet = CurrentSheet();
            DraftingView[] views = sheet.GetDraftingViews();
            if (views == null || views.Length == 0) return ErrJson("sheet has no drafting views");

            string token = parts[1].Trim();
            DraftingView parent = null;
            for (int i = 0; i < views.Length; i++)
            {
                string nm = ""; try { nm = views[i].Name; } catch { }
                string ji = ""; try { ji = views[i].JournalIdentifier; } catch { }
                if (token == i.ToString() || token == nm || token == ji) { parent = views[i]; break; }
            }
            if (parent == null) parent = views[0];

            double x = D(parts[2]);
            double y = D(parts[3]);
            double radius = (parts.Length > 4 && !string.IsNullOrEmpty(parts[4].Trim())) ? D(parts[4]) : 10.0;
            double scale = (parts.Length > 5 && !string.IsNullOrEmpty(parts[5].Trim())) ? D(parts[5]) : 2.0;

            NXOpen.Drawings.DetailViewBuilder dvb =
                _part.DraftingViews.CreateDetailViewBuilder(null);
            string id = "";
            try
            {
                // Boundary must be declared circular first; the two points are then
                // centre + a point on the circle (radius).
                dvb.Type = NXOpen.Drawings.DetailViewBuilder.Types.Circular;
                // Pass a REAL model view: with null views NX could not tell the two
                // points apart and rejected them as identical.
                NXOpen.View mv = null;
                try { mv = _part.ModelingViews.WorkView; } catch { }
                dvb.Parent.View.SetValue(
                    NXOpen.InferSnapType.SnapType.None,
                    parent, mv, new Point3d(x, y, 0.0),
                    parent, mv, new Point3d(x + radius, y, 0.0));
                // Origin is the placement of the new view; place it offset to the right.
                // Same 3-arg pattern: SetValue(Point, View, Point3d)
                dvb.Origin.Placement.SetValue(null, null, new Point3d(x + 80.0, y, 0.0));
                dvb.Scale.Numerator = scale;
                dvb.Scale.Denominator = 1.0;
                dvb.Commit();
            }
            finally { try { dvb.Destroy(); } catch { } }

            try { ForceUpdateSheet(sheet); } catch { }
            int count = 0;
            try { count = sheet.GetDraftingViews().Length; } catch { }
            return OkJson("view", id, "detail view created views=" + count
                + " origin=(" + F(x) + "," + F(y) + ") r=" + F(radius) + " scale=" + F(scale));
        }
        catch (Exception e)
        {
            return ErrJson("create_detail_view failed: " + e.Message);
        }
    }

    // nx_create_title_block <pos_x> <pos_y>
    // Places a title block on the current sheet.
    private static string ExplicitTitleAttribute(params string[] keys)
    {
        foreach(string key in keys)
        {
            try { string value=_part.GetStringUserAttribute(key,-1); if(value!=null)return value; }
            catch(NXException) { }
        }
        return "";
    }
    private static string CreateTitleBlock(string[] parts)
    {
        if(parts.Length<3)return ErrJson("usage: nx_create_title_block <lower_right_x> <lower_right_y>");
        Session.UndoMarkId mark=default(Session.UndoMarkId);bool marked=false;
        try
        {
            AGuard();DrawingSheet sheet=CurrentSheet();
            double right=D(parts[1]),bottom=D(parts[2]);const double width=180,height=56;
            if(double.IsNaN(right)||double.IsInfinity(right)||double.IsNaN(bottom)||double.IsInfinity(bottom)
                ||right-width<0||bottom<0||right>sheet.Length||bottom+height>sheet.Height)
                return ErrJson("title rectangle must fit inside the current sheet; lower-right anchor and fixed size 180x56 mm");
            const string name="MCP_LEGACY_TITLE_BLOCK";
            foreach(NXOpen.Tag old in DTables())if(DObject(old).Name==name)
                return ErrJson("visible title table already exists; inspect nx_drafting_list_tables and update explicitly");
            // All values are explicit existing attributes. Missing values stay blank.
            object[] rows=new object[]{
                new object[]{"名称",ExplicitTitleAttribute("名称","零件名称","PART_NAME","PartName"),"图号",ExplicitTitleAttribute("图号","DRAWING_NUMBER","DrawingNumber")},
                new object[]{"材料",ExplicitTitleAttribute("材料","MATERIAL","Material"),"比例",ExplicitTitleAttribute("比例","SCALE","Scale")},
                new object[]{"设计",ExplicitTitleAttribute("设计","DESIGNER","Designer"),"审核",ExplicitTitleAttribute("审核","REVIEWER","Reviewer")},
                new object[]{"日期",ExplicitTitleAttribute("日期","DATE","Date"),"单位",ExplicitTitleAttribute("单位","UNITS","Units")},
                new object[]{"图幅",ExplicitTitleAttribute("图幅","SHEET_SIZE","SheetSize"),"页次",ExplicitTitleAttribute("页次","SHEET_NUMBER","SheetNumber")},
                new object[]{"版本",ExplicitTitleAttribute("版本","REVISION","Revision"),"重量",ExplicitTitleAttribute("重量","MASS","Mass")},
                new object[]{"校核",ExplicitTitleAttribute("校核","CHECKER","Checker"),"批准",ExplicitTitleAttribute("批准","APPROVER","Approver")}
            };
            var payload=ADict("name",name,"x",right-width,"y",bottom+height,
                "column_widths",new object[]{25.0,65.0,25.0,65.0},
                "row_heights",new object[]{8.0,8.0,8.0,8.0,8.0,8.0,8.0},
                "cells",rows,"font","chinesef","text_height",3.0,"auto_fit_text",true);
            mark=_session.SetUndoMark(Session.MarkVisibility.Visible,"NX MCP visible title table");marked=true;
            DCreateTable(payload);
            foreach(NXOpen.Tag table in DTables())if(DObject(table).Name==name)
                return OkJson("title_block",DObject(table).JournalIdentifier,
                    "visible editable title table 7x4, size 180x56 mm; lower-right="+F(right)+","+F(bottom)+"; explicit part attributes; missing values blank");
            throw new Exception("created title table did not read back");
        }
        catch(Exception error)
        {
            string rollback="not_needed";
            if(marked)try{_session.UndoToMark(mark,null);rollback="restored";}catch(Exception undo){rollback="FAILED: "+undo.Message;}
            return ErrJson("create_title_block failed: "+error.Message+"; rollback="+rollback);
        }
    }

    // nx_set_attribute <name> <value...>
    // Title-block cells normally display PART ATTRIBUTES, so filling a title block
    // means setting attributes. Value may contain spaces (tokens are re-joined).
    private static string SetAttributeCmd(string[] parts)
    {
        if (parts.Length < 3)
            return ErrJson("usage: nx_set_attribute <name> <value...>");
        try
        {
            _session = Session.GetSession();
            if (_part == null) return ErrJson("no active part; run nx_create_part first");

            string name = parts[1].Trim();
            var sb = new StringBuilder();
            for (int i = 2; i < parts.Length; i++)
            {
                if (i > 2) sb.Append(" ");
                sb.Append(parts[i]);
            }
            string value = sb.ToString().Trim();

            // Second arg is the array INDEX, not the type: -1 means "not an array".
            _part.SetUserAttribute(name, -1, value, NXOpen.Update.Option.Now);
            return OkJson("attribute", name, "set " + name + " = " + value);
        }
        catch (Exception e)
        {
            return ErrJson("set_attribute failed: " + e.Message);
        }
    }

    // nx_list_attributes
    // Read back the part attributes (what a title block would display).
    private static string ListAttributes(string[] parts)
    {
        try
        {
            _session = Session.GetSession();
            if (_part == null) return ErrJson("no active part; run nx_create_part first");

            var items = new System.Collections.Generic.List<string>();
            NXOpen.NXObject.AttributeInformation[] atts = _part.GetUserAttributes();
            if (atts != null)
            {
                foreach (NXOpen.NXObject.AttributeInformation a in atts)
                {
                    string t = ""; try { t = a.Title; } catch { }
                    string v = "";
                    try { v = a.StringValue; } catch { }
                    if (string.IsNullOrEmpty(v)) { try { v = a.IntegerValue.ToString(CultureInfo.InvariantCulture); } catch { } }
                    if (string.IsNullOrEmpty(v)) { try { v = a.RealValue.ToString(CultureInfo.InvariantCulture); } catch { } }
                    items.Add(t + "=" + v);
                }
            }
            return OkJson("attributes", items.Count.ToString(CultureInfo.InvariantCulture),
                "found " + items.Count + " attribute(s) [" + string.Join(", ", items.ToArray()) + "]");
        }
        catch (Exception e)
        {
            return ErrJson("list_attributes failed: " + e.Message);
        }
    }

    // nx_create_sheet_border [zones=1]
    // Sheet border lines + optional zone letters/numbers.
    // [NX12 port] Drawings.BordersAndZonesBuilder is deprecated in NX10.0.0; the
    // supported route is Diagramming.SheetBordersAndZonesBuilder, reached via
    // Part.DiagrammingManager.SheetBordersAndZones.
    private static string CreateSheetBorder(string[] parts)
    {
        try
        {
            _session = Session.GetSession();
            if (_part == null) return ErrJson("no active part; run nx_create_part first");
            try { _session.ApplicationSwitchImmediate("UG_APP_DRAFTING"); }
            catch (Exception ea) { Log("app switch warn: " + ea.Message); }

            bool zones = !(parts.Length > 1 && parts[1].Trim() == "0");

            int before = 0;
            try { before = _part.DiagrammingManager.SheetBordersAndZones.ToArray().Length; } catch { }

            NXOpen.Diagramming.SheetBordersAndZonesBuilder bz =
                _part.DiagrammingManager.SheetBordersAndZones
                     .CreateSheetBordersAndZonesBuilder(null);
            try
            {
                bz.CreateBorders = true;
                // Zone sizes default to 0, which NX rejects:
                // "宽度必须大于零...或小于图纸页大小的四分之一".
                bz.HorizontalSize = 50.0;
                bz.VerticalSize = 50.0;
                bz.Width = 1.0;
                // Zone settings live on the sub-builder and CreateZones must be on.
                NXOpen.Diagramming.SheetZoneSettingsBuilder zs = bz.SheetZoneSettings;
                try { zs.CreateZones = true; }
                catch (Exception ez) { Log("zone create warn: " + ez.Message); }
                try { zs.HorizontalSize = 50.0; }
                catch (Exception ez) { Log("zone hsize warn: " + ez.Message); }
                try { zs.VerticalSize = 50.0; }
                catch (Exception ez) { Log("zone vsize warn: " + ez.Message); }
                try { zs.CreateZoneMarkings = true; }
                catch (Exception ez) { Log("zone marking warn: " + ez.Message); }
                if (zones)
                {
                    bz.CreateZoneLabels = true;
                    bz.CreateZoneMarking = true;
                }
                bz.Commit();
            }
            finally { try { bz.Destroy(); } catch { } }

            int after = 0;
            try { after = _part.DiagrammingManager.SheetBordersAndZones.ToArray().Length; } catch { }

            try { ForceUpdateSheet(CurrentSheet()); } catch { }
            return OkJson("border", "ok",
                "sheet border enabled zones=" + zones
                + "  count " + before + " -> " + after
                + (after <= before ? "  [WARNING: nothing was actually created]" : ""));
        }
        catch (Exception e)
        {
            return ErrJson("create_sheet_border failed: " + e.Message);
        }
    }

    private static readonly string _annotationSelectorSession=Guid.NewGuid().ToString("N");
    private static string AnnotationSelector(NXObject annotation)
    { return "ANNTAG:"+_annotationSelectorSession+":"+_part.Tag.ToString()+":"+annotation.Tag.ToString(); }
    private static string AnnotationSelectorFields(NXObject annotation)
    {
        string name="";try{name=annotation.Name;}catch{}
        return "|selector="+AnnotationSelector(annotation)+"|native_tag="+annotation.Tag.ToString()+"|name="+OneLine(name);
    }
    private static bool AnnotationMatches(NXObject o, string token)
    {
        if(token.StartsWith("ANNTAG:",StringComparison.Ordinal))return token==AnnotationSelector(o);
        try { if (token == o.JournalIdentifier) return true; } catch { }
        try { if (token == o.Name) return true; } catch { }
        return false;
    }

    // nx_delete_annotation <id | kind:index>
    // Removes a note / dimension / GDT / roughness symbol. Needed because a stray
    // annotation cannot be removed any other way (nx_undo only reaches the most
    // recent visible mark, which is often not enough).
    // Deletion goes through Session.UpdateManager: AddToDeleteList + DoUpdate.
    private static string DeleteAnnotation(string[] parts)
    {
        if (parts.Length < 2)
            return ErrJson("usage: nx_delete_annotation <id|kind:index>  (kind = note|dim|gdt|rough)");
        try
        {
            _session = Session.GetSession();
            if (_part == null) return ErrJson("no active part; run nx_create_part first");
            string token=string.Join(" ",parts,1,parts.Length-1).Trim();
            if(token.StartsWith("b64:",StringComparison.Ordinal))
                token=Encoding.UTF8.GetString(Convert.FromBase64String(token.Substring(4)));
            if(string.IsNullOrWhiteSpace(token)||token.IndexOf('\0')>=0||token.IndexOf('\r')>=0||token.IndexOf('\n')>=0)
                return ErrJson("annotation selector must be nonempty single-line text; no object deleted");

            // [NX12 port] Update.AddToDeleteList is deprecated exactly in NX12.0.0;
            // use Update.AddObjectsToDeleteList(TaggedObject[]) instead.
            var objs = new List<NXOpen.TaggedObject>();

            // ---- collect candidates by kind ----
            var notes = new List<NXOpen.Annotations.Note>();
            var dims = new List<NXOpen.Annotations.Dimension>();
            var gdts = new List<NXOpen.Annotations.Gdt>();
            var roughs = new List<NXOpen.Annotations.DraftingSurfaceFinish>();
            try { foreach (NXOpen.Annotations.Note n in _part.Notes.ToArray()) notes.Add(n); } catch { }
            try { foreach (NXOpen.Annotations.Dimension d in _part.Dimensions.ToArray()) dims.Add(d); } catch { }
            try { foreach (NXOpen.Annotations.Gdt g in _part.Gdts.ToArray()) gdts.Add(g); } catch { }
            try
            {
                foreach (NXOpen.Annotations.DraftingSurfaceFinish sf
                         in _part.Annotations.DraftingSurfaceFinishSymbols.ToArray()) roughs.Add(sf);
            }
            catch { }

            // ---- 1) exact id / name match across all kinds ----
            foreach (var n in notes) if (AnnotationMatches(n, token)) objs.Add(n);
            foreach (var d in dims) if (AnnotationMatches(d, token)) objs.Add(d);
            foreach (var g in gdts) if (AnnotationMatches(g, token)) objs.Add(g);
            foreach (var r in roughs) if (AnnotationMatches(r, token)) objs.Add(r);

            // ---- 2) fallback: "kind:index" ----
            if (objs.Count == 0 && token.Contains(":"))
            {
                string[] kv = token.Split(':');
                string kind = kv[0].Trim().ToLowerInvariant();
                int idx;
                if (kv.Length == 2 && int.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out idx))
                {
                    if (kind == "note" && idx >= 0 && idx < notes.Count) objs.Add(notes[idx]);
                    else if (kind == "dim" && idx >= 0 && idx < dims.Count) objs.Add(dims[idx]);
                    else if (kind == "gdt" && idx >= 0 && idx < gdts.Count) objs.Add(gdts[idx]);
                    else if (kind == "rough" && idx >= 0 && idx < roughs.Count) objs.Add(roughs[idx]);
                    else return ErrJson("kind:index out of range: " + token);
                }
            }

            if (objs.Count == 0)
                return ErrJson("no annotation matched: " + token
                    + " (try nx_list_dimensions / nx_list_annotations first)");

            if(objs.Count!=1)return ErrJson("ambiguous annotation selector matched "+objs.Count+" objects; no object deleted; use the unique selector from nx_list_annotations");
            NXOpen.Session.UndoMarkId mark =
                _session.SetUndoMark(NXOpen.Session.MarkVisibility.Visible, "Delete Annotation");
            try
            {
                _session.UpdateManager.AddObjectsToDeleteList(objs.ToArray());
                int errors=_session.UpdateManager.DoUpdate(mark);
                if(errors!=0)throw new Exception("native delete update reported "+errors+" errors");
            }
            catch
            {
                _session.UndoToMark(mark,null);
                throw;
            }
            return OkJson("deleted", token, "deleted exactly 1 annotation");
        }
        catch (Exception e)
        {
            return ErrJson("delete_annotation failed: " + e.Message);
        }
    }

    // nx_list_annotations
    // Read back the non-dimension annotations of a drawing, so an agent can
    // understand an existing 2D process drawing beyond just sizes:
    //   NOTE  - drafting / technical-requirement text   (Part.Notes -> GetText)
    //   GDT   - geometric tolerance                     (Part.Gdts -> GetFcfFrameData)
    //   ROUGH - surface finish symbols                  (Annotations.DraftingSurfaceFinishSymbols)
    private static string ListAnnotations(string[] parts)
    {
        try
        {
            if (_part == null) return ErrJson("no active part");
            var items = new List<string>();

            // ---- 1. notes (technical requirement text) ----
            try
            {
                foreach (NXOpen.Annotations.Note n in _part.Notes.ToArray())
                {
                    string ji = "";
                    try { ji = n.JournalIdentifier; } catch { }
                    string txt = "";
                    try
                    {
                        string[] lines = n.GetText();
                        if (lines != null) txt = string.Join(" / ", lines);
                    }
                    catch { }
                    txt = txt.Replace("\r", " ").Replace("\n", " ").Trim();
                    items.Add("NOTE|" + ji + "|" + txt + AnnotationSelectorFields(n));
                }
            }
            catch { }

            // ---- 2. geometric dimensioning & tolerancing ----
            try
            {
                foreach (NXOpen.Annotations.Gdt g in _part.Gdts.ToArray())
                {
                    string ji = "";
                    try { ji = g.JournalIdentifier; } catch { }
                    // [NX12 port] FcfFrameData.GetFrames()/GeometricCharacteristic and
                    // FcfFrame.GetTolerance()/Get*DatumReference() are all deprecated in
                    // NX10.0.0. Use the recommended drafting FCF builder instead, obtained
                    // from AnnotationManager.CreateDraftingFeatureControlFrameBuilder(Gdt).
                    NXOpen.Annotations.FeatureControlFrameBuilder fb = null;
                    try
                    {
                        fb = _part.Annotations.CreateDraftingFeatureControlFrameBuilder(g);
                        string ch = "";
                        try { ch = fb.Characteristic.ToString(); } catch { }
                        // FeatureControlFrameDataList is a builder *list* object
                        // (Length + FindItem), not a plain array.
                        NXOpen.Annotations.FeatureControlFrameDataBuilderList dataList = null;
                        try { dataList = fb.FeatureControlFrameDataList; } catch { }
                        int nFrames = 0;
                        try { if (dataList != null) nFrames = dataList.Length; } catch { }
                        // NX12 FeatureControlFrameDataList contains at least two frames;
                        // SingleFrame uses only the first. Do not report its unused
                        // second placeholder as another tolerance. Composite keeps all.
                        if (fb.FrameStyle == NXOpen.Annotations.FeatureControlFrameBuilder.FcfFrameStyle.SingleFrame)
                            nFrames = Math.Min(nFrames, 1);
                        if (dataList == null || nFrames <= 0)
                        {
                            items.Add("GDT|" + ji + "|" + ch + "|<no frames>" + AnnotationSelectorFields(g));
                            continue;
                        }
                        for (int fi = 0; fi < nFrames; fi++)
                        {
                            NXOpen.Annotations.FeatureControlFrameDataBuilder d = null;
                            try { d = dataList.FindItem(fi); } catch { }
                            if (d == null) continue;
                            string tol = "";
                            try { object v = d.ToleranceValue; if (v != null) tol = v.ToString(); } catch { }
                            string d1 = "", d2 = "", d3 = "";
                            try { object r = d.PrimaryDatumReference; if (r != null) d1 = r.ToString(); } catch { }
                            try { object r = d.SecondaryDatumReference; if (r != null) d2 = r.ToString(); } catch { }
                            try { object r = d.TertiaryDatumReference; if (r != null) d3 = r.ToString(); } catch { }
                            items.Add("GDT|" + ji + "|" + ch
                                + "|tol=" + OneLine(tol)
                                + "|datum=" + OneLine(d1) + "," + OneLine(d2) + "," + OneLine(d3) + AnnotationSelectorFields(g));
                        }
                    }
                    catch (Exception eg)
                    {
                        items.Add("GDT|" + ji + "|<read error: " + OneLine(eg.Message) + ">" + AnnotationSelectorFields(g));
                    }
                    finally
                    {
                        if (fb != null) { try { fb.Destroy(); } catch { } }
                    }
                }
            }
            catch { }

            // ---- 3. surface finish (roughness) symbols ----
            try
            {
                NXOpen.Annotations.DraftingSurfaceFinishCollection sfs =
                    _part.Annotations.DraftingSurfaceFinishSymbols;
                foreach (NXOpen.Annotations.DraftingSurfaceFinish sf in sfs.ToArray())
                {
                    string ji = "";
                    try { ji = sf.JournalIdentifier; } catch { }
                    items.Add("ROUGH|" + ji + AnnotationSelectorFields(sf));
                }
            }
            catch { }

            return OkJsonList("annotations", items, "found " + items.Count + " annotation(s)");
        }
        catch (Exception e)
        {
            return ErrJson("list_annotations failed: " + e.Message);
        }
    }

    private static string OneLine(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\r", " ").Replace("\n", " ").Trim();
    }

    private static string CreatePart(string[] parts)
    {
        if (parts.Length < 2) return ErrJson("usage: nx_create_part <path>");
        string path = parts[1];
        string units = (parts.Length > 2 ? parts[2] : "mm").ToLowerInvariant();
        if (units != "mm" && units != "inch") return ErrJson("units must be mm or inch; no part created");
        _session = Session.GetSession();
        var lw = _session.ListingWindow;
        try { lw.Open(); } catch { }

        if (File.Exists(path) || Directory.Exists(path)) return ErrJson("refusing to overwrite existing part: " + path);

        // Use NewDisplay: FileNewBuilder crashes with access violation in this
        // resident-loader context (unmanaged exception cannot be caught).
        NXOpen.Part part = _session.Parts.NewDisplay(path, units == "inch" ? Part.Units.Inches : Part.Units.Millimeters);
        try { _session.ApplicationSwitchImmediate("UG_APP_MODELING"); } catch { }
        _part = part;
        _partPath = path;
        ResetTaskState();
        return OkJson("part", path, "created");
    }

    private static void ResetTaskState()
    {
        _modelUndo.Clear();
        _sketches.Clear();
        _sketchPlanes.Clear();
        _bodies.Clear();
        _sketchCounter = 0;
        _bodyCounter = 0;
    }

    private static string SavePart()
    {
        if (_part == null) return ErrJson("no active part");
        // [NX12 fix] When a part holds drawing sheets that are not displayed at save
        // time, NX pops a MODAL "保存 CGM" warning ("CGM will be deleted, continue?").
        // NX then blocks until a human clicks, so every pipe call hangs until the
        // 300s timeout. Explicitly disabling drawing-CGM generation removes the
        // prompt. PartSaveOptions.DrawingCgmData exists since NX3.0, so it is safe
        // on NX 12.
        try { _part.SaveOptions.DrawingCgmData = false; }
        catch (Exception e) { Log("save options warn: " + e.Message); }
        _part.Save(NXOpen.BasePart.SaveComponents.False, NXOpen.BasePart.CloseAfterSave.False);
        return OkJson("saved", _part.FullPath, "saved");
    }

    private static string ReleaseTask()
    {
        // Loader stays resident. Only clear task state and restore normal
        // manual editing (NX is already interactive after each command).
        ResetTaskState();
        _part = null;
        _partPath = null;
        try { FitView(); } catch { }
        return OkJson("task state cleared; loader stays ready");
    }

    private static Plane PlaneZ(double z)
    {
        return _part.Planes.CreatePlane(
            new Point3d(0.0, 0.0, z),
            new Vector3d(0.0, 0.0, 1.0),
            SmartObject.UpdateOption.WithinModeling);
    }

    private static string CreateSketch(string[] parts)
    {
        if (_part == null) return ErrJson("no active part");
        string plane = (parts.Length > 1 ? parts[1] : "XY").ToUpperInvariant();
        if (plane != "XY" && plane != "XZ" && plane != "YZ")
            return ErrJson("plane must be XY, XZ or YZ");

        // Optional explicit name is one Base64 UTF-8 token. The returned
        // SKETCH_n stays the opaque task-local identifier used by builders.
        string requestedName = "";
        if (parts.Length > 2)
        {
            requestedName = Encoding.UTF8.GetString(Convert.FromBase64String(parts[2]));
            if (requestedName.IndexOf('\0') >= 0 || requestedName.IndexOf('\r') >= 0 || requestedName.IndexOf('\n') >= 0)
                return ErrJson("sketch name cannot contain NUL or line breaks");
            foreach(Sketch existing in _part.Sketches)
                if(!string.IsNullOrEmpty(requestedName) && (SketchNativeName(existing) == requestedName || existing.Name == requestedName))
                    return ErrJson("an existing sketch already has the requested name; no sketch created");
        }
        _sketchCounter++;
        string id = "SKETCH_" + _sketchCounter;

        // NX 2506 does not reliably honor a bare CreatePlane(origin, normal)
        // when CreateSketchInPlaceBuilder2 commits a non-XY sketch: the sketch
        // can silently fall back to Z=0. Use an explicitly oriented fixed plane
        // and fully specify origin + horizontal axis + ExistingPlane mode.
        Point3d origin = new Point3d(0.0, 0.0, 0.0);
        Plane planeRef = _part.Planes.CreateFixedTypePlane(
            origin,
            SketchPlaneMatrix(plane),
            SmartObject.UpdateOption.WithinModeling);
        Direction axisRef = _part.Directions.CreateDirection(
            origin,
            SketchLocalXAxis(plane),
            SmartObject.UpdateOption.WithinModeling);
        NXOpen.Point sketchOrigin = _part.Points.CreatePoint(origin);

        var b = _part.Sketches.CreateSketchInPlaceBuilder2(null);
        Sketch sk;
        try
        {
            b.PlaneReference = planeRef;
            b.AxisReference = axisRef;
            b.SketchOrigin = sketchOrigin;
            b.PlaneOption = Sketch.PlaneOption.ExistingPlane;
            b.OriginOption = OriginMethod.SpecifyPoint;
            sk = (Sketch)b.Commit();
        }
        finally
        {
            b.Destroy();
        }
        if(string.IsNullOrEmpty(requestedName)) { try { sk.SetName(id); } catch { } }
        else
        {
            sk.SetName(requestedName);
            // A Sketch and its history feature are distinct NX objects. NX12
            // exposes the associated feature through Sketch.Feature (since NX3).
            NXOpen.Features.Feature sketchFeature=sk.Feature;
            if(sketchFeature!=null)sketchFeature.SetName(requestedName);
            string sketchName=sk.Name,featureName=sketchFeature==null?"":sketchFeature.Name;
            Log("sketch name readback requested=["+requestedName+"] sketch=["+sketchName+"] feature=["+featureName+"]");
            if(sketchName != requestedName && featureName != requestedName)
                throw new Exception("explicit sketch name did not read back; requested=["+requestedName+"] sketch=["+sketchName+"] feature=["+featureName+"]; command must roll back");
        }
        try { sk.Activate(Sketch.ViewReorient.True); } catch { }
        _sketches[id] = sk;
        _sketchPlanes[id] = plane;
        return AJson.Serialize(ADict("ok",true,"sketch_id",id,"sketch_name",SketchNativeName(sk),"result","created plane="+plane));
    }

    private static string SketchLine(string[] parts)
    {
        if (parts.Length < 6) return ErrJson("usage: nx_sketch_line <sketch_id> x1 y1 x2 y2");
        var sk = GetSketch(parts[1]);
        double x1 = D(parts[2]), y1 = D(parts[3]), x2 = D(parts[4]), y2 = D(parts[5]);
        var curve = _part.Curves.CreateLine(
            SketchPoint(parts[1], x1, y1),
            SketchPoint(parts[1], x2, y2));
        sk.AddGeometry(curve, Sketch.InferConstraintsOption.InferNoConstraints);
        return OkJson("added line " + parts[2] + "," + parts[3] + " -> " + parts[4] + "," + parts[5]);
    }

    private static string SketchRectangle(string[] parts)
    {
        if (parts.Length < 6) return ErrJson("usage: nx_sketch_rectangle <sketch_id> cx cy w h");
        var sk = GetSketch(parts[1]);
        double cx = D(parts[2]), cy = D(parts[3]), w = D(parts[4]), h = D(parts[5]);
        double x1 = cx - w / 2.0, y1 = cy - h / 2.0;
        double x2 = cx + w / 2.0, y2 = cy + h / 2.0;
        AddLine(sk, parts[1], x1, y1, x2, y1);
        AddLine(sk, parts[1], x2, y1, x2, y2);
        AddLine(sk, parts[1], x2, y2, x1, y2);
        AddLine(sk, parts[1], x1, y2, x1, y1);
        return OkJson("added rectangle " + w + "x" + h + " at " + cx + "," + cy);
    }

    private static string SketchCircle(string[] parts)
    {
        if (parts.Length < 5) return ErrJson("usage: nx_sketch_circle <sketch_id> cx cy diameter");
        var sk = GetSketch(parts[1]);
        double cx = D(parts[2]), cy = D(parts[3]), dia = D(parts[4]);
        double r = dia / 2.0;
        string plane = GetSketchPlaneName(parts[1]);
        var ell = _part.Curves.CreateEllipse(
            SketchPoint(parts[1], cx, cy),
            SketchVectorForPlane(plane, 1.0, 0.0),
            SketchVectorForPlane(plane, 0.0, 1.0),
            r, r, 0.0, 2.0 * Math.PI);
        sk.AddGeometry(ell, Sketch.InferConstraintsOption.InferNoConstraints);
        return OkJson("added circle d=" + dia + " at " + cx + "," + cy);
    }

    private static string SketchArc(string[] parts)
    {
        if (parts.Length < 7) return ErrJson("usage: nx_sketch_arc <sketch_id> cx cy radius start_deg end_deg");
        var sk = GetSketch(parts[1]);
        double cx = D(parts[2]), cy = D(parts[3]), r = D(parts[4]);
        double a1 = D(parts[5]), a2 = D(parts[6]);
        double rad1 = a1 * Math.PI / 180.0;
        double rad2 = a2 * Math.PI / 180.0;
        string plane = GetSketchPlaneName(parts[1]);
        var arc = _part.Curves.CreateArc(
            SketchPoint(parts[1], cx, cy),
            SketchVectorForPlane(plane, 1.0, 0.0),
            SketchVectorForPlane(plane, 0.0, 1.0),
            r, rad1, rad2);
        sk.AddGeometry(arc, Sketch.InferConstraintsOption.InferNoConstraints);
        return OkJson("added arc r=" + r + " " + a1 + ".." + a2 + " deg at " + cx + "," + cy);
    }

    private static string FinishSketch(string[] parts)
    {
        if (parts.Length < 2) return ErrJson("usage: nx_finish_sketch <sketch_id>");
        var sk = GetSketch(parts[1]);
        try { sk.Deactivate(Sketch.ViewReorient.True, Sketch.UpdateLevel.Model); } catch { }
        return OkJson("finished " + parts[1]);
    }

    private static string Extrude(string[] parts)
    {
        if (parts.Length < 3) return ErrJson("usage: nx_extrude <sketch_id> <distance> [reverse] [start_offset] [create|subtract] [body_id]");
        var sk = GetSketch(parts[1]);
        double dist = D(parts[2]);
        bool reverse = parts.Length > 3 && parts[3] == "1";
        double startOff = parts.Length > 4 ? D(parts[4]) : 0.0;
        string op = parts.Length > 5 ? parts[5] : "create";
        if (op != "create" && op != "subtract") return ErrJson("operation must be create or subtract");
        Body target = null;
        string targetId = parts.Length > 6 ? parts[6] : null;
        if (targetId != null) target = GetBody(targetId);
        if (op == "subtract" && target == null) return ErrJson("subtract requires an explicit target_body_id");
        if (double.IsNaN(dist) || double.IsInfinity(dist) || dist <= 0 || double.IsNaN(startOff) || double.IsInfinity(startOff) || startOff < 0)
            return ErrJson("distance must be positive and start_offset non-negative finite numbers");

        var bt = op == "subtract"
            ? BooleanOperation.BooleanType.Subtract
            : (target != null
                ? BooleanOperation.BooleanType.Unite
                : BooleanOperation.BooleanType.Create);

        var section = _part.Sections.CreateSection();
        var rule = _part.ScRuleFactory.CreateRuleCurveFeature(new Feature[] { sk.Feature });
        section.AddToSection(
            new SelectionIntentRule[] { rule }, null, null, null,
            new Point3d(0.0, 0.0, 0.0), Section.Mode.Create, false);

        Vector3d axis = SketchExtrudeAxis(GetSketchPlaneName(parts[1]));
        if (reverse) axis = new Vector3d(-axis.X, -axis.Y, -axis.Z);
        var dir = _part.Directions.CreateDirection(
            new Point3d(0.0, 0.0, 0.0), axis,
            SmartObject.UpdateOption.WithinModeling);

        var b = _part.Features.CreateExtrudeBuilder(null);
        Feature feature;
        try
        {
            b.Section = section;
            b.Direction = dir;
            b.Limits.StartExtend.Value.RightHandSide = startOff.ToString("0.###", CultureInfo.InvariantCulture);
            b.Limits.EndExtend.Value.RightHandSide = (startOff + dist).ToString("0.###", CultureInfo.InvariantCulture);
            b.BooleanOperation.Type = bt;
            if (target != null)
            {
                b.BooleanOperation.SetTargetBodies(new Body[] { target });
            }
            b.AllowSelfIntersectingSection(true);
            feature = b.CommitFeature();
        }
        finally
        {
            b.Destroy();
        }

        BodyFeature bodyFeature = feature as BodyFeature;
        Body[] resultBodies = bodyFeature != null ? bodyFeature.GetBodies() : new Body[0];
        if (resultBodies.Length != 1) throw new Exception("extrude expected one output body, got " + resultBodies.Length);
        Body resultBody = resultBodies[0];
        string bid;
        if (bt == BooleanOperation.BooleanType.Subtract || bt == BooleanOperation.BooleanType.Unite)
        {
            bid = targetId;
            _bodies[bid] = resultBody;
        }
        else
        {
            bid = "BODY_" + (++_bodyCounter);
            _bodies[bid] = resultBody;
        }
        return OkJson("body_id", bid, "extruded " + dist + (bt == BooleanOperation.BooleanType.Subtract ? " subtract" : "") + (reverse ? " (rev)" : ""));
    }

    private static string Revolve(string[] parts)
    {
        if (parts.Length < 7) return ErrJson("usage: nx_revolve <sketch_id> <ax> <ay> <bx> <by> <angle> [reverse]");
        var sk = GetSketch(parts[1]);
        double ax = D(parts[2]), ay = D(parts[3]);
        double bx = D(parts[4]), by = D(parts[5]);
        double angle = D(parts[6]);
        bool reverse = parts.Length > 7 && parts[7] == "1";

        double vx = bx - ax, vy = by - ay;
        double len = Math.Sqrt(vx * vx + vy * vy);
        if (len < 1e-9) return ErrJson("revolve axis has zero length");
        vx /= len; vy /= len;
        if (reverse) { vx = -vx; vy = -vy; }

        var section = _part.Sections.CreateSection();
        var rule = _part.ScRuleFactory.CreateRuleCurveFeature(new Feature[] { sk.Feature });
        section.AddToSection(
            new SelectionIntentRule[] { rule }, null, null, null,
            new Point3d(0.0, 0.0, 0.0), Section.Mode.Create, false);

        NXOpen.Axis axis;
        try
        {
            NXOpen.Point axisPoint = _part.Points.CreatePoint(SketchPoint(parts[1], ax, ay));
            NXOpen.Direction axisDir = _part.Directions.CreateDirection(
                axisPoint, SketchVectorForPlane(GetSketchPlaneName(parts[1]), vx, vy));
            axis = _part.Axes.CreateAxis(axisPoint, axisDir, SmartObject.UpdateOption.WithinModeling);
        }
        catch (Exception e) { return ErrJson("revolve[axis] failed: " + e.Message); }

        var b = _part.Features.CreateRevolveBuilder(null);
        Feature feature;
        try
        {
            b.Section = section;
            b.Axis = axis;
            b.Limits.StartExtend.Value.RightHandSide = "0";
            b.Limits.EndExtend.Value.RightHandSide = angle.ToString("0.###", CultureInfo.InvariantCulture);
            b.BooleanOperation.Type = BooleanOperation.BooleanType.Create;
            feature = b.CommitFeature();
        }
        catch (Exception e)
        {
            try { b.Destroy(); } catch { }
            return ErrJson("revolve[commit] failed: " + e.Message);
        }
        b.Destroy();

        Body[] bodies = _part.Bodies.ToArray();
        Body newBody = null;
        if (bodies.Length > 0) newBody = bodies[bodies.Length - 1];
        if (newBody != null)
        {
            _bodyCounter++;
            string bid = "BODY_" + _bodyCounter;
            _bodies[bid] = newBody;
            return OkJson("body_id", bid, "revolved " + angle + " deg");
        }
        return ErrJson("revolve produced no body");
    }

    private static string Mirror(string[] parts)
    {
        if (parts.Length < 3) return ErrJson("usage: nx_mirror <body_id> <XY|XZ|YZ> [offset]");
        Body body = GetBody(parts[1]);
        string planeName = parts[2].ToUpperInvariant();
        double offset = parts.Length > 3 ? D(parts[3]) : 0.0;

        Point3d origin;
        Vector3d mx, my, mz;
        switch (planeName)
        {
            case "XY":
                origin = new Point3d(0.0, 0.0, offset);
                mx = new Vector3d(1, 0, 0); my = new Vector3d(0, 1, 0); mz = new Vector3d(0, 0, 1);
                break;
            case "XZ":
                origin = new Point3d(0.0, offset, 0.0);
                mx = new Vector3d(0, 0, 1); my = new Vector3d(1, 0, 0); mz = new Vector3d(0, 1, 0);
                break;
            case "YZ":
                origin = new Point3d(offset, 0.0, 0.0);
                mx = new Vector3d(0, 1, 0); my = new Vector3d(0, 0, 1); mz = new Vector3d(1, 0, 0);
                break;
            default:
                return ErrJson("plane must be XY, XZ or YZ");
        }
        var m = new Matrix3x3();
        m.Xx = mx.X; m.Xy = mx.Y; m.Xz = mx.Z;
        m.Yx = my.X; m.Yy = my.Y; m.Yz = my.Z;
        m.Zx = mz.X; m.Zy = mz.Y; m.Zz = mz.Z;

        var before = new HashSet<Tag>();
        foreach (Body b in _part.Bodies) before.Add(b.Tag);

        DatumPlane datumPlane;
        var mb = _part.Features.CreateMirrorBodyBuilder(null);
        try
        {
            datumPlane = _part.Datums.CreateFixedDatumPlane(origin, m);
            mb.MirrorBodyList.SetArray(new Body[] { body });
            mb.Plane.SetValue(datumPlane, null, new Point3d(0.0, 0.0, 0.0));
            // [NX12 port] MirrorBodyBuilder.DeleteSourceBody does NOT exist in NX 12
            // (the member list in NXOpen.xml for NX 12 has no such property; it was
            // introduced in a later release). NX 12's Mirror Body keeps the source
            // body by default, which already matches the intended `false` semantics.
            // mb.DeleteSourceBody = false;
            mb.CommitFeature();
        }
        catch (Exception e)
        {
            try { mb.Destroy(); } catch { }
            return ErrJson("mirror failed: " + e.Message);
        }
        mb.Destroy();

        Body newBody = null;
        foreach (Body b in _part.Bodies)
        {
            if (!before.Contains(b.Tag)) { newBody = b; break; }
        }
        if (newBody != null)
        {
            _bodyCounter++;
            string bid = "BODY_" + _bodyCounter;
            _bodies[bid] = newBody;
            return OkJson("body_id", bid, "mirrored about " + planeName);
        }
        return ErrJson("mirror produced no new body");
    }

    private static string LinearPattern(string[] parts)
    {
        if (parts.Length < 5)
            return ErrJson("usage: nx_linear_pattern <body_id> <X|Y|Z> <count> <spacing> [reverse]");
        Body body = GetBody(parts[1]);
        string dirName = parts[2].ToUpperInvariant();
        int count;
        double spacing;
        if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out count))
            return ErrJson("count must be an integer >= 2");
        if (!double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out spacing))
            return ErrJson("bad spacing");
        bool reverse = parts.Length > 5 && parts[5] == "1";
        if (count < 2) return ErrJson("count must be >= 2");
        if (double.IsNaN(spacing) || double.IsInfinity(spacing) || spacing <= 0.0)
            return ErrJson("spacing must be > 0");

        Vector3d vec;
        switch (dirName)
        {
            case "X": vec = new Vector3d(1, 0, 0); break;
            case "Y": vec = new Vector3d(0, 1, 0); break;
            case "Z": vec = new Vector3d(0, 0, 1); break;
            default: return ErrJson("direction must be X, Y or Z");
        }

        var before = new HashSet<Tag>();
        foreach (Body b in _part.Bodies) before.Add(b.Tag);

        // Linear pattern via Move-Object copy: NumberOfCopies is the number of
        // NEW instances (excluding the seed), each translated by `spacing` along
        // the direction vector, so count/spacing/reverse semantics are exact and
        // every copy is an independent non-associative body.
        var mb = _part.BaseFeatures.CreateMoveObjectBuilder(null);
        try
        {
            try { mb.ObjectToMoveObject.SetArray(new NXObject[] { body }); }
            catch (Exception e) { throw new Exception("step Objects: " + e.Message, e); }
            try
            {
                mb.Associative = false;
                mb.MoveObjectResult = MoveObjectBuilder.MoveObjectResultOptions.CopyOriginal;
                mb.NumberOfCopies = count - 1;
            }
            catch (Exception e) { throw new Exception("step Options: " + e.Message, e); }

            try
            {
                Vector3d dirVec = reverse
                    ? new Vector3d(-vec.X, -vec.Y, -vec.Z)
                    : vec;
                Direction dirObj = _part.Directions.CreateDirection(
                    new Point3d(0.0, 0.0, 0.0), dirVec,
                    SmartObject.UpdateOption.WithinModeling);
                ModlMotion motion = mb.TransformMotion;
                motion.Option = ModlMotion.Options.Distance;
                motion.DistanceVector = dirObj;
                motion.DistanceValue.RightHandSide =
                    spacing.ToString("0.###", CultureInfo.InvariantCulture);
            }
            catch (Exception e) { throw new Exception("step Motion: " + e.Message, e); }

            try { mb.Commit(); }
            catch (Exception e) { throw new Exception("step Commit: " + e.Message, e); }
        }
        catch (Exception e)
        {
            try { mb.Destroy(); } catch { }
            return ErrJson("linear pattern failed: " + e.Message);
        }
        mb.Destroy();

        var newIds = new List<string>();
        foreach (Body b in _part.Bodies)
        {
            if (!before.Contains(b.Tag))
            {
                _bodyCounter++;
                string bid = "BODY_" + _bodyCounter;
                _bodies[bid] = b;
                newIds.Add(bid);
            }
        }
        if (newIds.Count == 0) return ErrJson("linear pattern produced no new bodies");
        return OkJsonList("body_ids", newIds,
            "linear pattern " + count + " along " + dirName + (reverse ? " (reverse)" : ""));
    }


    private static string CircularPattern(string[] parts)
    {
        if (parts.Length < 8) return ErrJson("usage: nx_circular_pattern <body_id> <X|Y|Z> <cx> <cy> <cz> <count> <angle> [reverse]");
        Body body = GetBody(parts[1]);
        string axisName = parts[2].ToUpperInvariant();
        double cx = D(parts[3]), cy = D(parts[4]), cz = D(parts[5]);
        int count; double totalAngle;
        if (!int.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out count) || count < 2)
            return ErrJson("count must be an integer >= 2");
        if (!double.TryParse(parts[7], NumberStyles.Float, CultureInfo.InvariantCulture, out totalAngle) ||
            double.IsNaN(totalAngle) || double.IsInfinity(totalAngle) || totalAngle <= 0 || totalAngle > 360)
            return ErrJson("angle must be in (0,360]");
        if (double.IsNaN(cx) || double.IsInfinity(cx) || double.IsNaN(cy) || double.IsInfinity(cy) || double.IsNaN(cz) || double.IsInfinity(cz))
            return ErrJson("center must be finite");
        bool reverse = parts.Length > 8 && parts[8] == "1";
        Vector3d vector;
        switch (axisName)
        {
            case "X": vector = new Vector3d(1,0,0); break;
            case "Y": vector = new Vector3d(0,1,0); break;
            case "Z": vector = new Vector3d(0,0,1); break;
            default: return ErrJson("axis must be X,Y or Z");
        }
        double step = Math.Abs(totalAngle - 360) < 1e-9 ? totalAngle/count : totalAngle/(count-1);
        HashSet<Tag> before = new HashSet<Tag>();
        foreach (Body oldBody in _part.Bodies) before.Add(oldBody.Tag);
        Point pivot = _part.Points.CreatePoint(new Point3d(cx,cy,cz));
        Direction direction = _part.Directions.CreateDirection(pivot,vector);
        Axis axis = _part.Axes.CreateAxis(pivot,direction,SmartObject.UpdateOption.WithinModeling);
        for (int i=1;i<count;i++)
        {
            MoveObjectBuilder builder = _part.BaseFeatures.CreateMoveObjectBuilder(null);
            try
            {
                builder.ObjectToMoveObject.SetArray(new NXObject[] { body });
                builder.Associative = false;
                builder.MoveObjectResult = MoveObjectBuilder.MoveObjectResultOptions.CopyOriginal;
                builder.NumberOfCopies = 1;
                ModlMotion motion = builder.TransformMotion;
                motion.Option = ModlMotion.Options.Angle;
                motion.AngularAxis = axis;
                motion.Angle.RightHandSide = ((reverse ? -1.0 : 1.0)*i*step).ToString("0.###############",CultureInfo.InvariantCulture);
                builder.Commit();
            }
            finally { builder.Destroy(); }
        }
        List<string> created = new List<string>();
        foreach (Body result in _part.Bodies)
        {
            if (before.Contains(result.Tag)) continue;
            string id = "BODY_" + (++_bodyCounter);
            _bodies[id] = result; created.Add(id);
        }
        if (created.Count != count-1) throw new Exception("circular pattern created " + created.Count + " bodies; expected " + (count-1));
        return OkJsonList("body_ids",created,"circular pattern " + count + " about " + axisName + " center " + cx + "," + cy + "," + cz + " sweep " + totalAngle + (reverse ? " (reverse)" : ""));
    }

    private static string Hole(string[] parts)
    {
        if (parts.Length < 6) return ErrJson("usage: nx_hole <body_id> cx cy diameter depth [start_offset]");
        Body body = GetBody(parts[1]);
        double cx = D(parts[2]), cy = D(parts[3]), dia = D(parts[4]), depth = D(parts[5]);
        double startOff = parts.Length > 6 ? D(parts[6]) : 0.0;

        _sketchCounter++;
        string id = "SKETCH_HOLE_" + _sketchCounter;
        var b = _part.Sketches.CreateSketchInPlaceBuilder2(null);
        Sketch sk;
        try
        {
            sk = (Sketch)b.Commit();
        }
        finally
        {
            b.Destroy();
        }
        try { sk.SetName(id); } catch { }
        try { sk.Activate(Sketch.ViewReorient.True); } catch { }
        _sketches[id] = sk;

        double r = dia / 2.0;
        var ell = _part.Curves.CreateEllipse(
            new Point3d(cx, cy, 0.0),
            new Vector3d(1.0, 0.0, 0.0), new Vector3d(0.0, 1.0, 0.0),
            r, r, 0.0, 2.0 * Math.PI);
        sk.AddGeometry(ell, Sketch.InferConstraintsOption.InferNoConstraints);
        try { sk.Deactivate(Sketch.ViewReorient.True, Sketch.UpdateLevel.Model); } catch { }

        ExtrudeSketchCore(sk, startOff, startOff + depth,
            BooleanOperation.BooleanType.Subtract, body);
        return OkJson("hole d=" + dia + " depth=" + depth + " at " + cx + "," + cy);
    }

    private static string CounterboreHole(string[] parts)
    {
        if (parts.Length < 8)
            return ErrJson("usage: nx_counterbore_hole <body_id> cx cy hole_dia hole_depth cb_dia cb_depth [start_offset]");
        Body body = GetBody(parts[1]);
        double cx = D(parts[2]), cy = D(parts[3]);
        double holeDia = D(parts[4]), holeDepth = D(parts[5]);
        double cbDia = D(parts[6]), cbDepth = D(parts[7]);
        double startOff = parts.Length > 8 ? D(parts[8]) : 0.0;
        double[] targetBox = new double[6];
        NXOpen.UF.UFSession.GetUFSession().Modl.AskBoundingBox(body.Tag,targetBox);
        double topZ = targetBox[5];

        if (double.IsNaN(holeDia) || double.IsInfinity(holeDia) || holeDia <= 0)
            return ErrJson("hole_diameter must be a positive finite number");
        if (double.IsNaN(holeDepth) || double.IsInfinity(holeDepth) || holeDepth <= 0)
            return ErrJson("hole_depth must be a positive finite number");
        if (double.IsNaN(cbDia) || double.IsInfinity(cbDia) || cbDia <= 0)
            return ErrJson("counterbore_diameter must be a positive finite number");
        if (double.IsNaN(cbDepth) || double.IsInfinity(cbDepth) || cbDepth <= 0)
            return ErrJson("counterbore_depth must be > 0");
        if (cbDia <= holeDia)
            return ErrJson("counterbore_diameter must be > hole_diameter");
        if (cbDepth >= holeDepth)
            return ErrJson("counterbore_depth must be < hole_depth");

        // 1) main through hole (smaller dia, deeper)
        double rHole = holeDia / 2.0;
        _sketchCounter++;
        var b1 = _part.Sketches.CreateSketchInPlaceBuilder2(null);
        Sketch sk1;
        try { sk1 = (Sketch)b1.Commit(); }
        finally { b1.Destroy(); }
        try { sk1.SetName("SKETCH_CB_HOLE_" + _sketchCounter); } catch { }
        try { sk1.Activate(Sketch.ViewReorient.True); } catch { }
        _sketches["SKETCH_CB_HOLE_" + _sketchCounter] = sk1;
        var ellHole = _part.Curves.CreateEllipse(
            new Point3d(cx, cy, 0.0),
            new Vector3d(1.0, 0.0, 0.0), new Vector3d(0.0, 1.0, 0.0),
            rHole, rHole, 0.0, 2.0 * Math.PI);
        sk1.AddGeometry(ellHole, Sketch.InferConstraintsOption.InferNoConstraints);
        try { sk1.Deactivate(Sketch.ViewReorient.True, Sketch.UpdateLevel.Model); } catch { }
        ExtrudeSketchCore(sk1, startOff, startOff + holeDepth,
            BooleanOperation.BooleanType.Subtract, body);

        // 2) counterbore (larger dia, shallower, coaxial, from top surface down cbDepth)
        double rCb = cbDia / 2.0;
        _sketchCounter++;
        var b2 = _part.Sketches.CreateSketchInPlaceBuilder2(null);
        Sketch sk2;
        try { sk2 = (Sketch)b2.Commit(); }
        finally { b2.Destroy(); }
        try { sk2.SetName("SKETCH_CB_" + _sketchCounter); } catch { }
        try { sk2.Activate(Sketch.ViewReorient.True); } catch { }
        _sketches["SKETCH_CB_" + _sketchCounter] = sk2;
        var ellCb = _part.Curves.CreateEllipse(
            new Point3d(cx, cy, 0.0),
            new Vector3d(1.0, 0.0, 0.0), new Vector3d(0.0, 1.0, 0.0),
            rCb, rCb, 0.0, 2.0 * Math.PI);
        sk2.AddGeometry(ellCb, Sketch.InferConstraintsOption.InferNoConstraints);
        try { sk2.Deactivate(Sketch.ViewReorient.True, Sketch.UpdateLevel.Model); } catch { }
        ExtrudeSketchCore(sk2, topZ - cbDepth, topZ,
            BooleanOperation.BooleanType.Subtract, body);

        return OkJson("counterbore hole d=" + holeDia + " depth=" + holeDepth
            + " cb_d=" + cbDia + " cb_depth=" + cbDepth + " at " + cx + "," + cy);
    }

    private static string CountersinkHole(string[] parts)
    {
        if (parts.Length < 8)
            return ErrJson("usage: nx_countersink_hole <body_id> cx cy hole_dia hole_depth cs_dia cs_angle [start_offset]");
        Body body = GetBody(parts[1]);
        double cx = D(parts[2]), cy = D(parts[3]);
        double holeDia = D(parts[4]), holeDepth = D(parts[5]);
        double csDia = D(parts[6]), csAngle = D(parts[7]);
        double startOff = parts.Length > 8 ? D(parts[8]) : 0.0;
        double[] targetBox = new double[6];
        NXOpen.UF.UFSession.GetUFSession().Modl.AskBoundingBox(body.Tag,targetBox);
        double topZ = targetBox[5];

        if (double.IsNaN(holeDia) || double.IsInfinity(holeDia) || holeDia <= 0)
            return ErrJson("hole_diameter must be a positive finite number");
        if (double.IsNaN(holeDepth) || double.IsInfinity(holeDepth) || holeDepth <= 0)
            return ErrJson("hole_depth must be a positive finite number");
        if (double.IsNaN(csDia) || double.IsInfinity(csDia) || csDia <= 0)
            return ErrJson("countersink_diameter must be a positive finite number");
        if (double.IsNaN(csAngle) || double.IsInfinity(csAngle) || csAngle <= 0.0 || csAngle >= 180.0)
            return ErrJson("countersink_angle must be in (0,180)");
        if (csDia <= holeDia)
            return ErrJson("countersink_diameter must be > hole_diameter");

        // countersink depth from included angle:
        // depth = ((cs_dia - hole_dia)/2) / tan(cs_angle/2)
        double csDepth = ((csDia - holeDia) / 2.0) / Math.Tan(csAngle / 2.0 * Math.PI / 180.0);
        if (csDepth >= holeDepth)
            return ErrJson("computed countersink depth must be < hole_depth");

        // 1) main through hole (smaller dia, deeper cylinder subtract)
        double rHole = holeDia / 2.0;
        _sketchCounter++;
        var b1 = _part.Sketches.CreateSketchInPlaceBuilder2(null);
        Sketch sk1;
        try { sk1 = (Sketch)b1.Commit(); }
        finally { b1.Destroy(); }
        try { sk1.SetName("SKETCH_CS_HOLE_" + _sketchCounter); } catch { }
        try { sk1.Activate(Sketch.ViewReorient.True); } catch { }
        _sketches["SKETCH_CS_HOLE_" + _sketchCounter] = sk1;
        var ellHole = _part.Curves.CreateEllipse(
            new Point3d(cx, cy, 0.0),
            new Vector3d(1.0, 0.0, 0.0), new Vector3d(0.0, 1.0, 0.0),
            rHole, rHole, 0.0, 2.0 * Math.PI);
        sk1.AddGeometry(ellHole, Sketch.InferConstraintsOption.InferNoConstraints);
        try { sk1.Deactivate(Sketch.ViewReorient.True, Sketch.UpdateLevel.Model); } catch { }
        ExtrudeSketchCore(sk1, startOff, startOff + holeDepth,
            BooleanOperation.BooleanType.Subtract, body);

        // 2) conical countersink (cone, large top dia -> small bottom dia, subtract)
        NXOpen.Point axisPt = _part.Points.CreatePoint(new Point3d(cx, cy, topZ));
        NXOpen.Direction axisDir = _part.Directions.CreateDirection(
            new Point3d(cx, cy, topZ), new Vector3d(0.0, 0.0, -1.0),
            SmartObject.UpdateOption.WithinModeling);
        Axis coneAxis = _part.Axes.CreateAxis(axisPt, axisDir, SmartObject.UpdateOption.WithinModeling);

        var cb = _part.Features.CreateConeBuilder(null);
        try
        {
            cb.Type = NXOpen.Features.ConeBuilder.Types.DiametersAndHeight;
            cb.Axis = coneAxis;
            cb.BaseDiameter.RightHandSide = csDia.ToString("0.###", CultureInfo.InvariantCulture);
            cb.TopDiameter.RightHandSide = holeDia.ToString("0.###", CultureInfo.InvariantCulture);
            cb.Height.RightHandSide = csDepth.ToString("0.###", CultureInfo.InvariantCulture);
            cb.BooleanOption.Type = BooleanOperation.BooleanType.Subtract;
            cb.BooleanOption.SetTargetBodies(new Body[] { body });
            cb.CommitFeature();
        }
        catch (Exception e)
        {
            try { cb.Destroy(); } catch { }
            return ErrJson("countersink cone failed: " + e.Message);
        }
        cb.Destroy();

        return OkJson("countersink hole d=" + holeDia + " depth=" + holeDepth
            + " cs_d=" + csDia + " angle=" + csAngle + " depth=" + csDepth.ToString("0.###")
            + " at " + cx + "," + cy);
    }

    private static string Shell(string[] parts)
    {
        if (parts.Length < 4)
            return ErrJson("usage: nx_shell <body_id> <thickness> <remove_face_index> [inward=1]");
        Body body = GetBody(parts[1]);
        double thickness = D(parts[2]);
        int removeIdx;
        if (!int.TryParse(parts[3], out removeIdx))
            return ErrJson("remove_face_index must be an integer");
        bool inward = parts.Length < 5 || parts[4] != "0";

        if (double.IsNaN(thickness) || double.IsInfinity(thickness) || thickness <= 0)
            return ErrJson("thickness must be > 0");
        Face[] faces = body.GetFaces();
        if (removeIdx < 0 || removeIdx >= faces.Length)
            return ErrJson("remove_face_index out of range (0.." + (faces.Length - 1) + ")");

        var coll = _part.ScCollectors.CreateCollector();
        var faceRule = _part.ScRuleFactory.CreateRuleFaceDumb(new Face[] { faces[removeIdx] });
        // [NX12 port] NX 12 ScCollector has no AddRules(). Use the functionally
        // equivalent ReplaceRules(rules, createRulesWoUpdate) instead.
        // ReplaceRules has existed since NX4.0.0, so it is safe on NX 12.
        coll.ReplaceRules(new SelectionIntentRule[] { faceRule }, false);

        var sb = _part.Features.CreateShellBuilder(null);
        try
        {
            sb.Body = body;
            sb.Tolerance = 0.0254;
            sb.DefaultThickness.RightHandSide = thickness.ToString("0.###", CultureInfo.InvariantCulture);
            sb.DefaultThicknessFlip = inward;
            sb.RemovedFacesCollector = coll;
            sb.CommitFeature();
        }
        catch (Exception e)
        {
            try { sb.Destroy(); } catch { }
            try { coll.Destroy(); } catch { }
            return ErrJson("shell failed: " + e.Message);
        }
        sb.Destroy();
        try { coll.Destroy(); } catch { }

        return OkJson("body_id", parts[1],
            "shell t=" + thickness + " face=" + removeIdx + " inward=" + inward);
    }

    // NX12 query helpers. No features, expressions, copies or temporary bodies.
    private static string GNum(double v)
    {
        return (double.IsNaN(v) || double.IsInfinity(v)) ? "null" : v.ToString("0.###############", CultureInfo.InvariantCulture);
    }
    private static string GVec(double[] v, double scale)
    {
        if (v == null || v.Length < 3) return "null";
        return "[" + GNum(v[0] * scale) + "," + GNum(v[1] * scale) + "," + GNum(v[2] * scale) + "]";
    }
    private static string GCurvatureRadius(double radius,double scale)
    {return Math.Abs(radius)<1e-12 || Math.Abs(radius)>1e15 ? "null" : GNum(radius*scale);}
    private static double GDot(double[] a, double[] b) { return a[0]*b[0]+a[1]*b[1]+a[2]*b[2]; }
    private static double[] GSub(double[] a, double[] b) { return new double[] {a[0]-b[0],a[1]-b[1],a[2]-b[2]}; }
    private static double GNorm(double[] a) { return Math.Sqrt(GDot(a,a)); }
    private static double[] GCross(double[] a, double[] b) { return new double[] {a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]}; }
    private static double[] GUnit(double[] a) { double n=GNorm(a); if(n<1e-14) throw new Exception("zero direction");return new double[]{a[0]/n,a[1]/n,a[2]/n}; }
    private static double GScale(NXOpen.UF.UFSession uf) { int units; uf.Part.AskUnits(_part.Tag,out units); return units==2?25.4:1.0; }
    private static Body GBody(string id)
    {
        try { return GetBody(id); } catch { }
        // Read queries may resolve an exact journal/display name after reopening.
        Body[] all=_part.Bodies.ToArray(); Body match=null;
        foreach(Body body in all)
        {
            if(body.JournalIdentifier==id || body.Name==id) { if(match!=null)throw new Exception("ambiguous body name");match=body; }
        }
        if(match!=null)return match;
        if(all.Length==1)return all[0];
        throw new Exception("body id not found; multiple bodies require an exact id");
    }
    private sealed class GEdge
    {
        public Edge Edge; public double[] Start,End,Center,XAxis,YAxis;
        public string Type,AnalyticType="unavailable",Error="";
        public double Length=double.NaN,Major=double.NaN,Minor=double.NaN;
        public bool FullPeriod=false;
    }
    private static GEdge GReadEdge(Edge edge,NXOpen.UF.UFSession uf)
    {
        GEdge g=new GEdge();g.Edge=edge;g.Type=edge.SolidEdgeType.ToString();
        Point3d s,en;edge.GetVertices(out s,out en);g.Start=new double[]{s.X,s.Y,s.Z};g.End=new double[]{en.X,en.Y,en.Z};
        try{g.Length=edge.GetLength();}catch(Exception ex){g.Error=ex.Message;}
        IntPtr eval=IntPtr.Zero;
        try
        {
            if(g.Type=="Circular" || g.Type=="Elliptical")
            {
                uf.Eval.Initialize2(edge.Tag,out eval);
                if(g.Type=="Circular")
                {
                    NXOpen.UF.UFEval.Arc a;uf.Eval.AskArc(eval,out a);
                    g.Center=a.center;g.XAxis=a.x_axis;g.YAxis=a.y_axis;g.Major=a.radius;g.Minor=a.radius;g.AnalyticType="circle";
                    g.FullPeriod=Math.Abs(Math.Abs(a.limits[1]-a.limits[0])-2*Math.PI)<1e-7;
                }
                else
                {
                    NXOpen.UF.UFEval.Ellipse a;uf.Eval.AskEllipse(eval,out a);
                    g.Center=a.center;g.XAxis=a.x_axis;g.YAxis=a.y_axis;g.Major=a.major;g.Minor=a.minor;
                    g.AnalyticType=Math.Abs(a.major-a.minor)<=1e-9*Math.Max(1.0,Math.Abs(a.major))?"circle":"ellipse";
                    g.FullPeriod=Math.Abs(Math.Abs(a.limits[1]-a.limits[0])-2*Math.PI)<1e-7;
                }
            }
        }
        catch(Exception ex){g.Error+=(g.Error.Length==0?"":"; ")+"analytic curve: "+ex.Message;}
        finally{if(eval!=IntPtr.Zero)uf.Eval.Free(eval);}
        return g;
    }
    private static string GBounds(NXOpen.Tag tag,NXOpen.UF.UFSession uf,double scale)
    {
        double[] b=new double[6];uf.Modl.AskBoundingBox(tag,b);
        return "\"bbox_min\":"+GVec(new double[]{b[0],b[1],b[2]},scale)+",\"bbox_max\":"+GVec(new double[]{b[3],b[4],b[5]},scale)+",\"bbox_method\":\"UF_MODL_ask_bounding_box_absolute_enclosure\"";
    }
    private static string GBodyProperties(Body body,NXOpen.UF.UFSession uf,double scale)
    {
        try
        {
            double[] values=new double[47],stats=new double[13],accuracy=new double[11];accuracy[0]=0.999999;
            // UF units=4: kg/meters. Only a BODY tag is supplied, never face tags.
            uf.Modl.AskMassProps3d(new NXOpen.Tag[]{body.Tag},1,body.IsSolidBody?1:2,4,1.0,1,accuracy,values,stats);
            if(values[0]<=0 || double.IsNaN(values[0]))throw new Exception("mass properties returned no positive area");
            double[] nativeWcs=new double[]{values[3]*1000/scale,values[4]*1000/scale,values[5]*1000/scale};
            double[] nativeAbs=new double[3];uf.Csys.MapPoint(3,nativeWcs,1,nativeAbs);
            return "{\"status\":\"measured\",\"method\":\"UF_MODL_ask_mass_props_3d\",\"coordinate_system\":\"ABS\",\"surface_area\":"+GNum(values[0]*1e6)+",\"volume\":"+GNum(values[1]*1e9)+",\"centroid\":"+GVec(nativeAbs,scale)+",\"centroid_kind\":\""+(body.IsSolidBody?"uniform_body_volume_centroid":"uniform_sheet_area_centroid")+"\",\"area_unit\":\"mm2\",\"volume_unit\":\"mm3\",\"length_unit\":\"mm\","+GBounds(body.Tag,uf,scale)+"}";
        }
        catch(Exception ex){return "{\"status\":\"unavailable\",\"surface_area\":null,\"volume\":null,\"centroid\":null,\"error\":\""+Esc(ex.Message)+"\"}";}
    }
    private sealed class GLoop
    {
        public double Area,Cx,Cy;public List<double[]> Polygon=new List<double[]>();
    }
    private static double[] GProject(double[] p,double[] origin,double[] u,double[] v)
    {double[] d=GSub(p,origin);return new double[]{GDot(d,u),GDot(d,v)};}
    private static bool GInside(double[] p,List<double[]> polygon)
    {
        bool inside=false;
        for(int i=0,j=polygon.Count-1;i<polygon.Count;j=i++)
        {double[] a=polygon[i],b=polygon[j];if((a[1]>p[1])!=(b[1]>p[1]) && p[0]<(b[0]-a[0])*(p[1]-a[1])/(b[1]-a[1])+a[0])inside=!inside;}
        return inside;
    }
    private static double[] GPlanarCentroid(List<GEdge> all,double[] origin,double[] normal,double areaNative,out string method)
    {
        method="unsupported_boundary";
        double[] n=GUnit(normal),axis=Math.Abs(n[0])<0.8?new double[]{1,0,0}:new double[]{0,1,0};
        double[] u=GUnit(GCross(axis,n)),v=GCross(n,u);
        List<GLoop> loops=new List<GLoop>();List<GEdge> lines=new List<GEdge>();
        foreach(GEdge edge in all)
        {
            if(edge.Type=="Linear"){lines.Add(edge);continue;}
            if(!edge.FullPeriod || edge.Center==null || edge.Major<=0 || edge.Minor<=0)return null;
            if(Math.Abs(Math.Abs(GDot(GUnit(GCross(edge.XAxis,edge.YAxis)),n))-1)>1e-7)return null;
            GLoop loop=new GLoop();loop.Area=Math.PI*edge.Major*edge.Minor;
            double[] center=GProject(edge.Center,origin,u,v);loop.Cx=center[0];loop.Cy=center[1];
            for(int j=0;j<128;j++)
            {
                double angle=2*Math.PI*j/128.0;double[] p=new double[3];
                for(int k=0;k<3;k++)p[k]=edge.Center[k]+edge.Major*Math.Cos(angle)*edge.XAxis[k]+edge.Minor*Math.Sin(angle)*edge.YAxis[k];
                loop.Polygon.Add(GProject(p,origin,u,v));
            }
            loops.Add(loop);
        }
        while(lines.Count>0)
        {
            GEdge first=lines[0];lines.RemoveAt(0);List<double[]> vertices=new List<double[]>();vertices.Add(first.Start);double[] last=first.End;
            int safety=0;
            while(GNorm(GSub(last,first.Start))>1e-6)
            {
                vertices.Add(last);int found=-1;bool reverse=false;
                for(int j=0;j<lines.Count;j++)
                {
                    if(GNorm(GSub(lines[j].Start,last))<=1e-6){if(found!=-1)return null;found=j;reverse=false;}
                    else if(GNorm(GSub(lines[j].End,last))<=1e-6){if(found!=-1)return null;found=j;reverse=true;}
                }
                if(found<0 || ++safety>all.Count)return null;
                GEdge next=lines[found];lines.RemoveAt(found);last=reverse?next.Start:next.End;
            }
            if(vertices.Count<3)return null;
            GLoop loop=new GLoop();foreach(double[] p in vertices)loop.Polygon.Add(GProject(p,origin,u,v));
            double twiceArea=0,cx=0,cy=0;
            for(int j=0;j<loop.Polygon.Count;j++)
            {
                double[] a=loop.Polygon[j],b=loop.Polygon[(j+1)%loop.Polygon.Count];double cross=a[0]*b[1]-b[0]*a[1];
                twiceArea+=cross;cx+=(a[0]+b[0])*cross;cy+=(a[1]+b[1])*cross;
            }
            if(Math.Abs(twiceArea)<1e-14)return null;
            loop.Area=Math.Abs(twiceArea)/2;loop.Cx=cx/(3*twiceArea);loop.Cy=cy/(3*twiceArea);loops.Add(loop);
        }
        if(loops.Count==0)return null;
        loops.Sort(delegate(GLoop a,GLoop b){return b.Area.CompareTo(a.Area);});
        GLoop outer=loops[0];double area=outer.Area,mx=outer.Area*outer.Cx,my=outer.Area*outer.Cy;
        for(int j=1;j<loops.Count;j++)
        {
            // Supported contract: one outer loop with disjoint simple holes.
            foreach(double[] p in loops[j].Polygon)if(!GInside(p,outer.Polygon))return null;
            for(int k=1;k<loops.Count;k++)if(k!=j && GInside(new double[]{loops[j].Cx,loops[j].Cy},loops[k].Polygon))return null;
            area-=loops[j].Area;mx-=loops[j].Area*loops[j].Cx;my-=loops[j].Area*loops[j].Cy;
        }
        if(area<=0 || Math.Abs(area-areaNative)>Math.Max(1e-7,areaNative*1e-7)){method="boundary_area_mismatch";return null;}
        method="planar_line_polygon_full_ellipse_area_moments_validated_against_face_area";
        double[] result=new double[3];for(int k=0;k<3;k++)result[k]=origin[k]+u[k]*mx/area+v[k]*my/area;
        return result;
    }
    private static double[] GSweepCentroid(List<GEdge> edges,out string method)
    {
        method="unsupported_trimmed_curved_face";
        if(edges.Count!=2)return null;GEdge a=edges[0],b=edges[1];
        if(!a.FullPeriod || !b.FullPeriod || a.Center==null || b.Center==null)return null;
        if(Math.Abs(a.Major-b.Major)>1e-7 || Math.Abs(a.Minor-b.Minor)>1e-7)return null;
        if(Math.Abs(a.Major-a.Minor)>1e-7 && Math.Abs(Math.Abs(GDot(a.XAxis,b.XAxis))-1)>1e-7)return null;
        double[] normal=GUnit(GCross(a.XAxis,a.YAxis)),normal2=GUnit(GCross(b.XAxis,b.YAxis)),delta=GSub(b.Center,a.Center);
        if(Math.Abs(Math.Abs(GDot(normal,normal2))-1)>1e-7 || GNorm(delta)<1e-9)return null;
        if(GNorm(GCross(GUnit(delta),normal))>1e-7)return null;
        method="full_period_coaxial_equal_ellipse_rim_symmetry";
        return new double[]{(a.Center[0]+b.Center[0])/2,(a.Center[1]+b.Center[1])/2,(a.Center[2]+b.Center[2])/2};
    }
    private static string ListEdges(string[] parts)
    {
        if(parts.Length<2)return ErrJson("usage: nx_list_edges <body_id>");
        Body body=GBody(parts[1]);NXOpen.UF.UFSession uf=NXOpen.UF.UFSession.GetUFSession();double scale=GScale(uf);
        Edge[] edges=body.GetEdges();StringBuilder sb=new StringBuilder();
        sb.Append("{\"ok\":true,\"body_id\":\"").Append(Esc(parts[1])).Append("\",\"length_unit\":\"mm\",\"coordinate_system\":\"ABS\",\"edge_count\":").Append(edges.Length).Append(",\"edges\":[");
        for(int i=0;i<edges.Length;i++)
        {
            GEdge g=GReadEdge(edges[i],uf);if(i>0)sb.Append(',');
            string bounds="\"bbox_min\":null,\"bbox_max\":null";
            try{bounds=GBounds(edges[i].Tag,uf,scale);}catch(Exception ex){g.Error+="; bbox: "+ex.Message;}
            string direction="OTHER";double[] d=GSub(g.End,g.Start);
            if(g.Type=="Linear"){if(Math.Abs(d[0])>1e-8 && Math.Abs(d[1])<1e-8 && Math.Abs(d[2])<1e-8)direction="X";else if(Math.Abs(d[1])>1e-8 && Math.Abs(d[0])<1e-8 && Math.Abs(d[2])<1e-8)direction="Y";else if(Math.Abs(d[2])>1e-8 && Math.Abs(d[0])<1e-8 && Math.Abs(d[1])<1e-8)direction="Z";}
            double[] mid=new double[]{(g.Start[0]+g.End[0])/2,(g.Start[1]+g.End[1])/2,(g.Start[2]+g.End[2])/2};
            sb.Append("{\"index\":").Append(i).Append(",\"tag\":").Append(edges[i].Tag.ToString())
              .Append(",\"curve_type\":\"").Append(g.Type).Append("\",\"start\":").Append(GVec(g.Start,scale)).Append(",\"end\":").Append(GVec(g.End,scale))
              .Append(",\"midpoint\":").Append(GVec(mid,scale)).Append(",\"midpoint_kind\":\"endpoint_average_not_curve_center\",\"length\":").Append(GNum(g.Length*scale)).Append(',').Append(bounds)
              .Append(",\"direction\":\"").Append(direction).Append("\",\"adjacent_faces\":").Append(edges[i].GetFaces().Length)
              .Append(",\"analytic_type\":\"").Append(g.AnalyticType).Append("\",\"center\":").Append(GVec(g.Center,scale))
              .Append(",\"radius\":").Append(g.AnalyticType=="circle"?GNum(g.Major*scale):"null")
              .Append(",\"major_radius\":").Append(GNum(g.Major*scale)).Append(",\"minor_radius\":").Append(GNum(g.Minor*scale))
              .Append(",\"full_period\":").Append(g.FullPeriod?"true":"false").Append(",\"err\":\"").Append(Esc(g.Error)).Append("\"}");
        }
        sb.Append("],\"body_properties\":").Append(GBodyProperties(body,uf,scale)).Append('}');return sb.ToString();
    }
    private static string ListFaces(string[] parts)
    {
        if(parts.Length<2)return ErrJson("usage: nx_list_faces <body_id>");
        Body body=GBody(parts[1]);NXOpen.UF.UFSession uf=NXOpen.UF.UFSession.GetUFSession();double scale=GScale(uf);
        Face[] faces=body.GetFaces();StringBuilder sb=new StringBuilder();
        sb.Append("{\"ok\":true,\"body_id\":\"").Append(Esc(parts[1])).Append("\",\"coordinate_system\":\"ABS\",\"length_unit\":\"mm\",\"area_unit\":\"mm2\",\"face_count\":").Append(faces.Length).Append(",\"faces\":[");
        for(int i=0;i<faces.Length;i++)
        {
            Face face=faces[i];if(i>0)sb.Append(',');
            string err="",areaStatus="unavailable",centroidMethod="unavailable",normalStatus="unavailable";
            double area=double.NaN,perimeter=double.NaN,majorRadius=double.NaN,minorRadius=double.NaN;int faceCode=-1,normDir=0;
            double[] point=new double[3],axis=new double[3],box=new double[6],normal=null,samplePoint=null,centroid=null,radii=null,uvSample=null;
            string type=face.SolidFaceType.ToString();
            MeasureFaces measure=null;
            try
            {
                Unit au=_part.UnitCollection.FindObject("SquareMilliMeter"),lu=_part.UnitCollection.FindObject("MilliMeter");
                measure=_part.MeasureManager.NewFaceProperties(au,lu,0.999999,new IParameterizedSurface[]{face});
                area=measure.Area;perimeter=measure.Perimeter;if(area<=0)throw new Exception("nonpositive face area");areaStatus="measured";
            }
            catch(Exception ex){area=double.NaN;perimeter=double.NaN;err="area: "+ex.Message;}
            finally{if(measure!=null)measure.Dispose();}
            try{uf.Modl.AskFaceData(face.Tag,out faceCode,point,axis,box,out majorRadius,out minorRadius,out normDir);}catch(Exception ex){point=null;axis=null;err+="; surface: "+ex.Message;}
            try
            {
                double[] limits=new double[4];uf.Modl.AskFaceUvMinmax(face.Tag,limits);
                uvSample=new double[]{(limits[0]+limits[1])/2,(limits[2]+limits[3])/2};samplePoint=new double[3];normal=new double[3];radii=new double[2];
                uf.Modl.AskFaceProps(face.Tag,uvSample,samplePoint,new double[3],new double[3],new double[3],new double[3],normal,radii);
                normal=GUnit(normal);normalStatus=faceCode==22?"global_planar_unit_normal":"underlying_surface_sample_unit_normal";
            }
            catch(Exception ex){normal=null;samplePoint=null;radii=null;err+="; normal: "+ex.Message;}
            try
            {
                List<GEdge> edgeData=new List<GEdge>();foreach(Edge edge in face.GetEdges())edgeData.Add(GReadEdge(edge,uf));
                if(faceCode==22 && areaStatus=="measured" && normal!=null)centroid=GPlanarCentroid(edgeData,point,normal,area/(scale*scale),out centroidMethod);
                else if(faceCode==16 || faceCode==20)centroid=GSweepCentroid(edgeData,out centroidMethod);
                else centroidMethod="unsupported_trimmed_curved_face";
            }
            catch(Exception ex){centroid=null;centroidMethod="unavailable";err+="; centroid: "+ex.Message;}
            string radiusJson=(faceCode==16||faceCode==17||faceCode==18||faceCode==19)?GNum(majorRadius*scale):"null";
            string bounds="\"bbox_min\":null,\"bbox_max\":null";try{bounds=GBounds(face.Tag,uf,scale);}catch(Exception ex){err+="; bbox: "+ex.Message;}
            sb.Append("{\"index\":").Append(i).Append(",\"tag\":").Append(face.Tag.ToString()).Append(",\"face_type\":\"").Append(type).Append("\",\"surface_type_code\":").Append(faceCode)
              .Append(",\"centroid\":").Append(GVec(centroid,scale)).Append(",\"centroid_status\":\"").Append(centroid==null?"unavailable":"measured").Append("\",\"centroid_method\":\"").Append(centroidMethod)
              .Append("\",\"area\":").Append(GNum(area)).Append(",\"perimeter\":").Append(GNum(perimeter)).Append(",\"area_status\":\"").Append(areaStatus).Append("\",\"area_method\":\"MeasureManager.NewFaceProperties\"")
              .Append(",\"normal\":").Append(faceCode==22?GVec(normal,1):"null").Append(",\"normal_status\":\"").Append(normalStatus).Append("\",\"sample_normal\":").Append(GVec(normal,1)).Append(",\"normal_sample_point\":").Append(GVec(samplePoint,scale))
              .Append(",\"normal_sample_scope\":\"underlying_surface_UV_midpoint_may_be_outside_trim\",\"radius\":").Append(radiusJson)
              .Append(",\"radius_status\":\"").Append(radiusJson=="null"?"not_defined_for_surface_type":"analytic_surface_radius").Append("\",\"curvature_radii_sample\":")
              .Append(faceCode==22 || radii==null?"null":"["+GCurvatureRadius(radii[0],scale)+","+GCurvatureRadius(radii[1],scale)+"]")
              .Append(",\"curvature_radii_status\":\"").Append(faceCode==22?"not_defined_planar_zero_curvature":radii==null?"unavailable":"sampled_underlying_surface_principal_radii").Append("\"")
              .Append(",\"surface_reference_point\":").Append((faceCode==16||faceCode==17||faceCode==18||faceCode==19||faceCode==22)?GVec(point,scale):"null").Append(",\"surface_reference_point_is_centroid\":false,\"surface_axis\":").Append((faceCode==16||faceCode==17||faceCode==19)?GVec(axis,1):"null")
              .Append(',').Append(bounds).Append(",\"adjacent_edges\":").Append(face.GetEdges().Length).Append(",\"err\":\"").Append(Esc(err.TrimStart(';',' '))).Append("\"}");
        }
        sb.Append("],\"body_properties\":").Append(GBodyProperties(body,uf,scale)).Append('}');return sb.ToString();
    }

    private static string F(double v)
    {
        return v.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string EdgeBlend(string[] parts)
    {
        if (parts.Length < 3) return ErrJson("usage: nx_edge_blend <body_id> <radius> [edge_indices csv]");
        Body body = GetBody(parts[1]);
        double r = D(parts[2]);
        int[] idx = parts.Length > 3 ? ParseIndices(parts[3]) : null;
        int done = BlendChamfer(body, r, idx, true);
        return OkJson("edge_blend done=" + done);
    }

    private static string Chamfer(string[] parts)
    {
        if (parts.Length < 3) return ErrJson("usage: nx_chamfer <body_id> <offset> [edge_indices csv]");
        Body body = GetBody(parts[1]);
        double off = D(parts[2]);
        int[] idx = parts.Length > 3 ? ParseIndices(parts[3]) : null;
        int done = BlendChamfer(body, off, idx, false);
        return OkJson("chamfer done=" + done);
    }

    private static string Unite(string[] parts)
    {
        if (parts.Length < 3) return ErrJson("usage: nx_unite <target_body_id> <tool_body_ids csv>");
        Body target;
        try { target = GetBody(parts[1]); }
        catch (Exception e) { return ErrJson(e.Message); }
        List<Body> tools = new List<Body>();
        string[] ids = parts[2].Split(',');
        foreach (string id in ids)
        {
            string t = id.Trim();
            if (t.Length == 0) continue;
            try { tools.Add(GetBody(t)); }
            catch (Exception e) { return ErrJson(e.Message); }
        }
        if (tools.Count == 0) return ErrJson("no tool bodies given");
        try
        {
            if (_session == null) _session = Session.GetSession();
            NXOpen.Features.BooleanBuilder bld = null;
            try { bld = _part.Features.CreateBooleanBuilder(null); }
            catch (Exception e) { return ErrJson("unite[create] failed: " + e.Message); }
            try { bld.Operation = NXOpen.Features.Feature.BooleanType.Unite; }
            catch (Exception e) { return ErrJson("unite[op] failed: " + e.Message); }
            try { bld.Target = target; }
            catch (Exception e) { return ErrJson("unite[target] failed: " + e.Message); }
            NXOpen.DisplayableObject[] objs = new NXOpen.DisplayableObject[tools.Count];
            for (int i = 0; i < tools.Count; i++) objs[i] = tools[i];
            try { bld.Tools.Add(objs); }
            catch (Exception e) { return ErrJson("unite[tools] failed: " + e.Message); }
            NXOpen.NXObject obj = null;
            try { obj = bld.Commit(); }
            catch (Exception e) { return ErrJson("unite[commit] failed: " + e.Message); }
            try { bld.Destroy(); } catch { }
            // tool bodies are consumed by the boolean; drop their stale ids
            foreach (string id in ids)
            {
                string t = id.Trim();
                if (t.Length > 0) _bodies.Remove(t);
            }
            return OkJson("body_id", parts[1], "united " + tools.Count + " tool body(ies)");
        }
        catch (Exception e)
        {
            return ErrJson("unite failed: " + e.Message);
        }
    }

    private static string FitView()
    {
        if (_session == null) _session = Session.GetSession();
        try { _session.Parts.Work.Views.WorkView.Fit(); } catch { }
        return OkJson("view fitted");
    }

    private static string ExportStep(string[] parts)
    {
        if (parts.Length < 2) return ErrJson("usage: nx_export_step <path>");
        if (_part == null) return ErrJson("no active part");
        string stepPath = parts[1];
        if (File.Exists(stepPath) || Directory.Exists(stepPath))
            return ErrJson("refusing to overwrite existing STEP: " + stepPath);
        if (_session == null) _session = Session.GetSession();
        var creator = _session.DexManager.CreateStepCreator();
        try
        {
            // NX12 DexBuilder: hold Commit until the translator has completed.
            creator.ProcessHoldFlag = true;
            creator.OutputFile = stepPath;
            creator.InputFile = _partPath;
            creator.ObjectTypes.Solids = true;
            creator.ExportAs = StepCreator.ExportAsOption.Ap214;
            string baseDir = Environment.GetEnvironmentVariable("UGII_BASE_DIR");
            if (!string.IsNullOrEmpty(baseDir))
            {
                string settings = Path.Combine(baseDir, "STEP214UG", "ugstep214.def");
                if (File.Exists(settings)) creator.SettingsFile = settings;
            }
            creator.Commit();
        }
        finally
        {
            try { creator.Destroy(); } catch { }
        }
        if (!File.Exists(stepPath) || new FileInfo(stepPath).Length < 13)
            return ErrJson("STEP translator completed without a nonempty output; inspect translator log before retrying: " + stepPath);
        byte[] magic = new byte[13];
        using (FileStream input = new FileStream(stepPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            if (input.Read(magic, 0, magic.Length) != magic.Length || Encoding.ASCII.GetString(magic) != "ISO-10303-21;")
                return ErrJson("exported file has no STEP header; file preserved for diagnosis: " + stepPath);
        return OkJson("step", stepPath, "exported AP214; translator completed; STEP header verified");
    }

    // ---- composite acceptance part ------------------------------------
    private static string BuildPlateTest()
    {
        string ws = Environment.GetEnvironmentVariable("NX_MCP_WORKSPACE") ?? "";
        if (string.IsNullOrEmpty(ws))
            ws = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "NX_MCP_WORKSPACE");
        string prtPath = Path.Combine(ws, "loader_plate_demo.prt");
        string stepPath = Path.Combine(ws, "loader_plate_demo.step");

        // 1. part
        string cr = CreatePart(new string[] { "nx_create_part", prtPath });
        if (cr.Contains("\"ok\":false")) return cr;

        // 2. base plate 120x80x12
        string skB = CreateSketch(new string[] { "nx_create_sketch", "XY" });
        string skBId = ExtractId(skB, "sketch_id");
        SketchRectangle(new string[] { "nx_sketch_rectangle", skBId, "0", "0", "120", "80" });
        FinishSketch(new string[] { "nx_finish_sketch", skBId });
        string exB = Extrude(new string[] { "nx_extrude", skBId, "12" });
        if (exB.Contains("\"ok\":false")) return exB;
        string bodyId = ExtractId(exB, "body_id");

        // 3. boss 30x20 (circle dia 30, extrude 20 starting at z=12, unite)
        string skT = CreateSketch(new string[] { "nx_create_sketch", "XY" });
        string skTId = ExtractId(skT, "sketch_id");
        SketchCircle(new string[] { "nx_sketch_circle", skTId, "0", "0", "30" });
        FinishSketch(new string[] { "nx_finish_sketch", skTId });
        string exT = Extrude(new string[] { "nx_extrude", skTId, "20", "0", "12", "create", bodyId });
        if (exT.Contains("\"ok\":false")) return exT;
        string bodyId2 = ExtractId(exT, "body_id");

        // 4. center hole 12 through (32)
        string skH1 = CreateSketch(new string[] { "nx_create_sketch", "XY" });
        string skH1Id = ExtractId(skH1, "sketch_id");
        SketchCircle(new string[] { "nx_sketch_circle", skH1Id, "0", "0", "12" });
        FinishSketch(new string[] { "nx_finish_sketch", skH1Id });
        string exH1 = Extrude(new string[] { "nx_extrude", skH1Id, "32", "0", "0", "subtract", bodyId2 });
        if (exH1.Contains("\"ok\":false")) return exH1;

        // 5. two 10 holes at (+-40, 0) through plate (12)
        string skH2 = CreateSketch(new string[] { "nx_create_sketch", "XY" });
        string skH2Id = ExtractId(skH2, "sketch_id");
        SketchCircle(new string[] { "nx_sketch_circle", skH2Id, "-40", "0", "10" });
        SketchCircle(new string[] { "nx_sketch_circle", skH2Id, "40", "0", "10" });
        FinishSketch(new string[] { "nx_finish_sketch", skH2Id });
        string exH2 = Extrude(new string[] { "nx_extrude", skH2Id, "12", "0", "0", "subtract", bodyId2 });
        if (exH2.Contains("\"ok\":false")) return exH2;

        // 6. slot 40x12: middle rect 28x12 + two 12 circles at (+-14,0)
        string skS = CreateSketch(new string[] { "nx_create_sketch", "XY" });
        string skSId = ExtractId(skS, "sketch_id");
        SketchRectangle(new string[] { "nx_sketch_rectangle", skSId, "0", "0", "28", "12" });
        SketchCircle(new string[] { "nx_sketch_circle", skSId, "-14", "0", "12" });
        SketchCircle(new string[] { "nx_sketch_circle", skSId, "14", "0", "12" });
        FinishSketch(new string[] { "nx_finish_sketch", skSId });
        string exS = Extrude(new string[] { "nx_extrude", skSId, "12", "0", "0", "subtract", bodyId2 });
        if (exS.Contains("\"ok\":false")) return exS;

        // 7. blend R8 (tolerant all edges), 8. blend R3, 9. chamfer C2
        int b8 = 0, b3 = 0, c2 = 0;
        try { b8 = BlendChamfer(_bodies[bodyId2], 8.0, null, true); } catch { }
        try { b3 = BlendChamfer(_bodies[bodyId2], 3.0, null, true); } catch { }
        try { c2 = BlendChamfer(_bodies[bodyId2], 2.0, null, false); } catch { }

        // 10. save + export
        SavePart();
        string exStep = ExportStep(new string[] { "nx_export_step", stepPath });

        return "{\"ok\":true,\"result\":\"plate test built\"," +
               "\"part\":\"" + Esc(prtPath) + "\"," +
               "\"step\":\"" + Esc(stepPath) + "\"," +
               "\"blend8\":" + b8 + ",\"blend3\":" + b3 + ",\"chamfer2\":" + c2 + "}";
    }

    // ---- helpers -------------------------------------------------------
    private static string SketchNativeName(Sketch sketch)
    {
        // Feature.Name is the persisted navigator name; Sketch.Name can expose
        // a different, system-normalized sketch-entity name in NX12.
        try { NXOpen.Features.Feature feature=sketch.Feature; if(feature!=null&&!string.IsNullOrEmpty(feature.Name))return feature.Name; } catch { }
        try { return sketch.Name ?? ""; } catch { return ""; }
    }

    private static Sketch GetSketch(string id)
    {
        Sketch sk;
        if (!_sketches.TryGetValue(id, out sk))
        {
            // Listing an explicitly named sketch yields that native name.
            // Resolve only sketches already owned by this current task cache;
            // do not guess a principal plane for arbitrary reopened sketches.
            foreach(Sketch candidate in _sketches.Values)
            {
                if(SketchNativeName(candidate) == id || candidate.Name == id || candidate.JournalIdentifier == id)
                {
                    if(sk != null && sk.Tag != candidate.Tag)throw new Exception("ambiguous sketch selector: " + id);
                    sk=candidate;
                }
            }
            if(sk == null)throw new Exception("unknown or expired task-local sketch_id: " + id);
        }
        if (_part == null || sk.OwningPart.Tag != _part.Tag) throw new Exception("sketch belongs to another part: " + id);
        return sk;
    }

    private static Body GetBody(string id)
    {
        Body b;
        if (!_bodies.TryGetValue(id, out b)) throw new Exception("unknown body_id: " + id);
        if (_part == null || b.OwningPart.Tag != _part.Tag) throw new Exception("body belongs to another part: " + id);
        return b;
    }

    private static double D(string s)
    {
        return double.Parse(s, CultureInfo.InvariantCulture);
    }

    private static int[] ParseIndices(string csv)
    {
        string[] t = csv.Split(',');
        int[] r = new int[t.Length];
        for (int i = 0; i < t.Length; i++) r[i] = int.Parse(t[i], CultureInfo.InvariantCulture);
        return r;
    }

    private static string ExtractId(string json, string key)
    {
        string needle = "\"" + key + "\":\"";
        int i = json.IndexOf(needle, StringComparison.Ordinal);
        if (i < 0) throw new Exception("missing " + key + " in " + json);
        int s = i + needle.Length;
        int e = json.IndexOf('"', s);
        return json.Substring(s, e - s);
    }

    private static string GetSketchPlaneName(string id)
    {
        string plane;
        if (_sketchPlanes.TryGetValue(id, out plane)) return plane;
        Sketch resolved=GetSketch(id);
        foreach(KeyValuePair<string,Sketch> entry in _sketches)
            if(entry.Value.Tag==resolved.Tag && _sketchPlanes.TryGetValue(entry.Key,out plane))return plane;
        throw new Exception("sketch plane is not registered for this task; no XY fallback permitted");
    }

    private static Point3d SketchPoint(string sketchId, double u, double v)
    {
        return SketchPointForPlane(GetSketchPlaneName(sketchId), u, v);
    }

    private static Point3d SketchPointForPlane(string plane, double u, double v)
    {
        switch (plane)
        {
            case "XZ": return new Point3d(u, 0.0, v);
            case "YZ": return new Point3d(0.0, u, v);
            default: return new Point3d(u, v, 0.0);
        }
    }

    private static Vector3d SketchVectorForPlane(string plane, double u, double v)
    {
        switch (plane)
        {
            case "XZ": return new Vector3d(u, 0.0, v);
            case "YZ": return new Vector3d(0.0, u, v);
            default: return new Vector3d(u, v, 0.0);
        }
    }

    private static Vector3d SketchPlaneNormal(string plane)
    {
        // Normals are right-handed with the local (u,v) mapping above.
        switch (plane)
        {
            case "XZ": return new Vector3d(0.0, -1.0, 0.0);
            case "YZ": return new Vector3d(1.0, 0.0, 0.0);
            default: return new Vector3d(0.0, 0.0, 1.0);
        }
    }

    private static Vector3d SketchLocalXAxis(string plane)
    {
        switch (plane)
        {
            case "YZ": return new Vector3d(0.0, 1.0, 0.0);
            default: return new Vector3d(1.0, 0.0, 0.0);
        }
    }

    private static Matrix3x3 SketchPlaneMatrix(string plane)
    {
        Matrix3x3 m = new Matrix3x3();
        if (plane == "XZ")
        {
            // local x -> +X, local y -> +Z, local normal -> -Y
            m.Xx = 1.0; m.Xy = 0.0;  m.Xz = 0.0;
            m.Yx = 0.0; m.Yy = 0.0;  m.Yz = 1.0;
            m.Zx = 0.0; m.Zy = -1.0; m.Zz = 0.0;
        }
        else if (plane == "YZ")
        {
            // local x -> +Y, local y -> +Z, local normal -> +X
            m.Xx = 0.0; m.Xy = 1.0; m.Xz = 0.0;
            m.Yx = 0.0; m.Yy = 0.0; m.Yz = 1.0;
            m.Zx = 1.0; m.Zy = 0.0; m.Zz = 0.0;
        }
        else
        {
            m.Xx = 1.0; m.Xy = 0.0; m.Xz = 0.0;
            m.Yx = 0.0; m.Yy = 1.0; m.Yz = 0.0;
            m.Zx = 0.0; m.Zy = 0.0; m.Zz = 1.0;
        }
        return m;
    }

    private static Vector3d SketchExtrudeAxis(string plane)
    {
        // Stable user-facing default: positive global axis for reverse=false.
        switch (plane)
        {
            case "XZ": return new Vector3d(0.0, 1.0, 0.0);
            case "YZ": return new Vector3d(1.0, 0.0, 0.0);
            default: return new Vector3d(0.0, 0.0, 1.0);
        }
    }

    private static void AddLine(Sketch sk, string sketchId, double x1, double y1, double x2, double y2)
    {
        var curve = _part.Curves.CreateLine(
            SketchPoint(sketchId, x1, y1),
            SketchPoint(sketchId, x2, y2));
        sk.AddGeometry(curve, Sketch.InferConstraintsOption.InferNoConstraints);
    }

    private static void ExtrudeSketchCore(Sketch sk, double startZ, double endZ,
        BooleanOperation.BooleanType bt, Body target)
    {
        var section = _part.Sections.CreateSection();
        var rule = _part.ScRuleFactory.CreateRuleCurveFeature(new Feature[] { sk.Feature });
        section.AddToSection(
            new SelectionIntentRule[] { rule }, null, null, null,
            new Point3d(0.0, 0.0, 0.0), Section.Mode.Create, false);
        var dir = _part.Directions.CreateDirection(
            new Point3d(0.0, 0.0, 0.0), new Vector3d(0.0, 0.0, 1.0),
            SmartObject.UpdateOption.WithinModeling);
        var b = _part.Features.CreateExtrudeBuilder(null);
        try
        {
            b.Section = section;
            b.Direction = dir;
            b.Limits.StartExtend.Value.RightHandSide = startZ.ToString("0.###", CultureInfo.InvariantCulture);
            b.Limits.EndExtend.Value.RightHandSide = endZ.ToString("0.###", CultureInfo.InvariantCulture);
            b.BooleanOperation.Type = bt;
            if (target != null) b.BooleanOperation.SetTargetBodies(new Body[] { target });
            b.AllowSelfIntersectingSection(true);
            b.CommitFeature();
        }
        finally
        {
            b.Destroy();
        }
    }


    private static int BlendChamfer(Body body, double value, int[] indices, bool isBlend)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0) throw new Exception("blend/chamfer value must be positive and finite");
        Edge[] edges = body.GetEdges();
        if (indices != null)
        {
            if (indices.Length == 0) throw new Exception("edge_indices must not be empty");
            HashSet<int> unique = new HashSet<int>();
            foreach (int selected in indices)
                if (selected < 0 || selected >= edges.Length || !unique.Add(selected))
                    throw new Exception("invalid or duplicate edge index: " + selected);
        }
        int done=0;
        for (int i=0;i<edges.Length;i++)
        {
            if (indices != null && Array.IndexOf(indices,i)<0) continue;
            ScCollector collector = _part.ScCollectors.CreateCollector();
            try
            {
                SelectionIntentRule rule = _part.ScRuleFactory.CreateRuleEdgeDumb(new Edge[] { edges[i] });
                collector.ReplaceRules(new SelectionIntentRule[] { rule },false);
                if (isBlend)
                {
                    EdgeBlendBuilder builder = _part.Features.CreateEdgeBlendBuilder(null);
                    try { builder.AddChainset(collector,value.ToString("0.###",CultureInfo.InvariantCulture)); builder.CommitFeature(); }
                    finally { builder.Destroy(); }
                }
                else
                {
                    ChamferBuilder builder = _part.Features.CreateChamferBuilder(null);
                    try
                    {
                        builder.SmartCollector=collector;
                        builder.FirstOffset=value.ToString("0.###",CultureInfo.InvariantCulture);
                        builder.Option=ChamferBuilder.ChamferOption.SymmetricOffsets;
                        builder.CommitFeature();
                    }
                    finally { builder.Destroy(); }
                }
                done++;
            }
            catch (Exception ex) { throw new Exception((isBlend ? "blend" : "chamfer") + " failed at selected edge " + i + ": " + ex.Message,ex); }
            finally { collector.Destroy(); }
        }
        if (done==0) throw new Exception("no edges selected for blend/chamfer");
        return done;
    }

    // ---- legacy create_block (kept) ------------------------------------
    private static string CreateBlock(string[] parts)
    {
        if (parts.Length < 4) return ErrJson("usage: create_block W H D");
        double w = D(parts[1]), h = D(parts[2]), d = D(parts[3]);
        string ws = Environment.GetEnvironmentVariable("NX_MCP_WORKSPACE") ?? "";
        string partName = string.IsNullOrEmpty(ws)
            ? "nx_mcp_block.prt"
            : Path.Combine(ws, "nx_mcp_block.prt");
        string cr = CreatePart(new string[] { "nx_create_part", partName });
        if (cr.Contains("\"ok\":false")) return cr;

        string sk = CreateSketch(new string[] { "nx_create_sketch", "XY" });
        string sid = ExtractId(sk, "sketch_id");
        SketchRectangle(new string[] { "nx_sketch_rectangle", sid, "0", "0", w.ToString("0.###", CultureInfo.InvariantCulture), h.ToString("0.###", CultureInfo.InvariantCulture) });
        FinishSketch(new string[] { "nx_finish_sketch", sid });
        string ex = Extrude(new string[] { "nx_extrude", sid, d.ToString("0.###", CultureInfo.InvariantCulture) });
        SavePart();
        return OkJson("block " + w + "x" + h + "x" + d + " saved=" + partName);
    }

    // ---- phase-2: remaining certified tools ----------------------------
    /// Normalise a path so comparisons are not defeated by mixed '/' and '\'.
    private static string NormPath(string p)
    {
        if (string.IsNullOrEmpty(p)) return "";
        return Path.GetFullPath(p.Trim()).Replace("/", "\\");
    }

    private static void RegisterExistingBodies(NXOpen.Part part)
    {
        if (part == null) return;
        try
        {
            foreach (Body b in part.Bodies.ToArray())
            {
                string bid = "";
                try { bid = b.Name; } catch { }
                if (string.IsNullOrEmpty(bid)) { try { bid = b.JournalIdentifier; } catch { } }
                if (string.IsNullOrEmpty(bid)) bid = "BODY_" + (++_bodyCounter);
                _bodies[bid] = b;
            }
        }
        catch { }
    }

    // Opening a STEP is an explicit translation, never a native .prt load or
    // an import into the existing work part. The .stp/.step input is read-only.
    private static string StepImportWorkspacePath(string raw)
    {
        string workspace=Environment.GetEnvironmentVariable("NX_MCP_AUDIT_ROOT");
        if(string.IsNullOrEmpty(workspace))workspace=Environment.GetEnvironmentVariable("NX_MCP_WORKSPACE");
        if(string.IsNullOrEmpty(workspace))throw new Exception("STEP translation requires an explicit workspace");
        string boundary=Path.GetFullPath(workspace).TrimEnd('\\','/')+Path.DirectorySeparatorChar;
        string full=Path.GetFullPath(raw);
        if(!full.StartsWith(boundary,StringComparison.OrdinalIgnoreCase))throw new Exception("STEP translation path is outside its write boundary");
        string current=File.Exists(full)?full:Path.GetDirectoryName(full);
        while(!string.IsNullOrEmpty(current)&&current.Length>=boundary.TrimEnd('\\').Length)
        {
            if((File.Exists(current)||Directory.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                throw new Exception("STEP translation through reparse points is not permitted");
            current=Path.GetDirectoryName(current);
        }
        return full;
    }
    private static string StepSourceHash(string path)
    {
        using(var stream=File.OpenRead(path))using(var hash=System.Security.Cryptography.SHA256.Create())
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-","");
    }
    private static void LogStepPartState(string phase)
    {
        try
        {
            var paths=new List<string>();foreach(BasePart loaded in _session.Parts)paths.Add(loaded.FullPath);
            Log("STEP "+phase+" work=["+(_session.Parts.Work==null?"":_session.Parts.Work.FullPath)+"] display=["+(_session.Parts.Display==null?"":_session.Parts.Display.FullPath)+"] loaded="+AJson.Serialize(paths));
        }
        catch(Exception diagnostic){Log("STEP state diagnostic failed: "+diagnostic.Message);}
    }
    private static string ImportStepAsNewPart(string sourcePath)
    {
        string output="";
        try
        {
            sourcePath=StepImportWorkspacePath(sourcePath);
            if(_session.ActiveSketch!=null)throw new Exception("finish the active sketch before opening a STEP");
            string header;
            using(var stream=File.OpenRead(sourcePath))
            {
                byte[] bytes=new byte[Math.Min(8192,(int)Math.Min(stream.Length,8192))];int read=stream.Read(bytes,0,bytes.Length);
                header=Encoding.ASCII.GetString(bytes,0,read).ToUpperInvariant();
            }
            if(!header.StartsWith("ISO-10303-21;"))throw new Exception("input is not an ISO-10303-21 STEP file");
            if(header.IndexOf("AUTOMOTIVE_DESIGN",StringComparison.Ordinal)<0)
                throw new Exception("this safe STEP branch accepts AP214 AUTOMOTIVE_DESIGN only; NX12 has separate AP203/AP242 importers but these are not exposed by this branch");
            output=StepImportWorkspacePath(Path.Combine(Path.GetDirectoryName(sourcePath),Path.GetFileNameWithoutExtension(sourcePath)+"__imported.prt"));
            if(File.Exists(output)||Directory.Exists(output))throw new Exception("derived STEP output already exists; open the explicit __imported.prt or use a new STEP filename; no overwrite performed");
            string nxRoot=Environment.GetEnvironmentVariable("NX_HOME");
            if(string.IsNullOrEmpty(nxRoot))nxRoot=Environment.GetEnvironmentVariable("UGII_BASE_DIR");
            if(string.IsNullOrEmpty(nxRoot))throw new Exception("select the local NX12 installation through NX_HOME or UGII_BASE_DIR");
            string sdk=Path.Combine(nxRoot,@"NXBIN\managed\NXOpen.dll");
            if(!File.Exists(sdk)||System.Diagnostics.FileVersionInfo.GetVersionInfo(sdk).FileMajorPart!=12)
                throw new Exception("NX12 SDK installation must be explicitly selected");
            string settings=Path.Combine(nxRoot,@"STEP214UG\step214ug.def");
            if(!File.Exists(settings))throw new Exception("installed NX12 STEP214 import settings not found");
            string inputHash=StepSourceHash(sourcePath);
            Step214Importer importer=_session.DexManager.CreateStep214Importer();
            try
            {
                importer.SettingsFile=settings;importer.InputFile=sourcePath;importer.OutputFile=output;
                // NX12 SDK: set FileOpenFlag=false when doing file import.
                importer.ImportTo=Step214Importer.ImportToOption.NewPart;importer.FileOpenFlag=false;
                importer.FlattenAssembly=true;importer.ImportToTeamcenter=false;importer.SewSurfaces=true;
                importer.Optimize=false;importer.SimplifyGeometry=false;importer.ProcessHoldFlag=true;
                importer.ObjectTypes.Solids=true;importer.ObjectTypes.Surfaces=true;importer.ObjectTypes.Curves=true;
                // SDK explicitly documents that successful Commit returns NULL.
                Log("STEP import config fileOpen=false importTo=NewPart hold=true input=["+sourcePath+"] output=["+output+"] settings=["+settings+"]");
                LogStepPartState("before commit");importer.Commit();LogStepPartState("after commit");
            }
            finally{importer.Destroy();}
            if(!File.Exists(output)||new FileInfo(output).Length==0)throw new Exception("STEP import Commit returned without a nonempty new output .prt; inspect import diagnostics (no successful translation claimed)");
            if(StepSourceHash(sourcePath)!=inputHash)throw new Exception("source STEP changed during translation");
            string opened=OpenPart(new string[]{"nx_open_part",output});
            if(opened.IndexOf("\"ok\":false",StringComparison.Ordinal)>=0)throw new Exception("translated .prt could not be opened: "+opened);
            if(_part==null||!string.Equals(NormPath(_part.FullPath),NormPath(output),StringComparison.OrdinalIgnoreCase))
                throw new Exception("translated output did not become the actual work part");
            _part.SetUserAttribute("NX_MCP_STEP_SOURCE",-1,sourcePath,NXOpen.Update.Option.Now);
            _part.SetUserAttribute("NX_MCP_STEP_SOURCE_SHA256",-1,inputHash,NXOpen.Update.Option.Now);
            SavePart();
            return OkJson("part",output,"imported AP214 via NX12 Step214Importer; new native part; input unchanged; translator completed");
        }
        catch(Exception error)
        {
            // File translation is not an atomic undo. Keep diagnostics and any
            // newly generated output; never delete or retry over it automatically.
            try{_part=_session.Parts.Work;_partPath=_part==null?null:_part.FullPath;ResetTaskState();RegisterExistingBodies(_part);}catch{}
            return ErrJson("STEP open/translation failed: "+error.Message+"; any new output retained for inspection: "+output);
        }
    }

    private static string OpenPart(string[] parts)
    {
        if (parts.Length < 2) return ErrJson("usage: nx_open_part <path>");
        string path = parts[1];
        if (!File.Exists(path)) return ErrJson("part file not found: " + path);
        if (_session == null) _session = Session.GetSession();
        string extension=Path.GetExtension(path);
        if(string.Equals(extension,".stp",StringComparison.OrdinalIgnoreCase)||string.Equals(extension,".step",StringComparison.OrdinalIgnoreCase))
            return ImportStepAsNewPart(path);

        // [NX12 fix] Re-opening a part that is ALREADY loaded in this session made
        // NX throw "文件已存在", and every later call then reported stale data from
        // whatever part was still current. An agent routinely re-opens the same
        // drawing, so detect that case and simply switch to the loaded part.
        try
        {
            string want = NormPath(path);
            foreach (NXOpen.BasePart bp in _session.Parts.ToArray())
            {
                string fp = "";
                try { fp = bp.FullPath; } catch { }
                if (string.IsNullOrEmpty(fp)) continue;
                string have = NormPath(fp);

                // The path arriving over the pipe can mix '/' and '\' separators,
                // which makes a raw string comparison miss an already-open part.
                bool hit = string.Equals(have, want, StringComparison.OrdinalIgnoreCase);
                // Full path only: a same-named part in another folder is not this part.
                if (!hit) continue;

                NXOpen.PartLoadStatus st2 = null;
                try { _session.Parts.SetDisplay(bp, false, false, out st2); } catch { }
                finally { if (st2 != null) { try { st2.Dispose(); } catch { } } }

                NXOpen.Part p2 = _session.Parts.Work;
                if (p2 == null) p2 = bp as NXOpen.Part;
                if (p2 != null)
                {
                    _part = p2;
                    _partPath = p2.FullPath;
                    ResetTaskState();
                    RegisterExistingBodies(p2);
                    return OkJson("part", _partPath, "already open; switched to it");
                }
            }
        }
        catch { }

        NXOpen.PartLoadStatus status = null;
        try
        {
            var opened = _session.Parts.OpenBaseDisplay(path, out status);
            NXOpen.Part part = opened as NXOpen.Part;
            if (part == null) part = _session.Parts.Work;
            _part = part;
            _partPath = part.FullPath;
            ResetTaskState();
            // register existing bodies so GetBody works after nx_open_part
            RegisterExistingBodies(_part);
            return OkJson("part", _partPath, "opened");
        }
        finally
        {
            if (status != null) { try { status.Dispose(); } catch { } }
        }
    }

    private static string ClosePart(string[] parts)
    {
        bool save = parts.Length < 2 || parts[1] != "0";
        if (_part != null && save) SavePart();
        if (_part != null)
        {
            _part.Close(NXOpen.BasePart.CloseWholeTree.False,
                        NXOpen.BasePart.CloseModified.CloseModified, null);
            _part = null;
            _partPath = null;
        }
        ResetTaskState();
        return OkJson("closed part");
    }

    private static string ListObjects(string collection, string kind)
    {
        if (_part == null) return ErrJson("no active part");
        var names = new List<string>();
        try
        {
            if (collection == "Sketches")
            {
                var refs=new List<object>();
                foreach (NXOpen.Sketch o in _part.Sketches)
                {
                    string n=SketchNativeName(o);
                    if (string.IsNullOrEmpty(n)) { try { n = o.JournalIdentifier; } catch { } }
                    string id=n;
                    foreach(KeyValuePair<string,Sketch> cached in _sketches)
                        if(cached.Value.Tag==o.Tag){id=cached.Key;break;}
                    names.Add(n);
                    refs.Add(ADict("id",id,"name",n));
                }
                return AJson.Serialize(ADict("ok",true,"objects",names,"sketch_refs",refs,"part",_part.FullPath,"message","Found "+names.Count+" sketch(s)."));
            }
            else if (collection == "Bodies")
            {
                foreach (NXOpen.Body o in _part.Bodies)
                {
                    string n = "";
                    try { n = o.Name; } catch { }
                    if (string.IsNullOrEmpty(n)) { try { n = o.JournalIdentifier; } catch { } }
                    names.Add(n);
                }
            }
            else if (collection == "Features")
            {
                foreach (NXOpen.Features.Feature o in _part.Features)
                {
                    string n = "";
                    try { n = o.GetFeatureName(); } catch { }
                    if (string.IsNullOrEmpty(n)) { try { n = o.JournalIdentifier; } catch { } }
                    names.Add(n);
                }
            }
        }
        catch { }
        var sb = new System.Text.StringBuilder();
        sb.Append("{\"ok\":true,\"objects\":[");
        for (int i = 0; i < names.Count; i++)
        {
            if (i > 0) sb.Append(",");
            sb.Append("\"").Append(Esc(names[i])).Append("\"");
        }
        sb.Append("],\"message\":\"Found ").Append(names.Count).Append(" ").Append(kind).Append("(s).\"}");
        return sb.ToString();
    }


    private static string Undo()
    {
        if (_session == null) _session = Session.GetSession();
        if (_modelUndo.Count == 0) return ErrJson("no recorded NX MCP modeling operation is available to undo in this part; no change made");
        ModelUndoFrame frame = _modelUndo[_modelUndo.Count - 1];
        if (_part == null || _session.Parts.Work == null ||
            frame.PartTag != _part.Tag || frame.PartTag != _session.Parts.Work.Tag)
            return ErrJson("undo part identity mismatch; no change made");
        if (!_session.DoesUndoMarkExist(frame.Mark, null))
            return ErrJson("the recorded MCP undo mark no longer exists; no change made");
        if (!_session.NewestVisibleUndoMark.Equals(frame.Mark))
            return ErrJson("a newer non-MCP visible operation exists; no change made");
        try
        {
            RollBackModelFrame(frame);
            _modelUndo.RemoveAt(_modelUndo.Count - 1);
            return OkJson("undone NX MCP command " + frame.Command);
        }
        catch (Exception ex)
        {
            _modelUndo.Clear();
            return ErrJson("undo failed: " + ex.Message + "; inspect current model before further commands");
        }
    }
}
