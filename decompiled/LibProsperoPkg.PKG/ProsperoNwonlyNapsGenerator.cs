using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibProsperoPkg.PFS;
using LibProsperoPkg.PFS.Compression;

namespace LibProsperoPkg.PKG;

public static class ProsperoNwonlyNapsGenerator
{
	private const int Block64K = 65536;

	private const long Ublock256K = 262144L;

	public static byte[] Generate(ProsperoPs5InnerImageResult result, IReadOnlyCollection<long>? runOverride = null, byte[]? outerBlockCmacKey = null)
	{
		ArgumentNullException.ThrowIfNull(result, "result");
		if (outerBlockCmacKey != null && outerBlockCmacKey.Length != 16)
		{
			throw new ArgumentException("NAPS outer-block CMAC key must be exactly 16 bytes.", "outerBlockCmacKey");
		}
		IReadOnlyList<ProsperoPs5InnerPlacement> placements = result.Placements;
		long num = result.Ndblock * 65536;
		long metaBaseLogical = result.MetaBaseLogical;
		long dataEndLogical = result.DataEndLogical;
		List<NapsFilePlacement> files = placements.Select((ProsperoPs5InnerPlacement p) => new NapsFilePlacement
		{
			OnDiskOffset = p.OnDiskOffset,
			LogicalOffset = p.LogicalOffset,
			OnDiskSize = p.OnDiskSize,
			UncompressedSize = p.UncompressedSize,
			StoreRaw = p.StoreRaw,
			CompressedKde = 2,
			CompressionBlocks = (p.CompressionBlocks ?? Array.Empty<ProsperoInnerDataBlockChunk>())
		}).ToList();
		HashSet<long> hashSet;
		if (runOverride != null)
		{
			hashSet = new HashSet<long>(runOverride);
		}
		else
		{
			hashSet = new HashSet<long>();
			long num2 = -1L;
			for (int num3 = 0; num3 < placements.Count; num3++)
			{
				if (num3 == 0 || placements[num3].OnDiskOffset != num2)
				{
					hashSet.Add(placements[num3].OnDiskOffset);
				}
				num2 = placements[num3].OnDiskOffset + placements[num3].OnDiskSize;
			}
		}
		List<NapsCblockPlanEntry> list = new List<NapsCblockPlanEntry>();
		long num4 = metaBaseLogical - dataEndLogical;
		int num5 = (int)((num4 > 0) ? ((num4 + 262144 - 1) / 262144) : 0);
		for (int num6 = 0; num6 < num5; num6++)
		{
			bool flag = Math.Min(262144L, num4 - (long)num6 * 262144L) > 131072;
			list.Add(new NapsCblockPlanEntry
			{
				StartRun = (num6 == 0),
				OnDiskOffset = result.BlockInfoOnDiskOffset,
				LogicalOffset = dataEndLogical + (long)num6 * 262144L,
				EvenChunkCompressedLength = 8L,
				StreamLength = (flag ? 16 : 8),
				Even = 1,
				Odd = (flag ? ((byte)1) : ((byte)0)),
				KdePredictor = 0,
				ShuffleIndex = 0
			});
		}
		IReadOnlyList<ProsperoInnerMetaBlockChunk> readOnlyList = result.MetadataBlocks;
		if (readOnlyList.Count == 0)
		{
			readOnlyList = ProsperoCompressedPfsFile.Parse(ProsperoCompressedPfsImage.Pack(result.MetadataPlaintext)).Blocks.Select((ProsperoPfsBlock b) => new ProsperoInnerMetaBlockChunk(b.CompressedSize, b.UncompressedSize, b.IsMultiChunk, b.FirstChunkCompressedSize, b.Flags)).ToList();
		}
		long num7 = result.MetadataOnDiskOffset;
		int count = readOnlyList.Count;
		for (int num8 = 0; num8 < count; num8++)
		{
			ProsperoInnerMetaBlockChunk prosperoInnerMetaBlockChunk = readOnlyList[num8];
			bool flag2 = prosperoInnerMetaBlockChunk.CompressedSize == prosperoInnerMetaBlockChunk.UncompressedSize;
			int num9;
			if (flag2)
			{
				num9 = Math.Min(prosperoInnerMetaBlockChunk.CompressedSize, 131072);
			}
			else
			{
				num9 = (prosperoInnerMetaBlockChunk.IsMultiChunk ? prosperoInnerMetaBlockChunk.FirstChunkCompressedSize : prosperoInnerMetaBlockChunk.CompressedSize);
			}
			list.Add(new NapsCblockPlanEntry
			{
				StartRun = (num8 == 0),
				OnDiskOffset = num7,
				LogicalOffset = metaBaseLogical + (long)num8 * 262144L,
				EvenChunkCompressedLength = num9,
				StreamLength = prosperoInnerMetaBlockChunk.CompressedSize,
				Even = (byte)(flag2 ? 1 : ToNapsHalfMode(prosperoInnerMetaBlockChunk.BoundaryFlags, firstHalf: true)),
				Odd = (flag2 ? ((prosperoInnerMetaBlockChunk.CompressedSize > 131072) ? ((byte)1) : ((byte)0)) : ((byte)(prosperoInnerMetaBlockChunk.IsMultiChunk ? ToNapsHalfMode(prosperoInnerMetaBlockChunk.BoundaryFlags, firstHalf: false) : 0))),
				KdePredictor = 0,
				ShuffleIndex = 0
			});
			num7 += prosperoInnerMetaBlockChunk.CompressedSize;
		}
		list.Add(new NapsCblockPlanEntry
		{
			StartRun = true,
			OnDiskOffset = num7,
			LogicalOffset = num,
			Terminator = true
		});
		List<long> list2 = new List<long>(result.AfidLogicalOffsets);
		list2.AddRange(result.EmptyFileLogicalOffsets);
		if (list2.Count == 0 || list2[list2.Count - 1] != dataEndLogical)
		{
			list2.Add(dataEndLogical);
		}
		list2.Add(metaBaseLogical);
		list2.Add(num);
		byte[] array = Enumerable.Repeat((byte)0, list2.Count).ToArray();
		int numUBlocks;
		int num10;
		IReadOnlyList<byte[]> outerBlockDigests;
		checked
		{
			foreach (ProsperoPs5SparseAfidHole sparseAfidHole in result.SparseAfidHoles)
			{
				array[(int)sparseAfidHole.Afid] = 64;
			}
			array[^1] = 64;
			numUBlocks = (int)unchecked(checked(num + 262144 - 1) / 262144) + 1;
			num10 = (int)unchecked(checked(result.ImageLength + 65536 - 1) / 65536);
			outerBlockDigests = null;
		}
		if (outerBlockCmacKey != null)
		{
			List<byte[]> list3 = new List<byte[]>(num10);
			using Stream stream = result.OpenImage();
			byte[] array2 = new byte[65536];
			for (int num11 = 0; num11 < num10; num11++)
			{
				Array.Clear(array2);
				int num12;
				checked
				{
					num12 = (int)Math.Min(65536L, result.ImageLength - unchecked((long)num11) * 65536L);
				}
				stream.Position = (long)num11 * 65536L;
				if (num12 > 0)
				{
					stream.ReadExactly(array2.AsSpan(0, num12));
				}
				list3.Add(ProsperoNapsImage.ComputeOuterBlockDigest(array2, outerBlockCmacKey));
			}
			outerBlockDigests = list3;
		}
		List<NapsCblockPlanEntry> source = ProsperoNapsLayoutBuilder.DeriveDataRegionBlocks(files, hashSet);
		source = source.OrderBy((NapsCblockPlanEntry block) => block.LogicalOffset).ToList();
		source.AddRange(list);
		return ProsperoNapsLayout.BuildLayout(ProsperoNapsLayoutBuilder.BuildDocument(new NapsGenerationRequest
		{
			NumUBlocks = numUBlocks,
			NumOuterBlocks = num10,
			FileLogicalOffsets = list2,
			FileOffsetTypes = array,
			Blocks = source,
			OuterBlockDigests = outerBlockDigests
		}));
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
}
