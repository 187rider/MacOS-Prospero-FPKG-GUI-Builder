using System;
using System.IO;

namespace LibProsperoPkg.PKG;

public class FileEntry : Entry
{
	private Action<Stream> Writer;

	public override EntryId Id { get; }

	public override string Name { get; }

	public override uint Length { get; }

	public FileEntry(EntryId id, Action<Stream> writer, uint length)
	{
		Id = id;
		Name = EntryNames.IdToName[id];
		Length = length;
		Writer = writer;
	}

	public FileEntry(EntryId id, string path)
		: this(id, (Stream s) =>
		{
			using FileStream fileStream = File.OpenRead(path);
			fileStream.CopyTo(s);
		}, (uint)new FileInfo(path).Length)
	{
	}

	public override void Write(Stream s)
	{
		Writer(s);
	}
}
