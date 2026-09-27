namespace LibProsperoPkg.PFS;

/// <summary>The outcome of an inner-PFS layout build.</summary>
public sealed class ProsperoPfsLayoutResult
{
	/// <summary>The path the plaintext inner-PFS image was written to.</summary>
	public required string OutputPath { get; init; }

	/// <summary>Total image size in bytes.</summary>
	public required long ImageSize { get; init; }

	/// <summary>Filesystem block size used (bytes).</summary>
	public required uint BlockSize { get; init; }

	/// <summary>The PFS superblock version stamped (2 = PS5).</summary>
	public required long Version { get; init; }

	/// <summary>Number of files placed into the image.</summary>
	public required int FileCount { get; init; }

	/// <summary>Number of directories placed into the image.</summary>
	public required int DirectoryCount { get; init; }
}
