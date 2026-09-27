namespace LibProsperoPkg.PFS;

/// <summary>The outcome of a pack operation.</summary>
public sealed class ProsperoPfscResult
{
	/// <summary>The path the PFSC image was written to.</summary>
	public required string OutputPath { get; init; }

	/// <summary>Logical (uncompressed) source size in bytes.</summary>
	public required long RawSize { get; init; }

	/// <summary>Size of the produced image in bytes.</summary>
	public required long EncodedSize { get; init; }

	/// <summary>True when the source was stored uncompressed because compression gave no benefit.</summary>
	public required bool StoredRaw { get; init; }

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
