using System;
using System.Collections.Generic;
using LibProsperoPkg.PKG;

namespace LibProsperoPkg;

/// <summary>Options describing the PS5 package to build.</summary>
public sealed class ProsperoBuildOptions
{
	/// <summary>The build preset.</summary>
	public ProsperoPackageMode Mode { get; set; }

	/// <summary>
	/// The container format the builder emits. Defaults to the finalized debug
	/// <see cref="F:LibProsperoPkg.ProsperoOutputFormat.DebugImage" />, since only a \x7FFIH image is a full,
	/// installable package; a bare \x7FCNT is metadata only.
	/// </summary>
	public ProsperoOutputFormat OutputFormat { get; set; } = ProsperoOutputFormat.DebugImage;

	/// <summary>
	/// Trusted console/tooling boundary used only by <see cref="F:LibProsperoPkg.ProsperoOutputFormat.RetailImage" />.
	/// It produces the exact 0x300-byte standard Retail FIH material.
	/// </summary>
	public IProsperoRetailFinalizationProvider? RetailFinalizationProvider { get; set; }

	/// <summary>Folder whose contents become the package image (must contain <c>sce_sys/</c>).</summary>
	public string SourceFolder { get; set; } = "";

	/// <summary>Folder the finished <c>*.pkg</c> is written to.</summary>
	public string OutputFolder { get; set; } = "";

	/// <summary>36-character content id (e.g. <c>UP9000-PPSA00000_00-PROSPERO00000000</c>).</summary>
	public string ContentId { get; set; } = "";

	/// <summary>
	/// Optional 36-character primary package id. It is the identity used by publisher
	/// <c>ENTRY_KEYS</c> index 1 and the PFS-image-key KDF, and defaults to
	/// <see cref="P:LibProsperoPkg.ProsperoBuildOptions.ContentId" />.
	/// </summary>
	public string? PrimaryId { get; set; }

	/// <summary>32-character passcode. Defaults to all zeroes.</summary>
	public string Passcode { get; set; } = new string('0', 32);

	/// <summary>Human-readable title written into <c>param.json</c> when one is generated.</summary>
	public string Title { get; set; } = "";

	/// <summary>9-character title id (e.g. <c>PPSA00000</c>).</summary>
	public string TitleId { get; set; } = "";

	/// <summary>Content/master version, formatted <c>NN.NN</c>.</summary>
	public string Version { get; set; } = "01.00";

	/// <summary>
	/// UTC package creation time used consistently by PFS timestamps and the publisher
	/// <c>param.json/pubtools/creationDate</c> field.
	/// </summary>
	public DateTime TimeStamp { get; set; } = DateTime.UnixEpoch;

	/// <summary>When true a minimal <c>param.json</c> is generated if the source folder lacks one.</summary>
	public bool GenerateParamJsonIfMissing { get; set; } = true;

	/// <summary>When true disables staging aside any sce_sys or loose files (for already patched packages).</summary>
	public bool DisableQuarantine { get; set; }

	/// <summary>Cancellation token to abort the build.</summary>
	public System.Threading.CancellationToken CancellationToken { get; set; } = System.Threading.CancellationToken.None;

	/// <summary>
	/// When true the inner <c>pfs_image.dat</c> is stored PFSC-compressed (shrinking the package,
	/// the dominant size driver) instead of raw. Incompressible images fall back to the raw wrapper
	/// automatically. Off by default to preserve the size-stable path. This is the zlib
	/// PFSC used for the installable inner image; for the <c>nwonly</c> Kraken codec
	/// set <see cref="P:LibProsperoPkg.ProsperoBuildOptions.InnerCompression" /> to <see cref="F:LibProsperoPkg.PKG.ProsperoInnerCompression.Kraken" /> instead.
	/// </summary>
	public bool CompressInnerImage { get; set; }

	/// <summary>
	/// Selects the inner-image codec explicitly. When left at <see cref="F:LibProsperoPkg.PKG.ProsperoInnerCompression.None" />
	/// the legacy <see cref="P:LibProsperoPkg.ProsperoBuildOptions.CompressInnerImage" /> flag decides (true =&gt; <see cref="F:LibProsperoPkg.PKG.ProsperoInnerCompression.Zlib" />).
	/// When set to a non-<c>None</c> value this takes precedence over <see cref="P:LibProsperoPkg.ProsperoBuildOptions.CompressInnerImage" />:
	/// <list type="bullet">
	/// <item><see cref="F:LibProsperoPkg.PKG.ProsperoInnerCompression.Zlib" /> — zlib PFSC (installable inner image).</item>
	/// <item><see cref="F:LibProsperoPkg.PKG.ProsperoInnerCompression.Kraken" /> — PS5 PFSv3 Kraken (the
	/// <c>nwonly</c> inner-image codec), validated against reference output.
	/// Incompressible images fall back to the raw wrapper automatically.</item>
	/// </list>
	/// </summary>
	public ProsperoInnerCompression InnerCompression { get; set; }

	/// <summary>Zlib level used by the legacy whole-inner PFSC path (0..9, default 9).</summary>
	public int LegacyZlibCompressionLevel { get; set; } = 9;

	/// <summary>
	/// Maximum zlib workers used by the legacy whole-inner PFSC path. Zero (the default) uses
	/// the logical processor count.
	/// </summary>
	public int LegacyZlibMaxDegreeOfParallelism { get; set; }

	/// <summary>
	/// Build the publisher data-first outer PFS containing NAPS-packed, direct-offset PPR-PFS data.
	/// Enabled by default. Set false only for the legacy superblock-first/PFSC package profile.
	/// </summary>
	public bool UsePublisherPprNaps { get; set; } = true;

	/// <summary>
	/// Number of automatically generated PlayGo chunks. One preserves the verified publisher
	/// <c>nwonly</c> profile. Values above one create an experimental single-scenario layout whose
	/// physical main extent and AFID-ordered files are divided deterministically between chunks.
	/// </summary>
	public int PlayGoChunkCount { get; set; } = 1;

	/// <summary>
	/// Selects either the stock encrypted publisher image or the research-only plaintext/no-auth
	/// representation. The latter keeps structural SHA3 validation but requires the matching
	/// 9.40 PPR/PackageRead runtime selector and is not publisher-compatible.
	/// </summary>
	public ProsperoPublisherImageMode PublisherImageMode { get; set; }

	/// <summary>
	/// Optional 16-byte publishing CMAC key for NAPS outer-block digest slots. Keyed profiles
	/// require the matching key, while the verified Publishing Tools 2.79 debug/AC profile
	/// deliberately stores zero tags and passes the official host-side integrity verifier.
	/// </summary>
	public byte[]? NapsOuterBlockCmacKey { get; set; }

	/// <summary>
	/// Optional publisher-authored <c>common/etc/naps_meta_18.dat</c> SI payload. Publisher AC
	/// packages require this protected metric record; the library preserves it verbatim.
	/// </summary>
	public byte[]? NapsMeta18 { get; set; }

	/// <summary>
	/// Optional override for <c>ihsh/rhsh</c> and provider for the AES-XTS-derived
	/// <c>obcc</c> table inside <c>naps_meta_18.dat</c>. Ignored when
	/// <see cref="P:LibProsperoPkg.ProsperoBuildOptions.NapsMeta18" /> is supplied verbatim.
	/// </summary>
	public IProsperoNapsIntegrityProvider? NapsIntegrityProvider { get; set; }

	/// <summary>
	/// Optional exact path-to-AFID assignment for preserving a sparse publisher FIDX layout.
	/// Paths are rooted at the inner user root (for example <c>/data/file.bin</c>). Missing slot
	/// numbers become 256-KiB zero extents and <c>-1</c> AFID table entries.
	/// </summary>
	public IReadOnlyDictionary<string, uint>? PublisherAfidAssignments { get; set; }

	/// <summary>
	/// Optional expected 32-byte publisher <c>pfs-image-key</c>. The library derives this
	/// value locally from primary id, passcode and seed; when supplied, it is treated as a
	/// known-answer vector and must match.
	/// </summary>
	public byte[]? NapsPfsImageKey { get; set; }

	/// <summary>
	/// Optional raw 16-byte publisher <c>pfs-image-seed</c>. In the publisher profile this is
	/// also the outer-PFS seed
	/// stored at superblock offset <c>+0x370</c>. If <see cref="P:LibProsperoPkg.ProsperoBuildOptions.OuterPfsSeed" /> is supplied too,
	/// both values must be identical.
	/// </summary>
	public byte[]? NapsPfsImageSeed { get; set; }

	/// <summary>
	/// Optional publisher-authored raw <c>IMAGE_KEY</c> CNT entry (exactly <c>0x800</c> bytes)
	/// to preserve verbatim. When omitted, the library reproduces the native <c>sc2</c>
	/// RSA-3072 plus SHAKE128 construction from the locally derived PFS-image key.
	/// </summary>
	public byte[]? PublisherImageKey { get; set; }

	/// <summary>
	/// Optional publisher-authored raw <c>ENTRY_KEYS</c> CNT entry (exactly <c>0xB80</c> bytes).
	/// Supplying it preserves all seven RSA-3072 wrapped records verbatim for an exact rebuild.
	/// </summary>
	public byte[]? PublisherEntryKeys { get; set; }

	/// <summary>
	/// Optional fixed 16-byte outer-PFS seed. When omitted, the seed is derived in
	/// <see cref="P:LibProsperoPkg.ProsperoBuildOptions.DeterministicBuild" /> mode and generated with a cryptographic RNG otherwise.
	/// </summary>
	public byte[]? OuterPfsSeed { get; set; }

	/// <summary>
	/// Enables byte-reproducible package generation: stable RSA wrapping and a content-derived
	/// outer-PFS seed when <see cref="P:LibProsperoPkg.ProsperoBuildOptions.OuterPfsSeed" /> is omitted. The timestamp remains the explicit
	/// <see cref="P:LibProsperoPkg.ProsperoBuildOptions.TimeStamp" /> value (Unix epoch by default).
	/// </summary>
	public bool DeterministicBuild { get; set; }

	/// <summary>
	/// Legacy private-signing hook retained for source compatibility. Current publisher-compatible
	/// CNT generation does not use it: CNT+0x1000 is a deterministic RSA public-key wrap generated
	/// from the embedded sc2 passcode bank.
	/// </summary>
	public IProsperoMetadataSigner? MetadataSigner { get; set; }

	/// <summary>
	/// Optional provider for already-issued decrypted AC/AL <c>license.dat</c> and
	/// <c>license.info</c>. It takes precedence over loose sidecars in the source folder.
	/// The returned records are validated and CNT-encrypted by the builder.
	/// </summary>
	public IProsperoLicenseProvider? LicenseProvider { get; set; }

	/// <summary>
	/// Refuses to build unless every caller-supplied input required by the external Publishing
	/// Tools acceptance path is present. This checks availability, not whether a supplied signer
	/// or keyed provider belongs to a particular SDK trust domain; final acceptance is still
	/// established by <c>img_info</c>/<c>img_verify</c>.
	/// </summary>
	public bool RequirePublisherCompatibility { get; set; }

	/// <summary>
	/// Maximum number of CPU threads to use for outer block hashing (default: 2 to maintain low CPU thermals).
	/// </summary>
	public int MaxHashingThreads { get; set; } = 2;
}
