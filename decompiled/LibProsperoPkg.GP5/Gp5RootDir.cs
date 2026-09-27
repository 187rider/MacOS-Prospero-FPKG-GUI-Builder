using System.Collections.Generic;
using System.Xml.Serialization;

namespace LibProsperoPkg.GP5;

/// <summary>
/// The <c>&lt;rootdir&gt;</c> element of the <see cref="F:LibProsperoPkg.GP5.Gp5Layout.Normal" /> layout: a single source
/// directory the reference tool walks recursively, with optional directory- and file-exclude masks.
/// The explicit per-file/per-folder listing of the flat layout lives in
/// <see cref="P:LibProsperoPkg.GP5.Gp5Project.Files" /> / <see cref="P:LibProsperoPkg.GP5.Gp5Project.Folders" />, not here.
/// </summary>
public sealed class Gp5RootDir
{
	[XmlAttribute("dir_exclude")]
	public string? DirExclude { get; set; }

	[XmlAttribute("file_exclude")]
	public string? FileExclude { get; set; }

	[XmlAttribute("src_path")]
	public string? SourcePath { get; set; }

	/// <summary>Overlay and virtual directories nested directly under <c>rootdir</c>.</summary>
	[XmlElement(ElementName = "dir", Type = typeof(Gp5Dir))]
	public List<Gp5Dir> Directories { get; set; } = new List<Gp5Dir>();

	/// <summary>Overlay files nested directly under <c>rootdir</c>.</summary>
	[XmlElement(ElementName = "file", Type = typeof(Gp5File))]
	public List<Gp5File> Files { get; set; } = new List<Gp5File>();

	public bool ShouldSerializeSourcePath()
	{
		return !string.IsNullOrEmpty(SourcePath);
	}

	public bool ShouldSerializeDirExclude()
	{
		return !string.IsNullOrEmpty(DirExclude);
	}

	public bool ShouldSerializeFileExclude()
	{
		return !string.IsNullOrEmpty(FileExclude);
	}
}
