using System;
using System.IO;

namespace LibProsperoPkg.Util;

public class StreamReader : IMemoryReader, IDisposable
{
	private bool owns;

	private long startOffset;

	private Stream stream;

	public StreamReader(Stream s, long offset = 0L, bool takeOwnership = false)
	{
		stream = s;
		owns = takeOwnership;
		startOffset = offset;
	}

	public void Dispose()
	{
		if (owns)
		{
			stream.Dispose();
		}
	}

	public void Read(long pos, byte[] buf, int offset, int count)
	{
		stream.Position = pos + startOffset;
		stream.ReadExactly(buf, offset, count);
	}
}
