using System;
using System.IO;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PFS;

/// <summary>
/// AES-XTS encrypt/decrypt for the PS5 nwonly <em>outer</em> finalized-image PFS. Each whole
/// filesystem block is one XTS data unit numbered by its image block index; the metadata
/// (superblock) block is left plaintext. See the file header for how this differs from the
/// inner-image crypto in <see cref="T:LibProsperoPkg.PFS.ProsperoPfsImage" /> and how it was validated.
/// </summary>
public static class ProsperoOuterPfsImage
{
	/// <summary>The outer finalized-image PFS block size (one block = one AES-XTS data unit).</summary>
	public const int DefaultBlockSize = 65536;

	/// <summary>
	/// Computes the index of the plaintext metadata (superblock) block from the package-absolute
	/// image and metadata offsets recorded in the FIH / <c>pfsimage.xml</c>
	/// (e.g. image <c>0x10000</c>, metadata <c>0x70000</c>, block <c>0x10000</c> ⇒ block 6).
	/// </summary>
	public static int MetadataBlockIndex(long imageOffset, long metadataOffset, int blockSize = 65536)
	{
		if (blockSize <= 0 || (blockSize & 0xF) != 0)
		{
			throw new ArgumentOutOfRangeException("blockSize", "Block size must be a positive multiple of 16.");
		}
		if (metadataOffset < imageOffset)
		{
			throw new ArgumentOutOfRangeException("metadataOffset", "Metadata offset must not precede the image offset.");
		}
		long num = metadataOffset - imageOffset;
		if (num % blockSize != 0L)
		{
			throw new ArgumentException("Metadata offset is not block-aligned within the image.", "metadataOffset");
		}
		checked
		{
			return (int)unchecked(num / blockSize);
		}
	}

	/// <summary>
	/// AES-XTS transforms <paramref name="image" /> in place: every whole block is encrypted (or
	/// decrypted) as a single XTS data unit with the sector number equal to its block index,
	/// except <paramref name="plaintextBlockIndex" /> which is left untouched. Returns the number
	/// of blocks transformed.
	/// </summary>
	/// <param name="image">The full outer-PFS image (mutated in place).</param>
	/// <param name="tweakKey">16-byte AES-XTS tweak key.</param>
	/// <param name="dataKey">16-byte AES-XTS data key.</param>
	/// <param name="blockSize">Block size in bytes (default 0x10000); must be a multiple of 16.</param>
	/// <param name="plaintextBlockIndex">Index of the metadata/superblock block to leave plaintext, or a negative value to encrypt every block.</param>
	/// <param name="encrypt"><c>true</c> to encrypt, <c>false</c> to decrypt.</param>
	public static int Transform(Span<byte> image, ReadOnlySpan<byte> tweakKey, ReadOnlySpan<byte> dataKey, int blockSize, int plaintextBlockIndex, bool encrypt)
	{
		if (tweakKey.Length != 16)
		{
			throw new ArgumentException($"Tweak key must be 16 bytes (was {tweakKey.Length}).", "tweakKey");
		}
		if (dataKey.Length != 16)
		{
			throw new ArgumentException($"Data key must be 16 bytes (was {dataKey.Length}).", "dataKey");
		}
		if (blockSize <= 0 || (blockSize & 0xF) != 0)
		{
			throw new ArgumentOutOfRangeException("blockSize", "Block size must be a positive multiple of 16.");
		}
		using XtsBlockTransform xtsBlockTransform = new XtsBlockTransform(dataKey.ToArray(), tweakKey.ToArray());
		int num = (image.Length + blockSize - 1) / blockSize;
		int num2 = 0;
		for (int i = 0; i < num; i++)
		{
			if (i != plaintextBlockIndex)
			{
				int num3 = i * blockSize;
				int num4 = Math.Min(blockSize, image.Length - num3);
				if ((num4 & 0xF) != 0)
				{
					throw new ArgumentException($"Block {i} length {num4} is not a multiple of the AES block size (16).", "image");
				}
				byte[] array = image.Slice(num3, num4).ToArray();
				xtsBlockTransform.CryptSector(array, (ulong)i, encrypt);
				array.CopyTo(image.Slice(num3, num4));
				num2++;
			}
		}
		return num2;
	}

	/// <summary>
	/// AES-XTS transforms <paramref name="image" /> in place using an explicit per-block
	/// classification: <see cref="F:LibProsperoPkg.PFS.ProsperoOuterBlockKind.Data" /> blocks use sector = block index,
	/// <see cref="F:LibProsperoPkg.PFS.ProsperoOuterBlockKind.Signed" /> blocks use sector =
	/// <see cref="F:LibProsperoPkg.PFS.ProsperoOuterPfsSignature.SignedBlockTweakFlag" /> | block index (PS5), and
	/// <see cref="F:LibProsperoPkg.PFS.ProsperoOuterBlockKind.Plaintext" /> blocks are left untouched. This is the full
	/// validated PS5 nwonly outer-image scheme (data blocks 0-5 plain, superblock plaintext, signed
	/// metadata blocks 7-10 with bit 47 set). Returns the number of blocks transformed.
	/// </summary>
	public static int Transform(Span<byte> image, ReadOnlySpan<byte> tweakKey, ReadOnlySpan<byte> dataKey, int blockSize, ReadOnlySpan<ProsperoOuterBlockKind> blockKinds, bool encrypt)
	{
		if (tweakKey.Length != 16)
		{
			throw new ArgumentException($"Tweak key must be 16 bytes (was {tweakKey.Length}).", "tweakKey");
		}
		if (dataKey.Length != 16)
		{
			throw new ArgumentException($"Data key must be 16 bytes (was {dataKey.Length}).", "dataKey");
		}
		if (blockSize <= 0 || (blockSize & 0xF) != 0)
		{
			throw new ArgumentOutOfRangeException("blockSize", "Block size must be a positive multiple of 16.");
		}
		int num = (image.Length + blockSize - 1) / blockSize;
		if (blockKinds.Length != num)
		{
			throw new ArgumentException($"blockKinds length ({blockKinds.Length}) must equal the block count ({num}).", "blockKinds");
		}
		using XtsBlockTransform xtsBlockTransform = new XtsBlockTransform(dataKey.ToArray(), tweakKey.ToArray());
		int num2 = 0;
		for (int i = 0; i < num; i++)
		{
			ProsperoOuterBlockKind prosperoOuterBlockKind = blockKinds[i];
			if (prosperoOuterBlockKind != ProsperoOuterBlockKind.Plaintext)
			{
				int num3 = i * blockSize;
				int num4 = Math.Min(blockSize, image.Length - num3);
				if ((num4 & 0xF) != 0)
				{
					throw new ArgumentException($"Block {i} length {num4} is not a multiple of the AES block size (16).", "image");
				}
				ulong sectorNum = ProsperoOuterPfsSignature.BlockSector(i, prosperoOuterBlockKind == ProsperoOuterBlockKind.Signed);
				byte[] array = image.Slice(num3, num4).ToArray();
				xtsBlockTransform.CryptSector(array, sectorNum, encrypt);
				array.CopyTo(image.Slice(num3, num4));
				num2++;
			}
		}
		return num2;
	}

	/// <summary>
	/// Streaming equivalent of the explicit-kind transform. Exactly <paramref name="length" /> bytes
	/// are consumed from the current input position and written at the current output position.
	/// Memory use is bounded by one outer-PFS block, so finalized images larger than 2 GiB do not
	/// need to be materialized in a single managed array.
	/// </summary>
	public static int Transform(Stream input, Stream output, long length, ReadOnlySpan<byte> tweakKey, ReadOnlySpan<byte> dataKey, int blockSize, ReadOnlySpan<ProsperoOuterBlockKind> blockKinds, bool encrypt)
	{
		ArgumentNullException.ThrowIfNull(input, "input");
		ArgumentNullException.ThrowIfNull(output, "output");
		if (!input.CanRead)
		{
			throw new ArgumentException("Input stream must be readable.", "input");
		}
		if (!output.CanWrite)
		{
			throw new ArgumentException("Output stream must be writable.", "output");
		}
		if (input == output)
		{
			throw new ArgumentException("Streaming outer-PFS transform requires distinct input and output streams.");
		}
		if (length < 0)
		{
			throw new ArgumentOutOfRangeException("length");
		}
		if (tweakKey.Length != 16)
		{
			throw new ArgumentException($"Tweak key must be 16 bytes (was {tweakKey.Length}).", "tweakKey");
		}
		if (dataKey.Length != 16)
		{
			throw new ArgumentException($"Data key must be 16 bytes (was {dataKey.Length}).", "dataKey");
		}
		if (blockSize <= 0 || (blockSize & 0xF) != 0)
		{
			throw new ArgumentOutOfRangeException("blockSize", "Block size must be a positive multiple of 16.");
		}
		if ((length & 0xF) != 0L)
		{
			throw new ArgumentException("Outer-PFS length must be a multiple of the AES block size.", "length");
		}
		long num = checked(length + blockSize - 1) / blockSize;
		if (num > int.MaxValue)
		{
			throw new ArgumentOutOfRangeException("length", "Outer-PFS block count exceeds Int32.");
		}
		int num2 = (int)num;
		if (blockKinds.Length != num2)
		{
			throw new ArgumentException($"blockKinds length ({blockKinds.Length}) must equal the block count ({num2}).", "blockKinds");
		}
		using XtsBlockTransform xtsBlockTransform = new XtsBlockTransform(dataKey.ToArray(), tweakKey.ToArray());
		byte[] array = new byte[blockSize];
		int num3 = 0;
		long num4 = length;
		for (int i = 0; i < num2; i++)
		{
			int num5 = checked((int)Math.Min(blockSize, num4));
			input.ReadExactly(array, 0, num5);
			ProsperoOuterBlockKind prosperoOuterBlockKind = blockKinds[i];
			if (prosperoOuterBlockKind != ProsperoOuterBlockKind.Plaintext)
			{
				ulong sectorNum = ProsperoOuterPfsSignature.BlockSector(i, prosperoOuterBlockKind == ProsperoOuterBlockKind.Signed);
				if (num5 == array.Length)
				{
					xtsBlockTransform.CryptSector(array, sectorNum, encrypt);
				}
				else
				{
					byte[] array2 = array.AsSpan(0, num5).ToArray();
					xtsBlockTransform.CryptSector(array2, sectorNum, encrypt);
					array2.CopyTo(array, 0);
				}
				num3++;
			}
			output.Write(array, 0, num5);
			num4 -= num5;
		}
		return num3;
	}

	public static int EncryptInPlace(Span<byte> image, byte[] ekpfs, byte[] seed, int plaintextBlockIndex, int blockSize = 65536)
	{
		var (array, array2) = ProsperoPfsKeys.DeriveImageEncryptionKeys(ekpfs, seed);
		return Transform(image, array, array2, blockSize, plaintextBlockIndex, encrypt: true);
	}

	/// <summary>
	/// Decrypts the outer image in place (inverse of <see cref="M:LibProsperoPkg.PFS.ProsperoOuterPfsImage.EncryptInPlace(System.Span{System.Byte},System.Byte[],System.Byte[],System.Int32,System.Int32)" />).
	/// </summary>
	public static int DecryptInPlace(Span<byte> image, byte[] ekpfs, byte[] seed, int plaintextBlockIndex, int blockSize = 65536)
	{
		var (array, array2) = ProsperoPfsKeys.DeriveImageEncryptionKeys(ekpfs, seed);
		return Transform(image, array, array2, blockSize, plaintextBlockIndex, encrypt: false);
	}

	/// <summary>
	/// Encrypts the outer image in place, deriving the EKPFS (and then the AES-XTS keys) from the
	/// package <paramref name="contentId" /> + <paramref name="passcode" /> and the 16-byte
	/// <paramref name="seed" /> in one step.
	/// </summary>
	public static int EncryptInPlace(Span<byte> image, string contentId, string passcode, byte[] seed, int plaintextBlockIndex, int blockSize = 65536)
	{
		return EncryptInPlace(image, ProsperoPfsKeys.DeriveEkpfs(contentId, passcode), seed, plaintextBlockIndex, blockSize);
	}

	/// <summary>
	/// Decrypts the outer image in place, deriving the keys from the package
	/// <paramref name="contentId" /> + <paramref name="passcode" /> and the <paramref name="seed" />.
	/// </summary>
	public static int DecryptInPlace(Span<byte> image, string contentId, string passcode, byte[] seed, int plaintextBlockIndex, int blockSize = 65536)
	{
		return DecryptInPlace(image, ProsperoPfsKeys.DeriveEkpfs(contentId, passcode), seed, plaintextBlockIndex, blockSize);
	}
}
