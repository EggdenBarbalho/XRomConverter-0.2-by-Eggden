using System.Buffers.Binary;
using System.Text;
using MikuMikuLibrary.Materials;
using MikuMikuLibrary.Objects;

namespace XRomConverter.Target.MegaMix;

internal sealed record ClassicIdentifierPatch(int Offset, uint Before, uint After, string Field);
internal sealed record PatchedClassicObjectSet(byte[] Bytes, IReadOnlyList<ClassicIdentifierPatch> Patches);

// A narrow writer for one little-endian, 32-bit classic object set. It changes
// identifiers only; mesh/skin/reserved bytes never pass through serialization.
internal static class ClassicObjectSetIdPatcher
{
    public static PatchedClassicObjectSet Apply(byte[] source, ObjectSet parsed, string targetName,
        uint targetId, IReadOnlyDictionary<uint, uint> textureMap)
    {
        uint Read(int offset)
        {
            if (offset < 0 || offset > source.Length - 4) throw new InvalidDataException("Classic object-set offset is out of range.");
            return BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(offset, 4));
        }
        int Offset(int field) => checked((int)Read(field));
        if (Read(0) is not (0x05062500 or 0x05062501) || Read(4) != 1 || parsed.Objects.Count != 1 ||
            Read(32) != parsed.TextureIds.Count)
            throw new InvalidDataException("Unsupported classic object-set header.");
        int objects = Offset(12), names = Offset(20), ids = Offset(24), textures = Offset(28);
        int obj = Offset(objects);
        if (Read(obj) != 0x10000 || Read(obj + 32) != parsed.Objects[0].Materials.Count)
            throw new InvalidDataException("Classic object/material header differs from the parsed reference.");
        int materials = checked(obj + Offset(obj + 36));
        byte[] name = Encoding.UTF8.GetBytes(targetName + "\0");
        int nameOffset = checked((source.Length + 3) & ~3);
        byte[] bytes = new byte[checked(nameOffset + name.Length)];
        source.CopyTo(bytes, 0); name.CopyTo(bytes, nameOffset);
        var patches = new List<ClassicIdentifierPatch>();
        void Patch(int offset, uint expected, uint value, string field)
        {
            uint actual = Read(offset);
            if (actual != expected || patches.Any(x => Math.Abs(x.Offset - offset) < 4))
                throw new InvalidDataException("Classic identifier location differs from the parsed reference: " + field);
            patches.Add(new(offset, actual, value, field));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
        }
        uint originalNameOffset = Read(names);
        if (originalNameOffset >= source.Length ||
            !Encoding.UTF8.GetBytes(parsed.Objects[0].Name + "\0").AsSpan()
                .SequenceEqual(source.AsSpan(checked((int)originalNameOffset), Encoding.UTF8.GetByteCount(parsed.Objects[0].Name) + 1)))
            throw new InvalidDataException("Classic source object name pointer differs.");
        Patch(names, originalNameOffset, checked((uint)nameOffset), "object-name-pointer");
        Patch(ids, parsed.Objects[0].Id, targetId, "object-id");
        for (int i = 0; i < parsed.TextureIds.Count; i++)
            Patch(checked(textures + i * 4), parsed.TextureIds[i], textureMap[parsed.TextureIds[i]], "texture-slot:" + i);
        for (int m = 0; m < parsed.Objects[0].Materials.Count; m++)
        {
            var material = parsed.Objects[0].Materials[m];
            if (material.MaterialTextures.Length != 8) throw new InvalidDataException("Expected eight classic material texture slots.");
            for (int slot = 0; slot < 8; slot++)
            {
                uint id = material.MaterialTextures[slot].TextureId;
                int at = checked(materials + m * Material.BYTE_SIZE + 20 + slot * 120 + 4);
                if (Read(at) != id) throw new InvalidDataException("Classic material texture offset differs.");
                if (id != uint.MaxValue) Patch(at, id, textureMap[id], "material:" + m + "/texture:" + slot);
            }
        }
        Verify(source, bytes, patches, targetName);
        return new(bytes, patches);
    }

    public static byte[] Verify(byte[] reference, byte[] output, IReadOnlyList<ClassicIdentifierPatch> patches, string targetName)
    {
        int appendedAt = checked((reference.Length + 3) & ~3);
        byte[] name = Encoding.UTF8.GetBytes(targetName + "\0");
        if (output.Length != appendedAt + name.Length || !output.AsSpan(appendedAt).SequenceEqual(name) ||
            output.AsSpan(reference.Length, appendedAt - reference.Length).IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("Unexpected bytes appended to the classic model.");
        byte[] reversed = output.AsSpan(0, reference.Length).ToArray();
        foreach (var patch in patches)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(reversed.AsSpan(patch.Offset, 4)) != patch.After)
                throw new InvalidDataException("Identifier patch did not survive archive transport.");
            BinaryPrimitives.WriteUInt32LittleEndian(reversed.AsSpan(patch.Offset, 4), patch.Before);
        }
        if (!reversed.AsSpan().SequenceEqual(reference))
            throw new InvalidDataException("Model bytes outside the declared identifiers changed.");
        return reversed;
    }
}
