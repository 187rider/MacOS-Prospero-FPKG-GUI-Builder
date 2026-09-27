namespace LibProsperoPkg.PFS;

/// <summary>
/// File-backed input for a large outer-PFS build. The source file is opened only while its blocks
/// are copied and hashed, so the complete payload never needs to be held in a managed array.
/// </summary>
public sealed class ProsperoOuterFileSource
{
	public required string Name { get; init; }

	public required string Path { get; init; }

	public long? SizeCompressed { get; init; }

	public bool Signed { get; init; }
}
