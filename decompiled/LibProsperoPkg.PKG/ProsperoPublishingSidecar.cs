using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace LibProsperoPkg.PKG;

public static class ProsperoPublishingSidecar
{
	public const string MetadataPrivateKeyFileName = "pkg_meta_rsa_key.pem";

	public const string NapsCmacKeyFileName = "naps_cmac_key.bin";

	public const string NapsPfsImageKeyFileName = "pfs_image_key.bin";

	public const string NapsPfsImageSeedFileName = "pfs_image_seed.bin";

	public const string PublisherImageKeyFileName = "pkg_image_key.bin";

	public const string PublisherEntryKeysFileName = "pkg_entry_keys.bin";

	public const string NapsMeta18FileName = "naps_meta_18.dat";

	public static string DefaultDirectory => Path.GetFullPath(AppContext.BaseDirectory);

	public static string GetPath(string fileName, string? directory = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fileName, "fileName");
		return Path.Combine(Path.GetFullPath(directory ?? DefaultDirectory), fileName);
	}

	public static ProsperoRsaMetadataSigner? TryLoadMetadataSigner(string? directory = null)
	{
		string path = GetPath("pkg_meta_rsa_key.pem", directory);
		if (!File.Exists(path))
		{
			return null;
		}
		return ProsperoRsaMetadataSigner.LoadPem(path, "sidecar:pkg_meta_rsa_key.pem");
	}

	public static byte[]? TryLoadNapsCmacKey(string? directory = null)
	{
		string path = GetPath("naps_cmac_key.bin", directory);
		if (!File.Exists(path))
		{
			return null;
		}
		byte[] array = File.ReadAllBytes(path);
		if (array.Length != 16)
		{
			throw new InvalidDataException($"{"naps_cmac_key.bin"} must contain exactly 16 raw bytes, not {array.Length}.");
		}
		return array;
	}

	public static byte[]? TryLoadNapsPfsImageKey(string? directory = null)
	{
		return TryLoadRawSidecar("pfs_image_key.bin", 32, directory);
	}

	public static byte[]? TryLoadNapsPfsImageSeed(string? directory = null)
	{
		return TryLoadRawSidecar("pfs_image_seed.bin", 16, directory);
	}

	public static byte[]? TryLoadPublisherImageKey(string? directory = null)
	{
		return TryLoadRawSidecar("pkg_image_key.bin", 2048, directory);
	}

	public static byte[]? TryLoadPublisherEntryKeys(string? directory = null)
	{
		return TryLoadRawSidecar("pkg_entry_keys.bin", 2944, directory);
	}

	public static byte[]? TryLoadNapsMeta18(string? directory = null)
	{
		string path = GetPath("naps_meta_18.dat", directory);
		if (!File.Exists(path))
		{
			return null;
		}
		return File.ReadAllBytes(path);
	}

	public static byte[] ReadPublisherImageKey(string packagePath)
	{
		return ReadRawCntEntry(packagePath, EntryId.IMAGE_KEY, 2048, "IMAGE_KEY");
	}

	public static byte[] ReadPublisherEntryKeys(string packagePath)
	{
		return ReadRawCntEntry(packagePath, EntryId.ENTRY_KEYS, 2944, "ENTRY_KEYS");
	}

	private static byte[] ReadRawCntEntry(string packagePath, EntryId entryId, int expectedLength, string displayName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(packagePath, "packagePath");
		using FileStream fileStream = File.OpenRead(packagePath);
		ProsperoPkg prosperoPkg = ProsperoPkgReader.Read(fileStream);
		ProsperoPkgEntry prosperoPkgEntry = prosperoPkg.Entries.SingleOrDefault((ProsperoPkgEntry candidate) => candidate.RawId == (uint)entryId) ?? throw new InvalidDataException("The package does not contain a CNT " + displayName + " entry.");
		if (prosperoPkgEntry.DataSize != expectedLength)
		{
			throw new InvalidDataException($"Publisher CNT {displayName} must contain exactly 0x{expectedLength:X} bytes, not 0x{prosperoPkgEntry.DataSize:X}.");
		}
		long num = checked(((prosperoPkg.Fih == null) ? 0 : ((long)prosperoPkg.Fih.EmbeddedCntOffset)) + prosperoPkgEntry.DataOffset);
		if (num < 0 || num > fileStream.Length - prosperoPkgEntry.DataSize)
		{
			throw new InvalidDataException("The CNT " + displayName + " range is outside the package.");
		}
		byte[] array = new byte[prosperoPkgEntry.DataSize];
		fileStream.Position = num;
		fileStream.ReadExactly(array);
		return array;
	}

	public static byte[]? TryReadNapsMeta18(string packagePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(packagePath, "packagePath");
		checked
		{
			using FileStream fileStream = File.OpenRead(packagePath);
			ProsperoPackageMap prosperoPackageMap = ProsperoPackageArchive.Inspect(fileStream);
			if (prosperoPackageMap.SupplementSize == 0L)
			{
				return null;
			}
			if (prosperoPackageMap.SupplementSize > int.MaxValue)
			{
				throw new InvalidDataException("The package SI segment is too large to inspect in memory.");
			}
			byte[] array = new byte[(int)prosperoPackageMap.SupplementSize];
			fileStream.Position = prosperoPackageMap.SupplementOffset;
			fileStream.ReadExactly(array);
			using MemoryStream stream = new MemoryStream(array, writable: false);
			using ZipArchive zipArchive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
			ZipArchiveEntry zipArchiveEntry = zipArchive.Entries.SingleOrDefault((ZipArchiveEntry entry) => string.Equals(entry.FullName.Replace('\\', '/'), "common/etc/naps_meta_18.dat", StringComparison.OrdinalIgnoreCase));
			if (zipArchiveEntry == null)
			{
				return null;
			}
			if (zipArchiveEntry.Length > int.MaxValue)
			{
				throw new InvalidDataException("The SI naps_meta_18.dat member is too large.");
			}
			using Stream stream2 = zipArchiveEntry.Open();
			using MemoryStream memoryStream = new MemoryStream((int)zipArchiveEntry.Length);
			stream2.CopyTo(memoryStream);
			return memoryStream.ToArray();
		}
	}

	public static IReadOnlyList<string> ExportReusableInputs(string packagePath, string outputDirectory, bool overwrite = false)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory, "outputDirectory");
		byte[] item = ReadPublisherImageKey(packagePath);
		byte[] item2 = ReadPublisherEntryKeys(packagePath);
		byte[] array = TryReadNapsMeta18(packagePath);
		string fullPath = Path.GetFullPath(outputDirectory);
		bool flag;
		using (FileStream stream = File.OpenRead(packagePath))
		{
			flag = ProsperoPkgReader.Read(stream).Fih?.IsOfficial ?? false;
		}
		List<(string, byte[])> list = new List<(string, byte[])>
		{
			(Path.Combine(fullPath, "pkg_entry_keys.bin"), item2),
			(Path.Combine(fullPath, "pkg_image_key.bin"), item)
		};
		if (array != null)
		{
			list.Add((Path.Combine(fullPath, "naps_meta_18.dat"), array));
		}
		if (!overwrite)
		{
			IEnumerable<string> enumerable = list.Select(((string Path, byte[] Data) output) => output.Path);
			if (flag)
			{
				enumerable = enumerable.Concat(new _003C_003Ez__ReadOnlyArray<string>(new string[4]
				{
					Path.Combine(fullPath, "retail_fih_request.sha3"),
					Path.Combine(fullPath, "retail_fih_finalization.bin"),
					Path.Combine(fullPath, "retail_cnt_request.sha3"),
					Path.Combine(fullPath, "retail_cnt_authentication.bin")
				}));
			}
			string text = enumerable.FirstOrDefault(File.Exists);
			if (text != null)
			{
				throw new IOException("Refusing to overwrite existing publisher sidecar: " + text);
			}
		}
		Directory.CreateDirectory(fullPath);
		foreach (var (path, bytes) in list)
		{
			File.WriteAllBytes(path, bytes);
		}
		List<string> list2 = list.Select(((string Path, byte[] Data) output) => output.Path).ToList();
		if (flag)
		{
			list2.AddRange(ProsperoDirectoryRetailFinalizationProvider.ExportFromPackage(packagePath, fullPath, overwrite));
		}
		return list2;
	}

	private static byte[]? TryLoadRawSidecar(string fileName, int expectedLength, string? directory)
	{
		string path = GetPath(fileName, directory);
		if (!File.Exists(path))
		{
			return null;
		}
		byte[] array = File.ReadAllBytes(path);
		if (array.Length != expectedLength)
		{
			throw new InvalidDataException($"{fileName} must contain exactly {expectedLength} raw bytes, not {array.Length}.");
		}
		return array;
	}
}
