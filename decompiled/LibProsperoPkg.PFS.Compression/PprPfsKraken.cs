using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using LibProsperoPkg.PFS.Compression.Oodle;

namespace LibProsperoPkg.PFS.Compression;

/// <summary>
/// Creates the legacy PFSC version-2 Kraken files embedded in a PPR-PFS filesystem.
/// This is the layout used by the kernel's <c>ppr_pfs_cmp_bread</c> path, not the
/// section-directory PFSC version-3 layout used by NAPS package metadata.
/// </summary>
public static class PprPfsKraken
{
	private readonly record struct KrakenGroupResult(byte[] Input, int GroupSize, bool ForceRaw, bool CandidateSmaller, bool Compressed, EncodedBlock? Encoded);

	/// <summary>PFSC magic (<c>"PFSC"</c> as a little-endian integer).</summary>
	public const uint Magic = 1129530960u;

	/// <summary>PFSC container version used by per-file publisher PPR-PFS compression.</summary>
	public const uint Version = 2u;

	/// <summary>The only block size accepted by the ppr_pfs Kraken path.</summary>
	public const int BlockSize = 131072;

	public const int CompressionGroupSize = 262144;

	/// <summary>Default encoder level observed in publisher-produced PFSC v2 files.</summary>
	public const int DefaultLevel = 8;

	private const int HeaderSize = 1024;

	private const int TableAlignment = 32;

	private const ulong Low48Mask = 281474976710655uL;

	private const ulong GroupBoundaryFlag = 32768uL;

	private const ulong CompressedFlag = 16384uL;

	private static readonly byte[] EncoderIdentifier = new byte[16]
	{
		213, 117, 80, 251, 41, 34, 118, 191, 87, 174,
		174, 234, 38, 49, 147, 162
	};

	/// <summary>Compresses an input file into a ppr_pfs-compatible PFSC v2 file.</summary>
	/// <param name="inputPath">Path to the logical input file.</param>
	/// <param name="outputPath">Destination PFSC v2 path.</param>
	/// <param name="level">Kraken level recorded in the header. Publisher output normally uses 8.</param>
	/// <param name="verifyBlocks">Decode every emitted Kraken block and compare it with the source.</param>
	/// <returns>Container and block statistics.</returns>
	public static PprPfsKrakenWriteResult PackFile(string inputPath, string outputPath, int level = 8, bool verifyBlocks = false)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(inputPath, "inputPath");
		ArgumentException.ThrowIfNullOrWhiteSpace(outputPath, "outputPath");
		return PackFile(inputPath, outputPath, new PprPfsKrakenWriteOptions
		{
			Level = level,
			VerifyBlocks = verifyBlocks
		});
	}

	/// <summary>Compresses a file with explicit runtime-read optimization options.</summary>
	public static PprPfsKrakenWriteResult PackFile(string inputPath, string outputPath, PprPfsKrakenWriteOptions options)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(inputPath, "inputPath");
		ArgumentException.ThrowIfNullOrWhiteSpace(outputPath, "outputPath");
		ArgumentNullException.ThrowIfNull(options, "options");
		string fullPath = Path.GetFullPath(inputPath);
		string fullPath2 = Path.GetFullPath(outputPath);
		if (PathsEqual(fullPath, fullPath2))
		{
			throw new IOException("PFSC input and output paths must be different.");
		}
		string? obj = Path.GetDirectoryName(fullPath2) ?? Directory.GetCurrentDirectory();
		Directory.CreateDirectory(obj);
		string text = Path.Combine(obj, ".pfsc-write-" + Guid.NewGuid().ToString("N") + ".tmp");
		try
		{
			PprPfsKrakenWriteResult result;
			using (FileStream fileStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.SequentialScan))
			{
				using FileStream destination = new FileStream(text, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1048576, FileOptions.SequentialScan);
				result = Write(fileStream, fileStream.Length, destination, options);
			}
			File.Move(text, fullPath2, overwrite: true);
			return result;
		}
		finally
		{
			try
			{
				File.Delete(text);
			}
			catch
			{
			}
		}
	}

	/// <summary>Decompresses a standalone PFSC v2 file.</summary>
	/// <param name="inputPath">PFSC v2 input path.</param>
	/// <param name="outputPath">Destination logical file path.</param>
	/// <returns>Number of decompressed bytes written.</returns>
	public static long UnpackFile(string inputPath, string outputPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(inputPath, "inputPath");
		ArgumentException.ThrowIfNullOrWhiteSpace(outputPath, "outputPath");
		string fullPath = Path.GetFullPath(inputPath);
		string fullPath2 = Path.GetFullPath(outputPath);
		if (PathsEqual(fullPath, fullPath2))
		{
			throw new IOException("PFSC input and output paths must be different.");
		}
		string? obj = Path.GetDirectoryName(fullPath2) ?? Directory.GetCurrentDirectory();
		Directory.CreateDirectory(obj);
		string text = Path.Combine(obj, ".pfsc-unpack-" + Guid.NewGuid().ToString("N") + ".tmp");
		try
		{
			long result;
			using (FileStream source = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.SequentialScan))
			{
				using FileStream destination = new FileStream(text, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1048576, FileOptions.SequentialScan);
				result = Unpack(source, 0L, destination);
			}
			File.Move(text, fullPath2, overwrite: true);
			return result;
		}
		finally
		{
			try
			{
				File.Delete(text);
			}
			catch
			{
			}
		}
	}

	/// <summary>Decompresses a PFSC v2 container embedded at an arbitrary stream offset.</summary>
	/// <param name="source">Seekable stream containing the PFSC v2 bytes.</param>
	/// <param name="containerOffset">Absolute stream offset of the <c>PFSC</c> magic.</param>
	/// <param name="destination">Writable destination for the logical file.</param>
	/// <returns>Number of decompressed bytes written.</returns>
	public static long Unpack(Stream source, long containerOffset, Stream destination)
	{
		ArgumentNullException.ThrowIfNull(source, "source");
		ArgumentNullException.ThrowIfNull(destination, "destination");
		ArgumentOutOfRangeException.ThrowIfNegative(containerOffset, "containerOffset");
		if (!source.CanRead || !source.CanSeek)
		{
			throw new ArgumentException("Source stream must be readable and seekable.", "source");
		}
		if (!destination.CanWrite)
		{
			throw new ArgumentException("Destination stream must be writable.", "destination");
		}
		if (containerOffset > source.Length - 128)
		{
			throw new InvalidDataException("PFSC header extends past the source stream.");
		}
		byte[] array = new byte[128];
		source.Position = containerOffset;
		ReadExactly(source, array, array.Length);
		ReadOnlySpan<byte> readOnlySpan = array;
		uint num = BinaryPrimitives.ReadUInt32LittleEndian(readOnlySpan.Slice(0));
		uint num2 = BinaryPrimitives.ReadUInt32LittleEndian(readOnlySpan.Slice(4));
		uint num3 = BinaryPrimitives.ReadUInt32LittleEndian(readOnlySpan.Slice(8));
		uint num4 = BinaryPrimitives.ReadUInt32LittleEndian(readOnlySpan.Slice(12));
		uint num5 = BinaryPrimitives.ReadUInt32LittleEndian(readOnlySpan.Slice(16));
		int num6;
		long num7;
		long num8;
		long num9;
		long num10;
		checked
		{
			num6 = (int)BinaryPrimitives.ReadUInt32LittleEndian(readOnlySpan.Slice(20));
			num7 = (long)BinaryPrimitives.ReadUInt64LittleEndian(readOnlySpan.Slice(24));
			num8 = (long)BinaryPrimitives.ReadUInt64LittleEndian(readOnlySpan.Slice(32));
			num9 = (long)BinaryPrimitives.ReadUInt64LittleEndian(readOnlySpan.Slice(48));
			num10 = (long)BinaryPrimitives.ReadUInt64LittleEndian(readOnlySpan.Slice(64));
			if (num != 1129530960 || num2 != 2 || num3 != 2 || num4 != 131072 || num5 != 32)
			{
				throw new InvalidDataException($"Unsupported PFSC header: magic=0x{num:X8}, version={num2}, algorithm={num3}, block=0x{num4:X}.");
			}
		}
		if (num6 != Math.Max(1L, (num7 + 131072 - 1) / 131072))
		{
			throw new InvalidDataException("PFSC block count does not match the logical size.");
		}
		long num11 = Align(1024 + checked(unchecked((long)checked(num6 + 1)) * 8L), 32);
		if (num9 != 1024 || num10 != num11 || num8 < num10 || num8 > source.Length - containerOffset)
		{
			throw new InvalidDataException("PFSC offset table or data offset is invalid.");
		}
		byte[] array2 = new byte[checked((num6 + 1) * 8)];
		source.Position = containerOffset + num9;
		ReadExactly(source, array2, array2.Length);
		if (BinaryPrimitives.ReadUInt64LittleEndian(array2.AsSpan(num6 * 8)) != (ulong)(long.MinValue | num8))
		{
			throw new InvalidDataException("PFSC final offset-table entry is invalid.");
		}
		byte[] array3 = new byte[262144];
		long num12 = 0L;
		int num13 = 0;
		while (num13 < num6)
		{
			ulong num14 = BinaryPrimitives.ReadUInt64LittleEndian(array2.AsSpan(num13 * 8));
			ulong num15 = BinaryPrimitives.ReadUInt64LittleEndian(array2.AsSpan((num13 + 1) * 8));
			long num16;
			long num17;
			ushort num18;
			checked
			{
				num16 = (long)(num14 & 0xFFFFFFFFFFFFL);
				num17 = (long)(num15 & 0xFFFFFFFFFFFFL);
				if (num16 < num10 || num17 < num16 || num17 > num8)
				{
					throw new InvalidDataException($"PFSC block {num13} has an invalid stored range.");
				}
				num18 = (ushort)(num14 >> 48);
			}
			if (((ulong)num18 & 0x4000uL) != 0)
			{
				bool flag = (ulong)num18 == 49152;
				bool flag2 = num13 + 1 < num6 && (ulong)(ushort)(num15 >> 48) == 16384;
				if (!flag)
				{
					throw new InvalidDataException($"PFSC block {num13} is an orphaned Kraken continuation.");
				}
				int num19 = ((!flag2) ? 1 : 2);
				int num21;
				checked
				{
					long num20 = (long)(BinaryPrimitives.ReadUInt64LittleEndian(MemoryExtensions.AsSpan(array2, unchecked((num13 + num19) * 8))) & 0xFFFFFFFFFFFFL);
					if (num20 <= num16 || num20 > num8)
					{
						throw new InvalidDataException($"PFSC Kraken group at block {num13} has an invalid stored range.");
					}
					byte[] array4 = new byte[(int)(num20 - num16)];
					source.Position = unchecked(containerOffset + num16);
					ReadExactly(source, array4, array4.Length);
					num21 = (int)Math.Min(unchecked((long)num19) * 131072L, num7 - num12);
					int flags = ((num19 == 2) ? 34 : 2);
					int firstChunkComp = ((num19 == 2) ? ((int)(num17 - num16)) : 0);
					Span<byte> span = array3.AsSpan(0, num21);
					KrakenDecodeStatus krakenDecodeStatus = KrakenDecoder.DecodeBlock(array4, flags, firstChunkComp, span);
					if (krakenDecodeStatus != KrakenDecodeStatus.Success)
					{
						throw new InvalidDataException($"PFSC Kraken group at block {num13} decode failed ({krakenDecodeStatus}).");
					}
					destination.Write(span);
				}
				num12 += num21;
				num13 += num19;
				continue;
			}
			if ((ulong)num18 != 32768)
			{
				throw new InvalidDataException($"PFSC block {num13} has unsupported flags 0x{num18:X4}.");
			}
			int num22;
			int num23;
			byte[] array5;
			checked
			{
				num22 = (int)(num17 - num16);
				num23 = (int)Math.Min(131072L, num7 - num12);
				array5 = new byte[num22];
			}
			source.Position = containerOffset + num16;
			ReadExactly(source, array5, array5.Length);
			if (num7 == 0L && num13 == 0)
			{
				if (num22 != 131072 || Array.Exists(array5, (byte value) => value != 0))
				{
					throw new InvalidDataException("An empty PFSC file must contain one stored zero block.");
				}
			}
			else
			{
				if (num22 != num23)
				{
					throw new InvalidDataException($"PFSC block {num13} stored size does not match its logical size.");
				}
				destination.Write(array5);
			}
			num12 += num23;
			num13++;
		}
		if (num12 != num7)
		{
			throw new InvalidDataException($"PFSC produced {num12:N0} bytes, expected {num7:N0} bytes.");
		}
		return num12;
	}

	/// <summary>
	/// Writes a seekable PFSC v2 container with a bounded working set proportional to the selected
	/// worker count.
	/// </summary>
	/// <param name="source">Readable source stream positioned at the logical file start.</param>
	/// <param name="length">Number of logical bytes to consume.</param>
	/// <param name="destination">Seekable, writable destination stream.</param>
	/// <param name="level">Kraken level stored at header byte <c>+0x29</c>.</param>
	/// <param name="verifyBlocks">Decode and byte-compare each emitted compressed block.</param>
	/// <returns>Container and block statistics.</returns>
	/// <exception cref="T:System.IO.InvalidDataException">The source ends early or an encoded block fails verification.</exception>
	public static PprPfsKrakenWriteResult Write(Stream source, long length, Stream destination, int level = 8, bool verifyBlocks = false)
	{
		return Write(source, length, destination, new PprPfsKrakenWriteOptions
		{
			Level = level,
			VerifyBlocks = verifyBlocks
		});
	}

	/// <summary>Writes PFSC v2 using explicit runtime-read optimization options.</summary>
	public static PprPfsKrakenWriteResult Write(Stream source, long length, Stream destination, PprPfsKrakenWriteOptions options)
	{
		ArgumentNullException.ThrowIfNull(source, "source");
		ArgumentNullException.ThrowIfNull(destination, "destination");
		ArgumentNullException.ThrowIfNull(options, "options");
		ArgumentOutOfRangeException.ThrowIfNegative(length, "length");
		if (!source.CanRead)
		{
			throw new ArgumentException("Source stream must be readable.", "source");
		}
		if (!destination.CanWrite || !destination.CanSeek)
		{
			throw new ArgumentException("Destination stream must be writable and seekable.", "destination");
		}
		int level = options.Level;
		if ((level < -4 || level > 9) ? true : false)
		{
			throw new ArgumentOutOfRangeException("Level", "Kraken level must be in the range -4..9.");
		}
		level = options.MinimumSavingsPercent;
		if ((level < 0 || level > 100) ? true : false)
		{
			throw new ArgumentOutOfRangeException("MinimumSavingsPercent", "Minimum savings must be in the range 0..100 percent.");
		}
		if (options.MaxDegreeOfParallelism < 1)
		{
			throw new ArgumentOutOfRangeException("MaxDegreeOfParallelism", "Parallelism must be at least 1.");
		}
		long num = (source.CanSeek ? source.Position : 0);
		int num2;
		long num3;
		checked
		{
			num2 = Math.Max(1, (int)unchecked(checked(length + 131072 - 1) / 131072));
			num3 = unchecked((long)checked(num2 + 1)) * 8L;
		}
		long num4 = Align(1024 + num3, 32);
		if (num4 > 281474976710655L)
		{
			throw new ArgumentOutOfRangeException("length", "PFSC metadata exceeds the 48-bit offset field.");
		}
		List<ulong> offsets = new List<ulong>(num2 + 1);
		PprPfsRawRange[] array = NormalizeRawRanges(options.RawRanges, length);
		int i = 0;
		int compressedBlocks = 0;
		int forcedRawBlocks = 0;
		int lowGainRawBlocks = 0;
		long num5 = 0L;
		destination.Position = num4;
		int num6;
		byte[] array2;
		Queue<Task<KrakenGroupResult>> queue;
		checked
		{
			num6 = Math.Max(1, (int)unchecked(checked(length + 262144 - 1) / 262144));
			array2 = ((options.MaxDegreeOfParallelism == 1) ? new byte[262144] : null);
			queue = new Queue<Task<KrakenGroupResult>>(options.MaxDegreeOfParallelism);
		}
		for (int j = 0; j < num6; j++)
		{
			int groupSize;
			checked
			{
				groupSize = (int)Math.Min(262144L, length - num5);
				byte[] inputBuffer = array2 ?? new byte[262144];
				ReadExactly(source, inputBuffer, groupSize);
				long num7 = num5;
				long num8 = num7 + groupSize;
				for (; i < array.Length && array[i].Offset + array[i].Length <= num7; i = unchecked(i + 1))
				{
				}
				bool forceRaw = i < array.Length && array[i].Offset < num8;
				if (options.MaxDegreeOfParallelism == 1)
				{
					WriteGroup(EncodeGroup(inputBuffer, groupSize, forceRaw, options));
				}
				else
				{
					queue.Enqueue(Task.Run(() => EncodeGroup(inputBuffer, groupSize, forceRaw, options)));
					if (queue.Count >= options.MaxDegreeOfParallelism)
					{
						WriteGroup(queue.Dequeue().GetAwaiter().GetResult());
					}
				}
			}
			num5 += groupSize;
		}
		while (queue.Count != 0)
		{
			WriteGroup(queue.Dequeue().GetAwaiter().GetResult());
		}
		if (offsets.Count != num2)
		{
			throw new InvalidDataException("PFSC encoder produced an unexpected number of block boundaries.");
		}
		long position = destination.Position;
		if ((ulong)position > 281474976710655uL)
		{
			throw new InvalidDataException("PFSC payload exceeds the 48-bit offset field.");
		}
		offsets.Add((ulong)(long.MinValue | position));
		byte[] array3 = new byte[1024];
		Span<byte> span = array3;
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0), 1129530960u);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4), 2u);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8), 2u);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12), 131072u);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(16), 32u);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(20), (uint)num2);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(24), (ulong)length);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(32), (ulong)position);
		ulong value = 2 | ((ulong)(byte)(sbyte)options.Level << 8) | 0x120000;
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(40), value);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(48), 1024uL);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(64), (ulong)num4);
		EncoderIdentifier.CopyTo(span.Slice(120));
		destination.Position = 0L;
		destination.Write(array3);
		Span<byte> span2 = stackalloc byte[8];
		foreach (ulong item in offsets)
		{
			BinaryPrimitives.WriteUInt64LittleEndian(span2, item);
			destination.Write(span2);
		}
		if (destination.Position < num4)
		{
			Span<byte> span3 = stackalloc byte[32];
			destination.Write(span3.Slice(0, checked((int)(num4 - destination.Position))));
		}
		destination.SetLength(position);
		destination.Position = position;
		if (source.CanSeek && source.Position != num + length)
		{
			throw new InvalidDataException("PFSC writer consumed an unexpected number of source bytes.");
		}
		return new PprPfsKrakenWriteResult
		{
			UncompressedSize = length,
			StoredSize = position,
			BlockCount = num2,
			CompressedBlockCount = compressedBlocks,
			ForcedRawBlockCount = forcedRawBlocks,
			LowGainRawBlockCount = lowGainRawBlocks
		};
		void WriteGroup(KrakenGroupResult result)
		{
			int num9 = Math.Max(1, (result.GroupSize + 131072 - 1) / 131072);
			if (result.ForceRaw)
			{
				forcedRawBlocks += num9;
			}
			else if (result.CandidateSmaller && !result.Compressed)
			{
				lowGainRawBlocks += num9;
			}
			if (result.Compressed)
			{
				EncodedBlock value2 = result.Encoded.Value;
				byte[] payload = value2.Payload;
				bool flag = result.GroupSize > 131072;
				AddOffset(offsets, destination.Position, 49152uL);
				if (flag)
				{
					destination.Write(payload.AsSpan(0, value2.FirstChunkCompSize));
					AddOffset(offsets, destination.Position, 16384uL);
					destination.Write(payload.AsSpan(value2.FirstChunkCompSize));
					compressedBlocks += 2;
				}
				else
				{
					destination.Write(payload);
					compressedBlocks++;
				}
			}
			else if (result.GroupSize == 0)
			{
				AddOffset(offsets, destination.Position, 32768uL);
				destination.Write(result.Input.AsSpan(0, 131072));
			}
			else
			{
				int num10;
				for (int k = 0; k < result.GroupSize; k += num10)
				{
					num10 = Math.Min(131072, result.GroupSize - k);
					AddOffset(offsets, destination.Position, 32768uL);
					destination.Write(result.Input.AsSpan(k, num10));
				}
			}
		}
	}

	private static KrakenGroupResult EncodeGroup(byte[] input, int groupSize, bool forceRaw, PprPfsKrakenWriteOptions options)
	{
		ReadOnlySpan<byte> readOnlySpan = input.AsSpan(0, groupSize);
		EncodedBlock? encodedBlock = (((groupSize == 0) | forceRaw) ? ((EncodedBlock?)null) : OodleKrakenEncoder.EncodeBlock(readOnlySpan, useHuffmanArrays: true, options.Level, allowSubLiterals: false, allowStoredHalves: false));
		int num = checked(groupSize * options.MinimumSavingsPercent + 99) / 100;
		int num2 = encodedBlock?.Payload.Length ?? int.MaxValue;
		bool flag = encodedBlock.HasValue && num2 < groupSize;
		bool flag2 = flag && num2 <= groupSize - num;
		if (flag2)
		{
			EncodedBlock value = encodedBlock.Value;
			byte[] payload = value.Payload;
			bool flag3 = groupSize > 131072;
			if (flag3 != value.MultiChunk || (flag3 && (value.FirstChunkCompSize <= 0 || value.FirstChunkCompSize >= payload.Length)))
			{
				throw new InvalidDataException("Kraken encoder returned invalid PPR-PFS chunk geometry.");
			}
			if (options.VerifyBlocks)
			{
				VerifyEncodedBlock(payload, readOnlySpan, value.FirstChunkCompSize, value.BoundaryFlags, new byte[262144]);
			}
		}
		return new KrakenGroupResult(input, groupSize, forceRaw, flag, flag2, encodedBlock);
	}

	private static PprPfsRawRange[] NormalizeRawRanges(IReadOnlyCollection<PprPfsRawRange>? ranges, long logicalLength)
	{
		if (ranges == null || ranges.Count == 0 || logicalLength == 0L)
		{
			return Array.Empty<PprPfsRawRange>();
		}
		List<PprPfsRawRange> list = new List<PprPfsRawRange>(ranges.Count);
		foreach (PprPfsRawRange range in ranges)
		{
			if (range.Offset < 0 || range.Length < 0)
			{
				throw new ArgumentOutOfRangeException("ranges", "Raw ranges cannot be negative.");
			}
			if (range.Length != 0L && range.Offset < logicalLength)
			{
				long value = Math.Min(logicalLength, checked(range.Offset + range.Length));
				long num = range.Offset / 262144 * 262144;
				value = Math.Min(logicalLength, Align(value, 262144));
				list.Add(new PprPfsRawRange(num, value - num));
			}
		}
		if (list.Count == 0)
		{
			return Array.Empty<PprPfsRawRange>();
		}
		list.Sort((PprPfsRawRange left, PprPfsRawRange right) => left.Offset.CompareTo(right.Offset));
		List<PprPfsRawRange> list2 = new List<PprPfsRawRange>(list.Count);
		foreach (PprPfsRawRange item in list)
		{
			if (list2.Count == 0)
			{
				list2.Add(item);
				continue;
			}
			PprPfsRawRange pprPfsRawRange = list2[list2.Count - 1];
			long num2;
			long val;
			checked
			{
				num2 = pprPfsRawRange.Offset + pprPfsRawRange.Length;
				val = item.Offset + item.Length;
			}
			if (item.Offset <= num2)
			{
				list2[list2.Count - 1] = new PprPfsRawRange(pprPfsRawRange.Offset, Math.Max(num2, val) - pprPfsRawRange.Offset);
			}
			else
			{
				list2.Add(item);
			}
		}
		return list2.ToArray();
	}

	private static void AddOffset(List<ulong> offsets, long storedOffset, ulong highFlags)
	{
		if ((ulong)storedOffset > 281474976710655uL)
		{
			throw new InvalidDataException("PFSC payload exceeds the 48-bit offset field.");
		}
		offsets.Add((highFlags << 48) | (ulong)storedOffset);
	}

	private static void VerifyEncodedBlock(byte[] payload, ReadOnlySpan<byte> expected, int firstChunkCompSize, int boundaryFlags, byte[] decodedBuffer)
	{
		Span<byte> span = decodedBuffer.AsSpan(0, expected.Length);
		bool flag = expected.Length > 131072;
		KrakenDecodeStatus krakenDecodeStatus = KrakenDecoder.DecodeBlock(payload, boundaryFlags, flag ? firstChunkCompSize : 0, span);
		if (krakenDecodeStatus != KrakenDecodeStatus.Success || !span.SequenceEqual(expected))
		{
			throw new InvalidDataException($"Kraken encoder round-trip failed ({krakenDecodeStatus}).");
		}
	}

	private static void ReadExactly(Stream source, byte[] buffer, int count)
	{
		int num;
		for (int i = 0; i < count; i += num)
		{
			num = source.Read(buffer, i, count - i);
			if (num == 0)
			{
				throw new InvalidDataException("Source stream ended before the declared logical length.");
			}
		}
	}

	private static long Align(long value, int alignment)
	{
		checked
		{
			return unchecked(checked(value + alignment - 1) / alignment) * alignment;
		}
	}

	private static bool PathsEqual(string left, string right)
	{
		return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
	}
}
