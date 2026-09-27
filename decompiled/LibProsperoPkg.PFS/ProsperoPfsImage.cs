using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PFS;

/// <summary>
/// PFS image AES-XTS encryptor/decryptor for PS5. See the file header for the scheme.
/// </summary>
public static class ProsperoPfsImage
{
	/// <summary>AES-XTS sector size used by the PFS image crypto.</summary>
	public const int XtsSectorSize = 4096;

	/// <summary>The PFS mode bit that marks an image AES-XTS encrypted.</summary>
	public const ushort EncryptedModeFlag = 4;

	/// <summary>The PFS mode bit that marks an image HMAC signed.</summary>
	public const ushort SignedModeFlag = 1;

	private const long ModeFieldOffset = 28L;

	private const long UnknownIndexOffset = 876L;

	private const long SeedFieldOffset = 880L;

	/// <summary>The all-zero EKPFS — the standard package key.</summary>
	public static byte[] ZeroEkpfs => new byte[32];

	/// <summary>
	/// Reads the PFS superblock of <paramref name="imagePath" /> and reports the fields
	/// relevant to encryption (version, mode, block size, seed).
	/// </summary>
	public static ProsperoPfsImageInfo Inspect(string imagePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(imagePath, "imagePath");
		if (!File.Exists(imagePath))
		{
			throw new FileNotFoundException("PFS image was not found.", imagePath);
		}
		using FileStream s = File.OpenRead(imagePath);
		PfsHeader pfsHeader = PfsHeader.ReadFromStream(s);
		return new ProsperoPfsImageInfo
		{
			Version = pfsHeader.Version,
			Mode = (ushort)pfsHeader.Mode,
			BlockSize = pfsHeader.BlockSize,
			Seed = (pfsHeader.Seed ?? new byte[16])
		};
	}

	/// <summary>
	/// Returns true when the PFS image at <paramref name="imagePath" /> has the encrypted
	/// mode flag set in its superblock (i.e. its filesystem sectors are AES-XTS encrypted).
	/// </summary>
	/// <param name="imagePath">Path to the PFS image to inspect.</param>
	/// <returns><c>true</c> if the superblock declares the image encrypted; otherwise <c>false</c>.</returns>
	public static bool IsEncrypted(string imagePath)
	{
		return Inspect(imagePath).Encrypted;
	}

	/// <summary>
	/// Encrypts a prepared (plaintext) PFS image in place: writes the encrypted-mode bit and
	/// the seed into the superblock, then AES-XTS-encrypts every filesystem sector after the
	/// header block.
	/// </summary>
	public static ProsperoPfsImageResult EncryptInPlace(string imagePath, ProsperoPfsImageOptions? options = null, Action<string>? logger = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(imagePath, "imagePath");
		return Transform(imagePath, imagePath, encrypt: true, options, logger);
	}

	/// <summary>
	/// Encrypts a prepared (plaintext) PFS image, writing the result to
	/// <paramref name="outputPath" /> (the input is left untouched).
	/// </summary>
	public static ProsperoPfsImageResult Encrypt(string inputImagePath, string outputPath, ProsperoPfsImageOptions? options = null, Action<string>? logger = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(inputImagePath, "inputImagePath");
		ArgumentException.ThrowIfNullOrWhiteSpace(outputPath, "outputPath");
		CopyTo(inputImagePath, outputPath);
		return Transform(outputPath, outputPath, encrypt: true, options, logger);
	}

	/// <summary>
	/// Decrypts an encrypted PFS image in place: AES-XTS-decrypts every filesystem sector
	/// after the header block and clears the encrypted-mode bit. Inverse of <see cref="M:LibProsperoPkg.PFS.ProsperoPfsImage.EncryptInPlace(System.String,LibProsperoPkg.PFS.ProsperoPfsImageOptions,System.Action{System.String})" />.
	/// </summary>
	public static ProsperoPfsImageResult DecryptInPlace(string imagePath, ProsperoPfsImageOptions? options = null, Action<string>? logger = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(imagePath, "imagePath");
		return Transform(imagePath, imagePath, encrypt: false, options, logger);
	}

	/// <summary>
	/// Decrypts an encrypted PFS image, writing the plaintext result to
	/// <paramref name="outputPath" /> (the input is left untouched).
	/// </summary>
	public static ProsperoPfsImageResult Decrypt(string inputImagePath, string outputPath, ProsperoPfsImageOptions? options = null, Action<string>? logger = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(inputImagePath, "inputImagePath");
		ArgumentException.ThrowIfNullOrWhiteSpace(outputPath, "outputPath");
		CopyTo(inputImagePath, outputPath);
		return Transform(outputPath, outputPath, encrypt: false, options, logger);
	}

	/// <summary>
	/// Proves the encrypt/decrypt pair is loss-less for a given image without leaving any
	/// artefacts: encrypts a temporary copy, decrypts it back and compares the filesystem
	/// data (every sector after the plaintext header block) to the original byte-for-byte.
	/// The header block legitimately gains the seed + encrypted-mode metadata, so it is
	/// excluded from the comparison. Used as the self-check that replaces on-hardware
	/// testing for the image crypto.
	/// </summary>
	public static bool VerifyRoundTrip(string plaintextImagePath, ProsperoPfsImageOptions? options = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(plaintextImagePath, "plaintextImagePath");
		string tempFileName = Path.GetTempFileName();
		string tempFileName2 = Path.GetTempFileName();
		try
		{
			ProsperoPfsImageInfo prosperoPfsImageInfo = Inspect(plaintextImagePath);
			Encrypt(plaintextImagePath, tempFileName, options);
			Decrypt(tempFileName, tempFileName2, options);
			return FilesEqual(plaintextImagePath, tempFileName2, prosperoPfsImageInfo.BlockSize);
		}
		finally
		{
			TryDelete(tempFileName);
			TryDelete(tempFileName2);
		}
	}

	private static ProsperoPfsImageResult Transform(string sourcePath, string targetPath, bool encrypt, ProsperoPfsImageOptions? options, Action<string>? logger)
	{
		if (!File.Exists(targetPath))
		{
			throw new FileNotFoundException("PFS image was not found.", targetPath);
		}
		if (options == null)
		{
			options = new ProsperoPfsImageOptions();
		}
		Action<string> action = logger ?? ((Action<string>)((string _) =>
		{
		}));
		byte[] array = ResolveEkpfs(options.Ekpfs);
		using FileStream fileStream = new FileStream(targetPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
		long length = fileStream.Length;
		fileStream.Position = 0L;
		PfsHeader pfsHeader = PfsHeader.ReadFromStream(fileStream);
		uint blockSize = pfsHeader.BlockSize;
		if (blockSize == 0 || blockSize % 4096 != 0)
		{
			throw new InvalidDataException($"PFS block size {blockSize} is not a multiple of the XTS sector size {4096}.");
		}
		if (length % 4096 != 0L)
		{
			throw new InvalidDataException($"PFS image length {length} is not a multiple of the XTS sector size {4096}.");
		}
		byte[] array2 = pfsHeader.Seed ?? new byte[16];
		byte[] array3 = options.Seed ?? ((!(IsAllZero(array2) & encrypt)) ? array2 : (options.DeterministicSeed ? HMACSHA256.HashData(array, "LibProsperoPkg deterministic PFS seed"u8).AsSpan(0, 16).ToArray() : RandomNumberGenerator.GetBytes(16)));
		if (array3.Length != 16)
		{
			throw new ArgumentException("PFS header seed must be exactly 16 bytes.", "options");
		}
		bool flag = (pfsHeader.Mode & PfsMode.Encrypted) != 0;
		if (!encrypt && !flag)
		{
			throw new InvalidDataException("The image is not marked encrypted; nothing to decrypt.");
		}
		ushort mode = (encrypt ? ((ushort)(pfsHeader.Mode | PfsMode.Encrypted)) : ((ushort)((uint)pfsHeader.Mode & 0xFFFFFFFBu)));
		PatchHeader(fileStream, mode, encrypt ? array3 : null);
		var (tweakKey, dataKey) = Crypto.PfsGenEncKey(array, array3, options.NewCrypt);
		using XtsBlockTransform xtsBlockTransform = new XtsBlockTransform(dataKey, tweakKey);
		long num = blockSize / 4096;
		long num2 = length / 4096;
		byte[] array6 = new byte[4096];
		long num3 = 0L;
		long num4 = -1L;
		action($"{(encrypt ? "Encrypting" : "Decrypting")} {Path.GetFileName(targetPath)} ({length:N0} bytes, PFS, block size 0x{blockSize:X})...");
		for (long num5 = num; num5 < num2; num5++)
		{
			long position = (fileStream.Position = num5 * 4096);
			ReadExact(fileStream, array6);
			xtsBlockTransform.CryptSector(array6, (ulong)num5, encrypt);
			fileStream.Position = position;
			fileStream.Write(array6, 0, array6.Length);
			num3++;
			long num7 = ((num2 <= num) ? 100 : ((num5 - num + 1) * 100 / (num2 - num)));
			if (num7 != num4)
			{
				num4 = num7;
				action($"  {num7,3}%");
			}
		}
		fileStream.Flush();
		action($"Done: {num3:N0} sectors {(encrypt ? "encrypted" : "decrypted")} (seed {Convert.ToHexString(array3.AsSpan(0, 4))}...).");
		return new ProsperoPfsImageResult
		{
			OutputPath = targetPath,
			ImageSize = length,
			BlockSize = blockSize,
			SectorsTransformed = num3,
			Seed = array3
		};
	}

	private static void PatchHeader(FileStream fs, ushort mode, byte[]? seed)
	{
		Span<byte> span = stackalloc byte[2];
		BinaryPrimitives.WriteUInt16LittleEndian(span, mode);
		fs.Position = 28L;
		fs.Write(span);
		if (seed != null)
		{
			Span<byte> span2 = stackalloc byte[4];
			fs.Position = 876L;
			fs.Write(span2);
			fs.Position = 880L;
			fs.Write(seed, 0, 16);
		}
	}

	private static byte[] ResolveEkpfs(byte[]? ekpfs)
	{
		if (ekpfs == null)
		{
			return ZeroEkpfs;
		}
		if (ekpfs.Length != 32)
		{
			throw new ArgumentException($"EKPFS must be exactly 32 bytes (was {ekpfs.Length}).", "ekpfs");
		}
		return ekpfs;
	}

	private static bool IsAllZero(byte[] data)
	{
		for (int i = 0; i < data.Length; i++)
		{
			if (data[i] != 0)
			{
				return false;
			}
		}
		return true;
	}

	private static void CopyTo(string sourcePath, string outputPath)
	{
		if (!File.Exists(sourcePath))
		{
			throw new FileNotFoundException("PFS image was not found.", sourcePath);
		}
		string directoryName = Path.GetDirectoryName(Path.GetFullPath(outputPath));
		if (!string.IsNullOrEmpty(directoryName))
		{
			Directory.CreateDirectory(directoryName);
		}
		if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(outputPath), StringComparison.Ordinal))
		{
			File.Copy(sourcePath, outputPath, overwrite: true);
		}
	}

	private static void ReadExact(Stream s, byte[] buffer)
	{
		int num;
		for (int i = 0; i < buffer.Length; i += num)
		{
			num = s.Read(buffer, i, buffer.Length - i);
			if (num == 0)
			{
				throw new EndOfStreamException("Unexpected end of PFS image while reading a sector.");
			}
		}
	}

	private static bool FilesEqual(string a, string b, long fromOffset = 0L)
	{
		FileInfo fileInfo = new FileInfo(a);
		FileInfo fileInfo2 = new FileInfo(b);
		if (fileInfo.Length != fileInfo2.Length)
		{
			return false;
		}
		using FileStream fileStream = fileInfo.OpenRead();
		using FileStream fileStream2 = fileInfo2.OpenRead();
		fileStream.Position = fromOffset;
		fileStream2.Position = fromOffset;
		byte[] array = new byte[65536];
		byte[] array2 = new byte[65536];
		int num;
		while ((num = fileStream.Read(array, 0, array.Length)) > 0)
		{
			int num2;
			for (int i = 0; i < num; i += num2)
			{
				num2 = fileStream2.Read(array2, i, num - i);
				if (num2 == 0)
				{
					return false;
				}
			}
			if (!array.AsSpan(0, num).SequenceEqual(array2.AsSpan(0, num)))
			{
				return false;
			}
		}
		return true;
	}

	private static void TryDelete(string path)
	{
		try
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
		catch
		{
		}
	}
}
