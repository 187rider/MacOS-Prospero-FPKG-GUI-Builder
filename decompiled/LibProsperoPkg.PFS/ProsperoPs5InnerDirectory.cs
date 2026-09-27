namespace LibProsperoPkg.PFS;

/// <summary>
/// One explicit directory to preserve in the PS5 nwonly inner image.  This is primarily needed for
/// empty GP5 directories, which cannot be inferred from a flat file list.
/// </summary>
public sealed class ProsperoPs5InnerDirectory
{
	/// <summary>Absolute path from the user root, e.g. <c>/data/shaders</c>.</summary>
	public required string Path { get; init; }
}
