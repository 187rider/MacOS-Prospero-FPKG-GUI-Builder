using System.Xml;
using System.Xml.Serialization;

namespace LibProsperoPkg.GP5;

/// <summary>An explicit file entry mapping a source path to a package destination path.</summary>
public sealed class Gp5File
{
	[XmlAttribute("dst_path")]
	public string DestinationPath { get; set; } = "";

	[XmlAttribute("src_path")]
	public string? SourcePath { get; set; }

	[XmlAttribute("chunk")]
	public int Chunk { get; set; }

	[XmlIgnore]
	public bool ChunkSpecified { get; set; }

	[XmlAttribute("content_config_label")]
	public string? ContentConfigLabel { get; set; }

	[XmlAttribute("pfs_compression")]
	public string? PfsCompression { get; set; }

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

	public bool ShouldSerializePfsCompression()
	{
		return !string.IsNullOrEmpty(PfsCompression);
	}
}
