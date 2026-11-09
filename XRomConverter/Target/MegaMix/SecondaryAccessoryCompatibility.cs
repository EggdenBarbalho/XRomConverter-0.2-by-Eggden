using System.Text;
using System.Text.RegularExpressions;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.CriMw;
using MikuMikuLibrary.Archives.Extensions;
using MikuMikuLibrary.IO;
namespace XRomConverter.Target.MegaMix;
internal static class SecondaryAccessoryCompatibility {
 static Dictionary<string,string> Read(FarcArchive a,string n){using var s=a.Open(n,EntryStreamMode.MemoryStream);using var r=new StreamReader(s);return r.ReadToEnd().TrimStart('\uFEFF').Split('\n').Select(l=>l.TrimEnd('\r')).Where(l=>l.Contains('=')).Select(l=>l.Split('=',2)).ToDictionary(l=>l[0],l=>l[1]);}
 static void Save(string p,Dictionary<string,byte[]> entries){using var a=new FarcArchive();foreach(var e in entries)a.Add(e.Key,new MemoryStream(e.Value),false);a.Save(p);}
 static byte[] Encode(Dictionary<string,string> f)=>Encoding.UTF8.GetBytes(string.Join("\n",f.OrderBy(e=>e.Key,StringComparer.Ordinal).Select(e=>e.Key+"="+e.Value))+"\n");
 public static void Write(string root,string gameFolder){
  using var game=BinaryFile.Load<CpkArchive>(Path.Combine(gameFolder,"diva_main.cpk"));
  using var native=game.Open<FarcArchive>("rom_switch/rom/chritm_prop.farc");
  string rom=Path.Combine(root,"rom"),cp=Path.Combine(rom,"mod_gm_customize_item_tbl.farc"),tp=Path.Combine(rom,"mod_chritm_prop.farc");
  Dictionary<string,string> custom;using(var customArchive=BinaryFile.Load<FarcArchive>(cp))custom=Read(customArchive,"gm_customize_item_id.bin");
  var prefixes=custom.Keys.Where(k=>Regex.IsMatch(k,@"^cstm_item\.\d+\.id$")).Select(k=>k[..^2]).OrderBy(p=>int.Parse(p.Split('.')[1])).ToArray();
  var old=prefixes.Where(p=>custom[p+"parts"]!="KAMI").ToArray();
  var numbers=old.Select(p=>custom[p+"obj_id"]).ToHashSet();var ids=old.Select(p=>custom[p+"id"]).ToHashSet();
  // Native secondary accessory slots are 0/4/8/16. Never replace module hairs, faces or clothing.
  var result=new Dictionary<string,byte[]>();var report=new List<string>{"Table\tItem\tUID\tObjset"};
  using(var current=BinaryFile.Load<FarcArchive>(tp))foreach(var name in current.FileNames){
   if(!name.EndsWith("itm_tbl.txt")||!native.FileNames.Contains(name)){using var s=current.Open(name,EntryStreamMode.MemoryStream);using var m=new MemoryStream();s.CopyTo(m);result[name]=m.ToArray();continue;}
   var f=Read(current,name);var n=Read(native,name);
   var nrows=Enumerable.Range(0,int.Parse(n["item.length"])).Select(i=>$"item.{i}.").Where(p=>new[]{"0","4","8","16"}.Contains(n[p+"sub_id"])&&n.GetValueOrDefault(p+"objset.0","").StartsWith("CMNITM",StringComparison.OrdinalIgnoreCase)).ToDictionary(p=>n[p+"no"]);
   var existing=Enumerable.Range(0,int.Parse(f["item.length"])).Select(i=>$"item.{i}.").ToDictionary(p=>f[p+"no"]);int count=int.Parse(f["item.length"]);
   foreach(var row in nrows){string prefix=existing.GetValueOrDefault(row.Key)??$"item.{count++}.";
    if(existing.ContainsKey(row.Key)&&!new[]{"0","4","8","16"}.Contains(f[prefix+"sub_id"]))throw new InvalidDataException("Accessory collides with module item "+row.Key);
    foreach(var k in f.Keys.Where(k=>k.StartsWith(prefix,StringComparison.Ordinal)).ToArray())f.Remove(k);
    foreach(var e in n.Where(e=>e.Key.StartsWith(row.Value,StringComparison.Ordinal)))f[prefix+e.Key[row.Value.Length..]]=e.Value;
    report.Add($"{name}\t{row.Key}\t{f.GetValueOrDefault(prefix+"data.obj.0.uid")}\t{f.GetValueOrDefault(prefix+"objset.0")}");
   }
   // Remove X-only shared entries which have no native counterpart; no COS uses them.
   var kept=Enumerable.Range(0,count).Select(i=>$"item.{i}.").Where(p=>!(numbers.Contains(f[p+"no"])&&!nrows.ContainsKey(f[p+"no"]))).ToArray();
   var rebuilt=f.Where(e=>!e.Key.StartsWith("item.",StringComparison.Ordinal)).ToDictionary(e=>e.Key,e=>e.Value);
   for(int i=0;i<kept.Length;i++)foreach(var e in f.Where(e=>e.Key.StartsWith(kept[i],StringComparison.Ordinal)))rebuilt[$"item.{i}."+e.Key[kept[i].Length..]]=e.Value;
   rebuilt["item.length"]=kept.Length.ToString();result[name]=Encode(rebuilt);
  }
  Save(tp,result);
  var hairRows=prefixes.Where(p=>custom[p+"parts"]=="KAMI").ToArray();var hair=new Dictionary<string,string>{{"cstm_item.data_list.length",hairRows.Length.ToString()},{"patch","0"},{"version","0"}};
  for(int i=0;i<hairRows.Length;i++)foreach(var e in custom.Where(e=>e.Key.StartsWith(hairRows[i],StringComparison.Ordinal)))hair[$"cstm_item.{i}."+e.Key[hairRows[i].Length..]]=e.Value;
  Save(cp,new(){{"gm_customize_item_id.bin",Encode(hair)}});
  string lp=Path.Combine(rom,"lang2","mod_str_array.toml");var lines=File.ReadAllLines(lp).Where(l=>{var m=Regex.Match(l,@"^customize\.(\d+)\s*=");return !m.Success||!ids.Contains(m.Groups[1].Value);});File.WriteAllLines(lp,lines,new UTF8Encoding(false));
  File.WriteAllLines(ConversionReports.PathFor(root,"NATIVE-SECONDARY-ACCESSORIES.tsv"),report);
  Console.WriteLine($"[SECONDARY_ACCESSORIES] {report.Count-1} native item bindings; removed {old.Length} conflicting X menu overrides; kept {hairRows.Length} X hairstyles.");
 }
}

