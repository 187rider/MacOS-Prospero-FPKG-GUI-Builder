using System;
using System.Collections.Generic;

namespace LibProsperoPkg.PFS.Compression.Oodle;

/// <summary>
/// Encodes a byte array into a Kraken entropy "array" (Huffman, chunk-type 2) that
/// <see cref="T:LibProsperoPkg.PFS.Compression.Oodle.KrakenDecoder" /> decodes byte-exact. Returns <c>null</c> when an entropy array
/// would not be smaller than the raw form (caller should then store the array raw, type 0).
/// </summary>
internal static class KrakenHuffmanArrayEncoder
{
	private sealed class MsbBitWriter
	{
		private readonly List<byte> _bytes = new List<byte>();

		private uint _acc;

		private int _nbits;

		public int BitLength => _bytes.Count * 8 + _nbits;

		public void Write(uint v, int len)
		{
			for (int num = len - 1; num >= 0; num--)
			{
				_acc = (_acc << 1) | ((v >> num) & 1);
				if (++_nbits == 8)
				{
					_bytes.Add((byte)_acc);
					_acc = 0u;
					_nbits = 0;
				}
			}
		}

		public byte[] ToBytesPadded()
		{
			if (_nbits > 0)
			{
				_bytes.Add((byte)(_acc << 8 - _nbits));
				_acc = 0u;
				_nbits = 0;
			}
			return _bytes.ToArray();
		}
	}

	private sealed class LsbBitWriter
	{
		private readonly List<byte> _bytes = new List<byte>();

		private uint _acc;

		private int _nbits;

		public void Write(uint v, int len)
		{
			_acc |= (v & (uint)((1 << len) - 1)) << _nbits;
			_nbits += len;
			while (_nbits >= 8)
			{
				_bytes.Add((byte)_acc);
				_acc >>= 8;
				_nbits -= 8;
			}
		}

		public byte[] ToBytesPadded()
		{
			if (_nbits > 0)
			{
				_bytes.Add((byte)_acc);
				_acc = 0u;
				_nbits = 0;
			}
			return _bytes.ToArray();
		}
	}

	private const int MaxCodeLen = 11;

	private const int PackageBit = 1073741824;

	/// <summary>
	/// Attempts to Huffman-encode <paramref name="data" /> as a single entropy array. Returns the
	/// on-disk array bytes (header + payload) or <c>null</c> if not beneficial / not representable.
	/// </summary>
	public static byte[]? TryEncode(ReadOnlySpan<byte> data)
	{
		int length = data.Length;
		if (length < 2 || length > 262143)
		{
			return null;
		}
		Span<int> span = stackalloc int[256];
		for (int i = 0; i < length; i++)
		{
			span[data[i]]++;
		}
		int num = 0;
		for (int j = 0; j < 256; j++)
		{
			if (span[j] != 0)
			{
				num++;
			}
		}
		if (num == 1)
		{
			int num2 = 0;
			for (int k = 0; k < 256; k++)
			{
				if (span[k] != 0)
				{
					num2 = k;
					break;
				}
			}
			int index = ((num2 == 0) ? 1 : 0);
			span[index] = 1;
			num = 2;
		}
		byte[] array = BuildCodeLengths(span);
		if (array.Length == 0)
		{
			return null;
		}
		BuildCanonicalCodes(array, out ushort[] codeOfSym);
		ushort[] array2 = new ushort[256];
		for (int l = 0; l < 256; l++)
		{
			if (array[l] != 0)
			{
				array2[l] = (ushort)ReverseBits(codeOfSym[l], array[l]);
			}
		}
		byte[] array3 = ((num < 5) ? BuildSimpleCodeLengthHeader(array) : BuildComplexCodeLengthHeader(array));
		if (array3 == null)
		{
			return null;
		}
		LsbBitWriter lsbBitWriter = new LsbBitWriter();
		LsbBitWriter lsbBitWriter2 = new LsbBitWriter();
		LsbBitWriter lsbBitWriter3 = new LsbBitWriter();
		for (int m = 0; m < length; m++)
		{
			byte b = data[m];
			int len = array[b];
			uint v = array2[b];
			switch (m % 3)
			{
			case 0:
				lsbBitWriter.Write(v, len);
				break;
			case 1:
				lsbBitWriter2.Write(v, len);
				break;
			default:
				lsbBitWriter3.Write(v, len);
				break;
			}
		}
		byte[] array4 = lsbBitWriter.ToBytesPadded();
		byte[] array5 = lsbBitWriter2.ToBytesPadded();
		byte[] array6 = lsbBitWriter3.ToBytesPadded();
		int num3 = array4.Length;
		if (num3 > 65535)
		{
			return null;
		}
		int num4 = array3.Length + 2 + num3 + array6.Length + array5.Length;
		if (num4 >= length)
		{
			return null;
		}
		byte[] array7 = BuildEntropyArray(2, num4, length, out var bodyOff);
		int num5 = bodyOff;
		Array.Copy(array3, 0, array7, num5, array3.Length);
		num5 += array3.Length;
		array7[num5++] = (byte)(num3 & 0xFF);
		array7[num5++] = (byte)((num3 >> 8) & 0xFF);
		Array.Copy(array4, 0, array7, num5, num3);
		num5 += num3;
		Array.Copy(array6, 0, array7, num5, array6.Length);
		num5 += array6.Length;
		for (int n = 0; n < array5.Length; n++)
		{
			array7[num5 + n] = array5[array5.Length - 1 - n];
		}
		num5 += array5.Length;
		return array7;
	}

	private static byte[] BuildEntropyArray(int chunkType, int srcSize, int dstSize, out int bodyOff)
	{
		byte[] array = new byte[5 + srcSize];
		int num = dstSize - 1;
		array[0] = (byte)((chunkType << 4) | ((num >> 14) & 0xF));
		uint num2 = (uint)(srcSize | ((num & 0x3FFF) << 18));
		array[1] = (byte)(num2 >> 24);
		array[2] = (byte)(num2 >> 16);
		array[3] = (byte)(num2 >> 8);
		array[4] = (byte)num2;
		bodyOff = 5;
		return array;
	}

	private static byte[]? BuildSimpleCodeLengthHeader(byte[] lenOfSym)
	{
		int num = 0;
		int num2 = 0;
		for (int i = 0; i < 256; i++)
		{
			if (lenOfSym[i] != 0)
			{
				num++;
				if (lenOfSym[i] > num2)
				{
					num2 = lenOfSym[i];
				}
			}
		}
		if (num < 2 || num >= 256)
		{
			return null;
		}
		int num3 = BitWidth(num2 - 1);
		if (num3 > 4)
		{
			return null;
		}
		MsbBitWriter msbBitWriter = new MsbBitWriter();
		msbBitWriter.Write(0u, 1);
		msbBitWriter.Write(0u, 1);
		msbBitWriter.Write((uint)num, 8);
		msbBitWriter.Write((uint)num3, 3);
		for (int j = 0; j < 256; j++)
		{
			if (lenOfSym[j] != 0)
			{
				msbBitWriter.Write((uint)j, 8);
				if (num3 > 0)
				{
					msbBitWriter.Write((uint)(lenOfSym[j] - 1), num3);
				}
			}
		}
		return msbBitWriter.ToBytesPadded();
	}

	private static byte[]? BuildComplexCodeLengthHeader(byte[] lenOfSym)
	{
		int num = -1;
		for (int i = 0; i < 256; i++)
		{
			if (lenOfSym[i] != 0)
			{
				num = i;
				break;
			}
		}
		if (num < 0)
		{
			return null;
		}
		byte[] result = null;
		int num2 = int.MaxValue;
		for (int j = 0; j <= 3; j++)
		{
			MsbBitWriter msbBitWriter = BuildComplexHeaderForFb(lenOfSym, j, num == 0);
			if (msbBitWriter != null)
			{
				int bitLength = msbBitWriter.BitLength;
				if (bitLength < num2)
				{
					num2 = bitLength;
					result = msbBitWriter.ToBytesPadded();
				}
			}
		}
		return result;
	}

	private static MsbBitWriter? BuildComplexHeaderForFb(byte[] lenOfSym, int fb, bool skip)
	{
		int num = 20 >>> fb;
		MsbBitWriter msbBitWriter = new MsbBitWriter();
		msbBitWriter.Write(0u, 1);
		msbBitWriter.Write(1u, 1);
		msbBitWriter.Write((uint)fb, 2);
		msbBitWriter.Write(skip ? 1u : 0u, 1);
		int num2 = 32;
		int num3 = 0;
		bool flag = true;
		int num4 = 0;
		while (num4 < 256)
		{
			if (lenOfSym[num4] == 0)
			{
				num4++;
				continue;
			}
			int num5 = num4;
			int i;
			for (i = num4; i + 1 < 256 && lenOfSym[i + 1] != 0; i++)
			{
			}
			int num6 = i - num5 + 1;
			if (!(flag & skip))
			{
				WriteGamma(msbBitWriter, num5 - num3 + 1);
			}
			num3 = num5;
			flag = false;
			WriteGamma(msbBitWriter, num6 + 1);
			for (int j = num5; j <= i; j++)
			{
				int num7 = num2 + 2 >> 2;
				int num8 = lenOfSym[j] - num7;
				uint num9 = (uint)((num8 << 1) ^ (num8 >> 31));
				int num10 = (int)(num9 >> fb);
				if (num10 > num)
				{
					return null;
				}
				for (int k = 0; k < num10; k++)
				{
					msbBitWriter.Write(0u, 1);
				}
				msbBitWriter.Write(1u, 1);
				if (fb > 0)
				{
					msbBitWriter.Write(num9 & (uint)((1 << fb) - 1), fb);
				}
				num2 = lenOfSym[j] + (3 * num2 + 2 >> 2);
			}
			num3 = i + 1;
			num4 = i + 1;
		}
		if (num3 < 256)
		{
			WriteGamma(msbBitWriter, 256 - num3 + 1);
		}
		return msbBitWriter;
	}

	internal static int[] DebugComplexHeaderFbBits(byte[] lenOfSym)
	{
		int num = -1;
		for (int i = 0; i < 256; i++)
		{
			if (lenOfSym[i] != 0)
			{
				num = i;
				break;
			}
		}
		int[] array = new int[4] { -1, -1, -1, -1 };
		if (num < 0)
		{
			return array;
		}
		for (int j = 0; j <= 3; j++)
		{
			array[j] = BuildComplexHeaderForFb(lenOfSym, j, num == 0)?.BitLength ?? (-1);
		}
		return array;
	}

	internal static (byte[]? simple, byte[]? complex, int chosen) DebugCodeLengthHeaders(byte[] lenOfSym)
	{
		byte[] array = BuildSimpleCodeLengthHeader(lenOfSym);
		byte[] array2 = BuildComplexCodeLengthHeader(lenOfSym);
		int item = ((array == null || (array2 != null && array2.Length <= array.Length)) ? 1 : 0);
		return (simple: array, complex: array2, chosen: item);
	}

	private static void WriteGamma(MsbBitWriter w, int f)
	{
		int num = 0;
		for (int num2 = f; num2 > 0; num2 >>= 1)
		{
			num++;
		}
		w.Write((uint)f, 2 * num - 2);
	}

	private static byte[] BuildCodeLengths(ReadOnlySpan<int> freq)
	{
		byte[] array = new byte[256];
		List<int> list = new List<int>();
		long num = 0L;
		for (int i = 0; i < 256; i++)
		{
			int num2 = freq[i];
			if (num2 != 0)
			{
				list.Add(i);
				num += num2;
			}
		}
		int count = list.Count;
		switch (count)
		{
		case 0:
			return Array.Empty<byte>();
		case 1:
			array[list[0]] = 1;
			return array;
		default:
		{
			long[] array2 = new long[256];
			ScaleCounts(freq, num, array2);
			int[] array3 = new int[count + 1];
			int[] array4 = new int[count + 1];
			for (int j = 0; j < count; j++)
			{
				array3[j] = list[j];
				array4[j] = (int)array2[list[j]];
			}
			if (count <= 32)
			{
				SortByCountSmall(array3, array4, count);
			}
			else
			{
				StableSortByCount(array3, array4, count);
			}
			int[] array5 = new int[count];
			Array.Copy(array4, array5, count);
			InPlaceHuffman(array5, count);
			if (array5[0] <= 11)
			{
				for (int k = 0; k < count; k++)
				{
					array[array3[k]] = (byte)array5[k];
				}
				if (!ValidateKraft(array, list, count))
				{
					return Array.Empty<byte>();
				}
				return array;
			}
			int num3 = 11;
			array4[count] = int.MaxValue;
			int num4 = 2 * count - 2;
			int[][] array6 = new int[num3 + 1][];
			long[][] array7 = new long[num3 + 1][];
			int[] array8 = new int[num3 + 1];
			for (int l = 1; l <= num3; l++)
			{
				array6[l] = new int[num4];
				array7[l] = new long[num4];
			}
			for (int m = 1; m <= num3; m++)
			{
				int num5 = 0;
				int num6 = 0;
				int n = 0;
				int num7 = ((m >= 2) ? array8[m - 1] : 0);
				long[] array9 = ((m >= 2) ? array7[m - 1] : null);
				for (; n < num4; n++)
				{
					long num8 = array4[num5];
					if (num6 + 1 < num7 && array9[num6] + array9[num6 + 1] <= num8)
					{
						array7[m][n] = array9[num6] + array9[num6 + 1];
						array6[m][n] = num6 | 0x40000000;
						num6 += 2;
						continue;
					}
					if (num5 >= count)
					{
						break;
					}
					array7[m][n] = num8;
					array6[m][n] = array3[num5];
					num5++;
				}
				array8[m] = n;
			}
			int num9 = array8[num3];
			for (int num10 = num3; num10 >= 1; num10--)
			{
				int num11 = 0;
				int[] array10 = array6[num10];
				for (int num12 = 0; num12 < num9; num12++)
				{
					int num13 = array10[num12];
					if ((num13 & 0x40000000) == 0)
					{
						array[num13]++;
					}
					else
					{
						num11 = (num13 & -1073741825) + 2;
					}
				}
				num9 = num11;
			}
			if (!ValidateKraft(array, list, count))
			{
				return Array.Empty<byte>();
			}
			return array;
		}
		}
	}

	private static bool ValidateKraft(byte[] result, List<int> present, int n)
	{
		long num = 0L;
		for (int i = 0; i < n; i++)
		{
			int num2 = result[present[i]];
			if (num2 < 1 || num2 > 11)
			{
				return false;
			}
			num += 1L << 11 - num2;
		}
		return num == 2048;
	}

	private static void ScaleCounts(ReadOnlySpan<int> freq, long total, long[] dest)
	{
		long num = 0L;
		int num2 = 0;
		for (int i = 0; i < 256; i++)
		{
			long num3 = freq[i];
			if (num < num3)
			{
				num = num3;
				num2 = i;
			}
		}
		if (num <= 65535 && total <= 65535)
		{
			for (int j = 0; j < 256; j++)
			{
				dest[j] = freq[j];
			}
			return;
		}
		float num4 = 65535f / (float)total;
		float num5 = 65535f / (float)num;
		float num6 = ((num4 <= num5) ? num4 : num5);
		long num7 = 0L;
		for (int k = 0; k < 256; k++)
		{
			if (freq[k] == 0)
			{
				dest[k] = 0L;
				continue;
			}
			uint num8 = (uint)((float)freq[k] * num6 + 0.5f);
			uint num9 = (((long)num8 < 65535L) ? num8 : 65535u);
			num7 += (dest[k] = ((num9 < 2) ? 1 : num9));
		}
		if (65535 < num7)
		{
			dest[num2] += 65535 - num7;
		}
	}

	private static void StableSortByCount(int[] sym, int[] cnt, int n)
	{
		int[] array = new int[n];
		for (int i = 0; i < n; i++)
		{
			array[i] = i;
		}
		Array.Sort(array, (int x, int y) => (cnt[x] == cnt[y]) ? x.CompareTo(y) : cnt[x].CompareTo(cnt[y]));
		int[] array2 = new int[n];
		int[] array3 = new int[n];
		for (int num = 0; num < n; num++)
		{
			array2[num] = sym[array[num]];
			array3[num] = cnt[array[num]];
		}
		Array.Copy(array2, sym, n);
		Array.Copy(array3, cnt, n);
	}

	private static void SortByCountSmall(int[] sym, int[] cnt, int n)
	{
		if (n < 2)
		{
			return;
		}
		int num = 0;
		uint num2 = (uint)n;
		do
		{
			num2 = (num2 >> 1) + (num2 >> 2);
			num++;
		}
		while (num2 != 0);
		Stack<(int, int)> stack = new Stack<(int, int)>();
		int num3 = 0;
		int num4 = n - 1;
		int num5 = n;
		while (true)
		{
			if (num5 > 1)
			{
				if (num5 == 2)
				{
					if (cnt[num4] < cnt[num3])
					{
						Swap(num3, num4);
					}
				}
				else
				{
					int num6 = num3 + (num5 >> 1);
					Median(num3, num6, num4);
					if (num5 < 5)
					{
						if (num5 == 4)
						{
							Insert(num3, num6, num4);
						}
					}
					else
					{
						if (stack.Count != num)
						{
							int num7 = num3;
							int num8 = num4;
							Swap(num6, num3);
							while (true)
							{
								num8--;
								if (cnt[num3] >= cnt[num8])
								{
									if (num8 <= num7)
									{
										break;
									}
									do
									{
										num7++;
									}
									while (cnt[num7] < cnt[num3]);
									if (num8 <= num7)
									{
										num7--;
										break;
									}
									Swap(num7, num8);
								}
							}
							Swap(num3, num7);
							int num9 = num7;
							int num10 = num7;
							do
							{
								num10++;
							}
							while (num10 < num4 && cnt[num9] >= cnt[num10]);
							int num11 = num7;
							do
							{
								num11--;
							}
							while (num3 < num11 && cnt[num11] >= cnt[num9]);
							int num12 = num11 - num3 + 1;
							int num13 = num4 - num10 + 1;
							if (num13 < num12)
							{
								stack.Push((num10, num4));
								num4 = num11;
								num5 = num12;
							}
							else
							{
								stack.Push((num3, num11));
								num3 = num10;
								num5 = num13;
							}
							continue;
						}
						HeapSort(num3, num5);
					}
				}
			}
			if (stack.Count != 0)
			{
				(int, int) tuple = stack.Pop();
				num3 = tuple.Item1;
				num4 = tuple.Item2;
				num5 = num4 - num3 + 1;
				continue;
			}
			break;
		}
		void HeapSort(int lo, int count)
		{
			for (int num14 = count / 2 - 1; num14 >= 0; num14--)
			{
				SiftDown(lo, num14, count);
			}
			for (int num15 = count - 1; num15 >= 1; num15--)
			{
				Swap(lo, lo + num15);
				SiftDown(lo, 0, num15);
			}
		}
		void Insert(int f, int m, int l)
		{
			int num14 = f + 1;
			if (cnt[m] < cnt[num14])
			{
				if (cnt[num14] < cnt[l])
				{
					Swap(num14, m);
				}
				else
				{
					Rot(m, num14, l);
				}
			}
			else if (cnt[num14] < cnt[f])
			{
				Swap(f, num14);
			}
		}
		void Median(int a, int b, int c)
		{
			if (cnt[b] < cnt[a])
			{
				if (cnt[b] < cnt[c])
				{
					if (cnt[a] < cnt[c])
					{
						Swap(a, b);
					}
					else
					{
						Rot(b, a, c);
					}
				}
				else
				{
					Swap(a, c);
				}
			}
			else if (cnt[c] < cnt[b])
			{
				if (cnt[a] < cnt[c])
				{
					Swap(b, c);
				}
				else
				{
					Rot(a, b, c);
				}
			}
		}
		void Rot(int x, int y, int z)
		{
			Swap(x, y);
			Swap(x, z);
		}
		void SiftDown(int lo, int nodeRel, int heapCount)
		{
			while (true)
			{
				int num14 = nodeRel * 2 + 1;
				if (num14 >= heapCount)
				{
					break;
				}
				int num15 = lo + num14;
				if (num14 + 1 < heapCount && cnt[num15] < cnt[num15 + 1])
				{
					num14++;
					num15++;
				}
				if (cnt[lo + nodeRel] >= cnt[num15])
				{
					break;
				}
				Swap(lo + nodeRel, num15);
				nodeRel = num14;
			}
		}
		void Swap(int i, int j)
		{
			ref int reference = ref sym[i];
			ref int reference2 = ref sym[j];
			int num14 = sym[j];
			int num15 = sym[i];
			reference = num14;
			reference2 = num15;
			reference = ref cnt[i];
			ref int reference3 = ref cnt[j];
			num15 = cnt[j];
			num14 = cnt[i];
			reference = num15;
			reference3 = num14;
		}
	}

	private static void InPlaceHuffman(int[] cnt, int n)
	{
		cnt[0] += cnt[1];
		int num = 0;
		int num2 = 2;
		for (int i = 1; i < n - 1; i++)
		{
			if (num2 < n && cnt[num2] <= cnt[num])
			{
				cnt[i] = cnt[num2];
				num2++;
			}
			else
			{
				cnt[i] = cnt[num];
				cnt[num] = i;
				num++;
			}
			if (num2 < n && (i <= num || cnt[num2] <= cnt[num]))
			{
				cnt[i] += cnt[num2];
				num2++;
			}
			else
			{
				cnt[i] += cnt[num];
				cnt[num] = i;
				num++;
			}
		}
		cnt[n - 2] = 0;
		for (int num3 = n - 3; num3 >= 0; num3--)
		{
			cnt[num3] = cnt[cnt[num3]] + 1;
		}
		int num4 = 0;
		int num5 = n - 2;
		int num6 = n - 1;
		int num7 = 1;
		while (num7 > 0)
		{
			int num8 = 0;
			while (num5 >= 0 && cnt[num5] == num4)
			{
				num8++;
				num5--;
			}
			while (num8 < num7)
			{
				cnt[num6] = num4;
				num6--;
				num7--;
			}
			num4++;
			num7 = num8 << 1;
		}
	}

	private static void BuildCanonicalCodes(byte[] lenOfSym, out ushort[] codeOfSym)
	{
		codeOfSym = new ushort[256];
		uint num = 0u;
		for (int i = 1; i <= 11; i++)
		{
			int num2 = 1 << 11 - i;
			for (int j = 0; j < 256; j++)
			{
				if (lenOfSym[j] == i)
				{
					codeOfSym[j] = (ushort)(num >> 11 - i);
					num += (uint)num2;
				}
			}
		}
	}

	private static int ReverseBits(int v, int len)
	{
		int num = 0;
		for (int i = 0; i < len; i++)
		{
			num = (num << 1) | (v & 1);
			v >>= 1;
		}
		return num;
	}

	private static int BitWidth(int v)
	{
		int num = 0;
		while (v > 0)
		{
			num++;
			v >>= 1;
		}
		return num;
	}
}
