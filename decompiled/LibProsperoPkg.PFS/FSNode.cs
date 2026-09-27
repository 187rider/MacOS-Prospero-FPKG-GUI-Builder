namespace LibProsperoPkg.PFS;

/// <summary>
/// Base class for directories and files in a PFS image builder.
/// </summary>
public abstract class FSNode
{
	/// <summary>
	/// The parent directory of this node. This should only be null for the root directory.
	/// </summary>
	public FSDir Parent;

	/// <summary>
	/// The name of this node.
	/// </summary>
	public string name;

	/// <summary>
	/// The inode describing this node.
	/// </summary>
	public Inode ino;

	/// <summary>
	/// The actual size on disk of this node (i.e. the filesize or size of the dirent block)
	/// </summary>
	public virtual long Size { get; protected set; }

	/// <summary>
	/// The logical size of this file. Should only differ from Size in the case of a compressed (PFSC) file.
	/// </summary>
	public virtual long CompressedSize => Size;

	/// <summary>
	/// Get the full path of this file within the image.
	/// </summary>
	/// <param name="suffix">Optional suffix to append to the result</param>
	/// <returns></returns>
	public string FullPath(string suffix = "")
	{
		if (Parent == null)
		{
			return suffix;
		}
		return Parent.FullPath("/" + name + suffix);
	}
}
