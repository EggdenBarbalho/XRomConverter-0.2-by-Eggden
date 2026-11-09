using System;
using System.Collections.Generic;
using System.Linq;

namespace XRomConverter.Content;

internal sealed class ContentSourceGroup : IDisposable
{
	private readonly IReadOnlyList<ContentSource> mSources;

	public ContentSourceGroup(IEnumerable<ContentSource> sources)
	{
		mSources = sources.ToArray();
	}

	public void Dispose()
	{
		foreach (ContentSource mSource in mSources)
		{
			mSource.Dispose();
		}
	}
}
