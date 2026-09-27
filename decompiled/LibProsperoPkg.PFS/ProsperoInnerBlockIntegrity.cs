namespace LibProsperoPkg.PFS;

/// <summary>Precomputed integrity values for one 256 KiB logical block of an inner payload file.</summary>
public readonly record struct ProsperoInnerBlockIntegrity(byte[] Sha3Digest, ulong Ihsh, ulong Rhsh);
