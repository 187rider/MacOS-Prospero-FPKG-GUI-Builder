using System;
using System.Collections.Generic;
using LibProsperoPkg.PFS.Compression;

namespace LibProsperoPkg.PFS;

/// <summary>Options controlling an inner-PFS layout build.</summary>
public sealed class ProsperoPfsLayoutOptions
{
	/// <summary>Filesystem block size in bytes. Default 64 KiB (the PS5 inner-PFS block size).</summary>
	public uint BlockSize { get; set; } = 65536u;

	/// <summary>
	/// Compress regular files as ppr_pfs-compatible PFSC v2 Kraken containers.
	/// Files are wrapped even when metadata overhead makes the stored container larger, matching
	/// publisher output. Set <see cref="P:LibProsperoPkg.PFS.ProsperoPfsLayoutOptions.KrakenOnlyWhenSmaller" /> for size-driven fallback.
	/// </summary>
	public bool CompressFilesWithKraken { get; set; }

	/// <summary>
	/// Selects per-file compression. <see cref="F:LibProsperoPkg.PFS.PfsFileCompressionMethod.None" /> stores files raw;
	/// the legacy <see cref="P:LibProsperoPkg.PFS.ProsperoPfsLayoutOptions.CompressFilesWithKraken" /> flag still selects Kraken when this remains None.
	/// </summary>
	public PfsFileCompressionMethod FileCompression { get; set; }

	/// <summary>
	/// Codec level. Kraken accepts -4..9 (publisher default 8); zlib accepts 0..9.
	/// </summary>
	public int CompressionLevel { get; set; } = 8;

	/// <summary>
	/// Maximum number of zlib compression workers. Zero (the default) uses the logical processor
	/// count. Multiple files are compressed concurrently; a single large file uses parallel blocks.
	/// </summary>
	public int ZlibMaxDegreeOfParallelism { get; set; }

	/// <summary>
	/// Maximum number of Kraken compression workers. Zero (the default) uses the logical processor
	/// count. Multiple files are compressed concurrently; a single large file uses parallel groups.
	/// </summary>
	public int KrakenMaxDegreeOfParallelism { get; set; }

	/// <summary>
	/// Selects which files are eligible for zlib. The aligned mode is intended for legacy classic
	/// PFS images whose runtime path expects whole 64-KiB logical blocks.
	/// </summary>
	public PfsZlibFileSelection ZlibFileSelection { get; set; }

	/// <summary>Kraken level recorded in PFSC v2 headers. Publisher-produced images use level 8.</summary>
	public int KrakenLevel
	{
		get
		{
			return CompressionLevel;
		}
		set
		{
			CompressionLevel = value;
		}
	}

	/// <summary>Smallest source file considered for Kraken compression.</summary>
	public long MinimumKrakenFileSize { get; set; }

	/// <summary>Keep a PFSC v2 file only when its complete stored size is smaller than the source.</summary>
	public bool KrakenOnlyWhenSmaller { get; set; }

	/// <summary>
	/// Minimum percentage a Kraken group must save. Lower-gain groups remain raw to reduce
	/// runtime decompression latency.
	/// </summary>
	public int KrakenMinimumSavingsPercent { get; set; }

	/// <summary>
	/// Optional logical raw-range provider for each file being wrapped in PFSC v2/Kraken.
	/// The callback receives a forward-slash relative path.
	/// </summary>
	public Func<string, IReadOnlyCollection<PprPfsRawRange>?>? KrakenRawRangeProvider { get; set; }

	/// <summary>Physically place latency-sensitive files before large sequential files.</summary>
	public bool OptimizeFileLayoutForReadSpeed { get; set; }

	/// <summary>Files at or below this size receive the early-layout priority.</summary>
	public long ReadPrioritySmallFileSize { get; set; } = 1048576L;

	/// <summary>Case-insensitive path globs receiving the highest early-layout priority.</summary>
	public IReadOnlyCollection<string> ReadPriorityPatterns { get; set; } = PprPfsReadOptimizationOptions.DefaultLatencySensitivePatterns;

	/// <summary>
	/// Use the publisher PPR-PFS outer layout: inode 0 is the user root and the inode table starts
	/// at block 2. This omits the classic super-root and flat-path-table wrapper.
	/// </summary>
	public bool UsePublisherPprLayout { get; set; }

	/// <summary>
	/// Remove sce_sys files that a full PKG build normally moves to outer container entries.
	/// Disable this when reproducing a standalone publisher PPR-PFS tree.
	/// </summary>
	public bool FilterOuterPackageEntries { get; set; } = true;

	/// <summary>
	/// Case-insensitive path globs excluded from Kraken compression. Paths use forward slashes;
	/// <c>*</c> matches within one component and <c>**</c> crosses directory separators.
	/// The default mirrors the reference image, where every file below <c>sce_sys</c> is raw.
	/// </summary>
	public IReadOnlyCollection<string> KrakenExcludePatterns { get; set; } = DefaultKrakenExcludePatterns;

	/// <summary>
	/// Alias for <see cref="P:LibProsperoPkg.PFS.ProsperoPfsLayoutOptions.KrakenExcludePatterns" /> used by all selected compression methods.
	/// </summary>
	public IReadOnlyCollection<string> CompressionExcludePatterns
	{
		get
		{
			return KrakenExcludePatterns;
		}
		set
		{
			KrakenExcludePatterns = value;
		}
	}

	/// <summary>Default publisher-style Kraken exclusions.</summary>
	public static IReadOnlyCollection<string> DefaultKrakenExcludePatterns { get; } = new string[1] { "sce_sys/**" };

	/// <summary>
	/// Timestamp written into the inode table. Defaults to the Unix epoch for reproducible output.
	/// </summary>
	public DateTime TimeStamp { get; set; } = DateTime.UnixEpoch;

	/// <summary>
	/// Case-insensitive file names that are skipped (project scaffolding that must never end up
	/// inside the image). Matches the default exclude masks the publishing tools use.
	/// </summary>
	public IReadOnlyCollection<string> ExcludeFileNames { get; set; } = DefaultExcludeFileNames;

	/// <summary>The default file-name exclude set (project files, intermediate caches, …).</summary>
	public static IReadOnlyCollection<string> DefaultExcludeFileNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "keystone", "disc_info.dat", "pfs-version.dat", "ext_info.dat" };

	/// <summary>File-name suffixes that are skipped (e.g. the project file itself).</summary>
	public IReadOnlyCollection<string> ExcludeFileSuffixes { get; set; } = DefaultExcludeFileSuffixes;

	/// <summary>The default file-suffix exclude set.</summary>
	public static IReadOnlyCollection<string> DefaultExcludeFileSuffixes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".gp4", ".gp5", ".esbak", ".dds" };
}
