using System.IO;
using System.Text;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PFS;

/// <summary>
/// Represents a PFS dirent. Directories are stored ondisk as blocks of dirents.
/// </summary>
public class PfsDirent
{
	public static int MaxSize = 280;

	public uint InodeNumber;

	public DirentType Type;

	public int NameLength;

	public int EntSize;

	private string name;

	public string Name
	{
		get
		{
			return name;
		}
		set
		{
			name = value;
			NameLength = name.Length;
			EntSize = CalculateEntSize();
		}
	}

	public int CalculateEntSize()
	{
		int num = NameLength + 17;
		if (num % 8 != 0)
		{
			num += 8 - num % 8;
		}
		return num;
	}

	public void WriteToStream(Stream s)
	{
		long position = s.Position;
		s.WriteLE(InodeNumber);
		s.WriteLE((int)Type);
		s.WriteLE(NameLength);
		s.WriteLE(EntSize);
		s.Write(Encoding.ASCII.GetBytes(Name), 0, NameLength);
		int num = (int)(EntSize - (s.Position - position));
		s.Write(new byte[num], 0, num);
	}

	public static PfsDirent ReadFromStream(Stream s)
	{
		long position = s.Position;
		PfsDirent pfsDirent = new PfsDirent
		{
			InodeNumber = s.ReadUInt32LE(),
			Type = (DirentType)s.ReadInt32LE(),
			NameLength = s.ReadInt32LE(),
			EntSize = s.ReadInt32LE()
		};
		pfsDirent.name = s.ReadASCIINullTerminated(pfsDirent.NameLength);
		s.Position = position + pfsDirent.EntSize;
		return pfsDirent;
	}
}
