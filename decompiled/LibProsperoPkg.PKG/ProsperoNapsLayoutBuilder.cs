using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibProsperoPkg.PFS;

namespace LibProsperoPkg.PKG;

public static class ProsperoNapsLayoutBuilder
{
	private const long UBlock = 262144L;

	private const uint Mod256K = 262143u;

	private const uint ClenEvenCap = 131071u;

	public static NapsLayoutDocument BuildDocument(NapsGenerationRequest request)
	{
		ArgumentNullException.ThrowIfNull(request, "request");
		if (request.Blocks == null || request.Blocks.Count == 0)
		{
			throw new ArgumentException("A naps generation request needs at least one block.", "request");
		}
		if (request.FileLogicalOffsets == null || request.FileLogicalOffsets.Count == 0)
		{
			throw new ArgumentException("A naps generation request needs the afid logical offset table.", "request");
		}
		if (request.FileOffsetTypes != null && request.FileOffsetTypes.Count != request.FileLogicalOffsets.Count)
		{
			throw new ArgumentException("The optional FIDX type table must match the logical-offset table.", "request");
		}
		(List<NapsCblockInfoEntry> Entries, List<(int Index, long Logical)> StdLogical) tuple = WalkBlocks(request.Blocks);
		List<NapsCblockInfoEntry> item = tuple.Entries;
		List<(int Index, long Logical)> item2 = tuple.StdLogical;
		NapsLayoutCounts counts = new NapsLayoutCounts(request.FileLogicalOffsets.Count, request.CompressionType, request.NumKeys, request.ShufflePatterns?.Count ?? 0, checked(request.NumUBlocks - 1), request.NumOuterBlocks, item.Count);
		List<NapsFileOffsetEntry> fileOffsets = BuildFileOffsets(request);
		List<NapsU2cEntry> cblockInfoOffsetByUblock = BuildU2c(item2, counts.UBlockCount, counts.NumCblockInfo, counts.NumU2cEntries);
		IReadOnlyList<byte[]> outerBlockDigests = request.OuterBlockDigests ?? (from _ in Enumerable.Range(0, request.NumOuterBlocks)
			select new byte[8]).ToList();
		IReadOnlyList<byte[]> shufflePatterns = request.ShufflePatterns ?? Array.Empty<byte[]>();
		return new NapsLayoutDocument
		{
			Counts = counts,
			Map = ProsperoNapsLayout.SectionMap(counts),
			OuterBlockDigests = outerBlockDigests,
			ShufflePatterns = shufflePatterns,
			FileOffsets = fileOffsets,
			CblockInfoOffsetByUblock = cblockInfoOffsetByUblock,
			CblockInfos = item
		};
	}

	public static byte[] Build(NapsGenerationRequest request, int alignment = 16)
	{
		return ProsperoNapsLayout.BuildLayout(BuildDocument(request), alignment);
	}

	public static List<NapsCblockPlanEntry> DeriveDataRegionBlocks(IReadOnlyList<NapsFilePlacement> files, IReadOnlyCollection<long> runStartOnDiskOffsets)
	{
		ArgumentNullException.ThrowIfNull(files, "files");
		ArgumentNullException.ThrowIfNull(runStartOnDiskOffsets, "runStartOnDiskOffsets");
		HashSet<long> hashSet = new HashSet<long>(runStartOnDiskOffsets);
		List<NapsCblockPlanEntry> list = new List<NapsCblockPlanEntry>();
		foreach (NapsFilePlacement file in files)
		{
			if (file.UncompressedSize == 0L && file.OnDiskSize == 0L)
			{
				continue;
			}
			if (file.StoreRaw)
			{
				long num = file.UncompressedSize / 262144;
				long num2 = file.UncompressedSize - num * 262144;
				for (long num3 = 0L; num3 < num; num3++)
				{
					long num4 = file.OnDiskOffset + num3 * 262144;
					list.Add(new NapsCblockPlanEntry
					{
						StartRun = hashSet.Contains(num4),
						OnDiskOffset = num4,
						LogicalOffset = file.LogicalOffset + num3 * 262144,
						EvenChunkCompressedLength = 131072L,
						StreamLength = 262144L,
						Even = 1,
						Odd = 1,
						KdePredictor = 0,
						ShuffleIndex = 0
					});
				}
				if (num2 > 0)
				{
					long num5 = file.OnDiskOffset + num * 262144;
					list.Add(new NapsCblockPlanEntry
					{
						StartRun = hashSet.Contains(num5),
						OnDiskOffset = num5,
						LogicalOffset = file.LogicalOffset + num * 262144,
						EvenChunkCompressedLength = Math.Min(num2, 131072L),
						StreamLength = num2,
						Even = 1,
						Odd = ((num2 > 131072) ? ((byte)1) : ((byte)0)),
						KdePredictor = 0,
						ShuffleIndex = 0
					});
				}
			}
			else if (file.CompressionBlocks.Count != 0)
			{
				long num6 = file.OnDiskOffset;
				long num7 = file.LogicalOffset;
				foreach (ProsperoInnerDataBlockChunk compressionBlock in file.CompressionBlocks)
				{
					int num8;
					if (compressionBlock.IsStored)
					{
						num8 = Math.Min(compressionBlock.CompressedSize, 131072);
					}
					else
					{
						num8 = (compressionBlock.IsMultiChunk ? compressionBlock.FirstChunkCompressedSize : compressionBlock.CompressedSize);
					}
					list.Add(new NapsCblockPlanEntry
					{
						StartRun = hashSet.Contains(num6),
						OnDiskOffset = num6,
						LogicalOffset = num7,
						EvenChunkCompressedLength = num8,
						StreamLength = compressionBlock.CompressedSize,
						Even = (byte)(compressionBlock.IsStored ? 1 : ToNapsHalfMode(compressionBlock.BoundaryFlags, firstHalf: true)),
						Odd = (compressionBlock.IsStored ? ((compressionBlock.CompressedSize > 131072) ? ((byte)1) : ((byte)0)) : ((byte)(compressionBlock.IsMultiChunk ? ToNapsHalfMode(compressionBlock.BoundaryFlags, firstHalf: false) : 0))),
						KdePredictor = 0,
						ShuffleIndex = 0
					});
					num6 += compressionBlock.CompressedSize;
					num7 += compressionBlock.UncompressedSize;
				}
			}
			else
			{
				list.Add(new NapsCblockPlanEntry
				{
					StartRun = hashSet.Contains(file.OnDiskOffset),
					OnDiskOffset = file.OnDiskOffset,
					LogicalOffset = file.LogicalOffset,
					EvenChunkCompressedLength = file.OnDiskSize,
					StreamLength = file.OnDiskSize,
					Even = 5,
					Odd = 0,
					KdePredictor = 0,
					ShuffleIndex = 0
				});
			}
		}
		return list;
	}

	private static byte ToNapsHalfMode(int boundaryFlags, bool firstHalf)
	{
		int num = (firstHalf ? 2 : 32);
		int num2 = (firstHalf ? 1 : 16);
		if (boundaryFlags == 0)
		{
			return (byte)(4u | (firstHalf ? 1u : 0u));
		}
		int num3 = (((boundaryFlags & num) != 0) ? 4 : ((!firstHalf) ? 1 : 0));
		if ((boundaryFlags & num2) != 0)
		{
			num3 |= 2;
		}
		if (firstHalf)
		{
			num3 |= 1;
		}
		return checked((byte)num3);
	}

	public static NapsLayoutDocument BuildFromInnerImage(int numUBlocks, int numOuterBlocks, IReadOnlyList<NapsFilePlacement> files, IReadOnlyCollection<long> runStartOnDiskOffsets, IReadOnlyList<NapsCblockPlanEntry> tailBlocks, IReadOnlyList<long> fileLogicalOffsets, IReadOnlyList<byte[]>? outerBlockDigests = null, byte finalFileOffsetType = 64)
	{
		ArgumentNullException.ThrowIfNull(tailBlocks, "tailBlocks");
		List<NapsCblockPlanEntry> list = DeriveDataRegionBlocks(files, runStartOnDiskOffsets);
		list.AddRange(tailBlocks);
		return BuildDocument(new NapsGenerationRequest
		{
			NumUBlocks = numUBlocks,
			NumOuterBlocks = numOuterBlocks,
			FileLogicalOffsets = fileLogicalOffsets,
			FinalFileOffsetType = finalFileOffsetType,
			Blocks = list,
			OuterBlockDigests = outerBlockDigests
		});
	}

	private static (List<NapsCblockInfoEntry> Entries, List<(int Index, long Logical)> StdLogical) WalkBlocks(IReadOnlyList<NapsCblockPlanEntry> blocks)
	{
		List<NapsCblockInfoEntry> list = new List<NapsCblockInfoEntry>(blocks.Count * 2);
		List<(int, long)> list2 = new List<(int, long)>(blocks.Count);
		long num = 0L;
		for (int i = 0; i < blocks.Count; i++)
		{
			NapsCblockPlanEntry napsCblockPlanEntry = blocks[i];
			if (napsCblockPlanEntry.StartRun || list.Count % 16 == 0)
			{
				uint coffsetEndMod256K2 = (uint)(num & 0x3FFFF);
				long num2 = (napsCblockPlanEntry.StartRun ? napsCblockPlanEntry.OnDiskOffset : num);
				num = num2;
				uint tweakIdxStart = (uint)((!napsCblockPlanEntry.Terminator) ? (num2 >> 16) : 0);
				uint coffsetStart256K = (uint)(num2 / 262144);
				list.Add(new NapsCblockInfoEntry
				{
					Raw = new byte[9],
					IsRunBase = true,
					CoffsetEndMod256K = coffsetEndMod256K2,
					TweakIdxStart = tweakIdxStart,
					KeyTableIdx = 0,
					CoffsetStart256K = coffsetStart256K
				});
			}
			else if (NeedsCursorPreservingRun(blocks, i, list.Count))
			{
				uint coffsetEndMod256K = (uint)(num & 0x3FFFF);
				list.Add(new NapsCblockInfoEntry
				{
					Raw = new byte[9],
					IsRunBase = true,
					CoffsetEndMod256K = coffsetEndMod256K,
					TweakIdxStart = (uint)(num >> 16),
					KeyTableIdx = 0,
					CoffsetStart256K = (uint)(num / 262144)
				});
			}
			uint coffsetStartMod256K = (uint)(num & 0x3FFFF);
			uint uoffsetStart = (uint)(napsCblockPlanEntry.LogicalOffset & 0x3FFFF);
			uint clenEvenMinus = (uint)((!napsCblockPlanEntry.Terminator) ? Math.Min(napsCblockPlanEntry.EvenChunkCompressedLength - 1, 131071L) : 0);
			int count = list.Count;
			list.Add(new NapsCblockInfoEntry
			{
				Raw = new byte[9],
				IsRunBase = false,
				CoffsetStartMod256K = coffsetStartMod256K,
				ReservedBit19 = napsCblockPlanEntry.Terminator,
				UoffsetStart = uoffsetStart,
				ClenEvenMinus1 = clenEvenMinus,
				Even = napsCblockPlanEntry.Even,
				Odd = napsCblockPlanEntry.Odd,
				KdePredictor = napsCblockPlanEntry.KdePredictor,
				ShuffleIdx = napsCblockPlanEntry.ShuffleIndex
			});
			list2.Add((count, napsCblockPlanEntry.LogicalOffset));
			num += napsCblockPlanEntry.StreamLength;
		}
		for (int j = 0; j < list.Count; j += 16)
		{
			if (!list[j].IsRunBase)
			{
				throw new InvalidDataException($"NAPS CblockInfo window 0x{j:X} does not begin with a RUN base.");
			}
		}
		return (Entries: list, StdLogical: list2);
	}

	private static bool NeedsCursorPreservingRun(IReadOnlyList<NapsCblockPlanEntry> blocks, int blockIndex, int entryCount)
	{
		int num = entryCount;
		for (int i = blockIndex; i < blocks.Count; i++)
		{
			if ((num & 0xF) == 0)
			{
				return false;
			}
			if (blocks[i].StartRun)
			{
				num++;
			}
			if ((num & 0xF) == 0)
			{
				return true;
			}
			num++;
		}
		return false;
	}

	private static List<NapsFileOffsetEntry> BuildFileOffsets(NapsGenerationRequest request)
	{
		int count = request.FileLogicalOffsets.Count;
		List<NapsFileOffsetEntry> list = new List<NapsFileOffsetEntry>(count);
		for (int i = 0; i < count; i++)
		{
			byte type = (byte)((request.FileOffsetTypes != null) ? request.FileOffsetTypes[i] : ((i == count - 1) ? request.FinalFileOffsetType : 0));
			list.Add(new NapsFileOffsetEntry(type, (ulong)request.FileLogicalOffsets[i]));
		}
		return list;
	}

	private static List<NapsU2cEntry> BuildU2c(List<(int Index, long Logical)> stdLogical, int numUBlocks, int numCblockInfo, int numGroups)
	{
		(int, long)[] array = stdLogical.OrderBy(((int Index, long Logical) t) => t.Logical).ToArray();
		int[] first = new int[numUBlocks];
		int num = 0;
		for (int num2 = 0; num2 < numUBlocks; num2++)
		{
			long num3;
			for (num3 = (long)num2 * 262144L; num < array.Length && !IsTerminal(array[num].Item1) && array[num].Item2 < num3; num++)
			{
			}
			first[num2] = ((num < array.Length && !IsTerminal(array[num].Item1) && array[num].Item2 >= num3) ? array[num].Item1 : (numCblockInfo - 1));
		}
		List<NapsU2cEntry> list = new List<NapsU2cEntry>(numGroups);
		for (int num4 = 0; num4 < numGroups; num4++)
		{
			int num5 = first[8 * num4];
			byte[] array2 = new byte[7];
			for (int num6 = 0; num6 < array2.Length; num6++)
			{
				array2[num6] = ToU2cByte(Delta(8 * num4 + 1 + num6, num5), "delta");
			}
			list.Add(new NapsU2cEntry((uint)num5, array2));
		}
		return list;
		int Delta(int ublock, int baseIndex)
		{
			if (ublock < numUBlocks)
			{
				return first[ublock] - baseIndex;
			}
			return numCblockInfo - 1 - baseIndex;
		}
		bool IsTerminal(int index)
		{
			return index == numCblockInfo - 1;
		}
		static byte ToU2cByte(int value, string field)
		{
			if (value < 0 || value > 255)
			{
				throw new NotSupportedException($"NAPS u2c {field} value {value} exceeds the single-byte field; this layout size is not supported.");
			}
			return (byte)value;
		}
	}
}
