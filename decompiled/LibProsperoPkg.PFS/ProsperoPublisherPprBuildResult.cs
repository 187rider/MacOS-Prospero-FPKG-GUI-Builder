using LibProsperoPkg.PFS.Compression;

namespace LibProsperoPkg.PFS;

/// <summary>Paths and geometry produced by <see cref="M:LibProsperoPkg.PFS.ProsperoPublisherPprBuilder.Build(LibProsperoPkg.PFS.ProsperoPublisherPprBuildOptions,System.Action{System.String})" />.</summary>
public sealed class ProsperoPublisherPprBuildResult
{
	public required string InnerPfsPath { get; init; }

	public required string LogicalImagePath { get; init; }

	public required string PackedImagePath { get; init; }

	public required string NapsLayoutPath { get; init; }

	public required string OuterPfsPath { get; init; }

	public required byte[] OuterSeed { get; init; }

	public required int OuterSuperblockIndex { get; init; }

	public required int InnerFileCount { get; init; }

	/// <summary>Publisher FIH inode count: uroot plus all child directories and files.</summary>
	public required int InnerInodeCount { get; init; }

	/// <summary>
	/// CNT <c>imagedigs.dat</c>: one byte-reversed SHA3-256 digest for every plaintext 64-KiB
	/// outer-PFS block. This formula is byte-exact against publisher-produced packages.
	/// </summary>
	public required byte[] ImageDigests { get; init; }

	/// <summary>SHA3-256 of the complete uncompressed logical PPR-PFS stream (artifact diagnostic).</summary>
	public required byte[] LogicalImageDigest { get; init; }

	public required ProsperoNapsBuildResult Naps { get; init; }

	public required bool OuterPfsEncrypted { get; init; }
}
