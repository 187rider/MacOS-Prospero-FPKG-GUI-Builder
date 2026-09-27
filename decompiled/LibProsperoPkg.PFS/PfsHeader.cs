using System.IO;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PFS;

/// <summary>
/// Represents a PFS image suberblock.
/// </summary>
public class PfsHeader
{
	/// <summary>PFS superblock version for PS5 images.</summary>
	public const long VersionPs5 = 2L;

	public long Version = 2L;

	public long Magic = 20130315L;

	public long Id;

	public byte Fmode;

	public byte Clean;

	public byte ReadOnly;

	public byte Rsv;

	public PfsMode Mode = PfsMode.UnknownFlagAlwaysSet;

	public ushort Unk1;

	public uint BlockSize = 65536u;

	public uint NBackup;

	/// <summary>
	/// This is always 1 for some reason.
	/// </summary>
	public long NBlock = 1L;

	public long DinodeCount;

	public long Ndblock;

	public long DinodeBlockCount;

	public DinodeS64 InodeBlockSig = new DinodeS64
	{
		Mode = (InodeMode)0,
		Nlink = 1,
		Flags = InodeFlags.@readonly,
		Size = 65536L,
		SizeCompressed = 65536L,
		Blocks = 1u
	};

	public int UnknownIndex;

	public byte[] Seed;

	public void WriteToStream(Stream s)
	{
		long position = s.Position;
		s.WriteInt64LE(Version);
		s.WriteInt64LE(Magic);
		s.WriteInt64LE(Id);
		s.WriteByte(Fmode);
		s.WriteByte(Clean);
		s.WriteByte(ReadOnly);
		s.WriteByte(Rsv);
		s.WriteUInt16LE((ushort)Mode);
		s.WriteUInt16LE(Unk1);
		s.WriteUInt32LE(BlockSize);
		s.WriteUInt32LE(NBackup);
		s.WriteInt64LE(NBlock);
		s.WriteInt64LE(DinodeCount);
		s.WriteInt64LE(Ndblock);
		s.WriteInt64LE(DinodeBlockCount);
		s.WriteInt64LE(0L);
		InodeBlockSig.WriteToStream(s);
		if (Seed != null)
		{
			s.Position = position + 876;
			s.WriteInt32LE(UnknownIndex);
			s.Write(Seed, 0, Seed.Length);
		}
		else
		{
			s.Position = position + 872;
			s.WriteInt32LE(1);
		}
	}

	public static PfsHeader ReadFromStream(Stream s)
	{
		long position = s.Position;
		PfsHeader pfsHeader = new PfsHeader
		{
			Version = s.ReadInt64LE(),
			Magic = s.ReadInt64LE(),
			Id = s.ReadInt64LE(),
			Fmode = s.ReadUInt8(),
			Clean = s.ReadUInt8(),
			ReadOnly = s.ReadUInt8(),
			Rsv = s.ReadUInt8(),
			Mode = (PfsMode)s.ReadUInt16LE(),
			Unk1 = s.ReadUInt16LE(),
			BlockSize = s.ReadUInt32LE(),
			NBackup = s.ReadUInt32LE(),
			NBlock = s.ReadInt64LE(),
			DinodeCount = s.ReadInt64LE(),
			Ndblock = s.ReadInt64LE(),
			DinodeBlockCount = s.ReadInt64LE()
		};
		s.Position += 8L;
		pfsHeader.InodeBlockSig = DinodeS64.ReadFromStream(s);
		if (pfsHeader.Version != 2 || pfsHeader.Magic != 20130315)
		{
			throw new InvalidDataException($"Invalid PFS superblock version ({pfsHeader.Version}) or magic ({pfsHeader.Magic})");
		}
		s.Position = position + 880;
		pfsHeader.Seed = s.ReadBytes(16);
		return pfsHeader;
	}
}
