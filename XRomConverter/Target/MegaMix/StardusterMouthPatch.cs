using System.Buffers.Binary;
using System.Security.Cryptography;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.CriMw;

namespace XRomConverter.Target.MegaMix;

// PV092 only: the MOUTH_ANIM at 141.16666 s changes KUCHI_A_OLD (24)
// to KUCHI_SMILE (5). No generic ROB bank, timing or intensity changes.
internal static class StardusterMouthPatch
{
    private sealed record Script(string Folder, string Name, int Word, string Original, string Patched, bool Required);
    private static readonly Script[] Scripts =
    {
        new("script", "pv_092_easy.dsc", 2340, "43015bdf08080123d5249e857d609fa38e0705d023ff741f01ebbb0e58109619", "825a4c4a42e471402a8e43fca71f999c9b7a1408f15df8185425e17c612d7c9b", true),
        new("script", "pv_092_normal.dsc", 3040, "8977e806a864a3f47d97a637628bcb7bc1054f6da181a5a3c3f830d7e45a519c", "fc9e8662e92bc46a01d1e17ec8abd2a3ab025acb6b6f6915ae574aecb395e568", true),
        new("script", "pv_092_hard.dsc", 3086, "9924c7017b62f0fb262c167f3c24d708dc473aea45db1002b368e520893f4e02", "9191f64aa26686e00800cd9454d7770781bb0f543144d88d0a841425c3181079", true),
        new("script", "pv_092_extreme.dsc", 3462, "623fca65f5fc31b0212ca6c86eb341142d4b5ad75cd1b6ccafe5913b82b4bcda", "7930a2bacea75cff0e1f3f13696101407c45100e39eb1d97c1a62280d1b3b10d", true),
        // Optional editions are patched only when already present in the output.
        // The converter does not bundle or discover other mods' charts.
        new("script", "pv_092_extreme_1.dsc", 6226, "9f3a240496f722ca6c713441cacf0ade84cda6773b63fefee792581e6cc94597", "fda3bc3761fac366b68f337f8f5415578b3109ef878d3d8cadd2cd1fc88018eb", false),
        new("script_pv", "pv_092_nochart.dsc", 1064, "6a89011ff305999b85ae6a877ffa95bae73a0606b1a0d5564f72a1d8db04ca2c", "23642a5e99ee9648d9c4f36c52f2c3f89855677197b6d7266edccda72c7e52b7", false)
    };

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    internal static void Write(string output, CpkArchive game)
    {
        var pending = new List<(string Path, byte[] Bytes)>();
        foreach (var script in Scripts)
        {
            string path = Path.Combine(output, "rom", script.Folder, script.Name);
            byte[] bytes;
            if (File.Exists(path)) bytes = File.ReadAllBytes(path);
            else
            {
                if (!script.Required) continue;
                using var input = game.Open("rom/script/" + script.Name, EntryStreamMode.MemoryStream);
                using var buffer = new MemoryStream(); input.CopyTo(buffer); bytes = buffer.ToArray();
            }
            string hash = Hash(bytes);
            if (hash.Equals(script.Patched, StringComparison.OrdinalIgnoreCase)) continue;
            if (!hash.Equals(script.Original, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unrecognized Starduster script; refusing to overwrite: " + path);
            int argument = checked((script.Word + 3) * sizeof(int));
            if (argument + 12 > bytes.Length || BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(argument)) != 24
                || BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(argument + 4)) != 1667
                || BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(argument + 8)) != -1)
                throw new InvalidDataException("Unexpected Starduster mouth command: " + script.Name);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(argument), 5);
            if (!Hash(bytes).Equals(script.Patched, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Starduster mouth patch validation failed: " + script.Name);
            pending.Add((path, bytes));
        }
        // Validate the complete batch before writing. Successful generation is silent.
        foreach (var patch in pending)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(patch.Path)!);
            File.WriteAllBytes(patch.Path, patch.Bytes);
        }
    }
}
