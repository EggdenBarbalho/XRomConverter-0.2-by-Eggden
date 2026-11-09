using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace XRomConverter.Content;

internal sealed class ContentResolver
{
	private readonly IReadOnlyList<ContentSource> mSources;

	public ContentResolver(params ContentSource[] sources)
	{
		mSources = sources.Distinct().ToArray();
	}

	public bool TryOpen(string fileName, out Stream? stream, out ContentSource? source)
	{
		foreach (ContentSource mSource in mSources)
		{
			if (mSource.TryOpen(fileName, out stream))
			{
				source = mSource;
				return true;
			}
		}
		stream = null;
		source = null;
		return false;
	}

	public Stream OpenRequired(string fileName, out ContentSource source)
	{
		if (TryOpen(fileName, out Stream stream, out ContentSource source2))
		{
			source = source2;
			return stream;
		}
		string value = string.Join(", ", mSources.Select((ContentSource x) => x.Id));
		throw new FileNotFoundException($"Could not resolve '{fileName}' in any enabled source ({value}).");
	}
}
