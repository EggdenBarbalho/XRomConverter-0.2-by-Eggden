using System.Text.Json;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.CriMw;
using MikuMikuLibrary.Archives.Extensions;
using MikuMikuLibrary.Bones;
using MikuMikuLibrary.Databases;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Motions;

namespace XRomConverter.Target.MegaMix;

internal static class ClosedEyeMotionAudit
{
    internal static void ReferencePvs(string gamePath, string patch, string output)
    {
        using var game=BinaryFile.Load<CpkArchive>(Path.Combine(gamePath,"diva_main.cpk"));
        using var dlc=BinaryFile.Load<CpkArchive>(Path.Combine(gamePath,"diva_dlc00.cpk"));
        var rows=new List<object>();
        foreach(var (id,name) in new[]{(236,"Absolunote / Arcade B"),(219,"Deep Sea Girl / Arcade A"),(83,"*Hello, Planet"),(1,"Love is War")})
        {
            string token=$"PV{id:D3}";
            var entries=game.FileNames.Concat(dlc.FileNames).Where(p=>p.Replace("_","").Contains(token,StringComparison.OrdinalIgnoreCase)).Where(p=>p.Contains("rob",StringComparison.OrdinalIgnoreCase)||p.Contains("script",StringComparison.OrdinalIgnoreCase)||p.EndsWith(".dsc",StringComparison.OrdinalIgnoreCase)).Distinct().ToArray();
            var files=Directory.GetFiles(Path.Combine(patch,"rom","rob"),"*",SearchOption.TopDirectoryOnly).Where(p=>Path.GetFileName(p).Contains(token,StringComparison.OrdinalIgnoreCase)).Select(p=>new{file=Path.GetFileName(p),sha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)))}).ToArray();
            rows.Add(new{pv=id,name,nativeEntries=entries,protectedPatchFiles=files,changedPvSpecificFiles=0,gameplayVerified=false});
        }
        File.WriteAllText(output,JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
    }
    internal static int Run(string[] args)
    {
        if(args.Length!=5) throw new ArgumentException("audit-closed-eyes GAME SOURCE_ROB TARGET_ROB REPORT");
        using var game=BinaryFile.Load<CpkArchive>(Path.Combine(args[1],"diva_main.cpk"));
        var bones=game.Open<BoneData>("rom/bone_data.bin");
        using var dba=game.Open<FarcArchive>("rom_ps4/rom/rob/mot_db.farc");
        var db=dba.Open<MotionDatabase>("mot_db.bin");
        var rows=new List<object>();
        foreach(string ch in new[]{"MIK","RIN","LEN","LUK","KAI","MEI","HAK","NER","SAK","TET","CMN"})
        {
            var sourceFile=Path.Combine(args[2],$"mot_{ch}.farc");
            var targetFile=Path.Combine(args[3],$"mot_{ch}.farc");
            if(!File.Exists(sourceFile)||!File.Exists(targetFile)){rows.Add(new{character=ch,missing=true});continue;}
            var sk=bones.Skeletons.FirstOrDefault(x=>x.Name==ch)??bones.Skeletons.First(x=>x.Name=="MIK");
            MotionSet Load(string file){using var a=BinaryFile.Load<FarcArchive>(file);using var st=a.Open(a.FileNames.Single(n=>n.EndsWith(".bin",StringComparison.OrdinalIgnoreCase)),EntryStreamMode.MemoryStream);var set=new MotionSet();set.Load(st,sk,db);return set;}
            var source=Load(sourceFile);var target=Load(targetFile);var info=db.GetMotionSetInfo(ch);
            for(int i=0;i<Math.Min(source.Motions.Count,target.Motions.Count);i++)
            {
                var a=source.Motions[i].Bind();var b=target.Motions[i].Bind();
                var changes=new List<string>();
                foreach(var bone in a.BoneBindings.Where(x=>x.Name.Contains("mabu")||x.Name.Contains("eyelid")))
                {
                    var other=b.BoneBindings.FirstOrDefault(x=>x.Name==bone.Name);
                    if(other==null||!ClosedEyeRestorer.Equal(bone.Position,other.Position)||!ClosedEyeRestorer.Equal(bone.Rotation,other.Rotation))changes.Add(bone.Name);
                }
                string name=info.Motions[i].Name;
                if(changes.Count>0||name.Contains("_CL")||name.Contains("CLOSE"))rows.Add(new{character=ch,index=i,name,sourceCount=source.Motions.Count,targetCount=target.Motions.Count,changedEyeBones=changes});
            }
        }
        File.WriteAllText(args[4],JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
        return 0;
    }
}
