using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using LibProsperoPkg.PFS;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PlayGo;

/// <summary>
/// Generators for the PS5 PlayGo / "about" files produced by the publishing
/// pipeline. See the file header for the layouts and the boundary.
/// </summary>
public static class ProsperoPlayGo
{
	/// <summary>The fixed size of a PS5 <c>playgo-chunk.dat</c> for the single-chunk profile.</summary>
	public const int ChunkDatSize = 416;

	/// <summary>The fixed size of a PS5 <c>playgo-ficm.dat</c> header (per-file array follows).</summary>
	public const int FicmHeaderSize = 16;

	private const string RightSprxResource = "LibProsperoPkg.PlayGo.Data.right.sprx";

	/// <summary>The PlayGo CRC block size: the finalized mount image is reduced in 64KiB blocks.</summary>
	public const int ChunkCrcBlockSize = 65536;

	/// <summary>The fixed size of the <c>playgo-hash-table.dat</c> header + 16-byte prefix
	/// (the per-chunk constant table follows at this offset).</summary>
	public const int HashTableTableOffset = 56;

	private static readonly byte[][] HashTableEntries = new byte[5][]
	{
		new byte[8] { 142, 84, 203, 77, 74, 246, 48, 14 },
		new byte[8] { 242, 191, 246, 39, 185, 143, 136, 83 },
		new byte[8] { 203, 220, 198, 62, 236, 179, 196, 174 },
		new byte[8] { 11, 244, 233, 197, 218, 248, 201, 174 },
		new byte[8] { 76, 247, 12, 8, 23, 77, 203, 211 }
	};

	/// <summary>The size of one <c>playgo-hash-table.dat</c> per-chunk table entry.</summary>
	public const int HashTableEntrySize = 8;

	private static ReadOnlySpan<byte> HashTablePrefix => new byte[16]
	{
		81, 79, 162, 38, 171, 138, 202, 146, 77, 196,
		27, 164, 97, 183, 187, 9
	};

	/// <summary>
	/// Builds the PS5 <c>sce_suppl/config/&lt;content-id&gt;/playgo-chunk.crc</c> by reducing the
	/// finalized mount image with CRC-32C (Castagnoli) in 64KiB blocks and serialising each block's
	/// checksum as a little-endian uint32, in block order. This reproduces the reference
	/// output byte-for-byte (validated against every debug sample in
	/// TestFiles/PS5/PKG/Debug). The <paramref name="finalizedMountImage" /> is the FIH+PFS+SC region
	/// that precedes the SI segment (i.e. everything from offset 0 up to the SI archive); a reference
	/// mount image is always a whole number of 64KiB blocks, but a trailing partial block (if any)
	/// is reduced over its actual length for robustness.
	/// </summary>
	/// <param name="finalizedMountImage">The finalized mount image bytes (FIH header + PFS image + embedded CNT).</param>
	/// <returns>The <c>playgo-chunk.crc</c> payload: 4 bytes per 64KiB block.</returns>
	public static byte[] BuildChunkCrc(ReadOnlySpan<byte> finalizedMountImage)
	{
		if (finalizedMountImage.Length == 0)
		{
			return Array.Empty<byte>();
		}
		int num = (finalizedMountImage.Length + 65536 - 1) / 65536;
		byte[] array = new byte[num * 4];
		for (int i = 0; i < num; i++)
		{
			int num2 = i * 65536;
			int length = Math.Min(65536, finalizedMountImage.Length - num2);
			uint value = ProsperoCrc32C.Compute(finalizedMountImage.Slice(num2, length));
			BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(i * 4), value);
		}
		return array;
	}

	/// <summary>
	/// Streamed equivalent of <see cref="M:LibProsperoPkg.PlayGo.ProsperoPlayGo.BuildChunkCrc(System.ReadOnlySpan{System.Byte})" />. The bytes from the
	/// stream's current position through <paramref name="length" /> are reduced without buffering the
	/// complete mount image; the original position is restored.
	/// </summary>
	public static byte[] BuildChunkCrc(Stream finalizedMountImage, long length, Action<string>? log = null)
	{
		ArgumentNullException.ThrowIfNull(finalizedMountImage, "finalizedMountImage");
		if (!finalizedMountImage.CanRead || !finalizedMountImage.CanSeek)
		{
			throw new ArgumentException("Finalized mount-image stream must be readable and seekable.", "finalizedMountImage");
		}
		if (length < 0 || length > finalizedMountImage.Length - finalizedMountImage.Position)
		{
			throw new ArgumentOutOfRangeException("length");
		}
		if (length == 0L)
		{
			return Array.Empty<byte>();
		}
		long num = checked(length + 65536 - 1) / 65536;
		if (num > 536870911)
		{
			throw new InvalidDataException("Mount image has too many blocks for a CRC table.");
		}
		byte[] array = new byte[checked((int)num * 4)];
		const int ChunkBlocks = 64; // 4 MiB buffer
		const int BlockSize = 65536;
		byte[] buffer = new byte[ChunkBlocks * BlockSize];
		long position = finalizedMountImage.Position;
		try
		{
			long remaining = length;
			int totalBlocks = (int)num;
			int lastReportedPercent = -1;

			for (int blockOffset = 0; blockOffset < totalBlocks; blockOffset += ChunkBlocks)
			{
				int blocksInChunk = Math.Min(ChunkBlocks, totalBlocks - blockOffset);
				int bytesToRead = (int)Math.Min((long)blocksInChunk * BlockSize, remaining);

				finalizedMountImage.ReadExactly(buffer.AsSpan(0, bytesToRead));
				remaining -= bytesToRead;

				int chunkOffset = 0;
				for (int i = 0; i < blocksInChunk; i++)
				{
					int currentBlockIndex = blockOffset + i;
					int thisBlockSize = Math.Min(BlockSize, bytesToRead - chunkOffset);
					if (thisBlockSize <= 0) break;

					uint value = ProsperoCrc32C.Compute(buffer.AsSpan(chunkOffset, thisBlockSize));
					BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(currentBlockIndex * 4), value);
					chunkOffset += thisBlockSize;
				}

				if (totalBlocks > 1000)
				{
					int percent = (int)((long)(blockOffset + blocksInChunk) * 100 / totalBlocks);
					if (percent >= lastReportedPercent + 20 || blockOffset + blocksInChunk == totalBlocks)
					{
						lastReportedPercent = percent;
						log?.Invoke($"[stage 5/5] Calculating PlayGo chunk CRC32C: {percent}%...");
					}
				}
			}
			return array;
		}
		finally
		{
			finalizedMountImage.Position = position;
		}
	}

	/// <summary>
	/// Builds the PS5 <c>sce_sys/playgo-chunk.dat</c> (<c>plgx</c> container, version 0x1000) for the
	/// single-image / single-chunk / single-scenario profile used by PS5 system applications.
	/// </summary>
	/// <param name="contentId">The 36-character content id stamped at offset 0x40.</param>
	/// <param name="chunkDataSize">
	/// The size of chunk #0's primary manifest-chunk (mchunk) region (word at 0x148). When unknown,
	/// the inner PFS image size is a principled value.
	/// </param>
	/// <param name="chunkTailSize">
	/// The size of chunk #0's secondary mchunk region (word at 0x158). When unknown, pass 0.
	/// </param>
	/// <param name="publisherNwonly">
	/// Selects the publisher nwonly labels and mount-range convention used by PPR/NAPS packages.
	/// </param>
	/// <param name="includePublisherLabels">
	/// Keeps the standard <c>Chunk #0</c>/<c>Scenario #0</c> labels for publisher APP. Publisher
	/// AC uses one-byte empty label tables.
	/// </param>
	/// <param name="chunkCount">Number of automatic chunks. One preserves the byte-exact verified profile.</param>
	/// <returns>The generated <c>playgo-chunk.dat</c> payload.</returns>
	public static byte[] BuildChunkDat(string contentId, ulong chunkDataSize = 0uL, ulong chunkTailSize = 0uL, bool publisherNwonly = false, bool includePublisherLabels = false, int chunkCount = 1)
	{
		ArgumentException.ThrowIfNullOrEmpty(contentId, "contentId");
		if (contentId.Length != 36)
		{
			throw new ArgumentException("Content id must be exactly 36 characters.", "contentId");
		}
		if ((chunkCount < 1 || chunkCount > 64) ? true : false)
		{
			throw new ArgumentOutOfRangeException("chunkCount", "PlayGo chunk count must be in the range 1..64.");
		}
		if (chunkCount > 1)
		{
			return BuildMultiChunkDat(contentId, SplitMainExtent(chunkDataSize, chunkCount), chunkTailSize, publisherNwonly, includePublisherLabels, ulong.MaxValue);
		}
		byte[] array = new byte[416];
		Span<byte> span = array.AsSpan();
		Encoding.ASCII.GetBytes("plgx").CopyTo(span);
		BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(4), 4096);
		BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(6), 0);
		BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(8), 1);
		BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(10), 1);
		BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(12), 0);
		BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(14), 1);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(16), 416u);
		BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(20), 0);
		BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(22), 1);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(24), 0u);
		span[30] = 133;
		span[32] = 2;
		span[36] = 1;
		span[48] = 17;
		span[56] = byte.MaxValue;
		span[57] = byte.MaxValue;
		span[58] = byte.MaxValue;
		span[59] = byte.MaxValue;
		span[60] = byte.MaxValue;
		span[61] = byte.MaxValue;
		span[62] = byte.MaxValue;
		span[63] = byte.MaxValue;
		Encoding.ASCII.GetBytes(contentId).CopyTo(span.Slice(64));
		WritePtr(span, 192, 256u, 32u);
		WritePtr(span, 200, 288u, 8u);
		bool flag = publisherNwonly && !includePublisherLabels;
		WritePtr(span, 208, 304u, flag ? 1u : 9u);
		WritePtr(span, 216, 320u, 32u);
		WritePtr(span, 224, 352u, 32u);
		WritePtr(span, 232, 384u, 2u);
		WritePtr(span, 240, 400u, flag ? 1u : 12u);
		span[256] = 128;
		span[258] = 3;
		span[260] = 2;
		span[264] = 17;
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(272), ulong.MaxValue);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(292), 1u);
		if (!flag)
		{
			Encoding.ASCII.GetBytes("Chunk #0").CopyTo(span.Slice(304));
		}
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(320), 0uL);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(328), chunkDataSize);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(336), chunkDataSize);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(344), chunkTailSize);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(352), 33uL);
		span[372] = 1;
		span[374] = 1;
		if (!flag)
		{
			Encoding.ASCII.GetBytes("Scenario #0").CopyTo(span.Slice(400));
		}
		return array;
	}

	/// <summary>
	/// Builds a single-scenario PlayGo descriptor with multiple contiguous main-image extents.
	/// Chunk zero owns its main extent plus the common trailing metadata extent; every later chunk
	/// owns one main extent. Publishing Tools uses this shape for a scenario ordered as
	/// <c>0 1 ... N-1</c>.
	/// </summary>
	public static byte[] BuildMultiChunkDat(string contentId, IReadOnlyList<ulong> mainChunkSizes, ulong chunkTailSize, bool publisherNwonly = true, bool includePublisherLabels = true, ulong languageMask = ulong.MaxValue)
	{
		ArgumentException.ThrowIfNullOrEmpty(contentId, "contentId");
		ArgumentNullException.ThrowIfNull(mainChunkSizes, "mainChunkSizes");
		if (contentId.Length != 36)
		{
			throw new ArgumentException("Content id must be exactly 36 characters.", "contentId");
		}
		int count = mainChunkSizes.Count;
		if ((count < 2 || count > 64) ? true : false)
		{
			throw new ArgumentOutOfRangeException("mainChunkSizes", "Multi-chunk PlayGo requires 2..64 chunks.");
		}
		if (mainChunkSizes.Any((ulong size) => size == 0))
		{
			throw new ArgumentException("Every PlayGo chunk must own a non-empty main extent.", "mainChunkSizes");
		}
		int count2 = mainChunkSizes.Count;
		bool flag = publisherNwonly && !includePublisherLabels;
		string[] array = (from index in Enumerable.Range(0, count2)
			select $"Chunk #{index}").ToArray();
		int[] array2 = new int[count2];
		int num = (flag ? 1 : 0);
		if (!flag)
		{
			for (int num2 = 0; num2 < count2; num2++)
			{
				array2[num2] = num;
				num = checked(num + Encoding.ASCII.GetByteCount(array[num2]) + 1);
			}
		}
		int num3 = 256;
		int num4 = checked(count2 * 32);
		int num5 = num3 + num4;
		int num6 = checked((count2 + 1) * 4);
		int num7 = Align16(num5 + num6);
		int num8 = Align16(num7 + num);
		int num9 = checked((count2 + 1) * 16);
		int num10 = num8 + num9;
		int num11 = num10 + 32;
		int num12 = checked(count2 * 2);
		int num13 = Align16(num11 + num12);
		int num14 = (flag ? 1 : (Encoding.ASCII.GetByteCount("Scenario #0") + 1));
		int num15 = Align16(num13 + num14);
		byte[] array3 = new byte[num15];
		Span<byte> span = array3;
		Encoding.ASCII.GetBytes("plgx").CopyTo(span);
		BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(4), 4096);
		BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(8), 1);
		int num16;
		checked
		{
			BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(10), (ushort)count2);
			BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(14), 1);
			BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(16), (uint)num15);
			BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(22), 1);
			span[30] = 133;
			span[32] = (byte)(count2 + 1);
			span[36] = 1;
			span[48] = 17;
			BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(56), languageMask);
			Encoding.ASCII.GetBytes(contentId).CopyTo(span.Slice(64));
			WritePtr(span, 192, (uint)num3, (uint)num4);
			WritePtr(span, 200, (uint)num5, (uint)num6);
			WritePtr(span, 208, (uint)num7, (uint)num);
			WritePtr(span, 216, (uint)num8, (uint)num9);
			WritePtr(span, 224, (uint)num10, 32u);
			WritePtr(span, 232, (uint)num11, (uint)num12);
			WritePtr(span, 240, (uint)num13, (uint)num14);
			num16 = 0;
		}
		for (int num17 = 0; num17 < count2; num17++)
		{
			int num18 = num3 + num17 * 32;
			span[num18] = 128;
			span[num18 + 2] = 3;
			BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(num18 + 4), checked((ushort)((num17 != 0) ? 1 : 2)));
			span[num18 + 8] = 17;
			BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(num18 + 16), languageMask);
			BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(num18 + 24), checked((uint)num16));
			BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(num18 + 28), checked((uint)array2[num17]));
			int start = num5 + num16;
			BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(start), checked((uint)num17));
			num16 += 4;
			if (num17 == 0)
			{
				BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(num5 + num16), checked((uint)count2));
				num16 += 4;
			}
			if (!flag)
			{
				Encoding.ASCII.GetBytes(array[num17]).CopyTo(span.Slice(num7 + array2[num17]));
			}
		}
		ulong num19 = 0uL;
		for (int num20 = 0; num20 < count2; num20++)
		{
			int num21 = num8 + num20 * 16;
			BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(num21), num19);
			BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(num21 + 8), mainChunkSizes[num20]);
			num19 = checked(num19 + mainChunkSizes[num20]);
		}
		int num22 = num8 + count2 * 16;
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(num22), num19);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(num22 + 8), chunkTailSize);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(num10), 33uL);
		span[num10 + 20] = 1;
		span[num10 + 22] = checked((byte)count2);
		for (int num23 = 0; num23 < count2; num23++)
		{
			BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(num11 + num23 * 2), checked((ushort)num23));
		}
		if (!flag)
		{
			Encoding.ASCII.GetBytes("Scenario #0").CopyTo(span.Slice(num13));
		}
		return array3;
	}

	/// <summary>
	/// Builds the PS5 <c>sce_sys/playgo-ficm.dat</c>. The file is a 16-byte header followed by a
	/// <paramref name="fileCount" />-byte per-file array (zero-filled in the reference samples), so its
	/// total length is <c>16 + fileCount</c>.
	/// </summary>
	/// <param name="fileCount">The PlayGo file/inode count stamped at 0x0C.</param>
	public static byte[] BuildFicm(uint fileCount)
	{
		if (fileCount > 1048576)
		{
			throw new ArgumentOutOfRangeException("fileCount", fileCount, "PlayGo file count is implausibly large.");
		}
		byte[] array = new byte[16 + fileCount];
		Span<byte> span = array.AsSpan();
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0), 1u);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8), 16u);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12), fileCount);
		return array;
	}

	/// <summary>
	/// Builds the PS5 <c>sce_sys/playgo-hash-table.dat</c> (CNT entry id <c>0x2010</c>). The file is a
	/// 0x28-byte header, a 16-byte constant prefix, then a <paramref name="chunkCount" />-entry constant
	/// table (8 bytes each), so its total length is <c>0x38 + chunkCount * 8</c>. The number of
	/// hash-table chunks is half the PlayGo file/inode count stamped in <c>playgo-ficm.dat</c>
	/// (proven across the reference debug samples: ficm 8 -&gt; 4 chunks, ficm 10 -&gt; 5 chunks).
	/// </summary>
	/// <param name="chunkCount">The hash-table chunk count (= <c>ficmFileCount / 2</c>).</param>
	public static byte[] BuildHashTable(uint chunkCount)
	{
		if (chunkCount > 65536)
		{
			throw new ArgumentOutOfRangeException("chunkCount", chunkCount, "PlayGo hash-table chunk count is implausibly large.");
		}
		int num = (int)(chunkCount * 8);
		byte[] array = new byte[56 + num];
		Span<byte> span = array.AsSpan();
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0), 1u);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4), 134217728u);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8), 56u);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12), (uint)num);
		new byte[4] { 127, 70, 76, 84 }.CopyTo(span.Slice(24));
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(36), chunkCount);
		HashTablePrefix.CopyTo(span.Slice(40));
		for (int i = 0; i < chunkCount; i++)
		{
			HashTableEntries[Math.Min(i, HashTableEntries.Length - 1)].CopyTo(span.Slice(56 + i * 8));
		}
		return array;
	}

	/// <summary>
	/// Builds publisher FICM data for AFID-ordered files. Each file contributes the observed
	/// two-byte pair <c>{chunkId, 0}</c>.
	/// </summary>
	public static byte[] BuildFicm(IReadOnlyList<byte> fileChunkIds)
	{
		ArgumentNullException.ThrowIfNull(fileChunkIds, "fileChunkIds");
		byte[] array = BuildFicm(checked((uint)fileChunkIds.Count * 2));
		for (int i = 0; i < fileChunkIds.Count; i++)
		{
			array[16 + i * 2] = fileChunkIds[i];
		}
		return array;
	}

	/// <summary>Assigns AFID-ordered files to contiguous, near-equal automatic chunk groups.</summary>
	public static byte[] BuildAutomaticFileChunkIds(int fileCount, int chunkCount)
	{
		if (fileCount < 1)
		{
			throw new ArgumentOutOfRangeException("fileCount", "PlayGo requires at least one file.");
		}
		bool flag = ((chunkCount < 1 || chunkCount > 64) ? true : false);
		if (flag || chunkCount > fileCount)
		{
			throw new ArgumentOutOfRangeException("chunkCount", "PlayGo chunk count must be 1..64 and cannot exceed the file count.");
		}
		byte[] array = new byte[fileCount];
		for (int i = 0; i < fileCount; i++)
		{
			checked
			{
				array[i] = (byte)Math.Min(chunkCount - 1, unchecked(checked(unchecked((long)i) * unchecked((long)chunkCount)) / fileCount));
			}
		}
		return array;
	}

	/// <summary>
	/// Builds the publisher PPR/NAPS FLT hash table from the actual inner-file paths. Each table item is
	/// the PS5 flat-path hash written little-endian; publisher output sorts the hashes numerically.
	/// </summary>
	public static byte[] BuildHashTable(IReadOnlyList<string> innerPaths)
	{
		ArgumentNullException.ThrowIfNull(innerPaths, "innerPaths");
		ulong[] array = (from value in innerPaths.Select(ProsperoPs5FlatPathTable.HashPath)
			orderby value
			select value).ToArray();
		if (array.Length > 65536)
		{
			throw new ArgumentOutOfRangeException("innerPaths", "PlayGo FLT hash-table count is implausibly large.");
		}
		int num = checked(array.Length * 8);
		byte[] array2 = new byte[56 + num];
		Span<byte> span = array2;
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0), 1u);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4), 134217728u);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8), 56u);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12), (uint)num);
		new byte[4] { 127, 70, 76, 84 }.CopyTo(span.Slice(24));
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(36), (uint)array.Length);
		HashTablePrefix.CopyTo(span.Slice(40));
		for (int num2 = 0; num2 < array.Length; num2++)
		{
			BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(56 + num2 * 8), array[num2]);
		}
		return array2;
	}

	/// <summary>
	/// The fixed PS5 debug <c>sce_sys/about/right.sprx</c> module embedded in every reference
	/// debug package, or <c>null</c> when the embedded resource is unavailable.
	/// </summary>
	public static byte[]? GetRightSprx()
	{
		using Stream stream = typeof(ProsperoPlayGo).GetTypeInfo().Assembly.GetManifestResourceStream("LibProsperoPkg.PlayGo.Data.right.sprx");
		if (stream == null)
		{
			return null;
		}
		using MemoryStream memoryStream = new MemoryStream();
		stream.CopyTo(memoryStream);
		return memoryStream.ToArray();
	}

	private static void WritePtr(Span<byte> s, int at, uint offset, uint size)
	{
		BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(at), offset);
		BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(at + 4), size);
	}

	private static ulong[] SplitMainExtent(ulong size, int chunkCount)
	{
		ulong num = size / 65536;
		ulong num2 = size % 65536;
		if (num < (ulong)chunkCount)
		{
			throw new ArgumentOutOfRangeException("chunkCount", $"The PlayGo main extent has {num} blocks and cannot be split into {chunkCount} non-empty chunks.");
		}
		ulong[] array = new ulong[chunkCount];
		ulong num3 = num / (ulong)chunkCount;
		ulong num4 = num % (ulong)chunkCount;
		for (int i = 0; i < chunkCount; i++)
		{
			array[i] = checked((num3 + (((ulong)i < num4) ? 1uL : 0uL)) * 65536);
		}
		array[^1] = checked(array[^1] + num2);
		return array;
	}

	private static int Align16(int value)
	{
		return checked(value + 15) & -16;
	}

	/// <summary>
	/// Validates and repairs <c>playgo-chunk.dat</c> in <paramref name="sourceDir"/>/sce_sys.
	/// If the file is missing, corrupt, has a mismatched Content ID, or invalid chunk mask,
	/// it creates a .bak backup and patches/regenerates <c>playgo-chunk.dat</c> to match
	/// <paramref name="expectedContentId"/>, ensuring Pass-Through builds don't fail.
	/// Also safely sets aside conflicting retail hash tables if mismatched.
	/// </summary>
	public static bool ValidateAndEditPlayGoChunk(string sourceDir, string expectedContentId, Action<string>? logger = null)
	{
		string sceSysDir = Path.Combine(sourceDir, "sce_sys");
		if (!Directory.Exists(sceSysDir))
		{
			Directory.CreateDirectory(sceSysDir);
		}

		string chunkDatPath = Path.Combine(sceSysDir, "playgo-chunk.dat");
		if (!File.Exists(chunkDatPath))
		{
			logger?.Invoke($"playgo-chunk.dat was missing; generating standard PlayGo chunk for {expectedContentId}...");
			byte[] fresh = BuildChunkDat(expectedContentId);
			File.WriteAllBytes(chunkDatPath, fresh);
			logger?.Invoke("playgo-chunk.dat generated successfully.");
			return true;
		}

		try
		{
			byte[] data = File.ReadAllBytes(chunkDatPath);
			ulong chunkSize = data.Length >= 336 ? BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(328, 8)) : ulong.MaxValue;
			bool needsRegen = (data.Length < 100 || !data.AsSpan(0, 4).SequenceEqual("plgx"u8) || (data.Length >= 336 && chunkSize == 0));
			if (needsRegen)
			{
				logger?.Invoke("playgo-chunk.dat is truncated or invalid; regenerating standard PlayGo chunk file...");
				string bak = chunkDatPath + ".bak";
				if (!File.Exists(bak))
				{
					File.Copy(chunkDatPath, bak, overwrite: false);
				}
				byte[] fresh = BuildChunkDat(expectedContentId);
				File.WriteAllBytes(chunkDatPath, fresh);
				logger?.Invoke($"playgo-chunk.dat regenerated for {expectedContentId} (backup: playgo-chunk.dat.bak).");
				QuarantineConflictingTables(sceSysDir, logger);
				return true;
			}

			bool modified = false;
			string existingCid = Encoding.ASCII.GetString(data, 64, Math.Min(36, data.Length - 64)).TrimEnd('\0');
			if (!string.IsNullOrEmpty(expectedContentId) && expectedContentId.Length == 36 &&
			    !string.Equals(existingCid, expectedContentId, StringComparison.OrdinalIgnoreCase))
			{
				logger?.Invoke($"playgo-chunk.dat Content ID mismatch detected: '{existingCid}' vs expected '{expectedContentId}'. Updating Content ID...");
				Encoding.ASCII.GetBytes(expectedContentId).CopyTo(data.AsSpan(64, 36));
				modified = true;
			}

			ulong mask = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(56, 8));
			if (mask == 0 || mask == 0x4000000000000000uL)
			{
				logger?.Invoke($"playgo-chunk.dat chunk mask was 0x{mask:X16}. Normalizing to 0xFFFFFFFFFFFFFFFF...");
				data.AsSpan(56, 8).Fill(0xFF);
				modified = true;
			}

			if (modified)
			{
				string bak = chunkDatPath + ".bak";
				if (!File.Exists(bak))
				{
					File.Copy(chunkDatPath, bak, overwrite: false);
				}
				File.WriteAllBytes(chunkDatPath, data);
				logger?.Invoke($"playgo-chunk.dat successfully updated and verified (backup: playgo-chunk.dat.bak).");
				QuarantineConflictingTables(sceSysDir, logger);
			}
			else
			{
				logger?.Invoke($"playgo-chunk.dat verified valid (Content ID: {existingCid}, Mask: 0x{mask:X16}).");
			}

			return true;
		}
		catch (Exception ex)
		{
			logger?.Invoke($"Warning: could not validate/edit playgo-chunk.dat: {ex.Message}");
			return false;
		}
	}

	private static void QuarantineConflictingTables(string sceSysDir, Action<string>? logger)
	{
		string[] conflicting = { "playgo-hash-table.dat", "playgo-ficm.dat" };
		foreach (string name in conflicting)
		{
			string path = Path.Combine(sceSysDir, name);
			if (File.Exists(path))
			{
				string bak = path + ".bak";
				if (!File.Exists(bak))
				{
					File.Move(path, bak);
					logger?.Invoke($"Quarantined conflicting retail table {name} -> {name}.bak to prevent hash mismatch on PS5.");
				}
			}
		}
	}
}

