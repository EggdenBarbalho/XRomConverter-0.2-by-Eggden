using System.Text.Json;
using XRomConverter.Content;
using XRomConverter.Identity;
using XRomConverter.Ids;
using XRomConverter.Source.DivaX;
using XRomConverter.Target.MegaMix;

namespace XRomConverter.Analysis;

internal static class ModuleAnalysisCommand
{
    public static bool IsRequested(string[] args) =>
        args.Length > 0 && args[0].Equals("analyze-module", StringComparison.OrdinalIgnoreCase);

    public static int Run(string[] args)
    {
        if (args.Length < 3 || !short.TryParse(args[2], out short moduleId))
        {
            Console.Error.WriteLine("Usage: XRomConverter analyze-module <Diva X rom path> <module ID> [--dependency-source PATH]... [--target-id N] [--observed-target-id N] [--community-id-policy preserve|normalize] [--target-profile NAME] [--occupied-module-csv PATH] [--occupied-id-registry PATH] [--id-registry PATH] [--commit-id-binding] [--output PATH]");
            return 64;
        }
        int? targetId = null;
        int? observedTargetId = null;
        string communityIdPolicy = "preserve";
        string profile = "megamix_plus";
        string? occupiedModuleCsv = null;
        string? occupiedIdRegistry = null;
        string? outputPath = null;
        string? registryPath = null;
        bool commitIdBinding = false;
        var dependencyPaths = new List<string>();
        for (int i = 3; i < args.Length; i++)
        {
            if (args[i] == "--target-id" && i + 1 < args.Length) targetId = int.Parse(args[++i]);
            else if (args[i] == "--observed-target-id" && i + 1 < args.Length) observedTargetId = int.Parse(args[++i]);
            else if (args[i] == "--community-id-policy" && i + 1 < args.Length) communityIdPolicy = args[++i];
            else if (args[i] == "--target-profile" && i + 1 < args.Length) profile = args[++i];
            else if (args[i] == "--occupied-module-csv" && i + 1 < args.Length) occupiedModuleCsv = args[++i];
            else if (args[i] == "--occupied-id-registry" && i + 1 < args.Length) occupiedIdRegistry = args[++i];
            else if (args[i] == "--output" && i + 1 < args.Length) outputPath = args[++i];
            else if (args[i] == "--dependency-source" && i + 1 < args.Length) dependencyPaths.Add(args[++i]);
            else if (args[i] == "--id-registry" && i + 1 < args.Length) registryPath = args[++i];
            else if (args[i] == "--commit-id-binding") commitIdBinding = true;
        }
        if (commitIdBinding && registryPath == null)
        {
            Console.Error.WriteLine("--commit-id-binding requires --id-registry PATH.");
            return 64;
        }
        if (communityIdPolicy != "preserve" && communityIdPolicy != "normalize")
        {
            Console.Error.WriteLine("--community-id-policy must be preserve or normalize.");
            return 64;
        }
        LocalIdRegistry? localRegistry;
        try { localRegistry = registryPath == null ? null : LocalIdRegistry.Load(registryPath); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Invalid local ID registry: " + ex.Message);
            return 65;
        }

        XModuleAggregate aggregate;
        TextWriter originalOutput = Console.Out;
        var loadedSources = new List<ContentSource>();
        try
        {
            // Some legacy readers print parsing details to stdout. Keep the analysis
            // command's stdout as clean, machine-readable JSON.
            Console.SetOut(TextWriter.Null);
            var source = ContentSource.Load("primary", args[1]);
            loadedSources.Add(source);
            for (int i = 0; i < dependencyPaths.Count; i++)
                loadedSources.Add(ContentSource.Load($"dependency:{i + 1}", dependencyPaths[i]));
            aggregate = new XModuleReader().Read(source, loadedSources.Skip(1).ToArray(), moduleId);
        }
        finally
        {
            Console.SetOut(originalOutput);
            foreach (var loadedSource in loadedSources) loadedSource.Dispose();
        }
        var identifier = new ModuleIdentifier(ModuleIdentifier.LoadDefault());
        var normalized = new XModuleNormalizer().Normalize(aggregate, identifier);
        IReadOnlySet<int> occupied = occupiedModuleCsv == null
            ? new HashSet<int>()
            : ModuleArchiveIdCatalog.LoadOccupiedModuleIds(occupiedModuleCsv);
        var allOccupied = occupied.ToHashSet();
        if (occupiedIdRegistry != null)
            allOccupied.UnionWith(OccupiedIdRegistrySnapshot.Load(occupiedIdRegistry).ModuleIds);
        var binding = new ModuleIdResolver().Resolve(normalized, identifier, profile,
            targetId, allOccupied, localRegistry, observedTargetId, communityIdPolicy);
        var plan = new MegaMixBuildPlanner().Create(normalized, binding);
        if (commitIdBinding && plan.Status == "ready" &&
            binding.Source == "allocator-preview" && binding.TargetId.HasValue)
        {
            localRegistry!.Add(normalized, profile, binding.TargetId.Value);
            binding = new TargetIdBinding(binding.TargetId, "allocated", "local-registry");
            plan = new MegaMixBuildPlanner().Create(normalized, binding);
        }
        var result = new ModuleAnalysisResult(aggregate, normalized, plan);
        string json = JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        if (outputPath == null) Console.WriteLine(json);
        else
        {
            string fullPath = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, json);
            Console.WriteLine(fullPath);
        }
        return plan.Status == "ready" ? 0 : 2;
    }
}
