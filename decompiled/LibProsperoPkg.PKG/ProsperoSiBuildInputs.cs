using System;
using System.Collections.Generic;
using LibProsperoPkg.PFS;

namespace LibProsperoPkg.PKG;

internal sealed class ProsperoSiBuildInputs
{
	public required ProsperoPfsImageXmlOptions Xml { get; init; }

	public byte[]? PlayGoChunkDat { get; init; }

	public long InnerImageSize { get; init; }

	public ulong NapsLayoutSize { get; init; }

	public uint FihNapsFileCount { get; init; }

	public int SparseAfidCount { get; init; }

	public int EmptyFileCount { get; init; }

	public byte[]? NapsMeta18 { get; init; }

	public IProsperoNapsIntegrityProvider? NapsIntegrityProvider { get; init; }

	public byte[]? NapsPfsImageKey { get; init; }

	public byte[]? NapsPfsImageSeed { get; init; }

	public bool IncludePfsImageXml { get; init; } = true;

	public IReadOnlyList<(string Path, long Size)> ContentFiles { get; init; } = Array.Empty<(string, long)>();

	public ProsperoPs5InnerImageResult? InnerImage { get; init; }

	public string? TemporaryInnerImagePath { get; init; }

	public long NestedMetaBaseBlocks { get; init; }

	public uint ContentVersionHigh { get; init; }

	public int AppFileCount { get; init; }

	public int OuterSuperblockIndex { get; init; } = -1;
	public byte[]? OuterImageDigests { get; init; }
}
