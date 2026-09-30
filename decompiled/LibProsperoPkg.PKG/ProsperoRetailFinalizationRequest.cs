using System;

namespace LibProsperoPkg.PKG;

public sealed class ProsperoRetailFinalizationRequest
{
	public required ReadOnlyMemory<byte> FihHeader { get; init; }

	public int FihFinalizationOffset => 61440;

	public int FihFinalizationSize => 768;
}
