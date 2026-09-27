namespace LibProsperoPkg.PFS;

/// <summary>Statistics describing a completed PFSC encode.</summary>
public sealed class PfscEncodeStats
{
	/// <summary>Logical (uncompressed) size of the source payload.</summary>
	public long RawSize { get; init; }

	/// <summary>Size of the produced PFSC image (header + stored blocks).</summary>
	public long EncodedSize { get; init; }

	/// <summary>Total number of logical blocks.</summary>
	public long BlockCount { get; init; }

	/// <summary>Number of blocks that were stored compressed.</summary>
	public long CompressedBlocks { get; init; }

	/// <summary>True when the payload was stored raw (no PFSC wrapper) because compression gave no benefit.</summary>
	public bool StoredRaw { get; init; }

	/// <summary>Percentage saved relative to the raw size (negative if it grew).</summary>
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
