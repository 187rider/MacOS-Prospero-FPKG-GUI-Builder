namespace LibProsperoPkg.PKG;

public sealed class ProsperoFihHeader
{
	public byte SignedByte { get; init; }

	public bool IsOfficial => SignedByte == 128;

	public ulong PfsImageOffset { get; init; }

	public ulong PfsImageSize { get; init; }

	public ulong EmbeddedCntOffset { get; init; }

	public uint InnerImageBlockCount { get; init; }

	public uint MetadataBlockCount { get; init; }

	public ulong NapsLayoutSize { get; init; }
}
