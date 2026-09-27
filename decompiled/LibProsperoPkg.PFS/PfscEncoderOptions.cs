using System;
using System.IO.Compression;

namespace LibProsperoPkg.PFS;

/// <summary>
/// Options controlling how a PFSC image is produced. Exposes the tunable
/// surface for PFSC encoding (<c>--block-size</c>, <c>--compression-level</c>,
/// <c>--threshold-gain</c>, <c>--min-compress-size</c>, worker count).
/// </summary>
public sealed class PfscEncoderOptions
{
	/// <summary>Logical PFSC block size. Must be a power of two between 4 KiB and 2 MiB. Default 64 KiB.</summary>
	public int BlockSize { get; set; } = 65536;

	/// <summary>zlib compression level used for each block (default: maximum).</summary>
	public CompressionLevel CompressionLevel { get; set; } = CompressionLevel.SmallestSize;

	/// <summary>
	/// Numeric zlib level (-1 = library default, 0 = stored, 1 = fastest, 9 = smallest).
	/// When set, this takes precedence over <see cref="P:LibProsperoPkg.PFS.PfscEncoderOptions.CompressionLevel" />.
	/// </summary>
	public int? ZlibLevel { get; set; }

	/// <summary>
	/// Minimum per-block gain percent (0-100) required to keep a block compressed.
	/// A block whose gain is below this stays raw. Default 0 (keep any gain).
	/// </summary>
	public int ThresholdGain { get; set; }

	/// <summary>
	/// Files smaller than this are stored raw (never PFSC-wrapped). Default 0.
	/// </summary>
	public long MinCompressSize { get; set; }

	/// <summary>
	/// Maximum number of zlib blocks compressed concurrently. The default is 1 for API
	/// compatibility; high-level builders opt into an automatic processor-count value.
	/// </summary>
	public int MaxDegreeOfParallelism { get; set; } = 1;

	/// <summary>Validates the option values, throwing on anything out of range.</summary>
	public void Validate()
	{
		if (BlockSize < 4096 || BlockSize > 2097152)
		{
			throw new ArgumentOutOfRangeException("BlockSize", BlockSize, "Block size must be between 4096 and 2097152.");
		}
		if ((BlockSize & (BlockSize - 1)) != 0)
		{
			throw new ArgumentException("Block size must be a power of two.", "BlockSize");
		}
		if (ThresholdGain < 0 || ThresholdGain > 100)
		{
			throw new ArgumentOutOfRangeException("ThresholdGain", ThresholdGain, "Threshold gain must be between 0 and 100.");
		}
		if (MinCompressSize < 0)
		{
			throw new ArgumentOutOfRangeException("MinCompressSize", MinCompressSize, "Minimum compress size must be non-negative.");
		}
		bool flag;
		switch (ZlibLevel)
		{
		default:
			flag = true;
			break;
		case null:
		case -1:
		case 0:
		case 1:
		case 2:
		case 3:
		case 4:
		case 5:
		case 6:
		case 7:
		case 8:
		case 9:
			flag = false;
			break;
		}
		if (flag)
		{
			throw new ArgumentOutOfRangeException("ZlibLevel", ZlibLevel, "Zlib level must be in the range -1..9.");
		}
		if (MaxDegreeOfParallelism < 1)
		{
			throw new ArgumentOutOfRangeException("MaxDegreeOfParallelism", MaxDegreeOfParallelism, "Parallelism must be at least 1.");
		}
	}
}
