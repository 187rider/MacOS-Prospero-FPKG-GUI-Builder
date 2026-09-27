namespace LibProsperoPkg.PFS.Compression;

/// <summary>One decoded normal <c>CblockInfo</c> span.</summary>
public readonly record struct ProsperoNapsSpan(int Index, int CblockInfoIndex, long CompressedOffset, int CompressedLength, int FirstChunkCompressedLength, long StoredOffset, long UncompressedOffset, int UncompressedLength, uint TweakIndex, byte KeyTableIndex, byte Even, byte Odd, byte KdePredictor, byte ShuffleIndex);
