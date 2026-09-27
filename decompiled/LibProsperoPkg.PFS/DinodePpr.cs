using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PFS;

/// <summary>
/// Publisher PPR inode. The common 0x60-byte prefix matches an unsigned inode, but the 0x48-byte
/// tail starts with an absolute logical byte offset instead of <c>blocks + db[] + ib[]</c>.
/// </summary>
public sealed class DinodePpr : Inode
{
	public const long SizeOf = 168L;

	public long DataOffset;

	public byte[] Tail = new byte[64];

	public override int StartBlock
	{
		get
		{
			checked
			{
				return (int)unchecked(DataOffset / 65536);
			}
		}
	}

	public override IList<int> DirectBlocks
	{
		get
		{
			int num = 1;
			List<int> list = new List<int>(num);
			CollectionsMarshal.SetCount(list, num);
			CollectionsMarshal.AsSpan(list)[0] = StartBlock;
			return list;
		}
	}

	public override IList<int> IndirectBlocks => new List<int>();

	public override void SetDirectBlock(int idx, int block)
	{
		if (idx != 0)
		{
			throw new ArgumentOutOfRangeException("idx");
		}
		DataOffset = (long)block * 65536L;
	}

	public override void WriteToStream(Stream s)
	{
		s.WriteLE((ushort)Mode);
		s.WriteLE(Nlink);
		s.WriteLE((uint)Flags);
		s.WriteLE(Size);
		s.WriteLE(SizeCompressed);
		s.WriteLE(Time1_sec);
		s.WriteLE(Time2_sec);
		s.WriteLE(Time3_sec);
		s.WriteLE(Time4_sec);
		s.WriteLE(Time1_nsec);
		s.WriteLE(Time2_nsec);
		s.WriteLE(Time3_nsec);
		s.WriteLE(Time4_nsec);
		s.WriteLE(Uid);
		s.WriteLE(Gid);
		s.WriteLE(Unk1);
		s.WriteLE(Unk2);
		s.WriteLE(DataOffset);
		s.Write(Tail, 0, Tail.Length);
	}

	public static DinodePpr ReadFromStream(Stream s)
	{
		return new DinodePpr
		{
			Mode = (InodeMode)s.ReadUInt16LE(),
			Nlink = s.ReadUInt16LE(),
			Flags = (InodeFlags)s.ReadUInt32LE(),
			Size = s.ReadInt64LE(),
			SizeCompressed = s.ReadInt64LE(),
			Time1_sec = s.ReadInt64LE(),
			Time2_sec = s.ReadInt64LE(),
			Time3_sec = s.ReadInt64LE(),
			Time4_sec = s.ReadInt64LE(),
			Time1_nsec = s.ReadUInt32LE(),
			Time2_nsec = s.ReadUInt32LE(),
			Time3_nsec = s.ReadUInt32LE(),
			Time4_nsec = s.ReadUInt32LE(),
			Uid = s.ReadUInt32LE(),
			Gid = s.ReadUInt32LE(),
			Unk1 = s.ReadUInt64LE(),
			Unk2 = s.ReadUInt64LE(),
			DataOffset = s.ReadInt64LE(),
			Tail = s.ReadBytes(64)
		};
	}
}
