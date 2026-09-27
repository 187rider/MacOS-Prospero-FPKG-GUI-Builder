namespace LibProsperoPkg.PFS.Compression;

/// <summary>A logical byte range that must be represented by raw PFSC table entries.</summary>
public readonly record struct PprPfsRawRange(long Offset, long Length);
