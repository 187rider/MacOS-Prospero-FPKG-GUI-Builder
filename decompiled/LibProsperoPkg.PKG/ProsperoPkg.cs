using System;
using System.Collections.Generic;

namespace LibProsperoPkg.PKG;

public sealed class ProsperoPkg
{
	public required ProsperoPkgType Type { get; init; }

	public ProsperoPkgHeader? Header { get; init; }

	public IReadOnlyList<ProsperoPkgEntry> Entries { get; init; } = Array.Empty<ProsperoPkgEntry>();

	public ProsperoFihHeader? Fih { get; init; }
}
