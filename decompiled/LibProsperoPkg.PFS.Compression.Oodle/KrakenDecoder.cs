using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;

namespace LibProsperoPkg.PFS.Compression.Oodle;

/// <summary>
/// A complete managed Kraken (newLZ) chunk decoder. Decodes one PFS block (one or two internal
/// newLZ chunks) into a caller-supplied destination buffer. This is the production decoder used by
/// <see cref="T:LibProsperoPkg.PFS.Compression.CompressedPfsFile" /> to read both this library's own output and reference-produced blocks.
/// </summary>
internal static class KrakenDecoder
{
	/// <summary>Holds the four decoded streams for one newLZ chunk.</summary>
	private sealed class LzTable
	{
		public byte[] LitStream = Array.Empty<byte>();

		public int LitStreamSize;

		public byte[] CmdStream = Array.Empty<byte>();

		public int CmdStreamSize;

		public int[] OffsStream = Array.Empty<int>();

		public int OffsStreamSize;

		public int[] LenStream = Array.Empty<int>();

		public int LenStreamSize;
	}

	/// <summary>Diagnostic descriptor for one entropy/raw array inside a chunk.</summary>
	internal sealed class KrakenArrayInfo
	{
		public string Name = "";

		public int SrcOffset;

		public int SrcLen;

		public int ChunkType;

		public int Transmission = -1;

		public byte[] Decoded = Array.Empty<byte>();
	}

	internal sealed class Type12Info
	{
		public int ChunkType;

		public int HeaderLen;

		public int SrcSize;

		public int DstSize;

		public int Transmission = -1;

		public int NumSyms;

		public int CodeLenEndOffset;

		public int SplitMid = -1;

		public int SplitLeft = -1;

		public int SplitRight = -1;

		public int PayloadOffset = -1;

		public byte[] CodeLengths = new byte[256];
	}

	private sealed class NewHuffLut
	{
		public readonly byte[] Bits2Len = new byte[2064];

		public readonly byte[] Bits2Sym = new byte[2064];
	}

	private struct HuffRange
	{
		public int Symbol;

		public int Num;
	}

	private struct HuffReader
	{
		public byte[] B;

		public byte[] Out;

		public int Src;

		public int SrcEnd;

		public int SrcMid;

		public int SrcMidOrg;

		public uint SrcBits;

		public uint SrcMidBits;

		public uint SrcEndBits;

		public int SrcBitpos;

		public int SrcMidBitpos;

		public int SrcEndBitpos;

		public int OutOff;

		public int OutEnd;

		public HuffReader(byte[] b, byte[] o)
		{
			this = default;
			B = b;
			Out = o;
			SrcBits = (SrcMidBits = (SrcEndBits = 0u));
			SrcBitpos = (SrcMidBitpos = (SrcEndBitpos = 0));
		}
	}

	private struct RefBitReader2
	{
		public byte[] B;

		public int P;

		public int PEnd;

		public int BitPos;
	}

	private struct RefBitReader
	{
		public byte[] B;

		public int P;

		public int Bound;

		public uint Bits;

		public int BitPos;

		private bool _bwd;

		public static RefBitReader Forward(byte[] b, int start, int end)
		{
			RefBitReader result = new RefBitReader
			{
				B = b,
				P = start,
				Bound = end,
				Bits = 0u,
				BitPos = 24,
				_bwd = false
			};
			result.RefillF();
			return result;
		}

		public static RefBitReader Backward(byte[] b, int low, int high)
		{
			RefBitReader result = new RefBitReader
			{
				B = b,
				P = high,
				Bound = low,
				Bits = 0u,
				BitPos = 24,
				_bwd = true
			};
			result.RefillB();
			return result;
		}

		public void Refill()
		{
			if (_bwd)
			{
				RefillB();
			}
			else
			{
				RefillF();
			}
		}

		public void RefillF()
		{
			while (BitPos > 0)
			{
				Bits |= (uint)(((P < Bound) ? B[P] : 0) << BitPos);
				BitPos -= 8;
				P++;
			}
		}

		public void RefillB()
		{
			while (BitPos > 0)
			{
				P--;
				Bits |= (uint)(((P >= Bound) ? B[P] : 0) << BitPos);
				BitPos -= 8;
			}
		}

		public int ReadBit()
		{
			Refill();
			uint result = Bits >> 31;
			Bits <<= 1;
			BitPos++;
			return (int)result;
		}

		public int ReadBitNoRefill()
		{
			uint result = Bits >> 31;
			Bits <<= 1;
			BitPos++;
			return (int)result;
		}

		public uint ReadBitsNoRefill(int n)
		{
			uint result = Bits >> 32 - n;
			Bits <<= n;
			BitPos += n;
			return result;
		}

		public uint ReadBitsNoRefillZero(int n)
		{
			uint result = Bits >> 1 >> 31 - n;
			Bits <<= n;
			BitPos += n;
			return result;
		}

		public int ReadFluff(int numSymbols)
		{
			if (numSymbols == 256)
			{
				return 0;
			}
			int num = 257 - numSymbols;
			if (num > numSymbols)
			{
				num = numSymbols;
			}
			num *= 2;
			int num2 = Bsr((uint)(num - 1)) + 1;
			uint num3 = Bits >> 32 - num2;
			uint num4 = (uint)((1 << num2) - num);
			if (num3 >> 1 >= num4)
			{
				Bits <<= num2;
				BitPos += num2;
				return (int)(num3 - num4);
			}
			Bits <<= num2 - 1;
			BitPos += num2 - 1;
			return (int)(num3 >> 1);
		}

		public uint ReadMoreThan24Bits(int n)
		{
			uint result;
			if (n <= 24)
			{
				result = ReadBitsNoRefillZero(n);
			}
			else
			{
				result = ReadBitsNoRefill(24) << n - 24;
				RefillF();
				result += ReadBitsNoRefill(n - 24);
			}
			RefillF();
			return result;
		}

		public uint ReadMoreThan24BitsB(int n)
		{
			uint result;
			if (n <= 24)
			{
				result = ReadBitsNoRefillZero(n);
			}
			else
			{
				result = ReadBitsNoRefill(24) << n - 24;
				RefillB();
				result += ReadBitsNoRefill(n - 24);
			}
			RefillB();
			return result;
		}

		public uint ReadDistance(uint v)
		{
			uint result;
			if (v < 240)
			{
				int num = (int)((v >> 4) + 4);
				uint num2 = BitOperations.RotateLeft(Bits | 1, num);
				BitPos += num;
				uint num3 = (uint)((2 << num) - 1);
				Bits = num2 & ~num3;
				result = ((num2 & num3) << 4) + (v & 0xF) - 248;
			}
			else
			{
				int num = (int)(v - 240 + 4);
				uint num2 = BitOperations.RotateLeft(Bits | 1, num);
				BitPos += num;
				uint num3 = (uint)((2 << num) - 1);
				Bits = num2 & ~num3;
				result = 8322816 + ((num2 & num3) << 12);
				RefillF();
				result += Bits >> 20;
				BitPos += 12;
				Bits <<= 12;
			}
			RefillF();
			return result;
		}

		public uint ReadDistanceB(uint v)
		{
			uint result;
			if (v < 240)
			{
				int num = (int)((v >> 4) + 4);
				uint num2 = BitOperations.RotateLeft(Bits | 1, num);
				BitPos += num;
				uint num3 = (uint)((2 << num) - 1);
				Bits = num2 & ~num3;
				result = ((num2 & num3) << 4) + (v & 0xF) - 248;
			}
			else
			{
				int num = (int)(v - 240 + 4);
				uint num2 = BitOperations.RotateLeft(Bits | 1, num);
				BitPos += num;
				uint num3 = (uint)((2 << num) - 1);
				Bits = num2 & ~num3;
				result = 8322816 + ((num2 & num3) << 12);
				RefillB();
				result += Bits >> 20;
				BitPos += 12;
				Bits <<= 12;
			}
			RefillB();
			return result;
		}

		public bool ReadLength(out uint v)
		{
			v = 0u;
			int num = 31 - Bsr(Bits);
			if (num > 12)
			{
				return false;
			}
			BitPos += num;
			Bits <<= num;
			RefillF();
			num += 7;
			BitPos += num;
			v = (Bits >> 32 - num) - 64;
			Bits <<= num;
			RefillF();
			return true;
		}

		public bool ReadLengthB(out uint v)
		{
			v = 0u;
			int num = 31 - Bsr(Bits);
			if (num > 12)
			{
				return false;
			}
			BitPos += num;
			Bits <<= num;
			RefillB();
			num += 7;
			BitPos += num;
			v = (Bits >> 32 - num) - 64;
			Bits <<= num;
			RefillB();
			return true;
		}
	}

	private const int ChunkMax = 131072;

	private const int SeedSize = 8;

	/// <summary>
	/// Diagnostic seam: when non-null, invoked once per decoded LZ command with
	/// (litRunLen, matchLen, distance, offsIndex). offsIndex 0/1/2 = recent-offset reuse, 3 = new
	/// offset; the final literal tail is reported as (tailLen, 0, 0, -1). Used by diagnostics
	/// to extract the reference parse from a real chunk. Null in production (zero cost).
	/// </summary>
	internal static Action<int, int, int, int>? ParseTrace = null;

	/// <summary>
	/// Diagnostic seam: when non-null, invoked with (label, srcOffset) at each newLZ array boundary
	/// ("litStart", "litEnd", "cmdEnd", "offsEnd", "litlenEnd"). Lets a diagnostic map which entropy
	/// array a byte-divergence falls in. Null in production (zero cost).
	/// </summary>
	internal static Action<string, int>? ArrayTrace = null;

	private const int Chunk0SubLitBit = 1;

	private const int Chunk0NewLzBit = 2;

	private const int Chunk1SubLitBit = 16;

	private const int Chunk1NewLzBit = 32;

	private static readonly uint[] CodePrefixOrg = new uint[12]
	{
		0u, 0u, 2u, 6u, 14u, 30u, 62u, 126u, 254u, 510u,
		766u, 1022u
	};

	private static readonly uint[] RiceVal = BuildRiceVal();

	private static readonly byte[] RiceLen = BuildRiceLen();

	/// <summary>
	/// Decodes a whole PFS block (the section-7 payload) into <paramref name="dst" /> (sized to the
	/// block's exact uncompressed length). <paramref name="flags" /> is the block's on-disk boundary
	/// flag byte; <paramref name="firstChunkComp" /> is the first sub-chunk's compressed size (from the
	/// boundary size hint) and is only consulted when the block spans two sub-chunks (its uncompressed
	/// size exceeds one 128 KiB chunk). A two-chunk block's sub-chunks have INDEPENDENT types — each is
	/// newLZ or a bare-entropy <c>Kraken_DecodeBytes</c> array selected by <paramref name="flags" />
	/// (chunk0: bit <c>0x02</c>; chunk1: bit <c>0x20</c>) — so they are decoded separately, the second
	/// seedless and able to back-reference the first. The literal model for each newLZ sub-chunk comes
	/// from its low bit (chunk0 <c>0x01</c>, chunk1 <c>0x10</c>): set = sub/delta, clear = raw.
	/// </summary>
	public static KrakenDecodeStatus DecodeBlock(ReadOnlySpan<byte> payload, int flags, int firstChunkComp, Span<byte> dst)
	{
		if (dst.Length <= 0)
		{
			if (payload.Length != 0)
			{
				return KrakenDecodeStatus.Malformed;
			}
			return KrakenDecodeStatus.Success;
		}
		byte[] array = payload.ToArray();
		byte[] array2 = new byte[dst.Length];
		bool flag = (flags & 2) != 0;
		int literalMode = (((flags & 1) == 0) ? 1 : 0);
		KrakenDecodeStatus krakenDecodeStatus;
		if (dst.Length <= 131072)
		{
			krakenDecodeStatus = (flag ? DecodeChunk(array, 0, array.Length, array2, 0, dst.Length, withSeed: true, literalMode) : DecodeStoredOrBareEntropyBlock(array, 0, array.Length, array2, 0, dst.Length));
		}
		else
		{
			if (firstChunkComp <= 0 || firstChunkComp > array.Length || dst.Length <= 131072)
			{
				return KrakenDecodeStatus.Malformed;
			}
			bool flag2 = (flags & 0x20) != 0;
			int literalMode2 = (((flags & 0x10) == 0) ? 1 : 0);
			int srcLen = array.Length - firstChunkComp;
			int num = dst.Length - 131072;
			krakenDecodeStatus = (flag ? DecodeChunk(array, 0, firstChunkComp, array2, 0, 131072, withSeed: true, literalMode) : DecodeStoredOrBareEntropyBlock(array, 0, firstChunkComp, array2, 0, 131072));
			if (krakenDecodeStatus != KrakenDecodeStatus.Success)
			{
				return krakenDecodeStatus;
			}
			krakenDecodeStatus = (flag2 ? DecodeChunk(array, firstChunkComp, srcLen, array2, 131072, num, withSeed: false, literalMode2) : DecodeStoredOrBareEntropyBlock(array, firstChunkComp, srcLen, array2, 131072, num));
		}
		if (krakenDecodeStatus == KrakenDecodeStatus.Success)
		{
			array2.AsSpan(0, dst.Length).CopyTo(dst);
		}
		return krakenDecodeStatus;
	}

	private static KrakenDecodeStatus DecodeStoredOrBareEntropyBlock(byte[] src, int srcStart, int srcLen, byte[] outBuf, int dstStart, int dstLen)
	{
		if (srcLen == dstLen)
		{
			Array.Copy(src, srcStart, outBuf, dstStart, dstLen);
			return KrakenDecodeStatus.Success;
		}
		return DecodeBareEntropyBlock(src, srcStart, srcLen, outBuf, dstStart, dstLen);
	}

	/// <summary>
	/// Decodes one bare-entropy array: <paramref name="srcLen" /> bytes at <paramref name="srcStart" />
	/// are a single <c>Kraken_DecodeBytes</c> array (raw type-0 or Huffman type-2/4) that expands to
	/// exactly <paramref name="dstLen" /> bytes written at <paramref name="dstStart" /> in
	/// <paramref name="outBuf" /> — no seed, no excess framing, no LZ table, no back-references. A block
	/// larger than one 128 KiB chunk is encoded as two such arrays back-to-back (each independent).
	/// The array must consume its whole source span.
	/// </summary>
	private static KrakenDecodeStatus DecodeBareEntropyBlock(byte[] src, int srcStart, int srcLen, byte[] outBuf, int dstStart, int dstLen)
	{
		if (srcLen < 0 || srcStart + srcLen > src.Length || dstLen < 0 || dstStart + dstLen > outBuf.Length)
		{
			return KrakenDecodeStatus.Malformed;
		}
		int num = DecodeBytes(src, srcStart, srcStart + srcLen, dstLen, out byte[] output, out int decodedSize);
		if (num < 0)
		{
			if (num != -2)
			{
				return KrakenDecodeStatus.Malformed;
			}
			return KrakenDecodeStatus.UnsupportedEntropy;
		}
		if (num != srcLen || decodedSize != dstLen)
		{
			return KrakenDecodeStatus.Malformed;
		}
		Array.Copy(output, 0, outBuf, dstStart, dstLen);
		return KrakenDecodeStatus.Success;
	}

	/// <summary>
	/// Decodes one newLZ chunk covering output range
	/// [<paramref name="dstStart" />, dstStart + <paramref name="dstSize" />) within the shared block
	/// buffer <paramref name="dst" />. Matches may reference any earlier byte in <paramref name="dst" />.
	/// </summary>
	private static KrakenDecodeStatus DecodeChunk(byte[] src, int srcStart, int srcLen, byte[] dst, int dstStart, int dstSize, bool withSeed, int literalMode)
	{
		if (dstSize <= 0 || dstStart + dstSize > dst.Length || srcLen < 0 || srcStart + srcLen > src.Length)
		{
			return KrakenDecodeStatus.Malformed;
		}
		int srcEnd = srcStart + srcLen;
		int sp = srcStart;
		int offset = ((!withSeed) ? dstStart : 0);
		LzTable lzt = new LzTable();
		int num = ReadLzTable(src, ref sp, srcEnd, dst, dstStart, dstSize, offset, lzt);
		if (num < 0)
		{
			if (num != -2)
			{
				return KrakenDecodeStatus.Malformed;
			}
			return KrakenDecodeStatus.UnsupportedEntropy;
		}
		if (!ProcessLzRuns(literalMode, dst, dstStart, dstSize, offset, lzt))
		{
			return KrakenDecodeStatus.Malformed;
		}
		return KrakenDecodeStatus.Success;
	}

	private static int ReadLzTable(byte[] src, ref int sp, int srcEnd, byte[] dst, int dstStart, int dstSize, int offset, LzTable lzt)
	{
		if (offset == 0)
		{
			if (srcEnd - sp < 8)
			{
				return -1;
			}
			Array.Copy(src, sp, dst, dstStart, 8);
			sp += 8;
		}
		bool excessFlag = false;
		int num = 0;
		if (sp < srcEnd && (src[sp] & 0x80) != 0)
		{
			byte b = src[sp++];
			if ((b & 0xC0) != 128)
			{
				return -1;
			}
			excessFlag = true;
			num = b & 0x3F;
			if (num > 31)
			{
				if (sp >= srcEnd)
				{
					return -1;
				}
				num += src[sp++] * 32;
			}
			srcEnd -= num;
			if (srcEnd < sp)
			{
				return -1;
			}
		}
		ArrayTrace?.Invoke("litStart", sp);
		int num2 = DecodeBytes(src, sp, srcEnd, dstSize, out byte[] output, out int decodedSize);
		if (num2 < 0)
		{
			return num2;
		}
		sp += num2;
		lzt.LitStream = output;
		lzt.LitStreamSize = decodedSize;
		ArrayTrace?.Invoke("litEnd", sp);
		num2 = DecodeBytes(src, sp, srcEnd, dstSize, out byte[] output2, out int decodedSize2);
		if (num2 < 0)
		{
			return num2;
		}
		sp += num2;
		lzt.CmdStream = output2;
		lzt.CmdStreamSize = decodedSize2;
		ArrayTrace?.Invoke("cmdEnd", sp);
		if (srcEnd - sp < 3)
		{
			return -1;
		}
		int num3 = 0;
		byte[] output3 = Array.Empty<byte>();
		byte[] output4;
		int decodedSize3;
		if ((src[sp] & 0x80) != 0)
		{
			num3 = src[sp] - 127;
			sp++;
			num2 = DecodeBytes(src, sp, srcEnd, decodedSize2, out output4, out decodedSize3);
			if (num2 < 0)
			{
				return num2;
			}
			sp += num2;
			if (num3 != 1)
			{
				num2 = DecodeBytes(src, sp, srcEnd, decodedSize3, out output3, out int decodedSize4);
				if (num2 < 0)
				{
					return num2;
				}
				if (decodedSize4 != decodedSize3)
				{
					return -1;
				}
				sp += num2;
			}
		}
		else
		{
			num2 = DecodeBytes(src, sp, srcEnd, decodedSize2, out output4, out decodedSize3);
			if (num2 < 0)
			{
				return num2;
			}
			sp += num2;
		}
		lzt.OffsStreamSize = decodedSize3;
		ArrayTrace?.Invoke("offsEnd", sp);
		num2 = DecodeBytes(src, sp, srcEnd, dstSize >> 2, out byte[] output5, out int decodedSize5);
		if (num2 < 0)
		{
			return num2;
		}
		sp += num2;
		lzt.LenStreamSize = decodedSize5;
		ArrayTrace?.Invoke("litlenEnd", sp);
		lzt.OffsStream = new int[decodedSize3];
		lzt.LenStream = new int[decodedSize5];
		if (!UnpackOffsets(src, sp, srcEnd, excessFlag, num, output4, decodedSize3, num3, output3, output5, decodedSize5, lzt.OffsStream, lzt.LenStream))
		{
			return -1;
		}
		return sp;
	}

	private static bool UnpackOffsets(byte[] src, int bsBegin, int bsEnd, bool excessFlag, int excessCount, byte[] packedOffs, int offsCount, int offsScaling, byte[] packedOffsExtra, byte[] packedLitLen, int lenCount, int[] offsStream, int[] lenStream)
	{
		RefBitReader refBitReader = RefBitReader.Forward(src, bsBegin, bsEnd);
		RefBitReader refBitReader2 = RefBitReader.Backward(src, bsBegin, bsEnd);
		int num = 0;
		if (!excessFlag)
		{
			if (refBitReader2.Bits < 8192)
			{
				return false;
			}
			int num2 = 31 - Bsr(refBitReader2.Bits);
			refBitReader2.BitPos += num2;
			refBitReader2.Bits <<= num2;
			refBitReader2.RefillB();
			num2++;
			num = (int)((refBitReader2.Bits >> 32 - num2) - 1);
			refBitReader2.BitPos += num2;
			refBitReader2.Bits <<= num2;
			refBitReader2.RefillB();
		}
		else
		{
			for (int i = 0; i < lenCount; i++)
			{
				if (packedLitLen[i] == byte.MaxValue)
				{
					num++;
				}
			}
		}
		if (offsScaling == 0)
		{
			int num3;
			for (num3 = 0; num3 < offsCount; num3++)
			{
				offsStream[num3] = (int)(0 - refBitReader.ReadDistance(packedOffs[num3]));
				num3++;
				if (num3 >= offsCount)
				{
					break;
				}
				offsStream[num3] = (int)(0 - refBitReader2.ReadDistanceB(packedOffs[num3]));
			}
		}
		else
		{
			int num4;
			for (num4 = 0; num4 < offsCount; num4++)
			{
				uint num5 = packedOffs[num4];
				if (num5 >> 3 > 26)
				{
					return false;
				}
				uint num6 = (8 + (num5 & 7) << (int)(num5 >> 3)) | refBitReader.ReadMoreThan24Bits((int)(num5 >> 3));
				offsStream[num4] = (int)(8 - num6);
				num4++;
				if (num4 >= offsCount)
				{
					break;
				}
				num5 = packedOffs[num4];
				if (num5 >> 3 > 26)
				{
					return false;
				}
				num6 = (8 + (num5 & 7) << (int)(num5 >> 3)) | refBitReader2.ReadMoreThan24BitsB((int)(num5 >> 3));
				offsStream[num4] = (int)(8 - num6);
			}
			if (offsScaling != 1)
			{
				CombineScaledOffsets(offsStream, offsCount, offsScaling, packedOffsExtra);
			}
		}
		if (num > 512)
		{
			return false;
		}
		uint[] array = new uint[num];
		if (excessFlag)
		{
			RefBitReader refBitReader3 = RefBitReader.Forward(src, bsEnd, bsEnd + excessCount);
			RefBitReader refBitReader4 = RefBitReader.Backward(src, bsEnd, bsEnd + excessCount);
			int j;
			for (j = 0; j + 1 < num; j += 2)
			{
				if (!refBitReader3.ReadLength(out array[j]))
				{
					return false;
				}
				if (!refBitReader4.ReadLengthB(out array[j + 1]))
				{
					return false;
				}
			}
			if (j < num && !refBitReader3.ReadLength(out array[j]))
			{
				return false;
			}
		}
		else
		{
			int k;
			for (k = 0; k + 1 < num; k += 2)
			{
				if (!refBitReader.ReadLength(out array[k]))
				{
					return false;
				}
				if (!refBitReader2.ReadLengthB(out array[k + 1]))
				{
					return false;
				}
			}
			if (k < num && !refBitReader.ReadLength(out array[k]))
			{
				return false;
			}
		}
		int num7 = refBitReader.P - (24 - refBitReader.BitPos >> 3);
		int num8 = refBitReader2.P + (24 - refBitReader2.BitPos >> 3);
		if (num7 != num8 && !excessFlag)
		{
			return false;
		}
		int num9 = 0;
		for (int l = 0; l < lenCount; l++)
		{
			uint num10 = packedLitLen[l];
			if (num10 == 255)
			{
				if (num9 >= num)
				{
					return false;
				}
				num10 = array[num9++] + 255;
			}
			lenStream[l] = (int)(num10 + 3);
		}
		return num9 == num;
	}

	private static void CombineScaledOffsets(int[] offs, int count, int scale, byte[] extra)
	{
		for (int i = 0; i < count; i++)
		{
			offs[i] = (sbyte)extra[i] - offs[i] * scale;
		}
	}

	private static int DecodeBytes(byte[] src, int sp, int srcEnd, int outputCap, out byte[] output, out int decodedSize)
	{
		output = Array.Empty<byte>();
		decodedSize = 0;
		int num = sp;
		if (srcEnd - sp < 2)
		{
			return -1;
		}
		int num2 = (src[sp] >> 4) & 7;
		int num3;
		if (num2 == 0)
		{
			if (src[sp] >= 128)
			{
				num3 = ((src[sp] << 8) | src[sp + 1]) & 0xFFF;
				sp += 2;
			}
			else
			{
				if (srcEnd - sp < 3)
				{
					return -1;
				}
				num3 = (src[sp] << 16) | (src[sp + 1] << 8) | src[sp + 2];
				if ((num3 & -262144) != 0)
				{
					return -1;
				}
				sp += 3;
			}
			if (num3 > outputCap || srcEnd - sp < num3)
			{
				return -1;
			}
			output = new byte[num3];
			Array.Copy(src, sp, output, 0, num3);
			decodedSize = num3;
			return sp + num3 - num;
		}
		int num5;
		if (src[sp] >= 128)
		{
			if (srcEnd - sp < 3)
			{
				return -1;
			}
			uint num4 = (uint)((src[sp] << 16) | (src[sp + 1] << 8) | src[sp + 2]);
			num3 = (int)(num4 & 0x3FF);
			num5 = (int)(num3 + ((num4 >> 10) & 0x3FF) + 1);
			sp += 3;
		}
		else
		{
			if (srcEnd - sp < 5)
			{
				return -1;
			}
			int num6 = (src[sp + 1] << 24) | (src[sp + 2] << 16) | (src[sp + 3] << 8) | src[sp + 4];
			num3 = num6 & 0x3FFFF;
			num5 = (((num6 >>> 18) | (src[sp] << 14)) & 0x3FFFF) + 1;
			if (num3 >= num5)
			{
				return -1;
			}
			sp += 5;
		}
		if (srcEnd - sp < num3 || num5 > outputCap)
		{
			return -1;
		}
		output = new byte[num5];
		if (num2 == 2 || num2 == 4)
		{
			int num7 = DecodeBytesType12(src, sp, num3, output, num5, num2 >> 1);
			if (num7 != num3)
			{
				return -1;
			}
			decodedSize = num5;
			return sp + num3 - num;
		}
		return -2;
	}

	/// <summary>
	/// Walks a single newLZ chunk exactly like <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.KrakenDecoder.ReadLzTable(System.Byte[],System.Int32@,System.Int32,System.Byte[],System.Int32,System.Int32,System.Int32,LibProsperoPkg.PFS.Compression.Oodle.KrakenDecoder.LzTable)" /> but, instead of building the LZ
	/// table, records each entropy/raw array's exact byte span, chunk type and code-length transmission
	/// plus its decoded bytes. Used by diagnostics to compare this library's entropy encoding against
	/// reference arrays byte-for-byte. Returns 0 on success or a negative error.
	/// </summary>
	internal static int InspectChunkArrays(byte[] src, int sp, int srcEnd, int offset, int dstSize, out List<KrakenArrayInfo> arrays)
	{
		List<KrakenArrayInfo> list = new List<KrakenArrayInfo>();
		arrays = list;
		if (offset == 0)
		{
			if (srcEnd - sp < 8)
			{
				return -1;
			}
			sp += 8;
		}
		if (sp < srcEnd && (src[sp] & 0x80) != 0)
		{
			byte b = src[sp++];
			if ((b & 0xC0) != 128)
			{
				return -1;
			}
			int num = b & 0x3F;
			if (num > 31)
			{
				if (sp >= srcEnd)
				{
					return -1;
				}
				num += src[sp++] * 32;
			}
			srcEnd -= num;
			if (srcEnd < sp)
			{
				return -1;
			}
		}
		if (Record("lit", dstSize, out var err) == null)
		{
			return err;
		}
		KrakenArrayInfo krakenArrayInfo = Record("cmd", dstSize, out var err2);
		if (krakenArrayInfo == null)
		{
			return err2;
		}
		int cap = krakenArrayInfo.Decoded.Length;
		if (srcEnd - sp < 3)
		{
			return -10;
		}
		int err5;
		if ((src[sp] & 0x80) != 0)
		{
			int num2 = src[sp] - 127;
			sp++;
			KrakenArrayInfo krakenArrayInfo2 = Record("offs", cap, out var err3);
			if (krakenArrayInfo2 == null)
			{
				return err3;
			}
			if (num2 != 1 && Record("offsExtra", krakenArrayInfo2.Decoded.Length, out var err4) == null)
			{
				return err4;
			}
		}
		else if (Record("offs", cap, out err5) == null)
		{
			return err5;
		}
		if (Record("litlen", dstSize >> 2, out var err6) == null)
		{
			return err6;
		}
		return 0;
		KrakenArrayInfo? Record(string name, int outputCap, out int reference)
		{
			int num3 = sp;
			int num4 = (reference = DecodeBytes(src, sp, srcEnd, outputCap, out byte[] output, out int decodedSize));
			if (num4 < 0)
			{
				return null;
			}
			KrakenArrayInfo krakenArrayInfo3 = new KrakenArrayInfo
			{
				Name = name,
				SrcOffset = num3,
				SrcLen = num4,
				ChunkType = ((src[num3] >> 4) & 7),
				Decoded = ((output.Length == decodedSize) ? output : output[..decodedSize])
			};
			krakenArrayInfo3.Transmission = PeekTransmission(src, num3, num3 + num4, krakenArrayInfo3.ChunkType);
			sp += num4;
			list.Add(krakenArrayInfo3);
			return krakenArrayInfo3;
		}
	}

	private static int PeekTransmission(byte[] src, int sp, int arrEnd, int chunkType)
	{
		if (chunkType != 2 && chunkType != 4)
		{
			return -1;
		}
		int num = ((src[sp] >= 128) ? 3 : 5);
		int num2 = sp + num;
		if (num2 >= arrEnd)
		{
			return -1;
		}
		RefBitReader refBitReader = RefBitReader.Forward(src, num2, arrEnd);
		if (refBitReader.ReadBitNoRefill() == 0)
		{
			return 0;
		}
		if (refBitReader.ReadBitNoRefill() == 0)
		{
			return 1;
		}
		return 2;
	}

	internal static bool InspectType12(byte[] src, int sp, int srcEnd, out Type12Info info)
	{
		info = new Type12Info();
		if (src == null || sp < 0 || srcEnd > src.Length || srcEnd - sp < 2)
		{
			return false;
		}
		int num = (src[sp] >> 4) & 7;
		if (num != 2 && num != 4)
		{
			return false;
		}
		info.ChunkType = num;
		int num3;
		int dstSize;
		int num4;
		if (src[sp] >= 128)
		{
			if (srcEnd - sp < 3)
			{
				return false;
			}
			uint num2 = (uint)((src[sp] << 16) | (src[sp + 1] << 8) | src[sp + 2]);
			num3 = (int)(num2 & 0x3FF);
			dstSize = (int)(num3 + ((num2 >> 10) & 0x3FF) + 1);
			num4 = 3;
		}
		else
		{
			if (srcEnd - sp < 5)
			{
				return false;
			}
			uint num5 = (uint)((src[sp + 1] << 24) | (src[sp + 2] << 16) | (src[sp + 3] << 8) | src[sp + 4]);
			num3 = (int)(num5 & 0x3FFFF);
			dstSize = (int)((((uint)(src[sp] << 14) | (num5 >> 18)) & 0x3FFFF) + 1);
			num4 = 5;
		}
		info.SrcSize = num3;
		info.DstSize = dstSize;
		info.HeaderLen = num4;
		int num6 = sp + num4;
		int num7 = num6 + num3;
		if (num7 > srcEnd || num6 >= num7)
		{
			return false;
		}
		RefBitReader bits = RefBitReader.Forward(src, num6, num7);
		uint[] array = (uint[])CodePrefixOrg.Clone();
		byte[] array2 = new byte[1280];
		int num8;
		if (bits.ReadBitNoRefill() == 0)
		{
			info.Transmission = 0;
			num8 = HuffReadCodeLengthsOld(ref bits, array2, array);
		}
		else
		{
			if (bits.ReadBitNoRefill() != 0)
			{
				info.Transmission = 2;
				return false;
			}
			info.Transmission = 1;
			num8 = HuffReadCodeLengthsNew(ref bits, array2, array);
		}
		if (num8 < 1)
		{
			return false;
		}
		info.NumSyms = num8;
		for (int i = 1; i <= 11; i++)
		{
			for (uint num9 = CodePrefixOrg[i]; num9 < array[i]; num9++)
			{
				info.CodeLengths[array2[num9]] = (byte)i;
			}
		}
		int num10 = bits.P - (24 - bits.BitPos) / 8;
		info.CodeLenEndOffset = num10;
		if (num8 == 1)
		{
			info.PayloadOffset = num10;
			return true;
		}
		if (num >> 1 == 1)
		{
			if (num10 + 2 > num7)
			{
				return false;
			}
			info.SplitMid = src[num10] | (src[num10 + 1] << 8);
			info.PayloadOffset = num10 + 2;
		}
		else
		{
			if (num10 + 5 > num7)
			{
				return false;
			}
			info.SplitMid = src[num10] | (src[num10 + 1] << 8) | (src[num10 + 2] << 16);
			info.SplitLeft = src[num10 + 3] | (src[num10 + 4] << 8);
			int num11 = num10 + 3 + info.SplitMid;
			if (num11 + 2 <= num7)
			{
				info.SplitRight = src[num11] | (src[num11 + 1] << 8);
			}
			info.PayloadOffset = num10 + 5;
		}
		return true;
	}

	private static int DecodeBytesType12(byte[] src, int srcStart, int srcSize, byte[] output, int outputSize, int type)
	{
		int num = srcStart + srcSize;
		RefBitReader bits = RefBitReader.Forward(src, srcStart, num);
		uint[] array = (uint[])CodePrefixOrg.Clone();
		byte[] array2 = new byte[1280];
		int num2;
		if (bits.ReadBitNoRefill() == 0)
		{
			num2 = HuffReadCodeLengthsOld(ref bits, array2, array);
		}
		else
		{
			if (bits.ReadBitNoRefill() != 0)
			{
				return -1;
			}
			num2 = HuffReadCodeLengthsNew(ref bits, array2, array);
		}
		if (num2 < 1)
		{
			return -1;
		}
		int num3 = bits.P - (24 - bits.BitPos) / 8;
		if (num2 == 1)
		{
			byte b = array2[0];
			for (int i = 0; i < outputSize; i++)
			{
				output[i] = b;
			}
			return srcSize;
		}
		NewHuffLut newHuffLut = new NewHuffLut();
		if (!HuffMakeLut(CodePrefixOrg, array, newHuffLut, array2))
		{
			return -1;
		}
		NewHuffLut newHuffLut2 = new NewHuffLut();
		ReverseBitsArray2048(newHuffLut.Bits2Len, newHuffLut2.Bits2Len);
		ReverseBitsArray2048(newHuffLut.Bits2Sym, newHuffLut2.Bits2Sym);
		if (type == 1)
		{
			if (num3 + 3 > num)
			{
				return -1;
			}
			int num4 = src[num3] | (src[num3 + 1] << 8);
			num3 += 2;
			HuffReader huffReader = new HuffReader(src, output);
			huffReader.OutOff = 0;
			huffReader.OutEnd = outputSize;
			huffReader.Src = num3;
			huffReader.SrcEnd = num;
			huffReader.SrcMidOrg = num3 + num4;
			huffReader.SrcMid = num3 + num4;
			HuffReader hr = huffReader;
			if (!DecodeBytesCore(ref hr, newHuffLut2))
			{
				return -1;
			}
		}
		else
		{
			if (num3 + 6 > num)
			{
				return -1;
			}
			int num5 = outputSize + 1 >> 1;
			int num6 = src[num3] | (src[num3 + 1] << 8) | (src[num3 + 2] << 16);
			num3 += 3;
			if (num6 > num - num3)
			{
				return -1;
			}
			int num7 = num3 + num6;
			int num8 = src[num3] | (src[num3 + 1] << 8);
			num3 += 2;
			if (num7 - num3 < num8 + 2 || num - num7 < 3)
			{
				return -1;
			}
			int num9 = src[num7] | (src[num7 + 1] << 8);
			if (num - (num7 + 2) < num9 + 2)
			{
				return -1;
			}
			HuffReader huffReader = new HuffReader(src, output);
			huffReader.OutOff = 0;
			huffReader.OutEnd = num5;
			huffReader.Src = num3;
			huffReader.SrcEnd = num7;
			huffReader.SrcMidOrg = num3 + num8;
			huffReader.SrcMid = num3 + num8;
			HuffReader hr2 = huffReader;
			if (!DecodeBytesCore(ref hr2, newHuffLut2))
			{
				return -1;
			}
			huffReader = new HuffReader(src, output);
			huffReader.OutOff = num5;
			huffReader.OutEnd = outputSize;
			huffReader.Src = num7 + 2;
			huffReader.SrcEnd = num;
			huffReader.SrcMidOrg = num7 + 2 + num9;
			huffReader.SrcMid = num7 + 2 + num9;
			HuffReader hr3 = huffReader;
			if (!DecodeBytesCore(ref hr3, newHuffLut2))
			{
				return -1;
			}
		}
		return srcSize;
	}

	private static bool HuffMakeLut(uint[] prefixOrg, uint[] prefixCur, NewHuffLut lut, byte[] syms)
	{
		uint num = 0u;
		for (uint num2 = 1u; num2 < 11; num2++)
		{
			uint num3 = prefixOrg[num2];
			uint num4 = prefixCur[num2] - num3;
			if (num4 != 0)
			{
				uint num5 = (uint)(1 << (int)(11 - num2));
				uint num6 = num4 << (int)(11 - num2);
				if (num + num6 > 2048)
				{
					return false;
				}
				FillByte(lut.Bits2Len, (int)num, (byte)num2, (int)num6);
				int num7 = (int)num;
				uint num8 = 0u;
				while (num8 != num4)
				{
					FillByte(lut.Bits2Sym, num7, syms[num3 + num8], (int)num5);
					num8++;
					num7 += (int)num5;
				}
				num += num6;
			}
		}
		if (prefixCur[11] - prefixOrg[11] != 0)
		{
			uint num9 = prefixCur[11] - prefixOrg[11];
			if (num + num9 > 2048)
			{
				return false;
			}
			FillByte(lut.Bits2Len, (int)num, 11, (int)num9);
			Array.Copy(syms, (int)prefixOrg[11], lut.Bits2Sym, (int)num, (int)num9);
			num += num9;
		}
		return num == 2048;
	}

	private static void FillByte(byte[] dst, int off, byte v, int n)
	{
		for (int i = 0; i < n; i++)
		{
			dst[off + i] = v;
		}
	}

	private static void ReverseBitsArray2048(byte[] input, byte[] output)
	{
		for (int i = 0; i < 2048; i++)
		{
			int num = Reverse11(i);
			output[i] = input[num];
		}
	}

	private static int Reverse11(int v)
	{
		int num = 0;
		for (int i = 0; i < 11; i++)
		{
			num = (num << 1) | (v & 1);
			v >>= 1;
		}
		return num;
	}

	private static int HuffReadCodeLengthsOld(ref RefBitReader bits, byte[] syms, uint[] codePrefix)
	{
		if (bits.ReadBitNoRefill() != 0)
		{
			int num = 0;
			int num2 = 0;
			int num3 = 32;
			int num4 = (int)bits.ReadBitsNoRefill(2);
			uint num5 = (uint)(1 << 31 - (20 >>> num4));
			bool flag = bits.ReadBit() != 0;
			do
			{
				if (!flag)
				{
					if ((bits.Bits & 0xFF000000u) == 0)
					{
						return -1;
					}
					num += (int)(bits.ReadBitsNoRefill(2 * (Clz(bits.Bits) + 1)) - 2 + 1);
					if (num >= 256)
					{
						break;
					}
				}
				flag = false;
				bits.Refill();
				if ((bits.Bits & 0xFF000000u) == 0)
				{
					return -1;
				}
				int num6 = (int)(bits.ReadBitsNoRefill(2 * (Clz(bits.Bits) + 1)) - 2 + 1);
				if (num + num6 > 256)
				{
					return -1;
				}
				bits.Refill();
				num2 += num6;
				do
				{
					if (bits.Bits < num5)
					{
						return -1;
					}
					int num7 = Clz(bits.Bits);
					int num8 = (int)bits.ReadBitsNoRefill(num7 + num4 + 1) + (num7 - 1 << num4);
					int num9 = (-(num8 & 1) ^ (num8 >> 1)) + (num3 + 2 >> 2);
					if (num9 < 1 || num9 > 11)
					{
						return -1;
					}
					num3 = num9 + (3 * num3 + 2 >> 2);
					bits.Refill();
					syms[codePrefix[num9]++] = (byte)num++;
				}
				while (--num6 != 0);
			}
			while (num != 256);
			if (num != 256 || num2 < 2)
			{
				return -1;
			}
			return num2;
		}
		int num10 = (int)bits.ReadBitsNoRefill(8);
		switch (num10)
		{
		case 0:
			return -1;
		case 1:
			syms[0] = (byte)bits.ReadBitsNoRefill(8);
			break;
		default:
		{
			int num11 = (int)bits.ReadBitsNoRefill(3);
			if (num11 > 4)
			{
				return -1;
			}
			for (int i = 0; i < num10; i++)
			{
				bits.Refill();
				int num12 = (int)bits.ReadBitsNoRefill(8);
				int num13 = (int)(bits.ReadBitsNoRefillZero(num11) + 1);
				if (num13 > 11)
				{
					return -1;
				}
				syms[codePrefix[num13]++] = (byte)num12;
			}
			break;
		}
		}
		return num10;
	}

	private static int HuffReadCodeLengthsNew(ref RefBitReader bits, byte[] syms, uint[] codePrefix)
	{
		int bitcount = (int)bits.ReadBitsNoRefill(2);
		int num = (int)(bits.ReadBitsNoRefill(8) + 1);
		int num2 = bits.ReadFluff(num);
		byte[] array = new byte[528];
		RefBitReader2 br = new RefBitReader2
		{
			B = bits.B,
			BitPos = ((bits.BitPos - 24) & 7),
			PEnd = bits.Bound,
			P = bits.P - (24 - bits.BitPos + 7 >> 3)
		};
		if (!DecodeGolombRiceLengths(array, num + num2, ref br))
		{
			return -1;
		}
		for (int i = 0; i < 16; i++)
		{
			array[num + num2 + i] = 0;
		}
		if (!DecodeGolombRiceBits(array, num, bitcount, ref br))
		{
			return -1;
		}
		bits.BitPos = 24;
		bits.P = br.P;
		bits.Bits = 0u;
		bits.Refill();
		bits.Bits <<= br.BitPos;
		bits.BitPos += br.BitPos;
		uint num3 = 30u;
		for (int j = 0; j < num; j++)
		{
			int num4 = array[j];
			num4 = -(num4 & 1) ^ (num4 >> 1);
			int num5 = num4 + (int)(num3 >> 2) + 1;
			if (num5 < 1 || num5 > 11)
			{
				return -1;
			}
			array[j] = (byte)num5;
			num3 += (uint)num4;
		}
		HuffRange[] array2 = new HuffRange[128];
		int num6 = HuffConvertToRanges(array2, num, num2, array, num, ref bits);
		if (num6 <= 0)
		{
			return -1;
		}
		int num7 = 0;
		for (int k = 0; k < num6; k++)
		{
			int symbol = array2[k].Symbol;
			int num8 = array2[k].Num;
			do
			{
				syms[codePrefix[array[num7++]]++] = (byte)symbol++;
			}
			while (--num8 != 0);
		}
		return num;
	}

	private static int HuffConvertToRanges(HuffRange[] range, int numSymbols, int p, byte[] symlen, int symlenOff, ref RefBitReader bits)
	{
		int num = p >> 1;
		int num2 = 0;
		if ((p & 1) != 0)
		{
			bits.Refill();
			int num3 = symlen[symlenOff++];
			if (num3 >= 8)
			{
				return -1;
			}
			num2 = (int)bits.ReadBitsNoRefill(num3 + 1) + (1 << num3 + 1) - 1;
		}
		int num4 = 0;
		for (int i = 0; i < num; i++)
		{
			bits.Refill();
			int num3 = symlen[symlenOff];
			if (num3 >= 9)
			{
				return -1;
			}
			int num5 = (int)bits.ReadBitsNoRefillZero(num3) + (1 << num3);
			num3 = symlen[symlenOff + 1];
			if (num3 >= 8)
			{
				return -1;
			}
			int num6 = (int)bits.ReadBitsNoRefill(num3 + 1) + (1 << num3 + 1) - 1;
			range[i].Symbol = num2;
			range[i].Num = num5;
			num4 += num5;
			num2 += num5 + num6;
			symlenOff += 2;
		}
		if (num2 >= 256 || num4 >= numSymbols || num2 + numSymbols - num4 > 256)
		{
			return -1;
		}
		range[num].Symbol = num2;
		range[num].Num = numSymbols - num4;
		return num + 1;
	}

	private static bool DecodeBytesCore(ref HuffReader hr, NewHuffLut lut)
	{
		byte[] b = hr.B;
		byte[] array = hr.Out;
		byte[] bits2Len = lut.Bits2Len;
		byte[] bits2Sym = lut.Bits2Sym;
		int num = hr.Src;
		uint num2 = hr.SrcBits;
		int num3 = hr.SrcBitpos;
		int num4 = hr.SrcMid;
		uint num5 = hr.SrcMidBits;
		int num6 = hr.SrcMidBitpos;
		int num7 = hr.SrcEnd;
		uint num8 = hr.SrcEndBits;
		int num9 = hr.SrcEndBitpos;
		int outOff = hr.OutOff;
		int outEnd = hr.OutEnd;
		if (num > num4)
		{
			return false;
		}
		while (outOff < outEnd)
		{
			if (num4 - num <= 1)
			{
				if (num4 - num == 1)
				{
					num2 |= (uint)(b[num] << num3);
				}
			}
			else
			{
				num2 |= (uint)((b[num] | (b[num + 1] << 8)) << num3);
			}
			int num10 = (int)(num2 & 0x7FF);
			int num11 = bits2Len[num10];
			num3 -= num11;
			num2 >>= num11;
			array[outOff++] = bits2Sym[num10];
			num += 7 - num3 >> 3;
			num3 &= 7;
			if (outOff < outEnd)
			{
				if (num7 - num4 <= 1)
				{
					if (num7 - num4 == 1)
					{
						num8 |= (uint)(b[num4] << num9);
						num5 |= (uint)(b[num4] << num6);
					}
				}
				else
				{
					uint num12 = (uint)(b[num7 - 2] | (b[num7 - 1] << 8));
					num8 |= (((num12 >> 8) | (num12 << 8)) & 0xFFFF) << num9;
					num5 |= (uint)((b[num4] | (b[num4 + 1] << 8)) << num6);
				}
				num10 = (int)(num8 & 0x7FF);
				num11 = bits2Len[num10];
				array[outOff++] = bits2Sym[num10];
				num9 -= num11;
				num8 >>= num11;
				num7 -= 7 - num9 >> 3;
				num9 &= 7;
				if (outOff < outEnd)
				{
					num10 = (int)(num5 & 0x7FF);
					num11 = bits2Len[num10];
					array[outOff++] = bits2Sym[num10];
					num6 -= num11;
					num5 >>= num11;
					num4 += 7 - num6 >> 3;
					num6 &= 7;
				}
			}
			if (num > num4 || num4 > num7)
			{
				return false;
			}
		}
		if (num != hr.SrcMidOrg || num7 != num4)
		{
			return false;
		}
		return true;
	}

	private static bool DecodeGolombRiceLengths(byte[] dst, int size, ref RefBitReader2 br)
	{
		int num = br.P;
		int pEnd = br.PEnd;
		int num2 = 0;
		if (num >= pEnd)
		{
			return false;
		}
		int num3 = -br.BitPos;
		uint num4 = (uint)(At(br.B, num++) & (255 >> br.BitPos));
		while (true)
		{
			if (num4 == 0)
			{
				num3 += 8;
			}
			else
			{
				uint num5 = RiceVal[num4];
				uint v = (uint)num3 + (num5 & 0xF0F0F0F);
				WriteLE32(dst, num2, v);
				WriteLE32(dst, num2 + 4, (num5 >> 4) & 0xF0F0F0F);
				num2 += RiceLen[num4];
				if (num2 >= size)
				{
					break;
				}
				num3 = (int)(num5 >> 28);
			}
			if (num >= pEnd)
			{
				return false;
			}
			num4 = At(br.B, num++);
		}
		if (num2 > size)
		{
			int num6 = num2 - size;
			do
			{
				num4 &= num4 - 1;
			}
			while (--num6 != 0);
		}
		int bitPos = 0;
		if ((num4 & 1) == 0)
		{
			num--;
			int num7 = Bsf(num4);
			bitPos = 8 - num7;
		}
		br.P = num;
		br.BitPos = bitPos;
		return true;
	}

	private static bool DecodeGolombRiceBits(byte[] dst, int size, int bitcount, ref RefBitReader2 br)
	{
		if (bitcount == 0)
		{
			return true;
		}
		int num = 0;
		int num2 = br.P;
		int bitPos = br.BitPos;
		int num3 = bitPos + bitcount * size;
		if (num3 + 7 >> 3 > br.PEnd - num2)
		{
			return false;
		}
		br.P = num2 + (num3 >> 3);
		br.BitPos = num3 & 7;
		ulong v = ReadLE64(dst, size);
		if (bitcount == 1)
		{
			do
			{
				ulong num4 = (byte)(BSwap32(ReadLE32(br.B, num2)) >> 24 - bitPos);
				num2++;
				num4 = (num4 | (num4 << 28)) & 0xF0000000FL;
				num4 = (num4 | (num4 << 14)) & 0x3000300030003L;
				num4 = (num4 | (num4 << 7)) & 0x101010101010101L;
				ulong num5 = ReadLE64(dst, num);
				WriteLE64(dst, num, num5 * 2 + BSwap64(num4));
				num += 8;
			}
			while (num < size);
		}
		else if (bitcount == 2)
		{
			do
			{
				ulong num6 = (ushort)(BSwap32(ReadLE32(br.B, num2)) >> 16 - bitPos);
				num2 += 2;
				num6 = (num6 | (num6 << 24)) & 0xFF000000FFL;
				num6 = (num6 | (num6 << 12)) & 0xF000F000F000FL;
				num6 = (num6 | (num6 << 6)) & 0x303030303030303L;
				ulong num7 = ReadLE64(dst, num);
				WriteLE64(dst, num, num7 * 4 + BSwap64(num6));
				num += 8;
			}
			while (num < size);
		}
		else
		{
			do
			{
				ulong num8 = (BSwap32(ReadLE32(br.B, num2)) >> 8 - bitPos) & 0xFFFFFF;
				num2 += 3;
				num8 = (num8 | (num8 << 20)) & 0xFFF00000FFFL;
				num8 = (num8 | (num8 << 10)) & 0x3F003F003F003FL;
				num8 = (num8 | (num8 << 5)) & 0x707070707070707L;
				ulong num9 = ReadLE64(dst, num);
				WriteLE64(dst, num, num9 * 8 + BSwap64(num8));
				num += 8;
			}
			while (num < size);
		}
		WriteLE64(dst, size, v);
		return true;
	}

	private static bool ProcessLzRuns(int mode, byte[] dst, int dstStart, int dstSize, int offset, LzTable lzt)
	{
		int dstEnd = dstStart + dstSize;
		int dstPos = dstStart + ((offset == 0) ? 8 : 0);
		int dstStart2 = dstStart - offset;
		return mode switch
		{
			1 => ProcessLzRunsType1(lzt, dst, dstPos, dstEnd, dstStart2), 
			0 => ProcessLzRunsType0(lzt, dst, dstPos, dstEnd, dstStart2), 
			_ => false, 
		};
	}

	private static bool ProcessLzRunsType1(LzTable lzt, byte[] dst, int dstPos, int dstEnd, int dstStart)
	{
		byte[] cmdStream = lzt.CmdStream;
		int num = 0;
		int cmdStreamSize = lzt.CmdStreamSize;
		int[] lenStream = lzt.LenStream;
		int num2 = 0;
		int lenStreamSize = lzt.LenStreamSize;
		byte[] litStream = lzt.LitStream;
		int num3 = 0;
		int litStreamSize = lzt.LitStreamSize;
		int[] offsStream = lzt.OffsStream;
		int num4 = 0;
		int offsStreamSize = lzt.OffsStreamSize;
		Span<int> span = stackalloc int[7];
		span[3] = -8;
		span[4] = -8;
		span[5] = -8;
		while (num < cmdStreamSize)
		{
			byte b = cmdStream[num++];
			uint num5 = (uint)(b & 3);
			int num6 = b >>> 6;
			uint num7 = (uint)((b >>> 2) & 0xF);
			if (num5 == 3)
			{
				if (num2 >= lenStreamSize)
				{
					return false;
				}
				num5 = (uint)lenStream[num2++];
			}
			span[6] = ((num4 < offsStreamSize) ? offsStream[num4] : 0);
			if (num5 != 0)
			{
				if (num3 + num5 > (uint)litStreamSize || dstPos + num5 > (uint)dstEnd)
				{
					return false;
				}
				for (uint num8 = 0u; num8 < num5; num8++)
				{
					dst[dstPos + num8] = litStream[num3 + num8];
				}
				dstPos += (int)num5;
				num3 += (int)num5;
			}
			int num9 = span[num6 + 3];
			span[num6 + 3] = span[num6 + 2];
			span[num6 + 2] = span[num6 + 1];
			span[num6 + 1] = span[num6];
			span[3] = num9;
			num4 += ((num6 + 1) & 4) >> 2;
			if (dstPos + num9 < dstStart)
			{
				return false;
			}
			int num10 = dstPos + num9;
			uint num11;
			if (num7 != 15)
			{
				num11 = num7 + 2;
			}
			else
			{
				if (num2 >= lenStreamSize)
				{
					return false;
				}
				num11 = (uint)(14 + lenStream[num2++]);
			}
			if (dstPos + num11 > (uint)dstEnd)
			{
				return false;
			}
			ParseTrace?.Invoke((int)num5, (int)num11, -num9, num6);
			for (uint num12 = 0u; num12 < num11; num12++)
			{
				dst[dstPos + num12] = dst[num10 + num12];
			}
			dstPos += (int)num11;
		}
		if (num4 != offsStreamSize || num2 != lenStreamSize)
		{
			return false;
		}
		int num13 = dstEnd - dstPos;
		if (num13 != litStreamSize - num3)
		{
			return false;
		}
		ParseTrace?.Invoke(num13, 0, 0, -1);
		for (int i = 0; i < num13; i++)
		{
			dst[dstPos + i] = litStream[num3 + i];
		}
		return true;
	}

	private static bool ProcessLzRunsType0(LzTable lzt, byte[] dst, int dstPos, int dstEnd, int dstStart)
	{
		byte[] cmdStream = lzt.CmdStream;
		int num = 0;
		int cmdStreamSize = lzt.CmdStreamSize;
		int[] lenStream = lzt.LenStream;
		int num2 = 0;
		int lenStreamSize = lzt.LenStreamSize;
		byte[] litStream = lzt.LitStream;
		int num3 = 0;
		int litStreamSize = lzt.LitStreamSize;
		int[] offsStream = lzt.OffsStream;
		int num4 = 0;
		int offsStreamSize = lzt.OffsStreamSize;
		Span<int> span = stackalloc int[7];
		span[3] = -8;
		span[4] = -8;
		span[5] = -8;
		int num5 = -8;
		while (num < cmdStreamSize)
		{
			byte b = cmdStream[num++];
			uint num6 = (uint)(b & 3);
			int num7 = b >>> 6;
			uint num8 = (uint)((b >>> 2) & 0xF);
			if (num6 == 3)
			{
				if (num2 >= lenStreamSize)
				{
					return false;
				}
				num6 = (uint)lenStream[num2++];
			}
			span[6] = ((num4 < offsStreamSize) ? offsStream[num4] : 0);
			if (num6 != 0)
			{
				if (num3 + num6 > (uint)litStreamSize || dstPos + num6 > (uint)dstEnd)
				{
					return false;
				}
				for (uint num9 = 0u; num9 < num6; num9++)
				{
					dst[dstPos + num9] = (byte)(litStream[num3 + num9] + dst[dstPos + (int)num9 + num5]);
				}
				dstPos += (int)num6;
				num3 += (int)num6;
			}
			int num10 = span[num7 + 3];
			span[num7 + 3] = span[num7 + 2];
			span[num7 + 2] = span[num7 + 1];
			span[num7 + 1] = span[num7];
			span[3] = num10;
			num5 = num10;
			num4 += ((num7 + 1) & 4) >> 2;
			if (dstPos + num10 < dstStart)
			{
				return false;
			}
			int num11 = dstPos + num10;
			uint num12;
			if (num8 != 15)
			{
				num12 = num8 + 2;
			}
			else
			{
				if (num2 >= lenStreamSize)
				{
					return false;
				}
				num12 = (uint)(14 + lenStream[num2++]);
			}
			if (dstPos + num12 > (uint)dstEnd)
			{
				return false;
			}
			ParseTrace?.Invoke((int)num6, (int)num12, -num10, num7);
			for (uint num13 = 0u; num13 < num12; num13++)
			{
				dst[dstPos + num13] = dst[num11 + num13];
			}
			dstPos += (int)num12;
		}
		if (num4 != offsStreamSize || num2 != lenStreamSize)
		{
			return false;
		}
		int num14 = dstEnd - dstPos;
		if (num14 != litStreamSize - num3)
		{
			return false;
		}
		ParseTrace?.Invoke(num14, 0, 0, -1);
		for (int i = 0; i < num14; i++)
		{
			dst[dstPos + i] = (byte)(litStream[num3 + i] + dst[dstPos + i + num5]);
		}
		return true;
	}

	private static int Bsr(uint x)
	{
		if (x != 0)
		{
			return 31 - BitOperations.LeadingZeroCount(x);
		}
		return 0;
	}

	private static int Bsf(uint x)
	{
		if (x != 0)
		{
			return BitOperations.TrailingZeroCount(x);
		}
		return 0;
	}

	private static int Clz(uint x)
	{
		if (x != 0)
		{
			return BitOperations.LeadingZeroCount(x);
		}
		return 31;
	}

	private static byte At(byte[] b, int i)
	{
		if ((uint)i >= (uint)b.Length)
		{
			return 0;
		}
		return b[i];
	}

	private static uint ReadLE32(byte[] b, int off)
	{
		uint num = 0u;
		for (int i = 0; i < 4; i++)
		{
			if ((uint)(off + i) < (uint)b.Length)
			{
				num |= (uint)(b[off + i] << 8 * i);
			}
		}
		return num;
	}

	private static ulong ReadLE64(byte[] b, int off)
	{
		ulong num = 0uL;
		for (int i = 0; i < 8; i++)
		{
			if ((uint)(off + i) < (uint)b.Length)
			{
				num |= (ulong)b[off + i] << 8 * i;
			}
		}
		return num;
	}

	private static void WriteLE32(byte[] b, int off, uint v)
	{
		for (int i = 0; i < 4; i++)
		{
			if ((uint)(off + i) < (uint)b.Length)
			{
				b[off + i] = (byte)(v >> 8 * i);
			}
		}
	}

	private static void WriteLE64(byte[] b, int off, ulong v)
	{
		for (int i = 0; i < 8; i++)
		{
			if ((uint)(off + i) < (uint)b.Length)
			{
				b[off + i] = (byte)(v >> 8 * i);
			}
		}
	}

	private static uint BSwap32(uint v)
	{
		return BinaryPrimitives.ReverseEndianness(v);
	}

	private static ulong BSwap64(ulong v)
	{
		return BinaryPrimitives.ReverseEndianness(v);
	}

	private static uint[] BuildRiceVal()
	{
		return new uint[256]
		{
			2147483648u, 7u, 268435462u, 6u, 536870917u, 261u, 268435461u, 5u, 805306372u, 516u,
			268435716u, 260u, 536870916u, 65540u, 268435460u, 4u, 1073741827u, 771u, 268435971u, 515u,
			536871171u, 65795u, 268435715u, 259u, 805306371u, 131075u, 268500995u, 65539u, 536870915u, 16777219u,
			268435459u, 3u, 1342177282u, 1026u, 268436226u, 770u, 536871426u, 66050u, 268435970u, 514u,
			805306626u, 131330u, 268501250u, 65794u, 536871170u, 16777474u, 268435714u, 258u, 1073741826u, 196610u,
			268566530u, 131074u, 536936450u, 16842754u, 268500994u, 65538u, 805306370u, 33554434u, 285212674u, 16777218u,
			536870914u, 18u, 268435458u, 2u, 1610612737u, 1281u, 268436481u, 1025u, 536871681u, 66305u,
			268436225u, 769u, 805306881u, 131585u, 268501505u, 66049u, 536871425u, 16777729u, 268435969u, 513u,
			1073742081u, 196865u, 268566785u, 131329u, 536936705u, 16843009u, 268501249u, 65793u, 805306625u, 33554689u,
			285212929u, 16777473u, 536871169u, 273u, 268435713u, 257u, 1342177281u, 262145u, 268632065u, 196609u,
			537001985u, 16908289u, 268566529u, 131073u, 805371905u, 33619969u, 285278209u, 16842753u, 536936449u, 65553u,
			268500993u, 65537u, 1073741825u, 50331649u, 301989889u, 33554433u, 553648129u, 16777233u, 285212673u, 16777217u,
			805306369u, 33u, 268435473u, 17u, 536870913u, 4097u, 268435457u, 1u, 1879048192u, 1536u,
			268436736u, 1280u, 536871936u, 66560u, 268436480u, 1024u, 805307136u, 131840u, 268501760u, 66304u,
			536871680u, 16777984u, 268436224u, 768u, 1073742336u, 197120u, 268567040u, 131584u, 536936960u, 16843264u,
			268501504u, 66048u, 805306880u, 33554944u, 285213184u, 16777728u, 536871424u, 528u, 268435968u, 512u,
			1342177536u, 262400u, 268632320u, 196864u, 537002240u, 16908544u, 268566784u, 131328u, 805372160u, 33620224u,
			285278464u, 16843008u, 536936704u, 65808u, 268501248u, 65792u, 1073742080u, 50331904u, 301990144u, 33554688u,
			553648384u, 16777488u, 285212928u, 16777472u, 805306624u, 288u, 268435728u, 272u, 536871168u, 4352u,
			268435712u, 256u, 1610612736u, 327680u, 268697600u, 262144u, 537067520u, 16973824u, 268632064u, 196608u,
			805437440u, 33685504u, 285343744u, 16908288u, 537001984u, 131088u, 268566528u, 131072u, 1073807360u, 50397184u,
			302055424u, 33619968u, 553713664u, 16842768u, 285278208u, 16842752u, 805371904u, 65568u, 268501008u, 65552u,
			536936448u, 69632u, 268500992u, 65536u, 1342177280u, 67108864u, 318767104u, 50331648u, 570425344u, 33554448u,
			301989888u, 33554432u, 822083584u, 16777248u, 285212688u, 16777232u, 553648128u, 16781312u, 285212672u, 16777216u,
			1073741824u, 48u, 268435488u, 32u, 536870928u, 4112u, 268435472u, 16u, 805306368u, 8192u,
			268439552u, 4096u, 536870912u, 1048576u, 268435456u, 0u
		};
	}

	private static byte[] BuildRiceLen()
	{
		return new byte[256]
		{
			0, 1, 1, 2, 1, 2, 2, 3, 1, 2,
			2, 3, 2, 3, 3, 4, 1, 2, 2, 3,
			2, 3, 3, 4, 2, 3, 3, 4, 3, 4,
			4, 5, 1, 2, 2, 3, 2, 3, 3, 4,
			2, 3, 3, 4, 3, 4, 4, 5, 2, 3,
			3, 4, 3, 4, 4, 5, 3, 4, 4, 5,
			4, 5, 5, 6, 1, 2, 2, 3, 2, 3,
			3, 4, 2, 3, 3, 4, 3, 4, 4, 5,
			2, 3, 3, 4, 3, 4, 4, 5, 3, 4,
			4, 5, 4, 5, 5, 6, 2, 3, 3, 4,
			3, 4, 4, 5, 3, 4, 4, 5, 4, 5,
			5, 6, 3, 4, 4, 5, 4, 5, 5, 6,
			4, 5, 5, 6, 5, 6, 6, 7, 1, 2,
			2, 3, 2, 3, 3, 4, 2, 3, 3, 4,
			3, 4, 4, 5, 2, 3, 3, 4, 3, 4,
			4, 5, 3, 4, 4, 5, 4, 5, 5, 6,
			2, 3, 3, 4, 3, 4, 4, 5, 3, 4,
			4, 5, 4, 5, 5, 6, 3, 4, 4, 5,
			4, 5, 5, 6, 4, 5, 5, 6, 5, 6,
			6, 7, 2, 3, 3, 4, 3, 4, 4, 5,
			3, 4, 4, 5, 4, 5, 5, 6, 3, 4,
			4, 5, 4, 5, 5, 6, 4, 5, 5, 6,
			5, 6, 6, 7, 3, 4, 4, 5, 4, 5,
			5, 6, 4, 5, 5, 6, 5, 6, 6, 7,
			4, 5, 5, 6, 5, 6, 6, 7, 5, 6,
			6, 7, 6, 7, 7, 8
		};
	}
}
