using System;
using System.Collections.Generic;

namespace LibProsperoPkg.PKG;

public sealed class ProsperoPkgWriterOptions
{
	public string ContentId { get; init; } = "";

	public uint Flags { get; init; }

	public uint DrmType { get; init; }

	public uint ContentType { get; init; }

	public ushort ScEntryCount { get; init; }

	public IReadOnlyList<ProsperoPkgWriterEntry> Entries { get; init; } = Array.Empty<ProsperoPkgWriterEntry>();
}
