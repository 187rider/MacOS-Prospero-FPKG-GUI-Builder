using System;
using System.Xml.Serialization;

namespace LibProsperoPkg.GP5;

/// <summary>The <c>&lt;volume&gt;</c> element of a GP5 project.</summary>
public sealed class Gp5Volume
{
	[XmlElement(ElementName = "volume_type")]
	public string VolumeTypeName { get; set; } = "prospero_app";

	[XmlIgnore]
	public Gp5VolumeType Type
	{
		get
		{
			if (!Enum.TryParse<Gp5VolumeType>(VolumeTypeName, out var result))
			{
				return Gp5VolumeType.prospero_app;
			}
			return result;
		}
		set
		{
			VolumeTypeName = value.ToString();
		}
	}

	[XmlElement(ElementName = "volume_id")]
	public string? VolumeId { get; set; }

	[XmlElement(ElementName = "volume_ts")]
	public string? VolumeTimestamp { get; set; }

	[XmlElement(ElementName = "package")]
	public Gp5Package Package { get; set; } = new Gp5Package();

	[XmlElement(ElementName = "chunk_info")]
	public Gp5ChunkInfo? ChunkInfo { get; set; }
}
