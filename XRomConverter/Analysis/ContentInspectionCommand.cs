using System.Text.Json;
using XRomConverter.Content;

namespace XRomConverter.Analysis;

internal static class ContentInspectionCommand
{
    public static bool IsRequested(string[] args) => args.Length > 0 &&
        args[0].Equals("list-modules", StringComparison.OrdinalIgnoreCase);

    public static int Run(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: XRomConverter list-modules <Diva X rom path>");
            return 64;
        }

        TextWriter originalOutput = Console.Out;
        ContentSource source;
        try
        {
            Console.SetOut(TextWriter.Null);
            source = ContentSource.Load("source", args[1]);
        }
        finally
        {
            Console.SetOut(originalOutput);
        }

        using (source)
        {
            var modules = source.Containers.SelectMany((container, containerIndex) =>
                container.ParameterDatabases.SelectMany(database => database.ModuleTable.Modules)
                    .Select(module => new
                    {
                        moduleId = module.ModuleID,
                        cosId = module.CosID,
                        character = module.Character.ToString(),
                        sortIndex = module.SortIndex,
                        name = source.Containers.SelectMany(x => x.StringArrays)
                            .Select(x => x.GetStringById(10010000 + module.ModuleID))
                            .FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? $"module_{module.ModuleID}",
                        packageId = source.FirstRead.AddonContentPackage.PackageID.ToString(),
                        containerIndex
                    }))
                .OrderBy(x => x.moduleId)
                .ToArray();

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                source = source.RootPath,
                modules
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return 0;
    }
}
