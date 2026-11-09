using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using XRomConverter.Analysis;

namespace XRomConverter.Ids;

internal sealed record LocalIdAllocation(string IdentityKey, string? CanonicalId,
    string TargetProfile, int TargetId, string Source);

internal sealed record LocalIdRegistryDocument(int SchemaVersion, string Installation,
    IReadOnlyList<LocalIdAllocation> Allocations);

internal sealed class LocalIdRegistry
{
    private readonly string mPath;
    private readonly LocalIdRegistryDocument mDocument;

    private LocalIdRegistry(string path, LocalIdRegistryDocument document)
    {
        mPath = path;
        mDocument = document;
        Validate(document);
    }

    public static LocalIdRegistry Load(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            return new LocalIdRegistry(fullPath,
                new LocalIdRegistryDocument(1, "local-megamix-plus", Array.Empty<LocalIdAllocation>()));
        var document = JsonSerializer.Deserialize<LocalIdRegistryDocument>(File.ReadAllText(fullPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Empty local ID registry.");
        return new LocalIdRegistry(fullPath, document);
    }

    public bool TryGet(NormalizedModule module, string targetProfile, out int targetId)
    {
        string identityKey = GetIdentityKey(module);
        var allocation = mDocument.Allocations.SingleOrDefault(x =>
            x.IdentityKey == identityKey && x.TargetProfile == targetProfile);
        targetId = allocation?.TargetId ?? -1;
        return allocation != null;
    }

    public IReadOnlySet<int> GetAllocatedIds(string targetProfile) =>
        mDocument.Allocations.Where(x => x.TargetProfile == targetProfile)
            .Select(x => x.TargetId).ToHashSet();

    public void Add(NormalizedModule module, string targetProfile, int targetId)
    {
        string identityKey = GetIdentityKey(module);
        if (TryGet(module, targetProfile, out int existing))
        {
            if (existing != targetId)
                throw new InvalidDataException($"Local identity '{identityKey}' is already bound to {existing}.");
            return;
        }
        if (mDocument.Allocations.Any(x => x.TargetProfile == targetProfile && x.TargetId == targetId))
            throw new InvalidDataException($"Target ID {targetId} is already allocated in profile '{targetProfile}'.");

        var updated = mDocument with { Allocations = mDocument.Allocations
            .Append(new LocalIdAllocation(identityKey, module.Identity?.CanonicalId,
                targetProfile, targetId, "allocator"))
            .OrderBy(x => x.TargetProfile).ThenBy(x => x.TargetId).ToArray() };
        Save(updated);
    }

    public static string GetIdentityKey(NormalizedModule module)
    {
        if (module.Identity != null) return "canonical:" + module.Identity.CanonicalId;
        var signature = new StringBuilder(module.Character);
        if (!module.SourceName.StartsWith("module_", StringComparison.OrdinalIgnoreCase))
            signature.Append('|').Append(module.SourceName);
        foreach (var component in module.Components.OrderBy(x => x.Role).ThenBy(x => x.SourceSubId))
        {
            signature.Append('|').Append(component.Role).Append(':').Append(component.SourceSubId);
            foreach (var set in component.ObjectSets.OrderBy(x => x.SourceName))
            {
                signature.Append('|').Append(set.SourceName);
                foreach (var obj in set.Objects.OrderBy(x => x.SourceName))
                    signature.Append('|').Append(obj.SourceName);
                foreach (var texture in set.Textures.OrderBy(x => x.Name ?? x.SourceId.ToString()))
                    signature.Append('|').Append(texture.Name ?? texture.SourceId.ToString());
            }
        }
        return "fingerprint:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(signature.ToString()))).ToLowerInvariant();
    }

    private void Save(LocalIdRegistryDocument document)
    {
        string? directory = Path.GetDirectoryName(mPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temporary = mPath + ".tmp";
        string json = JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        File.WriteAllText(temporary, json);
        File.Move(temporary, mPath, true);
    }

    private static void Validate(LocalIdRegistryDocument document)
    {
        if (document.SchemaVersion != 1)
            throw new InvalidDataException($"Unsupported local ID registry schema {document.SchemaVersion}.");
        var duplicateIdentity = document.Allocations.GroupBy(x => (x.IdentityKey, x.TargetProfile))
            .FirstOrDefault(x => x.Count() > 1);
        if (duplicateIdentity != null)
            throw new InvalidDataException($"Duplicate local binding for '{duplicateIdentity.Key.IdentityKey}' in '{duplicateIdentity.Key.TargetProfile}'.");
        var duplicateTarget = document.Allocations.GroupBy(x => (x.TargetProfile, x.TargetId))
            .FirstOrDefault(x => x.Count() > 1);
        if (duplicateTarget != null)
            throw new InvalidDataException($"Duplicate target ID {duplicateTarget.Key.TargetId} in '{duplicateTarget.Key.TargetProfile}'.");
    }
}
