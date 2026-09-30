using System;
using System.Collections.Generic;
using LibProsperoPkg.PFS;

namespace LibProsperoPkg.PKG;

public sealed class NapsFilePlacement
{
	public required long OnDiskOffset { get; init; }

	public required long LogicalOffset { get; init; }

	public required long OnDiskSize { get; init; }

	public required long UncompressedSize { get; init; }

	public required bool StoreRaw { get; init; }

	public byte CompressedKde { get; init; }

	public IReadOnlyList<ProsperoInnerDataBlockChunk> CompressionBlocks { get; init; } = Array.Empty<ProsperoInnerDataBlockChunk>();
}
