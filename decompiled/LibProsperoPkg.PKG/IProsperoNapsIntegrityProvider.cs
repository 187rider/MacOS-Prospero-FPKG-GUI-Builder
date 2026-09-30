namespace LibProsperoPkg.PKG;

public interface IProsperoNapsIntegrityProvider
{
	byte[]? BuildIhshPrefixes(ProsperoNapsIntegrityContext context);

	byte[]? BuildRollingHashes(ProsperoNapsIntegrityContext context);

	byte[]? BuildOuterBlockCheckCodes(ProsperoNapsIntegrityContext context);
}
