using System.Text.Json;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.Extensions;
using MikuMikuLibrary.Databases;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Objects;
using MikuMikuLibrary.Objects.Extra.Parameters;
using MikuMikuLibrary.Objects.Extra.Blocks;
using MikuMikuLibrary.Textures;

namespace XRomConverter.Analysis;

internal static class ModuleBuildValidator
{
    public static bool IsRequested(string[] args) => args.Length > 0 &&
        args[0].Equals("validate-module-build", StringComparison.OrdinalIgnoreCase);

    public static int Run(string[] args)
    {
        if (args.Length != 2 || !Directory.Exists(args[1])) return 64;
        var checks = new List<object>();
        var errors = new List<string>();
        foreach (string path in Directory.EnumerateFiles(args[1], "*.farc", SearchOption.AllDirectories))
        {
            try
            {
                using var archive = BinaryFile.Load<FarcArchive>(path);
                foreach (string entry in archive.FileNames)
                {
                    if (entry.EndsWith("_obj.bin", StringComparison.OrdinalIgnoreCase)) archive.Open<ObjectSet>(entry);
                    else if (entry.EndsWith("_tex.bin", StringComparison.OrdinalIgnoreCase)) archive.Open<TextureSet>(entry);
                }
                checks.Add(new { kind = "farc", path, entries = archive.FileNames.ToArray(), valid = true });
            }
            catch (Exception ex) { errors.Add($"{path}: {ex.Message}"); }
        }
        foreach (string path in Directory.EnumerateFiles(args[1], "mod_obj_db.bin", SearchOption.AllDirectories))
            TryLoad(path, "object-database", () => BinaryFile.Load<ObjectDatabase>(path), checks, errors);
        foreach (string path in Directory.EnumerateFiles(args[1], "mod_tex_db.bin", SearchOption.AllDirectories))
            TryLoad(path, "texture-database", () => BinaryFile.Load<TextureDatabase>(path), checks, errors);
        string skinDirectory = Path.Combine(args[1], "rom", "skin_param");
        if (Directory.Exists(skinDirectory))
            foreach (string path in Directory.EnumerateFiles(skinDirectory, "ext_skp_*.txt"))
                TryLoad(path, "skin-parameter", () => { var value = new OsageSkinParameterSet(); value.Load(path); return value; }, checks, errors);
        ValidateSkinBindings(args[1], checks, errors);
        Console.WriteLine(JsonSerializer.Serialize(new { valid = errors.Count == 0, checks, errors },
            new JsonSerializerOptions { WriteIndented = true }));
        return errors.Count == 0 ? 0 : 2;
    }

    private static void ValidateSkinBindings(string root, List<object> checks, List<string> errors)
    {
        string objectSetDir = Path.Combine(root, "rom", "objset");
        if (!Directory.Exists(objectSetDir)) return;
        foreach (string path in Directory.EnumerateFiles(objectSetDir, "*.farc"))
        {
            using var farc = BinaryFile.Load<FarcArchive>(path);
            string? entry = farc.FileNames.FirstOrDefault(x => x.EndsWith("_obj.bin", StringComparison.OrdinalIgnoreCase));
            if (entry == null) continue;
            var set = farc.Open<ObjectSet>(entry);
            foreach (var obj in set.Objects)
            {
                var osg = obj.Skin?.Blocks.OfType<OsageBlock>().ToArray() ?? Array.Empty<OsageBlock>();
                var mot = obj.Skin?.Blocks.OfType<MotionBlock>().ToArray() ?? Array.Empty<MotionBlock>();
                if (mot.Length > 0) errors.Add($"{obj.Name}: {mot.Length} MotionBlock(s) remain in MegaMix output.");
                if (osg.Length == 0) continue;
                string ospPath = Path.Combine(root, "rom", "skin_param", "ext_skp_" + obj.Name + ".txt");
                if (!File.Exists(ospPath)) { errors.Add($"{obj.Name}: expected skin parameter file is missing."); continue; }
                var osp = new OsageSkinParameterSet(); osp.Load(ospPath);
                foreach (var block in osg)
                    if (osp.Parameters.Count(x => x.Name == block.ExternalName) != 1)
                        errors.Add($"{obj.Name}: OSG '{block.ExternalName}' does not have exactly one parameter.");
                var names = osp.Parameters.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var parameter in osp.Parameters)
                {
                    foreach (string target in parameter.Bocs.Select(x => x.EdRoot).Where(x => !string.IsNullOrEmpty(x)))
                        if (!names.Contains(target)) errors.Add($"{obj.Name}: BOC target '{target}' is missing.");
                    if (!string.IsNullOrEmpty(parameter.CollisionTargetOsage) && !names.Contains(parameter.CollisionTargetOsage))
                        errors.Add($"{obj.Name}: collision target '{parameter.CollisionTargetOsage}' is missing.");
                }
                checks.Add(new { kind = "skin-binding", objectName = obj.Name, osg = osg.Select(x => x.ExternalName).ToArray(), parameters = osp.Parameters.Select(x => x.Name).ToArray(), valid = true });
            }
        }
    }

    private static void TryLoad(string path, string kind, Func<object> loader,
        List<object> checks, List<string> errors)
    {
        try { loader(); checks.Add(new { kind, path, valid = true }); }
        catch (Exception ex) { errors.Add($"{path}: {ex.Message}"); }
    }
}
