namespace LibProsperoPkg.PKG;

public sealed class ProsperoFlexibleContentFinalizationOptions
{
	public required string FixedInfoHeaderPath { get; init; }

	public required string PfsMetadataPath { get; init; }

	public required string SubcontainerPath { get; init; }

	public required string ManifestPath { get; init; }

	public required string TokenPath { get; init; }

	public required string PartnerPrivateKeyPath { get; init; }

	public required string Passcode { get; init; }
}
