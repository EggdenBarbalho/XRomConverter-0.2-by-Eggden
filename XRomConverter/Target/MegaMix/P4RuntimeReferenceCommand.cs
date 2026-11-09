using System.Text;
using System.Text.Json;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Databases;
using MikuMikuLibrary.IO;
using XRomConverter.Ids;

namespace XRomConverter.Target.MegaMix;

// Compatibility recovery for the gameplay-tested P4 reference. This deliberately
// retains its costume/item/model namespaces; it is not the isolated allocator.
internal static class P4RuntimeReferenceCommand
{
    private static readonly string[] Required = {
        "rom/bone_data.bin", "rom/gm_module_tbl.farc", "rom/mod_chritm_prop.farc",
        "rom/2d/mod_spr_db.bin", "rom/2d/spr_sel_md661cmn.farc", "rom/lang2/mod_str_array.toml",
        "rom/objset/mikitm000.farc", "rom/objset/mikitm153_x.farc",
        "rom/objset/mikitm553_x.farc", "rom/objset/mikitm753_x.farc",
        "rom/objset/mod_obj_db.bin", "rom/objset/mod_tex_db.bin",
        "rom/skin_param/ext_skp_mikitm153_joha_outer_153__divskn_x.txt",
        "rom/skin_param/ext_skp_mikitm753_atam_atama_153__divskn_x.txt",
        "rom/skin_param/mod_osage_setting.txt"
    };

    internal static int Run(string[] args)
    {
        string? temporary = null;
        try
        {
            if (args.Length != 4)
                throw new ArgumentException("Usage: rebuild-p4-runtime-reference WORKING_P4_661 R2_PRESENTATION_662 NEW_OUTPUT");
            string reference = Path.GetFullPath(args[1]), presentation = Path.GetFullPath(args[2]);
            string output = Path.GetFullPath(args[3]);
            if (Directory.Exists(output) || File.Exists(output))
                throw new InvalidDataException("Output must not already exist.");
            string[] files = Directory.GetFiles(Path.Combine(reference, "rom"), "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(reference, p).Replace('\\', '/')).Order().ToArray();
            if (!files.SequenceEqual(Required.Order()))
                throw new InvalidDataException("Working P4 runtime must contain exactly the known fifteen files, including skeleton and head.");
            var sourceModule = ModuleTableReference.ReadFields(Path.Combine(reference, "rom/gm_module_tbl.farc"));
            if (sourceModule["module.0.id"] != "661" || sourceModule["module.0.cos"] != "COS_301" ||
                sourceModule["module.data_list.length"] != "1")
                throw new InvalidDataException("Expected the stable module 661/COS_301 reference.");
            var targetModule = ModuleTableReference.ReadFields(Path.Combine(presentation, "rom/gm_module_tbl.farc"));
            if (targetModule["module.0.id"] != "662" || targetModule["module.0.chara"] != "MIKU" ||
                targetModule["module.data_list.length"] != "1" || targetModule["module.0.attr"] != "8")
                throw new InvalidDataException("Presentation must be the Complete Modules registration for slot 662.");
            ValidateReferenceLinks(reference);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            temporary = Path.Combine(Path.GetDirectoryName(output)!, ".p4-runtime-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            var inputs = new Dictionary<string, string>();
            void Copy(string root, string relative)
            {
                string source = Path.Combine(root, relative), destination = Path.Combine(temporary, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
                inputs[source] = Hash(source);
            }
            foreach (string relative in Required.Where(p => !p.StartsWith("rom/2d/") &&
                p != "rom/gm_module_tbl.farc" && p != "rom/lang2/mod_str_array.toml")) Copy(reference, relative);
            foreach (string relative in new[] { "rom/2d/mod_spr_db.bin", "rom/2d/spr_sel_md662cmn.farc", "rom/lang2/mod_str_array.toml" })
                Copy(presentation, relative);
            string modulePath = Path.Combine(temporary, "rom/gm_module_tbl.farc");
            Copy(presentation, "rom/gm_module_tbl.farc");
            ReplaceCostume(modulePath);
            File.WriteAllText(Path.Combine(temporary, "config.toml"),
                "enabled = true\nname = \"P4: Dancing All Night Miku (662 runtime reference r3)\"\n" +
                "author = \"XRomConverter compatibility reconstruction\"\ninclude = [\".\"]\n", Encoding.UTF8);
            ValidateReferenceLinks(temporary);
            var emittedModule = ModuleTableReference.ReadFields(modulePath);
            var expected = targetModule.ToDictionary(x => x.Key, x => x.Key == "module.0.cos" ? "COS_301" : x.Value);
            if (!emittedModule.OrderBy(x => x.Key).SequenceEqual(expected.OrderBy(x => x.Key)))
                throw new InvalidDataException("Registration changed beyond costume binding.");
            var preserved = Required.Where(p => !p.StartsWith("rom/2d/") &&
                p != "rom/gm_module_tbl.farc" && p != "rom/lang2/mod_str_array.toml").ToArray();
            foreach (string relative in preserved)
                if (Hash(Path.Combine(reference, relative)) != Hash(Path.Combine(temporary, relative)))
                    throw new InvalidDataException("Reference bytes changed: " + relative);
            foreach (var input in inputs)
                if (Hash(input.Key) != input.Value) throw new InvalidDataException("Input changed during build.");
            var manifest = new {
                schemaVersion = 1, phase = "p4-runtime-reference-recovery-r3", moduleId = 662,
                costumeId = 300, itemIds = new[] { 1052, 1053, 1054 }, enabled = true,
                runtimeVerified = false, referenceRuntimeReportedStableByUser = true,
                isolatedPhysicalNamespaces = false, sharedDependencies = new[] { "bone_data.bin", "MIKITM000" },
                reference, presentation, sourceModuleSha256 = Hash(Path.Combine(reference, "rom/gm_module_tbl.farc")),
                preservedRuntimeFiles = preserved, inputs,
                emittedFiles = Directory.GetFiles(temporary, "*", SearchOption.AllDirectories).Order()
                    .Select(p => new { path = Path.GetRelativePath(temporary, p).Replace('\\', '/'), sha256 = Hash(p) }).ToArray()
            };
            File.WriteAllText(Path.Combine(temporary, "build-manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            Directory.Move(temporary, output);
            temporary = null;
            Console.WriteLine(output);
            Console.WriteLine("Slot 662 -> COS_301 -> body 1052, hands 1053, hair 1054. Complete reference skeleton/head/skin retained. Gameplay test pending.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
        finally { if (temporary != null && Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); }
    }

    private static string Hash(string path) => PhysicalOccupancy.Hash(File.ReadAllBytes(path));

    private static string ReadItems(string root)
    {
        using var archive = BinaryFile.Load<FarcArchive>(Path.Combine(root, "rom/mod_chritm_prop.farc"));
        using var stream = archive.Open("mikitm_tbl.txt", EntryStreamMode.MemoryStream);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void ValidateReferenceLinks(string root)
    {
        var fields = ReadItems(root).Split('\n').Select(x => x.TrimEnd('\r').TrimStart('\uFEFF'))
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Split('=', 2))
            .ToDictionary(x => x[0], x => x[1], StringComparer.Ordinal);
        var required = new Dictionary<string, string> {
            ["cos.0.id"] = "300", ["cos.0.item.0"] = "1054", ["cos.0.item.1"] = "1052",
            ["cos.0.item.2"] = "1053", ["cos.0.item.length"] = "3", ["cos.length"] = "1", ["item.length"] = "3"
        };
        foreach (var pair in required)
            if (!fields.TryGetValue(pair.Key, out var value) || value != pair.Value)
                throw new InvalidDataException("Reference costume/item binding differs: " + pair.Key);
        using var dbStream = new MemoryStream(File.ReadAllBytes(Path.Combine(root, "rom/objset/mod_obj_db.bin")));
        var db = BinaryFile.Load<ObjectDatabase>(dbStream);
        string[] names = { "MIKITM153_X", "MIKITM553_X", "MIKITM753_X" };
        string[] subIds = { "10", "14", "1" };
        for (int i = 0; i < 3; i++)
        {
            string prefix = $"item.{i}.";
            var set = db.ObjectSets.Single(x => x.Name == names[i]);
            if (fields[prefix + "no"] != (1052 + i).ToString() || fields[prefix + "sub_id"] != subIds[i] ||
                fields[prefix + "objset.0"] != set.Name || fields[prefix + "data.obj.0.uid"] != set.Objects.Single().Name ||
                fields[prefix + "data.obj.0.uid"] != fields[prefix + "data.obj.0.uid"].ToUpperInvariant() ||
                !File.Exists(Path.Combine(root, "rom/objset", set.ArchiveFileName)))
                throw new InvalidDataException("Reference object binding is inconsistent: " + names[i]);
        }
        var head = db.ObjectSets.Single(x => x.Name == "MIKITM000");
        if (!File.Exists(Path.Combine(root, "rom/objset", head.ArchiveFileName)) ||
            !File.Exists(Path.Combine(root, "rom/bone_data.bin")))
            throw new InvalidDataException("Reference head or skeleton is absent.");
    }

    private static void ReplaceCostume(string path)
    {
        var files = new Dictionary<string, byte[]>();
        using (var archive = BinaryFile.Load<FarcArchive>(path))
            foreach (string name in archive.FileNames)
            {
                using var stream = archive.Open(name, EntryStreamMode.MemoryStream);
                using var memory = new MemoryStream(); stream.CopyTo(memory); files.Add(name, memory.ToArray());
            }
        string text = Encoding.UTF8.GetString(files["gm_module_id.bin"]);
        string[] lines = text.Split('\n');
        int index = Array.FindIndex(lines, x => x.TrimStart('\uFEFF').StartsWith("module.0.cos=", StringComparison.Ordinal));
        if (index < 0) throw new InvalidDataException("Module costume field absent.");
        lines[index] = "module.0.cos=COS_301" + (lines[index].EndsWith('\r') ? "\r" : "");
        files["gm_module_id.bin"] = Encoding.UTF8.GetBytes(string.Join('\n', lines));
        using var output = new FarcArchive();
        foreach (var file in files) output.Add(file.Key, new MemoryStream(file.Value), leaveOpen: false);
        output.Save(path);
    }
}
