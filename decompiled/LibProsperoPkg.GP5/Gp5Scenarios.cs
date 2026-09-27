using System.Collections.Generic;
using System.Xml.Serialization;

namespace LibProsperoPkg.GP5;

/// <summary>The <c>&lt;scenarios&gt;</c> container.</summary>
public sealed class Gp5Scenarios
{
	[XmlAttribute("default_id")]
	public int DefaultId { get; set; }

	[XmlElement(ElementName = "scenario", Type = typeof(Gp5Scenario))]
	public List<Gp5Scenario> Items { get; set; } = new List<Gp5Scenario>();
}
