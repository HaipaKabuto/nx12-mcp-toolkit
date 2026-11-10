// Publication copy modified by repository maintainer on 2026-11-10.
// Original authors and licenses: ../../docs/ATTRIBUTION.md.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using NXOpen;
using NXOpen.Assemblies;
using NXOpen.Positioning;

public static partial class NX_MCP_Loader
{
    // Every assembly command uses one base64 UTF-8 JSON argument. No journal/code execution.
    private static readonly JavaScriptSerializer AJson = new JavaScriptSerializer();
    private static Dictionary<string, object> APayload(string[] parts)
    {
        if (parts.Length != 2) throw new Exception("assembly command requires one base64 JSON payload");
        return AJson.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])));
    }
    private static string AString(Dictionary<string, object> p, string key, string fallback)
    { object v; return p.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : fallback; }
    private static double ANumber(Dictionary<string, object> p, string key, double fallback)
    { object v; double x = p.TryGetValue(key, out v) && v != null ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : fallback; if(double.IsNaN(x)||double.IsInfinity(x))throw new Exception(key+" must be finite");return x; }
    private static bool ABool(Dictionary<string, object> p, string key, bool fallback)
    { object v; return p.TryGetValue(key, out v) && v != null ? Convert.ToBoolean(v, CultureInfo.InvariantCulture) : fallback; }
    private static Dictionary<string,object> ADict(params object[] kv)
    { var d=new Dictionary<string,object>();for(int i=0;i<kv.Length;i+=2)d[(string)kv[i]]=kv[i+1];return d; }
    private static string AResponse(Dictionary<string,object> d)
    { d["ok"]=true;return AJson.Serialize(d); }
    private static string APath(string raw, bool mustExist)
    {
        string workspace=Environment.GetEnvironmentVariable("NX_MCP_WORKSPACE");
        if(string.IsNullOrEmpty(workspace))throw new Exception("NX_MCP_WORKSPACE is required");
        string boundary=Path.GetFullPath(workspace).TrimEnd('\\','/')+Path.DirectorySeparatorChar;
        string full=Path.GetFullPath(Path.IsPathRooted(raw)?raw:Path.Combine(workspace,raw));
        if(!full.StartsWith(boundary,StringComparison.OrdinalIgnoreCase))throw new Exception("assembly path is outside NX_MCP_WORKSPACE");
        if(!string.Equals(Path.GetExtension(full),".prt",StringComparison.OrdinalIgnoreCase))throw new Exception("assembly operations accept .prt files only");
        if(mustExist&&!File.Exists(full))throw new Exception("part file does not exist: "+full);
        string check=File.Exists(full)?full:Path.GetDirectoryName(full);
        while(!string.IsNullOrEmpty(check)&&check.Length>=boundary.TrimEnd('\\').Length)
        {
            if((File.Exists(check)||Directory.Exists(check))&&(File.GetAttributes(check)&FileAttributes.ReparsePoint)!=0)
                throw new Exception("assembly paths through reparse points are not allowed");
            check=Path.GetDirectoryName(check);
        }
        return full;
    }
    private static void AGuard()
    {
        _session=Session.GetSession();
        if(_part==null||_session.Parts.Work==null||_part.Tag!=_session.Parts.Work.Tag)throw new Exception("active NX work part does not match MCP task part");
        APath(_part.FullPath,false);
    }
    private static Part APrototype(Component component)
    {
        Part p = component.Prototype as Part;
        if (p == null)
        {
            // OpenDisplay can restore an assembly with unloaded prototypes.
            // Load the actual occurrence without retaining changed load preferences.
            LoadOptions options = _session.Parts.LoadOptions;
            bool oldPartial = options.UsePartialLoading;
            bool oldLightweight = options.UseLightweightRepresentations;
            PartLoadStatus status = null;
            try
            {
                options.UsePartialLoading = false;
                options.UseLightweightRepresentations = false;
                ComponentAssembly.OpenComponentStatus[] opened;
                status = _part.ComponentAssembly.OpenComponents(ComponentAssembly.OpenOption.WholeAssembly, new Component[] { component }, out opened);
                if (status != null && status.NumberUnloadedParts > 0) throw new Exception("one or more assembly prototypes could not be loaded: " + component.Name);
                if (opened != null) foreach (ComponentAssembly.OpenComponentStatus entry in opened)
                    if (entry != ComponentAssembly.OpenComponentStatus.SuccessfullyOpened) throw new Exception("component open status " + entry + ": " + component.Name);
            }
            finally
            {
                if (status != null) status.Dispose();
                options.UsePartialLoading = oldPartial;
                options.UseLightweightRepresentations = oldLightweight;
            }
            p = component.Prototype as Part;
        }
        if (p == null) throw new Exception("prototype remains unloaded after explicit NX12 component open: " + component.Name);
        if (!p.IsFullyLoaded)
        {
            PartLoadStatus status = null;
            try { status = p.LoadFully(); if (status != null && status.NumberUnloadedParts > 0) throw new Exception("prototype full load incomplete: " + p.FullPath); }
            finally { if (status != null) status.Dispose(); }
        }
        return p;
    }
    private static void ACheckTree(Part part, HashSet<Tag> seen)
    {
        if(!seen.Add(part.Tag))return;APath(part.FullPath,false);
        Component root=part.ComponentAssembly.RootComponent;
        if(root!=null)foreach(Component c in root.GetChildren())ACheckTree(APrototype(c),seen);
    }
    private static Part ALoad(string path)
    {
        foreach(BasePart p in _session.Parts.ToArray())
            if(!string.IsNullOrEmpty(p.FullPath)&&string.Equals(Path.GetFullPath(p.FullPath),path,StringComparison.OrdinalIgnoreCase))
            {Part loaded=p as Part;if(loaded!=null)return loaded;}
        PartLoadStatus status=null;
        try{return _session.Parts.OpenBase(path,out status) as Part;}
        finally{if(status!=null)status.Dispose();}
    }
    private static bool AContainsPart(Part part, Tag target, HashSet<Tag> seen)
    {
        if(part.Tag==target)return true;if(!seen.Add(part.Tag))return false;
        Component root=part.ComponentAssembly.RootComponent;
        if(root!=null)foreach(Component c in root.GetChildren())if(AContainsPart(APrototype(c),target,seen))return true;
        return false;
    }
    private static Component AComponent(string id)
    {
        if(string.IsNullOrEmpty(id)||id=="/")throw new Exception("component_id must identify a child, not assembly root");
        Component root=_part.ComponentAssembly.RootComponent;
        if(root==null)throw new Exception("active part has no components");
        Component current=root;
        foreach(string token in id.Trim('/').Split('/'))
        {
            Component hit=null;foreach(Component c in current.GetChildren())if(c.Name==token){if(hit!=null)throw new Exception("ambiguous component name path: "+id);hit=c;}
            if(hit==null)throw new Exception("component not found: "+id);current=hit;
        }
        return current;
    }
    private static string AId(Component c)
    {
        var names=new List<string>();Component t=c;
        while(t.Parent!=null){names.Insert(0,t.Name);t=t.Parent;}
        return "/"+string.Join("/",names.ToArray());
    }
    private static double[] AValues(Dictionary<string,object> p,string key,int count,double[] fallback)
    {
        object v;if(!p.TryGetValue(key,out v)||v==null)return fallback;
        IList items=v as IList;if(items==null||items.Count!=count)throw new Exception(key+" requires "+count+" numbers");
        var r=new double[count];for(int i=0;i<count;i++){r[i]=Convert.ToDouble(items[i],CultureInfo.InvariantCulture);if(double.IsNaN(r[i])||double.IsInfinity(r[i]))throw new Exception(key+" requires finite values");}return r;
    }
    private static double[] AIdentity(){return new double[]{1,0,0,0,1,0,0,0,1};}
    private static Matrix3x3 AMatrix(double[] v)
    {
        // NXOpen assembly matrices are COLUMN order: each group is an axis in parent coordinates.
        for(int i=0;i<3;i++)for(int j=0;j<3;j++)
        {double dot=0;for(int k=0;k<3;k++)dot+=v[i*3+k]*v[j*3+k];if(Math.Abs(dot-(i==j?1:0))>1e-7)throw new Exception("rotation must be orthonormal");}
        double det=v[0]*(v[4]*v[8]-v[5]*v[7])-v[3]*(v[1]*v[8]-v[2]*v[7])+v[6]*(v[1]*v[5]-v[2]*v[4]);
        if(Math.Abs(det-1)>1e-7)throw new Exception("rotation must have determinant +1");
        Matrix3x3 m=new Matrix3x3();m.Xx=v[0];m.Xy=v[1];m.Xz=v[2];m.Yx=v[3];m.Yy=v[4];m.Yz=v[5];m.Zx=v[6];m.Zy=v[7];m.Zz=v[8];return m;
    }
    private static double[] AArray(Matrix3x3 m){return new double[]{m.Xx,m.Xy,m.Xz,m.Yx,m.Yy,m.Yz,m.Zx,m.Zy,m.Zz};}
    private static double[] AMultiply(double[] a,double[] b)
    {var r=new double[9];for(int c=0;c<3;c++)for(int row=0;row<3;row++)for(int k=0;k<3;k++)r[c*3+row]+=a[k*3+row]*b[c*3+k];return r;}
    private static double[] ATranspose(double[] v){return new double[]{v[0],v[3],v[6],v[1],v[4],v[7],v[2],v[5],v[8]};}
    private static Dictionary<string,object> ADegreesOfFreedom(Component c)
    {
        try
        {
            // Available since NX6; this reports actual remaining freedom. A
            // solved constraint alone does not mean the component is fully fixed.
            DegreesOfFreedom freedom=c.GetDegreesOfFreedom();
            bool known=freedom.Result==DegreesOfFreedomResult.Success;
            object fullyConstrained=known?(object)(freedom.NumRotational==0&&freedom.NumTranslational==0
                &&freedom.NumInstantaneousRotational==0&&freedom.NumInstantaneousTranslational==0):null;
            return ADict("result",freedom.Result.ToString(),"rotational",freedom.NumRotational,"translational",freedom.NumTranslational,
                "instantaneous_rotational",freedom.NumInstantaneousRotational,"instantaneous_translational",freedom.NumInstantaneousTranslational,
                "fully_constrained",fullyConstrained,"method","NXOpen.Assemblies.Component.GetDegreesOfFreedom");
        }
        catch(Exception e){return ADict("result","unavailable","fully_constrained",null,"error",e.Message);}
    }
    private static Dictionary<string,object> AComponentInfo(Component c)
    {
        Point3d p;Matrix3x3 r;c.GetPosition(out p,out r);Part prototype=APrototype(c);
        return ADict("id",AId(c),"name",c.Name,"journal_id",c.JournalIdentifier,"prototype",prototype.FullPath,"position",new double[]{p.X,p.Y,p.Z},"rotation",AArray(r),"coordinate_system","owning_part_mm_NX_column_order","coordinate_part",c.OwningPart.FullPath,"children",c.GetChildren().Length,"is_suppressed",c.IsSuppressed,"degrees_of_freedom",ADegreesOfFreedom(c));
    }
    private static void AWalk(Component parent,List<object> items)
    {foreach(Component c in parent.GetChildren()){items.Add(AComponentInfo(c));AWalk(c,items);}}
    private static Dictionary<string,object> AList()
    {
        var items=new List<object>();Component root=_part.ComponentAssembly.RootComponent;if(root!=null)AWalk(root,items);
        return ADict("part",_part.FullPath,"is_assembly",root!=null,"component_count",items.Count,"components",items,"length_unit","mm");
    }
    private static Part AOwner(Component component)
    {
        // Nested occurrences must be edited through the direct owner prototype, not the displayed root.
        Component parent=component.Parent;if(parent==null)throw new Exception("cannot edit assembly root");
        Part p=parent.Parent==null?_part:APrototype(parent);APath(p.FullPath,false);return p;
    }
    private static Component ADirect(Component occurrence)
    {
        Part owner=AOwner(occurrence);Component root=owner.ComponentAssembly.RootComponent;
        if(root==null)throw new Exception("direct owner has no root");
        Component match=null;foreach(Component c in root.GetChildren())if(c.Name==occurrence.Name){if(match!=null)throw new Exception("ambiguous direct owner component name");match=c;}
        if(match==null)throw new Exception("direct owner component not found");return match;
    }
    private static Dictionary<string,object> AAdd(Dictionary<string,object> p)
    {
        string path=APath(AString(p,"path",""),true);string name=AString(p,"name","");
        if(string.IsNullOrWhiteSpace(name)||name.IndexOf('/')>=0||name.IndexOf('\\')>=0||name.Length>80)throw new Exception("name must be unique, 1-80 characters, no slashes");
        string parentId=AString(p,"parent_id","/");Part owner=parentId=="/"||parentId==""?_part:APrototype(AComponent(parentId));
        APath(owner.FullPath,false);Component root=owner.ComponentAssembly.RootComponent;
        if(root!=null)foreach(Component c in root.GetChildren())if(c.Name==name)throw new Exception("component name already exists under parent; no component inserted");
        Part loaded=ALoad(path);
        if(loaded==null)throw new Exception("component could not be fully loaded");
        ACheckTree(loaded,new HashSet<Tag>());
        if(AContainsPart(loaded,owner.Tag,new HashSet<Tag>()))throw new Exception("insertion would create a cyclic assembly reference");
        var pos=AValues(p,"position",3,new double[]{0,0,0});var matrix=AMatrix(AValues(p,"rotation",9,AIdentity()));
        PartLoadStatus status=null;Component component=null;
        try{component=owner.ComponentAssembly.AddComponent(path,"Entire Part",name,new Point3d(pos[0],pos[1],pos[2]),matrix,-1,out status);if(status!=null&&status.NumberUnloadedParts>0)throw new Exception("component load was incomplete");}
        finally{if(status!=null)status.Dispose();}
        if(component==null)throw new Exception("NX returned no inserted component");
        string insertedId=(parentId=="/"||parentId==""?"":parentId.TrimEnd('/'))+"/"+name;
        var info=AComponentInfo(component);info["id"]=insertedId;
        return ADict("component",info,"parent_id",parentId,"component_id",insertedId);
    }
    private static Dictionary<string,object> ATransform(Dictionary<string,object> p)
    {
        Component occurrence=AComponent(AString(p,"component_id",""));Component c=ADirect(occurrence);Part owner=AOwner(occurrence);
        if(c.GetConstraints().Length>0)throw new Exception("component has assembly constraints; remove constraints or use nx_assembly_solve rather than bypassing them");
        Point3d oldP;Matrix3x3 oldR;c.GetPosition(out oldP,out oldR);double[] old=AArray(oldR);
        bool relative=ABool(p,"relative",false);double[] position=AValues(p,"position",3,relative?new double[]{0,0,0}:new double[]{oldP.X,oldP.Y,oldP.Z});
        double[] requested=AValues(p,"rotation",9,relative?AIdentity():old);AMatrix(requested);
        double[] target=relative?AMultiply(requested,old):requested;
        double[] delta=relative?requested:AMultiply(target,ATranspose(old));
        double tx=relative?oldP.X+position[0]:position[0],ty=relative?oldP.Y+position[1]:position[1],tz=relative?oldP.Z+position[2]:position[2];
        // MoveComponent rotates both orientation AND component origin about the
        // assembly origin before applying translation (verified in real NX12).
        double rotatedX=delta[0]*oldP.X+delta[3]*oldP.Y+delta[6]*oldP.Z;
        double rotatedY=delta[1]*oldP.X+delta[4]*oldP.Y+delta[7]*oldP.Z;
        double rotatedZ=delta[2]*oldP.X+delta[5]*oldP.Y+delta[8]*oldP.Z;
        owner.ComponentAssembly.MoveComponent(c,new Vector3d(tx-rotatedX,ty-rotatedY,tz-rotatedZ),AMatrix(delta));
        Point3d actual;Matrix3x3 actualR;c.GetPosition(out actual,out actualR);double[] arr=AArray(actualR);
        if(Math.Abs(actual.X-tx)>1e-6||Math.Abs(actual.Y-ty)>1e-6||Math.Abs(actual.Z-tz)>1e-6)throw new Exception("component position readback does not match requested transform");
        for(int i=0;i<9;i++)if(Math.Abs(arr[i]-target[i])>1e-6)throw new Exception("component rotation readback does not match requested transform");
        return ADict("component_id",AString(p,"component_id",""),"component",AComponentInfo(c),"verified",true);
    }
    private static Dictionary<string,object> AReplace(Dictionary<string,object> p)
    {
        Component occurrence=AComponent(AString(p,"component_id",""));Component c=ADirect(occurrence);Part owner=AOwner(occurrence);
        string path=APath(AString(p,"path",""),true);Part loaded=ALoad(path);
        if(loaded==null)throw new Exception("replacement part could not be loaded");ACheckTree(loaded,new HashSet<Tag>());
        if(AContainsPart(loaded,owner.Tag,new HashSet<Tag>()))throw new Exception("replacement would create a cyclic assembly reference");
        NXOpen.Assemblies.ReplaceComponentBuilder builder=owner.AssemblyManager.CreateReplaceComponentBuilder();
        PartLoadStatus status=null;
        try
        {
            builder.ComponentsToReplace.Add(c);builder.ReplacementPart=path;builder.ReplaceAllOccurrences=false;builder.MaintainRelationships=true;
            builder.ComponentNameType=NXOpen.Assemblies.ReplaceComponentBuilder.ComponentNameOption.Maintain;
            builder.SetComponentReferenceSetType(NXOpen.Assemblies.ReplaceComponentBuilder.ComponentReferenceSet.EntirePart,"");
            status=builder.RegisterReplacePartLoadStatus();builder.Commit();
            if(status!=null&&status.NumberUnloadedParts>0)throw new Exception("replacement part load was incomplete");
        }
        finally{if(status!=null)status.Dispose();builder.Destroy();}
        Component current=ADirect(AComponent(AString(p,"component_id","")));
        if(!string.Equals(Path.GetFullPath(APrototype(current).FullPath),path,StringComparison.OrdinalIgnoreCase))throw new Exception("replacement prototype readback did not match requested file");
        return ADict("component",AComponentInfo(current),"component_id",AString(p,"component_id",""),"verified",true);
    }
    private static List<Body> ABodies(Component c)
    {
        Part prototype=APrototype(c);var bodies=new List<Body>();
        foreach(Body body in prototype.Bodies)if(body.IsSolidBody)
        {Body occurrence=c.FindOccurrence(body) as Body;if(occurrence==null)throw new Exception("solid body occurrence unavailable: "+AId(c));bodies.Add(occurrence);}
        return bodies;
    }
    private static List<Face> AFaces(Component c)
    {var faces=new List<Face>();foreach(Body body in ABodies(c))faces.AddRange(body.GetFaces());return faces;}
    private static Dictionary<string,object> AGeometry(Dictionary<string,object> p)
    {
        Component c=AComponent(AString(p,"component_id",""));var faces=AFaces(c);var result=new List<object>();var uf=NXOpen.UF.UFSession.GetUFSession();
        for(int i=0;i<faces.Count;i++)
        {
            Face f=faces[i];int type,norm;double[] point=new double[3],axis=new double[3],box=new double[6];double radius,radData;
            uf.Modl.AskFaceData(f.Tag,out type,point,axis,box,out radius,out radData,out norm);
            result.Add(ADict("index",i,"journal_id",f.JournalIdentifier,"face_type",f.SolidFaceType.ToString(),"surface_type_code",type,"reference_point",point,"axis",axis,"radius",radius,"normal_sense",norm,"bbox",box));
        }
        return ADict("component",AComponentInfo(c),"body_count",ABodies(c).Count,"faces",result,"coordinate_system","assembly_occurrence_ABS_mm","face_index_scope","flattened occurrence bodies then faces; reread after geometry change");
    }
    private static Dictionary<string,object> AConstraintInfo(Constraint c)
    {
        var references=new List<object>();foreach(ConstraintReference r in c.GetReferences())
        {NXObject movable=r.GetMovableObject();NXObject geometry=r.GetGeometry();references.Add(ADict("movable",movable==null?null:movable.JournalIdentifier,"geometry",geometry==null?null:geometry.JournalIdentifier,"uses_axis",r.GetUsesGeometryAxis()));}
        string expression=null;if(c.ConstraintType==Constraint.Type.Distance||c.ConstraintType==Constraint.Type.Angle)if(c.Expression!=null)expression=c.Expression.RightHandSide;
        bool allAxes=references.Count==2;
        foreach(ConstraintReference r in c.GetReferences())allAxes=allAxes&&r.GetUsesGeometryAxis();
        string semanticKind=c.ConstraintType==Constraint.Type.Touch&&allAxes?"coaxial":c.ConstraintType.ToString().ToLowerInvariant();
        return ADict("id",c.JournalIdentifier,"name",c.Name,"kind",c.ConstraintType.ToString(),"semantic_kind",semanticKind,"solver_status",c.GetConstraintStatus().ToString(),"alignment",c.ConstraintAlignment.ToString(),"expression",expression,"references",references);
    }
    private static Dictionary<string,object> AConstraints()
    {var results=new List<object>();foreach(Constraint c in _part.ComponentAssembly.Positioner.Constraints)results.Add(AConstraintInfo(c));return ADict("constraints",results,"count",results.Count,"owner",_part.FullPath);}
    private static Dictionary<string,object> ASolveNetwork(ComponentPositioner positioner)
    {
        Network network=null;
        try
        {
            network=positioner.EstablishNetwork();network.MoveObjectsState=true;network.NonMovingGroupGrounded=false;
            foreach(Constraint c in positioner.Constraints)network.AddConstraint(c);
            Component root=_part.ComponentAssembly.RootComponent;
            if(root!=null)foreach(Component c in root.GetChildren())network.AddMovableObject(c);
            if(!network.IsReferencedGeometryLoaded())network.LoadReferencedGeometry();
            network.Solve();network.ApplyToModel();
            var components=new List<object>();if(root!=null)foreach(Component c in root.GetChildren())components.Add(ADict("component_id",AId(c),"solver_status",network.GetMovableObjectStatus(c).ToString()));
            foreach(Constraint c in positioner.Constraints)
            {
                string status=c.GetConstraintStatus().ToString();
                if(status!="Solved"&&status!="BetweenFixed"&&status!="Suppressed"&&status!="IgnoredInArrangement")throw new Exception("constraint solver did not succeed: "+c.Name+"="+status);
            }
            return ADict("component_statuses",components,"constraints",AConstraints());
        }
        finally{if(network!=null)positioner.ClearNetwork();}
    }
    private static NXObject AConstraintGeometry(Face face, bool axis)
    {
        if (!axis || face.SolidFaceType == Face.FaceType.Cylindrical) return face;
        // NX12 represents extruded circular walls as Swept on this installation.
        // A genuine full circular rim provides a documented concentric reference.
        foreach (Edge edge in face.GetEdges())
        {
            GEdge geometry = GReadEdge(edge, NXOpen.UF.UFSession.GetUFSession());
            if (geometry.AnalyticType == "circle" && geometry.FullPeriod) return edge;
        }
        throw new Exception("coaxial reference needs a cylindrical face or a verified full circular rim");
    }
    private static Dictionary<string,object> ACreateConstraint(Dictionary<string,object> p)
    {
        string kind=AString(p,"kind","").ToLowerInvariant();Component a=AComponent(AString(p,"component_a",""));Component b=kind=="fix"?null:AComponent(AString(p,"component_b",""));
        if(a.Parent==null||a.Parent.Parent!=null||(b!=null&&(b.Parent==null||b.Parent.Parent!=null)))throw new Exception("constraints currently require direct children of active assembly; create constraints inside a subassembly before inserting it");
        var positioner=_part.ComponentAssembly.Positioner;positioner.BeginAssemblyConstraints();Constraint constraint=null;
        try
        {
            constraint=positioner.CreateConstraint(true);
            if(kind=="fix")constraint.ConstraintType=Constraint.Type.Fix;
            // Touch between two axis references means coincident axes. Concentric
            // instead additionally makes the circular rim planes coincide (NX12 SDK),
            // which overconstrains shafts with a separately specified axial location.
            else if(kind=="coaxial")constraint.ConstraintType=Constraint.Type.Touch;
            else if(kind=="touch")constraint.ConstraintType=Constraint.Type.Touch;
            else if(kind=="distance")constraint.ConstraintType=Constraint.Type.Distance;
            else if(kind=="angle")constraint.ConstraintType=Constraint.Type.Angle;
            else throw new Exception("kind must be fix/coaxial/touch/distance/angle");
            string name=AString(p,"name","");if(!string.IsNullOrEmpty(name)){foreach(Constraint existing in positioner.Constraints)if(existing.Tag!=constraint.Tag&&existing.Name==name)throw new Exception("constraint name already exists");constraint.SetName(name);}
            string alignment=AString(p,"alignment","infer");
            if(alignment!="same"&&alignment!="opposite"&&alignment!="infer")throw new Exception("alignment must be infer/same/opposite");
            constraint.ConstraintAlignment=alignment=="same"?Constraint.Alignment.CoAlign:alignment=="opposite"?Constraint.Alignment.ContraAlign:Constraint.Alignment.InferAlign;
            if(kind=="fix")constraint.CreateConstraintReference(a,a,false,false);
            else
            {
                var fa=AFaces(a);var fb=AFaces(b);int ia=(int)ANumber(p,"face_a",-1),ib=(int)ANumber(p,"face_b",-1);
                if(ia<0||ia>=fa.Count||ib<0||ib>=fb.Count)throw new Exception("face index invalid; call nx_assembly_component_geometry first");
                bool axis=kind=="coaxial";
                NXObject referenceA = AConstraintGeometry(fa[ia], axis);
                NXObject referenceB = AConstraintGeometry(fb[ib], axis);
                constraint.CreateConstraintReference(a,referenceA,axis,false);constraint.CreateConstraintReference(b,referenceB,axis,false);
                if(kind=="distance"||kind=="angle")constraint.SetExpression(ANumber(p,"value",0).ToString("R",CultureInfo.InvariantCulture));
            }
            var solve=ASolveNetwork(positioner);
            return ADict("constraint",AConstraintInfo(constraint),"solve",solve);
        }
        finally{positioner.EndAssemblyConstraints();}
    }
    private static Dictionary<string,object> AInterference(Dictionary<string,object> p)
    {
        var selected=new List<Component>();var unique=new HashSet<Tag>();object request;
        if(p.TryGetValue("component_ids",out request)&&request!=null)
        {
            IList ids=request as IList;if(ids==null)throw new Exception("component_ids must be an array");
            foreach(object id in ids)
            {
                Component chosen=AComponent(Convert.ToString(id,CultureInfo.InvariantCulture));
                if(chosen.GetChildren().Length!=0)throw new Exception("select leaf components for interference, or omit component_ids to check all assembly leaves");
                if(!unique.Add(chosen.Tag))throw new Exception("duplicate component in interference selection");
                selected.Add(chosen);
            }
        }
        else
        {
            Component root=_part.ComponentAssembly.RootComponent;if(root!=null)ACollectLeaves(root,selected);
        }
        if(selected.Count>100)throw new Exception("interference check limited to 100 leaf components per call");
        var results=new List<object>();int collisions=0,touching=0;bool includeTouching=ABool(p,"include_touching",false);
        var uf=NXOpen.UF.UFSession.GetUFSession();
        for(int i=0;i<selected.Count;i++)for(int j=i+1;j<selected.Count;j++)
        {
            var left=ABodies(selected[i]);var right=ABodies(selected[j]);bool collision=false,touch=false;int pairs=0;var rawStatuses=new List<int>();
            if(left.Count==0||right.Count==0)throw new Exception("selected leaf has no solid bodies; cannot certify absence of interference");
            foreach(Body a in left)foreach(Body b in right)
            {
                int[] status=new int[1];
                try{uf.Modl.CheckInterference(a.Tag,1,new Tag[]{b.Tag},status);}
                finally
                {
                    // uf_modl.h explicitly requires UF_MODL_update to free temporary
                    // intersection objects queued for deletion during the inquiry.
                    uf.Modl.Update();
                }
                pairs++;rawStatuses.Add(status[0]);
                // NX12 UGOPEN/uf_modl.h1513-1515: 1=interference, 2=none, 3=touching.
                if(status[0]==1)collision=true;
                else if(status[0]==3)touch=true;
                else if(status[0]!=2)throw new Exception("NX returned unknown interference status: "+status[0]);
            }
            if(collision)collisions++;else if(touch)touching++;
            results.Add(ADict("component_a",AId(selected[i]),"component_b",AId(selected[j]),"solid_interference",collision,"touching_only",!collision&&touch,"is_interference",collision||(includeTouching&&touch),"body_pairs_checked",pairs,"uf_statuses",rawStatuses));
        }
        return ADict("pairs",results,"pair_count",results.Count,"solid_interference_pairs",collisions,"touching_only_pairs",touching,"include_touching",includeTouching,"interference_pairs",collisions+(includeTouching?touching:0),"method","UF_MODL_check_interference: 1 solid interference, 2 none, 3 touching; UF_MODL_update cleanup");
    }
    private static void ACollectLeaves(Component parent,List<Component> result)
    {foreach(Component c in parent.GetChildren()){if(c.GetChildren().Length==0)result.Add(c);else ACollectLeaves(c,result);}}
    private static Dictionary<string,object> ASave()
    {
        ACheckTree(_part,new HashSet<Tag>());var saved=new List<string>();
        try{ASavePart(_part,new HashSet<Tag>(),saved);}
        catch(Exception e){throw new Exception("assembly save failed; already saved "+saved.Count+" part(s): "+string.Join("; ",saved.ToArray())+". Save is not an atomic multi-file transaction. "+e.Message,e);}
        return ADict("saved_parts",saved,"count",saved.Count);
    }
    private static void ASavePart(Part part,HashSet<Tag> seen,List<string> saved)
    {
        if(!seen.Add(part.Tag))return;Component root=part.ComponentAssembly.RootComponent;
        if(root!=null)foreach(Component c in root.GetChildren())ASavePart(APrototype(c),seen,saved);
        if(!NXOpen.UF.UFSession.GetUFSession().Part.IsModified(part.Tag)&&File.Exists(part.FullPath))return;
        part.SaveOptions.DrawingCgmData=false;PartSaveStatus status=null;
        try{status=part.Save(BasePart.SaveComponents.False,BasePart.CloseAfterSave.False);if(status!=null&&status.NumberUnsavedParts>0)throw new Exception("NX did not save every requested part: "+part.FullPath);}
        finally{if(status!=null)status.Dispose();}saved.Add(part.FullPath);
    }
    private static string DispatchAssembly(string[] parts)
    {
        string command=parts[0];bool mutation=command!="nx_assembly_list"&&command!="nx_assembly_constraints"&&command!="nx_assembly_component_geometry"&&command!="nx_assembly_interference"&&command!="nx_assembly_save"&&command!="nx_assembly_create";
        Session.UndoMarkId mark=default(Session.UndoMarkId);bool marked=false;
        try
        {
            var p=APayload(parts);
            if(command=="nx_assembly_create")
            {
                string path=APath(AString(p,"path",""),false);
                if(File.Exists(path)||Directory.Exists(path))throw new Exception("assembly target already exists; refusing overwrite");
                string response=CreatePart(new string[]{"nx_create_part",path});
                var created=AJson.Deserialize<Dictionary<string,object>>(response);object ok;if(!created.TryGetValue("ok",out ok)||!(bool)ok)return response;
                return AResponse(ADict("path",path,"assembly_container_created",true,"is_assembly",false,"component_count",0,"message","empty NX part created; NX assembly root materializes when first component is added"));
            }
            AGuard();
            if(mutation){ACheckTree(_part,new HashSet<Tag>());mark=_session.SetUndoMark(Session.MarkVisibility.Visible,"NX MCP "+command);marked=true;}
            Dictionary<string,object> result;
            if(command=="nx_assembly_add")result=AAdd(p);
            else if(command=="nx_assembly_list")result=AList();
            else if(command=="nx_assembly_transform")result=ATransform(p);
            else if(command=="nx_assembly_replace")result=AReplace(p);
            else if(command=="nx_assembly_remove")
            {Component c=ADirect(AComponent(AString(p,"component_id","")));_session.UpdateManager.AddToDeleteList(c);result=ADict("removed_component_id",AString(p,"component_id",""),"prototype_file_deleted",false);}
            else if(command=="nx_assembly_component_geometry")result=AGeometry(p);
            else if(command=="nx_assembly_constraint")result=ACreateConstraint(p);
            else if(command=="nx_assembly_constraints")result=AConstraints();
            else if(command=="nx_assembly_solve")
            {ComponentPositioner positioner=_part.ComponentAssembly.Positioner;positioner.BeginAssemblyConstraints();try{result=ASolveNetwork(positioner);}finally{positioner.EndAssemblyConstraints();}}
            else if(command=="nx_assembly_delete_constraint")
            {string id=AString(p,"constraint_id","");Constraint found=null;foreach(Constraint c in _part.ComponentAssembly.Positioner.Constraints)if(c.JournalIdentifier==id||c.Name==id){if(found!=null)throw new Exception("ambiguous constraint selector");found=c;}if(found==null)throw new Exception("constraint not found");_session.UpdateManager.AddToDeleteList(found);result=ADict("deleted_constraint",id);}
            else if(command=="nx_assembly_interference")result=AInterference(p);
            else if(command=="nx_assembly_save")result=ASave();
            else throw new Exception("unknown assembly command: "+command);
            if(marked){int errors=_session.UpdateManager.DoUpdate(mark);if(errors!=0)throw new Exception("NX assembly update reported "+errors+" error(s)");}
            result["operation"]=command;return AResponse(result);
        }
        catch(Exception e)
        {
            string rollback="not_needed";
            if(marked){try{_session.UndoToMark(mark,null);_session.DeleteUndoMark(mark,null);rollback="restored";}catch(Exception undo){rollback="FAILED: "+undo.Message;}}
            return AJson.Serialize(ADict("ok",false,"code","NX12_ASSEMBLY_FAILED","error",command+" failed: "+e.Message,"nx_code",e is NXException?(object)((NXException)e).ErrorCode:null,"details",ADict("command",command,"rollback",rollback)));
        }
    }
}
