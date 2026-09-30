using System;

namespace LibProsperoPkg.PKG;

public sealed class ProsperoRetailCntFinalizationRequest
{
	public required ReadOnlyMemory<byte> CntHeader { get; init; }
}
