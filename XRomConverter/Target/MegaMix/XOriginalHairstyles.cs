using System.Text;
using System.Text.Json;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.CriMw;
using MikuMikuLibrary.IO;

namespace XRomConverter.Target.MegaMix;

internal static class XOriginalHairstyles
{
    private static Dictionary<string,string> Read(FarcArchive archive,string name) {
        using var stream=archive.Open(name,EntryStreamMode.MemoryStream);using var reader=new StreamReader(stream);
        return reader.ReadToEnd().TrimStart('\uFEFF').Split('\n').Select(l=>l.TrimEnd('\r')).Where(l=>l.Contains('=')).Select(l=>l.Split('=',2)).ToDictionary(l=>l[0],l=>l[1]);
    }
    private static Dictionary<string,byte[]> Entries(string path) {
        var result=new Dictionary<string,byte[]>();if(!File.Exists(path))return result;
        using var a=BinaryFile.Load<FarcArchive>(path);foreach(var name in a.FileNames){using var s=a.Open(name,EntryStreamMode.MemoryStream);using var m=new MemoryStream();s.CopyTo(m);result[name]=m.ToArray();}return result;
    }
    public static void Write(string root,string gameDirectory) {
        string rom=Path.Combine(root,"rom"),customPath=Path.Combine(rom,"mod_gm_customize_item_tbl.farc");
        using var modulesArchive=BinaryFile.Load<FarcArchive>(Path.Combine(rom,"gm_module_tbl.farc"));var modules=Read(modulesArchive,"gm_module_id.bin");
        using var itemArchive=BinaryFile.Load<FarcArchive>(Path.Combine(rom,"mod_chritm_prop.farc"));
        var reservations=new Dictionary<string,HashSet<int>>();var identities=new Dictionary<int,HashSet<string>>();
        string Identity(Dictionary<string,string> t,string p)=>string.Join("|",new[]{"parts","bind_module","chara","obj_id","name"}.Select(k=>t.GetValueOrDefault(p+k,"")));
        var occupied=new HashSet<int>();Dictionary<string,string>? template=null;
        void Scan(FarcArchive archive) {
            if(!archive.FileNames.Contains("gm_customize_item_id.bin"))return;
            var table=Read(archive,"gm_customize_item_id.bin");
            foreach(var key in table.Keys.Where(k=>k.StartsWith("cstm_item.")&&k.EndsWith(".id"))){int reserved=int.Parse(table[key]);occupied.Add(reserved);string identity=Identity(table,key[..^2]);if(!reservations.ContainsKey(identity))reservations[identity]=new();reservations[identity].Add(reserved);if(!identities.ContainsKey(reserved))identities[reserved]=new();identities[reserved].Add(identity);}
            if(template==null)foreach(var key in table.Keys.Where(k=>k.EndsWith(".parts")&&table[k]=="KAMI")) {
                string prefix=key[..^5];template=table.Where(x=>x.Key.StartsWith(prefix)).ToDictionary(x=>x.Key[prefix.Length..],x=>x.Value);break;
            }
        }
        foreach(var file in Directory.GetFiles(gameDirectory,"*.cpk").Order()) {
            using var cpk=BinaryFile.Load<CpkArchive>(file);
            foreach(var name in cpk.FileNames.Where(n=>n.Contains("gm_customize_item_tbl")&&n.EndsWith(".farc"))) {
                using var stream=cpk.Open(name,EntryStreamMode.MemoryStream);using var archive=BinaryFile.Load<FarcArchive>(stream);Scan(archive);
            }
        }
        string mods=Path.Combine(gameDirectory,"mods");
        if(Directory.Exists(mods))foreach(var file in Directory.GetFiles(mods,"*gm_customize_item_tbl*.farc",SearchOption.AllDirectories).Order()) {using var archive=BinaryFile.Load<FarcArchive>(file);Scan(archive);}
        if(template==null)throw new InvalidDataException("Native KAMI template missing");
        var entries=Entries(customPath);Dictionary<string,string> custom;
        if(entries.ContainsKey("gm_customize_item_id.bin")){using var archive=BinaryFile.Load<FarcArchive>(customPath);custom=Read(archive,"gm_customize_item_id.bin");if(custom.Remove("cust_item.data_list.length",out var legacy))custom.TryAdd("cstm_item.data_list.length",legacy);Scan(archive);}
        else custom=new(){{"cstm_item.data_list.length","0"},{"patch","0"},{"version","0"}};
        var chars=new Dictionary<string,string>{{"MIKU","mik"},{"RIN","rin"},{"LEN","len"},{"LUKA","luk"},{"KAITO","kai"},{"MEIKO","mei"}};
        var tables=new Dictionary<string,Dictionary<string,string>>();
        string languagePath=Path.Combine(rom,"lang2","mod_str_array.toml");string language=File.Exists(languagePath)?File.ReadAllText(languagePath):"";
        foreach(System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(language,@"(?m)^customize\.(\d+)\s*="))occupied.Add(int.Parse(m.Groups[1].Value));
        int count=int.Parse(custom["cstm_item.data_list.length"]),added=0;bool changed=false;var report=new List<string>{"Module\tCharacter\tName\tMenuID\tHairItem\tUID"};
        for(int i=0;i<int.Parse(modules["module.data_list.length"]);i++) {
            string prefix=$"module.{i}.",character=modules[prefix+"chara"];int costume=int.Parse(modules[prefix+"cos"].Replace("COS_",""))-1;
            // Exclusive X costumes are allocated by the converter in this range.
            if((costume<9000&&modules[prefix+"name"].Trim()!="Racing Miku 2016")||!chars.TryGetValue(character,out var stem))continue;
            if(!tables.TryGetValue(character,out var table))tables[character]=table=Read(itemArchive,stem+"itm_tbl.txt");
            string cos=Enumerable.Range(0,int.Parse(table["cos.length"])).Select(c=>$"cos.{c}.").Single(c=>table[c+"id"]==costume.ToString());
            var numbers=Enumerable.Range(0,int.Parse(table[cos+"item.length"])).Select(j=>table[cos+"item."+j]).ToHashSet();
            string hair=Enumerable.Range(0,int.Parse(table["item.length"])).Select(j=>$"item.{j}.").Single(p=>table[p+"sub_id"]=="1"&&numbers.Contains(table[p+"no"]));
            string module=modules[prefix+"id"],uid=table[hair+"data.obj.0.uid"];
            string? existing=Enumerable.Range(0,count).Select(j=>$"cstm_item.{j}.").SingleOrDefault(p=>custom[p+"parts"]=="KAMI"&&custom[p+"obj_id"]==table[hair+"no"]&&custom[p+"chara"]==character);
            int id;
            if(existing!=null) {id=int.Parse(custom[existing+"id"]);if(custom[existing+"obj_id"]!=table[hair+"no"])throw new InvalidDataException("Existing X hairstyle points to another item");}
            else {
                string identity=string.Join("|","KAMI",module,character,table[hair+"no"],modules[prefix+"name"].Trim()+" Hair");
                var reusable=reservations.GetValueOrDefault(identity,new()).Where(n=>n>=10000&&identities[n].Count==1).Order().ToArray();
                id=reusable.Length>0?reusable[0]:10000;while(occupied.Contains(id)&&!reusable.Contains(id))id++;if(id>32767)throw new InvalidDataException("Local hairstyle ID range exhausted");occupied.Add(id);
                var row=new Dictionary<string,string>(template);row["id"]=id.ToString();row["bind_module"]=module;row["chara"]=character;row["obj_id"]=table[hair+"no"];row["name"]=modules[prefix+"name"].Trim()+" Hair";row["parts"]="KAMI";row["shop_price"]="0";row["sort_index"]=(10000+added).ToString();row["ng"]="0";row["shop_st_year"]="2025";row["shop_st_month"]="3";row["shop_st_day"]="9";
                foreach(var entry in row)custom.Add($"cstm_item.{count}."+entry.Key,entry.Value);
                count++;added++;language+="\ncustomize."+id+" = "+JsonSerializer.Serialize(row["name"])+"\n";
            }
            string registration=existing??$"cstm_item.{count-1}.";
            // Hairstyles follow ownership of their associated module.
            if(custom[registration+"bind_module"]!=module){custom[registration+"bind_module"]=module;changed=true;}
            foreach(var date in new Dictionary<string,string>{{"shop_st_year","2025"},{"shop_st_month","3"},{"shop_st_day","9"}})if(custom.GetValueOrDefault(registration+date.Key)!=date.Value){custom[registration+date.Key]=date.Value;changed=true;}
            report.Add($"{module}\t{character}\t{modules[prefix+"name"]}\t{id}\t{table[hair+"no"]}\t{uid}");
        }
        if(added>0||changed) {
            custom["cstm_item.data_list.length"]=count.ToString();entries["gm_customize_item_id.bin"]=Encoding.UTF8.GetBytes(string.Join("\n",custom.OrderBy(x=>x.Key,StringComparer.Ordinal).Select(x=>x.Key+"="+x.Value))+"\n");
            using var archive=new FarcArchive();foreach(var entry in entries)archive.Add(entry.Key,new MemoryStream(entry.Value),leaveOpen:false);archive.Save(customPath);
            Directory.CreateDirectory(Path.GetDirectoryName(languagePath)!);File.WriteAllText(languagePath,language,new UTF8Encoding(false));
        }
        XHairstyleIcons.Write(root,gameDirectory,report.Skip(1).Select(r=>r.Split('\t')).Select(r=>(int.Parse(r[3]),int.Parse(r[0]))));
        File.WriteAllLines(ConversionReports.PathFor(root,"X-HAIRSTYLE-REGISTRY.tsv"),report);
        Console.WriteLine($"[X_HAIRSTYLES] {report.Count-1} X module hairstyles registered; {added} new menu entries.");
    }
}
