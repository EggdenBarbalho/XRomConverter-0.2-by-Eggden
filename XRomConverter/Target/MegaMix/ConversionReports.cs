namespace XRomConverter.Target.MegaMix;

internal static class ConversionReports
{
    internal static string PathFor(string output, string name)
    {
        string directory = Path.Combine(output, "rom", "report");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, name);
    }
}
