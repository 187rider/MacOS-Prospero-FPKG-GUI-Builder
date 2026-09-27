using System.Collections.Generic;

namespace LibProsperoPkg.PFS;

/// <summary>
/// One node (inode) of a built PFS image as captured for the <c>pfsimage.xml</c>
/// introspection sections. Values come from this image's own inode table.
/// </summary>
public sealed class ProsperoPfsImageNode
{
	/// <summary>Node name (empty for the synthetic super-root).</summary>
	public string Name { get; init; } = "";

	/// <summary>True for a directory node.</summary>
	public bool IsDirectory { get; init; }

	/// <summary>Inode number (the <c>inode</c> attribute).</summary>
	public uint InodeNumber { get; init; }

	/// <summary>On-disk (stored) size — the smaller value for a PFSC-compressed file.</summary>
	public long StoredSize { get; init; }

	/// <summary>Logical (plain/decompressed) size — equals <see cref="P:LibProsperoPkg.PFS.ProsperoPfsImageNode.StoredSize" /> for raw files.</summary>
	public long PlainSize { get; init; }

	/// <summary>Inode flags (the <c>imode</c> attribute).</summary>
	public uint Flags { get; init; }

	/// <summary>Inode mode bits (the <c>mode</c> attribute).</summary>
	public ushort Mode { get; init; }

	/// <summary>Hard-link count (the <c>links</c> attribute).</summary>
	public ushort Nlink { get; init; }

	/// <summary>First data block of this node (the <c>index</c> attribute base).</summary>
	public int StartBlock { get; init; }

	/// <summary>Number of data blocks this node occupies.</summary>
	public uint Blocks { get; init; }

	/// <summary>True when the inode carries the PFSC-compressed flag.</summary>
	public bool Compressed { get; init; }

	/// <summary>True for the PFS-internal pseudo files (flat path table / collision resolver).</summary>
	public bool Internal { get; init; }

	/// <summary>Child nodes (directories first, then files, both ordinal by name).</summary>
	public List<ProsperoPfsImageNode> Children { get; } = new List<ProsperoPfsImageNode>();
}
