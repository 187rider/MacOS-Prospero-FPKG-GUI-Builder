using LibProsperoPkg.PFS;

namespace LibProsperoPkg.PKG;

public sealed class ProsperoAprImageBuildResult
{
	public required string PackedImagePath { get; init; }

	public required string LogicalImagePath { get; init; }

	public required string NapsLayoutPath { get; init; }

	public required string OuterPfsPath { get; init; }

	public required string EncryptionManifestPath { get; init; }

	public required byte[] OuterSeed { get; init; }

	public required int OuterSuperblockIndex { get; init; }

	public required ProsperoPfsImageTreeInfo OuterTree { get; init; }

	public required byte[] ImageDigests { get; init; }

	public required long PackedImageSize { get; init; }

	public required long LogicalImageSize { get; init; }

	public required long MetadataBase { get; init; }

	public required int InnerFileCount { get; init; }

	public required int InnerInodeCount { get; init; }

	public required int SparseAfidCount { get; init; }

	public required int EmptyFileCount { get; init; }

	public required NapsLayoutCounts NapsCounts { get; init; }

	public required bool OuterBlockDigestsKeyed { get; init; }

	public required bool OuterPfsEncrypted { get; init; }

	public string? CmacManifestPath { get; init; }

	public string? BuildStatePath { get; init; }

	public bool AwaitingA53Digests { get; init; }
}
