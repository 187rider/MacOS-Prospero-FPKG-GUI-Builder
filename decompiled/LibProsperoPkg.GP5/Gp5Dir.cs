using System.Collections.Generic;
using System.Xml;
using System.Xml.Serialization;

namespace LibProsperoPkg.GP5;

/// <summary>An explicit directory entry mapping a source folder to a package destination path.</summary>
public sealed class Gp5Dir
{
	[XmlAttribute("dst_path")]
	public string DestinationPath { get; set; } = "";

	[XmlAttribute("src_path")]
	public string? SourcePath { get; set; }

	[XmlAttribute("virtual")]
	public bool Virtual { get; set; }

	[XmlIgnore]
	public bool VirtualSpecified { get; set; }

	[XmlAttribute("chunk")]
	public int Chunk { get; set; }

	[XmlIgnore]
	public bool ChunkSpecified { get; set; }

	[XmlAttribute("content_config_label")]
	public string? ContentConfigLabel { get; set; }

	[XmlElement(ElementName = "dir", Type = typeof(Gp5Dir))]
	public List<Gp5Dir> Directories { get; set; } = new List<Gp5Dir>();

	[XmlElement(ElementName = "file", Type = typeof(Gp5File))]
	public List<Gp5File> Files { get; set; } = new List<Gp5File>();

	[XmlAnyAttribute]
	public XmlAttribute[]? AdditionalAttributes { get; set; }

	public bool ShouldSerializeSourcePath()
	{
		return !string.IsNullOrEmpty(SourcePath);
	}

	public bool ShouldSerializeContentConfigLabel()
	{
		return !string.IsNullOrEmpty(ContentConfigLabel);
	}
}
