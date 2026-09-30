using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PKG;

public static class ProsperoFihBuilder
{
	private const int CntPfsImageOffsetField = 1040;

	private const int CntPfsImageSizeField = 1048;

	public static IReadOnlyList<string> BuildFromCnt(string cntPath, string fihOutputPath, ProsperoFihVariant variant = ProsperoFihVariant.Debug, Action<string>? logger = null, byte[]? siArchive = null, Func<byte[], byte[]>? siArchiveFactory = null, Func<Stream, byte[]>? siArchiveStreamFactory = null, byte[]? nestedImageDigest = null, long nestedImageSize = 0L, long nestedMetaBaseBlocks = 0L, uint nwonlyContentVersionHi = 0u, int nwonlyNapsFileCount = 0, int nwonlyAppFileCount = 0, int nwonlySparseAfidCount = 0, int nwonlyEmptyFileCount = 0, int outerSuperblockIndex = -1, IProsperoRetailFinalizationProvider? retailFinalizationProvider = null)
	{
		ArgumentException.ThrowIfNullOrEmpty(cntPath, "cntPath");
		ArgumentException.ThrowIfNullOrEmpty(fihOutputPath, "fihOutputPath");
		if (variant == ProsperoFihVariant.Official && retailFinalizationProvider == null)
		{
			throw new InvalidOperationException("Official FIH generation requires a Retail finalization provider. Writing signed byte 0x80 without protected FIH material is refused.");
		}
		Action<string> action = logger ?? ((Action<string>)((string _) =>
		{
		}));
		List<string> list = new List<string>();
		string fullPath = Path.GetFullPath(cntPath);
		string fullPath2 = Path.GetFullPath(fihOutputPath);
		if (string.Equals(fullPath, fullPath2, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
		{
			throw new ArgumentException("CNT input and FIH output paths must be different.");
		}
		using FileStream fileStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.RandomAccess);
		byte[] array = ReadStreamRange(fileStream, 0L, 1440);
		if (array[0] != ProsperoPkgLayout.CntMagic[0] || array[1] != ProsperoPkgLayout.CntMagic[1] || array[2] != ProsperoPkgLayout.CntMagic[2] || array[3] != ProsperoPkgLayout.CntMagic[3])
		{
			throw new InvalidDataException("Input is not a PS5 CNT metadata package.");
		}
		ulong num = BinaryPrimitives.ReadUInt64BigEndian(array.AsSpan(1040));
		ulong num2 = BinaryPrimitives.ReadUInt64BigEndian(array.AsSpan(1048));
		if (num == 0L || num2 == 0L || num > (ulong)fileStream.Length || num2 > (ulong)(fileStream.Length - (long)num))
		{
			throw new InvalidDataException("CNT package has no embedded PFS image to finalize.");
		}
		if (num > int.MaxValue)
		{
			throw new InvalidDataException("CNT metadata preceding the PFS image exceeds the supported 2-GiB metadata limit.");
		}
		byte[] array2 = ReadStreamRange(fileStream, 0L, checked((int)num));
		BinaryPrimitives.WriteUInt64BigEndian(array2.AsSpan(1040), 65536uL);
		ulong num3 = 65536 + num2;
		(long SuperblockOffset, byte[]? GameDigest, byte[] ImageDigest) tuple = AnalyzeImageRange(fileStream, (long)num, (long)num2, outerSuperblockIndex);
		long item = tuple.SuperblockOffset;
		byte[] item2 = tuple.GameDigest;
		byte[] item3 = tuple.ImageDigest;
		byte[] array3 = BuildFihHeaderBlock(variant, num2, num3, item, item2, item3, list, nestedImageDigest, nestedImageSize, nestedMetaBaseBlocks, nwonlyContentVersionHi, nwonlyNapsFileCount, nwonlyAppFileCount, nwonlySparseAfidCount, nwonlyEmptyFileCount);
		if (variant == ProsperoFihVariant.Official)
		{
			ApplyOfficialDigestSlots(array3, array2);
		}
		action($"Writing finalized {((variant == ProsperoFihVariant.Debug) ? "debug" : "official")} (FIH) image: image=0x{num2:X} @0x{65536:X}, CNT @0x{num3:X}.");
		byte[] array4 = siArchive;
		if (variant == ProsperoFihVariant.Official)
		{
			ProsperoRetailFinalizationResult prosperoRetailFinalizationResult = retailFinalizationProvider.FinalizeFih(new ProsperoRetailFinalizationRequest
			{
				FihHeader = array3
			}) ?? throw new InvalidOperationException("The Retail finalization provider returned null.");
			if (prosperoRetailFinalizationResult.FihFinalizationMaterial == null || prosperoRetailFinalizationResult.FihFinalizationMaterial.Length != 768)
			{
				throw new InvalidDataException($"The Retail FIH finalization material must contain exactly 0x{768:X} bytes.");
			}
			if (IsAllZero(prosperoRetailFinalizationResult.FihFinalizationMaterial))
			{
				throw new InvalidDataException("The Retail FIH finalization material is all zero.");
			}
			prosperoRetailFinalizationResult.FihFinalizationMaterial.CopyTo(array3, 61440);
			array4 = prosperoRetailFinalizationResult.SupplementalData ?? throw new InvalidDataException("The Retail finalization provider returned null supplemental data.");
			ResealOfficialCnt(array2, array3, retailFinalizationProvider);
			action($"Applied Retail finalization: FIH+0x{61440:X} size=0x{768:X}, CNT authentication=0x{384:X}, supplemental=0x{array4.Length:X}.");
		}
		using (FileStream fileStream2 = new FileStream(fullPath2, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1048576, FileOptions.SequentialScan))
		{
			fileStream2.Write(array3, 0, array3.Length);
			CopyStreamRange(fileStream, fileStream2, (long)num, (long)num2, action);
			fileStream2.Write(array2, 0, array2.Length);
			fileStream2.Flush();
			if (variant == ProsperoFihVariant.Debug && array4 == null && siArchiveStreamFactory != null)
			{
				fileStream2.Position = 0L;
				array4 = siArchiveStreamFactory(fileStream2);
			}
			else if (variant == ProsperoFihVariant.Debug && array4 == null && siArchiveFactory != null)
			{
				if (fileStream2.Length > int.MaxValue)
				{
					throw new InvalidOperationException("The byte-array SI factory cannot process a mount image larger than 2 GiB. Use siArchiveStreamFactory.");
				}
				fileStream2.Position = 0L;
				byte[] array5 = new byte[(int)fileStream2.Length];
				fileStream2.ReadExactly(array5);
				array4 = siArchiveFactory(array5);
			}
			if (array4 != null && array4.Length != 0)
			{
				fileStream2.Position = fileStream2.Length;
				fileStream2.Write(array4, 0, array4.Length);
				action($"Appended SI segment: 0x{array4.Length:X} bytes after the embedded CNT.");
			}
		}
		action("INFO: FIH/CNT structural SHA3 digests and per-entry digest table were populated; " + ((nestedImageDigest != null && nestedImageDigest.Length == 32) ? "the nested NAPS layout digest was recorded." : "a standalone outer-image digest fallback was used.") + ((variant == ProsperoFihVariant.Debug) ? " Target: debug-mode console." : " Retail finalization material was supplied by the configured provider."));
		action("Done (FIH).");
		return list;
	}

	internal static byte[] BuildFihHeaderBlock(ProsperoFihVariant variant, ulong pfsImageSize, ulong embeddedCntOffset, byte[] image, List<string>? warnings = null, byte[]? nestedImageDigest = null, long nestedImageSize = 0L, long nestedMetaBaseBlocks = 0L, uint nwonlyContentVersionHi = 0u, int nwonlyNapsFileCount = 0, int nwonlyAppFileCount = 0, int nwonlySparseAfidCount = 0, int nwonlyEmptyFileCount = 0)
	{
		(int Offset, byte[]? Digest) tuple = ProsperoImageDigests.ComputeSblockDigestFromImage(image);
		int item = tuple.Offset;
		byte[] item2 = tuple.Digest;
		byte[] imageDigest = item2 ?? ProsperoImageDigests.Sha3_256(image);
		return BuildFihHeaderBlock(variant, pfsImageSize, embeddedCntOffset, item, item2, imageDigest, warnings, nestedImageDigest, nestedImageSize, nestedMetaBaseBlocks, nwonlyContentVersionHi, nwonlyNapsFileCount, nwonlyAppFileCount, nwonlySparseAfidCount, nwonlyEmptyFileCount);
	}

	internal static byte[] BuildFihHeaderBlock(ProsperoFihVariant variant, ulong pfsImageSize, ulong embeddedCntOffset, long sbOffsetInImage, byte[]? gameDigest, byte[] imageDigest, List<string>? warnings = null, byte[]? nestedImageDigest = null, long nestedImageSize = 0L, long nestedMetaBaseBlocks = 0L, uint nwonlyContentVersionHi = 0u, int nwonlyNapsFileCount = 0, int nwonlyAppFileCount = 0, int nwonlySparseAfidCount = 0, int nwonlyEmptyFileCount = 0)
	{
		ArgumentNullException.ThrowIfNull(imageDigest, "imageDigest");
		if (imageDigest.Length != 32)
		{
			throw new ArgumentException("Image digest must contain exactly 32 bytes.", "imageDigest");
		}
		byte[] array = new byte[65536];
		array[0] = ProsperoPkgLayout.FihMagic[0];
		array[1] = ProsperoPkgLayout.FihMagic[1];
		array[2] = ProsperoPkgLayout.FihMagic[2];
		array[3] = ProsperoPkgLayout.FihMagic[3];
		array[4] = 1;
		array[5] = (byte)((variant == ProsperoFihVariant.Official) ? 128u : 0u);
		array[6] = 3;
		BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(8), 1u);
		BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(16), 65536uL);
		BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(24), pfsImageSize);
		BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(40), 65536uL);
		BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(88), embeddedCntOffset);
		BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(96), 65536uL);
		BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(104), 140737488355328uL);
		if (nestedMetaBaseBlocks > 0)
		{
			BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(80), (ulong)nestedMetaBaseBlocks);
		}
		if (sbOffsetInImage >= 0 && gameDigest != null)
		{
			ulong value = (ulong)(65536 + sbOffsetInImage);
			BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(32), value);
			BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(40), 65536uL);
			CopyDigest(array, 48, gameDigest);
			CopyDigest(array, 112, gameDigest);
			CopyDigest(array, 208, gameDigest);
			int num = 65536;
			long num2 = sbOffsetInImage / num;
			long num3 = (long)pfsImageSize / (long)num;
			if (num2 >= 1 && sbOffsetInImage % num == 0L && (long)pfsImageSize % (long)num == 0L && num3 > num2)
			{
				bool flag = nwonlyNapsFileCount > 0;
				long num4 = ((flag && nestedImageSize > 0) ? ((nestedImageSize + num - 1) / num) : 1);
				if (num4 <= 0 || num4 > num2)
				{
					throw new InvalidDataException("The NAPS layout extent crosses the outer-PFS superblock.");
				}
				uint num5 = (uint)(num2 - num4);
				uint num6 = (uint)(flag ? nwonlyNapsFileCount : (num3 - num5));
				uint value2 = (flag ? (num6 + (uint)nwonlyEmptyFileCount) : num6);
				BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(144), num5);
				BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(148), num6);
				BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(152), value2);
				BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(160), (ulong)(num5 * num));
				if (nwonlyContentVersionHi != 0)
				{
					BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(156), nwonlyContentVersionHi);
				}
				if (nestedImageSize > 0)
				{
					BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(168), (ulong)nestedImageSize);
				}
				uint value3 = ((!flag || nwonlyAppFileCount <= 0) ? 1u : ((uint)nwonlyAppFileCount));
				BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(240), value3);
				checked
				{
					if (flag)
					{
						BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(244), (uint)nwonlySparseAfidCount);
						BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(252), (uint)nwonlyEmptyFileCount);
					}
					BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(248), 2u);
				}
			}
		}
		else
		{
			CopyDigest(array, 48, imageDigest);
			CopyDigest(array, 112, imageDigest);
			CopyDigest(array, 208, imageDigest);
			warnings?.Add("FIH game-digest filled best-effort: no plaintext outer superblock was found in the image (the SHA3-256(superblock) path applies to the nwonly outer-PFS image).");
		}
		CopyDigest(array, 176, (nestedImageDigest != null && nestedImageDigest.Length == 32) ? nestedImageDigest : imageDigest);
		return array;
	}

	private static void CopyDigest(byte[] dst, int offset, byte[] digest32)
	{
		Array.Copy(digest32, 0, dst, offset, Math.Min(32, digest32.Length));
	}

	private static bool IsAllZero(ReadOnlySpan<byte> value)
	{
		ReadOnlySpan<byte> readOnlySpan = value;
		for (int i = 0; i < readOnlySpan.Length; i++)
		{
			if (readOnlySpan[i] != 0)
			{
				return false;
			}
		}
		return true;
	}

	private static void ApplyOfficialDigestSlots(byte[] fih, byte[] cnt)
	{
		ProsperoPkgEntry? prosperoPkgEntry = ReadMetadataPackage(cnt).Entries.FirstOrDefault((ProsperoPkgEntry entry) => entry.RawId == 128) ?? throw new InvalidDataException("Official CNT has no GeneralDigests entry.");
		int num;
		int num2;
		checked
		{
			num = (int)prosperoPkgEntry.DataOffset;
			num2 = (int)prosperoPkgEntry.DataSize;
		}
		if (num < 0 || num2 < 416 || num > cnt.Length - num2)
		{
			throw new InvalidDataException("CNT GeneralDigests payload is outside the metadata region.");
		}
		cnt.AsSpan(num + 64, 32).CopyTo(fih.AsSpan(112, 32));
		cnt.AsSpan(num + 384, 32).CopyTo(fih.AsSpan(208, 32));
	}

	private static void ResealOfficialCnt(byte[] cnt, byte[] finalizedFih, IProsperoRetailFinalizationProvider provider)
	{
		if (cnt.Length < 4480)
		{
			throw new InvalidDataException("CNT metadata is too short for the header authentication block.");
		}
		ProsperoImageDigests.ComputeFixedInfoDigest(finalizedFih).CopyTo(cnt, 1120);
		ProsperoImageDigests.ComputeCntHeaderRollupDigest(cnt).CopyTo(cnt, 256);
		ProsperoImageDigests.ComputePackageDigest(cnt).CopyTo(cnt, 4064);
		byte[] array = provider.FinalizeCntHeader(new ProsperoRetailCntFinalizationRequest
		{
			CntHeader = cnt.AsMemory(0, 4096)
		}) ?? throw new InvalidOperationException("The Retail finalization provider returned null CNT authentication material.");
		if (array.Length != 384 || IsAllZero(array))
		{
			throw new InvalidDataException($"The Retail CNT authentication material must contain exactly 0x{384:X} non-zero bytes.");
		}
		array.CopyTo(cnt, 4096);
	}

	private static ProsperoPkg ReadMetadataPackage(byte[] cnt)
	{
		using MemoryStream stream = new MemoryStream(cnt, writable: false);
		ProsperoPkg prosperoPkg = ProsperoPkgReader.Read(stream);
		if (prosperoPkg.Type != ProsperoPkgType.Meta)
		{
			throw new InvalidDataException("Embedded metadata is not a CNT package.");
		}
		return prosperoPkg;
	}

	private static (long SuperblockOffset, byte[]? GameDigest, byte[] ImageDigest) AnalyzeImageRange(Stream stream, long offset, long size, int knownSuperblockIndex)
	{
		scoped Span<byte> span;
		checked
		{
			if (knownSuperblockIndex >= 0)
			{
				long num = unchecked((long)knownSuperblockIndex) * 65536L;
				if (num > unchecked(size - 65536))
				{
					throw new InvalidDataException("Known outer superblock index is outside the PFS image.");
				}
				byte[] array = ReadStreamRange(stream, offset + num, 65536);
				if (BinaryPrimitives.ReadUInt64LittleEndian(array) != 2 || array[8] != 11 || array[9] != 42 || array[10] != 51 || array[11] != 1)
				{
					throw new InvalidDataException("Known outer superblock index does not point to a PFS superblock.");
				}
				byte[] array2 = ProsperoImageDigests.ComputeSblockDigest(array);
				return (SuperblockOffset: num, GameDigest: array2, ImageDigest: array2);
			}
			span = stackalloc byte[12];
		}
		for (long num2 = 0L; num2 <= size - 65536; num2 += 65536)
		{
			checked
			{
				stream.Position = offset + num2;
				stream.ReadExactly(span);
				if (BinaryPrimitives.ReadUInt64LittleEndian(span) == 2 && span[8] == 11 && span[9] == 42 && span[10] == 51 && span[11] == 1)
				{
					byte[] array3 = ProsperoImageDigests.ComputeSblockDigest(ReadStreamRange(stream, offset + num2, 65536));
					return (SuperblockOffset: num2, GameDigest: array3, ImageDigest: array3);
				}
			}
		}
		ProsperoSha3.Incremental incremental = new ProsperoSha3.Incremental();
		byte[] array4 = new byte[1048576];
		stream.Position = offset;
		long num3 = size;
		while (num3 != 0L)
		{
			int count = (int)Math.Min(array4.Length, num3);
			int num4 = stream.Read(array4, 0, count);
			if (num4 == 0)
			{
				throw new EndOfStreamException();
			}
			incremental.AppendData(array4.AsSpan(0, num4));
			num3 -= num4;
		}
		return (SuperblockOffset: -1L, GameDigest: null, ImageDigest: incremental.GetHashAndReset());
	}

	private static byte[] ReadStreamRange(Stream stream, long offset, int size)
	{
		if (offset < 0 || size < 0 || offset > stream.Length || size > stream.Length - offset)
		{
			throw new InvalidDataException("Requested CNT range is outside the input stream.");
		}
		byte[] array = new byte[size];
		stream.Position = offset;
		stream.ReadExactly(array);
		return array;
	}

	private static void CopyStreamRange(Stream input, Stream output, long offset, long size, Action<string>? logger = null)
	{
		if (offset < 0 || size < 0 || offset > input.Length || size > input.Length - offset)
		{
			throw new InvalidDataException("Requested PFS range is outside the CNT input.");
		}
		input.Position = offset;
		byte[] array = new byte[1048576];
		long num = 0L;
		long num2 = size;
		int num3 = -1;
		while (size != 0L)
		{
			int count = (int)Math.Min(array.Length, size);
			int num4 = input.Read(array, 0, count);
			if (num4 == 0)
			{
				throw new EndOfStreamException();
			}
			output.Write(array, 0, num4);
			size -= num4;
			num += num4;
			if (num2 > 100000000)
			{
				int num5 = (int)(num * 100 / num2);
				if (num5 >= num3 + 10 || num == num2)
				{
					num3 = num5;
					logger?.Invoke($"[stage 5/5] Writing finalized mount image: {num5}% ({num / 1048576:N0} / {num2 / 1048576:N0} MiB)...");
				}
			}
			if ((num & 0x1FFFFFF) == 0L)
			{
				Thread.Sleep(8);
			}
		}
	}
}
