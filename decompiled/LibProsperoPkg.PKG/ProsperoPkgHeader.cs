namespace LibProsperoPkg.PKG;

public sealed class ProsperoPkgHeader
{
	public required byte[] Magic { get; init; }

	public uint Flags { get; init; }

	public uint EntryCount { get; init; }

	public ushort ScEntryCount { get; init; }

	public uint EntryTableOffset { get; init; }

	public ulong BodyOffset { get; init; }

	public ulong BodySize { get; init; }

	public string ContentId { get; init; } = "";

	public uint DrmType { get; init; }

	public uint ContentType { get; init; }
}
