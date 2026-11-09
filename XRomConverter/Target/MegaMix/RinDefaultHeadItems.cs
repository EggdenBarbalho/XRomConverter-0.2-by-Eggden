using System.Text;using System.Text.Json;using System.Text.RegularExpressions;
using MikuMikuLibrary.Archives;using MikuMikuLibrary.Archives.Extensions;using MikuMikuLibrary.Databases;using MikuMikuLibrary.IO;using MikuMikuLibrary.Objects;
namespace XRomConverter.Target.MegaMix;
internal static class RinDefaultHeadItems {
 static bool Same(Mesh a,Mesh b)=>a.Name==b.Name&&a.Positions.Length==b.Positions.Length&&a.Positions.Zip(b.Positions,(x,y)=>System.Numerics.Vector3.Distance(x,y)).All(d=>d<0.00001f)&&a.SubMeshes.Count==b.SubMeshes.Count&&a.SubMeshes.Zip(b.SubMeshes,(x,y)=>x.Indices.SequenceEqual(y.Indices)).All(x=>x);
 static string Shape(Mesh m)=>JsonSerializer.Serialize(new{m.Positions,m.Normals,m.TexCoords0,m.BlendWeights,indices=m.SubMeshes.Select(s=>s.Indices)},new JsonSerializerOptions{IncludeFields=true});
 public static void Write(string root,string backup){
  string folder=Path.Combine(root,"rom","objset"),tp=Path.Combine(root,"rom/mod_chritm_prop.farc");var entries=new Dictionary<string,byte[]>();using(var a=BinaryFile.Load<FarcArchive>(tp))foreach(var n in a.FileNames){using var s=a.Open(n,EntryStreamMode.MemoryStream);using var m=new MemoryStream();s.CopyTo(m);entries[n]=m.ToArray();}
  var f=Encoding.UTF8.GetString(entries["rinitm_tbl.txt"]).Split('\n').Select(l=>l.TrimEnd('\r')).Where(l=>l.Contains('=')).Select(l=>l.Split('=',2)).ToDictionary(l=>l[0],l=>l[1]);var db=BinaryFile.Load<ObjectDatabase>(Path.Combine(folder,"mod_obj_db.bin"));var report=new List<string>{"Hair\tDefaultItem\tPreviousUID\tNewUID\tRemovedMeshes"};var sets=new Dictionary<string,ObjectSet>();
  foreach(var key in f.Keys.Where(k=>k.EndsWith(".data.obj.0.uid")&&f[k].Contains("_HEADSLOT_RINITM",StringComparison.OrdinalIgnoreCase)).ToArray()){
   string old=f[key],p=key[..^14],hairStem=f[p+"objset.0"].ToLowerInvariant();var match=Regex.Match(old,@"_HEADSLOT_(RINITM\d+_X)",RegexOptions.IgnoreCase);string headStem=match.Groups[1].Value.ToLowerInvariant();string originalPath=Path.Combine(backup,"rom/objset",headStem+".farc");
   if(!File.Exists(originalPath))throw new FileNotFoundException("Original Rin default item",originalPath);using var donor=BinaryFile.Load<FarcArchive>(originalPath);var head=donor.Open<ObjectSet>(headStem+"_obj.bin").Objects.Single();
   if(!sets.TryGetValue(hairStem,out var set)){using var a=BinaryFile.Load<FarcArchive>(Path.Combine(folder,hairStem+".farc"));sets[hairStem]=set=a.Open<ObjectSet>(hairStem+"_obj.bin");}
   using var current=BinaryFile.Load<FarcArchive>(Path.Combine(folder,hairStem+".farc"));var model=current.Open<ObjectSet>(hairStem+"_obj.bin").Objects.Single(o=>o.Name.Equals(old,StringComparison.OrdinalIgnoreCase));
   int removed=0;foreach(var mesh in head.Meshes){string shape=Shape(mesh);var matches=model.Meshes.Where(m=>Same(m,mesh)).ToArray();if(matches.Length!=1)throw new InvalidDataException("Rin head geometry match is ambiguous "+headStem+"/"+mesh.Name+" matches="+matches.Length+" candidates="+string.Join(",",model.Meshes.Select(m=>m.Name+":"+m.Positions.Length)));model.Meshes.Remove(matches[0]);removed++;}
   string alias=Regex.Replace(old,@"_HEADSLOT_RINITM\d+_X","",RegexOptions.IgnoreCase)+"_DEFAULTHEAD_X";var info=db.ObjectSets.Single(s=>s.Name.Equals(hairStem,StringComparison.OrdinalIgnoreCase));model.Name=alias.ToLowerInvariant();model.Id=info.Objects.Max(o=>o.Id)+1;AccessoryRigTransfer.Bounds(model);set.Objects.Add(model);info.Objects.Add(new ObjectInfo{Name=alias,Id=model.Id});f[key]=alias;
   File.Copy(originalPath,Path.Combine(folder,headStem+".farc"),true);string skp=Path.Combine(root,"rom/skin_param"),from=Path.Combine(skp,"ext_skp_"+old.ToLowerInvariant()+".txt");if(File.Exists(from))File.Copy(from,Path.Combine(skp,"ext_skp_"+model.Name+".txt"),true);
   report.Add($"{hairStem}\t{headStem}\t{old}\t{alias}\t{removed}");
  }
  foreach(var asset in sets){Dictionary<string,byte[]> parts=new();using(var a=BinaryFile.Load<FarcArchive>(Path.Combine(folder,asset.Key+".farc")))foreach(var n in a.FileNames){using var s=a.Open(n,EntryStreamMode.MemoryStream);using var m=new MemoryStream();s.CopyTo(m);parts[n]=m.ToArray();}
   using var writer=new FarcArchive();foreach(var part in parts)if(part.Key==asset.Key+"_obj.bin")writer.Add(part.Key,asset.Value);else writer.Add(part.Key,new MemoryStream(part.Value),false);writer.Save(Path.Combine(folder,asset.Key+".farc"));
  }
  entries["rinitm_tbl.txt"]=Encoding.UTF8.GetBytes(string.Join("\n",f.OrderBy(e=>e.Key,StringComparer.Ordinal).Select(e=>e.Key+"="+e.Value))+"\n");using(var a=new FarcArchive()){foreach(var e in entries)a.Add(e.Key,new MemoryStream(e.Value),false);a.Save(tp);}db.Save(Path.Combine(folder,"mod_obj_db.bin"));File.WriteAllLines(ConversionReports.PathFor(root,"RIN-DEFAULT-HEAD-ITEMS.tsv"),report);Console.WriteLine($"[RIN_DEFAULT_HEAD] {report.Count-1} hair bindings restored to separate default head items.");
 }
}
