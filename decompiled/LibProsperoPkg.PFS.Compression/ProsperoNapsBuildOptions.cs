using System.Collections.Generic;

namespace LibProsperoPkg.PFS.Compression;

/// <summary>Options for producing a self-contained NAPS packed image and type-13 layout.</summary>
public sealed class ProsperoNapsBuildOptions
{
	/// <summary>Kraken compression level, in the range -4..9.</summary>
	public int CompressionLevel { get; init; } = 7;

	/// <summary>Try Kraken before falling back to a stored 256-KiB span.</summary>
	public bool Compress { get; init; } = true;

	/// <summary>Decode the finished artifacts and compare them with the input before returning.</summary>
	public bool VerifyRoundTrip { get; init; } = true;

	/// <summary>
	/// Optional 16-byte AES-CMAC key used to convert each reversed SHA3-256 outer-block digest to
	/// the truncated eight-byte <c>OuterBlockDigest</c>. When absent those policy-gated slots are zero.
	/// </summary>
	public byte[]? OuterBlockCmacKey { get; init; }

	/// <summary>
	/// Optional logical-file boundaries. The first value must be zero and the final value must equal
	/// the input length. If omitted, NAPS describes one logical file covering the complete image.
	/// </summary>
	public IReadOnlyList<long>? FileBoundaries { get; init; }
}
