namespace XRomConverter;

internal static class CorrectedSkinParamCatalog
{
    private const string ResourcePrefix =
        "XRomConverter.CorrectedSkinParam.";

    private static readonly string[] sAuxiliaryFiles =
    {
        "mdata_osage_setting.txt",
        "mdata_sp_skp_db.txt",
        "mod_osage_setting.txt",
        "osage_setting.txt",
        "sp_skp_db.txt"
    };

    public static bool TryCopy(string fileName, string outputPath)
    {
        if (!string.Equals(fileName, Path.GetFileName(fileName),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Invalid corrected skin parameter name: {fileName}");
        }

        using Stream? resource = typeof(CorrectedSkinParamCatalog)
            .Assembly.GetManifestResourceStream(ResourcePrefix + fileName);
        if (resource == null)
            return false;

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using (FileStream output = File.Create(outputPath))
            resource.CopyTo(output);

        Console.WriteLine(
            $"[SKIN PARAMETER] --> corrected embedded parameter: {fileName}");
        return true;
    }

    public static void CopyAuxiliaryFiles(string outputDirectory)
    {
        foreach (string fileName in sAuxiliaryFiles)
        {
            TryCopy(fileName, Path.Combine(outputDirectory, fileName));
        }
    }
}
