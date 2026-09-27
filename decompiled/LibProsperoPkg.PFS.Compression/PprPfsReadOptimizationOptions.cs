using System.Collections.Generic;

namespace LibProsperoPkg.PFS.Compression;

/// <summary>Controls which regions of an inner PFS image remain raw inside PFSC v2.</summary>
public sealed class PprPfsReadOptimizationOptions
{
	/// <summary>Keep the superblock, inode table, directory data, and other pre-file metadata raw.</summary>
	public bool KeepMetadataRaw { get; set; } = true;

	/// <summary>Keep files no larger than this many bytes raw. Zero disables the size rule.</summary>
	public long SmallFileRawThreshold { get; set; } = 1048576L;

	/// <summary>Case-insensitive inner paths that should remain raw.</summary>
	public IReadOnlyCollection<string> RawFilePatterns { get; set; } = DefaultLatencySensitivePatterns;

	/// <summary>Force the entire logical image to raw PFSC entries.</summary>
	public bool ForceAllRaw { get; set; }

	/// <summary>Default startup-sensitive paths for a game image.</summary>
	public static IReadOnlyCollection<string> DefaultLatencySensitivePatterns { get; } = new string[3] { "eboot.bin", "sce_module/**", "sce_sys/**" };
}
