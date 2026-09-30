using System;
using System.Collections.Generic;

namespace LibProsperoPkg.PKG;

public sealed class ProsperoAprImageBuildOptions
{
	public required string SourceFolder { get; init; }

	public required string OutputDirectory { get; init; }

	public required string ContentId { get; init; }

	public string Passcode { get; init; } = new string('0', 32);

	public ProsperoVolumeType VolumeType { get; init; }

	public byte[]? NapsOuterBlockCmacKey { get; init; }

	public byte[]? OuterSeed { get; init; }

	public bool DeterministicBuild { get; init; }

	public bool EncryptOuterPfs { get; init; } = true;

	public bool DeferOuterBlockDigestsToA53 { get; init; }

	public DateTime TimeStamp { get; init; } = DateTime.UnixEpoch;

	public IReadOnlyDictionary<string, uint>? AfidAssignments { get; init; }
}
