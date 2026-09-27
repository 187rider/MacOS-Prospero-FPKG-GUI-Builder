using System;
using LibProsperoPkg.PFS.Compression;

namespace LibProsperoPkg.PFS;

/// <summary>Inputs for the publisher-compatible PPR-PFS/NAPS/outer-PFS pipeline.</summary>
public sealed class ProsperoPublisherPprBuildOptions
{
	public required string SourceFolder { get; init; }

	public required string OutputDirectory { get; init; }

	public required string ContentId { get; init; }

	public string Passcode { get; init; } = new string('0', 32);

	/// <summary>Inner direct-offset PPR-PFS options. Publisher layout is forced for this operation.</summary>
	public ProsperoPfsLayoutOptions PfsOptions { get; init; } = new ProsperoPfsLayoutOptions();

	public ProsperoNapsBuildOptions NapsOptions { get; init; } = new ProsperoNapsBuildOptions();

	/// <summary>Outer-PFS seed. A random 16-byte seed is generated when omitted.</summary>
	public byte[]? OuterSeed { get; init; }

	/// <summary>Derive a stable content-specific outer seed when <see cref="P:LibProsperoPkg.PFS.ProsperoPublisherPprBuildOptions.OuterSeed" /> is omitted.</summary>
	public bool DeterministicBuild { get; init; }

	/// <summary>
	/// Encrypt the outer PFS with AES-XTS. Disabling this selects the final
	/// <see cref="F:LibProsperoPkg.ProsperoPublisherImageMode.PlaintextNoAuth" /> representation.
	/// </summary>
	public bool EncryptOuterPfs { get; init; } = true;

	public DateTime TimeStamp { get; init; } = DateTime.UnixEpoch;
}
