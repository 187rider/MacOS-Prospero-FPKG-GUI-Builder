using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using LibProsperoPkg.PFS.Compression.Oodle;

namespace LibProsperoPkg.PFS.Compression;

/// <summary>
/// A parsed PS5 PFSv2/PFSv3 compression container ("PFSC"). Provides the header fields,
/// the file-level SHA3-256 digest, the per-block table and full decompression of containers
/// produced by <see cref="T:LibProsperoPkg.PFS.Compression.CompressedPfsFileWriter" /> as well as reference containers: stored
/// blocks plus this library's Kraken codec, which decodes the entropy-coded
/// (Huffman) arrays and the post-seed excess framing used by reference blocks.
/// </summary>
public sealed class CompressedPfsFile
{
	/// <summary>The 4-byte container magic, 'P','F','S','C' (little-endian <c>0x43534650</c>).</summary>
	public const uint Magic = 1129530960u;

	private const ulong NewLzFlagBit = 2uL;

	private const int ChunkMaxUncompressed = 131072;

	private const int OffMagic = 0;

	private const int OffVersion = 4;

	private const int OffSectionCount = 6;

	private const int OffBlockSize = 8;

	private const int OffEncodeParams = 16;

	private const int OffUncompressedSize = 24;

	private const int OffTotalCompressedSize = 32;

	private const int OffFileDigest = 40;

	private const int OffSectionDirectory = 72;

	private const int SectionEntrySize = 16;

	private const int MinHeaderSize = 88;

	private const int SectionGitHash = 1;

	private const int SectionShuffleTable = 2;

	private const int SectionBlockBoundaries = 3;

	private const int SectionBlockHashes = 4;

	private const int SectionBlockData = 7;

	private readonly ReadOnlyMemory<byte> _buffer;

	private (int Offset, int Size) _shuffleRange;

	private (int Offset, int Size) _boundaryRange;

	private (int Offset, int Size) _blockHashRange;

	/// <summary>The container format version (<see cref="F:LibProsperoPkg.PFS.Compression.PfsCompressionFormat.Version2" /> or <see cref="F:LibProsperoPkg.PFS.Compression.PfsCompressionFormat.Version3" />).</summary>
	public PfsCompressionFormat Version { get; private init; }

	/// <summary>The compression algorithm recorded in the header (always <see cref="F:LibProsperoPkg.PFS.Compression.CompressionAlgorithm.Kraken" /> for PS5).</summary>
	public CompressionAlgorithm Algorithm { get; private init; }

	/// <summary>
	/// The Kraken compression level recorded in the header (encode-parameter byte <c>0x11</c>).
	/// Accepted levels are <c>0..9</c> and the fast range <c>-4..-1</c>; default-output containers use level 7.
	/// </summary>
	public int CompressionLevel { get; private init; }

	/// <summary>The Kraken sliding-window bits recorded in the header (encode-parameter byte <c>0x12</c>, normally 18).</summary>
	public int WindowBits { get; private init; }

	/// <summary>The logical block size, in bytes (256 KiB for PS5 v3).</summary>
	public int BlockSize { get; private init; }

	/// <summary>The total uncompressed size, in bytes, of the payload the container expands to.</summary>
	public long UncompressedSize { get; private init; }

	/// <summary>The total compressed size, in bytes, of the whole container file (including metadata).</summary>
	public long TotalCompressedSize { get; private init; }

	/// <summary>The header's 32-byte SHA3-256 file digest (offset <c>0x28</c>).</summary>
	public ReadOnlyMemory<byte> FileDigest { get; private init; }

	/// <summary>The 20-byte revision hash recorded in the metadata (section id=1), or empty when absent.</summary>
	public ReadOnlyMemory<byte> GitHash { get; private init; }

	/// <summary>The absolute byte offset where compressed block data begins (the metadata size).</summary>
	public long DataOffset { get; private init; }

	/// <summary>The parsed per-block table.</summary>
	public IReadOnlyList<PfsBlock> Blocks { get; private init; } = Array.Empty<PfsBlock>();

	private CompressedPfsFile(ReadOnlyMemory<byte> buffer)
	{
		_buffer = buffer;
	}

	/// <summary>
	/// Recomputes the file-level SHA3-256 digest from the parsed metadata and compares it to the
	/// value stored at header offset <c>0x28</c>. Returns <c>true</c> when they match.
	/// </summary>
	/// <remarks>
	/// This is the file-level integrity check performed when opening a container: <c>SHA3-256(header32 || shuffleSection || boundarySection || blockHashSection)</c>.
	/// </remarks>
	/// <exception cref="T:System.PlatformNotSupportedException">SHA3-256 is unavailable on this host.</exception>
	public bool VerifyFileDigest()
	{
		ReadOnlySpan<byte> span = _buffer.Span;
		return PfsDigest.VerifyFileDigest(span.Slice(8, 24), span.Slice(_shuffleRange.Offset, _shuffleRange.Size), span.Slice(_boundaryRange.Offset, _boundaryRange.Size), span.Slice(_blockHashRange.Offset, _blockHashRange.Size), FileDigest.Span);
	}

	/// <summary>
	/// Decompresses the whole container back to its original payload. Stored blocks are copied
	/// verbatim; compressed blocks are decoded with the Kraken codec
	/// (<see cref="T:LibProsperoPkg.PFS.Compression.Oodle.KrakenDecoder" />), which reads both this library's own output and
	/// reference blocks (entropy-coded arrays + the post-seed excess framing). The result has
	/// length <see cref="P:LibProsperoPkg.PFS.Compression.CompressedPfsFile.UncompressedSize" />.
	/// </summary>
	/// <returns>The reconstructed uncompressed payload.</returns>
	/// <exception cref="T:System.IO.InvalidDataException">
	/// A block is malformed or uses an unsupported entropy/excess form.
	/// </exception>
	public byte[] Decompress()
	{
		byte[] array = new byte[UncompressedSize];
		foreach (PfsBlock block in Blocks)
		{
			Span<byte> span = array.AsSpan((int)block.UncompressedOffset, block.UncompressedSize);
			ReadOnlyMemory<byte> compressedData;
			if (block.IsStored)
			{
				compressedData = block.CompressedData;
				compressedData.Span.CopyTo(span);
				continue;
			}
			compressedData = block.CompressedData;
			KrakenDecodeStatus krakenDecodeStatus = KrakenDecoder.DecodeBlock(compressedData.Span, block.Flags, block.FirstChunkCompressedSize, span);
			if (krakenDecodeStatus == KrakenDecodeStatus.Success)
			{
				continue;
			}
			throw new InvalidDataException($"Block {block.Index} could not be decoded ({krakenDecodeStatus}).");
		}
		return array;
	}

	/// <summary>
	/// Returns <c>true</c> when <paramref name="data" /> begins with a PS5 PFSv2/PFSv3 compression
	/// container, distinguishing it from the zlib "PFSC" image (which stores 0 in the
	/// version field).
	/// </summary>
	public static bool IsScePfsCompressed(ReadOnlySpan<byte> data)
	{
		if (data.Length < 88)
		{
			return false;
		}
		if (BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(0)) != 1129530960)
		{
			return false;
		}
		ushort num = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4));
		if ((uint)(num - 2) <= 1u)
		{
			return true;
		}
		return false;
	}

	/// <summary>Parses a PS5 PFSv2/PFSv3 compression container from <paramref name="buffer" />.</summary>
	/// <exception cref="T:System.ArgumentNullException"><paramref name="buffer" /> is null.</exception>
	/// <exception cref="T:System.IO.InvalidDataException">The buffer is not a valid PS5 PFS compression container.</exception>
	public static CompressedPfsFile Parse(byte[] buffer)
	{
		ArgumentNullException.ThrowIfNull(buffer, "buffer");
		return Parse(buffer.AsMemory());
	}

	/// <summary>Parses a PS5 PFSv2/PFSv3 compression container from <paramref name="buffer" />.</summary>
	/// <exception cref="T:System.IO.InvalidDataException">The buffer is not a valid PS5 PFS compression container.</exception>
	public static CompressedPfsFile Parse(ReadOnlyMemory<byte> buffer)
	{
		ReadOnlySpan<byte> span = buffer.Span;
		if (span.Length < 88)
		{
			throw new InvalidDataException("Buffer is too small to be a PFS compression container.");
		}
		if (BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(0)) != 1129530960)
		{
			throw new InvalidDataException("Missing 'PFSC' magic.");
		}
		ushort num = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(4));
		if ((uint)(num - 2) > 1u)
		{
			throw new InvalidDataException($"Unsupported PFS compression version {num}; expected 2 or 3 (a zlib 'PFSC' image stores 0 here).");
		}
		long uncompressedSize;
		long totalCompressedSize;
		Dictionary<int, (long Offset, long Size)> sections;
		long dataOffset;
		List<PfsBlock> blocks;
		ReadOnlyMemory<byte> gitHash;
		checked
		{
			uncompressedSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(24));
			totalCompressedSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(32));
			sections = ReadSectionDirectory(span);
			long num2;
			if (sections.TryGetValue(7, out (long, long) value))
			{
				(num2, _) = value;
			}
			else
			{
				num2 = 0L;
			}
			dataOffset = num2;
			blocks = ReadBlocks(buffer, span, sections, dataOffset);
			gitHash = ReadOnlyMemory<byte>.Empty;
		}
		if (sections.TryGetValue(1, out (long, long) value2) && InRange(span.Length, value2.Item1, value2.Item2))
		{
			gitHash = buffer.Slice((int)value2.Item1, (int)value2.Item2);
		}
		int len = span.Length;
		return new CompressedPfsFile(buffer)
		{
			Version = ((num == 3) ? PfsCompressionFormat.Version3 : PfsCompressionFormat.Version2),
			Algorithm = (CompressionAlgorithm)span[16],
			CompressionLevel = (sbyte)span[17],
			WindowBits = span[18],
			BlockSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8))),
			UncompressedSize = uncompressedSize,
			TotalCompressedSize = totalCompressedSize,
			FileDigest = buffer.Slice(40, 32),
			GitHash = gitHash,
			DataOffset = dataOffset,
			Blocks = blocks,
			_shuffleRange = RangeOf(2),
			_boundaryRange = RangeOf(3),
			_blockHashRange = RangeOf(4)
		};
		(int, int) RangeOf(int id)
		{
			if (!sections.TryGetValue(id, out (long, long) value3) || !InRange(len, value3.Item1, value3.Item2))
			{
				return (0, 0);
			}
			return ((int)value3.Item1, (int)value3.Item2);
		}
	}

	private static Dictionary<int, (long Offset, long Size)> ReadSectionDirectory(ReadOnlySpan<byte> span)
	{
		Dictionary<int, (long, long)> dictionary = new Dictionary<int, (long, long)>();
		int num = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(6));
		uint num2 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(74));
		long num3 = ((num2 >= 88 && num2 <= span.Length) ? ((long)num2) : ((long)span.Length));
		int num4 = 0;
		long num5 = 72L;
		while (num5 + 16 <= num3 && (num == 0 || num4 < num))
		{
			int num6 = (int)num5;
			ushort num7 = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(num6));
			if (num7 == 0)
			{
				break;
			}
			long item = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(num6 + 2));
			long item2 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(num6 + 10));
			dictionary[num7] = (item, item2);
			num5 += 16;
			num4++;
		}
		return dictionary;
	}

	private static List<PfsBlock> ReadBlocks(ReadOnlyMemory<byte> buffer, ReadOnlySpan<byte> span, Dictionary<int, (long Offset, long Size)> sections, long dataOffset)
	{
		List<PfsBlock> list = new List<PfsBlock>();
		if (!sections.TryGetValue(3, out (long, long) value))
		{
			throw new InvalidDataException("Compression container is missing the block-boundary section.");
		}
		if (value.Item2 < 32)
		{
			return list;
		}
		if (!InRange(span.Length, value.Item1, value.Item2))
		{
			throw new InvalidDataException("Block boundary table is out of range.");
		}
		int num = (int)(value.Item2 / 16) - 1;
		sections.TryGetValue(4, out (long, long) value2);
		bool flag = value2.Item2 >= (long)num * 32L && InRange(span.Length, value2.Item1, value2.Item2);
		for (int i = 0; i < num; i++)
		{
			int num2 = (int)value.Item1 + i * 16;
			ulong num3 = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(num2));
			ulong num4 = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(num2 + 8));
			long num5 = (long)(num3 & 0xFFFFFFFFFFFL);
			ulong num6 = (num3 >> 48) & 0xFF;
			long num7 = (long)(num4 & 0xFFFFFFFFFFFL);
			int num8 = (int)((num4 >> 44) & 0x1FFFF);
			ulong num9 = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(num2 + 16)) & 0xFFFFFFFFFFFL;
			ulong num10 = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(num2 + 16 + 8)) & 0xFFFFFFFFFFFL;
			long num11 = (long)num9 - num5;
			long num12 = (long)num10 - num7;
			if (num11 < 0 || num12 < 0 || num11 > int.MaxValue || num12 > int.MaxValue)
			{
				throw new InvalidDataException($"Block {i} has an invalid size in the boundary table.");
			}
			int num13 = (int)num11;
			int num14 = (int)num12;
			long num15 = dataOffset + num5;
			if (!InRange(span.Length, num15, num13))
			{
				throw new InvalidDataException($"Block {i} compressed data is out of range.");
			}
			bool flag2 = num13 == num14;
			bool flag3 = !flag2 && num14 > 131072;
			bool isBareEntropy = !flag2 && (num6 & 2) == 0;
			int firstChunkCompressedSize = (flag3 ? (num8 + 1) : 0);
			int literalMode = (((num6 & 1) == 0) ? 1 : 0);
			ReadOnlyMemory<byte> hash = ReadOnlyMemory<byte>.Empty;
			if (flag)
			{
				hash = buffer.Slice((int)value2.Item1 + i * 32, 32);
			}
			list.Add(new PfsBlock
			{
				Index = i,
				CompressedOffset = num15,
				CompressedSize = num13,
				UncompressedOffset = num7,
				UncompressedSize = num14,
				Hash = hash,
				CompressedData = buffer.Slice((int)num15, num13),
				IsMultiChunk = flag3,
				IsBareEntropy = isBareEntropy,
				FirstChunkCompressedSize = firstChunkCompressedSize,
				LiteralMode = literalMode,
				Flags = (int)num6
			});
		}
		return list;
	}

	private static bool InRange(int length, long offset, long size)
	{
		if (offset >= 0 && size >= 0)
		{
			return offset + size <= length;
		}
		return false;
	}
}
