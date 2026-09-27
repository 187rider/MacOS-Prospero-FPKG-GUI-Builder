namespace LibProsperoPkg.GP5;

/// <summary>
/// The PS5 package volume types expressed by a GP5 project.
/// </summary>
public enum Gp5VolumeType
{
	/// <summary>A standalone PS5 application/game package.</summary>
	prospero_app,
	/// <summary>A PS5 application patch.</summary>
	prospero_patch,
	/// <summary>Additional content (DLC) that ships data.</summary>
	prospero_ac,
	/// <summary>Additional content (DLC) entitlement only, no data.</summary>
	prospero_al
}
