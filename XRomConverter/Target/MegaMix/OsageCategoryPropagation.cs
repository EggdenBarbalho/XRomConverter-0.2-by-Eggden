using System.Text.Json;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.Extensions;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Objects;
using MikuMikuLibrary.Objects.Extra.Blocks;

namespace XRomConverter.Target.MegaMix;

// Runs after all hairstyle clones have been emitted. Changes registrations only.
internal static class OsageCategoryPropagation
{
    private static Dictionary<string,string> Fields(string text) => text.Split('\n')
        .Select(l=>l.Trim()).Where(l=>l.Contains('=')&&!l.StartsWith('#'))
        .Select(l=>l.Split('=',2)).ToDictionary(l=>l[0],l=>l[1]);

    public static void Write(string root, string? destination=null)
    {
        string path=Path.Combine(root,"rom","skin_param","mod_osage_setting.txt");
        if(!File.Exists(path))return;
        string original=File.ReadAllText(path);var fields=Fields(original);
        var categories=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        int count=int.Parse(fields["obj.length"]);
        int duplicates=0;
        for(int i=0;i<count;i++) {
            string uid=fields[$"obj.{i}.name"],cat=fields[$"obj.{i}.cat"];
            if(categories.TryGetValue(uid,out var previous)) {
                if(previous!=cat)throw new InvalidDataException("Conflicting Osage UID: "+uid);
                duplicates++;
            }
            categories[uid]=cat;
        }
        var roots=new Dictionary<string,string[]>(StringComparer.Ordinal);
        for(int i=0;i<int.Parse(fields["cat.length"]);i++)
            roots.Add(fields[$"cat.{i}.name"],Enumerable.Range(0,int.Parse(fields[$"cat.{i}.osg.length"]))
                .Select(j=>fields[$"cat.{i}.osg.{j}.root"]).ToArray());
        var corrected=new List<object>();var skipped=new List<object>();var additions=new List<string>();
        foreach(string file in Directory.EnumerateFiles(Path.Combine(root,"rom","objset"),"*_x.farc").Order()) {
            using var archive=BinaryFile.Load<FarcArchive>(file);
            string? entry=archive.FileNames.SingleOrDefault(n=>n.EndsWith("_obj.bin"));
            if(entry==null)continue;
            var set=archive.Open<ObjectSet>(entry);
            if(set.Objects.GroupBy(o=>o.Name,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1)
                ||set.Objects.GroupBy(o=>o.Id).Any(g=>g.Count()>1))throw new InvalidDataException("Duplicate object UID/ID: "+file);
            foreach(var target in set.Objects) {
                if(categories.ContainsKey(target.Name))continue;
                var targetChains=target.Skin?.Blocks.OfType<OsageBlock>().ToArray();
                if(targetChains==null||targetChains.Length==0)continue;
                string? sourceName=null;string lower=target.Name.ToLowerInvariant();
                int suffix=lower.IndexOf("_acc_",StringComparison.Ordinal);
                if(suffix<0)suffix=lower.IndexOf("_headslot_",StringComparison.Ordinal);
                if(suffix>=0)sourceName=target.Name[..suffix]+"_x";
                // This exact alias is emitted from the first Miku 601 object by DefaultMikuHeadphones.
                if(lower=="mikitm601_atam_headphones_x")sourceName="mikitm601_atam_atama_01__divskn_x";
                // Only clones have a category to inherit. Ordinary objects with no
                // source registration are covered by the module coverage report.
                if(sourceName==null)continue;
                var source=set.Objects.SingleOrDefault(o=>o.Name.Equals(sourceName,StringComparison.OrdinalIgnoreCase));
                if(source==null||!categories.TryGetValue(source.Name,out var category)) {
                    skipped.Add(new{Archive=Path.GetFileName(file),UID=target.Name,Reason="No registered source with documented clone identity"});continue;
                }
                var sourceChains=(source.Skin?.Blocks.OfType<OsageBlock>()??Enumerable.Empty<OsageBlock>()).ToDictionary(b=>b.ExternalName);
                if(!roots.TryGetValue(category,out var required))throw new InvalidDataException("Missing Osage category: "+category);
                // Follow the category's actual roots, independently of character,
                // hairstyle family or node count. Accessory OSGs outside this
                // category must not inherit its parts/exf settings.
                var chains=targetChains.Where(b=>required.Contains(b.ExternalName,StringComparer.Ordinal)).ToArray();
                var donorRoots=sourceChains.Keys.Where(n=>required.Contains(n,StringComparer.Ordinal)).Order(StringComparer.Ordinal);
                bool compatible=chains.Length>0&&chains.Select(b=>b.ExternalName).Order(StringComparer.Ordinal).SequenceEqual(donorRoots);
                foreach(var chain in chains) {
                    compatible &= sourceChains.TryGetValue(chain.ExternalName,out var donor)
                        &&chain.ParentName==donor.ParentName&&chain.Nodes.Select(n=>n.Name).SequenceEqual(donor.Nodes.Select(n=>n.Name))
                        &&chain.Nodes.Select(n=>n.Length).Zip(donor.Nodes.Select(n=>n.Length),(a,b)=>MathF.Abs(a-b)<=0.000001f).All(x=>x)
                        &&System.Numerics.Vector3.Distance(chain.Position,donor.Position)<=0.000001f&&System.Numerics.Vector3.Distance(chain.Rotation,donor.Rotation)<=0.000001f&&System.Numerics.Vector3.Distance(chain.Scale,donor.Scale)<=0.000001f;
                }
                if(!compatible){skipped.Add(new{Archive=Path.GetFileName(file),UID=target.Name,Reason="Category roots or source chain topology/transforms differ"});continue;}
                additions.Add($"obj.{count}.cat={category}");additions.Add($"obj.{count}.name={target.Name.ToUpperInvariant()}");count++;
                categories.Add(target.Name,category);
                corrected.Add(new{Archive=Path.GetFileName(file),UID=target.Name,Source=source.Name,Category=category,Roots=chains.Select(b=>b.ExternalName).ToArray()});
            }
        }
        destination??=root;
        string output=Path.Combine(destination,"rom","skin_param","mod_osage_setting.txt");Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string backup=Path.Combine(destination,"osage-category-backup","mod_osage_setting.txt");Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        if(!File.Exists(backup))File.WriteAllText(backup,original);
        string text=string.Join("\n",original.Split('\n').Select(l=>l.Trim().StartsWith("obj.length=")?$"obj.length={count}":l));
        if(additions.Count>0)text=text.TrimEnd()+"\n"+string.Join("\n",additions)+"\n";
        var result=Fields(text);
        foreach(var item in fields.Where(x=>x.Key!="obj.length"))if(result.GetValueOrDefault(item.Key)!=item.Value)throw new InvalidDataException("Existing Osage registration changed");
        // The game's parameter lookup uses lexical ordering, including obj.length.
        // Appending obj.N after obj.length makes that lookup fail.
        text = string.Join("\n", result.OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => x.Key + "=" + x.Value)) + "\n";
        File.WriteAllText(output,text);
        File.WriteAllText(ConversionReports.PathFor(destination,"OSAGE-CATEGORY-PROPAGATION.json"),JsonSerializer.Serialize(new{Corrected=corrected,Unclassified=skipped,ExistingDuplicateRows=duplicates,BeforeObjects=int.Parse(fields["obj.length"]),AfterObjects=count},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"[OSAGE_CATEGORIES] {corrected.Count} propagated, {skipped.Count} unclassified; existing records preserved.");
    }
}
