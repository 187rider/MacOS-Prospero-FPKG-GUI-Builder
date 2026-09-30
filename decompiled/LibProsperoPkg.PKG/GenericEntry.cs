using System.IO;

namespace LibProsperoPkg.PKG;

public class GenericEntry : Entry
{
	public byte[] FileData;

	public override EntryId Id { get; }

	public override string Name { get; }

	public override uint Length => (uint)(FileData?.Length ?? 0);

	public GenericEntry(EntryId id, string name = null)
	{
		Id = id;
		Name = name;
	}

	public override void Write(Stream s)
	{
		s.Write(FileData, 0, FileData.Length);
	}
}
