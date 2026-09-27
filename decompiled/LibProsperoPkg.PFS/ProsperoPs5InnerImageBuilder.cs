using System;
using System.Collections.Generic;
using System.IO;
using LibProsperoPkg.PFS.Compression;

namespace LibProsperoPkg.PFS;

/// <summary>
/// Assembles the inner <c>pfs_image.dat</c>: data-first per-file layout (raw files block-aligned,
/// compressed files packed), a 32-byte block-info table, then the compressed metadata.
/// </summary>
public sealed class ProsperoPs5InnerImageBuilder
{
	/// <summary>The inner-image block size (64 KiB).</summary>
	public const int BlockSize = 65536;

	/// <summary>The per-file Kraken compression block size (256 KiB).</summary>
	public const int CompressBlockSize = 262144;

	private static long AlignUp(long v, long a)
	{
		return (v + a - 1) & ~(a - 1);
	}

	/// <summary>
	/// Kraken-compresses a payload into its concatenated on-disk bytes (256 KiB blocks). Returns the raw bytes
	/// when compression does not save at least 6.25%, or when <paramref name="storeRaw" />.
	/// </summary>
	public static byte[] CompressPayload(byte[] raw, bool storeRaw)
	{
		ProsperoCompressedPfsFile compressedFile;
		return CompressPayload(raw, storeRaw, out compressedFile);
	}

	/// <summary>
	/// As <see cref="M:LibProsperoPkg.PFS.ProsperoPs5InnerImageBuilder.CompressPayload(System.Byte[],System.Boolean)" />, but also returns the parsed
	/// <see cref="T:LibProsperoPkg.PFS.Compression.ProsperoCompressedPfsFile" /> (its per-block chunk table) when the payload is stored
	/// compressed, so callers that need the block boundaries (e.g. the naps generator) do not have to
	/// Kraken-pack the same buffer a second time. <paramref name="compressedFile" /> is <see langword="null" />
	/// when the payload is stored raw (either <paramref name="storeRaw" /> or the 6.25% keep rule fell back).
	/// </summary>
	public static byte[] CompressPayload(byte[] raw, bool storeRaw, out ProsperoCompressedPfsFile? compressedFile)
	{
		compressedFile = null;
		if (storeRaw)
		{
			return raw;
		}
		ProsperoCompressedPfsFile prosperoCompressedPfsFile = ProsperoCompressedPfsFile.Parse(ProsperoCompressedPfsImage.Pack(raw));
		using MemoryStream memoryStream = new MemoryStream();
		foreach (ProsperoPfsBlock block in prosperoCompressedPfsFile.Blocks)
		{
			byte[] array = block.CompressedData.ToArray();
			memoryStream.Write(array, 0, array.Length);
		}
		byte[] array2 = memoryStream.ToArray();
		if (array2.Length <= (int)((long)raw.Length * 15L >> 4))
		{
			compressedFile = prosperoCompressedPfsFile;
			return array2;
		}
		return raw;
	}

	/// <summary>
	/// Assembles the inner image. <paramref name="payloads" /> are, in on-disk order, the data files followed by
	/// the block-info table payload and the metadata block. Each payload is compressed per its flags and placed
	/// block-aligned or packed. Returns the block-aligned-tail on-disk image.
	/// </summary>
	public byte[] Build(IReadOnlyList<ProsperoPs5InnerPayload> payloads)
	{
		using MemoryStream memoryStream = new MemoryStream();
		Write(memoryStream, payloads);
		return memoryStream.ToArray();
	}

	/// <summary>
	/// Streaming counterpart of <see cref="M:LibProsperoPkg.PFS.ProsperoPs5InnerImageBuilder.Build(System.Collections.Generic.IReadOnlyList{LibProsperoPkg.PFS.ProsperoPs5InnerPayload})" />. Payloads are compressed one at a time and
	/// written directly to a seekable destination; gaps created by alignment remain zero-filled.
	/// Returns the exact unpadded image length.
	/// </summary>
	public long Write(Stream output, IReadOnlyList<ProsperoPs5InnerPayload> payloads)
	{
		ArgumentNullException.ThrowIfNull(output, "output");
		ArgumentNullException.ThrowIfNull(payloads, "payloads");
		if (!output.CanWrite || !output.CanSeek)
		{
			throw new ArgumentException("Inner-image output must be writable and seekable.", "output");
		}
		output.Position = 0L;
		output.SetLength(0L);
		long num = 0L;
		foreach (ProsperoPs5InnerPayload payload in payloads)
		{
			ArgumentNullException.ThrowIfNull(payload, "payload");
			byte[] array = CompressPayload(payload.Data, payload.StoreRaw);
			if (payload.BlockAligned)
			{
				num = AlignUp(num, 65536L);
			}
			output.Position = num;
			output.Write(array);
			num = checked(num + array.LongLength);
			if (payload.BlockAlignedAfter)
			{
				num = AlignUp(num, 65536L);
			}
		}
		output.SetLength(num);
		output.Position = 0L;
		return num;
	}
}
