using System.Text;
using System.Text.Json;
using System.IO.Compression;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.CriMw;
using MikuMikuLibrary.Archives.Extensions;
using MikuMikuLibrary.Databases;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Objects;
using MikuMikuLibrary.Objects.Extra.Blocks;
using MikuMikuLibrary.Objects.Extra.Parameters;
using MikuMikuLibrary.Textures;
using XRomConverter.Ids;

namespace XRomConverter.Target.MegaMix;

// Compose focused legacy conversions with the user-approved P4 runtime. Shared
// tables are merged explicitly; approved physical and facial files are frozen.
internal static class ComparisonModBuilder
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static string Hash(string p) => PhysicalOccupancy.Hash(File.ReadAllBytes(p));
    private static Dictionary<string,string> Hashes(string root) => Directory.GetFiles(root,"*",SearchOption.AllDirectories)
        .ToDictionary(p=>Path.GetRelativePath(root,p).Replace('\\','/'),Hash,StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string,string> Fields(string text) => text.Split('\n').Select(x=>x.TrimEnd('\r').TrimStart('\uFEFF'))
        .Where(x=>x.Length>0&&!x.StartsWith('#')).Select(x=>x.Split('=',2)).ToDictionary(x=>x[0],x=>x[1],StringComparer.Ordinal);
    private static string Render(Dictionary<string,string> f) => string.Join("\n",f.OrderBy(x=>x.Key,StringComparer.Ordinal).Select(x=>x.Key+"="+x.Value))+"\n";
    private static string Text(string archive,string entry) { using var f=BinaryFile.Load<FarcArchive>(archive);using var s=f.Open(entry,EntryStreamMode.MemoryStream);using var r=new StreamReader(s,Encoding.UTF8);return r.ReadToEnd(); }
    private static void Table(string p,string entry,Dictionary<string,string> f) { using var a=new FarcArchive();a.Add(entry,new MemoryStream(Encoding.UTF8.GetBytes(Render(f))),leaveOpen:false);a.Save(p); }
    private static Dictionary<string,string> Row(Dictionary<string,string> f,string prefix) => f.Where(x=>x.Key.StartsWith(prefix,StringComparison.Ordinal)).ToDictionary(x=>x.Key[prefix.Length..],x=>x.Value);
    private static void AddRow(Dictionary<string,string> target,string prefix,Dictionary<string,string> row) { foreach(var x in row)target.Add(prefix+x.Key,x.Value); }
    private static uint Allocate(HashSet<uint> occupied,uint start) { while(occupied.Contains(start))start++;occupied.Add(start);return start; }
    private static void Require(bool condition,string message) { if(!condition)throw new InvalidDataException(message); }

    internal static int Run(string[] args)
    {
        string? temporary=null;
        try
        {
            Require(args.Length==8||((args.Length==10||args.Length==12)&&args[8]=="--body-skin-policy"&&args[9] is "complete" or "jsekai"),"Usage: build-comparison-mod APPROVED_P4 SNOW_RUNTIME NEWTYPE_RUNTIME JSEKAI_ZIP COMPLETE_ZIP GAME NEW_OUTPUT [--body-skin-policy complete|jsekai]");
            string bodyPolicy=args.Length>=10?args[9]:"jsekai";
            Require(args.Length!=12||args[10]=="--prior-comparison","Expected --prior-comparison PATH.");
            string? prior=args.Length==12?Path.GetFullPath(args[11]):null;
            string installedComparison=Path.Combine(Path.GetFullPath(args[6]),"mods","XRom02-P4-Snow322-NewType");
            Dictionary<string,string>? priorHashes=null;JsonDocument? priorManifest=null;ObjectDatabase? priorObjects=null;TextureDatabase? priorTextures=null;SpriteDatabase? priorSprites=null;
            if(prior!=null)
            {
                priorHashes=Hashes(prior);var rom=priorHashes.Where(x=>x.Key.StartsWith("rom/")).OrderBy(x=>x.Key).ToArray();
                Require(Directory.Exists(installedComparison)&&rom.SequenceEqual(Hashes(installedComparison).Where(x=>x.Key.StartsWith("rom/")).OrderBy(x=>x.Key)),"Installed comparison ROM differs from the explicitly selected prior package.");
                priorManifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(prior,"build-manifest.json")));Require(priorManifest.RootElement.GetProperty("phase").GetString()=="three-module-runtime-comparison","Prior package is not a comparison build.");
                priorObjects=BinaryFile.Load<ObjectDatabase>(Path.Combine(prior,"rom/objset/mod_obj_db.bin"));priorTextures=BinaryFile.Load<TextureDatabase>(Path.Combine(prior,"rom/objset/mod_tex_db.bin"));priorSprites=BinaryFile.Load<SpriteDatabase>(Path.Combine(prior,"rom/2d/mod_spr_db.bin"));
            }
            string[] roots=args.Skip(1).Take(3).Select(Path.GetFullPath).ToArray();string zip=Path.GetFullPath(args[4]),completeZip=Path.GetFullPath(args[5]),game=Path.GetFullPath(args[6]),output=Path.GetFullPath(args[7]);
            Require(!Directory.Exists(output)&&!File.Exists(output),"Output must not already exist.");
            Require(!output.StartsWith(game+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"Output must be outside the game installation.");
            var inputs=roots.Select(Hashes).ToArray();string zipHash=Hash(zip),completeZipHash=Hash(completeZip);
            using(var approved=JsonDocument.Parse(File.ReadAllText(Path.Combine(roots[0],"build-manifest.json"))))
                foreach(var file in approved.RootElement.GetProperty("emittedFiles").EnumerateArray())
                    Require(inputs[0].GetValueOrDefault(file.GetProperty("path").GetString()!)==file.GetProperty("sha256").GetString(),"Approved P4 manifest/hash differs.");
            int[] moduleIds={662,663,664},costumes={300,301,302};
            string[] expectedNames={"P4: Dancing All Night Miku","Snow Miku 2016","Hatsune Miku New Type"};
            string[][] setsExpected={new[]{"MIKITM153_X","MIKITM553_X","MIKITM753_X"},new[]{"MIKITM151_X","MIKITM351_X","MIKITM551_X","MIKITM751_X","MIKITM924_X","MIKITM925_X"},new[]{"MIKITM176_X","MIKITM376_X","MIKITM576_X","MIKITM776_X"}};
            var modules=roots.Select(r=>ModuleTableReference.ReadFields(Path.Combine(r,"rom/gm_module_tbl.farc"))).ToArray();
            var items=roots.Select(r=>Fields(Text(Path.Combine(r,"rom/mod_chritm_prop.farc"),"mikitm_tbl.txt"))).ToArray();
            var dbs=roots.Select(r=>BinaryFile.Load<ObjectDatabase>(Path.Combine(r,"rom/objset/mod_obj_db.bin"))).ToArray();
            var texs=roots.Select(r=>BinaryFile.Load<TextureDatabase>(Path.Combine(r,"rom/objset/mod_tex_db.bin"))).ToArray();
            var sprs=roots.Select(r=>BinaryFile.Load<SpriteDatabase>(Path.Combine(r,"rom/2d/mod_spr_db.bin"))).ToArray();
            for(int i=0;i<3;i++)
            {
                Require(modules[i]["module.data_list.length"]=="1"&&modules[i]["module.0.id"]==moduleIds[i].ToString()&&modules[i]["module.0.cos"]==$"COS_{costumes[i]+1}"&&modules[i]["module.0.name"]==expectedNames[i],"Unexpected input module/costume identity.");
                Require(items[i]["cos.length"]=="1"&&items[i]["cos.0.id"]==costumes[i].ToString(),"Unexpected input costume table.");
                Require(dbs[i].ObjectSets.Select(s=>s.Name).ToHashSet().SetEquals(setsExpected[i].Append("MIKITM000")),"Input component set differs from the complete reference chain.");
                foreach(var s in dbs[i].ObjectSets)Require(File.Exists(Path.Combine(roots[i],"rom/objset",s.ArchiveFileName)),"Missing model archive: "+s.Name);
                Require(sprs[i].SpriteSets.Count==1&&sprs[i].SpriteSets[0].Name==$"SPR_SEL_MD{moduleIds[i]}CMN","Unexpected module sprite.");
                ValidateItems(items[i],dbs[i],costumes[i],setsExpected[i].Length);
            }
            Require(inputs[0].Keys.Count(p=>p.StartsWith("rom/rob/"))==31,"Approved P4 must include its complete facial bank.");
            Require(Hash(Path.Combine(roots[0],"rom/skin_param/ext_skp_mikitm753_atam_atama_153__divskn_x.txt"))=="0D51456D8C7BCDBE2175AF2F4DB20B313D982B7563438284DB08BE07FAE6AF78","Approved P4 skin hash differs.");
            Require(Hash(Path.Combine(roots[0],"rom/bone_data.bin"))==Hash(Path.Combine(roots[1],"rom/bone_data.bin"))&&Hash(Path.Combine(roots[0],"rom/bone_data.bin"))==Hash(Path.Combine(roots[2],"rom/bone_data.bin")),"Generated skeleton differs from approved P4.");
            Require(Hash(Path.Combine(roots[0],"rom/objset/mikitm000.farc"))==Hash(Path.Combine(roots[1],"rom/objset/mikitm000.farc"))&&Hash(Path.Combine(roots[0],"rom/objset/mikitm000.farc"))==Hash(Path.Combine(roots[2],"rom/objset/mikitm000.farc")),"Shared head differs from approved P4.");

            // Reserve IDs from the base game and every installed declaration. This
            // also avoids collisions if a currently disabled model mod is enabled.
            var setIds=new HashSet<uint>();var textureIds=new HashSet<uint>();var spriteIds=new HashSet<uint>();var spriteSetIds=new HashSet<uint>();var spriteTextureIds=new HashSet<uint>();
            void OccupyObjects(ObjectDatabase d){foreach(var s in d.ObjectSets)setIds.Add(s.Id);}
            void OccupyTextures(TextureDatabase d){foreach(var t in d.Textures)textureIds.Add(t.Id);}
            void OccupySprites(SpriteDatabase d){foreach(var s in d.SpriteSets){spriteSetIds.Add(s.Id);foreach(var x in s.Sprites)spriteIds.Add(x.Id);foreach(var x in s.Textures)spriteTextureIds.Add(x.Id);}}
            using var cpk=BinaryFile.Load<CpkArchive>(Path.Combine(game,"diva_main.cpk"));
            OccupyObjects(cpk.Open<ObjectDatabase>("rom_steam/rom/objset/obj_db.bin"));OccupyTextures(cpk.Open<TextureDatabase>("rom_steam/rom/objset/tex_db.bin"));OccupySprites(cpk.Open<SpriteDatabase>("rom_steam/rom/2d/spr_db.bin"));
            foreach(string mod in Directory.GetDirectories(Path.Combine(game,"mods")))
            {
                foreach(string file in Directory.GetFiles(mod,"mod_obj_db.bin",SearchOption.AllDirectories))OccupyObjects(BinaryFile.Load<ObjectDatabase>(file));
                foreach(string file in Directory.GetFiles(mod,"mod_tex_db.bin",SearchOption.AllDirectories))OccupyTextures(BinaryFile.Load<TextureDatabase>(file));
                foreach(string file in Directory.GetFiles(mod,"mod_spr_db.bin",SearchOption.AllDirectories))OccupySprites(BinaryFile.Load<SpriteDatabase>(file));
            }
            var physical=PhysicalOccupancy.Scan(game);Require(physical.Complete,"Physical occupancy scan incomplete.");
            setIds.UnionWith(physical.ObjectSets.Select(x=>x.Id));textureIds.UnionWith(physical.Textures.Select(x=>x.Id));
            var metadata=MetadataOccupancy.Scan(game);Require(metadata.Complete,"Installation metadata scan incomplete.");
            spriteSetIds.UnionWith(metadata.Ids.Where(x=>x.Namespace=="sprite-set").Select(x=>x.Id));spriteIds.UnionWith(metadata.Ids.Where(x=>x.Namespace=="sprite").Select(x=>x.Id));spriteTextureIds.UnionWith(metadata.Ids.Where(x=>x.Namespace=="sprite-texture").Select(x=>x.Id));
            foreach(var d in dbs.Take(1))OccupyObjects(d);OccupyTextures(texs[0]);OccupySprites(sprs[0]);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);temporary=Path.Combine(Path.GetDirectoryName(output)!,".comparison-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            void Copy(string root,string rel) {string dest=Path.Combine(temporary,rel);Directory.CreateDirectory(Path.GetDirectoryName(dest)!);File.Copy(Path.Combine(root,rel),dest,overwrite:false);}
            foreach(string rel in inputs[0].Keys.Where(x=>x.StartsWith("rom/")))Copy(roots[0],rel);
            var objectDb=dbs[0];var textureDb=texs[0];var spriteDb=sprs[0];var changes=new List<object>();
            for(int i=1;i<3;i++)
            {
                var textureMap=new Dictionary<uint,uint>();
                foreach(var texture in texs[i].Textures)
                {
                    var existing=textureDb.Textures.SingleOrDefault(x=>x.Name==texture.Name);
                    uint id=existing?.Id??priorTextures?.Textures.SingleOrDefault(x=>x.Name==texture.Name)?.Id??Allocate(textureIds,90000);textureMap.Add(texture.Id,id);
                    if(existing==null){texture.Id=id;textureDb.Textures.Add(texture);}
                }
                foreach(var info in dbs[i].ObjectSets.Where(x=>x.Name!="MIKITM000"))
                {
                    uint before=info.Id;info.Id=priorObjects?.ObjectSets.SingleOrDefault(x=>x.Name==info.Name)?.Id??Allocate(setIds,42000);
                    Require(info.Id<=ushort.MaxValue,"Classic object-set ID range exhausted.");
                    using var farc=BinaryFile.Load<FarcArchive>(Path.Combine(roots[i],"rom/objset",info.ArchiveFileName));
                    string objEntry=farc.FileNames.Single(x=>x.EndsWith("_obj.bin")),texEntry=farc.FileNames.Single(x=>x.EndsWith("_tex.bin"));
                    var model=farc.Open<ObjectSet>(objEntry);var textures=farc.Open<TextureSet>(texEntry);
                    Require(model.Objects.Count==1&&info.Objects.Count==1,"Expected one-object component.");
                    using var raw=farc.Open(objEntry,EntryStreamMode.MemoryStream);using var mem=new MemoryStream();raw.CopyTo(mem);
                    var patched=ClassicObjectSetIdPatcher.Apply(mem.ToArray(),model,model.Objects[0].Name,model.Objects[0].Id,textureMap);
                    ClassicObjectSetIdPatcher.Verify(mem.ToArray(),patched.Bytes,patched.Patches,model.Objects[0].Name);
                    string dest=Path.Combine(temporary,"rom/objset",info.ArchiveFileName);
                    // Classic TXP payloads have no global texture IDs. Their order
                    // corresponds to model.TextureIds; retain every texture byte.
                    using(var written=new FarcArchive()){written.Add(objEntry,new MemoryStream(patched.Bytes),leaveOpen:false);using var tx=farc.Open(texEntry,EntryStreamMode.MemoryStream);using var tm=new MemoryStream();tx.CopyTo(tm);written.Add(texEntry,new MemoryStream(tm.ToArray()),leaveOpen:false);written.Save(dest);}
                    objectDb.ObjectSets.Add(info);changes.Add(new {set=info.Name,originalSetId=before,targetSetId=info.Id,identifierPatches=patched.Patches});
                }
                foreach(string file in Directory.GetFiles(Path.Combine(roots[i],"rom/skin_param"),"ext_skp_*.txt"))Copy(roots[i],"rom/skin_param/"+Path.GetFileName(file));
                var spr=sprs[i].SpriteSets.Single();var previousSprite=priorSprites?.SpriteSets.SingleOrDefault(x=>x.Name==spr.Name);spr.Id=previousSprite?.Id??Allocate(spriteSetIds,30000);foreach(var s in spr.Sprites)s.Id=previousSprite?.Sprites.SingleOrDefault(x=>x.Name==s.Name)?.Id??Allocate(spriteIds,30000);foreach(var t in spr.Textures)t.Id=previousSprite?.Textures.SingleOrDefault(x=>x.Name==t.Name)?.Id??Allocate(spriteTextureIds,30000);spriteDb.SpriteSets.Add(spr);
                Copy(roots[i],$"rom/2d/spr_sel_md{moduleIds[i]}cmn.farc");
            }
            objectDb.Save(Path.Combine(temporary,"rom/objset/mod_obj_db.bin"));textureDb.Save(Path.Combine(temporary,"rom/objset/mod_tex_db.bin"));spriteDb.Save(Path.Combine(temporary,"rom/2d/mod_spr_db.bin"));

            var moduleTable=new Dictionary<string,string>();var itemTable=new Dictionary<string,string>();var occupiedItems=new HashSet<uint>();
            for(int i=0;i<int.Parse(items[0]["item.length"]);i++)occupiedItems.Add(uint.Parse(items[0][$"item.{i}.no"]));
            foreach(var id in metadata.Ids.Where(x=>x.Namespace=="item"&&x.Scope=="MIK"))occupiedItems.Add(id.Id);
            Require(!metadata.Ids.Any(x=>x.Namespace=="module"&&(x.Id==663||x.Id==664)&&(prior==null||!metadata.Sources[x.Source].Path.StartsWith(installedComparison+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))),"Comparison module IDs are occupied.");
            Require(!metadata.Ids.Any(x=>x.Namespace=="costume"&&x.Scope=="MIK"&&(x.Id==301||x.Id==302)&&(prior==null||!metadata.Sources[x.Source].Path.StartsWith(installedComparison+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))),"Comparison costume IDs are occupied.");
            int itemCount=0;var itemMaps=new List<Dictionary<int,int>>();
            for(int i=0;i<3;i++)
            {
                var row=Row(modules[i],"module.0.");if(i>0){row["attr"]="8";row["sort_index"]=(int.Parse(modules[0]["module.0.sort_index"])+i).ToString();}AddRow(moduleTable,$"module.{i}.",row);
                var map=new Dictionary<int,int>();int count=int.Parse(items[i]["item.length"]);
                for(int j=0;j<count;j++){var it=Row(items[i],$"item.{j}.");int old=int.Parse(it["no"]),id=i==0?old:priorManifest!=null?priorManifest.RootElement.GetProperty("modules").EnumerateArray().Single(x=>x.GetProperty("targetModule").GetInt32()==moduleIds[i]).GetProperty("items").GetProperty(old.ToString()).GetInt32():checked((int)Allocate(occupiedItems,1055));map.Add(old,id);it["no"]=id.ToString();AddRow(itemTable,$"item.{itemCount++}.",it);}
                var cos=Row(items[i],"cos.0.");foreach(string k in cos.Keys.Where(x=>x.StartsWith("item.")&&x!="item.length").ToArray())cos[k]=map[int.Parse(cos[k])].ToString();AddRow(itemTable,$"cos.{i}.",cos);itemMaps.Add(map);
            }
            moduleTable.Add("module.data_list.length","3");itemTable.Add("cos.length","3");itemTable.Add("item.length",itemCount.ToString());Table(Path.Combine(temporary,"rom/gm_module_tbl.farc"),"gm_module_id.bin",moduleTable);Table(Path.Combine(temporary,"rom/mod_chritm_prop.farc"),"mikitm_tbl.txt",itemTable);
            string lang=File.ReadAllText(Path.Combine(roots[0],"rom/lang2/mod_str_array.toml"));for(int i=1;i<3;i++)lang+=File.ReadAllText(Path.Combine(roots[i],"rom/lang2/mod_str_array.toml"));File.WriteAllText(Path.Combine(temporary,"rom/lang2/mod_str_array.toml"),lang,Encoding.UTF8);
            var osage=Fields(File.ReadAllText(Path.Combine(roots[0],"rom/skin_param/mod_osage_setting.txt")));int objectCount=int.Parse(osage["obj.length"]);
            for(int i=1;i<3;i++){var incoming=Fields(File.ReadAllText(Path.Combine(roots[i],"rom/skin_param/mod_osage_setting.txt")));Require(incoming.Where(x=>x.Key.StartsWith("cat.")).All(x=>osage.GetValueOrDefault(x.Key)==x.Value),"Shared osage categories differ.");for(int j=0;j<int.Parse(incoming["obj.length"]);j++)AddRow(osage,$"obj.{objectCount++}.",Row(incoming,$"obj.{j}."));}
            osage["obj.length"]=objectCount.ToString();File.WriteAllText(Path.Combine(temporary,"rom/skin_param/mod_osage_setting.txt"),Render(osage),Encoding.UTF8);

            var skins=new List<object>();using var catalog=ZipFile.OpenRead(zip);using var complete=ZipFile.OpenRead(completeZip);
            foreach(string name in new[]{"ext_skp_mikitm151_joha_outer_151__divskn_x.txt","ext_skp_mikitm751_atam_atama_151__divskn_x.txt","ext_skp_mikitm925_joha_neck_125__divskn_x.txt"})
            {
                var entry=catalog.Entries.Single(e=>e.FullName=="skin_param/"+name);using var s=entry.Open();using var memory=new MemoryStream();s.CopyTo(memory);byte[] bytes=memory.ToArray();string rawHash=PhysicalOccupancy.Hash(bytes);string skinText=Encoding.UTF8.GetString(bytes);const string typo="c_hair_twin_r_osg.root.coli.9.bone.0.posy=0.000000a";bool corrected=skinText.Contains(typo);if(corrected)bytes=Encoding.UTF8.GetBytes(skinText.Replace(typo,"c_hair_twin_r_osg.root.coli.9.bone.0.posy=0.000000"));string dest=Path.Combine(temporary,"rom/skin_param",name);File.WriteAllBytes(dest,bytes);skins.Add(new {file=name,source="JSeKai 6.0",sourceSha256=rawHash,knownMalformedLiteralCorrected=corrected,initialStagedSha256=Hash(dest)});
            }
            // Snow's body repeats the seven neck OSGs from its own accessory. Add
            // only absent parameters whose full block topology is identical.
            string bodyPath=Path.Combine(temporary,"rom/skin_param/ext_skp_mikitm151_joha_outer_151__divskn_x.txt"),neckPath=Path.Combine(temporary,"rom/skin_param/ext_skp_mikitm925_joha_neck_125__divskn_x.txt");
            if(bodyPolicy=="complete")File.WriteAllBytes(bodyPath,SkinParamPatchAdapter.Entry(complete,"ext_skp_mikitm151_joha_outer_151__divskn_x.txt"));
            var bodyParams=Fields(File.ReadAllText(bodyPath));var neckParams=Fields(File.ReadAllText(neckPath));
            var bodyBlocks=Blocks(temporary,"mikitm151_x.farc");var neckBlocks=Blocks(temporary,"mikitm925_x.farc");
            var donated=new List<string>();foreach(var b in bodyBlocks.Where(b=>!bodyParams.Keys.Any(k=>k.StartsWith(b.ExternalName+"."))))
            {var n=neckBlocks.Single(x=>x.ExternalName==b.ExternalName);Require(Topology(b,n),"Shared neck OSG topology differs: "+b.ExternalName);foreach(var x in neckParams.Where(x=>x.Key.StartsWith(b.ExternalName+".")))bodyParams.Add(x.Key,x.Value);donated.Add(b.ExternalName);}
            Require(donated.Count==7,"Expected seven shared Snow neck parameters.");var snowCounts=bodyBlocks.ToDictionary(b=>b.ExternalName,b=>b.Nodes.Count);var snowTopologyAdjustments=SkinParamPatchAdapter.FitNodes(bodyParams,snowCounts).Concat(SkinParamPatchAdapter.FitRelations(bodyParams,snowCounts,Fields(File.ReadAllText(Path.Combine(roots[1],"rom/skin_param/ext_skp_mikitm151_joha_outer_151__divskn_x.txt"))))).ToArray();File.WriteAllText(bodyPath,Render(bodyParams),Encoding.UTF8);
            // Compare OSG identity/parent/node topology, then apply semantic
            // JSeKai deltas to New Type's own Complete Modules parameters.
            var p4Blocks=Blocks(temporary,"mikitm753_x.farc");var ntBlocks=Blocks(temporary,"mikitm776_x.farc");
            Require(ntBlocks.Length==p4Blocks.Length&&ntBlocks.All(b=>p4Blocks.Any(p=>Topology(b,p))),"New Type hair does not match the common Miku OSG topology.");
            var ntBody=Blocks(temporary,"mikitm176_x.farc");
            Require(ntBody.All(b=>bodyBlocks.Any(p=>Topology(b,p))),"New Type body does not match the Snow reference OSG topology.");
            var ntPatches=new List<SkinPatchResult>();
            if(bodyPolicy=="complete")File.WriteAllBytes(Path.Combine(temporary,"rom/skin_param/ext_skp_mikitm176_joha_outer_176__divskn_x.txt"),SkinParamPatchAdapter.Entry(complete,"ext_skp_mikitm176_joha_outer_176__divskn_x.txt"));
            foreach(var names in new[]{new[]{"ext_skp_mikitm176_joha_outer_176__divskn_x.txt","ext_skp_mikitm151_joha_outer_151__divskn_x.txt"},new[]{"ext_skp_mikitm776_atam_atama_176__divskn_x.txt","ext_skp_mikitm601_atam_atama_01__divskn_x.txt"}}.Where(names=>bodyPolicy=="jsekai"||!names[0].Contains("176_joha")))
                ntPatches.Add(SkinParamPatchAdapter.Adapt(SkinParamPatchAdapter.Entry(complete,names[0]),SkinParamPatchAdapter.Entry(complete,names[1]),SkinParamPatchAdapter.Entry(catalog,names[1]),Path.Combine(temporary,"rom/skin_param",names[0]),(names[0].Contains("176_joha")?ntBody:ntBlocks).ToDictionary(b=>b.ExternalName,b=>b.Nodes.Count)));
            ValidateModels(temporary,objectDb,textureDb);
            var preserved=inputs[0].Where(x=>x.Key.StartsWith("rom/")&&!new[]{"rom/gm_module_tbl.farc","rom/mod_chritm_prop.farc","rom/objset/mod_obj_db.bin","rom/objset/mod_tex_db.bin","rom/2d/mod_spr_db.bin","rom/lang2/mod_str_array.toml","rom/skin_param/mod_osage_setting.txt"}.Contains(x.Key)).ToArray();
            foreach(var f in preserved)Require(Hash(Path.Combine(temporary,f.Key))==f.Value,"Approved P4 bytes changed: "+f.Key);
            for(int i=0;i<3;i++)Require(inputs[i].OrderBy(x=>x.Key).SequenceEqual(Hashes(roots[i]).OrderBy(x=>x.Key)),"Input changed during build.");Require(zipHash==Hash(zip)&&completeZipHash==Hash(completeZip),"Skin reference archive changed.");
            File.WriteAllText(Path.Combine(temporary,"config.toml"),"enabled = true\nname = \"XRomConverter 0.2 - P4, Snow 2016, New Type comparison\"\nauthor = \"XRomConverter compatibility reconstruction\"\ninclude = [\".\"]\n",Encoding.UTF8);
            var manifest=new {schemaVersion=1,phase="three-module-runtime-comparison",bodySkinPolicy=bodyPolicy,priorComparison=prior,priorComparisonFiles=priorHashes,p4RuntimeUserApproved=true,newModulesRuntimeVerified=false,modules=Enumerable.Range(0,3).Select(i=>new {name=expectedNames[i],sourceModule=i==0?324:i==1?322:476,targetModule=moduleIds[i],costume=costumes[i],items=itemMaps[i]}),inputs=roots.Select((r,i)=>new{root=r,files=inputs[i]}),jsekaiArchive=zip,jsekaiArchiveSha256=zipHash,completeArchive=completeZip,completeArchiveSha256=completeZipHash,newTypeSkinPatches=ntPatches,skins,sharedSnowNeckParameters=donated,snowTopologyAdjustments,physicalIdentifierChanges=changes,preservedP4Files=preserved,sharedGlobalMikuFacials=true,emittedFiles=Hashes(temporary)};
            File.WriteAllText(Path.Combine(temporary,"build-manifest.json"),JsonSerializer.Serialize(manifest,Json),Encoding.UTF8);if(prior!=null)Require(priorHashes!.OrderBy(x=>x.Key).SequenceEqual(Hashes(prior).OrderBy(x=>x.Key)),"Prior package changed during build.");Directory.Move(temporary,output);temporary=null;Console.WriteLine(output);Console.WriteLine("Three registered modules; approved P4 preserved, Snow/New Type gameplay pending.");return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e.ToString());return 1;}
        finally{if(temporary!=null&&Directory.Exists(temporary))Directory.Delete(temporary,true);}
    }
    private static OsageBlock[] Blocks(string root,string archive){using var a=BinaryFile.Load<FarcArchive>(Path.Combine(root,"rom/objset",archive));return a.Open<ObjectSet>(a.FileNames.Single(x=>x.EndsWith("_obj.bin"))).Objects.Single().Skin!.Blocks.OfType<OsageBlock>().ToArray();}
    private static bool Topology(OsageBlock a,OsageBlock b)=>a.Name==b.Name&&a.ExternalName==b.ExternalName&&a.ParentName==b.ParentName&&a.Nodes.Select(x=>x.Name).SequenceEqual(b.Nodes.Select(x=>x.Name));
    private static void ValidateItems(Dictionary<string,string> f,ObjectDatabase db,int costume,int count)
    {
        Require(int.Parse(f["item.length"])==count,"Incomplete costume item list.");var numbers=Enumerable.Range(0,count).Select(i=>f[$"item.{i}.no"]).ToHashSet();
        Require(numbers.Count==count&&int.Parse(f["cos.0.item.length"])==count&&Enumerable.Range(0,count).All(i=>numbers.Contains(f[$"cos.0.item.{i}"])),"Incomplete costume/item links.");
        for(int i=0;i<count;i++)
        {
            string prefix=$"item.{i}.";var sets=Enumerable.Range(0,int.Parse(f[prefix+"objset.length"])).Select(j=>db.ObjectSets.Single(s=>s.Name==f[prefix+$"objset.{j}"])).ToArray();
            for(int j=0;j<int.Parse(f[prefix+"data.obj.length"]);j++){string uid=f[prefix+$"data.obj.{j}.uid"];Require((uid=="NULL_X"&&f[prefix+$"data.obj.{j}.rpk"]=="1"&&f[prefix+"type"]=="2")||sets.SelectMany(s=>s.Objects).Any(o=>o.Name==uid),"Unresolved item UID: "+uid);}
        }
    }
    private static void ValidateModels(string root,ObjectDatabase db,TextureDatabase textures)
    {
        Require(db.ObjectSets.Select(s=>s.Id).Distinct().Count()==db.ObjectSets.Count,"Object-set ID collision.");Require(textures.Textures.Select(t=>t.Id).Distinct().Count()==textures.Textures.Count,"Texture ID collision.");
        foreach(var info in db.ObjectSets)
        {
            using var a=BinaryFile.Load<FarcArchive>(Path.Combine(root,"rom/objset",info.ArchiveFileName));var model=a.Open<ObjectSet>(info.FileName);var tex=a.Open<TextureSet>(a.FileNames.Single(x=>x.EndsWith("_tex.bin")));
            Require(model.Objects.All(o=>info.Objects.Any(d=>d.Id==o.Id&&d.Name.Equals(o.Name,StringComparison.OrdinalIgnoreCase))),"Object database/model differs.");
            Require(model.TextureIds.Count==tex.Textures.Count&&model.TextureIds.All(id=>textures.Textures.Any(t=>t.Id==id)),"Model texture is unresolved.");
            foreach(var o in model.Objects)
            {
                Require(!o.Skin?.Blocks.OfType<MotionBlock>().Any()??true,"MotionBlock remains: "+o.Name);var blocks=o.Skin?.Blocks.OfType<OsageBlock>().ToArray()??Array.Empty<OsageBlock>();if(blocks.Length==0)continue;
                var p=new OsageSkinParameterSet();p.Load(Path.Combine(root,"rom/skin_param","ext_skp_"+o.Name+".txt"));
                foreach(var b in blocks){var parameter=p.Parameters.Single(x=>x.Name==b.ExternalName);Require(parameter.Nodes.Count==b.Nodes.Count,"Skin parameter node count differs: "+o.Name+"/"+b.ExternalName+" (parameter "+parameter.Nodes.Count+", model "+b.Nodes.Count+")");}
                var names=p.Parameters.Select(x=>x.Name).ToHashSet();foreach(var parameter in p.Parameters){Require(parameter.Bocs.All(x=>string.IsNullOrEmpty(x.EdRoot)||names.Contains(x.EdRoot)),"Orphan BOC target.");Require(string.IsNullOrEmpty(parameter.CollisionTargetOsage)||names.Contains(parameter.CollisionTargetOsage),"Orphan collision target.");}
            }
        }
    }
}
