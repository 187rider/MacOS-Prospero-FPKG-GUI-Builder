using System;
using System.Collections.Generic;

namespace LibProsperoPkg.PKG;

public sealed class ProsperoNapsIntegrityContext
{
	public required ulong InnerImageSize { get; init; }

	public required ReadOnlyMemory<byte> MountImage { get; init; }

	public required ReadOnlyMemory<byte> PhysicalInnerImage { get; init; }

	public string? PhysicalInnerImagePath { get; init; }

	public ReadOnlyMemory<byte> PfsImageKey { get; init; }

	public ReadOnlyMemory<byte> PfsImageSeed { get; init; }

	public required IReadOnlyList<ProsperoNapsIntegrityBlock> MappingBlocks { get; init; }

	public int PhysicalInnerBlockCount
	{
		get
		{
			checked
			{
				return (int)unchecked(InnerImageSize / 65536);
			}
		}
	}
}
