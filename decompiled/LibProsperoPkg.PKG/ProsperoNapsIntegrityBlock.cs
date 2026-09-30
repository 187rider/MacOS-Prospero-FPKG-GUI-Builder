using System;

namespace LibProsperoPkg.PKG;

public readonly record struct ProsperoNapsIntegrityBlock(int Index, ulong CompressedOffset, uint StoredSize, uint PlainSize, bool IsHole, uint OwnerFlag, ulong Tail, long OnDiskOffset, uint OnDiskLength, ReadOnlyMemory<byte> Plaintext, byte[] Sha3Digest, ulong? Ihsh = null, ulong? Rhsh = null);
