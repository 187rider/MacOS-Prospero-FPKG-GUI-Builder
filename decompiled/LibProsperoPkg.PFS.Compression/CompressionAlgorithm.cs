namespace LibProsperoPkg.PFS.Compression;

/// <summary>
/// Compression algorithm recorded in the PFS compressed-file header.
/// Only <see cref="F:LibProsperoPkg.PFS.Compression.CompressionAlgorithm.Kraken" /> is supported for compressed-file creation.
/// </summary>
public enum CompressionAlgorithm
{
	/// <summary>Fast Zlib. <b>Not</b> supported for PFS compressed-file creation.</summary>
	QuickZ,
	/// <summary>High-quality Zlib. <b>Not</b> supported for PFS compressed-file creation.</summary>
	Zlib,
	/// <summary>Kraken. The only supported algorithm for PFS compressed-file creation.</summary>
	Kraken
}
