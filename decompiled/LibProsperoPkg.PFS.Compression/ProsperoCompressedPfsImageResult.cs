namespace LibProsperoPkg.PFS.Compression;

/// <summary>The outcome of a <see cref="T:LibProsperoPkg.PFS.Compression.ProsperoCompressedPfsImage" /> pack operation.</summary>
public sealed class ProsperoCompressedPfsImageResult
{
	/// <summary>The path the container was written to, or <c>null</c> for in-memory packs.</summary>
	public string? OutputPath { get; init; }

	/// <summary>Logical (uncompressed) source size in bytes.</summary>
	public required long RawSize { get; init; }

	/// <summary>Size of the produced container in bytes (including metadata).</summary>
	public required long EncodedSize { get; init; }

	/// <summary>The logical block size used, in bytes.</summary>
	public required int BlockSize { get; init; }

	/// <summary>Total number of logical blocks in the container.</summary>
	public required int BlockCount { get; init; }

	/// <summary>Number of blocks that were Kraken-compressed (the rest are stored uncompressed).</summary>
	public required int CompressedBlocks { get; init; }

	/// <summary>True when no block compressed below its stored size (the whole image is stored raw).</summary>
	public bool StoredRaw => CompressedBlocks == 0;

	/// <summary>Percentage saved relative to the source (negative if it grew).</summary>
	public double GainPercent
	{
		get
		{
			if (RawSize != 0L)
			{
				return (double)(RawSize - EncodedSize) / (double)RawSize * 100.0;
			}
			return 0.0;
		}
	}
}
