namespace LibProsperoPkg.PKG;

public interface IProsperoRetailFinalizationProvider
{
	ProsperoRetailFinalizationResult FinalizeFih(ProsperoRetailFinalizationRequest request);

	byte[] FinalizeCntHeader(ProsperoRetailCntFinalizationRequest request);
}
