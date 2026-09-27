using System.Collections.Generic;
using System.IO;

namespace LibProsperoPkg.PFS;

/// <summary>
/// Base class for inodes. Inodes can be signed or unsigned, and 32 or 64 bit.
/// </summary>
public abstract class Inode
{
	public const InodeMode RXOnly = InodeMode.o_read | InodeMode.o_execute | InodeMode.g_read | InodeMode.g_execute | InodeMode.u_read | InodeMode.u_execute;

	/// <summary>
	/// The index of this inode in the block of inodes.
	/// </summary>
	public uint Number;

	/// <summary>
	/// Default is 555 octal.
	/// </summary>
	public InodeMode Mode = InodeMode.o_read | InodeMode.o_execute | InodeMode.g_read | InodeMode.g_execute | InodeMode.u_read | InodeMode.u_execute;

	/// <summary>
	/// Number of links to this file in the filesystem.
	/// 1 for regular files, 1 + 1 for every subdirectory for dirs.
	/// </summary>
	public ushort Nlink;

	public InodeFlags Flags;

	public long Size;

	public long SizeCompressed;

	public long Time1_sec;

	public long Time2_sec;

	public long Time3_sec;

	public long Time4_sec;

	public uint Time1_nsec;

	public uint Time2_nsec;

	public uint Time3_nsec;

	public uint Time4_nsec;

	public uint Uid;

	public uint Gid;

	public ulong Unk1;

	public ulong Unk2;

	public uint Blocks;

	public abstract int StartBlock { get; }

	public abstract IList<int> DirectBlocks { get; }

	public abstract IList<int> IndirectBlocks { get; }

	public Inode()
	{
		SetTime(0L);
	}

	public abstract void SetDirectBlock(int idx, int block);

	public abstract void WriteToStream(Stream s);

	public Inode SetTime(long time)
	{
		Time1_sec = time;
		Time2_sec = time;
		Time3_sec = time;
		Time4_sec = time;
		return this;
	}
}
