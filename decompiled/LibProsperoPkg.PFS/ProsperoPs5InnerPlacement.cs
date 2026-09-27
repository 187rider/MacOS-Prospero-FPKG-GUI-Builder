using System;
using System.Collections.Generic;

namespace LibProsperoPkg.PFS;

/// <summary>One file's on-disk + logical placement in the assembled inner image (afid order).</summary>
public readonly struct ProsperoPs5InnerPlacement
{
	/// <summary>AFID slot used by the inode/FIDX tables.</summary>
	public uint Afid { get; init; }

	/// <summary>On-disk (compressed-image) byte offset.</summary>
	public long OnDiskOffset { get; init; }

	/// <summary>Uncompressed logical byte offset in the mount.</summary>
	public long LogicalOffset { get; init; }

	/// <summary>On-disk byte size (raw size when stored raw, else the Kraken payload size).</summary>
	public long OnDiskSize { get; init; }

	/// <summary>Uncompressed byte size.</summary>
	public long UncompressedSize { get; init; }

	/// <summary>
	/// Original uncompressed bytes. NAPS <c>ihsh</c> and <c>rhsh</c> are computed from this input,
	/// not from the Kraken stream stored in <c>pfs_image.dat</c>.
	/// </summary>
	public ReadOnlyMemory<byte> PlainData { get; init; }

	/// <summary>True when stored raw (block-split), false when Kraken-compressed.</summary>
	public bool StoreRaw { get; init; }

	/// <summary>Per-256 KiB Kraken/storage blocks when this file is stored compressed.</summary>
	public IReadOnlyList<ProsperoInnerDataBlockChunk>? CompressionBlocks { get; init; }

	/// <summary>Per-256 KiB integrity hashes computed during compression/streaming.</summary>
	public IReadOnlyList<ProsperoInnerBlockIntegrity>? BlockIntegrities { get; init; }
}
