using System.Collections.Generic;

namespace LibProsperoPkg.PFS.Compression;

/// <summary>Raw ranges and diagnostics produced from an inner PFS layout.</summary>
public sealed class PprPfsReadOptimizationPlan
{
	public required IReadOnlyList<PprPfsRawRange> RawRanges { get; init; }

	public required long RawLogicalBytes { get; init; }

	public required int RawGroupCount { get; init; }

	public required int RawFileCount { get; init; }

	public required long MetadataPrefixBytes { get; init; }
}
