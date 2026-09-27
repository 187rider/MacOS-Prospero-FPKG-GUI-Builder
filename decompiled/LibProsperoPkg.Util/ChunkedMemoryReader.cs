using System;
using System.IO;

namespace LibProsperoPkg.Util;

internal class ChunkedMemoryReader : IMemoryReader, IDisposable
{
	private int chunkSize;

	private int[] chunks;

	private IMemoryReader reader;

	public ChunkedMemoryReader(IMemoryReader mr, int chunkSize, int[] chunks)
	{
		this.chunks = chunks;
		this.chunkSize = chunkSize;
		reader = mr;
	}

	public void Dispose()
	{
	}

	public void Read(long pos, byte[] buf, int offset, int count)
	{
		ArgumentNullException.ThrowIfNull(buf, "buf");
		ArgumentOutOfRangeException.ThrowIfNegative(pos, "pos");
		ArgumentOutOfRangeException.ThrowIfNegative(offset, "offset");
		ArgumentOutOfRangeException.ThrowIfNegative(count, "count");
		if (offset > buf.Length - count)
		{
			throw new ArgumentException("The destination range is outside the buffer.", "offset");
		}
		long num = pos / chunkSize;
		while (count > 0)
		{
			if ((ulong)num >= (ulong)chunks.Length)
			{
				throw new EndOfStreamException("Read extends past the chunked file extent.");
			}
			int num2 = (int)(pos % chunkSize);
			int num3 = Math.Min(chunkSize - num2, count);
			reader.Read((long)chunks[num++] * (long)chunkSize + num2, buf, offset, num3);
			pos += num3;
			offset += num3;
			count -= num3;
		}
	}
}
