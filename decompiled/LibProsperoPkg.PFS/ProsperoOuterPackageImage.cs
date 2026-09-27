namespace LibProsperoPkg.PFS;

/// <summary>
/// The finalized outer-PFS image for a package: the encrypted image plus the metadata the container
/// finalizer consumes (per-block digest table, superblock ICV, and image-tree snapshot).
/// </summary>
public sealed class ProsperoOuterPackageImage
{
	/// <summary>The encrypted outer-PFS image (the plaintext superblock block is left in the clear).</summary>
	public required byte[] Ciphertext { get; init; }

	/// <summary>Total image size in bytes.</summary>
	public required long PfsSize { get; init; }

	/// <summary>One 32-byte per-block digest for every image block (the imagedigs table).</summary>
	public required byte[] ImageDigests { get; init; }

	/// <summary>The 32-byte superblock integrity value.</summary>
	public required byte[] SuperblockIcv { get; init; }

	/// <summary>Index of the plaintext metadata (superblock) block.</summary>
	public required int SuperblockIndex { get; init; }

	/// <summary>Self-consistent image-tree snapshot for the pfsimage.xml introspection sections.</summary>
	public required ProsperoPfsImageTreeInfo Tree { get; init; }
}
