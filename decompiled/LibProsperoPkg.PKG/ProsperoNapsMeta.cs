using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LibProsperoPkg.PFS;
using LibProsperoPkg.PFS.Compression;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PKG;

public static class ProsperoNapsMeta
{
	private readonly record struct Meta18Block(ulong Co, uint Cs, uint Ps, uint C0, uint C1, uint Flag, bool IsHole, uint OwnerFlag, ulong Tail, long OnDiskOffset, uint OnDiskLen, ReadOnlyMemory<byte> Plaintext, long LogicalOffset, byte[]? Sha3Digest = null, ulong? Ihsh = null, ulong? Rhsh = null);

	public const int Meta300Length = 48;

	public const ulong Meta300KindId = 1001uL;

	public const ulong PfsBlockSize = 65536uL;

	public const ulong Meta300TrailingExtentSize = 131072uL;

	private const int Meta18BlockSize = 65536;

	private static readonly byte[] Meta18DataKey = new byte[16]
	{
		2, 45, 202, 246, 209, 17, 229, 143, 37, 147,
		110, 245, 70, 147, 69, 171
	};

	private static readonly byte[] Meta18TweakKey = new byte[16]
	{
		173, 172, 22, 55, 96, 218, 81, 70, 152, 194,
		69, 171, 76, 156, 66, 108
	};

	private static readonly byte[] Meta18Tweak = new byte[16]
	{
		60, 186, 16, 125, 0, 0, 0, 0, 0, 0,
		0, 0, 0, 0, 0, 0
	};

	private const long Meta18UBlock = 262144L;

	private const string PfsMetadataFileName = "*PFSmetadata";

	public static ReadOnlySpan<int> Meta300Ids => new int[4] { 300, 301, 302, 308 };

	public static byte[] BuildMeta300(ulong innerImageDataRegionSize)
	{
		byte[] array = new byte[48];
		Span<byte> span = array;
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(16, 8), innerImageDataRegionSize);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(24, 8), 1001uL);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(32, 8), innerImageDataRegionSize);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(40, 8), 131072uL);
		return array;
	}

	public static byte[] BuildMeta300FromInnerImageSize(ulong innerImageSize)
	{
		if (innerImageSize < 131072)
		{
			throw new ArgumentOutOfRangeException("innerImageSize", $"inner-image size 0x{innerImageSize:X} is smaller than the 0x{131072uL:X} trailing extent");
		}
		return BuildMeta300(innerImageSize - 131072);
	}

	public static byte[] BuildMeta18(ulong innerImageSize, byte[] mountImage, IReadOnlyList<(string Path, long Size)> contentFiles, ProsperoPs5InnerImageResult? inner = null, IProsperoNapsIntegrityProvider? integrityProvider = null, byte[]? pfsImageKey = null, byte[]? pfsImageSeed = null)
	{
		ArgumentNullException.ThrowIfNull(mountImage, "mountImage");
		ArgumentNullException.ThrowIfNull(contentFiles, "contentFiles");
		return BuildMeta18Core(innerImageSize, mountImage.LongLength, mountImage, contentFiles, inner, integrityProvider, pfsImageKey, pfsImageSeed);
	}

	public static byte[] BuildMeta18(ulong innerImageSize, long mountImageSize, IReadOnlyList<(string Path, long Size)> contentFiles, ProsperoPs5InnerImageResult inner, IProsperoNapsIntegrityProvider? integrityProvider = null, byte[]? pfsImageKey = null, byte[]? pfsImageSeed = null, Action<string>? log = null, int maxHashingThreads = 2, Stream? mountImageStream = null, byte[]? outerImageDigests = null)
	{
		ArgumentNullException.ThrowIfNull(contentFiles, "contentFiles");
		ArgumentNullException.ThrowIfNull(inner, "inner");
		return BuildMeta18Core(innerImageSize, mountImageSize, Array.Empty<byte>(), contentFiles, inner, integrityProvider, pfsImageKey, pfsImageSeed, log, maxHashingThreads, mountImageStream, outerImageDigests);
	}

	private static byte[] BuildMeta18Core(ulong innerImageSize, long mountImageSize, byte[] mountImage, IReadOnlyList<(string Path, long Size)> contentFiles, ProsperoPs5InnerImageResult? inner, IProsperoNapsIntegrityProvider? integrityProvider, byte[]? pfsImageKey, byte[]? pfsImageSeed, Action<string>? log = null, int maxHashingThreads = 2, Stream? mountImageStream = null, byte[]? outerImageDigests = null)
	{
		if (innerImageSize < 65536 || mountImageSize < 65536)
		{
			return Array.Empty<byte>();
		}
		if (mountImageSize < 0 || mountImageSize % 65536 != 0L)
		{
			throw new ArgumentOutOfRangeException("mountImageSize", "Mount-image size must be a non-negative multiple of 64 KiB.");
		}
		uint num = (uint)(innerImageSize / 65536);
		int num2 = (int)(mountImageSize / 65536);
		List<Meta18Block> list = ((inner != null) ? BuildInnerBlocks(inner) : null);
		int num3 = list?.FindIndex((Meta18Block b) => b.Tail == 1001) ?? (-1);
		ProsperoNapsIntegrityContext prosperoNapsIntegrityContext = BuildIntegrityContext(innerImageSize, mountImage, inner, list, pfsImageKey, pfsImageSeed);
		byte[] array = BuildIhshPrefixes(prosperoNapsIntegrityContext);
		byte[] array2 = BuildRollingHashes(prosperoNapsIntegrityContext);
		byte[] array3 = BuildOuterBlockCheckCodes(prosperoNapsIntegrityContext);
		byte[] array4 = integrityProvider?.BuildIhshPrefixes(prosperoNapsIntegrityContext);
		byte[] array5 = integrityProvider?.BuildRollingHashes(prosperoNapsIntegrityContext);
		byte[] array6 = integrityProvider?.BuildOuterBlockCheckCodes(prosperoNapsIntegrityContext);
		ValidateProtectedTable(array4, prosperoNapsIntegrityContext.MappingBlocks.Count * 8, "ihsh prefix");
		ValidateProtectedTable(array5, prosperoNapsIntegrityContext.MappingBlocks.Count * 8, "rhsh");
		ValidateProtectedTable(array6, prosperoNapsIntegrityContext.PhysicalInnerBlockCount * 4, "obcc");
		array = array4 ?? array;
		array2 = array5 ?? array2;
		array3 = array6 ?? array3;
		List<byte> list2 = new List<byte>(4096);
		Span<byte> span = stackalloc byte[24];
		WriteU32(span, 0, 1u);
		WriteU32(span, 4, 80u);
		WriteU32(span, 8, num);
		WriteU32(span, 12, (uint)(innerImageSize - 65536));
		WriteU32(span, 16, 1u);
		WriteU32(span, 20, 65536u);
		WriteRecord(list2, "phdr", 1, span);
		byte[] array7 = new byte[contentFiles.Count * 24];
		uint num4 = 0u;
		int i = 0;
		while (i < contentFiles.Count)
		{
			Span<byte> dst = array7.AsSpan(i * 24, 24);
			int num5;
			int num6;
			if (list != null)
			{
				num5 = ((contentFiles[i].Path == "*PFSmetadata") ? 1 : 0);
				if (num5 != 0)
				{
					num6 = 3;
					goto IL_0280;
				}
			}
			else
			{
				num5 = 0;
			}
			num6 = (int)((inner == null || i >= inner.Placements.Count) ? 1 : checked((uint)PlacementBlockCount(inner.Placements[i])));
			goto IL_0280;
			IL_0280:
			uint num7 = (uint)num6;
			checked
			{
				uint num8 = ((num5 == 0 && inner != null && i < inner.Placements.Count) ? ((uint)inner.SparseAfidHoles.Count((ProsperoPs5SparseAfidHole hole) => hole.Afid < inner.Placements[i].Afid)) : 0u);
				uint value = ((num5 != 0 && num3 >= 0) ? unchecked((uint)num3) : (num4 + num8));
				uint value2 = ((num5 != 0) ? 1001u : 0u);
				uint value3 = ((num5 == 0 && inner != null && i < inner.Placements.Count && IsExecutableAfid(inner, (int)inner.Placements[i].Afid)) ? 1u : 0u);
				ulong value4 = unchecked((ulong)((num5 != 0 && inner != null) ? inner.MetadataPlaintext.Length : contentFiles[i].Size));
				BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(0, 8), value4);
				WriteU32(dst, 8, value);
				WriteU32(dst, 12, num7);
				WriteU32(dst, 16, value2);
				WriteU32(dst, 20, value3);
				if (num5 == 0)
				{
					num4 += num7;
				}
			}
			i++;
		}
		WriteRecord(list2, "file", 2, array7);
		if (list != null)
		{
			byte[] array8 = new byte[contentFiles.Count * 56];
			for (int num9 = 0; num9 < contentFiles.Count; num9++)
			{
				Span<byte> span2 = array8.AsSpan(num9 * 56, 56);
				bool flag = contentFiles[num9].Path == "*PFSmetadata";
				ulong value5 = (ulong)((flag && inner != null) ? inner.MetadataPlaintext.Length : contentFiles[num9].Size);
				ulong value6 = (ulong)((!flag) ? ((int)((inner == null || num9 >= inner.Placements.Count) ? 1u : ((uint)PlacementBlockCount(inner.Placements[num9])))) : 3);
				BinaryPrimitives.WriteUInt64LittleEndian(span2.Slice(0, 8), 1uL);
				BinaryPrimitives.WriteUInt64LittleEndian(span2.Slice(8, 8), value5);
				BinaryPrimitives.WriteUInt64LittleEndian(span2.Slice(16, 8), value5);
				BinaryPrimitives.WriteUInt64LittleEndian(span2.Slice(24, 8), value6);
			}
			WriteRecord(list2, "ftyp", 1, array8);
		}
		if (list != null)
		{
			byte[] array9 = new byte[list.Count];
			for (int num10 = 0; num10 < list.Count; num10++)
			{
				array9[num10] = (byte)((list[num10].OwnerFlag == 1) ? 1u : 15u);
			}
			WriteRecord(list2, "ibcl", 1, array9);
		}
		else
		{
			byte[] array10 = new byte[num2];
			Array.Fill(array10, (byte)15);
			WriteRecord(list2, "ibcl", 1, array10);
		}
		if (list != null)
		{
			WriteRecord(list2, "ibcl", 1, new byte[list.Count]);
		}
		if (list != null)
		{
			byte[] array11 = new byte[list.Count * 40];
			for (int num11 = 0; num11 < list.Count; num11++)
			{
				Meta18Block meta18Block = list[num11];
				Span<byte> dst2 = array11.AsSpan(num11 * 40, 40);
				BinaryPrimitives.WriteUInt64LittleEndian(dst2.Slice(0, 8), meta18Block.Co);
				WriteU32(dst2, 8, meta18Block.Cs);
				WriteU32(dst2, 12, meta18Block.Ps);
				WriteU32(dst2, 16, meta18Block.C0);
				WriteU32(dst2, 20, meta18Block.C1);
				WriteU32(dst2, 24, (uint)(meta18Block.Co >> 16));
				WriteU32(dst2, 32, 1u);
				WriteU32(dst2, 36, meta18Block.Flag);
			}
			WriteRecord(list2, "i2ob", 1, array11);
		}
		else
		{
			byte[] array12 = new byte[num2 * 40];
			for (int num12 = 0; num12 < num2; num12++)
			{
				Span<byte> dst3 = array12.AsSpan(num12 * 40, 40);
				BinaryPrimitives.WriteUInt64LittleEndian(dst3.Slice(0, 8), (ulong)num12 * 65536uL);
				WriteU32(dst3, 8, 65536u);
				WriteU32(dst3, 12, 65536u);
				WriteU32(dst3, 16, 65536u);
				WriteU32(dst3, 36, 1074331648u);
			}
			WriteRecord(list2, "i2ob", 1, array12);
		}
		if (list != null)
		{
			byte[] array13 = new byte[list.Count * 16];
			for (int num13 = 0; num13 < list.Count; num13++)
			{
				Span<byte> span3 = array13.AsSpan(num13 * 16, 16);
				BinaryPrimitives.WriteUInt64LittleEndian(span3.Slice(0, 8), list[num13].Co);
				BinaryPrimitives.WriteUInt64LittleEndian(span3.Slice(8, 8), list[num13].Co >> 16);
			}
			WriteRecord(list2, "i2op", 1, array13);
		}
		else
		{
			byte[] array14 = new byte[num2 * 16];
			for (int num14 = 0; num14 < num2; num14++)
			{
				Span<byte> span4 = array14.AsSpan(num14 * 16, 16);
				BinaryPrimitives.WriteUInt64LittleEndian(span4.Slice(0, 8), (ulong)num14 * 65536uL);
				BinaryPrimitives.WriteUInt64LittleEndian(span4.Slice(8, 8), (ulong)num14 * 65536uL);
			}
			WriteRecord(list2, "i2op", 1, array14);
		}
		if (list != null)
		{
			byte[] array15 = new byte[list.Count * 48];
			for (int num15 = 0; num15 < list.Count; num15++)
			{
				Meta18Block meta18Block2 = list[num15];
				Span<byte> span5 = array15.AsSpan(num15 * 48, 48);
				array.AsSpan(num15 * 8, 8).CopyTo(span5.Slice(0, 8));
				prosperoNapsIntegrityContext.MappingBlocks[num15].Sha3Digest.AsSpan(0, 32).CopyTo(span5.Slice(8, 32));
				BinaryPrimitives.WriteUInt64LittleEndian(span5.Slice(40, 8), meta18Block2.Tail);
			}
			WriteRecord(list2, "ihsh", 1, array15);
		}
		else
		{
			byte[] array16 = new byte[num2 * 48];
			for (int num16 = 0; num16 < num2; num16++)
			{
				Span<byte> span6 = array16.AsSpan(num16 * 48, 48);
				array.AsSpan(num16 * 8, 8).CopyTo(span6.Slice(0, 8));
				prosperoNapsIntegrityContext.MappingBlocks[num16].Sha3Digest.AsSpan(0, 32).CopyTo(span6.Slice(8, 32));
			}
			WriteRecord(list2, "ihsh", 1, array16);
		}
		byte[] array18;
		checked
		{
			byte[] array17 = new byte[(list?.Count ?? num2) * 8];
			array2.CopyTo(array17, 0);
			WriteRecord(list2, "rhsh", 1, array17);
			StringBuilder stringBuilder = new StringBuilder();
			foreach (var contentFile in contentFiles)
			{
				string item = contentFile.Path;
				stringBuilder.Append(item.Replace('\\', '/'));
				stringBuilder.Append('\0');
			}
			WriteRecord(list2, "fstr", 1, Encoding.ASCII.GetBytes(stringBuilder.ToString()));
			Span<byte> span7 = stackalloc byte[20];
			WriteU32(span7, 4, num);
			WriteRecord(list2, "twek", 1, span7);
			array18 = new byte[(int)num * 32];
		}
		if (outerImageDigests != null && outerImageDigests.Length >= (int)num * 32)
		{
			Buffer.BlockCopy(outerImageDigests, 0, array18, 0, (int)num * 32);
			WriteRecord(list2, "obdg", 1, array18);
		}
		else
		{
			Stream? sourceStream = null;
			long streamBaseOffset = 0L;
			bool disposeStream = false;

			if (mountImageStream != null && mountImageStream.CanRead && mountImageStream.CanSeek && mountImageStream.Length >= 65536L + (long)num * 65536L)
			{
				sourceStream = mountImageStream;
				streamBaseOffset = 65536L;
			}
			else
			{
				sourceStream = inner?.OpenImage();
				disposeStream = true;
			}

			long originalStreamPos = (sourceStream != null && sourceStream.CanSeek) ? sourceStream.Position : 0L;
			try
			{
				byte[] chunkBuffer = new byte[8388608];
				int num17 = (int)num;
				int num18 = 0;
				int num19 = -1;
				for (int blockOffset = 0; blockOffset < num17; blockOffset += 128)
				{
					int num20 = Math.Min(128, num17 - blockOffset);
					long num21 = streamBaseOffset + (long)blockOffset * 65536L;
					int bytesToRead = 0;
					if (sourceStream != null && sourceStream.Length > num21)
					{
						bytesToRead = (int)Math.Min((long)num20 * 65536L, sourceStream.Length - num21);
						sourceStream.Position = num21;
						sourceStream.ReadExactly(chunkBuffer.AsSpan(0, bytesToRead));
					}
					Parallel.For(0, num20, new ParallelOptions
					{
						MaxDegreeOfParallelism = Math.Max(1, maxHashingThreads)
					}, (int num25) =>
					{
						int num24 = blockOffset + num25;
						ProsperoImageDigests.Sha3_256((bytesToRead >= (num25 + 1) * 65536) ? ((ReadOnlySpan<byte>)chunkBuffer.AsSpan(num25 * 65536, 65536)) : ReadOnlySpan<byte>.Empty).CopyTo(array18, num24 * 32);
					});
					if (maxHashingThreads <= 1)
					{
						Thread.Sleep(16);
					}
					else if (maxHashingThreads <= 2)
					{
						Thread.Sleep(8);
					}
					else if (maxHashingThreads <= 4)
					{
						Thread.Sleep(3);
					}
					num18 += num20;
					if (num17 > 1000)
					{
						int num22 = (int)((long)num18 * 100L / num17);
						if (num22 >= num19 + 5 || num18 == num17)
						{
							num19 = num22;
							log?.Invoke($"[stage 5/5] Generating NAPS outer block digests: {num22}% ({num18:N0}/{num17:N0} blocks)...");
						}
					}
				}
				WriteRecord(list2, "obdg", 1, array18);
			}
			finally
			{
				if (disposeStream)
				{
					sourceStream?.Dispose();
				}
				else if (sourceStream != null && sourceStream.CanSeek)
				{
					sourceStream.Position = originalStreamPos;
				}
			}
		}
		WriteRecord(list2, "obcc", 1, array3);
		byte[] array19 = BuildMeta300FromInnerImageSize(innerImageSize);
		WriteRecord(list2, "pgpl", 1, array19);
		WriteRecord(list2, "pgil", 1, array19);
		WriteRecord(list2, "pgpi", 1, array19);
		WriteRecord(list2, "pgpu", 1, array19);
		WriteRecord(list2, "gitt", 1, Encoding.ASCII.GetBytes("v1.5.0\0"));
		WriteRecord(list2, "gith", 1, Encoding.ASCII.GetBytes("7553c74caeba25754fbd4bee717652da631c08e4\0"));
		int num23 = (16 - list2.Count % 16) % 16;
		WriteRecord(list2, "zero", 1, new byte[num23]);
		return AesXtsTransform(list2.ToArray(), decrypt: false);
	}

	public static byte[] DecryptMeta18(byte[] encrypted)
	{
		ArgumentNullException.ThrowIfNull(encrypted, "encrypted");
		if (encrypted.Length == 0 || encrypted.Length % 16 != 0)
		{
			throw new InvalidDataException("naps_meta_18.dat must be a non-empty multiple of the 16-byte AES-XTS block size.");
		}
		return AesXtsTransform(encrypted, decrypt: true);
	}

	private static void WriteU32(Span<byte> dst, int offset, uint value)
	{
		BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(offset, 4), value);
	}

	private static void ValidateProtectedTable(byte[]? table, int expectedLength, string name)
	{
		if (table != null && table.Length != expectedLength)
		{
			throw new InvalidDataException($"NAPS {name} provider returned 0x{table.Length:X} bytes; expected 0x{expectedLength:X}.");
		}
	}

	private static void WriteRecord(List<byte> dst, string tag, byte version, ReadOnlySpan<byte> payload)
	{
		dst.Add((byte)tag[3]);
		dst.Add((byte)tag[2]);
		dst.Add((byte)tag[1]);
		dst.Add((byte)tag[0]);
		dst.Add(version);
		dst.Add(0);
		dst.Add(0);
		dst.Add(0);
		Span<byte> destination = stackalloc byte[8];
		BinaryPrimitives.WriteUInt64LittleEndian(destination, (ulong)payload.Length);
		for (int i = 0; i < 8; i++)
		{
			dst.Add(destination[i]);
		}
		for (int j = 0; j < payload.Length; j++)
		{
			dst.Add(payload[j]);
		}
	}

	private static byte[] AesXtsTransform(byte[] input, bool decrypt)
	{
		using Aes aes = Aes.Create();
		aes.Mode = CipherMode.ECB;
		aes.Padding = PaddingMode.None;
		aes.Key = Meta18DataKey;
		using Aes aes2 = Aes.Create();
		aes2.Mode = CipherMode.ECB;
		aes2.Padding = PaddingMode.None;
		aes2.Key = Meta18TweakKey;
		using ICryptoTransform cryptoTransform = (decrypt ? aes.CreateDecryptor() : aes.CreateEncryptor());
		using ICryptoTransform cryptoTransform2 = aes2.CreateEncryptor();
		byte[] array = cryptoTransform2.TransformFinalBlock(Meta18Tweak, 0, 16);
		byte[] array2 = new byte[input.Length];
		byte[] array3 = new byte[16];
		for (int i = 0; i < input.Length; i += 16)
		{
			for (int j = 0; j < 16; j++)
			{
				array3[j] = (byte)(input[i + j] ^ array[j]);
			}
			byte[] array4 = cryptoTransform.TransformFinalBlock(array3, 0, 16);
			for (int k = 0; k < 16; k++)
			{
				array2[i + k] = (byte)(array4[k] ^ array[k]);
			}
			array = GfMulAlpha(array);
		}
		return array2;
	}

	private static byte[] GfMulAlpha(byte[] t)
	{
		byte[] array = new byte[16];
		int num = 0;
		for (int i = 0; i < 16; i++)
		{
			int num2 = t[i];
			array[i] = (byte)(((num2 << 1) | num) & 0xFF);
			num = (num2 >> 7) & 1;
		}
		if (num != 0)
		{
			array[0] ^= 135;
		}
		return array;
	}

	private static int PlacementBlockCount(ProsperoPs5InnerPlacement placement)
	{
		IReadOnlyList<ProsperoInnerDataBlockChunk> compressionBlocks = placement.CompressionBlocks;
		if (compressionBlocks != null && compressionBlocks.Count > 0)
		{
			return compressionBlocks.Count;
		}
		checked
		{
			return (int)Math.Max(1L, unchecked(checked(placement.UncompressedSize + 262144 - 1) / 262144));
		}
	}

	private static bool IsExecutableAfid(ProsperoPs5InnerImageResult? inner, int afid)
	{
		return inner?.Nodes.Any((ProsperoPs5MetaNode n) => !n.IsDirectory && n.Afid == (uint)afid && (n.Flags & 0x40) != 0) ?? false;
	}

	private static List<Meta18Block> BuildInnerBlocks(ProsperoPs5InnerImageResult inner)
	{
		List<Meta18Block> list = new List<Meta18Block>();
		Dictionary<uint, byte[]> dictionary = new Dictionary<uint, byte[]>();
		IReadOnlyList<ProsperoPs5InnerPlacement> placements = inner.Placements;
		for (int i = 0; i < placements.Count; i++)
		{
			ProsperoPs5InnerPlacement prosperoPs5InnerPlacement = placements[i];
			uint ownerFlag = (IsExecutableAfid(inner, checked((int)prosperoPs5InnerPlacement.Afid)) ? 1u : 0u);
			IReadOnlyList<ProsperoInnerDataBlockChunk> compressionBlocks = prosperoPs5InnerPlacement.CompressionBlocks;
			if (compressionBlocks != null && compressionBlocks.Count > 0)
			{
				long num = prosperoPs5InnerPlacement.OnDiskOffset;
				int num2 = 0;
				for (int j = 0; j < compressionBlocks.Count; j++)
				{
					ProsperoInnerDataBlockChunk prosperoInnerDataBlockChunk = compressionBlocks[j];
					uint num3;
					uint num4;
					uint num5;
					checked
					{
						num3 = (uint)prosperoInnerDataBlockChunk.CompressedSize;
						num4 = (uint)prosperoInnerDataBlockChunk.UncompressedSize;
						num5 = (prosperoInnerDataBlockChunk.IsMultiChunk ? ((uint)prosperoInnerDataBlockChunk.FirstChunkCompressedSize) : num3);
					}
					uint num6 = (prosperoInnerDataBlockChunk.IsMultiChunk ? (num3 - num5) : 0u);
					uint flag = ((num6 != 0) ? 1078263808u : 1074069504u);
					byte[] sha3Digest = null;
					ulong? ihsh = null;
					ulong? rhsh = null;
					ReadOnlyMemory<byte> plaintext = ReadOnlyMemory<byte>.Empty;
					if (prosperoPs5InnerPlacement.BlockIntegrities != null && j < prosperoPs5InnerPlacement.BlockIntegrities.Count)
					{
						ProsperoInnerBlockIntegrity prosperoInnerBlockIntegrity = prosperoPs5InnerPlacement.BlockIntegrities[j];
						sha3Digest = prosperoInnerBlockIntegrity.Sha3Digest;
						ihsh = prosperoInnerBlockIntegrity.Ihsh;
						rhsh = prosperoInnerBlockIntegrity.Rhsh;
					}
					else
					{
						if (prosperoPs5InnerPlacement.PlainData.IsEmpty)
						{
							throw new InvalidDataException($"NAPS placement {i} has neither PlainData nor BlockIntegrities.");
						}
						if (num2 > prosperoPs5InnerPlacement.PlainData.Length - checked((int)num4))
						{
							throw new InvalidDataException($"NAPS placement {i} does not retain the 0x{num4:X}-byte plaintext block at 0x{num2:X}.");
						}
						plaintext = prosperoPs5InnerPlacement.PlainData.Slice(num2, checked((int)num4));
					}
					list.Add(new Meta18Block((ulong)num, num3, num4, num5, num6, flag, IsHole: false, ownerFlag, 0uL, num, num3, plaintext, checked(prosperoPs5InnerPlacement.LogicalOffset + num2), sha3Digest, ihsh, rhsh));
					num += num3;
					num2 += checked((int)num4);
				}
				continue;
			}
			long num7 = 0L;
			int num8 = 0;
			while (num7 < prosperoPs5InnerPlacement.UncompressedSize || num7 == 0L)
			{
				uint num9 = checked((uint)Math.Min(262144L, Math.Max(0L, prosperoPs5InnerPlacement.UncompressedSize - num7)));
				uint num10 = Math.Min(num9, 131072u);
				uint c = num9 - num10;
				long num11 = prosperoPs5InnerPlacement.OnDiskOffset + num7;
				byte[] sha3Digest2 = null;
				ulong? ihsh2 = null;
				ulong? rhsh2 = null;
				ReadOnlyMemory<byte> plaintext2 = ReadOnlyMemory<byte>.Empty;
				if (prosperoPs5InnerPlacement.BlockIntegrities != null && num8 < prosperoPs5InnerPlacement.BlockIntegrities.Count)
				{
					ProsperoInnerBlockIntegrity prosperoInnerBlockIntegrity2 = prosperoPs5InnerPlacement.BlockIntegrities[num8];
					sha3Digest2 = prosperoInnerBlockIntegrity2.Sha3Digest;
					ihsh2 = prosperoInnerBlockIntegrity2.Ihsh;
					rhsh2 = prosperoInnerBlockIntegrity2.Rhsh;
				}
				else
				{
					if (prosperoPs5InnerPlacement.PlainData.IsEmpty)
					{
						throw new InvalidDataException($"NAPS raw placement {i} has neither PlainData nor BlockIntegrities.");
					}
					if (num7 > prosperoPs5InnerPlacement.PlainData.Length - checked((int)num9))
					{
						throw new InvalidDataException($"NAPS raw placement {i} does not retain the 0x{num9:X}-byte plaintext block at 0x{num7:X}.");
					}
					plaintext2 = checked(prosperoPs5InnerPlacement.PlainData.Slice((int)num7, (int)num9));
				}
				list.Add(new Meta18Block((ulong)num11, num9, num9, num10, c, 1074331648u, IsHole: false, ownerFlag, 0uL, num11, num9, plaintext2, checked(prosperoPs5InnerPlacement.LogicalOffset + num7), sha3Digest2, ihsh2, rhsh2));
				num7 += num9;
				num8++;
				if (num9 == 0)
				{
					break;
				}
			}
		}
		checked
		{
			foreach (ProsperoPs5SparseAfidHole sparseAfidHole in inner.SparseAfidHoles)
			{
				uint num12 = (uint)sparseAfidHole.Size;
				if (!dictionary.TryGetValue(num12, out var value))
				{
					byte[] array = (dictionary[num12] = new byte[num12]);
					value = array;
				}
				list.Add(new Meta18Block((ulong)inner.BlockInfoOnDiskOffset, 16u, num12, 8u, 8u, 1074855936u, IsHole: true, 0u, 0uL, 0L, 0u, value, sparseAfidHole.LogicalOffset));
			}
		}
		long num13 = inner.MetaBaseLogical - inner.DataEndLogical;
		if (num13 > 0)
		{
			int num14 = (int)((num13 + 262144 - 1) / 262144);
			Dictionary<uint, ulong> dictionary2 = new Dictionary<uint, ulong>();
			ulong num15 = (ulong)inner.BlockInfoOnDiskOffset;
			for (int k = 0; k < num14; k++)
			{
				uint num16 = (uint)Math.Min(262144L, num13 - (long)k * 262144L);
				if (!dictionary2.TryGetValue(num16, out var value2))
				{
					ulong num17 = (dictionary2[num16] = num15);
					value2 = num17;
					num15 += 16;
				}
				if (!dictionary.TryGetValue(num16, out var value3))
				{
					byte[] array = (dictionary[num16] = new byte[num16]);
					value3 = array;
				}
				checked
				{
					list.Add(new Meta18Block(value2, 16u, num16, 8u, 8u, 1074855936u, IsHole: true, 0u, 1001uL, 0L, 0u, value3, inner.DataEndLogical + unchecked((long)k) * 262144L));
				}
			}
		}
		IReadOnlyList<ProsperoInnerMetaBlockChunk> readOnlyList = inner.MetadataBlocks;
		if (readOnlyList.Count == 0 && inner.MetadataPlaintext.Length != 0)
		{
			readOnlyList = ProsperoCompressedPfsFile.Parse(ProsperoCompressedPfsImage.Pack(inner.MetadataPlaintext)).Blocks.Select((ProsperoPfsBlock b) => new ProsperoInnerMetaBlockChunk(b.CompressedSize, b.UncompressedSize, b.IsMultiChunk, b.FirstChunkCompressedSize, b.Flags)).ToList();
		}
		ulong num19 = (ulong)inner.MetadataOnDiskOffset;
		int num20 = 0;
		foreach (ProsperoInnerMetaBlockChunk item in readOnlyList)
		{
			uint compressedSize = (uint)item.CompressedSize;
			int uncompressedSize = item.UncompressedSize;
			if (num20 > inner.MetadataPlaintext.Length - uncompressedSize)
			{
				throw new InvalidDataException($"NAPS metadata map exceeds its 0x{inner.MetadataPlaintext.Length:X}-byte plaintext.");
			}
			uint num21 = (item.IsMultiChunk ? ((uint)item.FirstChunkCompressedSize) : compressedSize);
			uint num22 = (item.IsMultiChunk ? (compressedSize - num21) : 0u);
			uint flag2 = ((num22 != 0) ? 1078263808u : 1074069504u);
			list.Add(new Meta18Block(num19, compressedSize, (uint)uncompressedSize, num21, num22, flag2, IsHole: false, 0u, 1001uL, (long)num19, compressedSize, inner.MetadataPlaintext.AsMemory(num20, uncompressedSize), checked(inner.MetaBaseLogical + num20)));
			num19 += compressedSize;
			num20 += uncompressedSize;
		}
		return list.OrderBy((Meta18Block block) => block.LogicalOffset).ToList();
	}

	private static byte[] InnerHoleDigestPreimage(uint plaintextSize)
	{
		return new byte[plaintextSize];
	}

	private static ProsperoNapsIntegrityContext BuildIntegrityContext(ulong innerImageSize, byte[] mountImage, ProsperoPs5InnerImageResult? inner, IReadOnlyList<Meta18Block>? blocks, byte[]? pfsImageKey, byte[]? pfsImageSeed)
	{
		List<ProsperoNapsIntegrityBlock> list = new List<ProsperoNapsIntegrityBlock>();
		if (blocks != null)
		{
			for (int i = 0; i < blocks.Count; i++)
			{
				Meta18Block meta18Block = blocks[i];
				byte[] sha3Digest = meta18Block.Sha3Digest ?? (meta18Block.Plaintext.IsEmpty ? Array.Empty<byte>() : ProsperoImageDigests.Sha3_256(meta18Block.Plaintext.Span));
				list.Add(new ProsperoNapsIntegrityBlock(i, meta18Block.Co, meta18Block.Cs, meta18Block.Ps, meta18Block.IsHole, meta18Block.OwnerFlag, meta18Block.Tail, meta18Block.OnDiskOffset, meta18Block.OnDiskLen, meta18Block.Plaintext, sha3Digest, meta18Block.Ihsh, meta18Block.Rhsh));
			}
		}
		else
		{
			int num = mountImage.Length / 65536;
			for (int j = 0; j < num; j++)
			{
				byte[] sha3Digest2 = ProsperoImageDigests.Sha3_256(mountImage.AsSpan(j * 65536, 65536));
				list.Add(new ProsperoNapsIntegrityBlock(j, (ulong)j * 65536uL, 65536u, 65536u, IsHole: false, 0u, 0uL, (long)j * 65536L, 65536u, mountImage.AsMemory(j * 65536, 65536), sha3Digest2));
			}
		}
		byte[] array = inner?.Image;
		ReadOnlyMemory<byte> physicalInnerImage = ((array != null) ? ((ReadOnlyMemory<byte>)array) : ReadOnlyMemory<byte>.Empty);
		ReadOnlyMemory<byte> pfsImageKey2 = ((pfsImageKey != null) ? ((ReadOnlyMemory<byte>)pfsImageKey) : ReadOnlyMemory<byte>.Empty);
		ReadOnlyMemory<byte> pfsImageSeed2 = ((pfsImageSeed != null) ? ((ReadOnlyMemory<byte>)pfsImageSeed) : ReadOnlyMemory<byte>.Empty);
		return new ProsperoNapsIntegrityContext
		{
			InnerImageSize = innerImageSize,
			MountImage = mountImage,
			PhysicalInnerImage = physicalInnerImage,
			PhysicalInnerImagePath = inner?.ImagePath,
			PfsImageKey = pfsImageKey2,
			PfsImageSeed = pfsImageSeed2,
			MappingBlocks = list
		};
	}

	private static byte[] BuildIhshPrefixes(ProsperoNapsIntegrityContext context)
	{
		byte[] array = new byte[checked(context.MappingBlocks.Count * 8)];
		for (int i = 0; i < context.MappingBlocks.Count; i++)
		{
			ulong value = context.MappingBlocks[i].Ihsh ?? ComputeInputChecksum(context.MappingBlocks[i].Plaintext.Span);
			BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(i * 8, 8), value);
		}
		return array;
	}

	private static byte[] BuildRollingHashes(ProsperoNapsIntegrityContext context)
	{
		byte[] array = new byte[checked(context.MappingBlocks.Count * 8)];
		for (int i = 0; i < context.MappingBlocks.Count; i++)
		{
			ulong value = context.MappingBlocks[i].Rhsh ?? ComputeRollingHash(context.MappingBlocks[i].Plaintext.Span);
			BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(i * 8, 8), value);
		}
		return array;
	}

	public static byte[] BuildOuterBlockCheckCodes(ProsperoNapsIntegrityContext context)
	{
		ArgumentNullException.ThrowIfNull(context, "context");
		int physicalInnerBlockCount = context.PhysicalInnerBlockCount;
		byte[] array = new byte[checked(physicalInnerBlockCount * 4)];
		ReadOnlyMemory<byte> readOnlyMemory = context.PfsImageKey;
		if (readOnlyMemory.IsEmpty)
		{
			readOnlyMemory = context.PfsImageSeed;
			if (readOnlyMemory.IsEmpty)
			{
				return array;
			}
		}
		readOnlyMemory = context.PfsImageKey;
		if (readOnlyMemory.Length == 32)
		{
			readOnlyMemory = context.PfsImageSeed;
			if (readOnlyMemory.Length == 16)
			{
				Span<byte> span = stackalloc byte[20];
				BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0, 4), 1u);
				readOnlyMemory = context.PfsImageSeed;
				readOnlyMemory.Span.CopyTo(span.Slice(4));
				readOnlyMemory = context.PfsImageKey;
				byte[] array2 = HMACSHA256.HashData(readOnlyMemory.Span, span);
				using XtsBlockTransform xtsBlockTransform = new XtsBlockTransform(array2.AsSpan(16, 16).ToArray(), array2.AsSpan(0, 16).ToArray());
				using Stream stream = OpenPhysicalInnerImage(context);
				byte[] array3 = new byte[65536];
				for (int i = 0; i < physicalInnerBlockCount; i++)
				{
					Array.Clear(array3);
					int j;
					int num;
					for (j = 0; j < array3.Length; j += num)
					{
						num = stream.Read(array3, j, array3.Length - j);
						if (num == 0)
						{
							break;
						}
					}
					if (j != array3.Length)
					{
						throw new EndOfStreamException($"Physical pfs_image.dat ended in block {i}: read 0x{j:X} of 0x{array3.Length:X} bytes.");
					}
					xtsBlockTransform.CryptSector(array3, (ulong)i, encrypt: true);
					BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(i * 4, 4), ProsperoCrc32C.Compute(array3));
				}
				return array;
			}
		}
		throw new InvalidDataException("NAPS pfs-image-key and pfs-image-seed must contain exactly 32 and 16 bytes.");
	}

	private static Stream OpenPhysicalInnerImage(ProsperoNapsIntegrityContext context)
	{
		if (!string.IsNullOrWhiteSpace(context.PhysicalInnerImagePath))
		{
			return new FileStream(context.PhysicalInnerImagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.SequentialScan);
		}
		if (!context.PhysicalInnerImage.IsEmpty)
		{
			return new MemoryStream(context.PhysicalInnerImage.ToArray(), writable: false);
		}
		throw new InvalidDataException("NAPS obcc generation requires the physical pfs_image.dat bytes or file path.");
	}

	public static ulong ComputeInputChecksum(ReadOnlySpan<byte> input)
	{
		uint num = 0u;
		uint num2 = checked((uint)input.Length);
		ReadOnlySpan<byte> readOnlySpan = input;
		for (int i = 0; i < readOnlySpan.Length; i++)
		{
			byte b = readOnlySpan[i];
			num += b;
			num2 += num;
		}
		return num | ((ulong)num2 << 32);
	}

	public static ulong ComputeRollingHash(ReadOnlySpan<byte> input)
	{
		ulong num = 0uL;
		ulong num2 = 0uL;
		int num3 = Math.Min(65536, input.Length);
		for (int i = 0; i < num3; i++)
		{
			num += input[i];
			num2 += num;
		}
		for (int j = num3; j < 65536; j++)
		{
			num2 += num;
		}
		return num ^ (num2 << 25);
	}
}
