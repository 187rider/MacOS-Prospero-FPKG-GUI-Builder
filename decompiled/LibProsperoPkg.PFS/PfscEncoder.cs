using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PFS;

/// <summary>
/// Encodes raw data into a PFSC-compressed image. The header layout, offset
/// table and per-block compress/raw decision follow the PFSC payload layout,
/// and the output is decodable by <see cref="T:LibProsperoPkg.PFS.PFSCReader" />.
/// </summary>
public static class PfscEncoder
{
	private readonly record struct EncodedBlock(byte[] Data, int StoredLength, int SourceLength, bool IsCompressed);

	/// <summary>The 4-byte PFSC magic ('P','F','S','C').</summary>
	public const uint Magic = 1129530960u;

	private const int BlockOffsetsOffset = 1024;

	private const int InitialDataOffset = 65536;

	private const int OffsetEntrySize = 8;

	private const int Unk8 = 6;

	/// <summary>
	/// Returns the PFSC header size (including any extra blocks needed for a
	/// large offset table) for an image with <paramref name="blockCount" /> blocks.
	/// </summary>
	public static long HeaderSize(long blockCount, int blockSize)
	{
		if (blockCount < 0)
		{
			throw new ArgumentOutOfRangeException("blockCount");
		}
		if (blockSize <= 0 || (blockSize & (blockSize - 1)) != 0)
		{
			throw new ArgumentOutOfRangeException("blockSize", "PFSC block size must be a positive power of two.");
		}
		long num = checked((blockCount + 1) * 8);
		long num2 = 64512L;
		long num3 = Math.Max(0L, num - num2);
		long num4 = ((num3 > 0) ? ((num3 + blockSize - 1) / blockSize) : 0);
		return checked(65536 + num4 * blockSize);
	}

	/// <summary>
	/// Encodes <paramref name="raw" /> into a PFSC image returned as a byte array.
	/// When compression yields no benefit (e.g. tiny or incompressible payloads)
	/// the original bytes are returned unchanged and <c>StoredRaw</c> is set.
	/// </summary>
	public static byte[] Encode(byte[] raw, PfscEncoderOptions? options, out PfscEncodeStats stats)
	{
		ArgumentNullException.ThrowIfNull(raw, "raw");
		using MemoryStream input = new MemoryStream(raw, writable: false);
		using MemoryStream memoryStream = new MemoryStream();
		stats = Encode(input, raw.Length, memoryStream, options);
		return stats.StoredRaw ? raw : memoryStream.ToArray();
	}

	/// <summary>
	/// Streams a PFSC image of the first <paramref name="length" /> bytes of
	/// <paramref name="input" /> into <paramref name="output" />.
	/// </summary>
	/// <remarks>
	/// <paramref name="output" /> must be seekable: the encoder reserves the
	/// header, streams each block (holding only one block in memory at a time)
	/// and then rewrites the header with the final offset table. This keeps the
	/// memory footprint flat even for multi-gigabyte images.
	/// </remarks>
	/// <returns>Statistics describing the encode.</returns>
	public static PfscEncodeStats Encode(Stream input, long length, Stream output, PfscEncoderOptions? options = null, Action<long>? progress = null)
	{
		ArgumentNullException.ThrowIfNull(input, "input");
		ArgumentNullException.ThrowIfNull(output, "output");
		if (length < 0)
		{
			throw new ArgumentOutOfRangeException("length");
		}
		if (options == null)
		{
			options = new PfscEncoderOptions();
		}
		options.Validate();
		if (!input.CanRead)
		{
			throw new ArgumentException("Input stream must be readable.", "input");
		}
		if (!input.CanSeek)
		{
			throw new ArgumentException("Input stream must be seekable so a non-beneficial PFSC encode can fall back to raw data.", "input");
		}
		if (!output.CanWrite)
		{
			throw new ArgumentException("Output stream must be writable.", "output");
		}
		if (!output.CanSeek)
		{
			throw new ArgumentException("Output stream must be seekable.", "output");
		}
		long position = input.Position;
		if (length > input.Length - position)
		{
			throw new EndOfStreamException("The requested PFSC input length exceeds the remaining stream data.");
		}
		int blockSize = options.BlockSize;
		if (length == 0L || length < options.MinCompressSize)
		{
			CopyExact(input, output, length);
			return new PfscEncodeStats
			{
				RawSize = length,
				EncodedSize = length,
				BlockCount = 0L,
				CompressedBlocks = 0L,
				StoredRaw = true
			};
		}
		long num = checked(length + blockSize - 1) / blockSize;
		long num2 = HeaderSize(num, blockSize);
		long position2 = output.Position;
		if (num >= int.MaxValue)
		{
			throw new ArgumentOutOfRangeException("length", "PFSC block count exceeds the supported table size.");
		}
		long[] offsets = new long[checked((int)num + 1)];
		offsets[0] = num2;
		output.Position = position2 + num2;
		long compressedBlocks = 0L;
		long produced = 0L;
		int requiredSavings = checked(blockSize * options.ThresholdGain + 99) / 100;
		Queue<Task<EncodedBlock>> pending;
		long nextOutputBlock;
		if (options.MaxDegreeOfParallelism == 1 || num == 1)
		{
			byte[] array = new byte[blockSize];
			using MemoryStream memoryStream = new MemoryStream(blockSize);
			for (long num3 = 0L; num3 < num; num3++)
			{
				int num4 = checked((int)Math.Min(blockSize, length - produced));
				int num5 = ReadUpTo(input, array, 0, num4);
				if (num5 != num4)
				{
					throw new EndOfStreamException("Unexpected end of input while encoding a PFSC block.");
				}
				if (num5 < blockSize)
				{
					Array.Clear(array, num5, blockSize - num5);
				}
				int num6 = Deflate(array, blockSize, options.CompressionLevel, options.ZlibLevel, memoryStream);
				int num7;
				if (num6 < blockSize && num6 <= blockSize - requiredSavings)
				{
					output.Write(memoryStream.GetBuffer(), 0, num6);
					num7 = num6;
					compressedBlocks++;
				}
				else
				{
					output.Write(array, 0, blockSize);
					num7 = blockSize;
				}
				offsets[checked((int)num3 + 1)] = offsets[checked((int)num3)] + num7;
				produced += num5;
				progress?.Invoke(produced);
			}
		}
		else
		{
			pending = new Queue<Task<EncodedBlock>>(options.MaxDegreeOfParallelism);
			nextOutputBlock = 0L;
			for (long num8 = 0L; num8 < num; num8++)
			{
				int num9 = checked((int)Math.Min(blockSize, length - num8 * blockSize));
				byte[] block = new byte[blockSize];
				int filled = ReadUpTo(input, block, 0, num9);
				if (filled != num9)
				{
					throw new EndOfStreamException("Unexpected end of input while encoding a PFSC block.");
				}
				pending.Enqueue(Task.Run(() => EncodeBlock(block, filled, blockSize, requiredSavings, options.CompressionLevel, options.ZlibLevel)));
				if (pending.Count >= options.MaxDegreeOfParallelism)
				{
					WriteNextParallelBlock();
				}
			}
			while (pending.Count != 0)
			{
				WriteNextParallelBlock();
			}
		}
		long num10 = offsets[checked((int)num)];
		if (compressedBlocks == 0L || num10 >= length)
		{
			output.Position = position2;
			output.SetLength(position2);
			input.Position = position;
			CopyExact(input, output, length);
			return new PfscEncodeStats
			{
				RawSize = length,
				EncodedSize = length,
				BlockCount = num,
				CompressedBlocks = 0L,
				StoredRaw = true
			};
		}
		output.Position = position2;
		WriteHeader(output, blockSize, num2, num, offsets);
		output.Position = position2 + num10;
		output.SetLength(position2 + num10);
		return new PfscEncodeStats
		{
			RawSize = length,
			EncodedSize = num10,
			BlockCount = num,
			CompressedBlocks = compressedBlocks,
			StoredRaw = false
		};
		void WriteNextParallelBlock()
		{
			EncodedBlock result = pending.Dequeue().GetAwaiter().GetResult();
			output.Write(result.Data, 0, result.StoredLength);
			offsets[checked((int)nextOutputBlock + 1)] = offsets[checked((int)nextOutputBlock)] + result.StoredLength;
			if (result.IsCompressed)
			{
				compressedBlocks++;
			}
			produced += result.SourceLength;
			progress?.Invoke(produced);
			nextOutputBlock++;
		}
	}

	private static void WriteHeader(Stream s, int blockSize, long headerSize, long blockCount, IReadOnlyList<long> offsets)
	{
		long position = s.Position;
		s.WriteUInt32LE(1129530960u);
		s.WriteInt32LE(0);
		s.WriteInt32LE(6);
		s.WriteInt32LE(blockSize);
		s.WriteInt64LE(blockSize);
		s.WriteInt64LE(1024L);
		s.WriteInt64LE(headerSize);
		s.WriteInt64LE(blockCount * blockSize);
		s.Position = position + 1024;
		for (long num = 0L; num <= blockCount; num++)
		{
			s.WriteInt64LE(offsets[(int)num]);
		}
	}

	private static int Deflate(byte[] data, int count, CompressionLevel level, int? zlibLevel, MemoryStream destination)
	{
		destination.Position = 0L;
		destination.SetLength(0L);
		using (ZLibStream zLibStream = (zlibLevel.HasValue ? new ZLibStream(destination, new ZLibCompressionOptions
		{
			CompressionLevel = zlibLevel.Value
		}, leaveOpen: true) : new ZLibStream(destination, level, leaveOpen: true)))
		{
			zLibStream.Write(data, 0, count);
		}
		return checked((int)destination.Length);
	}

	private static EncodedBlock EncodeBlock(byte[] block, int sourceLength, int blockSize, int requiredSavings, CompressionLevel level, int? zlibLevel)
	{
		using MemoryStream memoryStream = new MemoryStream(blockSize);
		int num = Deflate(block, blockSize, level, zlibLevel, memoryStream);
		return (num < blockSize && num <= blockSize - requiredSavings) ? new EncodedBlock(memoryStream.ToArray(), num, sourceLength, IsCompressed: true) : new EncodedBlock(block, blockSize, sourceLength, IsCompressed: false);
	}

	private static int ReadUpTo(Stream s, byte[] buffer, int offset, int count)
	{
		int i;
		int num;
		for (i = 0; i < count; i += num)
		{
			num = s.Read(buffer, offset + i, count - i);
			if (num == 0)
			{
				break;
			}
		}
		return i;
	}

	private static void CopyExact(Stream input, Stream output, long count)
	{
		if (count <= 0)
		{
			return;
		}
		byte[] array = new byte[Math.Min(count, 1048576L)];
		long num = count;
		while (num > 0)
		{
			int count2 = (int)Math.Min(num, array.Length);
			int num2 = ReadUpTo(input, array, 0, count2);
			if (num2 == 0)
			{
				throw new EndOfStreamException("Unexpected end of input while copying raw payload.");
			}
			output.Write(array, 0, num2);
			num -= num2;
		}
	}

	/// <summary>
	/// Returns true when a file should be kept raw (never PFSC-compressed)
	/// because it is an executable/already-packed payload.
	/// </summary>
	/// <param name="fileName">The base file name (e.g. <c>eboot.bin</c>).</param>
	/// <param name="relativePath">The file's path within the image (e.g. <c>sce_module/foo.prx</c>).</param>
	public static bool ShouldSkipExecutableCompression(string fileName, string relativePath)
	{
		string text = (fileName ?? string.Empty).ToLowerInvariant();
		string text2 = (relativePath ?? string.Empty).ToLowerInvariant().Replace('\\', '/');
		if ((!text.StartsWith("eboot", StringComparison.Ordinal) || !text.EndsWith(".bin", StringComparison.Ordinal)) && (!text.StartsWith("param", StringComparison.Ordinal) || !text.EndsWith(".sfx", StringComparison.Ordinal)) && !text.EndsWith(".prx", StringComparison.Ordinal) && !text.EndsWith(".sprx", StringComparison.Ordinal) && !text.EndsWith(".json", StringComparison.Ordinal) && !text.EndsWith(".txt", StringComparison.Ordinal) && !text.EndsWith(".png", StringComparison.Ordinal) && !text.EndsWith("keystone", StringComparison.Ordinal) && !text2.Contains("sce_module", StringComparison.Ordinal))
		{
			return text2.Contains("sce_sys", StringComparison.Ordinal);
		}
		return true;
	}
}
