namespace LibProsperoPkg.PKG;

public sealed class ProsperoPkgEntry
{
	public ProsperoEntryId Id { get; init; }

	public uint RawId { get; init; }

	public uint NameTableOffset { get; init; }

	public uint Flags1 { get; init; }

	public uint Flags2 { get; init; }

	public uint DataOffset { get; init; }

	public uint DataSize { get; init; }

	public string? Name { get; set; }

	public uint KeyIndex => (Flags2 & 0xF000) >> 12;

	public bool Encrypted => (Flags1 & 0x80000000u) != 0;
}
