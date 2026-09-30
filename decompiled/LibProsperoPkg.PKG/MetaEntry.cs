using System.IO;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PKG;

public class MetaEntry
{
	public EntryId id;

	public uint NameTableOffset;

	public uint Flags1;

	public uint Flags2;

	public uint DataOffset;

	public uint DataSize;

	public uint KeyIndex => (Flags2 & 0xF000) >> 12;

	public bool Encrypted => (Flags1 & 0x80000000u) != 0;

	public void Write(Stream s)
	{
		s.WriteUInt32BE((uint)id);
		s.WriteUInt32BE(NameTableOffset);
		s.WriteUInt32BE(Flags1);
		s.WriteUInt32BE(Flags2);
		s.WriteUInt32BE(DataOffset);
		s.WriteUInt32BE(DataSize);
		s.Position += 8L;
	}

	public static MetaEntry Read(Stream s)
	{
		MetaEntry result = new MetaEntry
		{
			id = (EntryId)s.ReadUInt32BE(),
			NameTableOffset = s.ReadUInt32BE(),
			Flags1 = s.ReadUInt32BE(),
			Flags2 = s.ReadUInt32BE(),
			DataOffset = s.ReadUInt32BE(),
			DataSize = s.ReadUInt32BE()
		};
		s.Position += 8L;
		return result;
	}

	public byte[] GetBytes()
	{
		byte[] array = new byte[32];
		using MemoryStream s = new MemoryStream(array);
		Write(s);
		return array;
	}
}
