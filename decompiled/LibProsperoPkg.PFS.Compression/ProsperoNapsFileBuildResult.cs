using LibProsperoPkg.PKG;

namespace LibProsperoPkg.PFS.Compression;

/// <summary>Artifacts emitted by the bounded-memory stream/file NAPS writer.</summary>
public sealed class ProsperoNapsFileBuildResult
{
	public required byte[] LayoutBytes { get; init; }

	public required NapsLayoutDocument Layout { get; init; }

	public required int CompressedSpanCount { get; init; }

	public required int StoredSpanCount { get; init; }

	public required long LogicalSize { get; init; }

	public required long PackedSize { get; init; }
}
