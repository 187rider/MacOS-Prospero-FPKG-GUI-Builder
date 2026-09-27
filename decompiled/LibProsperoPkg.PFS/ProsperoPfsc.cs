using System;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace LibProsperoPkg.PFS;

/// <summary>
/// PFSC image packer/unpacker for PS5 workflows.
/// </summary>
public static class ProsperoPfsc
{
	/// <summary>
	/// Wraps an already-prepared image <paramref name="inputImagePath" /> into a
	/// PFSC-compressed image at <paramref name="outputPath" />.
	/// </summary>
	/// <param name="inputImagePath">A prepared image file (exFAT/UFS image, or an inner PFS image).</param>
	/// <param name="outputPath">Destination PFSC image path.</param>
	/// <param name="options">Compression options. <c>null</c> uses the PS5 defaults.</param>
	/// <param name="logger">Optional progress sink.</param>
	/// <returns>Statistics describing the produced image.</returns>
	public static ProsperoPfscResult PackFile(string inputImagePath, string outputPath, ProsperoPfscOptions? options = null, Action<string>? logger = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(inputImagePath, "inputImagePath");
		ArgumentException.ThrowIfNullOrWhiteSpace(outputPath, "outputPath");
		if (!File.Exists(inputImagePath))
		{
			throw new FileNotFoundException("Input image was not found.", inputImagePath);
		}
		if (options == null)
		{
			options = new ProsperoPfscOptions();
		}
		Action<string> log = logger ?? ((Action<string>)((string _) =>
		{
		}));
		long length = new FileInfo(inputImagePath).Length;
		string directoryName = Path.GetDirectoryName(Path.GetFullPath(outputPath));
		if (!string.IsNullOrEmpty(directoryName))
		{
			Directory.CreateDirectory(directoryName);
		}
		log($"Packing {Path.GetFileName(inputImagePath)} ({length:N0} bytes) into a PFSC image...");
		PfscEncodeStats pfscEncodeStats;
		using (FileStream input = File.OpenRead(inputImagePath))
		{
			using FileStream output = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
			long reported = -1L;
			pfscEncodeStats = PfscEncoder.Encode(input, length, output, options.ToEncoderOptions(), (long produced) =>
			{
				long num = ((length == 0L) ? 100 : (produced * 100 / length));
				if (num != reported)
				{
					reported = num;
					log($"  {num,3}%");
				}
			});
		}
		if (pfscEncodeStats.StoredRaw)
		{
			log("Compression produced no benefit; the image was stored uncompressed.");
		}
		else
		{
			log($"Done: {pfscEncodeStats.EncodedSize:N0} bytes ({pfscEncodeStats.GainPercent:F1}% saved, {pfscEncodeStats.CompressedBlocks}/{pfscEncodeStats.BlockCount} blocks compressed).");
		}
		return new ProsperoPfscResult
		{
			OutputPath = outputPath,
			RawSize = pfscEncodeStats.RawSize,
			EncodedSize = pfscEncodeStats.EncodedSize,
			StoredRaw = pfscEncodeStats.StoredRaw
		};
	}

	/// <summary>
	/// Reverses <see cref="M:LibProsperoPkg.PFS.ProsperoPfsc.PackFile(System.String,System.String,LibProsperoPkg.PFS.ProsperoPfscOptions,System.Action{System.String})" />: decompresses a PFSC image back to a flat image file.
	/// </summary>
	/// <param name="pfscPath">A PFSC image produced by this tool (or any PFSC image).</param>
	/// <param name="outputPath">Destination flat image path.</param>
	/// <param name="logger">Optional progress sink.</param>
	/// <returns>The number of logical bytes written.</returns>
	public static long Unpack(string pfscPath, string outputPath, Action<string>? logger = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(pfscPath, "pfscPath");
		ArgumentException.ThrowIfNullOrWhiteSpace(outputPath, "outputPath");
		if (!File.Exists(pfscPath))
		{
			throw new FileNotFoundException("PFSC image was not found.", pfscPath);
		}
		if (!IsPfsc(pfscPath))
		{
			throw new InvalidDataException("The input file is not a PFSC image (missing 'PFSC' magic).");
		}
		Action<string> action = logger ?? ((Action<string>)((string _) =>
		{
		}));
		string directoryName = Path.GetDirectoryName(Path.GetFullPath(outputPath));
		if (!string.IsNullOrEmpty(directoryName))
		{
			Directory.CreateDirectory(directoryName);
		}
		long num = 0L;
		using (MemoryMappedFile memoryMappedFile = MemoryMappedFile.CreateFromFile(pfscPath, FileMode.Open, null, 0L, MemoryMappedFileAccess.Read))
		{
			using MemoryMappedViewAccessor va = memoryMappedFile.CreateViewAccessor(0L, 0L, MemoryMappedFileAccess.Read);
			using PFSCReader pFSCReader = new PFSCReader(va);
			using FileStream fileStream = File.Create(outputPath);
			long dataLength = pFSCReader.DataLength;
			byte[] array = new byte[pFSCReader.SectorSize];
			long num2 = 0L;
			while (num2 < dataLength)
			{
				int num3 = (int)Math.Min(array.Length, dataLength - num2);
				pFSCReader.Read(num2, array, 0, num3);
				fileStream.Write(array, 0, num3);
				num2 += num3;
				num += num3;
			}
		}
		action($"Unpacked {num:N0} bytes to {Path.GetFileName(outputPath)}.");
		return num;
	}

	/// <summary>Returns true when the file at <paramref name="path" /> begins with the PFSC magic.</summary>
	public static bool IsPfsc(string path)
	{
		using FileStream fileStream = File.OpenRead(path);
		Span<byte> buffer = stackalloc byte[4];
		if (fileStream.Read(buffer) != 4)
		{
			return false;
		}
		return buffer[0] == 80 && buffer[1] == 70 && buffer[2] == 83 && buffer[3] == 67;
	}
}
