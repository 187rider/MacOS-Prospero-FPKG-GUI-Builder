using LibProsperoPkg.PKG;

namespace LibProsperoPkg.PFS.Compression;

/// <summary>Artifacts emitted by the in-memory NAPS writer.</summary>
public sealed class ProsperoNapsBuildResult
{
	public required byte[] PackedImage { get; init; }

	public required byte[] LayoutBytes { get; init; }

	public required NapsLayoutDocument Layout { get; init; }

	public required int CompressedSpanCount { get; init; }

	public required int StoredSpanCount { get; init; }

	public long LogicalSize { get; init; }
}
