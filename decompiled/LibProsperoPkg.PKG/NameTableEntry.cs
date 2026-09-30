using System.Collections.Generic;
using System.IO;
using System.Text;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PKG;

public class NameTableEntry : Entry
{
	private int length = 1;

	private Dictionary<string, int> Names = new Dictionary<string, int> { { "", 0 } };

	private List<string> nameList = new List<string> { "" };

	public override EntryId Id => EntryId.ENTRY_NAMES;

	public override string Name => null;

	public override uint Length => (uint)length;

	public NameTableEntry()
	{
	}

	public NameTableEntry(List<string> names)
	{
		int num = 0;
		Names = new Dictionary<string, int>();
		nameList = names;
		foreach (string name in names)
		{
			Names.Add(name, num);
			num += name.Length + 1;
		}
	}

	public uint GetOffset(string name)
	{
		if (name == null || name == "")
		{
			return 0u;
		}
		if (!Names.ContainsKey(name))
		{
			nameList.Add(name);
			Names[name] = length;
			length += name.Length + 1;
		}
		return (uint)Names[name];
	}

	public string GetName(uint offset)
	{
		int num = 0;
		foreach (string name in nameList)
		{
			if (num == offset)
			{
				return name;
			}
			num += name.Length + 1;
		}
		return null;
	}

	public override void Write(Stream s)
	{
		foreach (string name in nameList)
		{
			byte[] bytes = Encoding.ASCII.GetBytes(name);
			s.Write(bytes, 0, bytes.Length);
			s.WriteByte(0);
		}
	}

	public static NameTableEntry Read(MetaEntry e, Stream pkg)
	{
		int i = 0;
		List<string> list = new List<string>();
		pkg.Position = e.DataOffset;
		string text;
		for (; i < e.DataSize; i += text.Length + 1)
		{
			text = pkg.ReadASCIINullTerminated((int)e.DataSize);
			list.Add(text);
		}
		return new NameTableEntry(list)
		{
			meta = e
		};
	}
}
