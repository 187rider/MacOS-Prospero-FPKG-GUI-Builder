using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace LibProsperoPkg.PKG;

public static class ProsperoNapsLayout
{
	public const int DefaultAlignment = 16;

	public const string FileName = "naps_pkg_layout.dat";

	public const int HeaderSize = 16;

	public const int OuterBlockDigestStride = 8;

	public const int ShufflePatternStride = 8;

	public const int FileOffsetStride = 6;

	public const int U2cStride = 10;

	public const int CblockInfoStride = 9;

	public const int DefaultFooterSize = 8;

	public static NapsLayoutCounts DecodeHeader(ReadOnlySpan<byte> header)
	{
		if (header.Length < 16)
		{
			throw new ArgumentException($"NAPS header needs {16} bytes.", "header");
		}
		ulong num = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(0, 8));
		ulong num2 = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(8, 8));
		int num3 = (int)(num & 0xFFFFFF);
		byte compressionType = (byte)((num >> 24) & 3);
		int num4 = (int)((num >> 26) & 3);
		int numShufflePatterns = (int)((num >> 28) & 0xF);
		if (num >> 55 != 0L || num2 >> 48 != 0L)
		{
			throw new InvalidDataException("NAPS header reserved bits are non-zero.");
		}
		int numUBlocks = (int)((num >> 32) & 0x7FFFFF);
		int numOuterBlocks = (int)(num2 & 0xFFFFFF);
		int num5 = (int)((num2 >> 24) & 0xFFFFFF);
		return new NapsLayoutCounts(num3 + 1, compressionType, num4 + 1, numShufflePatterns, numUBlocks, numOuterBlocks, num5 + 2);
	}

	public static byte[] EncodeHeader(NapsLayoutCounts counts)
	{
		ValidateCounts(counts);
		ulong value = ((ulong)(uint)(counts.NumFiles - 1) & 0xFFFFFFuL) | (ulong)((long)(counts.CompressionType & 3) << 24) | ((ulong)(uint)((counts.NumKeys - 1) & 3) << 26) | ((ulong)(uint)(counts.NumShufflePatterns & 0xF) << 28) | ((ulong)(uint)(counts.NumUBlocks & 0x7FFFFF) << 32);
		ulong value2 = (uint)(counts.NumOuterBlocks & 0xFFFFFF) | ((ulong)(uint)((counts.NumCblockInfo - 2) & 0xFFFFFF) << 24);
		byte[] array = new byte[16];
		BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(0, 8), value);
		BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(8, 8), value2);
		return array;
	}

	public static NapsSectionMap SectionMap(NapsLayoutCounts counts)
	{
		long num = 0L;
		NapsSection header = new NapsSection(num, 16L, 16, 1);
		num += 16;
		NapsSection outerBlockDigest = new NapsSection(num, (long)counts.NumOuterBlocks * 8L, 8, counts.NumOuterBlocks);
		num += outerBlockDigest.Size;
		NapsSection shufflePattern = new NapsSection(num, (long)counts.NumShufflePatterns * 8L, 8, counts.NumShufflePatterns);
		num += shufflePattern.Size;
		NapsSection uncompressedOffsetStartByFileIdx = new NapsSection(num, (long)counts.NumFiles * 6L, 6, counts.NumFiles);
		num += uncompressedOffsetStartByFileIdx.Size;
		int numU2cEntries = counts.NumU2cEntries;
		NapsSection cblockInfoOffsetByUblockIdxCompressed = new NapsSection(num, (long)numU2cEntries * 10L, 10, numU2cEntries);
		num += cblockInfoOffsetByUblockIdxCompressed.Size;
		num = AlignUp(num, 8);
		NapsSection cblockInfo = new NapsSection(num, (long)counts.NumCblockInfo * 9L, 9, counts.NumCblockInfo);
		num += cblockInfo.Size;
		return new NapsSectionMap(TotalSize: AlignUp(num, 8), Header: header, OuterBlockDigest: outerBlockDigest, ShufflePattern: shufflePattern, UncompressedOffsetStartByFileIdx: uncompressedOffsetStartByFileIdx, CblockInfoOffsetByUblockIdxCompressed: cblockInfoOffsetByUblockIdxCompressed, CblockInfo: cblockInfo);
	}

	public static NapsFileOffsetEntry DecodeFileOffsetEntry(ReadOnlySpan<byte> entry)
	{
		if (entry.Length < 6)
		{
			throw new ArgumentException($"fidx entry needs {6} bytes.", "entry");
		}
		ulong num = 0uL;
		for (int i = 0; i < 5; i++)
		{
			num |= (ulong)entry[i] << 8 * i;
		}
		return new NapsFileOffsetEntry(entry[5], num);
	}

	public static byte[] EncodeFileOffsetEntry(NapsFileOffsetEntry entry)
	{
		byte[] array = new byte[6];
		if (entry.UncompressedOffsetStart > 1099511627775L)
		{
			throw new ArgumentOutOfRangeException("entry", "NAPS file offset exceeds 40 bits.");
		}
		ulong uncompressedOffsetStart = entry.UncompressedOffsetStart;
		for (int i = 0; i < 5; i++)
		{
			array[i] = (byte)(uncompressedOffsetStart >> 8 * i);
		}
		array[5] = entry.Type;
		return array;
	}

	public static NapsU2cEntry DecodeU2cEntry(ReadOnlySpan<byte> entry)
	{
		if (entry.Length < 10)
		{
			throw new ArgumentException($"u2c entry needs {10} bytes.", "entry");
		}
		int infoOffset9BBase = entry[0] | (entry[1] << 8) | (entry[2] << 16);
		byte[] array = new byte[7];
		entry.Slice(3, 7).CopyTo(array);
		return new NapsU2cEntry((uint)infoOffset9BBase, array);
	}

	public static byte[] EncodeU2cEntry(NapsU2cEntry entry)
	{
		byte[] array = new byte[10]
		{
			(byte)entry.InfoOffset9BBase,
			(byte)(entry.InfoOffset9BBase >> 8),
			(byte)(entry.InfoOffset9BBase >> 16),
			0,
			0,
			0,
			0,
			0,
			0,
			0
		};
		for (int i = 0; i < 7 && i < entry.DeltaFromBase.Length; i++)
		{
			array[3 + i] = entry.DeltaFromBase[i];
		}
		return array;
	}

	public static NapsCblockInfoEntry DecodeCblockInfoEntry(ReadOnlySpan<byte> entry)
	{
		if (entry.Length < 9)
		{
			throw new ArgumentException($"cblockinfo entry needs {9} bytes.", "entry");
		}
		byte[] array = new byte[9];
		entry.Slice(0, 9).CopyTo(array);
		ulong num = 0uL;
		for (int i = 0; i < 8; i++)
		{
			num |= (ulong)array[i] << 8 * i;
		}
		UInt128 uInt = num | ((UInt128)array[8] << 64);
		bool flag = ((num >> 18) & 1) != 0;
		uint num2 = (uint)(num & 0x3FFFF);
		if (!flag)
		{
			return new NapsCblockInfoEntry
			{
				Raw = array,
				IsRunBase = false,
				CoffsetStartMod256K = num2,
				ReservedBit19 = (((uInt >> 19) & (byte)1) != (byte)0),
				UoffsetStart = (uint)((uInt >> 20) & 262143u),
				ClenEvenMinus1 = (uint)((uInt >> 38) & 131071u),
				Even = (byte)((uInt >> 55) & (byte)7),
				Odd = (byte)((uInt >> 58) & (byte)7),
				KdePredictor = (byte)((uInt >> 61) & (byte)63),
				ReservedBit67 = (((uInt >> 67) & (byte)1) != (byte)0),
				ShuffleIdx = (byte)((uInt >> 68) & (byte)15)
			};
		}
		return new NapsCblockInfoEntry
		{
			Raw = array,
			IsRunBase = true,
			CoffsetEndMod256K = num2,
			TweakIdxStart = (uint)((uInt >> 20) & 268435455u),
			KeyTableIdx = (byte)((uInt >> 48) & (byte)3),
			CoffsetStart256K = (uint)((uInt >> 50) & 4194303u)
		};
	}

	public static NapsLayoutDocument Parse(ReadOnlySpan<byte> blob)
	{
		return Parse(blob, DecodeHeader(blob));
	}

	public static NapsLayoutDocument Parse(ReadOnlySpan<byte> blob, NapsLayoutCounts counts)
	{
		NapsSectionMap map = SectionMap(counts);
		if (blob.Length < map.TotalSize)
		{
			throw new ArgumentException($"NAPS blob is {blob.Length} bytes but the counts require {map.TotalSize}.", "blob");
		}
		int trailingZeroBytes = checked(blob.Length - (int)map.TotalSize);
		for (int i = (int)map.TotalSize; i < blob.Length; i++)
		{
			if (blob[i] != 0)
			{
				throw new InvalidDataException("NAPS trailing footer contains non-zero bytes.");
			}
		}
		List<byte[]> outerBlockDigests = SliceRaw(blob, map.OuterBlockDigest);
		List<byte[]> shufflePatterns = SliceRaw(blob, map.ShufflePattern);
		List<NapsFileOffsetEntry> list = new List<NapsFileOffsetEntry>(map.UncompressedOffsetStartByFileIdx.Count);
		for (int j = 0; j < map.UncompressedOffsetStartByFileIdx.Count; j++)
		{
			list.Add(DecodeFileOffsetEntry(EntrySpan(blob, map.UncompressedOffsetStartByFileIdx, j)));
		}
		List<NapsU2cEntry> list2 = new List<NapsU2cEntry>(map.CblockInfoOffsetByUblockIdxCompressed.Count);
		for (int k = 0; k < map.CblockInfoOffsetByUblockIdxCompressed.Count; k++)
		{
			list2.Add(DecodeU2cEntry(EntrySpan(blob, map.CblockInfoOffsetByUblockIdxCompressed, k)));
		}
		List<NapsCblockInfoEntry> list3 = new List<NapsCblockInfoEntry>(map.CblockInfo.Count);
		for (int l = 0; l < map.CblockInfo.Count; l++)
		{
			list3.Add(DecodeCblockInfoEntry(EntrySpan(blob, map.CblockInfo, l)));
		}
		return new NapsLayoutDocument
		{
			Counts = counts,
			Map = map,
			OuterBlockDigests = outerBlockDigests,
			ShufflePatterns = shufflePatterns,
			FileOffsets = list,
			CblockInfoOffsetByUblock = list2,
			CblockInfos = list3,
			TrailingZeroBytes = trailingZeroBytes
		};
	}

	public static byte[] BuildLayout(NapsLayoutDocument document, int trailingZeroBytes = -1)
	{
		ArgumentNullException.ThrowIfNull(document, "document");
		NapsLayoutCounts counts = document.Counts;
		if (document.OuterBlockDigests.Count != counts.NumOuterBlocks)
		{
			throw new ArgumentException($"OuterBlockDigests count {document.OuterBlockDigests.Count} != NumOuterBlocks {counts.NumOuterBlocks}.", "document");
		}
		if (document.ShufflePatterns.Count != counts.NumShufflePatterns)
		{
			throw new ArgumentException($"ShufflePatterns count {document.ShufflePatterns.Count} != NumShufflePatterns {counts.NumShufflePatterns}.", "document");
		}
		if (document.FileOffsets.Count != counts.NumFiles)
		{
			throw new ArgumentException($"FileOffsets count {document.FileOffsets.Count} != NumFiles {counts.NumFiles}.", "document");
		}
		if (document.CblockInfoOffsetByUblock.Count != counts.NumU2cEntries)
		{
			throw new ArgumentException($"u2c count {document.CblockInfoOffsetByUblock.Count} != NumU2cEntries {counts.NumU2cEntries}.", "document");
		}
		if (document.CblockInfos.Count != counts.NumCblockInfo)
		{
			throw new ArgumentException($"CblockInfos count {document.CblockInfos.Count} != NumCblockInfo {counts.NumCblockInfo}.", "document");
		}
		ValidateDocument(document);
		NapsSectionMap napsSectionMap = SectionMap(counts);
		long totalSize = napsSectionMap.TotalSize;
		int num;
		if (trailingZeroBytes < 0)
		{
			num = ((document.TrailingZeroBytes != 0) ? document.TrailingZeroBytes : 8);
		}
		else
		{
			num = trailingZeroBytes;
		}
		byte[] array = new byte[checked(totalSize + num)];
		Span<byte> destination = array.AsSpan();
		EncodeHeader(counts).CopyTo(destination);
		int num2 = 16;
		foreach (byte[] outerBlockDigest in document.OuterBlockDigests)
		{
			if (outerBlockDigest.Length != 8)
			{
				throw new ArgumentException($"outer-block digest must be {8} bytes.", "document");
			}
			outerBlockDigest.CopyTo(destination.Slice(num2, 8));
			num2 += 8;
		}
		foreach (byte[] shufflePattern in document.ShufflePatterns)
		{
			if (shufflePattern.Length != 8)
			{
				throw new ArgumentException($"shuffle pattern must be {8} bytes.", "document");
			}
			shufflePattern.CopyTo(destination.Slice(num2, 8));
			num2 += 8;
		}
		foreach (NapsFileOffsetEntry fileOffset in document.FileOffsets)
		{
			EncodeFileOffsetEntry(fileOffset).CopyTo(destination.Slice(num2, 6));
			num2 += 6;
		}
		foreach (NapsU2cEntry item in document.CblockInfoOffsetByUblock)
		{
			EncodeU2cEntry(item).CopyTo(destination.Slice(num2, 10));
			num2 += 10;
		}
		num2 = checked((int)napsSectionMap.CblockInfo.Offset);
		foreach (NapsCblockInfoEntry cblockInfo in document.CblockInfos)
		{
			EncodeCblockInfoEntry(cblockInfo).CopyTo(destination.Slice(num2, 9));
			num2 += 9;
		}
		return array;
	}

	private static ReadOnlySpan<byte> EntrySpan(ReadOnlySpan<byte> blob, NapsSection section, int index)
	{
		return blob.Slice((int)section.Offset + index * section.Stride, section.Stride);
	}

	private static List<byte[]> SliceRaw(ReadOnlySpan<byte> blob, NapsSection section)
	{
		List<byte[]> list = new List<byte[]>(section.Count);
		for (int i = 0; i < section.Count; i++)
		{
			byte[] array = new byte[section.Stride];
			blob.Slice((int)section.Offset + i * section.Stride, section.Stride).CopyTo(array);
			list.Add(array);
		}
		return list;
	}

	public static byte[] EncodeCblockInfoEntry(NapsCblockInfoEntry entry)
	{
		UInt128 uInt = (entry.IsRunBase ? entry.CoffsetEndMod256K : entry.CoffsetStartMod256K) & 0x3FFFF;
		if (!entry.IsRunBase)
		{
			RequireFits(entry.UoffsetStart, 18, "UoffsetStart");
			RequireFits(entry.ClenEvenMinus1, 17, "ClenEvenMinus1");
			RequireFits(entry.Even, 3, "Even");
			RequireFits(entry.Odd, 3, "Odd");
			RequireFits(entry.KdePredictor, 6, "KdePredictor");
			RequireFits(entry.ShuffleIdx, 4, "ShuffleIdx");
			if (entry.ReservedBit19)
			{
				uInt |= (UInt128)1 << 19;
			}
			uInt |= (UInt128)entry.UoffsetStart << 20;
			uInt |= (UInt128)entry.ClenEvenMinus1 << 38;
			uInt |= (UInt128)entry.Even << 55;
			uInt |= (UInt128)entry.Odd << 58;
			uInt |= (UInt128)entry.KdePredictor << 61;
			if (entry.ReservedBit67)
			{
				uInt |= (UInt128)1 << 67;
			}
			uInt |= (UInt128)entry.ShuffleIdx << 68;
		}
		else
		{
			RequireFits(entry.TweakIdxStart, 28, "TweakIdxStart");
			RequireFits(entry.KeyTableIdx, 2, "KeyTableIdx");
			RequireFits(entry.CoffsetStart256K, 22, "CoffsetStart256K");
			uInt |= (UInt128)1 << 18;
			uInt |= (UInt128)entry.TweakIdxStart << 20;
			uInt |= (UInt128)entry.KeyTableIdx << 48;
			uInt |= (UInt128)entry.CoffsetStart256K << 50;
		}
		byte[] array = new byte[9];
		for (int i = 0; i < array.Length; i++)
		{
			array[i] = (byte)(uInt >> 8 * i);
		}
		return array;
	}

	private static void ValidateCounts(NapsLayoutCounts counts)
	{
		bool flag = counts.NumFiles < 1 || counts.NumFiles > 8388608 || counts.NumKeys < 1 || counts.NumKeys > 4;
		if (!flag)
		{
			int numShufflePatterns = counts.NumShufflePatterns;
			flag = ((numShufflePatterns < 0 || numShufflePatterns > 15) ? true : false);
		}
		bool flag2 = flag;
		if (!flag2)
		{
			int numUBlocks = counts.NumUBlocks;
			flag2 = ((numUBlocks < 0 || numUBlocks > 4194304) ? true : false);
		}
		bool flag3 = flag2;
		if (!flag3)
		{
			int numOuterBlocks = counts.NumOuterBlocks;
			flag3 = ((numOuterBlocks < 0 || numOuterBlocks > 16777215) ? true : false);
		}
		bool flag4 = flag3;
		if (!flag4)
		{
			int numCblockInfo = counts.NumCblockInfo;
			flag4 = ((numCblockInfo < 2 || numCblockInfo > 16777217) ? true : false);
		}
		if (flag4)
		{
			throw new ArgumentOutOfRangeException("counts", "NAPS counts exceed the kernel G6 profile.");
		}
	}

	private static void ValidateDocument(NapsLayoutDocument document)
	{
		if (document.FileOffsets.Count != 0 && document.FileOffsets[0].Continuation)
		{
			throw new InvalidDataException("The first NAPS file entry cannot be a continuation.");
		}
		ulong num = checked((ulong)document.Counts.UBlockCount) << 18;
		ulong num2 = 0uL;
		for (int i = 0; i < document.FileOffsets.Count; i++)
		{
			ulong uncompressedOffsetStart = document.FileOffsets[i].UncompressedOffsetStart;
			if (uncompressedOffsetStart < num2 || uncompressedOffsetStart > num)
			{
				throw new InvalidDataException($"NAPS file boundary {i} is invalid: current=0x{uncompressedOffsetStart:X}, previous=0x{num2:X}, maximum=0x{num:X}.");
			}
			num2 = uncompressedOffsetStart;
		}
		for (int j = 0; j < document.Counts.UBlockCount; j++)
		{
			NapsU2cEntry napsU2cEntry = document.CblockInfoOffsetByUblock[j >> 3];
			uint num3 = ((j % 8 == 0) ? napsU2cEntry.InfoOffset9BBase : checked(napsU2cEntry.InfoOffset9BBase + napsU2cEntry.DeltaFromBase[(j & 7) - 1]));
			int num5;
			if (document.FileOffsets.Count != 0)
			{
				ulong num4 = checked((ulong)j) << 18;
				IReadOnlyList<NapsFileOffsetEntry> fileOffsets = document.FileOffsets;
				num5 = ((num4 >= fileOffsets[fileOffsets.Count - 1].UncompressedOffsetStart) ? 1 : 0);
			}
			else
			{
				num5 = 0;
			}
			bool flag = (byte)num5 != 0;
			if (num3 >= document.CblockInfos.Count || document.CblockInfos[(int)num3].IsRunBase || (document.CblockInfos[(int)num3].IsTerminal && !flag))
			{
				throw new InvalidDataException($"NAPS ublock {j} has an invalid CblockInfo reference.");
			}
		}
		int num6 = 0;
		for (int k = 0; k < document.CblockInfos.Count; k++)
		{
			NapsCblockInfoEntry napsCblockInfoEntry = document.CblockInfos[k];
			if ((k & 0xF) == 0 && !napsCblockInfoEntry.IsRunBase)
			{
				throw new InvalidDataException($"NAPS CblockInfo window 0x{k:X} does not begin with a RUN base.");
			}
			if (napsCblockInfoEntry.IsTerminal)
			{
				num6++;
			}
			else if (!napsCblockInfoEntry.IsRunBase && napsCblockInfoEntry.ShuffleIdx > document.Counts.NumShufflePatterns)
			{
				throw new InvalidDataException($"NAPS shuffle index {napsCblockInfoEntry.ShuffleIdx} exceeds the {document.Counts.NumShufflePatterns}-entry table and identity sentinel.");
			}
		}
		if (num6 == 1)
		{
			IReadOnlyList<NapsCblockInfoEntry> cblockInfos = document.CblockInfos;
			if (cblockInfos[cblockInfos.Count - 1].IsTerminal)
			{
				return;
			}
		}
		throw new InvalidDataException("NAPS CblockInfo must end in exactly one terminal boundary.");
	}

	private static long AlignUp(long value, int alignment)
	{
		checked
		{
			return unchecked(checked(value + alignment - 1) / alignment) * alignment;
		}
	}

	private static void RequireFits(uint value, int bits, string name)
	{
		if (value >= (uint)(1 << bits))
		{
			throw new ArgumentOutOfRangeException(name);
		}
	}
}
