namespace LibProsperoPkg.PFS;

/// <summary>
/// Describes the nature of the dirent
/// </summary>
public enum DirentType
{
	/// <summary>
	/// A regular file
	/// </summary>
	File = 2,
	/// <summary>
	/// A directory
	/// </summary>
	Directory,
	/// <summary>
	/// The special "." file that points to the current dir
	/// </summary>
	Dot,
	/// <summary>
	/// The special ".." file that points to the parent dir
	/// </summary>
	DotDot
}
