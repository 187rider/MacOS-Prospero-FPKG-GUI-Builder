using System.Collections.Generic;

namespace LibProsperoPkg.PFS;

/// <summary>
/// Internal byte-level probe produced by the real indirect-map serializer. The companion
/// regression tool uses it to exercise a selected inode level without materializing file data.
/// </summary>
internal sealed class ProsperoOuterAddressingSerializationProbe
{
	public required int InodeLevel { get; init; }

	public required int RootBlockIndex { get; init; }

	public required int FirstDataOffset { get; init; }

	public required int DataBlockCount { get; init; }

	public required byte[] RootHash { get; init; }

	public required IReadOnlyDictionary<int, byte[]> MetadataBlocks { get; init; }
}
