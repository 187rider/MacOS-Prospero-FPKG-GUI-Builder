using System.Xml;
using System.Xml.Serialization;

namespace LibProsperoPkg.GP5;

/// <summary>The <c>&lt;package&gt;</c> element. PS5 packages carry the content id in param.json, not here.</summary>
public sealed class Gp5Package
{
	[XmlAttribute("content_id")]
	public string? ContentId { get; set; }

	[XmlAttribute("passcode")]
	public string Passcode { get; set; } = "00000000000000000000000000000000";

	[XmlAttribute("c_date")]
	public string? CreationDate { get; set; }

	[XmlAttribute("entitlement_key")]
	public string? EntitlementKey { get; set; }

	[XmlAttribute("storage_type")]
	public string? StorageType { get; set; }

	[XmlAttribute("app_path")]
	public string? AppPath { get; set; }

	[XmlAnyAttribute]
	public XmlAttribute[]? AdditionalAttributes { get; set; }

	public bool ShouldSerializeContentId()
	{
		return !string.IsNullOrEmpty(ContentId);
	}

	public bool ShouldSerializeCreationDate()
	{
		return !string.IsNullOrEmpty(CreationDate);
	}

	public bool ShouldSerializeEntitlementKey()
	{
		return !string.IsNullOrEmpty(EntitlementKey);
	}

	public bool ShouldSerializeStorageType()
	{
		return !string.IsNullOrEmpty(StorageType);
	}

	public bool ShouldSerializeAppPath()
	{
		return !string.IsNullOrEmpty(AppPath);
	}
}
