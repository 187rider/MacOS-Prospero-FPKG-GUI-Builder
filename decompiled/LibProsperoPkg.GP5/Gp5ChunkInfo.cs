using System.Collections.Generic;
using System.Xml.Serialization;

namespace LibProsperoPkg.GP5;

/// <summary>PlayGo chunk/scenario metadata; present only for application packages.</summary>
public sealed class Gp5ChunkInfo
{
	[XmlAttribute("chunk_count")]
	public int ChunkCount { get; set; }

	[XmlAttribute("scenario_count")]
	public int ScenarioCount { get; set; }

	[XmlElement(ElementName = "chunks")]
	public Gp5Chunks ChunkSet { get; set; } = new Gp5Chunks();

	/// <summary>Compatibility view over <see cref="P:LibProsperoPkg.GP5.Gp5ChunkInfo.ChunkSet" />.</summary>
	[XmlIgnore]
	public List<Gp5Chunk> Chunks
	{
		get
		{
			return ChunkSet.Items;
		}
		set
		{
			ChunkSet.Items = value ?? new List<Gp5Chunk>();
		}
	}

	[XmlElement(ElementName = "scenarios")]
	public Gp5Scenarios Scenarios { get; set; } = new Gp5Scenarios();
}
