using System;

namespace LibProsperoPkg.PFS;

/// <summary>
/// Result of building an outer-PFS image: the plaintext image, the per-block XTS classification,
/// and the metadata (superblock) block index.
/// </summary>
public sealed class ProsperoOuterPfsBuildResult
{
	/// <summary>The assembled plaintext outer-PFS image (a whole number of <see cref="F:LibProsperoPkg.PFS.ProsperoOuterPfsBuilder.BlockSize" /> blocks).</summary>
	public required byte[] Plaintext { get; init; }

	/// <summary>Per-block AES-XTS classification (Data / Signed / Plaintext), one entry per block.</summary>
	public required ProsperoOuterBlockKind[] BlockKinds { get; init; }

	/// <summary>Index of the plaintext metadata (superblock) block.</summary>
	public required int SuperblockIndex { get; init; }

	/// <summary>First image block of each outer file, parallel to the input file list.</summary>
	public int[] FileFirstBlock { get; init; } = Array.Empty<int>();

	/// <summary>Image block count of each outer file, parallel to the input file list.</summary>
	public int[] FileBlockCount { get; init; } = Array.Empty<int>();

	/// <summary>Block index of the inode (dinode) table.</summary>
	public int InodeTableIndex { get; init; }

	/// <summary>Block index of the super-root dirents.</summary>
	public int SuperRootDirentIndex { get; init; }

	/// <summary>Block index of the inode_flat_path_table.</summary>
	public int FltIndex { get; init; }

	/// <summary>Block index of the uroot dirents.</summary>
	public int UrootDirentIndex { get; init; }

	/// <summary>Total block count of the image.</summary>
	public int BlockCount => BlockKinds.Length;
}
