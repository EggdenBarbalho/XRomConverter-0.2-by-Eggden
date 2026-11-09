using System.Globalization;
using System.IO.Compression;
using System.Text;
using MikuMikuLibrary.Objects.Extra.Parameters;
using XRomConverter.Ids;

namespace XRomConverter.Target.MegaMix;

internal sealed record SkinFieldPatch(string Key,string? Before,string After);
internal sealed record SkinPatchResult(string Target,string CompleteBaselineSha256,string CompleteReferenceSha256,
    string JSeKaiReferenceSha256,string OutputSha256,IReadOnlyList<SkinFieldPatch> Applied,
    IReadOnlyList<string> Removed,IReadOnlyList<string> PreservedSpecificFields,IReadOnlyList<string> PreservedCollisionGroups,IReadOnlyList<string> SourceLiteralCorrections,IReadOnlyList<string> TopologyAdjustments);

// Semantic three-way patch: preserve target-specific collision geometry as whole
// parameter groups. Deletions are explicit; omitted values are never equated to 0.
internal static class SkinParamPatchAdapter
{
    internal static Dictionary<string,string> Read(byte[] bytes) => Encoding.UTF8.GetString(bytes).Split('\n')
        .Select(x=>x.TrimEnd('\r').TrimStart('\uFEFF')).Where(x=>x.Length>0&&!x.StartsWith('#'))
        .Select(x=>x.Split('=',2)).ToDictionary(x=>x[0],x=>x[1],StringComparer.Ordinal);
    internal static bool Equal(string a,string b) => decimal.TryParse(a,NumberStyles.Float,CultureInfo.InvariantCulture,out var x)&&decimal.TryParse(b,NumberStyles.Float,CultureInfo.InvariantCulture,out var y)?x==y:a==b;
    internal static byte[] Entry(ZipArchive z,string name)
    {
        var entry=z.Entries.Single(e=>e.FullName.EndsWith("/skin_param/"+name,StringComparison.Ordinal)||e.FullName=="skin_param/"+name);
        using var s=entry.Open();using var m=new MemoryStream();s.CopyTo(m);return m.ToArray();
    }
    internal static IReadOnlyList<string> FitNodes(Dictionary<string,string> fields,IReadOnlyDictionary<string,int> counts)
    {
        var adjustments=new List<string>();
        foreach(var p in counts)
        {
            string prefix=p.Key+".node.";string length=prefix+"length";
            if(!fields.TryGetValue(length,out var old))throw new InvalidDataException("Missing parameter node length: "+p.Key);
            if(int.Parse(old)<p.Value)throw new InvalidDataException("Skin reference has fewer nodes than model: "+p.Key);
            var discard=fields.Keys.Where(k=>k.StartsWith(prefix)&&k!=length&&int.Parse(k[prefix.Length..].Split('.')[0])>=p.Value).ToArray();
            if(int.Parse(old)!=p.Value||discard.Length>0)adjustments.Add(p.Key+": parameter nodes "+old+" -> model nodes "+p.Value+"; "+discard.Length+" unused node fields removed");
            foreach(string k in discard)fields.Remove(k);fields[length]=p.Value.ToString();
        }
        return adjustments;
    }
    internal static IReadOnlyList<string> FitRelations(Dictionary<string,string> fields,IReadOnlyDictionary<string,int> counts,Dictionary<string,string> fallback)
    {
        var changes=new List<string>();
        foreach(var p in counts)
        {
            string prefix=p.Key+".root.boc.";int length=int.Parse(fields.GetValueOrDefault(prefix+"length","0"));bool invalid=false;
            for(int i=0;i<length;i++)
            {
                string q=prefix+i+".";string target=fields.GetValueOrDefault(q+"ed_root","");
                if(!counts.TryGetValue(target,out int targetNodes)||int.Parse(fields.GetValueOrDefault(q+"st_node","0"))>=p.Value||int.Parse(fields.GetValueOrDefault(q+"ed_node","0"))>=targetNodes)invalid=true;
            }
            if(!invalid)continue;
            foreach(string key in fields.Keys.Where(k=>k.StartsWith(prefix)).ToArray())fields.Remove(key);
            foreach(var f in fallback.Where(x=>x.Key.StartsWith(prefix)))fields.Add(f.Key,f.Value);
            if(!fields.ContainsKey(prefix+"length"))fields[prefix+"length"]="0";
            changes.Add(p.Key+": incompatible JSeKai BOC group replaced with original target BOC group");
        }
        return changes;
    }
    internal static SkinPatchResult Adapt(byte[] targetBytes,byte[] referenceBytes,byte[] jsekaiBytes,string destination,IReadOnlyDictionary<string,int> nodeCounts)
    {
        var target=Read(targetBytes);var reference=Read(referenceBytes);var patch=Read(jsekaiBytes);var result=new Dictionary<string,string>(target);
        // The same documented malformed literal fixed in the approved P4 test.
        const string correctedKey="c_hair_twin_r_osg.root.coli.9.bone.0.posy";
        bool corrected=patch.GetValueOrDefault(correctedKey)=="0.000000a";if(corrected)patch[correctedKey]="0.000000";
        var targetParameters=target.Keys.Select(k=>k.Split('.')[0]).ToHashSet();
        if(!targetParameters.SetEquals(reference.Keys.Select(k=>k.Split('.')[0]))||!targetParameters.SetEquals(patch.Keys.Select(k=>k.Split('.')[0])))throw new InvalidDataException("Skin patch parameter sets differ.");
        var geometryGroups=target.Where(x=>x.Key.Contains(".root.coli.")&&reference.TryGetValue(x.Key,out var value)&&!Equal(x.Value,value))
            .Select(x=>x.Key.Split('.')[0]).Distinct().ToArray();
        bool Geometry(string key)=>geometryGroups.Any(p=>key.StartsWith(p+".root.coli.",StringComparison.Ordinal)||key==p+".root.coli.length");
        var applied=new List<SkinFieldPatch>();var removed=new List<string>();var specific=new HashSet<string>();
        foreach(var field in patch)
        {
            if(reference.TryGetValue(field.Key,out var original)&&Equal(original,field.Value))continue;
            if(Geometry(field.Key)){specific.Add(field.Key);continue;}
            // A differing NT-specific value wins a true three-way conflict.
            if(target.TryGetValue(field.Key,out var own)&&reference.TryGetValue(field.Key,out original)&&!Equal(own,original)&&!Equal(own,field.Value)){specific.Add(field.Key);continue;}
            if(!result.TryGetValue(field.Key,out var before)||!Equal(before,field.Value)){applied.Add(new(field.Key,before,field.Value));result[field.Key]=field.Value;}
        }
        foreach(var field in reference.Where(x=>!patch.ContainsKey(x.Key)))
        {
            if(Geometry(field.Key)){specific.Add(field.Key);continue;}
            if(result.TryGetValue(field.Key,out var own)&&Equal(own,field.Value)){result.Remove(field.Key);removed.Add(field.Key);}
            else if(result.ContainsKey(field.Key))specific.Add(field.Key);
        }
        foreach(var field in target.Where(x=>!reference.TryGetValue(x.Key,out var baseline)||!Equal(x.Value,baseline)))
            if(result.TryGetValue(field.Key,out var final)&&Equal(field.Value,final))specific.Add(field.Key);
        var topologyAdjustments=FitNodes(result,nodeCounts).Concat(FitRelations(result,nodeCounts,target)).ToArray();
        applied.RemoveAll(x=>!result.TryGetValue(x.Key,out var final)||!Equal(final,x.After));
        removed.RemoveAll(result.ContainsKey);
        byte[] output=Encoding.UTF8.GetBytes("# New Type: Complete Modules baseline with topology-checked JSeKai 6.0 semantic patches.\n"+
            string.Join("\n",result.OrderBy(x=>x.Key,StringComparer.Ordinal).Select(x=>x.Key+"="+x.Value))+"\n");
        using(var stream=new MemoryStream(output)){var parsed=new OsageSkinParameterSet();parsed.Load(stream);}
        File.WriteAllBytes(destination,output);
        return new(Path.GetFileName(destination),PhysicalOccupancy.Hash(targetBytes),PhysicalOccupancy.Hash(referenceBytes),PhysicalOccupancy.Hash(jsekaiBytes),PhysicalOccupancy.Hash(output),applied,removed,specific.Order().ToArray(),geometryGroups,corrected?new[]{correctedKey+"=0.000000a -> 0.000000"}:Array.Empty<string>(),topologyAdjustments);
    }
}
