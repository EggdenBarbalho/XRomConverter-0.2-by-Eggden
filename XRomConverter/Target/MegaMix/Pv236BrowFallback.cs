using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.CriMw;
using MikuMikuLibrary.Archives.Extensions;
using MikuMikuLibrary.Bones;
using MikuMikuLibrary.Databases;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Motions;

namespace XRomConverter.Target.MegaMix;
internal static class Pv236BrowFallback
{
    private static float Evaluate(KeySet c,float frame)
    {
        var keys=c.Keys;if(keys.Count==0)return 0;if(frame<=keys[0].Frame)return keys[0].Value;if(frame>=keys[^1].Frame)return keys[^1].Value;
        int j=keys.FindIndex(k=>k.Frame>frame);var a=keys[j-1];var b=keys[j];float span=b.Frame-a.Frame,t=(frame-a.Frame)/span;
        return c.HasTangents?(2*t*t*t-3*t*t+1)*a.Value+(t*t*t-2*t*t+t)*span*a.Tangent+(-2*t*t*t+3*t*t)*b.Value+(t*t*t-t*t)*span*b.Tangent:a.Value+t*(b.Value-a.Value);
    }
    private static float Derivative(KeySet c,float frame)
    {
        var k=c.Keys;if(k.Count<2||frame<k[0].Frame||frame>=k[^1].Frame)return 0;
        int j=k.FindIndex(x=>x.Frame>frame);var a=k[j-1];var b=k[j];float span=b.Frame-a.Frame,t=(frame-a.Frame)/span;
        return c.HasTangents?((6*t*t-6*t)*a.Value+(-6*t*t+6*t)*b.Value)/span+(3*t*t-4*t+1)*a.Tangent+(3*t*t-2*t)*b.Tangent:(b.Value-a.Value)/span;
    }
    internal static void Run(string gamePath,string input,string output,string expression="MIK_FACE_CLOSE",int start=2940,int end=3060)
    {
        if(start<0||end<=start||end>ushort.MaxValue)throw new ArgumentException("Invalid pose window.");
        if(File.Exists(output))throw new InvalidDataException("Output already exists.");
        using var game=BinaryFile.Load<CpkArchive>(Path.Combine(gamePath,"diva_main.cpk"));var sk=game.Open<BoneData>("rom/bone_data.bin").Skeletons.Single(x=>x.Name=="MIK");
        using var dba=game.Open<FarcArchive>("rom_ps4/rom/rob/mot_db.farc");var db=dba.Open<MotionDatabase>("mot_db.bin");
        MotionSet Load(FarcArchive a){using var s=a.Open(a.FileNames.Single(),EntryStreamMode.MemoryStream);var m=new MotionSet();m.Load(s,sk,db);return m;}
        using var archive=BinaryFile.Load<FarcArchive>(input);var before=Load(archive);var result=Load(archive);
        using var reference=typeof(Pv236BrowFallback).Assembly.GetManifestResourceStream("XRomConverter.ClosedEyes.mot_MIK.farc")!;using var ra=BinaryFile.Load<FarcArchive>(reference);var generic=Load(ra);
        int idx=db.GetMotionSetInfo("MIK").Motions.FindIndex(x=>x.Name==expression);if(idx<0)throw new InvalidDataException("Expression absent: "+expression);var pose=generic.Motions[idx].Bind().BoneBindings.ToDictionary(x=>x.Name);
        var binding=result.Motions.Single().Bind();var changed=new HashSet<string>();
        foreach(var b in binding.BoneBindings.Where(x=>x.Name.StartsWith("tl_mayu_")))
        {
            if(b.Position==null||!pose.TryGetValue(b.Name,out var p)||p.Position==null)continue;
            var originals=new[]{b.Position.X,b.Position.Y,b.Position.Z};var sources=new[]{p.Position.X,p.Position.Y,p.Position.Z};var outputs=new KeySet[3];
            for(int a=0;a<3;a++)
            {
                var c=originals[a];var src=sources[a];if(c==null||src==null)throw new InvalidDataException("Missing eyebrow position axis.");
                float value=src.Keys.Count==0?0:src.Keys[^1].Value;
                var next=new KeySet{HasTangents=true};next.Keys.AddRange(c.Keys.Where(k=>k.Frame<start));
                float Blend(float f){float ramp=Math.Clamp(Math.Min((f-start)/15,(end-f)/15),0,1);float w=ramp*ramp*(3-2*ramp);return Evaluate(c,f)*(1-w)+value*w;}
                for(int f=start;f<=end;f++)next.Keys.Add(new Key{Frame=(ushort)f,Value=Blend(f),Tangent=f==start||f==end?Derivative(c,f):(Blend(f+0.01f)-Blend(f-0.01f))/0.02f});
                next.Keys.AddRange(c.Keys.Where(k=>k.Frame>end));outputs[a]=next;
            }
            b.Position.X=outputs[0];b.Position.Y=outputs[1];b.Position.Z=outputs[2];changed.Add(b.Name);
        }
        if(changed.Count!=6)throw new InvalidDataException("Expected six eyebrow position controls.");binding.Unbind(sk,db);
        using var stream=new MemoryStream();result.Save(stream,sk,db,leaveOpen:true);stream.Position=0;var actual=new MotionSet();actual.Load(stream,sk,db);
        var old=before.Motions.Single();var now=actual.Motions.Single();if(old.FrameCount!=now.FrameCount||old.HighBits!=now.HighBits||old.DivFrameCount!=now.DivFrameCount||old.DivFileCount!=now.DivFileCount)throw new InvalidDataException("Motion timing changed.");
        var oldBones=old.Bind().BoneBindings.ToDictionary(x=>x.Name);var newBones=now.Bind().BoneBindings.ToDictionary(x=>x.Name);
        foreach(var e in oldBones)
        {
            var b=newBones[e.Key];if(!ClosedEyeRestorer.Equal(e.Value.Rotation,b.Rotation)||(!changed.Contains(e.Key)&&!ClosedEyeRestorer.Equal(e.Value.Position,b.Position)))throw new InvalidDataException("Non-eyebrow channel changed.");
            if(!changed.Contains(e.Key))continue;
            var x=new[]{e.Value.Position!.X,e.Value.Position.Y,e.Value.Position.Z};var y=new[]{b.Position!.X,b.Position.Y,b.Position.Z};
            for(int a=0;a<3;a++)for(int f=0;f<=old.FrameCount;f++)if(f<start||f>end)if(Math.Abs(Evaluate(x[a],f)-Evaluate(y[a],f))>0.000002f)throw new InvalidDataException($"Eyebrows changed outside selected window: {e.Key}/{a}/{f}.");
        }
        using var packed=new FarcArchive();packed.Add(archive.FileNames.Single(),new MemoryStream(stream.ToArray()),false);packed.Save(output);
        Console.WriteLine($"[X BROW POSE] {expression} blended at frames {start}-{end}. Other channels and samples outside the window verified.");
    }
}
