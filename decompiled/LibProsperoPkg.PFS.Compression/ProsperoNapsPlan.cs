using System.Collections.Generic;

namespace LibProsperoPkg.PFS.Compression;

/// <summary>
/// Fully resolved view of the NAPS boundary tables. It is the managed equivalent of the span,
/// ublock and file-view construction performed by <c>ric.exe</c> before image verification.
/// </summary>
public sealed class ProsperoNapsPlan
{
	public required IReadOnlyList<ProsperoNapsSpan> Spans { get; init; }

	public required IReadOnlyList<ProsperoNapsLogicalFile> Files { get; init; }

	public required long UncompressedSize { get; init; }
}
