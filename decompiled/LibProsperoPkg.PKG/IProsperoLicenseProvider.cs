namespace LibProsperoPkg.PKG;

public interface IProsperoLicenseProvider
{
	ProsperoLicenseArtifacts GetLicense(ProsperoLicenseRequest request);
}
