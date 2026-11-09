using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FirstLib.FirstRead;
using FirstLib.IO;
using MikuMikuLibrary.Cryptography;

namespace XRomConverter.Content;

internal sealed class ContentSource : IDisposable
{
	private readonly PsarcArchive? mPsarc;

	public string Id { get; }

	public string RootPath { get; }

	public ADCT FirstRead { get; }

	public IReadOnlyList<ACCT> Containers => FirstRead.AddonContentContainers;

	public bool HasPsarc => mPsarc != null;

	private ContentSource(string id, string rootPath, ADCT firstRead, PsarcArchive? psarc)
	{
		Id = id;
		RootPath = rootPath;
		FirstRead = firstRead;
		mPsarc = psarc;
	}

	public static ContentSource Load(string id, string path)
	{
		string text = NormalizeRomPath(path);
		string text2 = Path.Combine(text, "firstread.bin");
		if (!File.Exists(text2))
		{
			throw new FileNotFoundException("firstread.bin was not found in " + text + ".", text2);
		}
		ADCT firstRead = LoadFirstRead(text2);
		string text3 = Path.Combine(text, "data.psarc");
		PsarcArchive psarc = (File.Exists(text3) ? new PsarcArchive(text3) : null);
		return new ContentSource(id, text, firstRead, psarc);
	}

	public IEnumerable<string> EnumerateObjectSetFiles()
	{
		return Containers.SelectMany((ACCT x) => x.ObjsetFlist).Distinct<string>(StringComparer.OrdinalIgnoreCase);
	}

	public bool TryOpen(string fileName, out Stream? stream)
	{
		foreach (ACCT container in Containers)
		{
			foreach (ACCTPath item in container.Paths.Where((ACCTPath x) => x.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
			{
				if (TryOpen(item, out stream))
				{
					return true;
				}
			}
		}
		if (TryOpenLoose(Path.Combine("data", fileName), out stream))
		{
			return true;
		}
		if (mPsarc != null && mPsarc.TryOpen(fileName, out stream))
		{
			return true;
		}
		stream = null;
		return false;
	}

	public Stream OpenRequired(string fileName)
	{
		if (TryOpen(fileName, out Stream stream))
		{
			return stream;
		}
		throw new FileNotFoundException($"Could not resolve '{fileName}' in content source '{Id}' ({RootPath}).");
	}

	private bool TryOpen(ACCTPath entry, out Stream? stream)
	{
		string text = Path.Combine(entry.FilePath.Replace('/', Path.DirectorySeparatorChar), entry.FileName);
		if (entry.Flags == ACCTPathMode.Packed && TryOpenLoose(Path.Combine("data", entry.FileName), out stream))
		{
			return true;
		}
		if (TryOpenLoose(text, out stream))
		{
			return true;
		}
		if (mPsarc != null && (mPsarc.TryOpen(entry.FileName, out stream) || mPsarc.TryOpen(text, out stream)))
		{
			return true;
		}
		stream = null;
		return false;
	}

	private bool TryOpenLoose(string relativePath, out Stream? stream)
	{
		string safePath = GetSafePath(relativePath);
		if (!File.Exists(safePath))
		{
			stream = null;
			return false;
		}
		stream = File.Open(safePath, FileMode.Open, FileAccess.Read, FileShare.Read);
		return true;
	}

	private string GetSafePath(string relativePath)
	{
		string fullPath = Path.GetFullPath(Path.Combine(RootPath, relativePath));
		string value = RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
		if (!fullPath.StartsWith(value, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("Content path escapes source root: " + relativePath);
		}
		return fullPath;
	}

	private static string NormalizeRomPath(string path)
	{
		string fullPath = Path.GetFullPath(path.Trim('"'));
		if (File.Exists(Path.Combine(fullPath, "firstread.bin")))
		{
			return fullPath;
		}
		string text = Path.Combine(fullPath, "rom");
		if (File.Exists(Path.Combine(text, "firstread.bin")))
		{
			return Path.GetFullPath(text);
		}
		return fullPath;
	}

	private static ADCT LoadFirstRead(string firstReadPath)
	{
		XReader xReader = new XReader(File.Open(firstReadPath, FileMode.Open, FileAccess.Read, FileShare.Read));
		try
		{
			if (xReader.ReadString(StringFormat.FixedLength, 4) == "DIVA")
			{
				xReader.Seek(0, XSeekOrigin.Begin);
				MemoryStream input = DivafileDecryptor.DecryptToMemoryStream(xReader.BaseStream);
				xReader.Close();
				xReader = new XReader(input);
			}
			else
			{
				xReader.Seek(0, XSeekOrigin.Begin);
			}
			ADCT aDCT = new ADCT();
			aDCT.Read(xReader);
			return aDCT;
		}
		finally
		{
			xReader.Close();
		}
	}

	public void Dispose()
	{
		mPsarc?.Dispose();
	}
}
