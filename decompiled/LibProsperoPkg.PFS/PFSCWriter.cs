using System.IO;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PFS;

/// <summary>
/// Writes a PFSC header to to a stream. Doesn't actually do compression or anything interesting.
/// </summary>
internal class PFSCWriter
{
	private const int BlockSize = 65536;

	private long num_blocks;

	public readonly long HeaderSize;

	public PFSCWriter(long size)
	{
		num_blocks = (size + 65536 - 1) / 65536;
		long num = (8 + num_blocks * 8 - 64512 + 65535) / 65536;
		HeaderSize = 65536 + ((num > 0) ? (65536 * num) : 0);
	}

	public void WritePFSCHeader(Stream s)
	{
		long position = s.Position;
		s.WriteInt32BE(1346786115);
		s.WriteLE(0);
		s.WriteLE(6);
		s.WriteLE(65536);
		s.WriteLE(65536L);
		s.WriteLE(1024L);
		s.WriteLE(HeaderSize);
		s.WriteLE(num_blocks * 65536);
		s.Position = position + 1024;
		for (long num = 0L; num <= num_blocks; num++)
		{
			s.WriteLE(HeaderSize + num * 65536);
		}
		s.Position = position + HeaderSize;
	}
}
