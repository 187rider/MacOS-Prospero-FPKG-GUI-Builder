using System;
using System.Collections.Generic;
using System.Threading;

namespace LibProsperoPkg.PKG;

public sealed class ProsperoPkgBuildProperties
{
	public required string SourceFolder { get; init; }

	public required string ContentId { get; init; }

	public string? PrimaryId { get; init; }

	public string Passcode { get; init; } = new string('0', 32);

	public ProsperoVolumeType VolumeType { get; init; }

	public DateTime TimeStamp { get; init; } = DateTime.UnixEpoch;

	public bool CompressInnerImage { get; init; }

	public ProsperoInnerCompression InnerCompression { get; init; }

	public CancellationToken CancellationToken { get; init; } = CancellationToken.None;

	public int LegacyZlibCompressionLevel { get; init; } = 9;

	public int LegacyZlibMaxDegreeOfParallelism { get; init; }

	public bool UsePublisherPprNaps { get; init; } = true;

	public int PlayGoChunkCount { get; init; } = 1;

	public ProsperoPublisherImageMode PublisherImageMode { get; init; }

	public byte[]? NapsOuterBlockCmacKey { get; init; }

	public byte[]? NapsMeta18 { get; init; }

	public IProsperoNapsIntegrityProvider? NapsIntegrityProvider { get; init; }

	public IReadOnlyDictionary<string, uint>? PublisherAfidAssignments { get; init; }

	public byte[]? NapsPfsImageKey { get; init; }

	public byte[]? NapsPfsImageSeed { get; init; }

	public byte[]? PublisherImageKey { get; init; }

	public byte[]? PublisherEntryKeys { get; init; }

	public byte[]? OuterPfsSeed { get; init; }

	public bool DeterministicBuild { get; init; }

	public IProsperoMetadataSigner? MetadataSigner { get; init; }

	public IProsperoLicenseProvider? LicenseProvider { get; init; }

	public int MaxHashingThreads { get; init; } = 2;
}
