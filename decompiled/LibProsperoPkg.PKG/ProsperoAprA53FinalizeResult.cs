namespace LibProsperoPkg.PKG;

public sealed class ProsperoAprA53FinalizeResult
{
	public required string NapsLayoutPath { get; init; }

	public required string OuterPfsPath { get; init; }

	public required string EncryptionManifestPath { get; init; }

	public required int OuterBlockDigestCount { get; init; }

	public required int OuterSuperblockIndex { get; init; }

	public required long OuterPfsSize { get; init; }
}
