using System;

namespace LibProsperoPkg.PKG;

public sealed class ProsperoRetailFinalizationResult
{
	public required byte[] FihFinalizationMaterial { get; init; }

	public byte[] SupplementalData { get; init; } = Array.Empty<byte>();
}
