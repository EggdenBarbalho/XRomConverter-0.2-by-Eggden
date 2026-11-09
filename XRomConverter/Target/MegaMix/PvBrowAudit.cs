using System.Text.Json;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.CriMw;
using MikuMikuLibrary.Archives.Extensions;
using MikuMikuLibrary.Bones;
using MikuMikuLibrary.Databases;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Motions;

namespace XRomConverter.Target.MegaMix;
internal static class PvBrowAudit
{
    internal static void Retarget(string gamePath,string arcadeReference,string xReference,string patch,string output,int[]? selectedPvs=null)
    {
        using var main=BinaryFile.Load<CpkArchive>(Path.Combine(gamePath,"diva_main.cpk"));using var dlc=BinaryFile.Load<CpkArchive>(Path.Combine(gamePath,"diva_dlc00.cpk"));
        var sk=main.Open<BoneData>("rom/bone_data.bin").Skeletons.Single(x=>x.Name=="MIK");using var dba=main.Open<FarcArchive>("rom_ps4/rom/rob/mot_db.farc");var db=dba.Open<MotionDatabase>("mot_db.bin");
        MotionSet Load(FarcArchive a){using var s=a.Open(a.FileNames.Single(),EntryStreamMode.MemoryStream);var set=new MotionSet();set.Load(s,sk,db);return set;}
        using var ar=BinaryFile.Load<FarcArchive>(arcadeReference);using var xr=BinaryFile.Load<FarcArchive>(xReference);var aa=Load(ar);var xx=Load(xr);var names=db.GetMotionSetInfo("MIK").Motions;
        if(aa.Motions.Count!=names.Count||xx.Motions.Count!=names.Count)throw new InvalidDataException("Reference motion order/count mismatch.");
        var pairs=Enumerable.Range(0,names.Count).Where(i=>names[i].Name.Contains("_FACE_")).Select(i=>(aa.Motions[i],xx.Motions[i])).ToList();
        var profile=EyebrowRetargetProfile.Learn("MIK",pairs)??throw new InvalidDataException("No improved Arcade B to X eyebrow calibration.");Directory.CreateDirectory(output);
        int[] pvs=selectedPvs??new[]{218,232};
        if(pvs.Any(p=>p is not (218 or 232 or 236)))throw new ArgumentException("Unsupported eyebrow PV selection.");
        foreach(int pv in pvs)
        {
            string file=$"mot_PV{pv:D3}_FACE_MIK.farc";var found=main.FileNames.Select(p=>(archive:main,path:p)).Concat(dlc.FileNames.Select(p=>(archive:dlc,path:p))).Last(x=>Path.GetFileName(x.path).Equals(file,StringComparison.OrdinalIgnoreCase));
            using var native=found.archive.Open<FarcArchive>(found.path);var source=Load(native);source.Motions.ForEach(m=>profile.Retarget(m,sk,db));
            using var input=BinaryFile.Load<FarcArchive>(Path.Combine(patch,"rom/rob",file));var before=Load(input);var target=Load(input);if(source.Motions.Count!=target.Motions.Count)throw new InvalidDataException("PV motion counts differ.");
            for(int i=0;i<target.Motions.Count;i++)
            {
                var a=source.Motions[i].Bind().BoneBindings.ToDictionary(x=>x.Name);var b=target.Motions[i].Bind();foreach(var bone in b.BoneBindings.Where(x=>profile.BoneNames.Contains(x.Name)))bone.Position=a[bone.Name].Position;b.Unbind(sk,db);
            }
            using var stream=new MemoryStream();target.Save(stream,sk,db,leaveOpen:true);stream.Position=0;var actual=new MotionSet();actual.Load(stream,sk,db);
            for(int i=0;i<before.Motions.Count;i++)
            {
                var m=before.Motions[i];var n=actual.Motions[i];if(m.FrameCount!=n.FrameCount||m.HighBits!=n.HighBits||m.DivFrameCount!=n.DivFrameCount||m.DivFileCount!=n.DivFileCount)throw new InvalidDataException("PV timing changed.");
                var a=m.Bind().BoneBindings.ToDictionary(x=>x.Name);var b=n.Bind().BoneBindings.ToDictionary(x=>x.Name);if(!a.Keys.Order().SequenceEqual(b.Keys.Order()))throw new InvalidDataException("PV bindings changed.");
                foreach(var e in a)if(!ClosedEyeRestorer.Equal(e.Value.Rotation,b[e.Key].Rotation)||(!profile.BoneNames.Contains(e.Key)&&!ClosedEyeRestorer.Equal(e.Value.Position,b[e.Key].Position)))throw new InvalidDataException("Non-eyebrow channel changed.");
            }
            using var packed=new FarcArchive();packed.Add(input.FileNames.Single(),new MemoryStream(stream.ToArray()),false);packed.Save(Path.Combine(output,file));
        }
        File.WriteAllText(Path.Combine(output,"calibration.json"),JsonSerializer.Serialize(profile.Export(),new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine($"[PV BROWS] Arcade B to X calibration RMSE {profile.ValidationRmse:F6}, raw {profile.IdentityRmse:F6}; other channels verified for PVs {string.Join(",",pvs)}.");
    }
    internal static void Run(string gamePath,string patch,string report)
    {
        using var main=BinaryFile.Load<CpkArchive>(Path.Combine(gamePath,"diva_main.cpk"));
        using var dlc=BinaryFile.Load<CpkArchive>(Path.Combine(gamePath,"diva_dlc00.cpk"));
        var sk=main.Open<BoneData>("rom/bone_data.bin").Skeletons.Single(x=>x.Name=="MIK");
        using var dba=main.Open<FarcArchive>("rom_ps4/rom/rob/mot_db.farc");var db=dba.Open<MotionDatabase>("mot_db.bin");
        var rows=new List<object>();
        foreach(int pv in new[]{236,232,218})
        {
            var file=$"mot_PV{pv:D3}_FACE_MIK.farc";
            foreach(var owner in new[]{"native","patch"})
            {
                var found=main.FileNames.Select(p=>(archive:main,path:p)).Concat(dlc.FileNames.Select(p=>(archive:dlc,path:p))).Last(x=>Path.GetFileName(x.path).Equals(file,StringComparison.OrdinalIgnoreCase));
                using var a=owner=="native"?found.archive.Open<FarcArchive>(found.path):BinaryFile.Load<FarcArchive>(Path.Combine(patch,"rom/rob",file));
                using var s=a.Open(a.FileNames.Single(),EntryStreamMode.MemoryStream);var set=new MotionSet();set.Load(s,sk,db);
                for(int i=0;i<set.Motions.Count;i++)
                {
                    var m=set.Motions[i];var bones=m.Bind().BoneBindings.Where(x=>x.Name.Contains("mayu")).Select(b=>new{name=b.Name,b.Position,b.Rotation}).ToArray();
                    rows.Add(new{pv,owner,index=i,m.FrameCount,bones});
                }
            }
        }
        File.WriteAllText(report,JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
    }
}
