using System;
using System.Collections.Generic;
using System.IO;

namespace LibProsperoPkg.PFS;

/// <summary>The assembled inner image plus the intermediate model (for verification/diagnostics).</summary>
public sealed class ProsperoPs5InnerImageResult
{
	/// <summary>
	/// The on-disk inner <c>pfs_image.dat</c> bytes (data-first, per-file compressed).
	/// Empty for a result produced by a file-backed assembler overload.
	/// </summary>
	public required byte[] Image { get; init; }

	/// <summary>Physical image path for a file-backed result; otherwise <see langword="null" />.</summary>
	public string? ImagePath { get; init; }

	/// <summary>Exact block-aligned physical <c>pfs_image.dat</c> length.</summary>
	public required long ImageLength { get; init; }

	/// <summary>The uncompressed metadata-region plaintext (the block that is Kraken-compressed into the image tail).</summary>
	public required byte[] MetadataPlaintext { get; init; }

	/// <summary>The computed metadata nodes (inode order), for inspection.</summary>
	public required IReadOnlyList<ProsperoPs5MetaNode> Nodes { get; init; }

	/// <summary>The inner mount logical block count (superblock <c>Ndblock</c>).</summary>
	public required long Ndblock { get; init; }

	/// <summary>The per-file uncompressed logical start offsets, in afid order (the naps fidx values).</summary>
	public required IReadOnlyList<long> AfidLogicalOffsets { get; init; }

	/// <summary>
	/// Unreferenced FIDX boundaries emitted for zero-length files. Their inodes point at the common
	/// terminal FIDX entry, matching the publisher's empty-file representation.
	/// </summary>
	public IReadOnlyList<long> EmptyFileLogicalOffsets { get; init; } = Array.Empty<long>();

	/// <summary>Per-file on-disk/logical placement (afid order), for naps generation.</summary>
	public IReadOnlyList<ProsperoPs5InnerPlacement> Placements { get; init; } = Array.Empty<ProsperoPs5InnerPlacement>();

	/// <summary>
	/// Explicit empty AFID slots. Each slot covers one logical 256-KiB zero extent and is represented
	/// as <c>-1</c> in <c>afid_to_ino_table</c>.
	/// </summary>
	public IReadOnlyList<ProsperoPs5SparseAfidHole> SparseAfidHoles { get; init; } = Array.Empty<ProsperoPs5SparseAfidHole>();

	/// <summary>On-disk byte offset of the block-info table (block 75).</summary>
	public long BlockInfoOnDiskOffset { get; init; }

	/// <summary>On-disk byte offset of the Kraken-compressed metadata region.</summary>
	public long MetadataOnDiskOffset { get; init; }

	/// <summary>The Kraken-compressed metadata bytes (concatenated 256K blocks).</summary>
	public byte[] CompressedMetadata { get; init; } = Array.Empty<byte>();

	/// <summary>
	/// Per-256KiB-block chunk table of the compressed metadata region, captured once during assembly so
	/// the naps generator can read the chunk sizes WITHOUT Kraken-packing the metadata a second time.
	/// Empty when the metadata is stored raw (no compression).
	/// </summary>
	public IReadOnlyList<ProsperoInnerMetaBlockChunk> MetadataBlocks { get; init; } = Array.Empty<ProsperoInnerMetaBlockChunk>();

	/// <summary>Logical end of the packed data files (first fidx boundary after the last file).</summary>
	public long DataEndLogical { get; init; }

	/// <summary>Logical base of the metadata region in the mount (= Ndblock*64K - MetadataPlaintext.Length).</summary>
	public long MetaBaseLogical { get; init; }

	/// <summary>Opens the physical image without materializing it when this is a file-backed result.</summary>
	public Stream OpenImage()
	{
		if (ImagePath != null && File.Exists(ImagePath))
		{
			return new FileStream(ImagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.RandomAccess);
		}
		return new MemoryStream(Image ?? Array.Empty<byte>(), writable: false);
	}
}
