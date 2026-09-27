namespace LibProsperoPkg.PFS;

/// <summary>Compression geometry for one 256 KiB logical block of an inner payload file.</summary>
public readonly record struct ProsperoInnerDataBlockChunk(int CompressedSize, int UncompressedSize, bool IsStored, bool IsMultiChunk, int FirstChunkCompressedSize, int BoundaryFlags = 0);
