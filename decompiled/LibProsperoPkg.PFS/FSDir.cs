using System.Collections.Generic;
using System.Linq;

namespace LibProsperoPkg.PFS;

/// <summary>
/// Represents a directory in a PFS image builder.
/// </summary>
public class FSDir : FSNode
{
	/// <summary>
	/// The directories in this directory.
	/// </summary>
	public List<FSDir> Dirs = new List<FSDir>();

	/// <summary>
	/// The files in this directory.
	/// </summary>
	public List<FSFile> Files = new List<FSFile>();

	/// <summary>
	/// The dirents describing the nodes in this directory.
	/// </summary>
	public List<PfsDirent> Dirents = new List<PfsDirent>();

	public override long Size => Dirents.Sum((PfsDirent d) => d.EntSize);

	/// <summary>
	/// Gets all the dirs and files in this directory and subdirectories.
	/// </summary>
	/// <returns>all the dirs and files in this directory and subdirectories</returns>
	public List<FSNode> GetAllChildren()
	{
		List<FSNode> list = new List<FSNode>(GetAllChildrenDirs());
		list.AddRange(GetAllChildrenFiles());
		return list;
	}

	/// <summary>
	/// Gets all the dirs in this directory and subdirectories.
	/// </summary>
	/// <returns>all the dirs in this directory and subdirectories</returns>
	public List<FSDir> GetAllChildrenDirs()
	{
		List<FSDir> list = new List<FSDir>(Dirs);
		foreach (FSDir dir in Dirs)
		{
			foreach (FSDir allChildrenDir in dir.GetAllChildrenDirs())
			{
				list.Add(allChildrenDir);
			}
		}
		return list;
	}

	/// <summary>
	/// Gets all the files in this directory and subdirectories.
	/// </summary>
	/// <returns>all the files in this directory and subdirectories</returns>
	public List<FSFile> GetAllChildrenFiles()
	{
		List<FSFile> list = new List<FSFile>(Files);
		foreach (FSDir allChildrenDir in GetAllChildrenDirs())
		{
			foreach (FSFile file in allChildrenDir.Files)
			{
				list.Add(file);
			}
		}
		return list;
	}

	/// <summary>
	/// Gets the file at the given path relative to this directory.
	///
	/// For example, to get a file named "b" in a directory called "a" in this directory,
	/// you'd pass in "a/b".
	/// </summary>
	/// <param name="path">Relative path to the desired file</param>
	/// <returns>The file, or null if it can't be found.</returns>
	public FSFile GetFile(string path)
	{
		string[] breadcrumbs = path.Split('/');
		if (breadcrumbs.Length == 1)
		{
			return Files.Find((FSFile f) => f.name == path);
		}
		return Dirs.Find((FSDir d) => d.name == breadcrumbs[0])?.GetFile(path.Substring(path.IndexOf('/') + 1));
	}
}
