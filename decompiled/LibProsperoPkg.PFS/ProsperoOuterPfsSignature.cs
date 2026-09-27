using System;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PFS;

/// <summary>
/// PS5 outer-PFS signing primitives: the plain SHA3-256 per-block/dinode hash,
/// the superblock ICV, and the AES-XTS "signed block" sector flag. See the file header for the provenance.
/// </summary>
public static class ProsperoOuterPfsSignature
{
	/// <summary>Length of a 32-byte SHA3-256 hash as stored in dinodes and the superblock.</summary>
	public const int HashLength = 32;

	/// <summary>
	/// AES-XTS sector domain-separation flag OR'd into a PS5 signed / metadata block's sector number
	/// (bit 47). Plain file-data blocks use sector = blockIndex; signed blocks use
	/// <c>SignedBlockTweakFlag | blockIndex</c>.
	/// </summary>
	public const ulong SignedBlockTweakFlag = 140737488355328uL;

	/// <summary>Byte length of the superblock region covered by the ICV hash.</summary>
	public const int SuperblockIcvCoverage = 1440;

	/// <summary>Offset of the 32-byte ICV field within the superblock.</summary>
	public const int SuperblockIcvOffset = 896;

	/// <summary>Offset of the super-root inode hash (SHA3 of the inode-table block) in the superblock.</summary>
	public const int SuperblockRootHashOffset = 184;

	/// <summary>Offset of the super-root inode's block index (u32 LE) in the superblock.</summary>
	public const int SuperblockRootBlockIndexOffset = 216;

	/// <summary>
	/// The per-block / dinode integrity hash: plain SHA3-256 over the whole plaintext block.
	/// </summary>
	public static byte[] ComputeBlockHash(ReadOnlySpan<byte> plaintextBlock)
	{
		return ProsperoSha3.HashData(plaintextBlock);
	}

	public static void ComputeBlockHash(ReadOnlySpan<byte> plaintextBlock, Span<byte> destination)
	{
		ProsperoSha3.HashData(plaintextBlock, destination);
	}

	/// <summary>
	/// Computes the AES-XTS sector number for a block: <paramref name="blockIndex" /> for plain
	/// file-data blocks, or <c>SignedBlockTweakFlag | blockIndex</c> for signed/metadata blocks.
	/// </summary>
	public static ulong BlockSector(int blockIndex, bool signed)
	{
		if (blockIndex < 0)
		{
			throw new ArgumentOutOfRangeException("blockIndex");
		}
		ulong num = (uint)blockIndex;
		if (signed)
		{
			num |= 0x800000000000L;
		}
		return num;
	}

	/// <summary>
	/// Computes the superblock ICV: SHA3-256 over <c>superblock[0 .. 0x5a0]</c> with the 32-byte ICV
	/// field (at <see cref="F:LibProsperoPkg.PFS.ProsperoOuterPfsSignature.SuperblockIcvOffset" />) treated as zero. Does not mutate the input.
	/// </summary>
	public static byte[] ComputeSuperblockIcv(ReadOnlySpan<byte> superblock)
	{
		if (superblock.Length < 1440)
		{
			throw new ArgumentException($"Superblock must be at least 0x{1440:x} bytes to compute the ICV.", "superblock");
		}
		Span<byte> span = stackalloc byte[1440];
		superblock.Slice(0, 1440).CopyTo(span);
		span.Slice(896, 32).Clear();
		return ProsperoSha3.HashData(span);
	}

	/// <summary>
	/// Computes the superblock ICV and writes it into the 32-byte ICV field at
	/// <see cref="F:LibProsperoPkg.PFS.ProsperoOuterPfsSignature.SuperblockIcvOffset" />. The field is zeroed before hashing, exactly matching the
	/// builder (which hashes the region with the not-yet-written ICV field still zero).
	/// </summary>
	public static void WriteSuperblockIcv(Span<byte> superblock)
	{
		ComputeSuperblockIcv(superblock).CopyTo(superblock.Slice(896, 32));
	}
}
