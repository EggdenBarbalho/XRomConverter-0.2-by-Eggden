using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.CriMw;
using MikuMikuLibrary.Archives.Extensions;
using MikuMikuLibrary.IO;

namespace XRomConverter.Target.MegaMix;

// User-tested PV628 only. Patch classic motion values in place so unrelated
// keysets, tangents, frames and metadata never pass through a reserializer.
internal static class FreelyTomorrowClKaoPatch
{
    private const string OriginalHash = "5642D2F905AF59C521191FF86DD48CEBF486E83900F1C1E56185F6A9F6FBFF98";
    private const string PatchedHash = "EB13A366BE24E74AB7EE579B7C4F10188E9C5A043C7ACB6872546383C8C4C73C";
    private const string Entry = "mot_PV628.bin";
    private const int TargetKeySet = 41; // cl_kao rotation Z in the verified payload.
    private const int TargetKeyCount = 10509;

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    internal static byte[] Patch(byte[] original)
    {
        string hash = Hash(original);
        if (hash == PatchedHash) return original;
        if (hash != OriginalHash)
            throw new InvalidDataException("Unrecognized PV628 motion; refusing to overwrite cl_kao.");

        int U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(offset, 2));
        int I32(int offset) => BinaryPrimitives.ReadInt32LittleEndian(original.AsSpan(offset, 4));
        int info = I32(0), types = I32(4), cursor = I32(8);
        if ((U16(info) & 0x3fff) <= TargetKeySet || U16(info + 2) != 9696)
            throw new InvalidDataException("Unexpected PV628 motion header.");

        byte[] result = original.ToArray();
        for (int index = 0; index <= TargetKeySet; index++)
        {
            int type = (U16(types + index / 8 * 2) >> (index % 8 * 2)) & 3;
            if (type == 0) continue;
            if (type == 1) { cursor += 4; continue; }
            int count = U16(cursor);
            cursor = (cursor + 2 + count * 2 + 3) & ~3;
            int stride = type == 3 ? 8 : 4;
            if (index == TargetKeySet)
            {
                if (count != TargetKeyCount)
                    throw new InvalidDataException("Unexpected cl_kao Z key count.");
                for (int key = 0; key < count; key++)
                {
                    int offset = cursor + key * stride;
                    float value = BitConverter.Int32BitsToSingle(I32(offset)) + MathF.PI / 2;
                    BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset, 4),
                        BitConverter.SingleToInt32Bits(value));
                }
            }
            cursor += count * stride;
        }
        if (Hash(result) != PatchedHash)
            throw new InvalidDataException("PV628 patch differs from the user-tested payload.");
        return result;
    }

    internal static void Write(string output, CpkArchive game)
    {
        string path = Path.Combine(output, "rom", "rob", "mot_PV628.farc");
        using var archive = File.Exists(path)
            ? BinaryFile.Load<FarcArchive>(path)
            : game.Open<FarcArchive>("rom/rob/mot_PV628.farc");
        if (archive.FileNames.Count() != 1 || archive.FileNames.Single() != Entry)
            throw new InvalidDataException("Unexpected PV628 archive entries; no file changed.");
        using var input = archive.Open(Entry, EntryStreamMode.MemoryStream);
        using var memory = new MemoryStream();
        input.CopyTo(memory);
        byte[] original = memory.ToArray(), patched = Patch(original);
        bool alreadyApplied = ReferenceEquals(original, patched);
        if (!alreadyApplied)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".pv628-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var packed = new FarcArchive())
                {
                    packed.Add(Entry, new MemoryStream(patched), false);
                    packed.Save(temp);
                }
                using (var check = BinaryFile.Load<FarcArchive>(temp))
                using (var stream = check.Open(Entry, EntryStreamMode.MemoryStream))
                using (var verified = new MemoryStream())
                {
                    stream.CopyTo(verified);
                    if (!verified.ToArray().SequenceEqual(patched))
                        throw new InvalidDataException("PV628 archive verification failed.");
                }
                archive.Dispose();
                File.Move(temp, path, overwrite: true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        File.WriteAllText(ConversionReports.PathFor(output, "PV628-CL-KAO-PATCH.json"),
            JsonSerializer.Serialize(new { PV = 628, Motion = "PV628_FLT_P1_00", Bone = "cl_kao",
                Axis = "Z", Degrees = 90, Keys = TargetKeyCount, AlreadyApplied = alreadyApplied,
                PayloadSha256 = PatchedHash, UserTestedStandalone = true, AllModulesVisuallyVerified = false },
                new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("[PV628_CL_KAO] Z +90 degrees: " + (alreadyApplied ? "already applied." : "applied."));
    }
}
