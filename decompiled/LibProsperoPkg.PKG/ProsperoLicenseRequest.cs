namespace LibProsperoPkg.PKG;

public sealed class ProsperoLicenseRequest
{
	public required ProsperoVolumeType VolumeType { get; init; }

	public required string ContentId { get; init; }

	public byte[]? EntitlementKey { get; init; }
}
