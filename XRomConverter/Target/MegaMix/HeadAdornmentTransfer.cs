using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.Extensions;
using MikuMikuLibrary.Databases;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Objects;
using MikuMikuLibrary.Textures;
using ModelObject=MikuMikuLibrary.Objects.Object;

namespace XRomConverter.Target.MegaMix;

// Whole adornments and disconnected pieces of named head accessories/LEDs.
// Never select clothing merely because it has head weights. Welded pieces
// with any body influence or below the head cutoff remain for manual selection.
internal static class HeadAdornmentTransfer
{
    private static readonly Regex Accessory=new(@"head[ _-]?(phone|set|pin|acces)|hair[ _-]?(pin|clip|ornament|tie)|barrette|tiara|crown|helmet|goggle|glasses|earring|(?:^|[ _-])(hat|pin|clip)(?:$|[ _-])",RegexOptions.IgnoreCase);
    private static readonly Regex Light=new(@"light|led|glare",RegexOptions.IgnoreCase);
    // Audited outfits from the user's incomplete-accessory screenshots.
    // These names only enable inspection: the same disconnected-component,
    // head-weight and height checks still decide which faces may move.
    private static readonly Dictionary<string,string[]> AuditedMeshes=new(StringComparer.OrdinalIgnoreCase) {
        ["mikitm138_x"]=new[]{"acce_clothes_grey_MZ"},
        ["mikitm134_x"]=new[]{"main_main_white_MZ"},
        ["mikitm137_x"]=new[]{"jacketude_MZ"},
        ["mikitm136_x"]=new[]{"main_red_MZ"},
        ["mikitm143_x"]=new[]{"item_yellow_MZ"},
        ["mikitm144_x"]=new[]{"main_metal_bluegray_MZ","main_leather_bluegray_MZ"},
        ["lenitm107_x"]=new[]{"acce_clothes_gray_MZ"},
        ["lenitm111_x"]=new[]{"main_clothes_blue_MZ"},
        ["lenitm114_x"]=new[]{"main_enamel_blck_MZ","main_clothes_metal_MZ"},
        ["meiitm108_x"]=new[]{"main_clothes_metal_gray_MZ"},
        ["mikitm057_x"]=new[]{"polymsh_extracted"},
        ["mikitm060_x"]=new[]{"fril_MZ"},
        ["mikitm077_x"]=new[]{"outer_lace_alpha_MZ","lace_collar_MZ"},
        ["mikitm017_x"]=new[]{"arm_MZ","leg_MZ","dai_MZ"},
        ["mikitm020_x"]=new[]{"outer_MZ"},
        ["mikitm118_x"]=new[]{"other_MZ","flower_MZ"},
        ["mikitm023_x"]=new[]{"outer01_MZ"},
        ["mikitm040_x"]=new[]{"black_parts_MZ","white_parts_MZ"},
        ["mikitm045_x"]=new[]{"parts_MZ"},
        ["mikitm125_x"]=new[]{"main_MZ","acce_MZ"},
        ["mikitm079_x"]=new[]{"metal_MZ"},
        ["mikitm043_x"]=new[]{"heart_MZ"},
        ["rinitm017_x"]=new[]{"heart_MZ"},
        ["mikitm153_x"]=new[]{"touming_MZ","main_clothes_black_spoff_MZ"},
        ["rinitm022_x"]=new[]{"waist_MZ"},
        ["rinitm002_x"]=new[]{"metal_MZ"},
        ["rinitm011_x"]=new[]{"mic_MZ"},
        ["rinitm025_x"]=new[]{"fur_MZ","ribbon_MZ"},
        ["rinitm101_x"]=new[]{"ribon_MZ"},
        ["rinitm115_x"]=new[]{"main_clothes_white_MZ"},
        ["lukitm025_x"]=new[]{"acce_01_MZ"},
        ["lukitm010_x"]=new[]{"cloth_MZ"},
        ["lukitm103_x"]=new[]{"tie_MZ"},
        ["lenitm109_x"]=new[]{"main_clothes_yellow_MZ"},
        ["lukitm003_x"]=new[]{"coat_MZ"},
        ["lukitm009_x"]=new[]{"body_MZ"},
        ["lukitm023_x"]=new[]{"fur_cloth_MZ"},
        ["lukitm104_x"]=new[]{"acce_MZ","cloths_MZ","accealpha_MZ"},
        ["lukitm107_x"]=new[]{"acce_MZ"},
        ["lukitm108_x"]=new[]{"main_MZ","alpha_skirt_MZ"}
    };
    private sealed class Asset
    {
        public required string Name;
        public required ObjectSet Objects;
        public required TextureSet Textures;
        public bool Changed;
    }
    private sealed record Plan(Mesh Original,Mesh Transfer,List<SubMesh>? Remaining,bool PreserveRig=false);

    private static bool SameBind(Matrix4x4 a,Matrix4x4 b)
    {
        float[] Values(Matrix4x4 m)=>new[]{m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44};
        // The archive read/write cycle inverts these float matrices. Tiny
        // inversion noise is not a different rig; larger differences still skip.
        return Values(a).Zip(Values(b),(x,y)=>MathF.Abs(x-y)).All(d=>float.IsFinite(d)&&d<=0.000001f);
    }

    internal static IEnumerable<uint[]> Triangles(SubMesh sub)
    {
        if(sub.PrimitiveType==PrimitiveType.Triangles) {
            for(int i=0;i+2<sub.Indices.Length;i+=3) {
                uint a=sub.Indices[i],b=sub.Indices[i+1],c=sub.Indices[i+2];
                if(a!=b&&a!=c&&b!=c)yield return new[]{a,b,c};
            }
        } else if(sub.PrimitiveType==PrimitiveType.TriangleStrip) {
            uint a=uint.MaxValue,b=uint.MaxValue;int n=0;
            foreach(uint c in sub.Indices) {
                if(c==uint.MaxValue){a=b=uint.MaxValue;n=0;continue;}
                if(n>=2&&a!=b&&a!=c&&b!=c)yield return n%2==0?new[]{a,b,c}:new[]{b,a,c};
                a=b;b=c;n++;
            }
        } else throw new InvalidDataException("Unsupported primitive.");
    }
    private static T Clone<T>(T value) where T: new()
    {
        var clone=new T();
        foreach(var p in typeof(T).GetProperties().Where(p=>p.CanWrite)) {
            var v=p.GetValue(value);p.SetValue(clone,v is Array a?a.Clone():v);
        }
        return clone;
    }
    // Restart-delimited strips of three vertices retain winding and avoid
    // switching to Triangle primitives (the rejected build did that).
    private static SubMesh Faces(SubMesh original,IEnumerable<uint[]> faces)
    {
        var result=Clone(original);
        result.Indices=original.PrimitiveType==PrimitiveType.TriangleStrip
            ?faces.SelectMany(t=>t.Append(uint.MaxValue)).ToArray()
            :faces.SelectMany(t=>t).ToArray();
        result.IndexOffset=0;return result;
    }
    private static bool HeadVertex(Mesh m,SubMesh sub,Skin skin,uint v,bool allowJaw=false,bool allowRin=false,bool allowCute=false,bool allowLukaBow=false,bool allowNoble=false,bool allowAngel=false,bool allowSeaLily=false,bool allowMeikoRibbon=false)
    {
        if(m.BlendWeights==null||m.BlendIndices==null||sub.BoneIndices==null)return false;
        var w=m.BlendWeights[v];var b=m.BlendIndices[v];
        var weights=new[]{w.X,w.Y,w.Z,w.W};var indices=new[]{b.X,b.Y,b.Z,b.W};
        bool any=false;
        for(int i=0;i<4;i++)if(weights[i]>0) {
            any=true;int slot=indices[i];
            if(slot<0||slot>=sub.BoneIndices.Length||sub.BoneIndices[slot]>=skin.Bones.Count)return false;
            string bone=skin.Bones[sub.BoneIndices[slot]].Name;
            if(allowMeikoRibbon&&bone is "j_ribbon_l_000_wj" or "j_ribbon_l_001_wj" or "j_ribbon_r_000_wj" or "j_ribbon_r_001_wj")continue;
            if(!(allowAngel&&(bone=="j_headwing_l_000_wj"||bone=="j_headwing_r_000_wj"))&&!(allowSeaLily&&(bone=="j_hair_ribon_l_000_wj"||bone=="j_hair_ribon_r_000_wj"))&&bone!="j_kao_wj"&&!(allowNoble&&bone=="j_ribon_000_wj")&&!(allowJaw&&bone=="kl_ago_wj")&&!(allowRin&&(bone=="j_fronthair_b_000_wj"||bone=="j_hairribon_000_wj"))&&!(allowCute&&Regex.IsMatch(bone,@"^n_pattern_00[1-8]_wj_ex$"))&&!(allowLukaBow&&(bone=="j_ribon_l_wj"||bone=="j_ribon_r_wj"||bone=="j_ribon_r_000_wj")))return false;
        }
        return any;
    }
    private static Plan? Select(Mesh mesh,ModelObject obj,float cutoff,List<string> report,string body)
    {
        // Ultimate Len: these head-weighted costume pieces must stay with the
        // body. Do not transfer them based on head weights or height alone.
        if(body=="lenitm114_x"&&mesh.Name is "main_enamel_blck_MZ" or "main_clothes_yellow_MZ" or "main_clothes_metal_MZ")return null;
        // Rei-no-Sakura Camellia's remaining bow has its own ribbon OSG rig.
        bool meikoRibbon=body=="meiitm010_x"&&mesh.Name=="hair_tie_MZ";
        if(body=="kaiitm102_x"&&mesh.Name=="muffler_MZ")return null;
        bool angel=body=="mikitm068_x"&&(mesh.Name=="head_set_MZ"||mesh.Name=="wing_MZ");
        bool seaLily=body=="mikitm131_x"&&mesh.Name=="main_clothes_bluegray_MZ";
        bool noble=body=="mikitm060_x"&&mesh.Name=="fril_MZ";
        bool lukaBow=(body=="lukitm010_x"&&mesh.Name=="cloth_MZ")||(body=="lukitm103_x"&&mesh.Name=="tie_MZ");
        bool cute=(body=="mikitm043_x"||body=="rinitm017_x")&&mesh.Name=="heart_MZ";
        bool rin=body.StartsWith("rinitm",StringComparison.OrdinalIgnoreCase);
        var materialNames=mesh.SubMeshes.Select(s=>obj.Materials[(int)s.MaterialIndex].Name).ToArray();
        bool accessory=angel||seaLily||Accessory.IsMatch(mesh.Name)||materialNames.Any(Accessory.IsMatch)
            ||(AuditedMeshes.TryGetValue(body,out var audited)&&audited.Contains(mesh.Name,StringComparer.OrdinalIgnoreCase));
        bool light=(Light.IsMatch(mesh.Name)||materialNames.Any(Light.IsMatch))
            && !mesh.Name.Contains("fur",StringComparison.OrdinalIgnoreCase)
            && !mesh.Name.Contains("ledcloth",StringComparison.OrdinalIgnoreCase);
        // Unnamed meshes can also contain head ornaments. Only high, entirely
        // head-rigid, disconnected components qualify through this fallback.
        bool geometricFallback=!accessory&&!light;
        bool jaw=body=="mikitm079_x"&&mesh.Name=="head_set_MZ";
        bool cord=body=="mikitm153_x"&&mesh.Name=="main_clothes_black_spoff_MZ";
        var headVertices=mesh.SubMeshes.Select(s=>s.Indices.Where(v=>v!=uint.MaxValue).Distinct()
            .ToDictionary(v=>v,v=>HeadVertex(mesh,s,obj.Skin,v,jaw,rin,cute,lukaBow,noble,angel,seaLily,meikoRibbon))).ToArray();
        if(!headVertices.Any(s=>s.Values.Any(h=>h)))return null;
        bool whole=headVertices.All(s=>s.Values.All(h=>h));
        if(whole&&!geometricFallback)return new(mesh,mesh,null,jaw||rin||cute||lukaBow||noble||angel||seaLily||meikoRibbon);
        if(mesh.SubMeshes.Any(s=>s.PrimitiveType!=PrimitiveType.Triangles&&s.PrimitiveType!=PrimitiveType.TriangleStrip))return null;
        // Select entire disconnected pieces, never individual head-weighted
        // corners of clothing. Weld UV/normal seams for this decision only.
        var parent=Enumerable.Range(0,mesh.Positions.Length).ToArray();
        int Root(int v){while(parent[v]!=v){parent[v]=parent[parent[v]];v=parent[v];}return v;}
        void Join(uint a,uint b){parent[Root((int)a)]=Root((int)b);}
        var welded=new Dictionary<(int,int,int),uint>();
        for(int i=0;i<mesh.Positions.Length;i++) {
            var p=mesh.Positions[i];var key=((int)MathF.Round(p.X*100000),(int)MathF.Round(p.Y*100000),(int)MathF.Round(p.Z*100000));
            if(welded.TryGetValue(key,out var other))Join((uint)i,other);else welded[key]=(uint)i;
        }
        var triangles=mesh.SubMeshes.Select(s=>Triangles(s).ToArray()).ToArray();
        foreach(var tri in triangles.SelectMany(t=>t)){Join(tri[0],tri[1]);Join(tri[1],tri[2]);}
        var eligible=new Dictionary<int,bool>();
        for(int s=0;s<mesh.SubMeshes.Count;s++)foreach(var v in triangles[s].SelectMany(t=>t).Distinct()) {
            int component=Root((int)v);
            eligible[component]=eligible.GetValueOrDefault(component,true)&&headVertices[s][v]
                // Summer Memories' disconnected hat brim extends below the generic cutoff.
                // Only this audited mesh bypasses height; every vertex must still be head-rigid.
                &&((body=="mikitm010_x"&&mesh.Name=="onepiecehat_MZ")||mesh.Positions[v].Y>=cutoff+(geometricFallback?0.10f:0));
        }
        if(cord)foreach(var component in triangles.SelectMany(t=>t).SelectMany(t=>t).Distinct().GroupBy(v=>Root((int)v))) {
            var points=component.Select(v=>mesh.Positions[v]).ToArray();var min=points.Aggregate(Vector3.Min);var max=points.Aggregate(Vector3.Max);
            // Audited against the separate cord component of root rom's
            // MIKITM664 headset: this narrow right-back component is the cord.
            if(min.X>0.025f&&max.X<0.095f&&min.Y>1.09f&&max.Y<1.40f&&min.Z>-.17f&&max.Z<-.06f)
                eligible[component.Key]=true;
        }
        var transfer=Clone(mesh);transfer.Name=mesh.Name+(light?"__HEAD_LED":"__HEAD_ACCESSORY")+(rin?"__RIN_POLISH":"");
        var remaining=new List<SubMesh>();int selected=0;
        for(int s=0;s<mesh.SubMeshes.Count;s++) {
            var sub=mesh.SubMeshes[s];
            var headFaces=new List<uint[]>();var bodyFaces=new List<uint[]>();
            foreach(var tri in triangles[s]) {
                if(eligible[Root((int)tri[0])])headFaces.Add(tri);
                else bodyFaces.Add(tri);
            }
            selected+=headFaces.Count;
            if(headFaces.Count>0)transfer.SubMeshes.Add(Faces(sub,headFaces));
            if(bodyFaces.Count>0)remaining.Add(Faces(sub,bodyFaces));
        }
        if(selected==0){report.Add($"{body}\t-\t{mesh.Name}\tpending: no isolated component belonging entirely to the head\t-");return null;}
        return new(mesh,transfer,remaining,jaw||cord||rin||cute||lukaBow||noble||angel||seaLily||meikoRibbon);
    }

    public static void RepairAngelSeaLily(string root)
    {
        string folder=Path.Combine(root,"rom","objset");
        foreach(var pair in new[]{("mikitm068_x","mikitm668_x"),("mikitm131_x","mikitm731_x")}) {
            using var ba=BinaryFile.Load<FarcArchive>(Path.Combine(folder,pair.Item1+".farc"));
            using var ha=BinaryFile.Load<FarcArchive>(Path.Combine(folder,pair.Item2+".farc"));
            var body=ba.Open<ObjectSet>(pair.Item1+"_obj.bin");var hair=ha.Open<ObjectSet>(pair.Item2+"_obj.bin");
            var bt=ba.Open<TextureSet>(pair.Item1+"_tex.bin");var ht=ha.Open<TextureSet>(pair.Item2+"_tex.bin");
            ba.Dispose();ha.Dispose();
            var source=body.Objects.Single(o=>o.Name.Contains("joha_outer",StringComparison.OrdinalIgnoreCase));
            int head=source.Skin.Bones.FindIndex(b=>b.Name=="j_kao_wj");Matrix4x4.Invert(source.Skin.Bones[head].InverseBindPoseMatrix,out var bind);
            var report=new List<string>();
            var plans=source.Meshes.Where(m=>pair.Item1=="mikitm068_x"?m.Name=="head_set_MZ"||m.Name=="wing_MZ":m.Name=="main_clothes_bluegray_MZ")
                .Select(m=>Select(m,source,bind.Translation.Y-0.07f,report,pair.Item1)).Where(p=>p!=null).Cast<Plan>().ToArray();
            if(plans.Length==0)throw new InvalidDataException("No accessory components: "+pair.Item1);
            foreach(var target in hair.Objects) {
                int h=target.Skin.Bones.FindIndex(b=>b.Name=="j_kao_wj");
                if(h<0||!SameBind(source.Skin.Bones[head].InverseBindPoseMatrix,target.Skin.Bones[h].InverseBindPoseMatrix))throw new InvalidDataException("Head bind mismatch");
                var parameters=AccessoryRigTransfer.Copy(source,target,plans.Select(p=>p.Transfer),body,bt,hair,ht);
                AccessoryRigTransfer.MergeParameters(root,source.Name,target.Name,parameters);
            }
            foreach(var plan in plans) {
                if(plan.Remaining==null||plan.Remaining.Count==0)source.Meshes.Remove(plan.Original);
                else {plan.Original.SubMeshes.Clear();plan.Original.SubMeshes.AddRange(plan.Remaining);}
            }
            AccessoryRigTransfer.Bounds(source);
            using var bout=new FarcArchive();bout.Add(pair.Item1+"_obj.bin",body);bout.Add(pair.Item1+"_tex.bin",bt);bout.Save(Path.Combine(folder,pair.Item1+".farc"));
            using var hout=new FarcArchive();hout.Add(pair.Item2+"_obj.bin",hair);hout.Add(pair.Item2+"_tex.bin",ht);hout.Save(Path.Combine(folder,pair.Item2+".farc"));
            Console.WriteLine($"{pair.Item1}: {plans.Length} accessory pieces transferred to {hair.Objects.Count} hairstyle objects.");
        }
    }
    public static void Audit(string root)
    {
        var rows=new List<string>{"Body\tMesh\tMaterials\tUsedBones\tRisk\tSafeFaces\tNotes"};
        int bodies=0,meshes=0,candidates=0;
        foreach(var file in Directory.GetFiles(Path.Combine(root,"rom","objset"),"*_x.farc").Order()) {
            string stem=Path.GetFileNameWithoutExtension(file);
            using var archive=BinaryFile.Load<FarcArchive>(file);
            var set=archive.Open<ObjectSet>(stem+"_obj.bin");
            foreach(var obj in set.Objects.Where(o=>o.Name.Contains("JOHA_OUTER",StringComparison.OrdinalIgnoreCase)&&o.Skin!=null)) {
                bodies++;int head=obj.Skin.Bones.FindIndex(b=>b.Name=="j_kao_wj");
                if(head<0)continue;
                Matrix4x4.Invert(obj.Skin.Bones[head].InverseBindPoseMatrix,out var bind);
                foreach(var mesh in obj.Meshes) {
                    meshes++;var bones=new HashSet<string>();
                    if(mesh.BlendWeights!=null&&mesh.BlendIndices!=null)foreach(var sub in mesh.SubMeshes)
                    foreach(var v in sub.Indices.Where(v=>v!=uint.MaxValue).Distinct()) {
                        var w=mesh.BlendWeights[v];var b=mesh.BlendIndices[v];
                        var ws=new[]{w.X,w.Y,w.Z,w.W};var bs=new[]{b.X,b.Y,b.Z,b.W};
                        for(int i=0;i<4;i++)if(ws[i]>0&&sub.BoneIndices!=null&&bs[i]>=0&&bs[i]<sub.BoneIndices.Length)
                            bones.Add(obj.Skin.Bones[sub.BoneIndices[bs[i]]].Name);
                    }
                    var notes=new List<string>();var plan=Select(mesh,obj,bind.Translation.Y-0.07f,notes,stem);
                    bool headRelated=bones.Any(b=>b=="j_kao_wj"||b=="kl_ago_wj"||b.Contains("hair",StringComparison.OrdinalIgnoreCase));
                    string risk=plan!=null?"low risk: isolated rigid component":headRelated?"review: mixed weights, height or additional bones":"no indication of a head accessory";
                    int faces=plan==null?0:plan.Transfer.SubMeshes.Sum(s=>Triangles(s).Count());
                    if(plan!=null)candidates++;
                    rows.Add($"{stem}\t{mesh.Name}\t{string.Join(",",mesh.SubMeshes.Select(s=>obj.Materials[(int)s.MaterialIndex].Name).Distinct())}\t{string.Join(",",bones.Order())}\t{risk}\t{faces}\t{(plan!=null?"validate pairing and bind pose before transfer":"no automatic transfer")}");
                }
            }
        }
        File.WriteAllLines(ConversionReports.PathFor(root,"ALL-ACCESSORIES-AUDIT.tsv"),rows);
        Console.WriteLine($"[HEAD_AUDIT] {bodies} body objects, {meshes} meshes, {candidates} safe mesh candidates; no assets changed.");
    }

    public static void Write(string root,HashSet<string>? selectedBodies=null)
    {
        var timer=System.Diagnostics.Stopwatch.StartNew();
        var folder=Path.Combine(root,"rom","objset");
        var cache=new Dictionary<string,Asset>(StringComparer.OrdinalIgnoreCase);
        Asset Load(string name) {
            name=name.ToLowerInvariant();if(cache.TryGetValue(name,out var found))return found;
            using var a=BinaryFile.Load<FarcArchive>(Path.Combine(folder,name+".farc"));
            var asset=new Asset{Name=name,Objects=a.Open<ObjectSet>(name+"_obj.bin"),Textures=a.Open<TextureSet>(name+"_tex.bin")};
            if(asset.Objects.TextureIds.Count!=asset.Textures.Textures.Count)throw new InvalidDataException("Texture bank "+name);
            return cache[name]=asset;
        }
        var tablePath=Path.Combine(root,"rom","mod_chritm_prop.farc");
        var entries=new Dictionary<string,byte[]>();
        using(var a=BinaryFile.Load<FarcArchive>(tablePath))foreach(var n in a.FileNames) {
            using var s=a.Open(n,EntryStreamMode.MemoryStream);using var m=new MemoryStream();s.CopyTo(m);entries[n]=m.ToArray();
        }
        var dbPath=Path.Combine(folder,"mod_obj_db.bin");var db=BinaryFile.Load<ObjectDatabase>(dbPath);
        var report=new List<string>{"Body\tHair\tMesh\tResult\tTargetMesh\tTargetUID"};
        int moved=0,variants=0;bool tablesChanged=false;
        foreach(var name in entries.Keys.Where(n=>n.EndsWith("itm_tbl.txt")).ToArray()) {
            var rows=Encoding.UTF8.GetString(entries[name]).Split('\n').Select(x=>x.Trim()).Where(x=>x.Contains('=')).Select(x=>x.Split('=',2)).ToDictionary(x=>x[0],x=>x[1]);
            var items=Enumerable.Range(0,int.Parse(rows["item.length"])).Select(i=>$"item.{i}.").ToDictionary(p=>rows[p+"no"]);
            var pairs=new List<(string Body,string HairPrefix)>();
            for(int c=0;c<int.Parse(rows["cos.length"]);c++) {
                string? body=null,hair=null;
                for(int j=0;j<int.Parse(rows[$"cos.{c}.item.length"]);j++)if(items.TryGetValue(rows[$"cos.{c}.item.{j}"],out var p)) {
                    if(rows[p+"sub_id"]=="10")body=rows.GetValueOrDefault(p+"objset.0");
                    if(rows[p+"sub_id"]=="1")hair=p;
                }
                if(body!=null&&hair!=null&&body!=rows.GetValueOrDefault(hair+"objset.0"))pairs.Add((body,hair));
            }
            bool changed=false;
            foreach(var group in pairs.Distinct().GroupBy(p=>p.Body)) {
                if(selectedBodies!=null&&!selectedBodies.Contains(group.Key))continue;
                var body=Load(group.Key);var source=body.Objects.Objects.SingleOrDefault(o=>o.Name.Contains("JOHA_OUTER",StringComparison.OrdinalIgnoreCase));
                if(source?.Skin==null)continue;
                int head=source.Skin.Bones.FindIndex(b=>b.Name=="j_kao_wj");if(head<0)continue;
                Matrix4x4.Invert(source.Skin.Bones[head].InverseBindPoseMatrix,out var bind);
                var plans=source.Meshes.Select(m=>Select(m,source,bind.Translation.Y-0.07f,report,body.Name)).Where(p=>p!=null).Cast<Plan>().ToArray();
                if(plans.Length==0)continue;
                if(group.Any(p=>pairs.Any(other=>other.HairPrefix==p.HairPrefix&&other.Body!=p.Body))) {
                    report.Add($"{body.Name}\t-\t-\tpending: hair item shared by other bodies\t-");continue;
                }
                var targets=new List<(Asset Asset,ModelObject Object,string Prefix,string OriginalUid)>();
                bool compatible=true;
                foreach(var pair in group.Distinct()) {
                    var hair=Load(rows[pair.HairPrefix+"objset.0"]);string uid=rows[pair.HairPrefix+"data.obj.0.uid"];
                    // Reopen to get an independent deep copy of the selected object.
                    using var a=BinaryFile.Load<FarcArchive>(Path.Combine(folder,hair.Name+".farc"));
                    var target=a.Open<ObjectSet>(hair.Name+"_obj.bin").Objects.Single(o=>o.Name.Equals(uid,StringComparison.OrdinalIgnoreCase));
                    int h=target.Skin?.Bones.FindIndex(b=>b.Name=="j_kao_wj")??-1;
                    if(h<0||!SameBind(target.Skin!.Bones[h].InverseBindPoseMatrix,source.Skin.Bones[head].InverseBindPoseMatrix)){compatible=false;break;}
                    targets.Add((hair,target,pair.HairPrefix,uid));
                }
                if(!compatible){report.Add($"{body.Name}\t-\t-\tpending: different bind pose\t-");continue;}
                foreach(var targetGroup in targets.GroupBy(t=>t.OriginalUid,StringComparer.OrdinalIgnoreCase)) {
                    var t=targetGroup.First();
                    var info=db.ObjectSets.Single(s=>s.Name.Equals(t.Asset.Name,StringComparison.OrdinalIgnoreCase));
                    string alias=t.OriginalUid[..^2]+"_ACC_"+body.Name.ToUpperInvariant()+"_X";
                    if(info.Objects.Any(o=>o.Name.Equals(alias,StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("Existing accessory variant "+alias);
                    t.Object.Name=alias.ToLowerInvariant();t.Object.Id=info.Objects.Max(o=>o.Id)+1;
                    int targetHead=t.Object.Skin.Bones.FindIndex(b=>b.Name=="j_kao_wj");
                    var physics=new HashSet<string>();
                    foreach(var plan in plans) {
                        if(plan.PreserveRig) {
                            var transfer=plan.Transfer;
                            if(t.Object.Meshes.Any(m=>m.Name==transfer.Name)) {
                                transfer=Clone(plan.Transfer);transfer.Name+="__PART_"+t.Object.Meshes.Count;
                                foreach(var sub in plan.Transfer.SubMeshes)transfer.SubMeshes.Add(Clone(sub));
                            }
                            physics.UnionWith(AccessoryRigTransfer.Copy(source,t.Object,new[]{transfer},body.Objects,body.Textures,t.Asset.Objects,t.Asset.Textures));
                            moved++;report.Add($"{body.Name}\t{t.Asset.Name}\t{plan.Original.Name}\ttransferred\t{transfer.Name}\t{alias}");continue;
                        }
                        var mesh=Clone(plan.Transfer);foreach(var sub in plan.Transfer.SubMeshes)mesh.SubMeshes.Add(Clone(sub));
                        foreach(var sub in mesh.SubMeshes) {
                            var material=source.Materials[(int)sub.MaterialIndex];
                            foreach(var tex in material.MaterialTextures) {
                                int index=body.Objects.TextureIds.IndexOf(tex.TextureId);
                                if(index>=0&&!t.Asset.Objects.TextureIds.Contains(tex.TextureId)) {
                                    t.Asset.Objects.TextureIds.Add(tex.TextureId);t.Asset.Textures.Textures.Add(body.Textures.Textures[index]);
                                }
                            }
                            sub.MaterialIndex=(uint)t.Object.Materials.Count;t.Object.Materials.Add(material);
                            sub.BoneIndices=sub.BoneIndices.Select(i=>checked((ushort)(targetHead))).ToArray();
                        }
                        if(t.Object.Meshes.Any(m=>m.Name==mesh.Name))mesh.Name+="__PART_"+t.Object.Meshes.Count;
                        t.Object.Meshes.Add(mesh);moved++;
                        report.Add($"{body.Name}\t{t.Asset.Name}\t{plan.Original.Name}\ttransferred\t{mesh.Name}\t{alias}");
                    }
                    var points=t.Object.Meshes.SelectMany(m=>m.SubMeshes.SelectMany(s=>s.Indices.Where(v=>v!=uint.MaxValue)).Distinct().Select(v=>m.Positions[v])).ToArray();
                    var min=points.Aggregate(Vector3.Min);var max=points.Aggregate(Vector3.Max);var center=(min+max)*0.5f;
                    t.Object.BoundingSphere=new MikuMikuLibrary.Geometry.BoundingSphere{Center=center,Radius=points.Max(p=>Vector3.Distance(center,p))};
                    t.Asset.Objects.Objects.Add(t.Object);info.Objects.Add(new ObjectInfo{Name=alias,Id=t.Object.Id});t.Asset.Changed=true;
                    foreach(var mapped in targetGroup)rows[mapped.Prefix+"data.obj.0.uid"]=alias;
                    var skinFolder=Path.Combine(root,"rom","skin_param");var oldSkin=Path.Combine(skinFolder,"ext_skp_"+t.OriginalUid.ToLowerInvariant()+".txt");
                    if(File.Exists(oldSkin))File.Copy(oldSkin,Path.Combine(skinFolder,"ext_skp_"+alias.ToLowerInvariant()+".txt"),true);
                    AccessoryRigTransfer.MergeParameters(root,source.Name,alias,physics);
                    variants++;changed=tablesChanged=true;
                }
                foreach(var p in plans) {
                    if(p.Remaining==null||p.Remaining.Count==0)source.Meshes.Remove(p.Original);
                    else {p.Original.SubMeshes.Clear();p.Original.SubMeshes.AddRange(p.Remaining);}
                }
                body.Changed=true;
            }
            if(changed)entries[name]=Encoding.UTF8.GetBytes(string.Join("\n",rows.Select(k=>$"{k.Key}={k.Value}"))+"\n");
        }
        foreach(var asset in cache.Values.Where(a=>a.Changed)) {
            using var a=new FarcArchive();a.Add(asset.Name+"_obj.bin",asset.Objects);a.Add(asset.Name+"_tex.bin",asset.Textures);a.Save(Path.Combine(folder,asset.Name+".farc"));
        }
        if(tablesChanged) {
            db.Save(dbPath);using var a=new FarcArchive();foreach(var e in entries)a.Add(e.Key,new MemoryStream(e.Value),leaveOpen:false);a.Save(tablePath);
        }
        File.WriteAllLines(ConversionReports.PathFor(root,"HEAD-ACCESSORIES-LEDS.tsv"),report);
        Console.WriteLine($"[HEAD_ADORNMENTS] {moved} mesh transfers, {variants} hairstyle variants, {cache.Values.Count(a=>a.Changed)} archives; {timer.Elapsed.TotalSeconds:F2}s.");
    }
}

