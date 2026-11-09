using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.Extensions;
using MikuMikuLibrary.Databases;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Objects;
using MikuMikuLibrary.Textures;

namespace XRomConverter.Target.MegaMix;

// Explicit whole-mesh selection. Keep the shared bare hairstyle available to
// the other costumes; never infer detachable clothing from names or topology.
internal static class DefaultMikuHeadphones
{
    public static void Write(string root)
    {
        const string setName="mikitm601_x";
        const string alias="MIKITM601_ATAM_HEADPHONES_X";
        var folder=Path.Combine(root,"rom","objset");
        using var bodyArchive=BinaryFile.Load<FarcArchive>(Path.Combine(folder,"mikitm001_x.farc"));
        using var hairArchive=BinaryFile.Load<FarcArchive>(Path.Combine(folder,setName+".farc"));
        var bodySet=bodyArchive.Open<ObjectSet>("mikitm001_x_obj.bin");
        var hairSet=hairArchive.Open<ObjectSet>(setName+"_obj.bin");
        if(hairSet.Objects.Any(o=>o.Name.Equals(alias,StringComparison.OrdinalIgnoreCase))) {
            Console.WriteLine("[MIKU_HEADPHONES] Already applied."); return;
        }
        var variantSet=hairArchive.Open<ObjectSet>(setName+"_obj.bin");
        var target=variantSet.Objects.Single();
        var source=bodySet.Objects.Single();
        var selection=new[]{"headset_clothes_gray_MZ","headset_light"}
            .Select(n=>source.Meshes.Single(m=>m.Name==n)).ToArray();
        var head=source.Skin.Bones.FindIndex(b=>b.Name=="j_kao_wj");
        var targetHead=target.Skin.Bones.FindIndex(b=>b.Name=="j_kao_wj");
        if(head<0 || targetHead<0 || source.Skin.Bones[head].InverseBindPoseMatrix!=target.Skin.Bones[targetHead].InverseBindPoseMatrix)
            throw new InvalidDataException("Head bind pose mismatch; no assets saved.");
        foreach(var mesh in selection)foreach(var sub in mesh.SubMeshes) {
            if(sub.BoneIndices==null || sub.BoneIndices.Any(b=>b!=head))
                throw new InvalidDataException("Selected headset palette contains a non-head bone.");
        }
        var textures=hairArchive.Open<TextureSet>(setName+"_tex.bin");
        var bodyTextures=bodyArchive.Open<TextureSet>("mikitm001_x_tex.bin");
        if(bodyTextures.Textures.Count!=bodySet.TextureIds.Count || textures.Textures.Count!=hairSet.TextureIds.Count)
            throw new InvalidDataException("Texture bank mismatch.");
        var needed=new HashSet<uint>();
        foreach(var mesh in selection) {
            foreach(var sub in mesh.SubMeshes) {
                var material=source.Materials[(int)sub.MaterialIndex];
                foreach(var t in material.MaterialTextures) needed.Add(t.TextureId);
                sub.MaterialIndex=(uint)target.Materials.Count;target.Materials.Add(material);
                sub.BoneIndices=sub.BoneIndices.Select(_=>checked((ushort)targetHead)).ToArray();
            }
            source.Meshes.Remove(mesh);target.Meshes.Add(mesh);
        }
        for(int i=0;i<bodySet.TextureIds.Count;i++)if(needed.Contains(bodySet.TextureIds[i]) && !hairSet.TextureIds.Contains(bodySet.TextureIds[i])) {
            hairSet.TextureIds.Add(bodySet.TextureIds[i]);textures.Textures.Add(bodyTextures.Textures[i]);
        }
        var dbPath=Path.Combine(folder,"mod_obj_db.bin");
        var db=BinaryFile.Load<ObjectDatabase>(dbPath);
        var info=db.ObjectSets.Single(s=>s.Name.Equals(setName,StringComparison.OrdinalIgnoreCase));
        uint objectId=info.Objects.Max(o=>o.Id)+1;
        target.Name=alias.ToLowerInvariant();target.Id=objectId;
        var points=target.Meshes.SelectMany(m=>m.Positions).ToArray();
        var min=points.Aggregate(System.Numerics.Vector3.Min);var max=points.Aggregate(System.Numerics.Vector3.Max);
        var center=(min+max)*0.5f;
        target.BoundingSphere=new MikuMikuLibrary.Geometry.BoundingSphere {Center=center,Radius=points.Max(p=>System.Numerics.Vector3.Distance(center,p))};
        hairSet.Objects.Add(target);info.Objects.Add(new ObjectInfo{Name=alias,Id=objectId});
        var tablePath=Path.Combine(root,"rom","mod_chritm_prop.farc");
        using var tables=BinaryFile.Load<FarcArchive>(tablePath);
        var entries=new Dictionary<string,byte[]>();
        foreach(var name in tables.FileNames) {using var s=tables.Open(name,EntryStreamMode.MemoryStream);using var memory=new MemoryStream();s.CopyTo(memory);entries[name]=memory.ToArray();}
        var text=System.Text.Encoding.UTF8.GetString(entries["mikitm_tbl.txt"]);
        var rows=text.Split('\n').Select(s=>s.Trim()).Where(s=>s.Contains('=')).Select(s=>s.Split('=',2)).ToDictionary(s=>s[0],s=>s[1]);
        var items=Enumerable.Range(0,int.Parse(rows["item.length"])).Select(i=>$"item.{i}.").ToArray();
        var defaultCos=Enumerable.Range(0,int.Parse(rows["cos.length"])).Single(i=>rows[$"cos.{i}.id"]=="0");
        var defaultItems=Enumerable.Range(0,int.Parse(rows[$"cos.{defaultCos}.item.length"]))
            .Select(j=>rows[$"cos.{defaultCos}.item.{j}"]).ToHashSet();
        var hairPrefix=items.Single(p=>defaultItems.Contains(rows[p+"no"]) && rows.GetValueOrDefault(p+"sub_id")=="1" && rows.GetValueOrDefault(p+"objset.0")?.Equals(setName,StringComparison.OrdinalIgnoreCase)==true);
        string number=rows[hairPrefix+"no"];
        for(int i=0;i<int.Parse(rows["cos.length"]);i++)if(i!=defaultCos)
            for(int j=0;j<int.Parse(rows[$"cos.{i}.item.length"]);j++)if(rows[$"cos.{i}.item.{j}"]==number)
                throw new InvalidDataException("Default hairstyle item shared by another COS; no assets saved.");
        rows[hairPrefix+"data.obj.0.uid"]=alias;
        entries["mikitm_tbl.txt"]=System.Text.Encoding.UTF8.GetBytes(string.Join("\n",rows.Select(k=>$"{k.Key}={k.Value}"))+"\n");
        bodyArchive.Dispose();hairArchive.Dispose();tables.Dispose();
        void Save(string path,ObjectSet objects,TextureSet bank) {using var a=new FarcArchive();string stem=Path.GetFileNameWithoutExtension(path);a.Add(stem+"_obj.bin",objects);a.Add(stem+"_tex.bin",bank);a.Save(path);}
        Save(Path.Combine(folder,"mikitm001_x.farc"),bodySet,bodyTextures);
        Save(Path.Combine(folder,setName+".farc"),hairSet,textures);db.Save(dbPath);
        using(var a=new FarcArchive()) {foreach(var e in entries)a.Add(e.Key,new MemoryStream(e.Value),leaveOpen:false);a.Save(tablePath);}
        var skinFolder=Path.Combine(root,"rom","skin_param");
        string originalUid=hairSet.Objects.First().Name.ToLowerInvariant();
        var skin=Path.Combine(skinFolder,"ext_skp_"+originalUid+".txt");
        if(File.Exists(skin))File.Copy(skin,Path.Combine(skinFolder,"ext_skp_"+alias.ToLowerInvariant()+".txt"),true);
        Console.WriteLine($"[MIKU_HEADPHONES] 2 whole meshes; default hair item {number} uses {alias}; other hair entries unchanged. No item/module/COS IDs changed.");
    }
}
