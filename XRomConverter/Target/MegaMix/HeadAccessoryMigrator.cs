using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.Extensions;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Objects;
using System.Text.RegularExpressions;

namespace XRomConverter.Target.MegaMix;

// Selection only. Do not rewrite models or infer detachable pieces from topology.
internal static class HeadAccessoryMigrator
{
    private static readonly Regex NamedAccessory=new(
        @"head[ _-]?(phone|set|pin)|hair[ _-]?(pin|clip|ornament)|barrette|presilha|tiara|crown|helmet|goggle|glasses|earring|(?:^|[ _-])(hat|pin|clip)(?:$|[ _-])",
        RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);

    public static void Write(string output)
    {
        var rows=new List<string>{"File\tObject\tMesh\tMaterials\tSelection\tNotes"};
        int whole=0,mixed=0;
        foreach(var file in Directory.GetFiles(Path.Combine(output,"rom","objset"),"*_x.farc").Order()) {
            using var archive=BinaryFile.Load<FarcArchive>(file);
            var name=Path.GetFileNameWithoutExtension(file);
            var objects=archive.Open<ObjectSet>(name+"_obj.bin");
            foreach(var obj in objects.Objects.Where(o=>o.Name.Contains("JOHA_OUTER",StringComparison.OrdinalIgnoreCase) && o.Skin!=null)) {
                foreach(var mesh in obj.Meshes) {
                    var materials=mesh.SubMeshes.Select(s=>obj.Materials[(int)s.MaterialIndex].Name).Distinct().ToArray();
                    if(!NamedAccessory.IsMatch(mesh.Name) && !materials.Any(m=>NamedAccessory.IsMatch(m)))continue;
                    bool head=false,other=false;
                    if(mesh.BlendWeights==null || mesh.BlendIndices==null)continue;
                    foreach(var sub in mesh.SubMeshes)foreach(var v in sub.Indices.Where(v=>v!=uint.MaxValue).Distinct()) {
                        var w=mesh.BlendWeights[v];var b=mesh.BlendIndices[v];
                        var ws=new[]{w.X,w.Y,w.Z,w.W};var bs=new[]{b.X,b.Y,b.Z,b.W};
                        for(int i=0;i<4;i++)if(ws[i]>0) {
                            if(sub.BoneIndices==null || bs[i]<0 || bs[i]>=sub.BoneIndices.Length) {other=true;continue;}
                            var bone=obj.Skin.Bones[sub.BoneIndices[bs[i]]].Name;
                            if(bone=="j_kao_wj" || bone.Contains("hair",StringComparison.OrdinalIgnoreCase))head=true;
                            else other=true;
                        }
                    }
                    if(!head)continue;
                    string status=other?"manually select the accessory only":"candidate: entire head mesh";
                    if(other)mixed++;else whole++;
                    rows.Add($"{Path.GetFileName(file)}\t{obj.Name}\t{mesh.Name}\t{string.Join(",",materials)}\t{status}\tgeometry unchanged; no transfer applied");
                }
            }
        }
        File.WriteAllLines(ConversionReports.PathFor(output,"HEAD-ACCESSORY-SELECTION.tsv"),rows);
        Console.WriteLine($"[HEAD_ACCESSORIES_SELECTION] {whole} whole-mesh candidates; {mixed} meshes require manual selection. No models changed.");
    }
}
