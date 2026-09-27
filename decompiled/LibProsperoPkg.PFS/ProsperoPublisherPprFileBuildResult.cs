using LibProsperoPkg.PFS.Compression;

namespace LibProsperoPkg.PFS;

/// <summary>
/// File-backed publisher artifact result. Large payloads stay in the returned files; only layout
/// and digest metadata are retained in memory.
/// </summary>
public sealed class ProsperoPublisherPprFileBuildResult
{
	public required string InnerPfsPath { get; init; }

	public required string LogicalImagePath { get; init; }

	public required string PackedImagePath { get; init; }

	public required string NapsLayoutPath { get; init; }

	public required string OuterPfsPath { get; init; }

	public required byte[] OuterSeed { get; init; }

	public required int OuterSuperblockIndex { get; init; }

	public required int InnerFileCount { get; init; }

	public required int InnerInodeCount { get; init; }

	public required byte[] ImageDigests { get; init; }

	public required byte[] LogicalImageDigest { get; init; }

	public required ProsperoNapsFileBuildResult Naps { get; init; }

	public required ProsperoPfsImageTreeInfo OuterTree { get; init; }

	public required bool OuterPfsEncrypted { get; init; }
}
