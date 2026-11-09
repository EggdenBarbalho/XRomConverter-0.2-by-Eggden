using System;
using System.Collections.Generic;

namespace XRomConverter;

internal sealed class CliOptions
{
	public string BaseRomPath { get; }

	public string MegaMixPath { get; }

	public string OutputPath { get; }

	public IReadOnlyList<string> DlcRomPaths { get; }

	public bool IncludeBaseContent { get; }

	private CliOptions(string baseRomPath, string megaMixPath, string outputPath, IReadOnlyList<string> dlcRomPaths, bool includeBaseContent)
	{
		BaseRomPath = baseRomPath;
		MegaMixPath = megaMixPath;
		OutputPath = outputPath;
		DlcRomPaths = dlcRomPaths;
		IncludeBaseContent = includeBaseContent;
	}

	public static bool TryParse(string[] args, out CliOptions? options, out string? error)
	{
		options = null;
		error = null;
		if (args.Length < 3)
		{
			error = "Missing required paths.";
			return false;
		}
		List<string> list = new List<string>();
		bool includeBaseContent = false;
		for (int i = 3; i < args.Length; i++)
		{
			if (args[i].Equals("--include-base", StringComparison.OrdinalIgnoreCase) || args[i].Equals("--all", StringComparison.OrdinalIgnoreCase))
			{
				includeBaseContent = true;
			}
			else if (args[i].Equals("--dlc", StringComparison.OrdinalIgnoreCase) || args[i].Equals("-d", StringComparison.OrdinalIgnoreCase))
			{
				if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
				{
					error = "The --dlc option requires a DLC package or rom path.";
					return false;
				}
				list.Add(args[++i]);
			}
		}
		options = new CliOptions(args[0], args[1], args[2], list, includeBaseContent);
		return true;
	}

	public static void PrintUsage()
	{
		Console.WriteLine("Usage: XRomConverter.exe \"X Rom Path\" \"MegaMix+ Path\" \"Output Path\" [--dlc \"DLC Package or Rom Path\"]... [--include-base] [--inspect] [OPTIONS]");
	}
}
