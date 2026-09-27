using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PFS;

/// <summary>
/// Signed 64-bit inode
/// </summary>
public class DinodeS64 : Inode
{
	public const long SizeOf = 784L;

	public block_sig64[] db;

	public block_sig64[] ib;

	public override int StartBlock => (int)db[0].block;

	public override IList<int> DirectBlocks => db.Select((block_sig64 d) => (int)d.block).ToList();

	public override IList<int> IndirectBlocks => ib.Select((block_sig64 d) => (int)d.block).ToList();

	public DinodeS64()
	{
		db = new block_sig64[12];
		ib = new block_sig64[5];
		for (int i = 0; i < 12; i++)
		{
			db[i].sig = new byte[32];
			if (i < 5)
			{
				ib[i].sig = new byte[32];
			}
		}
	}

	public override void SetDirectBlock(int idx, int block)
	{
		db[idx].block = block;
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
		s.WriteLE(0);
		block_sig64[] array = db;
		for (int i = 0; i < array.Length; i++)
		{
			block_sig64 block_sig65 = array[i];
			s.Write(block_sig65.sig, 0, 32);
			s.WriteLE(block_sig65.block);
		}
		array = ib;
		for (int i = 0; i < array.Length; i++)
		{
			block_sig64 block_sig66 = array[i];
			s.Write(block_sig66.sig, 0, 32);
			s.WriteLE(block_sig66.block);
		}
	}

	public static DinodeS64 ReadFromStream(Stream s)
	{
		DinodeS64 dinodeS = new DinodeS64
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
			Blocks = (uint)s.ReadInt64LE(),
			db = new block_sig64[12],
			ib = new block_sig64[5]
		};
		for (int i = 0; i < 12; i++)
		{
			dinodeS.db[i] = new block_sig64
			{
				sig = s.ReadBytes(32),
				block = s.ReadInt64LE()
			};
		}
		for (int j = 0; j < 5; j++)
		{
			dinodeS.ib[j] = new block_sig64
			{
				sig = s.ReadBytes(32),
				block = s.ReadInt64LE()
			};
		}
		return dinodeS;
	}
}
