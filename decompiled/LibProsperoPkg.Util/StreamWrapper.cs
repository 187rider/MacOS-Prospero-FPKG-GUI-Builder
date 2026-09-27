using System;
using System.IO;

namespace LibProsperoPkg.Util;

public class StreamWrapper : Stream
{
	private IMemoryReader reader;

	private long position;

	public override bool CanRead => true;

	public override bool CanSeek => true;

	public override bool CanWrite => false;

	public override long Length { get; }

	public override long Position
	{
		get
		{
			return position;
		}
		set
		{
			ArgumentOutOfRangeException.ThrowIfNegative(value, "value");
			position = value;
		}
	}

	public StreamWrapper(IMemoryReader r, long size)
	{
		reader = r;
		Length = size;
	}

	public override void Flush()
	{
	}

	public override int Read(byte[] buffer, int offset, int count)
	{
		ArgumentNullException.ThrowIfNull(buffer, "buffer");
		ArgumentOutOfRangeException.ThrowIfNegative(offset, "offset");
		ArgumentOutOfRangeException.ThrowIfNegative(count, "count");
		if (offset > buffer.Length - count)
		{
			throw new ArgumentException("The destination range is outside the buffer.", "offset");
		}
		if (position >= Length)
		{
			return 0;
		}
		int num = checked((int)Math.Min(count, Length - position));
		reader.Read(position, buffer, offset, num);
		position += num;
		return num;
	}

	public override long Seek(long offset, SeekOrigin origin)
	{
		switch (origin)
		{
		case SeekOrigin.Begin:
			position = offset;
			break;
		case SeekOrigin.Current:
			position += offset;
			break;
		case SeekOrigin.End:
			position = Length + offset;
			break;
		}
		if (position < 0)
		{
			throw new IOException("Cannot seek before the beginning of the stream.");
		}
		return position;
	}

	public override void SetLength(long value)
	{
		throw new NotSupportedException("StreamWrapper is read-only.");
	}

	public override void Write(byte[] buffer, int offset, int count)
	{
		throw new NotSupportedException("StreamWrapper is read-only.");
	}
}
