namespace LibProsperoPkg.PFS;

/// <summary>One compressed-metadata 256KiB block's chunk sizes (for naps generation).</summary>
public readonly record struct ProsperoInnerMetaBlockChunk(int CompressedSize, int UncompressedSize, bool IsMultiChunk, int FirstChunkCompressedSize, int BoundaryFlags = 0);
