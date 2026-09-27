using System.Collections.Generic;

namespace LibProsperoPkg.PFS;

/// <summary>
/// Describes one node (file or directory) of the inner image for metadata reconstruction. All offsets are
/// resolved by the caller/builder from the image's data + metadata layout.
/// </summary>
public sealed class ProsperoPs5MetaNode
{
	/// <summary>Node name (leaf).</summary>
	public string Name = "";

	/// <summary>Full path from the user root (e.g. <c>/sce_sys/keystone</c>), used for the flat-path hash.</summary>
	public string FullPath = "";

	/// <summary>Assigned inode number.</summary>
	public uint Inode;

	/// <summary>True for a directory node.</summary>
	public bool IsDirectory;

	/// <summary>True when this node is a top-level uroot regular file that also appears in apr_flat_path_table.</summary>
	public bool IsApr;

	/// <summary>The node's afid (file id) or 0 for directories.</summary>
	public uint Afid;

	/// <summary>The node's logical byte offset within the inner image (data region for files, metadata region for dirs/tables).</summary>
	public ulong LogicalOffset;

	/// <summary>Uncompressed size in bytes.</summary>
	public long Size;

	/// <summary>Inode mode (e.g. 0x816d file, 0x4168 dir).</summary>
	public ushort Mode;

	/// <summary>Directory link count.</summary>
	public ushort Nlink = 1;

	/// <summary>Inode flags word.</summary>
	public uint Flags;

	/// <summary>Parent inode number, or -1 for the super-root's direct children.</summary>
	public int ParentInode = -1;

	/// <summary>Byte offset of this node's dirent within its parent dirent block, or -1.</summary>
	public int DirentOffset = -1;

	/// <summary>The dirents contained in this directory (in on-disk order), if it is a directory.</summary>
	public List<PfsDirent> Dirents = new List<PfsDirent>();
}
