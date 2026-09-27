using System;
using System.IO.Compression;

namespace LibProsperoPkg.PFS;

/// <summary>Options for <see cref="T:LibProsperoPkg.PFS.ProsperoPfsc" /> pack operations.</summary>
public sealed class ProsperoPfscOptions
{
	/// <summary>Logical PFSC block size (power of two, 4 KiB - 2 MiB). Default 64 KiB.</summary>
	public int BlockSize { get; set; } = 65536;

	/// <summary>zlib level used per block. Default: maximum.</summary>
	public CompressionLevel CompressionLevel { get; set; } = CompressionLevel.SmallestSize;

	/// <summary>Minimum per-block gain percent (0-100) to keep a compressed block. Default 0.</summary>
	public int ThresholdGain { get; set; }

	/// <summary>Files smaller than this are stored raw. Default 0.</summary>
	public long MinCompressSize { get; set; }

	/// <summary>Maximum zlib workers. Zero (the default) uses the logical processor count.</summary>
	public int MaxDegreeOfParallelism { get; set; }

	internal PfscEncoderOptions ToEncoderOptions()
	{
		return new PfscEncoderOptions
		{
			BlockSize = BlockSize,
			CompressionLevel = CompressionLevel,
			ThresholdGain = ThresholdGain,
			MinCompressSize = MinCompressSize,
			MaxDegreeOfParallelism = ((MaxDegreeOfParallelism == 0) ? Math.Max(1, Environment.ProcessorCount) : MaxDegreeOfParallelism)
		};
	}
}
