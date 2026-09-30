using System;
using System.IO;

namespace LibProsperoPkg.PKG;

public sealed class ProsperoLicenseArtifacts
{
	public required byte[] LicenseDat { get; init; }

	public required byte[] LicenseInfo { get; init; }

	public static ProsperoLicenseArtifacts Load(string directory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory, "directory");
		string fullPath = Path.GetFullPath(directory);
		return new ProsperoLicenseArtifacts
		{
			LicenseDat = File.ReadAllBytes(Path.Combine(fullPath, "license.dat")),
			LicenseInfo = File.ReadAllBytes(Path.Combine(fullPath, "license.info"))
		};
	}

	public void Validate(ProsperoLicenseRequest request)
	{
		ArgumentNullException.ThrowIfNull(request, "request");
		if (!ProsperoSystemFiles.ValidateLicenseDat(LicenseDat, request.ContentId, out string error))
		{
			throw new InvalidDataException("license.dat: " + error);
		}
		if (!ProsperoSystemFiles.ValidateLicenseInfo(LicenseInfo, request.ContentId, request.EntitlementKey ?? Array.Empty<byte>(), out string error2))
		{
			throw new InvalidDataException("license.info: " + error2);
		}
	}
}
