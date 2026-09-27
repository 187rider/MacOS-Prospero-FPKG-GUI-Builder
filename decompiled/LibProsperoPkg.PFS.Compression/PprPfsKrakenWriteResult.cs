namespace LibProsperoPkg.PFS.Compression;

/// <summary>Statistics returned by <c>PprPfsKraken.Write</c>.</summary>
public sealed class PprPfsKrakenWriteResult
{
	/// <summary>Logical, decompressed file size.</summary>
	public required long UncompressedSize { get; init; }

	/// <summary>Complete PFSC v2 container size.</summary>
	public required long StoredSize { get; init; }

	/// <summary>Number of logical 128 KiB blocks.</summary>
	public required int BlockCount { get; init; }

	/// <summary>Number of blocks stored as Kraken streams.</summary>
	public required int CompressedBlockCount { get; init; }

	/// <summary>Number of incompressible blocks stored verbatim.</summary>
	public int StoredBlockCount => BlockCount - CompressedBlockCount;

	/// <summary>Number of blocks deliberately kept raw by a read-optimization range.</summary>
	public int ForcedRawBlockCount { get; init; }

	/// <summary>Number of blocks kept raw because Kraken did not save the configured minimum.</summary>
	public int LowGainRawBlockCount { get; init; }
}
