using System.Xml;
using System.Xml.Serialization;

namespace LibProsperoPkg.GP5;

/// <summary>A single PlayGo chunk.</summary>
public sealed class Gp5Chunk
{
	[XmlAttribute("id")]
	public int Id { get; set; }

	[XmlAttribute("label")]
	public string Label { get; set; } = "";

	[XmlAttribute("layer_no")]
	public int LayerNumber { get; set; }

	[XmlIgnore]
	public bool LayerNumberSpecified { get; set; }

	[XmlAttribute("languages")]
	public string? Languages { get; set; }

	[XmlAttribute("use")]
	public bool Use { get; set; }

	[XmlIgnore]
	public bool UseSpecified { get; set; }

	[XmlAnyAttribute]
	public XmlAttribute[]? AdditionalAttributes { get; set; }

	public bool ShouldSerializeLabel()
	{
		return !string.IsNullOrEmpty(Label);
	}

	public bool ShouldSerializeLanguages()
	{
		return !string.IsNullOrEmpty(Languages);
	}
}
