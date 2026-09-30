using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PKG;

public static class ProsperoImageDigests
{
	public const int BlockSize = 65536;

	public const int DigestSize = 32;

	public const int DigestTableEntryId = 1;

	public const int PackageDigestRegionSize = 4064;

	public const int PackageDigestStoredOffset = 4064;

	public const int CntHeaderRollupStoredOffset = 256;

	private const int CntRollupSizeFieldOffset = 28;

	private const int CntRollupOffsetFieldOffset = 32;

	public const ushort GeneralDigestsTypeFull = 258;

	public const uint GeneralDigestsSetNwonly = 4318u;

	public const int HeaderDigestMountDescriptorSize = 128;

	public const int HeaderDigestPrefixSize = 64;

	public const int ContentDescriptorSize = 56;

	public const ulong FihRelativeImageOffset = 65536uL;

	public const int CntPfsImageOffsetField = 1040;

	private static ReadOnlySpan<byte> SuperblockMagic => new byte[4] { 11, 42, 51, 1 };

	public static byte[] Sha3_256(ReadOnlySpan<byte> data)
	{
		return ProsperoSha3.HashData(data);
	}

	public static byte[] ComputeSblockDigest(ReadOnlySpan<byte> superblockBlock)
	{
		if (superblockBlock.Length != 65536)
		{
			throw new ArgumentException($"Superblock block must be exactly 0x{65536:X} bytes.", "superblockBlock");
		}
		return Sha3_256(superblockBlock);
	}

	public static byte[] ComputeGameDigest(ReadOnlySpan<byte> superblockBlock)
	{
		return ComputeSblockDigest(superblockBlock);
	}

	public static byte[] ComputeFixedInfoDigest(ReadOnlySpan<byte> fihHeaderBlock)
	{
		if (fihHeaderBlock.Length != 65536)
		{
			throw new ArgumentException($"FIH header block must be exactly 0x{65536:X} bytes.", "fihHeaderBlock");
		}
		return Sha3_256(fihHeaderBlock);
	}

	public static byte[] ComputeBodyDigest(ReadOnlySpan<byte> cntBody)
	{
		return Sha3_256(cntBody);
	}

	public static byte[] ComputeEntryDigest(ReadOnlySpan<byte> entryPayload)
	{
		return Sha3_256(entryPayload);
	}

	public static byte[] ToStoredImageDigestTable(ReadOnlySpan<byte> imageDigests)
	{
		if (imageDigests.Length % 32 != 0)
		{
			throw new ArgumentException($"Image-digest table length must be a multiple of {32} bytes.", "imageDigests");
		}
		byte[] array = imageDigests.ToArray();
		for (int i = 0; i < array.Length; i += 32)
		{
			Array.Reverse(array, i, 32);
		}
		return array;
	}

	public static byte[] ComputeContentDigest(ReadOnlySpan<byte> contentDescriptor, ReadOnlySpan<byte> gameDigest, ReadOnlySpan<byte> majorParamDigest, bool includeGame)
	{
		if (contentDescriptor.Length != 56)
		{
			throw new ArgumentException($"Content descriptor must be exactly 0x{56:X} bytes.", "contentDescriptor");
		}
		if (includeGame && gameDigest.Length != 32)
		{
			throw new ArgumentException($"Game-digest must be exactly {32} bytes.", "gameDigest");
		}
		if (majorParamDigest.Length != 32)
		{
			throw new ArgumentException($"Major-param-digest must be exactly {32} bytes.", "majorParamDigest");
		}
		int num = 56 + (includeGame ? 32 : 0) + 32;
		Span<byte> span = ((num <= 256) ? stackalloc byte[num] : ((Span<byte>)new byte[num]));
		Span<byte> span2 = span;
		int num2 = 0;
		contentDescriptor.CopyTo(span2.Slice(num2));
		num2 += 56;
		if (includeGame)
		{
			gameDigest.CopyTo(span2.Slice(num2));
			num2 += 32;
		}
		majorParamDigest.CopyTo(span2.Slice(num2));
		return Sha3_256(span2);
	}

	public static byte[] ComputeHeaderDigest(ReadOnlySpan<byte> cntHeaderPrefix, ReadOnlySpan<byte> mountDescriptor)
	{
		if (cntHeaderPrefix.Length != 64)
		{
			throw new ArgumentException($"CNT header prefix must be exactly 0x{64:X} bytes.", "cntHeaderPrefix");
		}
		if (mountDescriptor.Length != 128)
		{
			throw new ArgumentException($"Mount descriptor must be exactly 0x{128:X} bytes.", "mountDescriptor");
		}
		Span<byte> span = stackalloc byte[192];
		cntHeaderPrefix.CopyTo(span);
		mountDescriptor.CopyTo(span.Slice(64));
		return Sha3_256(span);
	}

	public static byte[] ForceFihRelativeImageOffset(ReadOnlySpan<byte> mountDescriptor)
	{
		if (mountDescriptor.Length != 128)
		{
			throw new ArgumentException($"Mount descriptor must be exactly 0x{128:X} bytes.", "mountDescriptor");
		}
		byte[] array = mountDescriptor.ToArray();
		BinaryPrimitives.WriteUInt64BigEndian(array.AsSpan(16, 8), 65536uL);
		return array;
	}

	public static byte[] ComputeConcatDigest(IReadOnlyList<byte[]> entryDigests)
	{
		ArgumentNullException.ThrowIfNull(entryDigests, "entryDigests");
		byte[] array = new byte[entryDigests.Count * 32];
		for (int i = 0; i < entryDigests.Count; i++)
		{
			byte[] array2 = entryDigests[i];
			if (array2 == null || array2.Length != 32)
			{
				throw new ArgumentException($"Entry digest [{i}] must be exactly {32} bytes.", "entryDigests");
			}
			array2.CopyTo(array.AsSpan(i * 32, 32));
		}
		return Sha3_256(array);
	}

	public static byte[] BuildEntryDigestTable(IReadOnlyList<(int Id, ReadOnlyMemory<byte> Payload)> entries)
	{
		ArgumentNullException.ThrowIfNull(entries, "entries");
		byte[] array = new byte[entries.Count * 32];
		for (int i = 0; i < entries.Count; i++)
		{
			if (entries[i].Id != 1)
			{
				Sha3_256(entries[i].Payload.Span).CopyTo(array.AsSpan(i * 32, 32));
			}
		}
		return array;
	}

	public static byte[] ComputePackageDigest(ReadOnlySpan<byte> cnt)
	{
		if (cnt.Length < 4064)
		{
			throw new ArgumentException($"CNT must be at least 0x{4064:X} bytes to seal the package-digest.", "cnt");
		}
		return Sha3_256(cnt.Slice(0, 4064));
	}

	public static byte[] ComputeCntHeaderRollupDigest(ReadOnlySpan<byte> cnt)
	{
		if (cnt.Length < 40)
		{
			throw new ArgumentException("CNT is too small to contain the rollup header fields.", "cnt");
		}
		ulong num = BinaryPrimitives.ReadUInt64BigEndian(cnt.Slice(32, 8));
		uint num2 = BinaryPrimitives.ReadUInt32BigEndian(cnt.Slice(28, 4));
		if (num > (ulong)cnt.Length || (ulong)num2 > (ulong)((long)cnt.Length - (long)num))
		{
			throw new ArgumentException($"CNT rollup region [0x{num:X}, +0x{num2:X}) is outside the supplied CNT (0x{cnt.Length:X}).", "cnt");
		}
		return Sha3_256(cnt.Slice((int)num, (int)num2));
	}

	public static int LocateSuperblock(ReadOnlySpan<byte> image, int blockSize = 65536)
	{
		if (blockSize <= 16)
		{
			return -1;
		}
		for (int i = 0; i + blockSize <= image.Length; i += blockSize)
		{
			if (BinaryPrimitives.ReadUInt64LittleEndian(image.Slice(i, 8)) == 2 && image.Slice(i + 8, 4).SequenceEqual(SuperblockMagic))
			{
				return i;
			}
		}
		return -1;
	}

	public static (int Offset, byte[]? Digest) ComputeSblockDigestFromImage(ReadOnlySpan<byte> image, int blockSize = 65536)
	{
		int num = LocateSuperblock(image, blockSize);
		if (num < 0)
		{
			return (Offset: -1, Digest: null);
		}
		return (Offset: num, Digest: ComputeSblockDigest(image.Slice(num, 65536)));
	}
}
