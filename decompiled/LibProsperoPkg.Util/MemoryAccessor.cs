using System;

namespace LibProsperoPkg.Util;

internal class MemoryAccessor : IMemoryAccessor, IMemoryReader, IDisposable
{
	private IMemoryReader reader;

	private long offset;

	public MemoryAccessor(IMemoryReader mr, long offset = 0L)
	{
		if (mr is MemoryAccessor memoryAccessor)
		{
			this.offset = offset + memoryAccessor.offset;
			reader = memoryAccessor.reader;
		}
		else
		{
			this.offset = offset;
			reader = mr;
		}
	}

	public void Dispose()
	{
	}

	public void Read<T>(long pos, out T value) where T : struct
	{
		reader.Read<T>(pos + offset, out value);
	}

	public void Read(long pos, byte[] buf, int offset, int count)
	{
		reader.Read(pos + this.offset, buf, offset, count);
	}

	public void ReadArray<T>(long pos, T[] value, int offset, int count) where T : struct
	{
		reader.ReadArray(pos + this.offset, value, offset, count);
	}
}
