// Publication copy modified by repository maintainer on 2026-11-10.
// Original authors and licenses: ../../docs/ATTRIBUTION.md.
using System;
using System.IO;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;
using NXOpen;
using NXOpen.Drawings;
using NXOpen.Annotations;
using NXOpen.UF;

// NX12-only optional extension. Each candidate operation requires real runtime
// acceptance before its capability status can be promoted to verified.
public static partial class NX_MCP_Loader
{
    private static JavaScriptSerializer DSerializer() { return new JavaScriptSerializer { MaxJsonLength = 8388608 }; }
    private static string DJ(object value) { return DSerializer().Serialize(value); }
    private static string DS(Dictionary<string, object> p, string key, string fallback)
    { object v; return p.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : fallback; }
    private static double DN(Dictionary<string, object> p, string key, double fallback)
    { object v; double n = p.TryGetValue(key, out v) && v != null ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : fallback; if (double.IsNaN(n) || double.IsInfinity(n)) throw new Exception(key + " must be finite"); return n; }
    private static object[] DArray(object value, string label)
    {
        object[] direct = value as object[]; if (direct != null) return direct;
        IList list = value as IList; if (list == null) throw new Exception("missing array " + label);
        object[] result = new object[list.Count]; list.CopyTo(result, 0); return result;
    }
    private static object[] DA(Dictionary<string, object> p, string key)
    { object v; if (!p.TryGetValue(key, out v)) throw new Exception("missing array " + key); return DArray(v, key); }
    private static string DId(NXObject o) { return o.JournalIdentifier; }
    private static object DSuccess(string message, object data)
    { return new Dictionary<string, object> { { "ok", true }, { "status", "success" }, { "message", message }, { "data", data } }; }
    private static void DGuardPart()
    {
        _session = Session.GetSession();
        Part current = _session.Parts.Work;
        if (current == null || string.IsNullOrEmpty(current.FullPath)) throw new Exception("a saved independent work part is required");
        string ws = Environment.GetEnvironmentVariable("NX_MCP_WORKSPACE");
        if (string.IsNullOrEmpty(ws)) throw new Exception("NX_MCP_WORKSPACE is required");
        string boundary = Path.GetFullPath(ws).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(current.FullPath).StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
            throw new Exception("work part is outside the independent MCP workspace; no change allowed");
        _part = current; _partPath = current.FullPath;
    }
    private static DrawingSheet DSheet()
    {
        DrawingSheet sheet = _part.DrawingSheets.CurrentDrawingSheet;
        if (sheet == null) throw new Exception("no current drawing sheet");
        _session.ApplicationSwitchImmediate("UG_APP_DRAFTING");
        sheet.Open(); return sheet;
    }
    private static void DUpdate(DrawingSheet sheet)
    {
        foreach (DraftingView view in sheet.GetDraftingViews()) view.Update();
        _part.DrawingSheets.RefreshCurrentSheet();
    }
    private static DraftingView DView(Dictionary<string, object> p)
    {
        DrawingSheet sheet;
        return ResolveViewAnySheet(DS(p, "view_id", "0"), out sheet);
    }
    private static Edge DEdge(Dictionary<string, object> p, string key)
    {
        Body body = GBody(DS(p, "body_id", "BODY_1"));
        Edge[] edges = body.GetEdges(); int index = checked((int)DN(p, key, -1));
        if (index < 0 || index >= edges.Length) throw new Exception(key + " out of range");
        return edges[index];
    }
    private static string DispatchDraftingExtension(string[] parts)
    {
        if (parts.Length == 0 || !parts[0].StartsWith("nx_drafting_", StringComparison.Ordinal)) return null;
        string command = parts[0];
        bool mutation = command != "nx_drafting_list_tables" && command != "nx_drafting_export_cad";
        Session.UndoMarkId mark = default(Session.UndoMarkId); bool marked = false;
        try
        {
            if (parts.Length != 2) throw new Exception("expected one Base64 UTF-8 JSON argument");
            Dictionary<string, object> p = DSerializer().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])));
            DGuardPart();
            if (mutation) { mark = _session.SetUndoMark(Session.MarkVisibility.Visible, "NX MCP drafting extension " + command); marked = true; }
            object result;
            switch (command)
            {
                case "nx_drafting_add_model_view": result = DAddModelView(p); break;
                case "nx_drafting_create_table": result = DCreateTable(p); break;
                case "nx_drafting_update_table": result = DUpdateTable(p); break;
                case "nx_drafting_list_tables": result = DListTables(p); break;
                case "nx_drafting_center_mark": result = DCenterMark(p); break;
                case "nx_drafting_centerline": result = DCenterline(p); break;
                case "nx_drafting_detail_view": result = DDetailView(p); break;
                case "nx_drafting_section_view": result = DSectionView(p); break;
                case "nx_drafting_gdt": result = DGdt(p); break;
                case "nx_drafting_balloon": result = DBalloon(p); break;
                case "nx_drafting_export_cad": result = DExportCad(p); break;
                default: throw new Exception("unknown drafting extension command " + command);
            }
            // Keep the successful visible mark: legacy nx_undo must not silently
            // undo through these newly created drawing objects into older models.
            return DJ(result);
        }
        catch (Exception e)
        {
            bool rolledBack = false; string rollbackError = null;
            if (marked) { try { _session.UndoToMark(mark, null); _session.DeleteUndoMark(mark, null); rolledBack = true; } catch (Exception undo) { rollbackError = undo.Message; } }
            return DJ(new Dictionary<string, object> { { "ok", false }, { "code", "NX12_DRAFTING_EXTENSION_FAILED" }, { "error", e.Message }, { "command", command }, { "rolled_back", rolledBack }, { "rollback_error", rollbackError } });
        }
    }
    private static object DAddModelView(Dictionary<string, object> p)
    {
        DrawingSheet sheet = DSheet(); string modelName = DS(p, "model_view", "Isometric");
        ModelingView model = _part.ModelingViews.FindObject(modelName);
        double scale = DN(p, "scale", 1); if (scale <= 0) throw new Exception("scale must be positive");
        Point3d origin = new Point3d(DN(p, "x", 300), DN(p, "y", 200), 0);
        DraftingView view = sheet.SheetDraftingViews.CreateBaseView(model, origin, scale, false);
        string name = DS(p, "name", "MCP_" + modelName); view.SetName(name);
        view.MoveView(origin); DUpdate(sheet);
        Point3d actual = view.GetDrawingReferencePoint();
        if (Math.Abs(actual.X - origin.X) > 1e-6 || Math.Abs(actual.Y - origin.Y) > 1e-6) throw new Exception("model view origin readback mismatch");
        return DSuccess("model view created", new { view_id = DId(view), name = name, model_view = modelName, origin = new double[] { actual.X, actual.Y }, scale = scale });
    }
    private static List<Tag> DTables()
    {
        UFSession uf = UFSession.GetUFSession(); List<Tag> tables = new List<Tag>(); Tag tag = Tag.Null;
        while (true) { uf.Obj.CycleObjsInPart(_part.Tag, 165, ref tag); if (tag == Tag.Null) break; int type, subtype; uf.Obj.AskTypeAndSubtype(tag, out type, out subtype); if (subtype == 1 || subtype == 11) tables.Add(tag); }
        return tables;
    }
    private static NXObject DObject(Tag tag) { return (NXObject)NXOpen.Utilities.NXObjectManager.Get(tag); }
    private static Tag DTable(Dictionary<string, object> p)
    {
        string token = DS(p, "table_id", "");
        foreach (Tag tag in DTables()) { NXObject obj = DObject(tag); if (obj.JournalIdentifier == token || obj.Name == token) return tag; }
        throw new Exception("table_id not found in active part; call nx_drafting_list_tables");
    }
    private static object DTableData(Tag table)
    {
        UFSession uf = UFSession.GetUFSession(); int nr, nc; uf.Tabnot.AskNmRows(table, out nr); uf.Tabnot.AskNmColumns(table, out nc);
        List<object> rows = new List<object>(); List<double> widths = new List<double>(), heights = new List<double>();
        for (int j = 0; j < nc; j++) { Tag col; double width; uf.Tabnot.AskNthColumn(table, j, out col); uf.Tabnot.AskColumnWidth(col, out width); widths.Add(width); }
        for (int i = 0; i < nr; i++) { Tag row; double height; uf.Tabnot.AskNthRow(table, i, out row); uf.Tabnot.AskRowHeight(row, out height); heights.Add(height); }
        for (int i = 0; i < nr; i++) { Tag row; uf.Tabnot.AskNthRow(table, i, out row); List<string> texts = new List<string>(); for (int j = 0; j < nc; j++) { Tag col, cell; string text; uf.Tabnot.AskNthColumn(table, j, out col); uf.Tabnot.AskCellAtRowCol(row, col, out cell); uf.Tabnot.AskCellText(cell, out text); texts.Add(text); } rows.Add(texts); }
        NXObject obj = DObject(table);var layout=new List<object>();
        if(obj.Name=="MCP_LEGACY_TITLE_BLOCK")
        {
            for(int i=0;i<nr;i++)for(int j=0;j<nc;j++)
            {
                Tag row,col,cell;uf.Tabnot.AskNthRow(table,i,out row);uf.Tabnot.AskNthColumn(table,j,out col);uf.Tabnot.AskCellAtRowCol(row,col,out cell);
                UFTabnot.CellPrefs cp;uf.Tabnot.AskCellPrefs(cell,out cp);string evaluated;uf.Tabnot.AskEvaluatedCellText(cell,out evaluated);
                var fits=new List<int>();if(cp.fit_methods!=null)for(int k=0;k<Math.Min(cp.nm_fit_methods,cp.fit_methods.Length);k++)fits.Add((int)cp.fit_methods[k]);
                layout.Add(new{row=i,column=j,text_height=cp.text_height,text_font=cp.text_font,fit_methods=fits,evaluated_text=evaluated});
            }
        }
        return new { table_id = obj.JournalIdentifier, name = obj.Name, row_count = nr, column_count = nc, cells = rows, row_heights = heights, column_widths = widths, editable_tabular_note = true, cell_layout=layout, layout_validation="native preferences only; rendered PDF must be inspected" };
    }
    private static object DCreateTable(Dictionary<string, object> p)
    {
        DrawingSheet sheet = DSheet(); UFSession uf = UFSession.GetUFSession();
        object[] widths = DA(p, "column_widths"), heights = DA(p, "row_heights"), cells = DA(p, "cells");
        if (widths.Length < 1 || heights.Length < 1 || widths.Length > 30 || heights.Length > 100 || cells.Length != heights.Length) throw new Exception("table dimensions invalid; maximum 100 rows by 30 columns");
        string name = DS(p, "name", "MCP_TABLE"); foreach (Tag old in DTables()) if (DObject(old).Name == name) throw new Exception("table name already exists; update or use a new name");
        foreach (object w in widths) if (Convert.ToDouble(w) <= 0) throw new Exception("column width must be positive");
        foreach (object h in heights) if (Convert.ToDouble(h) <= 0) throw new Exception("row height must be positive");
        for (int i = 0; i < cells.Length; i++) if (DArray(cells[i], "cells row").Length != widths.Length) throw new Exception("cells must be a rectangular matrix");
        UFTabnot.SectionPrefs prefs; uf.Tabnot.AskDefaultSectionPrefs(out prefs); prefs.header_location = (UFTabnot.HeaderLocation)0; prefs.attach_point = (UFTabnot.AttachPoint)1;
        double requestedHeight = 0; foreach (object height in heights) requestedHeight += Convert.ToDouble(height, CultureInfo.InvariantCulture);
        prefs.max_height = requestedHeight + 1.0; // Keep this explicit table in a single section.
        Tag table; uf.Tabnot.Create(ref prefs, new double[] { DN(p, "x", 25), DN(p, "y", 60), 0 }, out table); DObject(table).SetName(name);
        int nr, nc; uf.Tabnot.AskNmRows(table, out nr); uf.Tabnot.AskNmColumns(table, out nc);
        while (nr > heights.Length) { Tag row; uf.Tabnot.AskNthRow(table, nr - 1, out row); uf.Tabnot.RemoveRow(row); uf.Obj.DeleteObject(row); nr--; }
        while (nc > widths.Length) { Tag col; uf.Tabnot.AskNthColumn(table, nc - 1, out col); uf.Tabnot.RemoveColumn(col); uf.Obj.DeleteObject(col); nc--; }
        while (nr < heights.Length) { Tag row; uf.Tabnot.CreateRow(8, out row); uf.Tabnot.AddRow(table, row, -1); nr++; }
        while (nc < widths.Length) { Tag col; uf.Tabnot.CreateColumn(30, out col); uf.Tabnot.AddColumn(table, col, -1); nc++; }
        for (int j = 0; j < nc; j++) { Tag col; uf.Tabnot.AskNthColumn(table, j, out col); uf.Tabnot.SetColumnWidth(col, Convert.ToDouble(widths[j], CultureInfo.InvariantCulture)); }
        int font = _part.Fonts.AddFont(DS(p, "font", "chinesef"));
        for (int i = 0; i < nr; i++)
        {
            Tag row; uf.Tabnot.AskNthRow(table, i, out row); object[] texts = DArray(cells[i], "cells row");
            for (int j = 0; j < nc; j++)
            {
                Tag col, cell; uf.Tabnot.AskNthColumn(table, j, out col); uf.Tabnot.AskCellAtRowCol(row, col, out cell);
                UFTabnot.CellPrefs cp; uf.Tabnot.AskCellPrefs(cell, out cp);
                // NX12 uf_tabnot.h: SetRowHeight is ignored while either
                // auto-size-row or auto-size-text fit methods remain enabled.
                cp.nm_fit_methods = 0;
                if (cp.fit_methods != null) for (int fit = 0; fit < cp.fit_methods.Length; fit++) cp.fit_methods[fit] = (UFTabnot.FitMethod)0;
                cp.text_font = font; cp.text_height = DN(p, "text_height", 3); cp.horiz_just = (UFTabnot.Just)1; cp.vert_just = (UFTabnot.Just)1;
                uf.Tabnot.SetCellPrefs(cell, ref cp); uf.Tabnot.SetCellText(cell, Convert.ToString(texts[j], CultureInfo.InvariantCulture) ?? "");
            }
            uf.Tabnot.SetRowHeight(row, Convert.ToDouble(heights[i], CultureInfo.InvariantCulture));
        }
        // SetRowHeight must run while fit methods are disabled. Only after
        // exact row/column dimensions are fixed may the legacy title opt into
        // auto-size-text. UF_TABNOT documents no-fit fallback as ########.
        object fitValue;bool autoFit=p.TryGetValue("auto_fit_text",out fitValue)&&Convert.ToBoolean(fitValue,CultureInfo.InvariantCulture);
        if(autoFit)
        {
            for(int i=0;i<nr;i++)for(int j=0;j<nc;j++)
            {
                Tag row,col,cell;uf.Tabnot.AskNthRow(table,i,out row);uf.Tabnot.AskNthColumn(table,j,out col);uf.Tabnot.AskCellAtRowCol(row,col,out cell);
                UFTabnot.CellPrefs cp;uf.Tabnot.AskCellPrefs(cell,out cp);
                if(cp.fit_methods==null||cp.fit_methods.Length<1)cp.fit_methods=new UFTabnot.FitMethod[8];
                for(int k=0;k<cp.fit_methods.Length;k++)cp.fit_methods[k]=(UFTabnot.FitMethod)0;
                cp.nm_fit_methods=1;cp.fit_methods[0]=(UFTabnot.FitMethod)2; // UF_TABNOT_fit_method_auto_size_text
                uf.Tabnot.SetCellPrefs(cell,ref cp);
            }
        }
        uf.Tabnot.Update(table); DUpdate(sheet);
        for (int i = 0; i < nr; i++) { Tag row; double actual; uf.Tabnot.AskNthRow(table, i, out row); uf.Tabnot.AskRowHeight(row, out actual); if (Math.Abs(actual - Convert.ToDouble(heights[i], CultureInfo.InvariantCulture)) > 1e-6) throw new Exception("table row height readback differs from requested height; creation rolled back"); }
        return DSuccess("editable drawing table created; explicit row heights read back", DTableData(table));
    }
    private static object DUpdateTable(Dictionary<string, object> p)
    {
        DrawingSheet sheet = DSheet(); Tag table = DTable(p); UFSession uf = UFSession.GetUFSession(); int nr, nc; uf.Tabnot.AskNmRows(table, out nr); uf.Tabnot.AskNmColumns(table, out nc);
        foreach (object item in DA(p, "updates")) { Dictionary<string, object> change = (Dictionary<string, object>)item; int i = (int)DN(change, "row", -1), j = (int)DN(change, "column", -1); if (i < 0 || i >= nr || j < 0 || j >= nc) throw new Exception("table update index out of range"); Tag row, col, cell; uf.Tabnot.AskNthRow(table, i, out row); uf.Tabnot.AskNthColumn(table, j, out col); uf.Tabnot.AskCellAtRowCol(row, col, out cell); uf.Tabnot.SetCellText(cell, DS(change, "text", "")); }
        uf.Tabnot.Update(table); DUpdate(sheet); return DSuccess("table updated", DTableData(table));
    }
    private static object DListTables(Dictionary<string, object> p)
    { List<object> data = new List<object>(); foreach (Tag t in DTables()) data.Add(DTableData(t)); return DSuccess("drawing tables read", new { count = data.Count, tables = data }); }
    private static object DCenterMark(Dictionary<string, object> p)
    {
        DrawingSheet sheet = DSheet(); DraftingView view = DView(p); Edge edge = DEdge(p, "edge_index");
        GEdge analytic = GReadEdge(edge, UFSession.GetUFSession());
        if (analytic.AnalyticType != "circle" || analytic.Center == null) throw new Exception("center mark requires an analytically circular edge (including equal-axis ellipse representation)");
        CenterMarkBuilder builder = _part.Annotations.Centerlines.CreateCenterMarkBuilder(null);
        try
        {
            builder.Settings.Size = DN(p, "size", 5); builder.MultipleCenterMarks = false;
            builder.Locations.Add(edge, view, new Point3d(analytic.Center[0], analytic.Center[1], analytic.Center[2]));
            NXObject obj = builder.Commit();
            if(obj == null)throw new Exception("center mark commit returned no native object");
            DUpdate(sheet); double scale=GScale(UFSession.GetUFSession());
            // Journal selectors are retained for compatibility, but a repeated
            // selector string is not sufficient evidence of object identity.
            // Native tags distinguish current-session objects only; they are
            // deliberately not advertised as stable identifiers across reopen.
            return DSuccess("associative center mark created", new {
                annotation_id = DId(obj), annotation_tag = obj.Tag.ToString(),
                annotation_object_type = obj.GetType().FullName,
                annotation_tag_scope = "current_NX_session_only_not_stable_after_reopen",
                journal_id_uniqueness = "not_used_as_object_count_evidence",
                view_id = DId(view), view_tag = view.Tag.ToString(),
                edge_index = DN(p, "edge_index", -1), edge_tag = edge.Tag.ToString(),
                analytic_curve_type = analytic.AnalyticType,
                analytic_center_abs_mm = new double[]{analytic.Center[0]*scale,analytic.Center[1]*scale,analytic.Center[2]*scale},
                length_unit = "mm"
            });
        }
        finally { builder.Destroy(); }
    }
    private static object DCenterline(Dictionary<string, object> p)
    {
        DrawingSheet sheet = DSheet(); DraftingView view = DView(p); Edge a = DEdge(p, "edge_index_1"), b = DEdge(p, "edge_index_2");
        Centerline2dBuilder builder = _part.Annotations.Centerlines.CreateCenterline2dBuilder(null);
        try { builder.Type = Centerline2dBuilder.Types.FromCurves; builder.Side1.SetValue(a, view, MidpointOfObject(a)); builder.Side2.SetValue(b, view, MidpointOfObject(b)); builder.Settings.Size = DN(p, "size", 5); NXObject obj = builder.Commit(); DUpdate(sheet); return DSuccess("associative centerline created", new { annotation_id = DId(obj), view_id = DId(view) }); }
        finally { builder.Destroy(); }
    }
    private static DraftingView DFindView(Tag tag)
    { foreach (DraftingView v in DSheet().GetDraftingViews()) if (v.Tag == tag) return v; throw new Exception("new drafting view not found"); }
    private static object DDetailView(Dictionary<string, object> p)
    {
        DrawingSheet sheet = DSheet(); DraftingView parent = DView(p); double x1 = DN(p, "min_x", 0), y1 = DN(p, "min_y", 0), x2 = DN(p, "max_x", 0), y2 = DN(p, "max_y", 0), scale = DN(p, "scale", 2);
        if (x2 <= x1 || y2 <= y1 || scale <= 0) throw new Exception("detail rectangular bounds and positive scale required");
        Tag tag; UFSession.GetUFSession().Draw.AddDetailView(sheet.Tag, parent.Tag, new double[] { x1, y1 }, new double[] { x2, y2 }, scale, new double[] { DN(p, "x", 320), DN(p, "y", 200) }, out tag);
        DraftingView view = DFindView(tag); view.SetName(DS(p, "name", "MCP_DETAIL")); DUpdate(sheet); return DSuccess("rectangular detail view created", new { view_id = DId(view), scale = scale, associative_boundary = false, api = "UF_DRAW_add_detail_view" });
    }
    private static object DSectionView(Dictionary<string, object> p)
    {
        DrawingSheet sheet = DSheet(); DraftingView parent = DView(p); bool horizontal = DS(p, "cut_direction", "vertical") == "horizontal";
        UFDrf.Object cut = new UFDrf.Object(); cut.object_view_tag = parent.Tag; cut.object_assoc_type = (UFDrf.AssocType)5; cut.assoc_dwg_pos = new double[] { DN(p, "cut_x", 200), DN(p, "cut_y", 150) };
        double scale = DN(p, "scale", 1); if (scale <= 0) throw new Exception("scale must be positive");
        Tag tag; UFSession.GetUFSession().Draw.CreateSimpleSxview(sheet.Tag, scale, horizontal ? new double[] { 1, 0, 0 } : new double[] { 0, 1, 0 }, horizontal ? new double[] { 0, -1, 0 } : new double[] { 1, 0, 0 }, parent.Tag, ref cut, new double[] { DN(p, "x", 320), DN(p, "y", 150) }, out tag);
        DraftingView view = DFindView(tag); view.SetName(DS(p, "name", "MCP_SECTION")); DUpdate(sheet); bool isSection; UFSession.GetUFSession().Draw.IsSxview(tag, out isSection); if (!isSection) throw new Exception("created view did not read back as a section"); Point3d actual = view.GetDrawingReferencePoint();
        return DSuccess("simple section view created; native parent alignment retained", new { view_id = DId(view), is_section = isSection, cut_direction = horizontal ? "horizontal" : "vertical", requested_origin = new double[] { DN(p, "x", 320), DN(p, "y", 150) }, actual_origin = new double[] { actual.X, actual.Y }, placement_rule = "native orthographic alignment to parent; choose the projected coordinate to avoid overlap", exact_requested_placement = Math.Abs(actual.X - DN(p, "x", 320)) < 1e-6 && Math.Abs(actual.Y - DN(p, "y", 150)) < 1e-6 });
    }
    private static object DGdt(Dictionary<string, object> p)
    {
        string characteristic = DS(p, "characteristic", "");
        if (!string.IsNullOrEmpty(characteristic)) return DTypedGdt(p, characteristic);
        DrawingSheet sheet = DSheet(); object[] raw = DA(p, "lines"); if (raw.Length == 0 || raw.Length > 10) throw new Exception("1..10 NX GD&T text lines required"); string[] lines = Array.ConvertAll(raw, delegate(object o) { return Convert.ToString(o, CultureInfo.InvariantCulture); });
        UFDrf.Object obj = new UFDrf.Object(); obj.assoc_dwg_pos = new double[2]; Tag tag; UFSession.GetUFSession().Drf.CreateGdtSymbol(lines.Length, lines, new double[] { DN(p, "x", 300), DN(p, "y", 70), 0 }, (UFDrf.LeaderType)1, (UFDrf.LeaderAttachType)2, ref obj, new double[3], (UFDrf.FrameCorner)0, out tag);
        DUpdate(sheet); return DSuccess("native GD&T symbol created; caller supplies NX formatted text", new { annotation_id = DId(DObject(tag)), lines = lines, semantic_validation = "raw_NX_symbol_text_requires_drawing_review" });
    }
    private static object DTypedGdt(Dictionary<string, object> p, string characteristic)
    {
        DrawingSheet sheet = DSheet(); string key = characteristic.ToLowerInvariant();
        if (key != "flatness" && key != "straightness" && key != "circularity" && key != "cylindricity") throw new Exception("typed mode currently supports form tolerances without datum references only");
        FeatureControlFrameBuilder.FcfCharacteristic kind = (FeatureControlFrameBuilder.FcfCharacteristic)Enum.Parse(typeof(FeatureControlFrameBuilder.FcfCharacteristic), characteristic, true);
        double value = DN(p, "tolerance", 0); if (value <= 0) throw new Exception("positive form tolerance required");
        DraftingFeatureControlFrameBuilder builder = _part.Annotations.CreateDraftingFeatureControlFrameBuilder(null); Gdt created;
        try
        {
            builder.Characteristic = kind; builder.FrameStyle = FeatureControlFrameBuilder.FcfFrameStyle.SingleFrame;
            FeatureControlFrameDataBuilder[] frames = builder.FeatureControlFrameDataList.GetContents();
            FeatureControlFrameDataBuilder first;
            if (frames.Length < 1)
            {
                first = _part.Annotations.CreateFeatureControlFrameDataBuilder(null);
                builder.FeatureControlFrameDataList.Append(first);
            }
            else first = frames[0];
            first.ToleranceValue = value.ToString("0.########", CultureInfo.InvariantCulture);
            builder.Origin.OriginPoint = new Point3d(DN(p, "x", 300), DN(p, "y", 70), 0);
            builder.Origin.Anchor = OriginBuilder.AlignmentPosition.TopLeft;
            created = (Gdt)builder.Commit();
        }
        finally { builder.Destroy(); }
        DraftingFeatureControlFrameBuilder reader = _part.Annotations.CreateDraftingFeatureControlFrameBuilder(created);
        string actualKind, actualTolerance;
        try { FeatureControlFrameDataBuilder[] readFrames = reader.FeatureControlFrameDataList.GetContents(); if (readFrames.Length < 1) throw new Exception("persisted GD&T has no native frame data"); actualKind = reader.Characteristic.ToString(); actualTolerance = readFrames[0].ToleranceValue; }
        finally { reader.Destroy(); }
        if (!string.Equals(actualKind, kind.ToString(), StringComparison.Ordinal) || Math.Abs(double.Parse(actualTolerance, CultureInfo.InvariantCulture) - value) > 1e-8) throw new Exception("native GD&T readback mismatch");
        DUpdate(sheet); return DSuccess("native form tolerance created and semantic fields read back", new { annotation_id = DId(created), characteristic = actualKind, tolerance = actualTolerance, semantic_validation = "native_builder_readback", datum_references = "none", associative_geometry = false });
    }
    private static object DBalloon(Dictionary<string, object> p)
    {
        DrawingSheet sheet = DSheet(); UFDrf.Object obj = new UFDrf.Object(); obj.assoc_dwg_pos = new double[2]; Tag tag; string text = DS(p, "text", "1");
        bool leader = p.ContainsKey("leader_x") && p.ContainsKey("leader_y") && p["leader_x"] != null && p["leader_y"] != null;
        UFSession.GetUFSession().Drf.CreateIdSymbol((UFDrf.IdSymbolType)1, text, "", new double[] { DN(p, "x", 300), DN(p, "y", 170), 0 }, (UFDrf.LeaderMode)(leader ? 2 : 1), (UFDrf.LeaderAttachType)2, ref obj, new double[] { DN(p, "leader_x", 0), DN(p, "leader_y", 0), 0 }, out tag);
        DUpdate(sheet); return DSuccess("native numbered balloon created", new { annotation_id = DId(DObject(tag)), text = text, associative_component = false, attachment = leader ? "sheet_point" : "none" });
    }
    private static object DExportCad(Dictionary<string, object> p)
    {
        DrawingSheet sheet = DSheet(); string path = DS(p, "path", ""), format = DS(p, "format", "dxf").ToLowerInvariant();
        if (format != "dxf" && format != "dwg") throw new Exception("format must be dxf or dwg");
        string boundary = Path.GetFullPath(Environment.GetEnvironmentVariable("NX_MCP_WORKSPACE")).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        path = Path.GetFullPath(path); if (!path.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new Exception("export path escapes MCP workspace");
        if (File.Exists(path) || Directory.Exists(path)) throw new Exception("refusing to overwrite existing CAD export");
        string want = DS(p, "sheet_name", sheet.Name); if (string.IsNullOrEmpty(want)) want = sheet.Name; DrawingSheet target = null; foreach (DrawingSheet candidate in _part.DrawingSheets) if (candidate.Name == want) target = candidate;
        if (target == null) throw new Exception("requested drawing sheet not found"); target.Open(); DUpdate(target);
        DxfdwgCreator creator = _session.DexManager.CreateDxfdwgCreator();
        try { creator.SettingsFile = Path.Combine(Environment.GetEnvironmentVariable("UGII_BASE_DIR"), "DXFDWG", "dxfdwg.def"); creator.ProcessHoldFlag = true; creator.OutputFile = path; creator.OutputFileType = format == "dxf" ? DxfdwgCreator.OutputFileTypeOption.Dxf : DxfdwgCreator.OutputFileTypeOption.Dwg; creator.OutputTo = DxfdwgCreator.OutputToOption.Drafting; creator.ExportFrom = DxfdwgCreator.ExportFromOption.DisplayPart; creator.ExportAs = DxfdwgCreator.ExportAsOption.TwoD; creator.ExportData = DxfdwgCreator.ExportDataOption.Drawing; creator.DrawingList = want; creator.InputFile = _part.FullPath; creator.FileSaveFlag = false; creator.Commit(); }
        finally { creator.Destroy(); }
        if (!File.Exists(path) || new FileInfo(path).Length < 16) throw new Exception("CAD translator completed without a valid nonempty output; translator logs preserved; no success claimed");
        string header; using (FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { byte[] bytes = new byte[Math.Min(2048, checked((int)input.Length))]; input.Read(bytes, 0, bytes.Length); header = Encoding.ASCII.GetString(bytes); }
        bool valid = format == "dwg" ? header.StartsWith("AC10", StringComparison.Ordinal) : header.Contains("SECTION") || header.StartsWith("AutoCAD Binary DXF", StringComparison.Ordinal);
        if (!valid) throw new Exception("CAD file header invalid; output preserved for diagnosis");
        return DSuccess("CAD export completed and file header verified", new { path = path, format = format, bytes = new FileInfo(path).Length, header_verified = true });
    }
}
