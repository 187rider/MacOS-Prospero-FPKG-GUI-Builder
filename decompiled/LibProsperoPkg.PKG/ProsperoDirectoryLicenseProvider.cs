using System;
using System.IO;

namespace LibProsperoPkg.PKG;

public sealed class ProsperoDirectoryLicenseProvider : IProsperoLicenseProvider
{
	private readonly string _directory;

	public ProsperoDirectoryLicenseProvider(string directory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory, "directory");
		_directory = Path.GetFullPath(directory);
	}

	public ProsperoLicenseArtifacts GetLicense(ProsperoLicenseRequest request)
	{
		return ProsperoLicenseArtifacts.Load(_directory);
	}
}
