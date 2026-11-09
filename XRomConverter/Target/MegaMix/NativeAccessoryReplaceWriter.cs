using System.Text;
using MikuMikuLibrary.Archives;using MikuMikuLibrary.Archives.CriMw;using MikuMikuLibrary.Archives.Extensions;using MikuMikuLibrary.Databases;using MikuMikuLibrary.IO;using MikuMikuLibrary.Objects;using MikuMikuLibrary.Textures;
namespace XRomConverter.Target.MegaMix;
internal static class NativeAccessoryReplaceWriter {
 static Dictionary<string,string> Read(FarcArchive a,string n){using var s=a.Open(n,EntryStreamMode.MemoryStream);using var r=new StreamReader(s);return r.ReadToEnd().TrimStart('\uFEFF').Split('\n').Select(l=>l.TrimEnd('\r')).Where(l=>l.Contains('=')).Select(l=>l.Split('=',2)).ToDictionary(l=>l[0],l=>l[1]);}
 public static void Write(string root,string gameDirectory){
  using var game=BinaryFile.Load<CpkArchive>(Path.Combine(gameDirectory,"diva_main.cpk"));using var tables=game.Open<FarcArchive>("rom_switch/rom/chritm_prop.farc");
  string folder=Path.Combine(root,"rom","objset");var sourceDb=BinaryFile.Load<ObjectDatabase>(Path.Combine(folder,"mod_obj_db.bin"));
  var nativeDb=game.Open<ObjectDatabase>("rom_steam/rom/objset/obj_db.bin");var nativeSets=nativeDb.ObjectSets.ToDictionary(s=>s.Name,StringComparer.OrdinalIgnoreCase);
  foreach(var path in game.FileNames.Where(n=>n.EndsWith("mdata_obj_db.bin")&&n.StartsWith("rom_ps4")))foreach(var set in game.Open<ObjectDatabase>(path).ObjectSets)nativeSets.TryAdd(set.Name,set);
  var sources=sourceDb.ObjectSets.SelectMany(s=>s.Objects.Select(o=>(Set:s,Object:o))).GroupBy(x=>x.Object.Name,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.ToArray(),StringComparer.OrdinalIgnoreCase);
  var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var report=new List<string>{"ArcadeItem\tUIDArcade\tUIDX\tFARC\tStatus"};int replaced=0;
  foreach(string tableName in tables.FileNames.Where(n=>n.EndsWith("itm_tbl.txt"))){var f=Read(tables,tableName);for(int i=0;i<int.Parse(f["item.length"]);i++){
   string p=$"item.{i}.",setName=f.GetValueOrDefault(p+"objset.0",""),uid=f.GetValueOrDefault(p+"data.obj.0.uid","");
   if(!setName.StartsWith("CMNITM",StringComparison.OrdinalIgnoreCase)||uid.Length==0||!visited.Add(uid))continue;
   string sourceUid=uid+"_X";
   if(!sources.TryGetValue(sourceUid,out var candidates)){report.Add($"{f[p+"no"]}\t{uid}\t-\t-\tno X counterpart; kept Arcade");continue;}
   if(candidates.Length!=1)throw new InvalidDataException("Ambiguous X accessory "+sourceUid);
   var candidate=candidates[0];if(!nativeSets.TryGetValue(setName,out var nativeSet))throw new InvalidDataException("Missing native accessory DB "+setName);
   var nativeObject=nativeSet.Objects.Single(o=>o.Name.Equals(uid,StringComparison.OrdinalIgnoreCase));
   string sourceStem=candidate.Set.Name.ToLowerInvariant();using var archive=BinaryFile.Load<FarcArchive>(Path.Combine(folder,candidate.Set.ArchiveFileName));
   var objects=archive.Open<ObjectSet>(candidate.Set.FileName);var model=objects.Objects.Single(o=>o.Name.Equals(sourceUid,StringComparison.OrdinalIgnoreCase));var textures=archive.Open<TextureSet>(candidate.Set.TextureFileName);
   if(model.Meshes.Count==0||model.Skin==null||objects.TextureIds.Count!=textures.Textures.Count)throw new InvalidDataException("Invalid X accessory "+sourceUid);
   foreach(var mesh in model.Meshes)foreach(var sub in mesh.SubMeshes){if(sub.MaterialIndex>=model.Materials.Count)throw new InvalidDataException("Material palette "+sourceUid);foreach(uint v in sub.Indices.Where(v=>v!=uint.MaxValue)){if(v>=mesh.Positions.Length)throw new InvalidDataException("Vertex index "+sourceUid);if(mesh.BlendWeights==null)continue;var w=mesh.BlendWeights[v];var b=mesh.BlendIndices[v];float[] weights={w.X,w.Y,w.Z,w.W};int[] indices={b.X,b.Y,b.Z,b.W};for(int j=0;j<4;j++)if(weights[j]>0&&(indices[j]<0||indices[j]>=sub.BoneIndices.Length||sub.BoneIndices[indices[j]]>=model.Skin.Bones.Count))throw new InvalidDataException("Skin palette "+sourceUid);}}
   // Preserve X geometry, materials, weights and skin. Only use the native asset identity.
   objects.Objects.Clear();objects.Objects.Add(model);model.Name=uid.ToLowerInvariant();model.Id=nativeObject.Id;
   using(var replacement=new FarcArchive()){replacement.Add(nativeSet.FileName,objects);replacement.Add(nativeSet.TextureFileName,textures);replacement.Save(Path.Combine(folder,nativeSet.ArchiveFileName));}
   string skin=Path.Combine(root,"rom","skin_param"),sourceSkin=Path.Combine(skin,"ext_skp_"+sourceUid.ToLowerInvariant()+".txt");if(File.Exists(sourceSkin))File.Copy(sourceSkin,Path.Combine(skin,"ext_skp_"+uid.ToLowerInvariant()+".txt"),true);
   report.Add($"{f[p+"no"]}\t{uid}\t{sourceUid}\t{nativeSet.ArchiveFileName}\treplaced");replaced++;
  }}
  File.WriteAllLines(ConversionReports.PathFor(root,"ARCADE-X-ACCESSORY-REPLACES.tsv"),report);Console.WriteLine($"[NATIVE_ACCESSORY_REPLACE] {replaced} exact UID matches; {report.Count-1-replaced} retain native assets.");
 }
}
