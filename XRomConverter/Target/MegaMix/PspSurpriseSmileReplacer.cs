using System.Reflection;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.CriMw;
using MikuMikuLibrary.Archives.Extensions;
using MikuMikuLibrary.Bones;
using MikuMikuLibrary.Databases;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Motions;

namespace XRomConverter.Target.MegaMix;

internal static class PspSurpriseSmileReplacer
{
    private static bool Mouth(string name) => name.Contains("ago") || name.Contains("ha_") || name.Contains("kuti") || name.Contains("kuchi") || name.Contains("sita") || name.Contains("tooth");
    private static KeySet?[] Channels(KeyBinding? b) => new[]{b?.X,b?.Y,b?.Z};
    internal static bool Equal(KeyBinding? a, KeyBinding? b)
    {
        var x=Channels(a);var y=Channels(b);
        for(int axis=0;axis<3;axis++)
        {
            if((x[axis]==null)!=(y[axis]==null))return false;
            if(x[axis]==null)continue;
            if(x[axis]!.HasTangents!=y[axis]!.HasTangents||x[axis]!.Keys.Count!=y[axis]!.Keys.Count)return false;
            for(int k=0;k<x[axis]!.Keys.Count;k++)
            {var p=x[axis]!.Keys[k];var q=y[axis]!.Keys[k];if(p.Frame!=q.Frame||!float.IsFinite(q.Value)||!float.IsFinite(q.Tangent)||Math.Abs(p.Value-q.Value)>0.000002||Math.Abs(p.Tangent-q.Tangent)>0.000002)return false;}
        }
        return true;
    }
    internal static void Audit(string gamePath,string reference,string target,string output)
    {
        using var game=BinaryFile.Load<CpkArchive>(Path.Combine(gamePath,"diva_main.cpk"));
        var bones=game.Open<BoneData>("rom/bone_data.bin");
        using var dba=game.Open<FarcArchive>("rom_ps4/rom/rob/mot_db.farc");var db=dba.Open<MotionDatabase>("mot_db.bin");
        var sk=bones.Skeletons.Single(x=>x.Name=="MIK");
        MotionSet Load(string p){using var a=BinaryFile.Load<FarcArchive>(p);using var st=a.Open(a.FileNames.Single(x=>x.EndsWith(".bin")),EntryStreamMode.MemoryStream);var m=new MotionSet();m.Load(st,sk,db);return m;}
        var a=Load(reference);var b=Load(target);var names=db.GetMotionSetInfo("MIK").Motions.Select(x=>x.Name).ToArray();
        var rows=new List<object>();
        for(int i=0;i<names.Length;i++){
            var ab=a.Motions[i].Bind().BoneBindings.ToDictionary(x=>x.Name);var bb=b.Motions[i].Bind().BoneBindings.ToDictionary(x=>x.Name);
            var diff=ab.Keys.Where(n=>!Equal(ab[n].Position,bb[n].Position)||!Equal(ab[n].Rotation,bb[n].Rotation)).ToArray();
            if(names[i].Contains("KUCHI")||names[i].Contains("SURPRISE"))rows.Add(new{name=names[i],frames=a.Motions[i].FrameCount,diff,bones=ab.Values.Where(x=>Mouth(x.Name)).Select(x=>new{x.Name,x.Position,x.Rotation}).ToArray()});
        }
        File.WriteAllText(output,System.Text.Json.JsonSerializer.Serialize(rows,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
    }
    internal static void Write(string root, string gamePath)
    {
        using var game=BinaryFile.Load<CpkArchive>(Path.Combine(gamePath,"diva_main.cpk"));
        var bones=game.Open<BoneData>("rom/bone_data.bin");
        using var dba=game.Open<FarcArchive>("rom_ps4/rom/rob/mot_db.farc");
        var db=dba.Open<MotionDatabase>("mot_db.bin");
        var rows=new List<string>{"Character\tMotion\tReplaced mouth bindings\tValidation"};
        var pending=new List<(string Path,byte[] Bytes)>();
        foreach(string ch in new[]{"MIK","RIN","LEN","LUK","KAI","MEI","HAK","NER","SAK","TET"})
        {
            string path=Path.Combine(root,"rom","rob",$"mot_{ch}.farc");if(!File.Exists(path))continue;
            var sk=bones.Skeletons.Single(x=>x.Name==ch);
            MotionSet Load(FarcArchive a){using var s=a.Open(a.FileNames.Single(x=>x.EndsWith(".bin",StringComparison.OrdinalIgnoreCase)),EntryStreamMode.MemoryStream);var m=new MotionSet();m.Load(s,sk,db);return m;}
            using var targetArchive=BinaryFile.Load<FarcArchive>(path);
            using var reference=Assembly.GetExecutingAssembly().GetManifestResourceStream($"XRomConverter.ClosedEyes.mot_{ch}.farc")??throw new InvalidDataException("Missing smile reference: "+ch);
            using var sourceArchive=BinaryFile.Load<FarcArchive>(reference);
            var source=Load(sourceArchive);var before=Load(targetArchive);var result=Load(targetArchive);
            var names=db.GetMotionSetInfo(ch).Motions.Select(x=>x.Name).ToArray();
            if(source.Motions.Count!=names.Length||result.Motions.Count!=names.Length)throw new InvalidDataException("Smile motion order/count incompatible: "+ch);
            var changed=new HashSet<int>();
            for(int i=0;i<names.Length;i++)
            {
                if(names[i]!=ch+"_KUCHI_PSP_SURPRISE" && names[i]!=ch+"_KUCHI_SURPRISE_OLD")continue;
                int smile=Array.IndexOf(names,ch+"_KUCHI_SMILE_L");
                if(smile<0)throw new InvalidDataException("Missing smile: "+ch);
                if(source.Motions[smile].FrameCount!=result.Motions[i].FrameCount)throw new InvalidDataException("Smile timing incompatible: "+ch);
                var a=source.Motions[smile].Bind();var b=result.Motions[i].Bind();int n=0;
                foreach(var bone in b.BoneBindings.Where(x=>Mouth(x.Name)))
                {
                    var input=a.BoneBindings.SingleOrDefault(x=>x.Name==bone.Name)??throw new InvalidDataException("Missing mouth bone: "+bone.Name);
                    if(Equal(bone.Position,input.Position)&&Equal(bone.Rotation,input.Rotation))continue;
                    bone.Position=input.Position;bone.Rotation=input.Rotation;n++;
                }
                if(n==0)continue;
                b.Unbind(sk,db);changed.Add(i);rows.Add($"{ch}\t{names[i]}\t{n}\tall other channels preserved");
            }
            if(changed.Count==0)continue;
            using var stream=new MemoryStream();result.Save(stream,sk,db,leaveOpen:true);stream.Position=0;
            var reopened=new MotionSet();reopened.Load(stream,sk,db);
            for(int i=0;i<names.Length;i++)
            {
                var old=before.Motions[i];var actual=reopened.Motions[i];
                if(old.FrameCount!=actual.FrameCount||old.HighBits!=actual.HighBits||old.DivFrameCount!=actual.DivFrameCount||old.DivFileCount!=actual.DivFileCount)throw new InvalidDataException("Motion timing changed: "+names[i]);
                var oldBones=old.Bind().BoneBindings.ToDictionary(x=>x.Name);
                var actualBones=actual.Bind().BoneBindings.ToDictionary(x=>x.Name);
                var expectedBones=(changed.Contains(i)?source.Motions[Array.IndexOf(names,ch+"_KUCHI_SMILE_L")]:old).Bind().BoneBindings.ToDictionary(x=>x.Name);
                if(!oldBones.Keys.Order().SequenceEqual(actualBones.Keys.Order()))throw new InvalidDataException("Motion bindings changed: "+names[i]);
                foreach(var entry in oldBones)
                {
                    var expected=changed.Contains(i)&&Mouth(entry.Key)?expectedBones[entry.Key]:entry.Value;var got=actualBones[entry.Key];
                    if(!Equal(expected.Position,got.Position)||!Equal(expected.Rotation,got.Rotation))throw new InvalidDataException("Motion channel validation failed: "+names[i]+"/"+entry.Key);
                }
            }
            using var output=new FarcArchive();
            foreach(string entry in targetArchive.FileNames)
            {
                if(entry.EndsWith(".bin",StringComparison.OrdinalIgnoreCase))output.Add(entry,new MemoryStream(stream.ToArray()),false);
                else {using var s=targetArchive.Open(entry,EntryStreamMode.MemoryStream);var bytes=new MemoryStream();s.CopyTo(bytes);bytes.Position=0;output.Add(entry,bytes,false);}
            }
            using var packed=new MemoryStream();output.Save(packed,leaveOpen:true);pending.Add((path,packed.ToArray()));
        }
        // Commit only after every character passes the preservation checks.
        foreach(var item in pending)File.WriteAllBytes(item.Path,item.Bytes);
        File.WriteAllLines(ConversionReports.PathFor(root,"PSP-SURPRISE-SMILE.tsv"),rows);
        Console.WriteLine($"[PSP SURPRISE SMILE] Replaced {rows.Count-1} expressions in {pending.Count} archives; all other channels verified.");
    }
}
