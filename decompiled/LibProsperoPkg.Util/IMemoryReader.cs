using System;

namespace LibProsperoPkg.Util;

public interface IMemoryReader : IDisposable
{
	void Read(long pos, byte[] buf, int offset, int count);
}
