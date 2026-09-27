using System;
using System.IO;
using System.Linq;

namespace LibProsperoPkg.GP5;

/// <summary>
/// Turns a prepared application folder into a <see cref="T:LibProsperoPkg.GP5.Gp5Project" />.
/// </summary>
public static class Gp5Creator
{
	/// <summary>
	/// The default directory-exclude mask, so project scaffolding (the .gp5 itself, keystone,
	/// intermediate caches, …) never ends up inside the image.
	/// </summary>
	public const string DefaultDirExclude = "about";

	/// <summary>The default file-exclude mask (semicolon separated).</summary>
	public const string DefaultFileExclude = "*.gp5;*.esbak;keystone;*.dds;disc_info.dat;pfs-version.dat;ext_info.dat";

	/// <summary>
	/// Builds a GP5 project that references <paramref name="sourceFolder" /> as a single
	/// recursively-walked <c>rootdir</c>. The path is emitted verbatim; callers that need a
	/// different on-disk root can pass an override.
	/// </summary>
	/// <param name="sourceFolder">The prepared folder (must contain <c>sce_sys/param.json</c>).</param>
	/// <param name="type">The Prospero volume type.</param>
	/// <param name="passcode">The 32-character package passcode (defaults to all zeroes).</param>
	/// <param name="rootDirPathOverride">
	/// When supplied, written as the <c>rootdir</c> <c>src_path</c> instead of
	/// <paramref name="sourceFolder" />.
	/// </param>
	public static Gp5Project FromFolder(string sourceFolder, Gp5VolumeType type = Gp5VolumeType.prospero_app, string passcode = "00000000000000000000000000000000", string? rootDirPathOverride = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourceFolder, "sourceFolder");
		if (!Directory.Exists(sourceFolder))
		{
			throw new DirectoryNotFoundException("Source folder does not exist: " + sourceFolder);
		}
		Gp5Project gp5Project = Gp5Project.Create(type, passcode);
		gp5Project.FilesSpecified = false;
		gp5Project.RootDir = new Gp5RootDir
		{
			SourcePath = (rootDirPathOverride ?? sourceFolder),
			DirExclude = "about",
			FileExclude = "*.gp5;*.esbak;keystone;*.dds;disc_info.dat;pfs-version.dat;ext_info.dat"
		};
		return gp5Project;
	}

	/// <summary>
	/// Builds a GP5 project in the <see cref="F:LibProsperoPkg.GP5.Gp5Layout.Flat" /> style: every file under
	/// <paramref name="sourceFolder" /> is listed as an explicit top-level <c>&lt;file&gt;</c> entry
	/// (inside <c>&lt;files&gt;</c>), with no <c>&lt;rootdir&gt;</c> / <c>&lt;global_exclude&gt;</c>. This
	/// produces a fully-resolved project that does not rely on the reference tool's own directory walking,
	/// which is convenient for a direct packaging pipeline.
	/// </summary>
	public static Gp5Project FromFolderExplicit(string sourceFolder, Gp5VolumeType type = Gp5VolumeType.prospero_app, string passcode = "00000000000000000000000000000000")
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourceFolder, "sourceFolder");
		if (!Directory.Exists(sourceFolder))
		{
			throw new DirectoryNotFoundException("Source folder does not exist: " + sourceFolder);
		}
		string root = Path.GetFullPath(sourceFolder);
		Gp5Project gp5Project = Gp5Project.Create(type, passcode);
		gp5Project.FilesSpecified = true;
		foreach (string item in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy((string path) => Path.GetRelativePath(root, path), StringComparer.Ordinal))
		{
			string destinationPath = Path.GetRelativePath(root, item).Replace(Path.DirectorySeparatorChar, '\\').Replace(Path.AltDirectorySeparatorChar, '\\');
			gp5Project.Files.Add(new Gp5File
			{
				SourcePath = item,
				DestinationPath = destinationPath
			});
		}
		return gp5Project;
	}
}
