using System;

namespace LibProsperoPkg.PKG;

public sealed class ProsperoPkgWriterEntry
{
	public required uint Id { get; init; }

	public string Name { get; init; } = "";

	public byte[] Data { get; init; } = Array.Empty<byte>();

	public uint Flags1 { get; init; }

	public uint Flags2 { get; init; }
}
