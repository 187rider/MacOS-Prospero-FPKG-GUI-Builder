using System.Collections.Generic;
using System.IO;

namespace LibProsperoPkg.PKG;

public class MetasEntry : Entry
{
	public List<MetaEntry> Metas = new List<MetaEntry>();

	public override EntryId Id => EntryId.METAS;

	public override uint Length => (uint)(Metas.Count * 32);

	public override string Name => null;

	public override void Write(Stream s)
	{
		foreach (MetaEntry meta in Metas)
		{
			meta.Write(s);
		}
	}
}
