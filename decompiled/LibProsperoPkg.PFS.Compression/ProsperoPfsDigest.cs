using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PFS.Compression;

/// <summary>
/// Computes the SHA3-256 digests the PS5 PFSv3 compression container uses for its
/// per-block hashes (the <c>id=4</c> metadata section) and its file-level digest (header
/// offset <c>0x28</c>).
/// </summary>
/// <remarks>
/// SHA3-256 is the hash primitive used throughout the PFS compression format. Per-block
/// hashes are taken over the <i>uncompressed</i> block bytes, which is what
/// <see cref="M:LibProsperoPkg.PFS.Compression.ProsperoPfsDigest.ComputeBlockDigest(System.ReadOnlySpan{System.Byte})" /> returns. The host runtime must
/// support SHA3-256; query <see cref="P:LibProsperoPkg.PFS.Compression.ProsperoPfsDigest.IsSupported" /> before use.
/// </remarks>
public static class ProsperoPfsDigest
{
	/// <summary>The length, in bytes, of a SHA3-256 digest (256 bits).</summary>
	public const int DigestLength = 32;

	/// <summary>The length, in bytes, of the header parameter slice (<c>container[0x08..0x20]</c>) the file digest hashes.</summary>
	public const int FileDigestHeaderParamsLength = 24;

	/// <summary>
	/// Gets a value indicating whether the host runtime and operating system provide SHA3-256.
	/// </summary>
	public static bool IsSupported => ProsperoSha3.IsSupported;

	/// <summary>
	/// Computes the SHA3-256 digest of a single PFS block's <b>uncompressed</b> bytes, matching the
	/// per-block hash stored in the container's <c>id=4</c> metadata section.
	/// </summary>
	/// <param name="uncompressedBlock">The raw (pre-compression, post-deshuffle) block bytes.</param>
	/// <returns>The 32-byte SHA3-256 digest.</returns>
	/// <exception cref="T:System.PlatformNotSupportedException">SHA3-256 is unavailable on this host.</exception>
	public static byte[] ComputeBlockDigest(ReadOnlySpan<byte> uncompressedBlock)
	{
		return ProsperoSha3.HashData(uncompressedBlock);
	}

	/// <summary>
	/// Computes the SHA3-256 digest of <paramref name="data" /> into <paramref name="destination" />.
	/// </summary>
	/// <param name="data">The bytes to hash.</param>
	/// <param name="destination">A span of at least <see cref="F:LibProsperoPkg.PFS.Compression.ProsperoPfsDigest.DigestLength" /> bytes.</param>
	/// <returns>The number of bytes written (always <see cref="F:LibProsperoPkg.PFS.Compression.ProsperoPfsDigest.DigestLength" />).</returns>
	/// <exception cref="T:System.PlatformNotSupportedException">SHA3-256 is unavailable on this host.</exception>
	public static int ComputeBlockDigest(ReadOnlySpan<byte> data, Span<byte> destination)
	{
		return ProsperoSha3.HashData(data, destination);
	}

	/// <summary>
	/// Returns <c>true</c> when <paramref name="expected" /> equals the SHA3-256 digest of
	/// <paramref name="uncompressedBlock" />, using a fixed-time comparison.
	/// </summary>
	/// <param name="uncompressedBlock">The raw (pre-compression) block bytes.</param>
	/// <param name="expected">The 32-byte digest read from the container metadata.</param>
	public static bool VerifyBlockDigest(ReadOnlySpan<byte> uncompressedBlock, ReadOnlySpan<byte> expected)
	{
		if (expected.Length != 32)
		{
			return false;
		}
		Span<byte> span = stackalloc byte[32];
		ProsperoSha3.HashData(uncompressedBlock, span);
		return CryptographicOperations.FixedTimeEquals(span, expected);
	}

	private static void EnsureSupported()
	{
		_ = ProsperoSha3.IsSupported;
	}

	/// <summary>
	/// Computes the container's file-level SHA3-256 digest (stored at header offset <c>0x28</c>),
	/// using the target format's file-digest algorithm.
	/// </summary>
	/// <param name="headerParams">
	/// Exactly <see cref="F:LibProsperoPkg.PFS.Compression.ProsperoPfsDigest.FileDigestHeaderParamsLength" /> (24) bytes copied verbatim from the container
	/// header at offsets <c>0x08..0x20</c> (block size, encode parameters, and uncompressed size).
	/// </param>
	/// <param name="shuffleSection">The shuffle-pattern metadata section bytes (directory id=2).</param>
	/// <param name="boundarySection">The block-boundary table bytes (directory id=3).</param>
	/// <param name="blockHashSection">The per-block hash table bytes (directory id=4).</param>
	/// <returns>The 32-byte SHA3-256 file digest.</returns>
	/// <remarks>
	/// The preimage is <c>header32 || shuffleSection || boundarySection || blockHashSection</c>, where
	/// <c>header32</c> is a 32-byte, 8-byte-aligned little-endian struct:
	/// <c>{u32 1; u32 blockSize; u32 encodeParam0C; u32 0 (pad); u64 encodeParam10; u64 uncompressedSize}</c>.
	/// This layout applies to independent encoder outputs (compressed and stored blocks).
	/// </remarks>
	/// <exception cref="T:System.ArgumentException"><paramref name="headerParams" /> is not 24 bytes.</exception>
	/// <exception cref="T:System.PlatformNotSupportedException">SHA3-256 is unavailable on this host.</exception>
	public static byte[] ComputeFileDigest(ReadOnlySpan<byte> headerParams, ReadOnlySpan<byte> shuffleSection, ReadOnlySpan<byte> boundarySection, ReadOnlySpan<byte> blockHashSection)
	{
		if (headerParams.Length != 24)
		{
			throw new ArgumentException($"headerParams must be exactly {24} bytes (container[0x08..0x20]).", "headerParams");
		}
		EnsureSupported();
		Span<byte> span = stackalloc byte[32];
		BinaryPrimitives.WriteUInt32LittleEndian(span, 1u);
		headerParams.Slice(0, 8).CopyTo(span.Slice(4));
		headerParams.Slice(8).CopyTo(span.Slice(16));
		ProsperoSha3.Incremental incremental = new ProsperoSha3.Incremental();
		incremental.AppendData(span);
		incremental.AppendData(shuffleSection);
		incremental.AppendData(boundarySection);
		incremental.AppendData(blockHashSection);
		return incremental.GetHashAndReset();
	}

	/// <summary>
	/// Returns <c>true</c> when <paramref name="expected" /> equals the file digest computed by
	/// <see cref="M:LibProsperoPkg.PFS.Compression.ProsperoPfsDigest.ComputeFileDigest(System.ReadOnlySpan{System.Byte},System.ReadOnlySpan{System.Byte},System.ReadOnlySpan{System.Byte},System.ReadOnlySpan{System.Byte})" />, using a fixed-time comparison.
	/// </summary>
	public static bool VerifyFileDigest(ReadOnlySpan<byte> headerParams, ReadOnlySpan<byte> shuffleSection, ReadOnlySpan<byte> boundarySection, ReadOnlySpan<byte> blockHashSection, ReadOnlySpan<byte> expected)
	{
		if (expected.Length != 32)
		{
			return false;
		}
		return CryptographicOperations.FixedTimeEquals(ComputeFileDigest(headerParams, shuffleSection, boundarySection, blockHashSection), expected);
	}
}
