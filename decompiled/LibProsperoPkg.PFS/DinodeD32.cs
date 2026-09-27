using System.Collections.Generic;
using System.IO;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PFS;

/// <summary>
/// 32-bit unsigned inodes
/// </summary>
public class DinodeD32 : Inode
{
	public const long SizeOf = 168L;

	public int[] db = new int[12];

	public int[] ib = new int[5];

	public override int StartBlock => db[0];

	public override IList<int> DirectBlocks => db;

	public override IList<int> IndirectBlocks => ib;

	public override void SetDirectBlock(int idx, int block)
	{
		db[idx] = block;
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
		s.WriteLE(Blocks);
		int[] array = db;
		foreach (int i2 in array)
		{
			s.WriteLE(i2);
		}
		array = ib;
		foreach (int i3 in array)
		{
			s.WriteLE(i3);
		}
	}

	public static DinodeD32 ReadFromStream(Stream s)
	{
		DinodeD32 dinodeD = new DinodeD32
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
			Blocks = s.ReadUInt32LE()
		};
		for (int i = 0; i < 12; i++)
		{
			dinodeD.db[i] = s.ReadInt32LE();
		}
		for (int j = 0; j < 5; j++)
		{
			dinodeD.ib[j] = s.ReadInt32LE();
		}
		return dinodeD;
	}
}
