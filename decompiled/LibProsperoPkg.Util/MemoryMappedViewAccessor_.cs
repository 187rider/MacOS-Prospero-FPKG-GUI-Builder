using System;
using System.IO.MemoryMappedFiles;

namespace LibProsperoPkg.Util;

public class MemoryMappedViewAccessor_ : IMemoryAccessor, IMemoryReader, IDisposable
{
	private MemoryMappedViewAccessor _va;

	private bool shouldDispose;

	public MemoryMappedViewAccessor_(MemoryMappedViewAccessor v, bool shouldDispose = false)
	{
		_va = v;
		this.shouldDispose = shouldDispose;
	}

	public void Dispose()
	{
		if (shouldDispose)
		{
			_va.Dispose();
		}
	}

	public void Read<T>(long pos, out T value) where T : struct
	{
		_va.Read<T>(pos, out value);
	}

	public void ReadArray<T>(long pos, T[] value, int offset, int count) where T : struct
	{
		_va.ReadArray(pos, value, offset, count);
	}

	public void Read(long pos, byte[] buf, int offset, int count)
	{
		_va.ReadArray(pos, buf, offset, count);
	}
}
