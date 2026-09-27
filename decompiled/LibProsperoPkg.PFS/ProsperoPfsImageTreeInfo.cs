namespace LibProsperoPkg.PFS;

/// <summary>
/// A self-consistent snapshot of a whole built PFS image (outer or nested), used to emit the
/// <c>pfsimage.xml</c> sections that describe this image's layout. Produced by
/// <see cref="M:LibProsperoPkg.PFS.PfsBuilder.CaptureImageTree" /> after the image has been written.
/// </summary>
public sealed class ProsperoPfsImageTreeInfo
{
	/// <summary>PFS block size in bytes (superblock <c>BlockSize</c>).</summary>
	public int BlockSize { get; init; }

	/// <summary>Total data blocks in the image (superblock <c>Ndblock</c>).</summary>
	public long ImageBlocks { get; init; }

	/// <summary>Total inode count (superblock <c>DinodeCount</c>).</summary>
	public int InodeCount { get; init; }

	/// <summary>Blocks occupied by the dinode (inode) table (<c>DinodeBlockCount</c>).</summary>
	public int DinodeBlockCount { get; init; }

	/// <summary>Root inode number (0).</summary>
	public uint RootInodeNumber { get; init; }

	/// <summary>Start block of the dinode-table descriptor (superblock <c>InodeBlockSig</c>).</summary>
	public int DinodeBlock { get; init; }

	/// <summary>Size in bytes of the dinode-table descriptor (a single block).</summary>
	public long DinodeSize { get; init; }

	/// <summary>Flags of the dinode-table descriptor (the super-inode <c>imode</c>).</summary>
	public uint DinodeFlags { get; init; }

	/// <summary>Superblock seed (16 bytes; all-zero for our deterministic build).</summary>
	public byte[]? Seed { get; init; }

	/// <summary>
	/// Superblock integrity value: the 32-byte HMAC-SHA256 self-signature of this image's own
	/// superblock (final signature block 0 @ 0x380), captured during signing. Populated only for a
	/// signed image with <see cref="F:LibProsperoPkg.PFS.PfsBuilder.CaptureSuperblockIcv" /> set; <see langword="null" />
	/// otherwise.
	/// </summary>
	public byte[]? SuperblockIcv { get; init; }

	/// <summary>True when the image is signed (the outer PFS).</summary>
	public bool Signed { get; init; }

	/// <summary>True when the image is encrypted (the outer PFS).</summary>
	public bool Encrypted { get; init; }

	/// <summary>
	/// The synthetic super-root: the flat path table (+ optional collision resolver) and the
	/// user root (<c>uroot</c>) subtree, matching the reference PFS super-root layout.
	/// </summary>
	public ProsperoPfsImageNode Root { get; init; } = new ProsperoPfsImageNode();
}
