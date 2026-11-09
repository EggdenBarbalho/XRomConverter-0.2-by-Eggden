using System.Text.Json;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.Extensions;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Objects;
using MikuMikuLibrary.Objects.Extra.Blocks;
namespace XRomConverter.Target.MegaMix;

internal static class OsageModuleCoverage
{
    public static void Write(string root, string? destination = null)
    {
Dictionary<string,string> Text(string text)=>text.TrimStart('\uFEFF').Split('\n').Select(l=>l.Trim()).Where(l=>l.Contains('=')&&!l.StartsWith('#')).Select(l=>l.Split('=',2)).ToDictionary(l=>l[0],l=>l[1]);
Dictionary<string,string> Read(FarcArchive a,string n){using var s=a.Open(n,EntryStreamMode.MemoryStream);using var r=new StreamReader(s);return Text(r.ReadToEnd());}
using var ma=BinaryFile.Load<FarcArchive>(Path.Combine(root,"rom","gm_module_tbl.farc"));var modules=Read(ma,"gm_module_id.bin");
using var tables=BinaryFile.Load<FarcArchive>(Path.Combine(root,"rom","mod_chritm_prop.farc"));
var settings=Text(File.ReadAllText(Path.Combine(root,"rom","skin_param","mod_osage_setting.txt")));
var regs=Enumerable.Range(0,int.Parse(settings["obj.length"])).ToDictionary(i=>settings[$"obj.{i}.name"],i=>settings[$"obj.{i}.cat"],StringComparer.OrdinalIgnoreCase);
var chars=new Dictionary<string,string>{{"MIKU","mik"},{"RIN","rin"},{"LEN","len"},{"LUKA","luk"},{"KAITO","kai"},{"MEIKO","mei"},{"NERU","ner"},{"HAKU","hak"},{"SAKINE","sak"},{"TETO","tet"}};
var cache=new Dictionary<string,ObjectSet>(StringComparer.OrdinalIgnoreCase);
var rows=new List<object>();
for(int m=0;m<int.Parse(modules["module.data_list.length"]);m++){
 string p=$"module.{m}.",ch=modules[p+"chara"];if(!chars.TryGetValue(ch,out var stem))continue;
 var t=Read(tables,stem+"itm_tbl.txt");int cos=int.Parse(modules[p+"cos"].Replace("COS_",""))-1;
 var cp=Enumerable.Range(0,int.Parse(t["cos.length"])).Select(i=>$"cos.{i}.").SingleOrDefault(x=>t[x+"id"]==cos.ToString());
 if(cp==null){rows.Add(new{Module=modules[p+"id"],Name=modules[p+"name"],Character=ch,Cos=cos,Status="missing-cos"});continue;}
 var nums=Enumerable.Range(0,int.Parse(t[cp+"item.length"])).Select(i=>t[cp+"item."+i]).ToHashSet();
 var hairs=Enumerable.Range(0,int.Parse(t["item.length"])).Select(i=>$"item.{i}.").Where(x=>nums.Contains(t[x+"no"])&&t.GetValueOrDefault(x+"sub_id")=="1").ToArray();
 foreach(var hp in hairs){string uid=t.GetValueOrDefault(hp+"data.obj.0.uid",""),setname=t.GetValueOrDefault(hp+"objset.0","");
 string file=Path.Combine(root,"rom","objset",setname.ToLowerInvariant()+".farc");
 if(!File.Exists(file)){rows.Add(new{Module=modules[p+"id"],Name=modules[p+"name"],Character=ch,Cos=cos,UID=uid,Set=setname,Status="missing-local-archive"});continue;}
 if(!cache.TryGetValue(setname,out var set)){using var a=BinaryFile.Load<FarcArchive>(file);var entry=a.FileNames.Single(n=>n.EndsWith("_obj.bin",StringComparison.OrdinalIgnoreCase));cache[setname]=set=a.Open<ObjectSet>(entry);}
 var o=set.Objects.SingleOrDefault(x=>x.Name.Equals(uid,StringComparison.OrdinalIgnoreCase));var osg=o?.Skin?.Blocks.OfType<OsageBlock>().ToArray()??Array.Empty<OsageBlock>();
 regs.TryGetValue(uid,out var cat); var lower=uid.ToLowerInvariant(); int suffix=lower.IndexOf("_acc_",StringComparison.Ordinal); if(suffix<0)suffix=lower.IndexOf("_headslot_",StringComparison.Ordinal); string? sourceUid=suffix<0?null:uid[..suffix]+"_X"; if(lower=="mikitm601_atam_headphones_x")sourceUid="MIKITM601_ATAM_ATAMA_01__DIVSKN_X"; string? sourceCategory=null; if(sourceUid!=null)regs.TryGetValue(sourceUid,out sourceCategory);
 rows.Add(new{Module=modules[p+"id"],Name=modules[p+"name"],Character=ch,Cos=cos,UID=uid,Set=setname,Category=cat,SourceUID=sourceUid,SourceCategory=sourceCategory,Status=o==null?"missing-object":osg.Length==0?"no-osg":cat!=null?"registered":"unregistered",Roots=osg.Select(b=>new{b.ExternalName,b.ParentName,Nodes=b.Nodes.Count}).ToArray()});
 }
}
File.WriteAllText(ConversionReports.PathFor(destination ?? root,"OSAGE-MODULE-COVERAGE.json"),JsonSerializer.Serialize(new { ModulesDeclared=int.Parse(modules["module.data_list.length"]), HairRows=rows.Count, Modules=rows },new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine("[OSAGE_COVERAGE] "+modules["module.data_list.length"]+" modules inspected; "+rows.Count+" hair bindings reported.");
    }
}



