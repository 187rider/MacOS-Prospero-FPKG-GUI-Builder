using System.Collections.Generic;
using System.Xml;
using System.Xml.Serialization;

namespace LibProsperoPkg.GP5;

/// <summary>The <c>&lt;chunks&gt;</c> container and its PlayGo language defaults.</summary>
public sealed class Gp5Chunks
{
	[XmlAttribute("supported_languages")]
	public string? SupportedLanguages { get; set; }

	[XmlAttribute("default_language")]
	public string? DefaultLanguage { get; set; }

	[XmlElement(ElementName = "chunk", Type = typeof(Gp5Chunk))]
	public List<Gp5Chunk> Items { get; set; } = new List<Gp5Chunk>();

	[XmlAnyAttribute]
	public XmlAttribute[]? AdditionalAttributes { get; set; }

	public bool ShouldSerializeSupportedLanguages()
	{
		return !string.IsNullOrEmpty(SupportedLanguages);
	}

	public bool ShouldSerializeDefaultLanguage()
	{
		return !string.IsNullOrEmpty(DefaultLanguage);
	}
}
