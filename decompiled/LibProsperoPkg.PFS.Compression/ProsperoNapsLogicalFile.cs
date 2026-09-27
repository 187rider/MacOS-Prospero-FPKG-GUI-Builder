namespace LibProsperoPkg.PFS.Compression;

/// <summary>Logical file range described by the NAPS fidx boundary table.</summary>
public readonly record struct ProsperoNapsLogicalFile(int Index, byte Type, long UncompressedOffset, long Length);
