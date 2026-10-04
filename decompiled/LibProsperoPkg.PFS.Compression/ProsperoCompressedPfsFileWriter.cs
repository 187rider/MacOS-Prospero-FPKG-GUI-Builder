using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading.Tasks;
using LibProsperoPkg.PFS.Compression.Oodle;

namespace LibProsperoPkg.PFS.Compression;

/// <summary>
/// Produces a valid PS5 PFSv3/PFSv2 compression container ("PFSC"). Each block is
/// Kraken-compressed with <see cref="T:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder" />; incompressible blocks are
/// stored uncompressed. Both paths round-trip exactly through the decoder.
/// </summary>
public static class ProsperoCompressedPfsFileWriter
{
	/// <summary>The default logical block size for PS5 v3 containers (256 KiB).</summary>
	public const int DefaultBlockSize = 262144;

	private const int HeaderSize = 72;

	private const int DirectoryEntrySize = 16;

	private const int SectionCount = 7;

	private const int SectionAlignment = 8;

	private const int DataAlignment = 1024;

	private const uint Magic = 1129530960u;

	private const uint EncodeParam0C = 2050u;

	private const ulong StoredFlagBase = 12uL;

	private const ulong StoredFlagLargeHalf = 192uL;

	private const ulong CompressedFlag = 6uL;

	private const ulong MultiChunkFlag = 32uL;

	private const ulong BoundaryShuffleShift = 44uL;

	private const ulong BoundaryFlagShift = 48uL;

	private const ulong SizeHintShift = 44uL;

	private const ulong SizeHintMax = 131071uL;

	private static readonly byte[] GitHash = new byte[20]
	{
		35, 152, 125, 22, 201, 32, 154, 199, 40, 55,
		25, 50, 126, 15, 80, 107, 188, 244, 89, 244
	};

	private static readonly byte[] ShuffleTable = new byte[64]
	{
		4, 4, 0, 0, 0, 0, 0, 0, 2, 2,
		4, 0, 0, 0, 0, 0, 1, 1, 6, 0,
		0, 0, 0, 0, 1, 1, 1, 1, 1, 1,
		1, 1, 8, 2, 2, 4, 0, 0, 0, 0,
		1, 1, 6, 2, 2, 4, 0, 0, 1, 1,
		6, 1, 1, 6, 0, 0, 4, 4, 4, 4,
		0, 0, 0, 0
	};

	private static bool KeepCompressed(int compressedSize, int uncompressedSize)
	{
		return compressedSize <= (long)uncompressedSize * 15L >> 4;
	}

	/// <summary>
	/// Builds a stored (uncompressed) PFSv3 compression container for <paramref name="payload" /> and
	/// returns the complete container bytes.
	/// </summary>
	/// <param name="payload">The uncompressed data to wrap (the logical file the container expands to).</param>
	/// <param name="level">The Kraken level recorded in the header (default 7). It has no effect
	/// on stored data but is preserved because it contributes to the file digest.</param>
	/// <param name="blockSize">The logical block size (default 256 KiB). Must be positive.</param>
	/// <returns>The serialized PFSv3 container.</returns>
	/// <exception cref="T:System.ArgumentNullException"><paramref name="payload" /> is null.</exception>
	/// <exception cref="T:System.ArgumentOutOfRangeException"><paramref name="blockSize" /> is not positive.</exception>
	/// <exception cref="T:System.PlatformNotSupportedException">SHA3-256 is unavailable on this host.</exception>
	public static byte[] WriteStored(ReadOnlySpan<byte> payload, int level = 7, int blockSize = 262144)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize, "blockSize");
		if (!ProsperoPfsDigest.IsSupported)
		{
			throw new PlatformNotSupportedException("SHA3-256 is required for the PS5 PFSv3 compression format but is not available on this host.");
		}
		int num = ((payload.Length == 0) ? 1 : ((payload.Length + blockSize - 1) / blockSize));
		int num2 = GitHash.Length;
		int num3 = ShuffleTable.Length;
		int num4 = (num + 1) * 16;
		int num5 = num * 32;
		int num6 = num * 16;
		int num7 = 184;
		int num8 = Align(num7 + num2, 8);
		int num9 = Align(num8 + num3, 8);
		int num10 = Align(num9 + num4, 8);
		int num11 = Align(num10 + num5, 8);
		int num12 = Align(num11 + num6, 8);
		int num13 = Align(num12, 1024);
		long num14 = (long)num13 + (long)payload.Length;
		byte[] array = new byte[checked((int)num14)];
		Span<byte> span = array;
		BinaryPrimitives.WriteUInt32LittleEndian(span, 1129530960u);
		BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(4), 3);
		BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(6), 7);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8), (uint)blockSize);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12), 2050u);
		ulong value = 2 | ((ulong)(byte)(sbyte)level << 8) | 0x120000;
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(16), value);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(24), (ulong)payload.Length);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(32), (ulong)num14);
		WriteDirectoryEntry(span, 0, 1, num7, num2);
		WriteDirectoryEntry(span, 1, 2, num8, num3);
		WriteDirectoryEntry(span, 2, 3, num9, num4);
		WriteDirectoryEntry(span, 3, 4, num10, num5);
		WriteDirectoryEntry(span, 4, 5, num11, num6);
		WriteDirectoryEntry(span, 5, 6, num12, 0);
		WriteDirectoryEntry(span, 6, 7, num13, payload.Length);
		GitHash.CopyTo(span.Slice(num7));
		ShuffleTable.CopyTo(span.Slice(num8));
		int num15 = blockSize / 2;
		long num16 = 0L;
		for (int i = 0; i < num; i++)
		{
			int num17 = (int)((payload.Length != 0) ? Math.Min(blockSize, payload.Length - num16) : 0);
			ulong num18 = 0xCuL | (ulong)((num17 > num15) ? 192 : 0);
			ulong num19 = (ulong)Math.Min(Math.Max(num17 - 1, 0), 131071L);
			int num20 = num9 + i * 16;
			BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(num20), (ulong)num16 | (num18 << 48));
			BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(num20 + 8), (ulong)num16 | (num19 << 44));
			ReadOnlySpan<byte> data = ((num17 == 0) ? default(ReadOnlySpan<byte>) : payload.Slice((int)num16, num17));
			int start = num10 + i * 32;
			ProsperoPfsDigest.ComputeBlockDigest(data, span.Slice(start, 32));
			if (num17 > 0)
			{
				data.CopyTo(span.Slice(num13 + (int)num16));
			}
			num16 += num17;
		}
		int num21 = num9 + num * 16;
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(num21), (ulong)payload.Length);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(num21 + 8), (ulong)payload.Length);
		ProsperoPfsDigest.ComputeFileDigest(span.Slice(8, 24), span.Slice(num8, num3), span.Slice(num9, num4), span.Slice(num10, num5)).CopyTo(span.Slice(40));
		return array;
	}

	/// <summary>
	/// Builds a stored PFSv3 container for <paramref name="payload" /> and writes it to
	/// <paramref name="destination" />.
	/// </summary>
	public static void WriteStored(Stream destination, ReadOnlySpan<byte> payload, int level = 7, int blockSize = 262144)
	{
		ArgumentNullException.ThrowIfNull(destination, "destination");
		destination.Write(WriteStored(payload, level, blockSize));
	}

	/// <summary>
	/// Builds a Kraken-compressed PFSv3 container for <paramref name="payload" />. Each logical block is
	/// compressed with the Kraken encoder; blocks that do not compress below their stored
	/// size are written stored (isBlockCompressed = 0). The resulting container round-trips exactly through
	/// the decoder.
	/// </summary>
	/// <param name="payload">The uncompressed data to wrap.</param>
	/// <param name="level">The Kraken level recorded in the header (default 7, matching "nwonly").</param>
	/// <param name="blockSize">The logical block size (default 256 KiB). Must be positive.</param>
	/// <param name="useHuffmanArrays">When true (the default), the literal/command/length streams within
	/// each chunk are Huffman-coded (entropy chunk type 2) where that is smaller than the raw form,
	/// producing markedly smaller blocks. Pass false for raw entropy arrays (larger, useful for
	/// debugging/determinism). Both forms round-trip exactly through the decoder.</param>
	/// <returns>The serialized PFSv3 container.</returns>
	/// <exception cref="T:System.ArgumentOutOfRangeException"><paramref name="blockSize" /> is not positive.</exception>
	/// <exception cref="T:System.PlatformNotSupportedException">SHA3-256 is unavailable on this host.</exception>
	public static byte[] WriteCompressed(ReadOnlySpan<byte> payload, int level = 7, int blockSize = 262144, bool useHuffmanArrays = true)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize, "blockSize");
		if (!ProsperoPfsDigest.IsSupported)
		{
			throw new PlatformNotSupportedException("SHA3-256 is required for the PS5 PFSv3 compression format but is not available on this host.");
		}
		int num = ((payload.Length == 0) ? 1 : ((payload.Length + blockSize - 1) / blockSize));
		byte[][] array = new byte[num][];
		bool[] array2 = new bool[num];
		bool[] array3 = new bool[num];
		int[] array4 = new int[num];
		int[] array5 = new int[num];
		int[] array6 = new int[num];
		unsafe
		{
			fixed (byte* pPayload = payload)
			{
				IntPtr basePtr = (IntPtr)pPayload;
				int totalPayloadLength = payload.Length;

				Parallel.For(0, num, i =>
				{
					long blockOffset = (long)i * blockSize;
					int blockSizeActual = (int)((totalPayloadLength != 0) ? Math.Min(blockSize, totalPayloadLength - blockOffset) : 0);
					array6[i] = blockSizeActual;

					ReadOnlySpan<byte> data = (blockSizeActual == 0 || basePtr == IntPtr.Zero)
						? default
						: new ReadOnlySpan<byte>((byte*)basePtr + blockOffset, blockSizeActual);

					EncodedBlock? encodedBlock = (blockSizeActual == 0) ? null : OodleKrakenEncoder.EncodeBlock(data, useHuffmanArrays);
					if (encodedBlock.HasValue)
					{
						EncodedBlock valueOrDefault = encodedBlock.GetValueOrDefault();
						if (KeepCompressed(valueOrDefault.Payload.Length, blockSizeActual))
						{
							array[i] = valueOrDefault.Payload;
							array2[i] = true;
							array3[i] = valueOrDefault.MultiChunk;
							array4[i] = valueOrDefault.FirstChunkCompSize;
							array5[i] = valueOrDefault.BoundaryFlags;
							return;
						}
					}
					array[i] = data.ToArray();
					array2[i] = false;
				});
			}
		}
		long num3 = 0L;
		for (int i = 0; i < num; i++)
		{
			num3 += array[i].Length;
		}
		int num5 = GitHash.Length;
		int num6 = ShuffleTable.Length;
		int num7 = (num + 1) * 16;
		int num8 = num * 32;
		int num9 = num * 16;
		int num10 = 184;
		int num11 = Align(num10 + num5, 8);
		int num12 = Align(num11 + num6, 8);
		int num13 = Align(num12 + num7, 8);
		int num14 = Align(num13 + num8, 8);
		int num15 = Align(num14 + num9, 8);
		int num16 = Align(num15, 1024);
		long num17 = num16 + num3;
		byte[] array7 = new byte[checked((int)num17)];
		Span<byte> span = array7;
		BinaryPrimitives.WriteUInt32LittleEndian(span, 1129530960u);
		BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(4), 3);
		BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(6), 7);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8), (uint)blockSize);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12), 2050u);
		ulong value = 2 | ((ulong)(byte)(sbyte)level << 8) | 0x120000;
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(16), value);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(24), (ulong)payload.Length);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(32), (ulong)num17);
		WriteDirectoryEntry(span, 0, 1, num10, num5);
		WriteDirectoryEntry(span, 1, 2, num11, num6);
		WriteDirectoryEntry(span, 2, 3, num12, num7);
		WriteDirectoryEntry(span, 3, 4, num13, num8);
		WriteDirectoryEntry(span, 4, 5, num14, num9);
		WriteDirectoryEntry(span, 5, 6, num15, 0);
		WriteDirectoryEntry(span, 6, 7, num16, checked((int)num3));
		GitHash.CopyTo(span.Slice(num10));
		ShuffleTable.CopyTo(span.Slice(num11));
		int num18 = blockSize / 2;
		long num19 = 0L;
		long num2 = 0L;
		for (int j = 0; j < num; j++)
		{
			int num20 = array6[j];
			byte[] array8 = array[j];
			int num21 = array8.Length;
			ulong num22;
			ulong num23;
			if (array2[j])
			{
				num22 = checked((ulong)array5[j]);
				num23 = (ulong)Math.Min(Math.Max((array3[j] ? array4[j] : num21) - 1, 0), 131071L);
			}
			else
			{
				num22 = 0xCuL | (ulong)((num20 > num18) ? 192 : 0);
				num23 = (ulong)Math.Min(Math.Max(num21 - 1, 0), 131071L);
			}
			int num24 = num12 + j * 16;
			BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(num24), (ulong)num19 | (num22 << 48));
			BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(num24 + 8), (ulong)num2 | (num23 << 44));
			ReadOnlySpan<byte> data2 = ((num20 == 0) ? default(ReadOnlySpan<byte>) : payload.Slice((int)num2, num20));
			int start = num13 + j * 32;
			ProsperoPfsDigest.ComputeBlockDigest(data2, span.Slice(start, 32));
			if (num21 > 0)
			{
				array8.CopyTo(span.Slice(num16 + (int)num19));
			}
			num19 += num21;
			num2 += num20;
		}
		int num25 = num12 + num * 16;
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(num25), (ulong)num19);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(num25 + 8), (ulong)payload.Length);
		ProsperoPfsDigest.ComputeFileDigest(span.Slice(8, 24), span.Slice(num11, num6), span.Slice(num12, num7), span.Slice(num13, num8)).CopyTo(span.Slice(40));
		return array7;
	}

	private static void WriteDirectoryEntry(Span<byte> span, int index, ushort id, int offset, int size)
	{
		int num = 72 + index * 16;
		BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(num), id);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(num + 2), (uint)offset);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(num + 10), (uint)size);
	}

	private static int Align(int value, int alignment)
	{
		return (value + alignment - 1) & ~(alignment - 1);
	}
}
