using System.Collections.Generic;
using System.IO;

namespace LibProsperoPkg.PFS;

public class CollisionResolver
{
	private List<List<PfsDirent>> Entries;

	public int Size { get; }

	public CollisionResolver(List<List<PfsDirent>> ents)
	{
		Entries = ents;
		int num = 0;
		foreach (List<PfsDirent> ent in ents)
		{
			foreach (PfsDirent item in ent)
			{
				num += item.EntSize;
			}
			num += 24;
		}
		Size = num;
	}

	public void WriteToStream(Stream s)
	{
		foreach (List<PfsDirent> entry in Entries)
		{
			foreach (PfsDirent item in entry)
			{
				item.WriteToStream(s);
			}
			s.Position += 24L;
		}
	}
}
