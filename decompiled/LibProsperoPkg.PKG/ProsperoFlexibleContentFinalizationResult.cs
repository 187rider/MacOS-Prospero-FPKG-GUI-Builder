namespace LibProsperoPkg.PKG;

public sealed class ProsperoFlexibleContentFinalizationResult
{
	public required byte[] SuperblockDigest { get; init; }

	public required byte[] FixedInfoDigest { get; init; }

	public long SuperblockOffsetInPfsMetadata { get; init; }

	public int TokenFormatVersion { get; init; }
}
