using System;

namespace LibProsperoPkg.Util;

/// <summary>
/// Non-thread safe buffered reader
/// </summary>
internal class BufferedMemoryReader : IMemoryReader, IDisposable
{
	private long bufferStart;

	private byte[] buffer;

	private IMemoryReader reader;

	public BufferedMemoryReader(IMemoryReader mr, int bufferSize)
	{
		buffer = new byte[bufferSize];
		reader = mr;
		bufferStart = -buffer.Length - 1;
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
		while (count > 0)
		{
			if (bufferStart > pos || pos >= bufferStart + buffer.Length)
			{
				bufferStart = pos;
				reader.Read(pos, buffer, 0, buffer.Length);
			}
			int num = (int)(pos - bufferStart);
			int num2 = Math.Min(buffer.Length - num, count);
			Buffer.BlockCopy(buffer, num, buf, offset, num2);
			pos += num2;
			offset += num2;
			count -= num2;
		}
	}
}
