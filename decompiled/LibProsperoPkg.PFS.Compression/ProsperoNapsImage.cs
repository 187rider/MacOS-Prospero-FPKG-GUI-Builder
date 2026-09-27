using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using LibProsperoPkg.PFS.Compression.Oodle;
using LibProsperoPkg.PKG;

namespace LibProsperoPkg.PFS.Compression;

/// <summary>Resolves and decodes <c>pfs_image.dat</c> using <c>naps_pkg_layout.dat</c>.</summary>
public static class ProsperoNapsImage
{
	private sealed class NapsVerifyingWriteStream : Stream
	{
		private readonly Stream expected;

		private readonly long expectedLength;

		private byte[] scratch = new byte[262144];

		private long position;

		private long declaredLength;

		private long maxWritten;

		public override bool CanRead => false;

		public override bool CanSeek => true;

		public override bool CanWrite => true;

		public override long Length => declaredLength;

		public override long Position
		{
			get
			{
				return position;
			}
			set
			{
				if (value < 0 || value > expectedLength)
				{
					throw new ArgumentOutOfRangeException("value");
				}
				position = value;
			}
		}

		public NapsVerifyingWriteStream(Stream expected, long expectedLength)
		{
			this.expected = expected;
			this.expectedLength = expectedLength;
			declaredLength = expectedLength;
		}

		public override void Write(byte[] buffer, int offset, int count)
		{
			Write(buffer.AsSpan(offset, count));
		}

		public override void Write(ReadOnlySpan<byte> buffer)
		{
			if (position > expectedLength || buffer.Length > expectedLength - position)
			{
				throw new InvalidDataException("NAPS round-trip produced more logical bytes than expected.");
			}
			if (scratch.Length < buffer.Length)
			{
				scratch = new byte[buffer.Length];
			}
			expected.Position = position;
			expected.ReadExactly(scratch.AsSpan(0, buffer.Length));
			if (!scratch.AsSpan(0, buffer.Length).SequenceEqual(buffer))
			{
				throw new InvalidDataException($"Generated NAPS image differs at logical offset 0x{position:X}.");
			}
			position += buffer.Length;
			maxWritten = Math.Max(maxWritten, position);
		}

		public void EnsureComplete()
		{
			if (declaredLength != expectedLength || maxWritten != expectedLength)
			{
				throw new InvalidDataException($"NAPS round-trip produced 0x{maxWritten:X} of 0x{expectedLength:X} logical bytes.");
			}
		}

		public override void SetLength(long value)
		{
			if (value != expectedLength)
			{
				throw new InvalidDataException($"NAPS round-trip declared length 0x{value:X}, expected 0x{expectedLength:X}.");
			}
			declaredLength = value;
			if (position > value)
			{
				position = value;
			}
		}

		public override long Seek(long offset, SeekOrigin origin)
		{
			Position = checked(origin switch
			{
				SeekOrigin.Begin => offset, 
				SeekOrigin.Current => position + offset, 
				SeekOrigin.End => expectedLength + offset, 
				_ => throw new ArgumentOutOfRangeException("origin"), 
			});
			return position;
		}

		public override void Flush()
		{
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			throw new NotSupportedException();
		}
	}

	public const int UBlockSize = 262144;

	public const int OuterBlockSize = 65536;

	/// <summary>
	/// Packs a logical PPR-PFS stream into the NAPS physical representation and generates all
	/// structural type-13 tables. This baseline producer deliberately emits no deduplication,
	/// predictor, or shuffle records; every physical run is monotonic and uses key-table index zero.
	/// </summary>
	public static ProsperoNapsBuildResult Pack(ReadOnlySpan<byte> logicalImage, ProsperoNapsBuildOptions? options = null)
	{
		if (options == null)
		{
			options = new ProsperoNapsBuildOptions();
		}
		if (logicalImage.Length == 0)
		{
			throw new ArgumentException("NAPS input cannot be empty.", "logicalImage");
		}
		int compressionLevel = options.CompressionLevel;
		if ((compressionLevel < -4 || compressionLevel > 9) ? true : false)
		{
			throw new ArgumentOutOfRangeException("options", "Kraken level must be in -4..9.");
		}
		byte[] outerBlockCmacKey = options.OuterBlockCmacKey;
		if (outerBlockCmacKey != null && outerBlockCmacKey.Length != 16)
		{
			throw new ArgumentException("NAPS outer-block CMAC key must be exactly 16 bytes.", "options");
		}
		long[] array = ValidateBoundaries(logicalImage.Length, options.FileBoundaries);
		List<NapsCblockInfoEntry> list = new List<NapsCblockInfoEntry>();
		List<uint> list2 = new List<uint>();
		using MemoryStream memoryStream = new MemoryStream();
		int num = 0;
		int num2 = 0;
		list.Add(new NapsCblockInfoEntry
		{
			IsRunBase = true,
			CoffsetEndMod256K = 0u,
			TweakIdxStart = 0u,
			KeyTableIdx = 0,
			CoffsetStart256K = 0u
		});
		int i = 0;
		int j = 1;
		int num3;
		for (; i < logicalImage.Length; i += num3)
		{
			for (; j < array.Length && array[j] <= i; j++)
			{
			}
			int val = 262144 - (i & 0x3FFFF);
			int val2 = ((j < array.Length) ? checked((int)(array[j] - i)) : (logicalImage.Length - i));
			num3 = Math.Min(val, val2);
			ReadOnlySpan<byte> data = logicalImage.Slice(i, num3);
			EncodedBlock? encodedBlock = (options.Compress ? OodleKrakenEncoder.EncodeBlock(data, useHuffmanArrays: true, options.CompressionLevel) : ((EncodedBlock?)null));
			bool flag = encodedBlock.HasValue && encodedBlock.GetValueOrDefault().Payload.Length < data.Length;
			byte[] array2 = (flag ? encodedBlock.Value.Payload : data.ToArray());
			int num4 = (flag ? encodedBlock.Value.FirstChunkCompSize : Math.Min(131072, num3));
			if ((num4 < 1 || num4 > 131072) ? true : false)
			{
				throw new InvalidDataException("NAPS first-chunk length exceeds the 17-bit minus-one field.");
			}
			long position = memoryStream.Position;
			checked
			{
				if ((i & 0x3FFFF) == 0)
				{
					list2.Add((uint)list.Count);
				}
				memoryStream.Write(array2);
				list.Add(new NapsCblockInfoEntry
				{
					CoffsetStartMod256K = (uint)(position & 0x3FFFF),
					UoffsetStart = (uint)(i & 0x3FFFF),
					ClenEvenMinus1 = (uint)(num4 - 1),
					Even = unchecked((byte)((!flag) ? 1 : encodedBlock.Value.NapsEvenMode)),
					Odd = unchecked((byte)(flag ? encodedBlock.Value.NapsOddMode : ((!flag && num3 > 131072) ? 1 : 0))),
					KdePredictor = 0,
					ShuffleIdx = 0
				});
			}
			if (flag)
			{
				num++;
			}
			else
			{
				num2++;
			}
		}
		long position2 = memoryStream.Position;
		int num5;
		byte[] array3;
		List<byte[]> list3;
		checked
		{
			list.Add(new NapsCblockInfoEntry
			{
				ReservedBit19 = true,
				CoffsetStartMod256K = (uint)(position2 & 0x3FFFF),
				UoffsetStart = (uint)(logicalImage.Length & 0x3FFFF),
				ClenEvenMinus1 = 0u
			});
			num5 = (int)unchecked(checked(position2 + 65536 - 1) / 65536);
			memoryStream.SetLength(unchecked((long)num5) * 65536L);
			array3 = memoryStream.ToArray();
			list3 = new List<byte[]>(num5);
		}
		for (int k = 0; k < num5; k++)
		{
			ReadOnlySpan<byte> outerBlock = array3.AsSpan(k * 65536, 65536);
			list3.Add((options.OuterBlockCmacKey == null) ? new byte[8] : ComputeOuterBlockDigest(outerBlock, options.OuterBlockCmacKey));
		}
		List<NapsFileOffsetEntry> list4 = new List<NapsFileOffsetEntry>(array.Length);
		for (int l = 0; l < array.Length; l++)
		{
			list4.Add(new NapsFileOffsetEntry((byte)((l == array.Length - 1) ? 64 : 0), checked((ulong)array[l])));
		}
		int num6 = checked(logicalImage.Length + 262144 - 1) / 262144;
		List<NapsU2cEntry> cblockInfoOffsetByUblock = BuildU2c(list2, num6, checked((uint)list.Count - 1));
		NapsLayoutCounts counts = new NapsLayoutCounts(list4.Count, 2, 1, 0, num6 - 1, num5, list.Count);
		NapsLayoutDocument document = new NapsLayoutDocument
		{
			Counts = counts,
			Map = ProsperoNapsLayout.SectionMap(counts),
			OuterBlockDigests = list3,
			ShufflePatterns = Array.Empty<byte[]>(),
			FileOffsets = list4,
			CblockInfoOffsetByUblock = cblockInfoOffsetByUblock,
			CblockInfos = list,
			TrailingZeroBytes = 0
		};
		byte[] array4 = ProsperoNapsLayout.BuildLayout(document);
		document = ProsperoNapsLayout.Parse(array4);
		if (options.VerifyRoundTrip)
		{
			using MemoryStream pfsImage = new MemoryStream(array3, writable: false);
			using MemoryStream memoryStream2 = new MemoryStream(logicalImage.Length);
			Decompress(pfsImage, document, memoryStream2);
			if (!memoryStream2.GetBuffer().AsSpan(0, checked((int)memoryStream2.Length)).SequenceEqual(logicalImage))
			{
				throw new InvalidDataException("Generated NAPS image failed its decode round-trip.");
			}
		}
		return new ProsperoNapsBuildResult
		{
			PackedImage = array3,
			LayoutBytes = array4,
			Layout = document,
			CompressedSpanCount = num,
			StoredSpanCount = num2,
			LogicalSize = logicalImage.Length
		};
	}

	/// <summary>
	/// Packs a seekable logical image into a seekable file/stream using at most one 256-KiB source
	/// block plus encoder scratch. The packed stream is truncated and rewritten from offset zero.
	/// </summary>
	public static ProsperoNapsFileBuildResult Pack(Stream logicalInput, long logicalLength, Stream packedOutput, ProsperoNapsBuildOptions? options = null)
	{
		ArgumentNullException.ThrowIfNull(logicalInput, "logicalInput");
		ArgumentNullException.ThrowIfNull(packedOutput, "packedOutput");
		if (options == null)
		{
			options = new ProsperoNapsBuildOptions();
		}
		if (!logicalInput.CanRead || !logicalInput.CanSeek)
		{
			throw new ArgumentException("Logical NAPS input must be readable and seekable.", "logicalInput");
		}
		if (!packedOutput.CanRead || !packedOutput.CanWrite || !packedOutput.CanSeek)
		{
			throw new ArgumentException("Packed NAPS output must be readable, writable and seekable.", "packedOutput");
		}
		if (logicalLength <= 0 || logicalLength > logicalInput.Length)
		{
			throw new ArgumentOutOfRangeException("logicalLength");
		}
		int compressionLevel = options.CompressionLevel;
		if ((compressionLevel < -4 || compressionLevel > 9) ? true : false)
		{
			throw new ArgumentOutOfRangeException("options", "Kraken level must be in -4..9.");
		}
		byte[] outerBlockCmacKey = options.OuterBlockCmacKey;
		if (outerBlockCmacKey != null && outerBlockCmacKey.Length != 16)
		{
			throw new ArgumentException("NAPS outer-block CMAC key must be exactly 16 bytes.", "options");
		}
		long[] array = ValidateBoundaries(logicalLength, options.FileBoundaries);
		logicalInput.Position = 0L;
		packedOutput.Position = 0L;
		packedOutput.SetLength(0L);
		List<NapsCblockInfoEntry> list = new List<NapsCblockInfoEntry>();
		List<uint> list2 = new List<uint>();
		int num = 0;
		int num2 = 0;
		list.Add(new NapsCblockInfoEntry
		{
			IsRunBase = true,
			CoffsetEndMod256K = 0u,
			TweakIdxStart = 0u,
			KeyTableIdx = 0,
			CoffsetStart256K = 0u
		});
		byte[] array2 = new byte[262144];
		long num3 = 0L;
		int i = 1;
		int num5;
		for (; num3 < logicalLength; num3 += num5)
		{
			for (; i < array.Length && array[i] <= num3; i++)
			{
			}
			int num4 = 262144 - (int)(num3 & 0x3FFFF);
			long val = ((i < array.Length) ? (array[i] - num3) : (logicalLength - num3));
			bool flag;
			checked
			{
				num5 = (int)Math.Min(num4, val);
				logicalInput.ReadExactly(array2.AsSpan(0, num5));
				ReadOnlySpan<byte> data = array2.AsSpan(0, num5);
				EncodedBlock? encodedBlock = (options.Compress ? OodleKrakenEncoder.EncodeBlock(data, useHuffmanArrays: true, options.CompressionLevel) : ((EncodedBlock?)null));
				flag = encodedBlock.HasValue && encodedBlock.GetValueOrDefault().Payload.Length < data.Length;
				ReadOnlyMemory<byte> readOnlyMemory = (flag ? ((Memory<byte>)encodedBlock.Value.Payload) : array2.AsMemory(0, num5));
				int num6 = (flag ? encodedBlock.Value.FirstChunkCompSize : Math.Min(131072, num5));
				if ((num6 < 1 || num6 > 131072) ? true : false)
				{
					throw new InvalidDataException("NAPS first-chunk length exceeds the 17-bit minus-one field.");
				}
				long position = packedOutput.Position;
				if ((num3 & 0x3FFFF) == 0L)
				{
					list2.Add((uint)list.Count);
				}
				packedOutput.Write(readOnlyMemory.Span);
				list.Add(new NapsCblockInfoEntry
				{
					CoffsetStartMod256K = (uint)(position & 0x3FFFF),
					UoffsetStart = (uint)(num3 & 0x3FFFF),
					ClenEvenMinus1 = (uint)(num6 - 1),
					Even = unchecked((byte)((!flag) ? 1 : encodedBlock.Value.NapsEvenMode)),
					Odd = unchecked((byte)(flag ? encodedBlock.Value.NapsOddMode : ((!flag && num5 > 131072) ? 1 : 0))),
					KdePredictor = 0,
					ShuffleIdx = 0
				});
			}
			if (flag)
			{
				num++;
			}
			else
			{
				num2++;
			}
		}
		long position2 = packedOutput.Position;
		int num7;
		long num8;
		List<byte[]> list3;
		checked
		{
			list.Add(new NapsCblockInfoEntry
			{
				ReservedBit19 = true,
				CoffsetStartMod256K = (uint)(position2 & 0x3FFFF),
				UoffsetStart = (uint)(logicalLength & 0x3FFFF),
				ClenEvenMinus1 = 0u
			});
			num7 = (int)unchecked(checked(position2 + 65536 - 1) / 65536);
			num8 = unchecked((long)num7) * 65536L;
			packedOutput.SetLength(num8);
			list3 = new List<byte[]>(num7);
		}
		if (options.OuterBlockCmacKey == null)
		{
			for (int j = 0; j < num7; j++)
			{
				list3.Add(new byte[8]);
			}
		}
		else
		{
			byte[] array3 = new byte[65536];
			for (int k = 0; k < num7; k++)
			{
				packedOutput.Position = (long)k * 65536L;
				packedOutput.ReadExactly(array3);
				list3.Add(ComputeOuterBlockDigest(array3, options.OuterBlockCmacKey));
			}
		}
		List<NapsFileOffsetEntry> list4 = new List<NapsFileOffsetEntry>(array.Length);
		for (int l = 0; l < array.Length; l++)
		{
			list4.Add(new NapsFileOffsetEntry((byte)((l == array.Length - 1) ? 64 : 0), checked((ulong)array[l])));
		}
		int num9;
		List<NapsU2cEntry> cblockInfoOffsetByUblock;
		checked
		{
			num9 = (int)unchecked(checked(logicalLength + 262144 - 1) / 262144);
			cblockInfoOffsetByUblock = BuildU2c(list2, num9, (uint)list.Count - 1);
		}
		NapsLayoutCounts counts = new NapsLayoutCounts(list4.Count, 2, 1, 0, num9 - 1, num7, list.Count);
		NapsLayoutDocument document = new NapsLayoutDocument
		{
			Counts = counts,
			Map = ProsperoNapsLayout.SectionMap(counts),
			OuterBlockDigests = list3,
			ShufflePatterns = Array.Empty<byte[]>(),
			FileOffsets = list4,
			CblockInfoOffsetByUblock = cblockInfoOffsetByUblock,
			CblockInfos = list,
			TrailingZeroBytes = 0
		};
		byte[] array4 = ProsperoNapsLayout.BuildLayout(document);
		document = ProsperoNapsLayout.Parse(array4);
		if (options.VerifyRoundTrip)
		{
			packedOutput.Position = 0L;
			logicalInput.Position = 0L;
			using NapsVerifyingWriteStream napsVerifyingWriteStream = new NapsVerifyingWriteStream(logicalInput, logicalLength);
			Decompress(packedOutput, document, napsVerifyingWriteStream);
			napsVerifyingWriteStream.EnsureComplete();
		}
		logicalInput.Position = 0L;
		packedOutput.Position = 0L;
		return new ProsperoNapsFileBuildResult
		{
			LayoutBytes = array4,
			Layout = document,
			CompressedSpanCount = num,
			StoredSpanCount = num2,
			LogicalSize = logicalLength,
			PackedSize = num8
		};
	}

	/// <summary>
	/// Computes the publisher type-13 eight-byte outer-block tag:
	/// <c>CMAC-AES128(key, reverse(SHA3-256(block)))[8..16]</c>.
	/// </summary>
	public static byte[] ComputeOuterBlockDigest(ReadOnlySpan<byte> outerBlock, ReadOnlySpan<byte> cmacKey)
	{
		if (outerBlock.Length != 65536)
		{
			throw new ArgumentException($"NAPS outer block must be {65536} bytes.", "outerBlock");
		}
		if (cmacKey.Length != 16)
		{
			throw new ArgumentException("AES-CMAC key must be 16 bytes.", "cmacKey");
		}
		byte[] array = ProsperoImageDigests.Sha3_256(outerBlock);
		Array.Reverse(array);
		return AesCmac(cmacKey, array).AsSpan(8, 8).ToArray();
	}

	/// <summary>Builds and strictly validates the compressed-span graph.</summary>
	public static ProsperoNapsPlan BuildPlan(NapsLayoutDocument layout)
	{
		ArgumentNullException.ThrowIfNull(layout, "layout");
		if (layout.FileOffsets.Count < 1)
		{
			throw new InvalidDataException("NAPS layout has no terminal file boundary.");
		}
		List<ProsperoNapsLogicalFile> list = new List<ProsperoNapsLogicalFile>(layout.FileOffsets.Count - 1);
		for (int i = 0; i + 1 < layout.FileOffsets.Count; i++)
		{
			NapsFileOffsetEntry napsFileOffsetEntry = layout.FileOffsets[i];
			NapsFileOffsetEntry napsFileOffsetEntry2 = layout.FileOffsets[i + 1];
			if (napsFileOffsetEntry2.UncompressedOffsetStart < napsFileOffsetEntry.UncompressedOffsetStart)
			{
				throw new InvalidDataException($"NAPS file boundary {i + 1} moves backwards.");
			}
			list.Add(checked(new ProsperoNapsLogicalFile(i, napsFileOffsetEntry.Type, (long)napsFileOffsetEntry.UncompressedOffsetStart, (long)(napsFileOffsetEntry2.UncompressedOffsetStart - napsFileOffsetEntry.UncompressedOffsetStart))));
		}
		List<ProsperoNapsSpan> list2 = new List<ProsperoNapsSpan>();
		ProsperoNapsSpan? prosperoNapsSpan = null;
		foreach (ProsperoNapsLogicalFile item in list)
		{
			if ((item.Type & 0x40) != 0)
			{
				if (!prosperoNapsSpan.HasValue)
				{
					throw new InvalidDataException($"NAPS continuation file {item.Index} has no preceding span.");
				}
				continue;
			}
			long num = item.UncompressedOffset;
			long num2 = item.Length;
			int num3 = ResolveCblockInfoIndex(layout, num);
			while (num2 != 0L)
			{
				if ((uint)num3 >= (uint)layout.CblockInfos.Count)
				{
					throw new InvalidDataException($"NAPS file {item.Index} runs past CblockInfo.");
				}
				NapsCblockInfoEntry napsCblockInfoEntry = layout.CblockInfos[num3];
				if (napsCblockInfoEntry.IsRunBase)
				{
					num3++;
					continue;
				}
				if (napsCblockInfoEntry.IsTerminal)
				{
					throw new InvalidDataException($"NAPS file {item.Index} reaches the terminal boundary early.");
				}
				int num4 = checked(num3 + 1);
				if ((uint)num4 >= (uint)layout.CblockInfos.Count)
				{
					throw new InvalidDataException("NAPS normal CblockInfo has no following boundary.");
				}
				NapsCblockInfoEntry napsCblockInfoEntry2 = layout.CblockInfos[num4];
				int num5 = (napsCblockInfoEntry2.IsRunBase ? checked(num4 + 1) : num4);
				if ((uint)num5 >= (uint)layout.CblockInfos.Count)
				{
					throw new InvalidDataException("NAPS run-base has no following logical boundary.");
				}
				NapsCblockInfoEntry napsCblockInfoEntry3 = layout.CblockInfos[num5];
				int num6 = Delta18(napsCblockInfoEntry2.IsRunBase ? napsCblockInfoEntry2.CoffsetEndMod256K : napsCblockInfoEntry2.CoffsetStartMod256K, napsCblockInfoEntry.CoffsetStartMod256K);
				int num7 = Delta18(napsCblockInfoEntry3.UoffsetStart, napsCblockInfoEntry.UoffsetStart);
				if (num6 <= 0 || num7 <= 0 || num7 > 262144)
				{
					throw new InvalidDataException($"NAPS span at CblockInfo {num3} has invalid lengths.");
				}
				if (num7 > num2)
				{
					throw new InvalidDataException($"NAPS span at CblockInfo {num3} crosses file {item.Index}.");
				}
				long num8;
				uint num9;
				byte keyTableIndex;
				if (num3 > 0 && layout.CblockInfos[num3 - 1].IsRunBase)
				{
					NapsCblockInfoEntry napsCblockInfoEntry4 = layout.CblockInfos[num3 - 1];
					num8 = (long)(((ulong)napsCblockInfoEntry4.CoffsetStart256K << 18) | napsCblockInfoEntry.CoffsetStartMod256K);
					num9 = napsCblockInfoEntry4.TweakIdxStart;
					keyTableIndex = napsCblockInfoEntry4.KeyTableIdx;
				}
				else
				{
					if (!prosperoNapsSpan.HasValue)
					{
						throw new InvalidDataException("The first NAPS span is not preceded by a run-base.");
					}
					ProsperoNapsSpan valueOrDefault = prosperoNapsSpan.GetValueOrDefault();
					num8 = checked(valueOrDefault.CompressedOffset + valueOrDefault.CompressedLength);
					long num10 = (num8 >> 16) - (valueOrDefault.CompressedOffset >> 16);
					num9 = checked((uint)(valueOrDefault.TweakIndex + num10));
					keyTableIndex = valueOrDefault.KeyTableIndex;
				}
				checked
				{
					long storedOffset = unchecked((long)num9) * 65536L + (num8 & 0xFFFF);
					ProsperoNapsSpan prosperoNapsSpan2 = new ProsperoNapsSpan(list2.Count, num3, num8, num6, (int)napsCblockInfoEntry.ClenEvenMinus1 + 1, storedOffset, num, num7, num9, keyTableIndex, napsCblockInfoEntry.Even, napsCblockInfoEntry.Odd, napsCblockInfoEntry.KdePredictor, napsCblockInfoEntry.ShuffleIdx);
					list2.Add(prosperoNapsSpan2);
					prosperoNapsSpan = prosperoNapsSpan2;
				}
				num += num7;
				num2 -= num7;
				num3++;
			}
		}
		int num11 = 0;
		foreach (NapsCblockInfoEntry cblockInfo in layout.CblockInfos)
		{
			if (!cblockInfo.IsRunBase && !cblockInfo.IsTerminal)
			{
				num11++;
			}
		}
		if (list2.Count != num11)
		{
			throw new InvalidDataException($"NAPS span graph used {list2.Count} of {num11} normal entries.");
		}
		IReadOnlyList<NapsFileOffsetEntry> fileOffsets = layout.FileOffsets;
		checked
		{
			ProsperoNapsPlan prosperoNapsPlan = new ProsperoNapsPlan
			{
				Spans = list2,
				Files = list,
				UncompressedSize = (long)fileOffsets[unchecked(fileOffsets.Count - 1)].UncompressedOffsetStart
			};
			return prosperoNapsPlan;
		}
	}

	/// <summary>Decompresses the complete logical stream to a seekable destination.</summary>
	public static void Decompress(Stream pfsImage, NapsLayoutDocument layout, Stream destination)
	{
		ArgumentNullException.ThrowIfNull(pfsImage, "pfsImage");
		ArgumentNullException.ThrowIfNull(destination, "destination");
		if (!pfsImage.CanRead || !pfsImage.CanSeek)
		{
			throw new ArgumentException("NAPS pfs_image stream must be readable and seekable.", "pfsImage");
		}
		if (!destination.CanWrite || !destination.CanSeek)
		{
			throw new ArgumentException("NAPS output stream must be writable and seekable.", "destination");
		}
		ProsperoNapsPlan prosperoNapsPlan = BuildPlan(layout);
		destination.SetLength(prosperoNapsPlan.UncompressedSize);
		foreach (ProsperoNapsSpan span in prosperoNapsPlan.Spans)
		{
			if (span.KdePredictor != 0)
			{
				throw new NotSupportedException($"NAPS span {span.Index} requires KDE predictor {span.KdePredictor}.");
			}
			ProsperoPfsShufflePattern prosperoPfsShufflePattern = ResolveShufflePattern(layout, span.ShuffleIndex);
			if (span.StoredOffset < 0 || span.StoredOffset > pfsImage.Length || span.CompressedLength > pfsImage.Length - span.StoredOffset)
			{
				throw new InvalidDataException($"NAPS span {span.Index} lies outside pfs_image.dat.");
			}
			byte[] array = new byte[span.CompressedLength];
			pfsImage.Position = span.StoredOffset;
			pfsImage.ReadExactly(array);
			byte[] array2 = new byte[span.UncompressedLength];
			if (!IsDeduplicatedZeroSpan(span))
			{
				if (span.CompressedLength == span.UncompressedLength)
				{
					array.CopyTo(array2, 0);
				}
				else
				{
					int firstChunk = ((span.UncompressedLength > 131072) ? span.FirstChunkCompressedLength : 0);
					DecodeKrakenSpan(array, array2, firstChunk, span);
				}
			}
			if (prosperoPfsShufflePattern != ProsperoPfsShufflePattern.None)
			{
				ProsperoPfsShuffle.Deshuffle(array2, prosperoPfsShufflePattern).CopyTo(array2, 0);
			}
			destination.Position = span.UncompressedOffset;
			destination.Write(array2);
		}
		destination.Position = 0L;
	}

	private static ProsperoPfsShufflePattern ResolveShufflePattern(NapsLayoutDocument layout, byte shuffleIndex)
	{
		int count = layout.ShufflePatterns.Count;
		if (shuffleIndex == count)
		{
			return ProsperoPfsShufflePattern.None;
		}
		if (shuffleIndex > count)
		{
			throw new InvalidDataException($"NAPS shuffle index {shuffleIndex} exceeds the {count}-entry pattern table and its identity sentinel.");
		}
		ReadOnlySpan<byte> bytes = layout.ShufflePatterns[shuffleIndex];
		if (bytes.Length != 8)
		{
			throw new InvalidDataException($"NAPS shuffle pattern {shuffleIndex} has invalid size {bytes.Length}.");
		}
		int num = bytes.Length;
		while (num > 0 && bytes[num - 1] == 0)
		{
			num--;
		}
		for (ProsperoPfsShufflePattern prosperoPfsShufflePattern = ProsperoPfsShufflePattern.Shuffle44; prosperoPfsShufflePattern <= ProsperoPfsShufflePattern.Shuffle2626; prosperoPfsShufflePattern++)
		{
			int[] item = ProsperoPfsShuffle.Describe(prosperoPfsShufflePattern).Fields;
			bool flag = item.Length == num;
			int num2 = 0;
			while (flag && num2 < item.Length)
			{
				flag = bytes[num2] == item[num2];
				num2++;
			}
			if (flag)
			{
				return prosperoPfsShufflePattern;
			}
		}
		throw new NotSupportedException($"NAPS shuffle pattern {shuffleIndex} has unknown descriptor {Convert.ToHexString(bytes)}.");
	}

	private static bool IsDeduplicatedZeroSpan(ProsperoNapsSpan span)
	{
		bool num = span.Odd == 1 && span.CompressedLength == 16 && span.UncompressedLength > 16;
		bool flag = span.Odd == 0 && span.CompressedLength == 8 && span.UncompressedLength > 8 && span.UncompressedLength <= 131072;
		if ((num | flag) && span.FirstChunkCompressedLength == 8 && span.Even == 1 && span.KdePredictor == 0)
		{
			return span.ShuffleIndex == 0;
		}
		return false;
	}

	private static int ResolveCblockInfoIndex(NapsLayoutDocument layout, long uncompressedOffset)
	{
		if (uncompressedOffset < 0)
		{
			throw new ArgumentOutOfRangeException("uncompressedOffset");
		}
		int num;
		uint num2;
		uint num3;
		checked
		{
			num = (int)(uncompressedOffset >> 18);
			num2 = ResolveU2c(layout, num);
			num3 = ((unchecked(num + 1) < layout.Counts.UBlockCount) ? ResolveU2c(layout, num + 1) : ((uint)layout.CblockInfos.Count));
		}
		long num4 = (long)num << 18;
		for (uint num5 = num2; num5 < num3 && num5 < layout.CblockInfos.Count; num5++)
		{
			NapsCblockInfoEntry napsCblockInfoEntry = layout.CblockInfos[(int)num5];
			if (!napsCblockInfoEntry.IsRunBase && !napsCblockInfoEntry.IsTerminal && uncompressedOffset == num4 + napsCblockInfoEntry.UoffsetStart)
			{
				return checked((int)num5);
			}
		}
		throw new InvalidDataException($"No NAPS CblockInfo starts at logical offset 0x{uncompressedOffset:x}.");
	}

	private static uint ResolveU2c(NapsLayoutDocument layout, int ublock)
	{
		if (ublock < 0 || ublock >= layout.Counts.UBlockCount)
		{
			throw new InvalidDataException($"NAPS ublock {ublock} is outside the u2c table.");
		}
		NapsU2cEntry napsU2cEntry = layout.CblockInfoOffsetByUblock[ublock >> 3];
		if ((ublock & 7) != 0)
		{
			return checked(napsU2cEntry.InfoOffset9BBase + napsU2cEntry.DeltaFromBase[(ublock & 7) - 1]);
		}
		return napsU2cEntry.InfoOffset9BBase;
	}

	private static long[] ValidateBoundaries(long logicalLength, IReadOnlyList<long>? requested)
	{
		if (requested == null)
		{
			return new long[2] { 0L, logicalLength };
		}
		if (requested.Count >= 2 && requested[0] == 0L)
		{
			if (requested[requested.Count - 1] == logicalLength)
			{
				long[] array = new long[requested.Count];
				long num = -1L;
				for (int i = 0; i < requested.Count; i++)
				{
					long num2 = requested[i];
					if (num2 <= num || num2 < 0 || num2 > logicalLength)
					{
						throw new ArgumentException($"NAPS boundary {i} is not strictly increasing.", "requested");
					}
					array[i] = num2;
					num = num2;
				}
				return array;
			}
		}
		throw new ArgumentException("NAPS boundaries must start at zero and end at the logical length.", "requested");
	}

	private static List<NapsU2cEntry> BuildU2c(IReadOnlyList<uint> indexes, int ublockCount, uint terminalIndex)
	{
		if (indexes.Count != ublockCount)
		{
			throw new InvalidDataException("NAPS writer did not record one CblockInfo start per ublock.");
		}
		int num = (ublockCount + 7) / 8;
		List<NapsU2cEntry> list = new List<NapsU2cEntry>(num);
		for (int i = 0; i < num; i++)
		{
			int num2 = i * 8;
			uint num3 = indexes[num2];
			byte[] array = new byte[7];
			for (int j = 1; j < 8; j++)
			{
				int num4 = num2 + j;
				uint num5 = checked(((num4 < ublockCount) ? indexes[num4] : terminalIndex) - num3);
				if (num5 > 255)
				{
					throw new InvalidDataException("NAPS u2c group delta exceeds eight bits.");
				}
				array[j - 1] = (byte)num5;
			}
			list.Add(new NapsU2cEntry(num3, array));
		}
		return list;
	}

	private static byte[] AesCmac(ReadOnlySpan<byte> key, ReadOnlySpan<byte> message)
	{
		using Aes aes = Aes.Create();
		aes.Key = key.ToArray();
		Span<byte> span = stackalloc byte[16];
		Span<byte> span2 = stackalloc byte[16];
		aes.EncryptEcb(span, span2, PaddingMode.None);
		Span<byte> span3 = stackalloc byte[16];
		Span<byte> span4 = stackalloc byte[16];
		DoubleCmacBlock(span2, span3);
		DoubleCmacBlock(span3, span4);
		int num = Math.Max(1, (message.Length + 15) / 16);
		bool flag = message.Length != 0 && (message.Length & 0xF) == 0;
		Span<byte> span5 = stackalloc byte[16];
		Span<byte> span6 = stackalloc byte[16];
		for (int i = 0; i < num; i++)
		{
			span6.Clear();
			int num2 = i * 16;
			int num3 = Math.Min(16, message.Length - num2);
			if (num3 > 0)
			{
				message.Slice(num2, num3).CopyTo(span6);
			}
			if (i == num - 1)
			{
				if (flag)
				{
					XorInPlace(span6, span3);
				}
				else
				{
					span6[num3] = 128;
					XorInPlace(span6, span4);
				}
			}
			XorInPlace(span6, span5);
			aes.EncryptEcb(span6, span5, PaddingMode.None);
		}
		return span5.ToArray();
	}

	private static void DoubleCmacBlock(ReadOnlySpan<byte> input, Span<byte> output)
	{
		int num = 0;
		for (int num2 = 15; num2 >= 0; num2--)
		{
			int num3 = input[num2];
			output[num2] = (byte)((num3 << 1) | num);
			num = num3 >> 7;
		}
		if (num != 0)
		{
			output[15] ^= 135;
		}
	}

	private static void XorInPlace(Span<byte> target, ReadOnlySpan<byte> value)
	{
		for (int i = 0; i < target.Length; i++)
		{
			target[i] ^= value[i];
		}
	}

	private static int Delta18(uint next, uint previous)
	{
		int num = checked((int)next - (int)previous);
		if (num <= 0)
		{
			num += 262144;
		}
		return num;
	}

	private static void DecodeKrakenSpan(byte[] payload, byte[] output, int firstChunk, ProsperoNapsSpan span)
	{
		int flags = (int)((uint)(((span.Even & 4) != 0) ? 2 : 0) | (((span.Even & 2) != 0) ? 1u : 0u) | (uint)(((span.Odd & 4) != 0) ? 32 : 0)) | (((span.Odd & 2) != 0) ? 16 : 0);
		KrakenDecodeStatus krakenDecodeStatus = KrakenDecoder.DecodeBlock(payload, flags, firstChunk, output);
		if (krakenDecodeStatus != KrakenDecodeStatus.Success)
		{
			throw new InvalidDataException($"NAPS span {span.Index} (CBI {span.CblockInfoIndex}, stored=0x{span.StoredOffset:x}, compressed=0x{span.CompressedLength:x}, first=0x{firstChunk:x}, uncompressed=0x{span.UncompressedLength:x}) Kraken decode failed ({krakenDecodeStatus}, modes {span.Even}/{span.Odd}, payload={Convert.ToHexString(payload.AsSpan(0, Math.Min(16, payload.Length)))}).");
		}
	}
}
