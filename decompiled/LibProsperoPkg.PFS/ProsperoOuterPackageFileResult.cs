namespace LibProsperoPkg.PFS;

/// <summary>Metadata returned by the file-backed outer-PFS package writer.</summary>
public sealed class ProsperoOuterPackageFileResult
{
	public required long PfsSize { get; init; }

	public required byte[] ImageDigests { get; init; }

	public required byte[] SuperblockIcv { get; init; }

	public required int SuperblockIndex { get; init; }

	public required int InodeTableIndex { get; init; }

	public required int SuperRootDirentIndex { get; init; }

	public required int FltIndex { get; init; }

	public required int UrootDirentIndex { get; init; }

	public required int[] FileFirstBlock { get; init; }

	public required int[] FileBlockCount { get; init; }

	public required ProsperoOuterBlockKind[] BlockKinds { get; init; }

	public required ProsperoPfsImageTreeInfo Tree { get; init; }
}
