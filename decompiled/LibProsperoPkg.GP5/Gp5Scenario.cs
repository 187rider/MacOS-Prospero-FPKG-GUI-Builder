using System.Xml.Serialization;

namespace LibProsperoPkg.GP5;

/// <summary>A single PlayGo scenario; the chunk list is stored as element text.</summary>
public sealed class Gp5Scenario
{
	[XmlAttribute("id")]
	public int Id { get; set; }

	[XmlAttribute("type")]
	public string Type { get; set; } = "playmode";

	[XmlAttribute("initial_chunk_count")]
	public int InitialChunkCount { get; set; }

	[XmlAttribute("label")]
	public string Label { get; set; } = "";

	[XmlText]
	public string Chunks { get; set; } = "0";
}
