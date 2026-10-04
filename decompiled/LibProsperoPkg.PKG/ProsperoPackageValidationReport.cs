using System;
using System.Collections.Generic;

namespace LibProsperoPkg.PKG;

/// <summary>
/// Detailed diagnostic and verification report produced by <see cref="ProsperoPackageArchive.ValidatePackage"/>.
/// </summary>
public sealed class ProsperoPackageValidationReport
{
	/// <summary>
	/// Gets or sets whether all critical package structures, integrity check vectors,
	/// PlayGo metadata, and layout definitions are strictly valid.
	/// </summary>
	public bool IsValid { get; set; }

	/// <summary>
	/// Gets or sets the path of the validated package, if applicable.
	/// </summary>
	public string PackagePath { get; set; } = string.Empty;

	/// <summary>
	/// Gets or sets the container classification detected by <see cref="ProsperoPkgReader.DetectType(System.IO.Stream)"/>.
	/// </summary>
	public string ContainerType { get; set; } = "Unknown";

	/// <summary>
	/// Gets or sets the total size in bytes of the package file or stream.
	/// </summary>
	public long FileSize { get; set; }

	/// <summary>
	/// Gets or sets the Content ID recorded in the CNT container header.
	/// </summary>
	public string ContentId { get; set; } = string.Empty;

	/// <summary>
	/// Gets or sets the SDK version declared in param.json (e.g. "0x0400000000000000").
	/// </summary>
	public string? SdkVersion { get; set; }

	/// <summary>
	/// Gets or sets whether the SDK version is down-patched for FW 4.xx compatibility (&lt;= 0x0400000000000000).
	/// </summary>
	public bool IsDownpatched { get; set; }

	/// <summary>
	/// Gets or sets the requiredSystemSoftwareVersion declared in param.json.
	/// </summary>
	public string? RequiredSystemSoftwareVersion { get; set; }

	/// <summary>
	/// Gets or sets whether PlayGo chunk/manifest structures are valid and match the package Content ID.
	/// </summary>
	public bool PlayGoValid { get; set; }

	/// <summary>
	/// Gets or sets the Content ID embedded inside playgo-chunk.dat (if present).
	/// </summary>
	public string? PlayGoContentId { get; set; }

	/// <summary>
	/// Gets or sets the language/chunk mask found in playgo-chunk.dat.
	/// </summary>
	public ulong? PlayGoChunkMask { get; set; }

	/// <summary>
	/// Gets or sets whether the package contains a NAPS layout document (naps_pkg_layout.dat).
	/// </summary>
	public bool HasNapsLayout { get; set; }

	/// <summary>
	/// Gets or sets the verified number of compression/data spans in the NAPS plan.
	/// </summary>
	public int? NapsSpanCount { get; set; }

	/// <summary>
	/// Gets or sets the number of files indexed in the outer PFS and NAPS layout.
	/// </summary>
	public int? InnerFileCount { get; set; }

	/// <summary>
	/// Gets or sets whether the outer PFS superblock integrity check vector (ICV) passed.
	/// </summary>
	public bool OuterSuperblockValid { get; set; }

	/// <summary>
	/// Gets or sets the signed byte from the FIH header (0x00 for debug/fpkg).
	/// </summary>
	public byte SignedByte { get; set; }

	/// <summary>
	/// Gets or sets the outer PFS mode (0x000D for publisher outer PFS).
	/// </summary>
	public ushort OuterMode { get; set; }

	/// <summary>
	/// Gets or sets the outer PFS seed marker string (e.g. "PPRPLAIN-NOAUTH!").
	/// </summary>
	public string? SeedMarker { get; set; }

	/// <summary>
	/// Gets or sets the computed SHA-256 digest of the package, if requested.
	/// </summary>
	public string? Sha256 { get; set; }

	/// <summary>
	/// Gets the list of hard validation errors. If any error is present, <see cref="IsValid"/> will be false.
	/// </summary>
	public List<string> Errors { get; } = new();

	/// <summary>
	/// Gets the list of non-critical warnings (e.g. firmware version advisories).
	/// </summary>
	public List<string> Warnings { get; } = new();
}
