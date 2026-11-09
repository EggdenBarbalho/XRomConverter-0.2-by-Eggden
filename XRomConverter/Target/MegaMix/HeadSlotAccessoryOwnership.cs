using System.Text;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.Extensions;
using MikuMikuLibrary.Databases;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Objects;
using MikuMikuLibrary.Textures;
using ModelObject=MikuMikuLibrary.Objects.Object;

namespace XRomConverter.Target.MegaMix;

internal static class HeadSlotAccessoryOwnership
{
    private sealed record Asset(string Stem,ObjectSet Set,TextureSet Tex);
    public static void Write(string root) {
        string folder=Path.Combine(root,"rom","objset"),tablePath=Path.Combine(root,"rom","mod_chritm_prop.farc"),dbPath=Path.Combine(folder,"mod_obj_db.bin");
        var cache=new Dictionary<string,Asset>(StringComparer.OrdinalIgnoreCase);var changed=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Asset Load(string stem){stem=stem.ToLowerInvariant();if(cache.TryGetValue(stem,out var result))return result;using var a=BinaryFile.Load<FarcArchive>(Path.Combine(folder,stem+".farc"));return cache[stem]=new(stem,a.Open<ObjectSet>(stem+"_obj.bin"),a.Open<TextureSet>(stem+"_tex.bin"));}
        var entries=new Dictionary<string,byte[]>();using(var a=BinaryFile.Load<FarcArchive>(tablePath))foreach(var n in a.FileNames){using var s=a.Open(n,EntryStreamMode.MemoryStream);using var m=new MemoryStream();s.CopyTo(m);entries[n]=m.ToArray();}
        var db=BinaryFile.Load<ObjectDatabase>(dbPath);
        var report=new List<string>{"Source\tTarget\tMesh\tTargetUID\tDirection"};int variants=0;
        bool tablesChanged=false;
        foreach(string name in new[]{"lukitm_tbl.txt"}) {
        if(!entries.ContainsKey(name))continue;
        int previousVariants=variants;
        var rows=Encoding.UTF8.GetString(entries[name]).Split('\n').Select(s=>s.Trim()).Where(s=>s.Contains('=')).Select(s=>s.Split('=',2)).ToDictionary(s=>s[0],s=>s[1]);
        var items=Enumerable.Range(0,int.Parse(rows["item.length"])).Select(i=>$"item.{i}.").ToDictionary(p=>rows[p+"no"]);
        var pairs=new List<(string Hair,string Head,string Cos)>();var audit=new List<string>{"COS\tHair\tAdornment\tStatus"};
        for(int c=0;c<int.Parse(rows["cos.length"]);c++) {
            var costumeItems=Enumerable.Range(0,int.Parse(rows[$"cos.{c}.item.length"])).Select(j=>items.GetValueOrDefault(rows[$"cos.{c}.item.{j}"])).Where(p=>p!=null).ToArray();
            string? hair=costumeItems.SingleOrDefault(p=>rows[p+"sub_id"]=="1"),head=costumeItems.SingleOrDefault(p=>rows[p+"sub_id"]=="0");
            if(hair==null)continue;
            audit.Add($"{rows[$"cos.{c}.id"]}\t{rows[hair+"objset.0"]}\t{(head==null?"-":rows[head+"objset.0"])}\t{(head==null?"no separate adornment":"separate adornment linked to hair")}");
            if(head!=null&&File.Exists(Path.Combine(folder,rows[head+"objset.0"].ToLowerInvariant()+".farc"))&&File.Exists(Path.Combine(folder,rows[hair+"objset.0"].ToLowerInvariant()+".farc")))pairs.Add((hair,head,rows[$"cos.{c}.id"]));
        }
        if(pairs.GroupBy(p=>p.Hair).Any(g=>g.Select(p=>p.Head).Distinct().Count()>1))throw new InvalidDataException("Shared hair item has conflicting head ornaments");
        foreach(var group in pairs.GroupBy(p=>(rows[p.Hair+"data.obj.0.uid"],rows[p.Head+"data.obj.0.uid"]))) {
            var pair=group.First();var source=Load(rows[pair.Head+"objset.0"]);var donor=source.Set.Objects.Single(o=>o.Name.Equals(group.Key.Item2,StringComparison.OrdinalIgnoreCase));
            if(donor.Meshes.Count==0)continue;
            var hair=Load(rows[pair.Hair+"objset.0"]);
            // Independent object clone, preserving older variants and their users.
            using var archive=BinaryFile.Load<FarcArchive>(Path.Combine(folder,hair.Stem+".farc"));
            var target=archive.Open<ObjectSet>(hair.Stem+"_obj.bin").Objects.Single(o=>o.Name.Equals(group.Key.Item1,StringComparison.OrdinalIgnoreCase));
            string originalUid=target.Name,alias=target.Name[..^2]+"_HEADSLOT_"+source.Stem.ToUpperInvariant()+"_X";
            var info=db.ObjectSets.Single(s=>s.Name.Equals(hair.Stem,StringComparison.OrdinalIgnoreCase));
            target.Name=alias.ToLowerInvariant();target.Id=info.Objects.Max(o=>o.Id)+1;
            var blocks=AccessoryRigTransfer.Copy(donor,target,donor.Meshes,source.Set,source.Tex,hair.Set,hair.Tex);
            string skinFolder=Path.Combine(root,"rom","skin_param"),oldSkin=Path.Combine(skinFolder,"ext_skp_"+originalUid.ToLowerInvariant()+".txt"),newSkin=Path.Combine(skinFolder,"ext_skp_"+target.Name+".txt");
            if(File.Exists(oldSkin))File.Copy(oldSkin,newSkin,true);
            AccessoryRigTransfer.MergeParameters(root,donor.Name,target.Name,blocks);
            hair.Set.Objects.Add(target);info.Objects.Add(new ObjectInfo{Name=alias,Id=target.Id});changed.Add(hair.Stem);
            foreach(var linked in group)rows[linked.Hair+"data.obj.0.uid"]=alias;
            foreach(var mesh in donor.Meshes)report.Add($"{source.Stem}\t{hair.Stem}\t{mesh.Name}\t{alias}\tseparate head item to hair");
            variants++;
        }
        // Clear only after every distinct hairstyle variant using an ornament
        // has been built. Keep item/COS IDs and the empty object registration.
        foreach(var pair in pairs.DistinctBy(p=>p.Head)) {
            var asset=Load(rows[pair.Head+"objset.0"]);var o=asset.Set.Objects.Single(o=>o.Name.Equals(rows[pair.Head+"data.obj.0.uid"],StringComparison.OrdinalIgnoreCase));if(o.Meshes.Count>0){o.Meshes.Clear();changed.Add(asset.Stem);}
        }
        if(variants>previousVariants){entries[name]=Encoding.UTF8.GetBytes(string.Join("\n",rows.Select(k=>$"{k.Key}={k.Value}"))+"\n");tablesChanged=true;}
        File.WriteAllLines(ConversionReports.PathFor(root,"ADORNMENT-AUDIT-"+(name.StartsWith("rin")?"RIN":"LUKA")+".tsv"),audit);
        }
        // Originator's scarf was supplied in the X hairstyle itself. It belongs
        // to the costume; retain its exact geometry, physics and body weights.
        if(File.Exists(Path.Combine(folder,"kaiitm702_x.farc"))&&File.Exists(Path.Combine(folder,"kaiitm102_x.farc"))) {
        var originatorHair=Load("kaiitm702_x");var body=Load("kaiitm102_x");var bodyObject=body.Set.Objects.Single(o=>o.Name.Contains("JOHA_OUTER",StringComparison.OrdinalIgnoreCase));
        foreach(var hairObject in originatorHair.Set.Objects) {
            var scarves=hairObject.Meshes.Where(m=>m.Name=="muffler_MZ").ToArray();if(scarves.Length==0)continue;
            var blocks=AccessoryRigTransfer.Copy(hairObject,bodyObject,scarves,originatorHair.Set,originatorHair.Tex,body.Set,body.Tex);
            AccessoryRigTransfer.MergeParameters(root,hairObject.Name,bodyObject.Name,blocks);
            foreach(var mesh in scarves){hairObject.Meshes.Remove(mesh);report.Add($"{originatorHair.Stem}\t{body.Stem}\t{mesh.Name}\t{bodyObject.Name}\thair to body");}
            AccessoryRigTransfer.Bounds(hairObject);changed.Add(originatorHair.Stem);changed.Add(body.Stem);
        }
        }
        foreach(var stem in changed){var asset=cache[stem];using var archive=new FarcArchive();archive.Add(stem+"_obj.bin",asset.Set);archive.Add(stem+"_tex.bin",asset.Tex);archive.Save(Path.Combine(folder,stem+".farc"));}
        if(tablesChanged){using var a=new FarcArchive();foreach(var e in entries)a.Add(e.Key,new MemoryStream(e.Value),leaveOpen:false);a.Save(tablePath);db.Save(dbPath);}
        File.WriteAllLines(ConversionReports.PathFor(root,"HEAD-ADORNMENT-TRANSFERS.tsv"),report);
        Console.WriteLine($"[HEAD_SLOT_OWNERSHIP] {variants} hairstyle variants, {report.Count-1} mesh ownership changes.");
    }
}
