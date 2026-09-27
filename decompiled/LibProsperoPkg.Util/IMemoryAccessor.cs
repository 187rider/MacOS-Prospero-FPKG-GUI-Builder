using System;

namespace LibProsperoPkg.Util;

public interface IMemoryAccessor : IMemoryReader, IDisposable
{
	void Read<T>(long pos, out T value) where T : struct;

	void ReadArray<T>(long pos, T[] value, int offset, int count) where T : struct;
}
