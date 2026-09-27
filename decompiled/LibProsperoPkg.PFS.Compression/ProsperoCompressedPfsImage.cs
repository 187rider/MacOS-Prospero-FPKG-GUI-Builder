using System;
using System.Buffers.Binary;
using System.IO;

namespace LibProsperoPkg.PFS.Compression;

/// <summary>
/// Packer/unpacker for the PS5 PFSv3 compression container ("PFSC", Kraken). This is the codec used for the "nwonly" inner image
/// (<c>pfs_image.dat</c>); for the installable zlib PFSC image use
/// <see cref="T:LibProsperoPkg.PFS.ProsperoPfsc" />.
/// </summary>
public static class ProsperoCompressedPfsImage
{
	/// <summary>The default PS5 v3 logical block size (256 KiB), matching "nwonly".</summary>
	public const int DefaultBlockSize = 262144;

	/// <summary>The default Kraken level recorded in the header (7, matching "nwonly").</summary>
	public const int DefaultLevel = 7;

	private const uint PfscMagic = 1129530960u;

	/// <summary>
	/// Compresses an already-prepared inner image into a PS5 Kraken PFSv3 container in memory. Each
	/// block is Kraken-compressed; blocks that do not shrink are stored uncompressed.
	/// </summary>
	/// <param name="image">The raw inner image (e.g. a nested pfs_image payload) to wrap.</param>
	/// <param name="level">The Kraken level recorded in the header. Default 7.</param>
	/// <param name="blockSize">The logical block size. Default 256 KiB. Must be positive.</param>
	/// <returns>The serialized PFSv3 'PFSC' container.</returns>
	public static byte[] Pack(ReadOnlySpan<byte> image, int level = 7, int blockSize = 262144)
	{
		return CompressedPfsFileWriter.WriteCompressed(image, level, blockSize);
	}

	/// <summary>
	/// Wraps an already-prepared inner image into a PS5 Kraken PFSv3 container, storing every block
	/// uncompressed (isBlockCompressed = 0). Useful for incompressible images or deterministic output.
	/// </summary>
	/// <param name="image">The raw inner image to wrap.</param>
	/// <param name="level">The Kraken level recorded in the header. Default 7.</param>
	/// <param name="blockSize">The logical block size. Default 256 KiB. Must be positive.</param>
	/// <returns>The serialized PFSv3 'PFSC' container with stored blocks.</returns>
	public static byte[] PackStored(ReadOnlySpan<byte> image, int level = 7, int blockSize = 262144)
	{
		return CompressedPfsFileWriter.WriteStored(image, level, blockSize);
	}

	/// <summary>
	/// Compresses a prepared inner image file at <paramref name="inputImagePath" /> into a PS5 Kraken
	/// PFSv3 container at <paramref name="outputPath" />.
	/// </summary>
	/// <param name="inputImagePath">A prepared inner image file (e.g. a nested PFS image).</param>
	/// <param name="outputPath">Destination container path.</param>
	/// <param name="level">The Kraken level recorded in the header. Default 7.</param>
	/// <param name="blockSize">The logical block size. Default 256 KiB. Must be positive.</param>
	/// <param name="logger">Optional progress sink.</param>
	/// <returns>Statistics describing the produced container.</returns>
	public static ProsperoCompressedPfsImageResult PackFile(string inputImagePath, string outputPath, int level = 7, int blockSize = 262144, Action<string>? logger = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(inputImagePath, "inputImagePath");
		ArgumentException.ThrowIfNullOrWhiteSpace(outputPath, "outputPath");
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize, "blockSize");
		if (!File.Exists(inputImagePath))
		{
			throw new FileNotFoundException("Input image was not found.", inputImagePath);
		}
		Action<string> action = logger ?? ((Action<string>)((string _) =>
		{
		}));
		long length = new FileInfo(inputImagePath).Length;
		if (length > Array.MaxLength)
		{
			throw new NotSupportedException($"The inner image is {length:N0} bytes; the in-memory Kraken packer supports up to {Array.MaxLength:N0} bytes.");
		}
		string directoryName = Path.GetDirectoryName(Path.GetFullPath(outputPath));
		if (!string.IsNullOrEmpty(directoryName))
		{
			Directory.CreateDirectory(directoryName);
		}
		action($"Packing {Path.GetFileName(inputImagePath)} ({length:N0} bytes) into a PS5 Kraken PFSv3 image...");
		byte[] array = Pack(File.ReadAllBytes(inputImagePath), level, blockSize);
		File.WriteAllBytes(outputPath, array);
		CompressedPfsFile compressedPfsFile = CompressedPfsFile.Parse(array);
		int num = 0;
		foreach (PfsBlock block in compressedPfsFile.Blocks)
		{
			if (!block.IsStored)
			{
				num++;
			}
		}
		int count = compressedPfsFile.Blocks.Count;
		ProsperoCompressedPfsImageResult prosperoCompressedPfsImageResult = new ProsperoCompressedPfsImageResult
		{
			OutputPath = outputPath,
			RawSize = length,
			EncodedSize = array.Length,
			BlockSize = compressedPfsFile.BlockSize,
			BlockCount = count,
			CompressedBlocks = num
		};
		if (prosperoCompressedPfsImageResult.StoredRaw)
		{
			action("Compression produced no benefit; every block was stored uncompressed.");
		}
		else
		{
			action($"Done: {prosperoCompressedPfsImageResult.EncodedSize:N0} bytes ({prosperoCompressedPfsImageResult.GainPercent:F1}% saved, {num}/{count} blocks compressed).");
		}
		return prosperoCompressedPfsImageResult;
	}

	/// <summary>
	/// Decompresses a PS5 Kraken PFSv3 container in memory back to the original image bytes, using the
	/// Kraken decoder.
	/// </summary>
	/// <param name="container">A container produced by <see cref="M:LibProsperoPkg.PFS.Compression.ProsperoCompressedPfsImage.Pack(System.ReadOnlySpan{System.Byte},System.Int32,System.Int32)" /> (or any valid PFSv3 compressed image).</param>
	/// <returns>The reconstructed image.</returns>
	public static byte[] Unpack(byte[] container)
	{
		ArgumentNullException.ThrowIfNull(container, "container");
		return CompressedPfsFile.Parse(container).Decompress();
	}

	/// <summary>
	/// Reverses <see cref="M:LibProsperoPkg.PFS.Compression.ProsperoCompressedPfsImage.PackFile(System.String,System.String,System.Int32,System.Int32,System.Action{System.String})" />: decompresses a PS5 Kraken PFSv3 container back to a flat image.
	/// </summary>
	/// <param name="inputPath">A container produced by this API (or any valid PFSv3 compressed image).</param>
	/// <param name="outputPath">Destination flat image path.</param>
	/// <param name="logger">Optional progress sink.</param>
	/// <returns>The number of logical bytes written.</returns>
	public static long UnpackFile(string inputPath, string outputPath, Action<string>? logger = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(inputPath, "inputPath");
		ArgumentException.ThrowIfNullOrWhiteSpace(outputPath, "outputPath");
		if (!File.Exists(inputPath))
		{
			throw new FileNotFoundException("Container was not found.", inputPath);
		}
		Action<string>? obj = logger ?? ((Action<string>)((string _) =>
		{
		}));
		byte[] array = File.ReadAllBytes(inputPath);
		if (!IsScePfsImage(array))
		{
			throw new InvalidDataException("The input file is not a PFSv3 compressed image (wrong magic/version).");
		}
		byte[] array2 = Unpack(array);
		string directoryName = Path.GetDirectoryName(Path.GetFullPath(outputPath));
		if (!string.IsNullOrEmpty(directoryName))
		{
			Directory.CreateDirectory(directoryName);
		}
		File.WriteAllBytes(outputPath, array2);
		obj($"Unpacked {array2.Length:N0} bytes to {Path.GetFileName(outputPath)}.");
		return array2.LongLength;
	}

	/// <summary>
	/// Returns <c>true</c> when <paramref name="data" /> begins with a PS5 PFSv3/PFSv2 'PFSC'
	/// header. This distinguishes the Kraken container from the zlib PFSC (which carries a
	/// zero word at offset 0x04 and no section count), so callers can pick the right unpacker.
	/// </summary>
	public static bool IsScePfsImage(ReadOnlySpan<byte> data)
	{
		if (data.Length < 8)
		{
			return false;
		}
		if (BinaryPrimitives.ReadUInt32LittleEndian(data) != 1129530960)
		{
			return false;
		}
		ushort num = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4));
		ushort num2 = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6));
		if (num == 2 || num == 3)
		{
			return num2 == 7;
		}
		return false;
	}

	/// <summary>Returns <c>true</c> when the file at <paramref name="path" /> is a PS5 PFSv3 compressed image.</summary>
	public static bool IsScePfsImageFile(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path, "path");
		using FileStream fileStream = File.OpenRead(path);
		Span<byte> span = stackalloc byte[8];
		return fileStream.Read(span) == span.Length && IsScePfsImage(span);
	}

	/// <summary>
	/// In-process self-test: packs <paramref name="image" /> with the Kraken encoder, decodes it
	/// back with the Kraken decoder, and verifies the result is byte-exact. Returns <c>true</c>
	/// on success. This does not require external processes.
	/// </summary>
	/// <param name="image">The image to round-trip.</param>
	/// <param name="level">The Kraken level recorded in the header. Default 7.</param>
	/// <param name="blockSize">The logical block size. Default 256 KiB.</param>
	public static bool ValidateRoundTrip(ReadOnlySpan<byte> image, int level = 7, int blockSize = 262144)
	{
		byte[] array = Pack(image, level, blockSize);
		if (!IsScePfsImage(array))
		{
			return false;
		}
		return CompressedPfsFile.Parse(array).Decompress().AsSpan()
			.SequenceEqual(image);
	}
}
