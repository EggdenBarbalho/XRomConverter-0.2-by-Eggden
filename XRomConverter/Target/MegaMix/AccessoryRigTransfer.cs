using System.Numerics;
using MikuMikuLibrary.Objects;
using MikuMikuLibrary.Objects.Extra.Blocks;
using MikuMikuLibrary.Textures;
using ModelObject=MikuMikuLibrary.Objects.Object;

namespace XRomConverter.Target.MegaMix;

internal static class AccessoryRigTransfer
{
    internal static T Clone<T>(T value) where T:new() {
        var clone=new T();foreach(var p in typeof(T).GetProperties().Where(p=>p.CanWrite)) {
            var v=p.GetValue(value);p.SetValue(clone,v is Array a?a.Clone():v);
        }return clone;
    }
    private static bool SameBind(Matrix4x4 a,Matrix4x4 b) {
        float[] V(Matrix4x4 m)=>new[]{m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44};
        return V(a).Zip(V(b),(x,y)=>MathF.Abs(x-y)).All(d=>float.IsFinite(d)&&d<0.00001f);
    }
    internal static HashSet<string> UsedBones(ModelObject source,IEnumerable<Mesh> meshes) {
        var names=new HashSet<string>();foreach(var mesh in meshes)foreach(var sub in mesh.SubMeshes)
        foreach(var v in sub.Indices.Where(v=>v!=uint.MaxValue).Distinct()) {
            var w=mesh.BlendWeights[v];var b=mesh.BlendIndices[v];var ws=new[]{w.X,w.Y,w.Z,w.W};var bs=new[]{b.X,b.Y,b.Z,b.W};
            for(int i=0;i<4;i++)if(ws[i]>0)names.Add(source.Skin.Bones[sub.BoneIndices[bs[i]]].Name);
        }return names;
    }
    internal static HashSet<string> Copy(ModelObject source,ModelObject target,IEnumerable<Mesh> selected,ObjectSet donorSet,TextureSet donorTex,ObjectSet targetSet,TextureSet targetTex) {
        var meshes=selected.ToArray();var names=UsedBones(source,meshes);var blocks=source.Skin.Blocks.OfType<OsageBlock>().Where(b=>b.Nodes.Any(n=>names.Contains(n.Name))).ToArray();
        int Import(BoneInfo original) {
            int existing=target.Skin.Bones.FindIndex(b=>b.Name==original.Name);
            if(existing>=0){if(!SameBind(target.Skin.Bones[existing].InverseBindPoseMatrix,original.InverseBindPoseMatrix))throw new InvalidDataException("Accessory bind differs: "+original.Name);return existing;}
            var bone=Clone(original);if(original.Parent!=null)bone.Parent=target.Skin.Bones[Import(original.Parent)];
            if(bone.IsEx)bone.Id=Math.Max(32768u,target.Skin.Bones.Max(b=>b.Id)+1);
            int result=target.Skin.Bones.Count;target.Skin.Bones.Add(bone);return result;
        }
        // Expression/constraint transforms are dependencies too (for example fox ears).
        string? Field(object b,string key)=>(string?)b.GetType().GetProperty(key)?.GetValue(b);
        var dependencies=new HashSet<string>(names);
        foreach(var block in blocks)dependencies.Add(block.ParentName);
        bool expanded;
        do {expanded=false;foreach(var block in source.Skin.Blocks)if(dependencies.Contains(Field(block,"Name")??"")) {
            string? parent=Field(block,"ParentName");if(parent!=null)expanded|=dependencies.Add(parent);
            if(block is ExpressionBlock expression)foreach(var text in expression.Expressions)
                foreach(System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text,@"\b\d+\.(\w+)"))expanded|=dependencies.Add(match.Groups[1].Value);
        }}while(expanded);
        foreach(var block in source.Skin.Blocks.Where(b=>b is not OsageBlock&&dependencies.Contains(Field(b,"Name")??""))) {
            if(!target.Skin.Blocks.Any(b=>Field(b,"Name")==Field(block,"Name")))target.Skin.Blocks.Add(block);
            var bone=source.Skin.Bones.SingleOrDefault(b=>b.Name==Field(block,"Name"));if(bone!=null)Import(bone);
        }
        var addedParameters=new HashSet<string>();
        foreach(var block in blocks) {
            foreach(var node in block.Nodes){var bone=source.Skin.Bones.SingleOrDefault(b=>b.Name==node.Name);if(bone!=null)Import(bone);}
            var existing=target.Skin.Blocks.OfType<OsageBlock>().SingleOrDefault(b=>b.Name==block.Name);
            if(existing==null){target.Skin.Blocks.Add(block);addedParameters.Add(block.ExternalName);}
            else if(!existing.Nodes.Select(n=>n.Name).SequenceEqual(block.Nodes.Select(n=>n.Name))||existing.ParentName!=block.ParentName)throw new InvalidDataException("Accessory physics block differs "+block.Name);
        }
        foreach(var originalMesh in meshes) {
            var mesh=Clone(originalMesh);foreach(var originalSub in originalMesh.SubMeshes) {
                var sub=Clone(originalSub);var slots=new HashSet<int>();
                foreach(var v in sub.Indices.Where(v=>v!=uint.MaxValue).Distinct()) {
                    var w=mesh.BlendWeights[v];var b=mesh.BlendIndices[v];var ws=new[]{w.X,w.Y,w.Z,w.W};var bs=new[]{b.X,b.Y,b.Z,b.W};for(int i=0;i<4;i++)if(ws[i]>0)slots.Add(bs[i]);
                }
                int head=target.Skin.Bones.FindIndex(b=>b.Name=="j_kao_wj");
                sub.BoneIndices=sub.BoneIndices.Select((i,slot)=>checked((ushort)(slots.Contains(slot)?Import(source.Skin.Bones[i]):head))).ToArray();
                var material=source.Materials[(int)sub.MaterialIndex];
                foreach(var tex in material.MaterialTextures){int index=donorSet.TextureIds.IndexOf(tex.TextureId);if(index>=0&&!targetSet.TextureIds.Contains(tex.TextureId)){targetSet.TextureIds.Add(tex.TextureId);targetTex.Textures.Add(donorTex.Textures[index]);}}
                sub.MaterialIndex=(uint)target.Materials.Count;target.Materials.Add(material);mesh.SubMeshes.Add(sub);
            }target.Meshes.Add(mesh);
        }
        Bounds(target);return addedParameters;
    }
    internal static void Bounds(ModelObject target) {
        var points=target.Meshes.SelectMany(m=>m.SubMeshes.SelectMany(s=>s.Indices.Where(v=>v!=uint.MaxValue)).Distinct().Select(v=>m.Positions[v])).ToArray();if(points.Length==0)return;
        var center=(points.Aggregate(Vector3.Min)+points.Aggregate(Vector3.Max))*.5f;target.BoundingSphere=new MikuMikuLibrary.Geometry.BoundingSphere{Center=center,Radius=points.Max(p=>Vector3.Distance(center,p))};
    }
    internal static void MergeParameters(string root,string sourceUid,string targetUid,IEnumerable<string> names) {
        var prefixes=names.Select(n=>n+".").ToArray();if(prefixes.Length==0)return;
        string folder=Path.Combine(root,"rom","skin_param"),source=Path.Combine(folder,"ext_skp_"+sourceUid.ToLowerInvariant()+".txt"),target=Path.Combine(folder,"ext_skp_"+targetUid.ToLowerInvariant()+".txt");
        if(!File.Exists(source))throw new FileNotFoundException("Accessory physics parameters",source);
        var incoming=File.ReadAllLines(source).Where(line=>prefixes.Any(p=>line.StartsWith(p,StringComparison.Ordinal))).ToArray();
        if(prefixes.Any(p=>!incoming.Any(line=>line.StartsWith(p,StringComparison.Ordinal))))throw new InvalidDataException("Missing accessory physics section: "+source+" / "+string.Join(",",prefixes.Where(p=>!incoming.Any(line=>line.StartsWith(p,StringComparison.Ordinal)))));
        var lines=File.Exists(target)?File.ReadAllLines(target).Where(line=>!prefixes.Any(p=>line.StartsWith(p,StringComparison.Ordinal))):Array.Empty<string>();
        File.WriteAllLines(target,lines.Concat(incoming));
    }
}
