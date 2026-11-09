using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.CriMw;
using MikuMikuLibrary.Archives.Extensions;
using MikuMikuLibrary.Databases;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Objects;
using MikuMikuLibrary.Textures;

namespace XRomConverter.Target.MegaMix;

internal static class NativeHairReplaceWriter
{
    private static Dictionary<string,string> Read(FarcArchive archive, string name)
    {
        using var stream = archive.Open(name, EntryStreamMode.MemoryStream);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n').Select(x=>x.Trim()).Where(x=>x.Contains('='))
            .Select(x=>x.Split('=',2)).ToDictionary(x=>x[0],x=>x[1]);
    }

    // A manual hairstyle may retain the native object UID. Replace that asset too,
    // so native menu lookups and the converted COS resolve identical geometry.
    public static int Write(string output, CpkArchive game, ObjectDatabase nativeDatabase, IEnumerable<short> sourceModules)
    {
        var selected=sourceModules.ToHashSet();
        var characters=new Dictionary<string,string>{{"MIKU","mik"},{"RIN","rin"},{"LEN","len"},{"LUKA","luk"},{"KAITO","kai"},{"MEIKO","mei"},{"NERU","ner"},{"HAKU","hak"},{"SAKINE","sak"},{"TETO","tet"}};
        using var mapStream=typeof(NativeHairReplaceWriter).Assembly.GetManifestResourceStream("XRomConverter.HairIdentityV5.csv")
            ?? throw new InvalidDataException("Missing native hair identity map.");
        using var mapReader=new StreamReader(mapStream);
        var allowed=new HashSet<(string Table,string Number)>();
        mapReader.ReadLine();
        while(mapReader.ReadLine() is { } line) {
            if(string.IsNullOrWhiteSpace(line)) continue;
            var fields=line.Split(',');
            if(selected.Contains(short.Parse(fields[0]))) allowed.Add((characters[fields[1]]+"itm_tbl.txt",fields[2]));
        }
        using var nativeTables=game.Open<FarcArchive>("rom_switch/rom/chritm_prop.farc");
        var nativeSets=nativeDatabase.ObjectSets.ToDictionary(s=>s.Name,StringComparer.OrdinalIgnoreCase);
        foreach(var databasePath in game.FileNames.Where(n=>n.EndsWith("mdata_obj_db.bin") && n.StartsWith("rom_ps4",StringComparison.OrdinalIgnoreCase)))
            foreach(var set in game.Open<ObjectDatabase>(databasePath).ObjectSets)
                nativeSets.TryAdd(set.Name,set);
        using var convertedTables=BinaryFile.Load<FarcArchive>(Path.Combine(output,"rom","mod_chritm_prop.farc"));
        int count=0;
        var written=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var name in convertedTables.FileNames.Where(n=>n.EndsWith("itm_tbl.txt")))
        {
            if(!nativeTables.FileNames.Contains(name)) continue;
            var native=Read(nativeTables,name); var converted=Read(convertedTables,name);
            var nativeRows=Enumerable.Range(0,int.Parse(native["item.length"]))
                .Select(i=>$"item.{i}.").ToDictionary(p=>native[p+"no"]);
            foreach(var prefix in Enumerable.Range(0,int.Parse(converted["item.length"])).Select(i=>$"item.{i}.")
                .OrderBy(p=>int.Parse(converted[p+"no"])))
            {
                if(!allowed.Contains((name,converted[prefix+"no"])) || converted.GetValueOrDefault(prefix+"sub_id")!="1" ||
                   !nativeRows.TryGetValue(converted[prefix+"no"],out var nativePrefix)) continue;
                var sourceUid=converted.GetValueOrDefault(prefix+"data.obj.0.uid","");
                var targetUid=native.GetValueOrDefault(nativePrefix+"data.obj.0.uid","");
                if(!sourceUid.EndsWith("_X") || native.GetValueOrDefault(nativePrefix+"sub_id")!="1" || targetUid.Length==0) continue;
                var sourceSet=converted[prefix+"objset.0"].ToLowerInvariant();
                var targetSet=native[nativePrefix+"objset.0"];
                if(!nativeSets.TryGetValue(targetSet,out var info)) throw new InvalidDataException($"Missing native hair object set {targetSet}.");
                // Some alternate hairstyles share a native mesh but differ in X.
                // Keep the primary replace instead of overwriting it with an alt.
                if(written.TryGetValue(targetUid,out var priorSource)) {
                    Console.WriteLine($"[NATIVE_HAIR_SHARED_ALIAS] {name} item {converted[prefix+"no"]}: {targetUid} already uses {priorSource}; alternate {sourceUid} retains its X COS link.");
                    continue;
                }
                written.Add(targetUid,sourceUid);
                var targetObject=info.Objects.Single(o=>o.Name.Equals(targetUid,StringComparison.OrdinalIgnoreCase));
                using var source=BinaryFile.Load<FarcArchive>(Path.Combine(output,"rom","objset",sourceSet+".farc"));
                var objects=source.Open<ObjectSet>(sourceSet+"_obj.bin");
                var hair=objects.Objects.Single(o=>o.Name.Equals(sourceUid,StringComparison.OrdinalIgnoreCase));
                objects.Objects.Clear(); objects.Objects.Add(hair);
                hair.Name=targetUid.ToLowerInvariant(); hair.Id=targetObject.Id;
                var textures=source.Open<TextureSet>(sourceSet+"_tex.bin");
                using var replacement=new FarcArchive();
                replacement.Add(info.FileName,objects);
                replacement.Add(info.TextureFileName,textures);
                replacement.Save(Path.Combine(output,"rom","objset",info.ArchiveFileName));
                var parameter=Path.Combine(output,"rom","skin_param","ext_skp_"+sourceUid.ToLowerInvariant()+".txt");
                if(File.Exists(parameter)) File.Copy(parameter,Path.Combine(output,"rom","skin_param","ext_skp_"+targetUid.ToLowerInvariant()+".txt"),true);
                Console.WriteLine($"[NATIVE_HAIR_REPLACE] {name} item {converted[prefix+"no"]}: {targetUid} <- {sourceUid}");
                count++;
            }
        }
        return count;
    }
}
