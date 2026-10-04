using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LibProsperoPkg.PFS;
using LibProsperoPkg.PFS.Compression;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PKG;

public static class ProsperoPackageArchive
{
	public const int OuterBlockSize = 65536;

	public static bool VerifyCntMetadataSignature(string packagePath)
	{
		checked
		{
			using FileStream fileStream = File.OpenRead(packagePath);
			ProsperoPkg prosperoPkg = ProsperoPkgReader.Read(fileStream);
			long num = ((prosperoPkg.Fih == null) ? 0 : ((long)prosperoPkg.Fih.EmbeddedCntOffset));
			byte[] array = ReadRange(fileStream, num, 4096);
			byte[] array2 = ReadRange(fileStream, num + 4096, 384);
			ulong num2 = BinaryPrimitives.ReadUInt64BigEndian(array.AsSpan(1040, 8));
			if (num2 != 0L && num2 != 65536)
			{
				BinaryPrimitives.WriteUInt64BigEndian(array.AsSpan(1040, 8), 65536uL);
			}
			return ProsperoPublisherRsa.VerifyCntHeaderWrap(array, array2);
		}
	}

	public static ProsperoPackageValidationReport ValidatePackage(string packagePath, bool computeSha256 = false, ProsperoPublisherImageMode? expectedMode = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(packagePath, nameof(packagePath));
		if (!File.Exists(packagePath))
		{
			ProsperoPackageValidationReport missingReport = new ProsperoPackageValidationReport
			{
				PackagePath = packagePath,
				IsValid = false
			};
			missingReport.Errors.Add($"Package file does not exist: {packagePath}");
			return missingReport;
		}

		using FileStream stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4 * 1024 * 1024, FileOptions.RandomAccess);
		return ValidatePackage(stream, packagePath, computeSha256, expectedMode);
	}

	public static ProsperoPackageValidationReport ValidatePackage(Stream stream, string? packagePath = null, bool computeSha256 = false, ProsperoPublisherImageMode? expectedMode = null)
	{
		ArgumentNullException.ThrowIfNull(stream, nameof(stream));
		ProsperoPackageValidationReport report = new ProsperoPackageValidationReport
		{
			PackagePath = packagePath ?? string.Empty,
			FileSize = stream.Length
		};

		if (stream.Length < 4096)
		{
			report.Errors.Add("Package file is too small to contain a valid FIH or CNT header.");
			report.IsValid = false;
			return report;
		}

		long originalPos = stream.Position;
		try
		{
			stream.Position = 0;
			ProsperoPkgType? type = ProsperoPkgReader.DetectType(stream);
			report.ContainerType = type?.ToString() ?? "Unknown";

			Span<byte> fihBuffer = stackalloc byte[48];
			stream.Position = 0;
			stream.ReadExactly(fihBuffer);

			bool isFih = fihBuffer.Slice(0, 4).SequenceEqual("\u007fFIH"u8);
			bool isCnt = fihBuffer.Slice(0, 4).SequenceEqual("\u007fCNT"u8);

			if (!isFih && !isCnt)
			{
				report.Errors.Add("Package does not begin with valid \\x7fFIH or \\x7fCNT magic.");
				report.IsValid = false;
				return report;
			}

			if (isFih)
			{
				report.SignedByte = fihBuffer[5];
				if (report.SignedByte != 0 && report.SignedByte != 128)
				{
					report.Warnings.Add($"FIH signed byte is 0x{report.SignedByte:X2} (expected 0x00 for debug/fpkg or 0x80 for retail).");
				}

				long outerOffset = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(fihBuffer.Slice(32, 8)));
				if (outerOffset < 0 || outerOffset + 896 > stream.Length)
				{
					report.Errors.Add("FIH header contains an invalid outer superblock offset (0x" + outerOffset.ToString("X") + ").");
				}
				else
				{
					stream.Position = outerOffset + 28;
					Span<byte> modeSpan = stackalloc byte[2];
					stream.ReadExactly(modeSpan);
					report.OuterMode = BinaryPrimitives.ReadUInt16LittleEndian(modeSpan);
					if (report.OuterMode != 13) // 0x000D
					{
						report.Warnings.Add($"Outer PFS mode is 0x{report.OuterMode:X4} (expected 0x000D for publisher outer PFS).");
					}

					stream.Position = outerOffset + 880;
					byte[] seedBytes = new byte[16];
					stream.ReadExactly(seedBytes);
					report.SeedMarker = Encoding.ASCII.GetString(seedBytes);

					if (expectedMode == ProsperoPublisherImageMode.PlaintextNoAuth &&
					    !string.Equals(report.SeedMarker, "PPRPLAIN-NOAUTH!", StringComparison.Ordinal))
					{
						report.Errors.Add("The outer PFS does not contain the expected PPRPLAIN-NOAUTH! seed marker.");
					}
				}
			}

			// Package inspection and map check
			ProsperoPackageMap map;
			try
			{
				map = Inspect(stream);
			}
			catch (Exception ex)
			{
				report.Errors.Add("Package map inspection failed: " + ex.Message);
				report.IsValid = false;
				return report;
			}

			if (isFih)
			{
				if (map.OuterPfsOffset < 65536 || map.OuterPfsSize <= 0)
				{
					report.Errors.Add("Invalid outer PFS segment geometry in package map.");
				}
				if (map.CntOffset != map.OuterPfsOffset + map.OuterPfsSize)
				{
					report.Errors.Add("CNT offset does not immediately follow the outer PFS segment.");
				}
			}

			// Read Package CNT entries
			ProsperoPkg pkg;
			try
			{
				pkg = ProsperoPkgReader.Read(stream);
			}
			catch (Exception ex)
			{
				report.Errors.Add("Failed reading package CNT container: " + ex.Message);
				report.IsValid = false;
				return report;
			}

			report.ContentId = pkg.Header?.ContentId ?? string.Empty;
			if (string.IsNullOrWhiteSpace(report.ContentId))
			{
				report.Errors.Add("Package CNT header is missing Content ID.");
			}

			// Validate outer PFS superblock ICV
			if (isFih && map.OuterSuperblockIndex >= 0)
			{
				long sbOffset = map.OuterPfsOffset + (long)map.OuterSuperblockIndex * 65536L;
				if (sbOffset + 65536L <= stream.Length)
				{
					stream.Position = sbOffset;
					byte[] sb = new byte[65536];
					stream.ReadExactly(sb);
					byte[] actualIcv = ProsperoOuterPfsSignature.ComputeSuperblockIcv(sb);
					byte[] expectedIcv = sb.AsSpan(896, 32).ToArray();
					if (!actualIcv.AsSpan().SequenceEqual(expectedIcv))
					{
						report.Errors.Add("Outer PFS superblock ICV mismatch. Integrity check vector is invalid.");
					}
					else
					{
						report.OuterSuperblockValid = true;
					}
				}
				else
				{
					report.Errors.Add("Superblock offset is beyond the end of the stream.");
				}
			}

			// Validate param.json
			long baseOffset = (long)(pkg.Fih?.EmbeddedCntOffset ?? 0UL);
			ProsperoPkgEntry? paramEntry = pkg.Entries.FirstOrDefault(e => e.RawId == 0x2000 || string.Equals(e.Name, "param.json", StringComparison.OrdinalIgnoreCase));
			if (paramEntry != null)
			{
				stream.Position = baseOffset + paramEntry.DataOffset;
				byte[] pdata = new byte[paramEntry.DataSize];
				stream.ReadExactly(pdata);
				try
				{
					using JsonDocument doc = JsonDocument.Parse(pdata);
					JsonElement root = doc.RootElement;
					if (root.TryGetProperty("contentId", out JsonElement cidProp))
					{
						string pCid = cidProp.GetString() ?? string.Empty;
						if (!string.Equals(pCid, report.ContentId, StringComparison.OrdinalIgnoreCase))
						{
							report.Errors.Add($"param.json contentId '{pCid}' does not match package contentId '{report.ContentId}'.");
						}
					}
					else
					{
						report.Errors.Add("param.json is missing required 'contentId' property.");
					}

					if (root.TryGetProperty("sdkVersion", out JsonElement sdkProp))
					{
						report.SdkVersion = sdkProp.GetString();
						if (ulong.TryParse(report.SdkVersion?.Replace("0x", string.Empty), System.Globalization.NumberStyles.HexNumber, null, out ulong sdkVal))
						{
							report.IsDownpatched = sdkVal <= 0x0400000000000000uL;
							if (!report.IsDownpatched)
							{
								report.Warnings.Add($"param.json sdkVersion is {report.SdkVersion} (> 0x0400000000000000). On FW 4.xx consoles, this title will fail to launch unless downpatched.");
							}
						}
					}

					if (root.TryGetProperty("requiredSystemSoftwareVersion", out JsonElement reqFwProp))
					{
						report.RequiredSystemSoftwareVersion = reqFwProp.GetString();
						if (!string.Equals(report.RequiredSystemSoftwareVersion, "0x0100000000000000", StringComparison.OrdinalIgnoreCase))
						{
							report.Warnings.Add($"requiredSystemSoftwareVersion is '{report.RequiredSystemSoftwareVersion}' (recommended: 0x0100000000000000 for cross-firmware compatibility).");
						}
					}
				}
				catch (Exception ex)
				{
					report.Errors.Add("Failed parsing param.json: " + ex.Message);
				}
			}
			else
			{
				report.Warnings.Add("Package does not contain param.json.");
			}

			// Validate playgo-chunk.dat
			ProsperoPkgEntry? playgoEntry = pkg.Entries.FirstOrDefault(e => e.RawId == 0x1001 || string.Equals(e.Name, "playgo-chunk.dat", StringComparison.OrdinalIgnoreCase));
			if (playgoEntry != null)
			{
				stream.Position = baseOffset + playgoEntry.DataOffset;
				byte[] pdata = new byte[playgoEntry.DataSize];
				stream.ReadExactly(pdata);
				if (pdata.Length >= 100 && pdata.AsSpan(0, 4).SequenceEqual("plgx"u8))
				{
					string pCid = Encoding.ASCII.GetString(pdata, 64, Math.Min(36, pdata.Length - 64)).TrimEnd('\0');
					report.PlayGoContentId = pCid;
					ulong mask = BinaryPrimitives.ReadUInt64LittleEndian(pdata.AsSpan(56, 8));
					report.PlayGoChunkMask = mask;
					ulong chunkSize = pdata.Length >= 336 ? BinaryPrimitives.ReadUInt64LittleEndian(pdata.AsSpan(328, 8)) : ulong.MaxValue;
					if (!string.Equals(pCid, report.ContentId, StringComparison.OrdinalIgnoreCase))
					{
						report.Errors.Add($"playgo-chunk.dat Content ID mismatch: expected '{report.ContentId}', found '{pCid}'. PlayGo service will reject this package.");
						report.PlayGoValid = false;
					}
					else if (mask == 0 || mask == 0x4000000000000000uL)
					{
						report.Errors.Add($"playgo-chunk.dat chunk mask is corrupted (0x{mask:X16}).");
						report.PlayGoValid = false;
					}
					else if (pdata.Length >= 336 && chunkSize == 0 && (report.InnerFileCount > 0 || isFih))
					{
						report.Errors.Add("playgo-chunk.dat Chunk #0 size is 0 bytes (corrupted PlayGo chunk extent).");
						report.PlayGoValid = false;
					}
					else
					{
						report.PlayGoValid = true;
					}
				}
				else
				{
					report.Errors.Add("playgo-chunk.dat has invalid plgx header or is truncated.");
					report.PlayGoValid = false;
				}
			}
			else
			{
				report.PlayGoValid = true;
			}

			// Validate outer PFS & NAPS layout
			if (isFih && map.OuterPfsSize > 0)
			{
				try
				{
					using var r2 = new LibProsperoPkg.Util.StreamReader(stream, map.OuterPfsOffset);
					var pfs = new PfsReader(r2, 0uL, null, null, null, (long)map.OuterSuperblockIndex * 65536L, encryptedDataAlreadyDecrypted: true);
					var files = pfs.GetAllFiles().ToList();
					report.InnerFileCount = files.Count;
					var napsFile = files.FirstOrDefault(f => string.Equals(Path.GetFileName(f.FullName), "naps_pkg_layout.dat", StringComparison.OrdinalIgnoreCase));
					if (napsFile != null)
					{
						report.HasNapsLayout = true;
						byte[] napsBytes = napsFile.ReadAllBytes();
						var doc = ProsperoNapsLayout.Parse(napsBytes);
						report.InnerFileCount = doc.Counts.NumFiles;
						var plan = LibProsperoPkg.PFS.Compression.ProsperoNapsImage.BuildPlan(doc);
						report.NapsSpanCount = plan.Spans.Count;
					}
				}
				catch (Exception ex)
				{
					report.Errors.Add("Outer PFS / NAPS layout validation error: " + ex.Message);
				}
			}

			if (computeSha256)
			{
				stream.Position = 0;
				report.Sha256 = Convert.ToHexString(SHA256.HashData(stream));
			}
		}
		finally
		{
			try { stream.Position = originalPos; } catch { }
		}

		report.IsValid = report.Errors.Count == 0;
		return report;
	}

	public static ProsperoPackageMap Inspect(string path)
	{
		using FileStream input = File.OpenRead(path);
		return Inspect(input);
	}

	public static ProsperoPackageMap Inspect(Stream input)
	{
		ArgumentNullException.ThrowIfNull(input, "input");
		if (!input.CanRead || !input.CanSeek)
		{
			throw new ArgumentException("Package stream must be readable and seekable.", "input");
		}
		ProsperoPkg prosperoPkg = ProsperoPkgReader.Read(input);
		ProsperoPkgHeader prosperoPkgHeader = prosperoPkg.Header ?? throw new InvalidDataException("CNT header is unavailable.");
		long val = 1440L;
		checked
		{
			val = Math.Max(val, prosperoPkgHeader.EntryTableOffset + prosperoPkg.Entries.Count * 32);
			if (prosperoPkgHeader.BodyOffset > long.MaxValue || prosperoPkgHeader.BodySize > long.MaxValue)
			{
				throw new InvalidDataException("CNT body range exceeds Int64.");
			}
			val = Math.Max(val, (long)prosperoPkgHeader.BodyOffset + (long)prosperoPkgHeader.BodySize);
			foreach (ProsperoPkgEntry entry in prosperoPkg.Entries)
			{
				val = Math.Max(val, unchecked((long)entry.DataOffset) + unchecked((long)entry.DataSize));
			}
			val = AlignUp(val, 16);
		}
		if (prosperoPkg.Fih == null)
		{
			RequireRange(input.Length, 0L, val, "CNT");
			return new ProsperoPackageMap(0L, 0L, 0L, 0L, 0L, val, val, input.Length - val, -1);
		}
		ProsperoFihHeader fih = prosperoPkg.Fih;
		if (fih.PfsImageOffset > long.MaxValue || fih.PfsImageSize > long.MaxValue || fih.EmbeddedCntOffset > long.MaxValue)
		{
			throw new InvalidDataException("Package ranges exceed Int64.");
		}
		long pfsImageOffset = (long)fih.PfsImageOffset;
		long pfsImageSize = (long)fih.PfsImageSize;
		long embeddedCntOffset = (long)fih.EmbeddedCntOffset;
		RequireRange(input.Length, pfsImageOffset, pfsImageSize, "outer PFS");
		long num;
		int outerSuperblockIndex;
		checked
		{
			if (embeddedCntOffset != pfsImageOffset + pfsImageSize)
			{
				throw new InvalidDataException("FIH CNT offset does not immediately follow the outer PFS image.");
			}
			RequireRange(input.Length, embeddedCntOffset, val, "CNT");
			num = embeddedCntOffset + val;
			outerSuperblockIndex = ResolveSuperblockIndex(fih, (int)unchecked(pfsImageSize / 65536));
		}
		return new ProsperoPackageMap(0L, pfsImageOffset, pfsImageOffset, pfsImageSize, embeddedCntOffset, val, num, input.Length - num, outerSuperblockIndex);
	}

	public static void Split(Stream input, Stream outerPfs, Stream cnt, Stream? supplement = null)
	{
		ProsperoPackageMap prosperoPackageMap = Inspect(input);
		CopyRange(input, outerPfs, prosperoPackageMap.OuterPfsOffset, prosperoPackageMap.OuterPfsSize);
		CopyRange(input, cnt, prosperoPackageMap.CntOffset, prosperoPackageMap.CntSize);
		if (supplement != null)
		{
			CopyRange(input, supplement, prosperoPackageMap.SupplementOffset, prosperoPackageMap.SupplementSize);
		}
	}

	public static byte[] DecryptOuterPfs(string packagePath, string passcode)
	{
		using FileStream fileStream = File.OpenRead(packagePath);
		ProsperoPkg package = ProsperoPkgReader.Read(fileStream);
		ProsperoPackageMap map = Inspect(fileStream);
		if (map.OuterPfsSize > int.MaxValue)
		{
			throw new InvalidDataException("Outer PFS is too large for the in-memory decryptor.");
		}
		using MemoryStream memoryStream = new MemoryStream(checked((int)map.OuterPfsSize));
		DecryptOuterPfs(fileStream, memoryStream, package, map, passcode);
		return memoryStream.ToArray();
	}

	public static void DecryptOuterPfs(string packagePath, string outputPath, string passcode)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(packagePath, "packagePath");
		ArgumentException.ThrowIfNullOrWhiteSpace(outputPath, "outputPath");
		EnsureDistinctPaths(packagePath, outputPath);
		string fullPath = Path.GetFullPath(outputPath);
		string? obj = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
		Directory.CreateDirectory(obj);
		string text = Path.Combine(obj, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
		try
		{
			using FileStream fileStream = File.OpenRead(packagePath);
			ProsperoPkg package = ProsperoPkgReader.Read(fileStream);
			ProsperoPackageMap map = Inspect(fileStream);
			using (FileStream output = new FileStream(text, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1048576, FileOptions.SequentialScan))
			{
				DecryptOuterPfs(fileStream, output, package, map, passcode);
			}
			File.Move(text, fullPath, overwrite: true);
		}
		finally
		{
			TryDelete(text);
		}
	}

	public static IReadOnlyList<string> ExtractOuterFiles(string packagePath, string outputDirectory, string passcode, bool decompress = false)
	{
		Directory.CreateDirectory(outputDirectory);
		string text = Path.Combine(Path.GetFullPath(outputDirectory), ".libprospero-outer-" + Guid.NewGuid().ToString("N") + ".tmp");
		try
		{
			DecryptOuterPfs(packagePath, text, passcode);
			ProsperoPackageMap prosperoPackageMap = Inspect(packagePath);
			using FileStream s = new FileStream(text, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.RandomAccess);
			using LibProsperoPkg.Util.StreamReader r = new LibProsperoPkg.Util.StreamReader(s, 0L);
			PfsReader pfsReader = new PfsReader(r, 0uL, null, null, null, (long)prosperoPackageMap.OuterSuperblockIndex * 65536L, encryptedDataAlreadyDecrypted: true);
			List<string> list = new List<string>();
			foreach (PfsReader.File allFile in pfsReader.GetAllFiles())
			{
				string text2 = NormalizeRelativePath(allFile.FullName);
				string path = SafeTarget(outputDirectory, text2);
				Directory.CreateDirectory(Path.GetDirectoryName(path));
				allFile.Save(path, decompress);
				list.Add(text2);
			}
			return list;
		}
		finally
		{
			TryDelete(text);
		}
	}

	public static byte[] DecodeInnerPfs(string packagePath, string passcode)
	{
		byte[] array = DecryptOuterPfs(packagePath, passcode);
		int outerSuperblockIndex = ResolveSuperblockIndex(ProsperoPkgReader.Read(packagePath).Fih, array.Length / 65536);
		using MemoryStream outerImage = new MemoryStream(array, writable: false);
		using MemoryStream memoryStream = new MemoryStream();
		DecodeInnerPfs(outerImage, outerSuperblockIndex, memoryStream);
		return memoryStream.ToArray();
	}

	public static void DecodeInnerPfs(string packagePath, string outputPath, string passcode)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(packagePath, "packagePath");
		ArgumentException.ThrowIfNullOrWhiteSpace(outputPath, "outputPath");
		EnsureDistinctPaths(packagePath, outputPath);
		string fullPath = Path.GetFullPath(outputPath);
		string? obj = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
		Directory.CreateDirectory(obj);
		string text = Path.Combine(obj, ".libprospero-outer-" + Guid.NewGuid().ToString("N") + ".tmp");
		string text2 = Path.Combine(obj, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
		try
		{
			DecryptOuterPfs(packagePath, text, passcode);
			ProsperoPackageMap prosperoPackageMap = Inspect(packagePath);
			using FileStream outerImage = new FileStream(text, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.RandomAccess);
			using (FileStream destination = new FileStream(text2, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1048576, FileOptions.RandomAccess))
			{
				DecodeInnerPfs(outerImage, prosperoPackageMap.OuterSuperblockIndex, destination);
			}
			File.Move(text2, fullPath, overwrite: true);
		}
		finally
		{
			TryDelete(text2);
			TryDelete(text);
		}
	}

	public static IReadOnlyList<string> ExtractInnerFiles(string packagePath, string outputDirectory, string passcode, bool decompressFiles = true)
	{
		Directory.CreateDirectory(outputDirectory);
		string text = Path.Combine(Path.GetFullPath(outputDirectory), ".libprospero-inner-" + Guid.NewGuid().ToString("N") + ".tmp");
		try
		{
			DecodeInnerPfs(packagePath, text, passcode);
			using FileStream fileStream = new FileStream(text, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.RandomAccess);
			long num = LocateSuperblock(fileStream);
			if (num < 0)
			{
				throw new InvalidDataException("The NAPS logical stream does not contain an inner PPR-PFS superblock.");
			}
			using LibProsperoPkg.Util.StreamReader r = new LibProsperoPkg.Util.StreamReader(fileStream, 0L);
			PfsReader pfsReader = new PfsReader(r, 0uL, null, null, null, num, encryptedDataAlreadyDecrypted: true);
			List<string> list = new List<string>();
			foreach (PfsReader.File allFile in pfsReader.GetAllFiles())
			{
				string text2 = NormalizeRelativePath(allFile.FullName);
				if (text2.StartsWith("uroot/", StringComparison.Ordinal))
				{
					text2 = text2.Substring("uroot/".Length);
				}
				string path = SafeTarget(outputDirectory, text2);
				Directory.CreateDirectory(Path.GetDirectoryName(path));
				allFile.Save(path, decompressFiles);
				list.Add(text2);
			}
			return list;
		}
		finally
		{
			TryDelete(text);
		}
	}

	public static IReadOnlyList<string> ExtractCntEntries(string packagePath, string outputDirectory, bool includeEncrypted = true)
	{
		return ExtractCntEntriesCore(packagePath, outputDirectory, null, includeEncrypted);
	}

	public static IReadOnlyList<string> ExtractCntEntries(string packagePath, string outputDirectory, string passcode, bool includeEncrypted = true)
	{
		if (passcode == null || passcode.Length != 32)
		{
			throw new ArgumentException("Passcode must be exactly 32 characters.", "passcode");
		}
		return ExtractCntEntriesCore(packagePath, outputDirectory, passcode, includeEncrypted);
	}

	private static IReadOnlyList<string> ExtractCntEntriesCore(string packagePath, string outputDirectory, string? passcode, bool includeEncrypted)
	{
		using FileStream fileStream = File.OpenRead(packagePath);
		ProsperoPkg prosperoPkg = ProsperoPkgReader.Read(fileStream);
		ProsperoPkgHeader prosperoPkgHeader = prosperoPkg.Header ?? throw new InvalidDataException("Package has no CNT header.");
		long num = ((prosperoPkg.Fih == null) ? 0 : checked((long)prosperoPkg.Fih.EmbeddedCntOffset));
		bool publisherProfile = prosperoPkg.Entries.Any((ProsperoPkgEntry e) => e.RawId == 16 && e.DataSize >= 2944);
		Directory.CreateDirectory(outputDirectory);
		List<string> list = new List<string>();
		foreach (ProsperoPkgEntry entry in prosperoPkg.Entries)
		{
			if (includeEncrypted || !entry.Encrypted)
			{
				string value = null;
				EntryNames.IdToName.TryGetValue((EntryId)entry.RawId, out value);
				string text;
				if (string.IsNullOrWhiteSpace(entry.Name))
				{
					text = ((!string.IsNullOrWhiteSpace(value)) ? NormalizeRelativePath(value) : $"entry-{entry.RawId:x8}.bin");
				}
				else
				{
					text = NormalizeRelativePath(entry.Name);
				}
				string path = SafeTarget(outputDirectory, text);
				Directory.CreateDirectory(Path.GetDirectoryName(path));
				byte[] array = checked(ReadRange(size: entry.Encrypted ? ((int)((entry.DataSize + 15) & 0xFFFFFFF0u)) : ((int)entry.DataSize), input: fileStream, offset: num + entry.DataOffset));
				if (entry.Encrypted && passcode != null)
				{
					array = Entry.Decrypt(meta: new MetaEntry
					{
						id = (EntryId)entry.RawId,
						NameTableOffset = entry.NameTableOffset,
						Flags1 = entry.Flags1,
						Flags2 = entry.Flags2,
						DataOffset = entry.DataOffset,
						DataSize = entry.DataSize
					}, entryBytes: array, contentId: prosperoPkgHeader.ContentId, passcode: passcode, publisherProfile: publisherProfile);
				}
				else if (array.Length != entry.DataSize)
				{
					Array.Resize(ref array, checked((int)entry.DataSize));
				}
				File.WriteAllBytes(path, array);
				list.Add(text);
			}
		}
		return list;
	}

	public static IReadOnlyList<string> ExtractSiEntries(string packagePath, string outputDirectory)
	{
		using FileStream input = File.OpenRead(packagePath);
		ProsperoPackageMap prosperoPackageMap = Inspect(input);
		if (prosperoPackageMap.SupplementSize <= 0)
		{
			return Array.Empty<string>();
		}
		if (prosperoPackageMap.SupplementSize > int.MaxValue)
		{
			throw new InvalidDataException("SI segment is too large for the in-memory ZIP reader.");
		}
		using MemoryStream stream = new MemoryStream(ReadRange(input, prosperoPackageMap.SupplementOffset, checked((int)prosperoPackageMap.SupplementSize)), writable: false);
		using ZipArchive zipArchive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
		Directory.CreateDirectory(outputDirectory);
		List<string> list = new List<string>();
		foreach (ZipArchiveEntry entry in zipArchive.Entries)
		{
			if (string.IsNullOrEmpty(entry.Name))
			{
				continue;
			}
			string text = NormalizeRelativePath(entry.FullName);
			string path = SafeTarget(outputDirectory, text);
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			using Stream stream2 = entry.Open();
			using FileStream destination = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
			stream2.CopyTo(destination);
			list.Add(text);
		}
		return list;
	}

	private static void DecryptOuterPfs(Stream input, Stream output, ProsperoPkg package, ProsperoPackageMap map, string passcode)
	{
		if (!input.CanRead || !input.CanSeek)
		{
			throw new ArgumentException("Package stream must be readable and seekable.", "input");
		}
		if (!output.CanWrite)
		{
			throw new ArgumentException("Decrypted output stream must be writable.", "output");
		}
		ProsperoFihHeader fih = package.Fih ?? throw new InvalidDataException("A finalized FIH package is required.");
		ProsperoPkgHeader prosperoPkgHeader = package.Header ?? throw new InvalidDataException("Embedded CNT header is unavailable.");
		if (map.OuterPfsSize <= 0 || map.OuterPfsSize % 65536 != 0L)
		{
			throw new InvalidDataException("Outer PFS is empty or not block aligned.");
		}
		long num = map.OuterPfsSize / 65536;
		if (num > int.MaxValue)
		{
			throw new InvalidDataException("Outer PFS block count exceeds Int32.");
		}
		int blocks = (int)num;
		int outerSuperblockIndex = map.OuterSuperblockIndex;
		byte[] array;
		checked
		{
			array = ReadRange(input, map.OuterPfsOffset + unchecked((long)outerSuperblockIndex) * 65536L, 65536);
			ValidateSuperblockShape(array, blocks);
			byte[] array2 = array.AsSpan(896, 32).ToArray();
			if (!MemoryExtensions.SequenceEqual(other: (ReadOnlySpan<byte>)ProsperoOuterPfsSignature.ComputeSuperblockIcv(array), span: (ReadOnlySpan<byte>)array2.AsSpan()))
			{
				throw new InvalidDataException("Outer PFS superblock ICV mismatch.");
			}
		}
		PfsMode pfsMode = (PfsMode)BinaryPrimitives.ReadUInt16LittleEndian(array.AsSpan(28, 2));
		PfsMode pfsMode2 = PfsMode.Signed | PfsMode.Encrypted | PfsMode.UnknownFlagAlwaysSet;
		byte[] array3 = array.AsSpan(880, 16).ToArray();
		if (pfsMode == pfsMode2 && ((ReadOnlySpan<byte>)array3.AsSpan()).SequenceEqual(ProsperoOuterPfsBuilder.PlaintextNoAuthSeedMarker))
		{
			CopyRange(input, output, map.OuterPfsOffset, map.OuterPfsSize);
			output.Flush();
			return;
		}
		if (pfsMode != pfsMode2)
		{
			throw new InvalidDataException($"Unsupported outer PFS mode 0x{(ushort)pfsMode:X4}; expected 0x000D.");
		}
		(byte[] TweakKey, byte[] DataKey) tuple = ProsperoPfsKeys.DeriveImageEncryptionKeys(ProsperoPfsKeys.DeriveEkpfs(prosperoPkgHeader.ContentId, passcode), array3);
		byte[] item = tuple.TweakKey;
		byte[] item2 = tuple.DataKey;
		ProsperoOuterBlockKind[] array4 = InferBlockKinds(fih, blocks, outerSuperblockIndex);
		input.Position = map.OuterPfsOffset;
		ProsperoOuterPfsImage.Transform(input, output, map.OuterPfsSize, item, item2, 65536, array4, encrypt: false);
		output.Flush();
	}

	private static void DecodeInnerPfs(Stream outerImage, int outerSuperblockIndex, Stream destination)
	{
		if (!outerImage.CanRead || !outerImage.CanSeek)
		{
			throw new ArgumentException("Decrypted outer-PFS stream must be readable and seekable.", "outerImage");
		}
		using LibProsperoPkg.Util.StreamReader r = new LibProsperoPkg.Util.StreamReader(outerImage, 0L);
		PfsReader pfs = new PfsReader(r, 0uL, null, null, null, (long)outerSuperblockIndex * 65536L, encryptedDataAlreadyDecrypted: true);
		PfsReader.File file = FindFile(pfs, "pfs_image.dat");
		NapsLayoutDocument layout = ProsperoNapsLayout.Parse(FindFile(pfs, "naps_pkg_layout.dat").ReadAllBytes());
		using IMemoryReader r2 = file.GetView();
		using StreamWrapper pfsImage = new StreamWrapper(r2, file.size);
		ProsperoNapsImage.Decompress(pfsImage, layout, destination);
	}

	private static long LocateSuperblock(Stream input, int blockSize = 65536)
	{
		if (!input.CanRead || !input.CanSeek)
		{
			throw new ArgumentException("PFS stream must be readable and seekable.", "input");
		}
		if (blockSize <= 16)
		{
			throw new ArgumentOutOfRangeException("blockSize");
		}
		long position = input.Position;
		try
		{
			Span<byte> span = stackalloc byte[12];
			for (long num = 0L; num <= input.Length - blockSize; num += blockSize)
			{
				input.Position = num;
				input.ReadExactly(span);
				if (BinaryPrimitives.ReadUInt64LittleEndian(span) == 2 && span[8] == 11 && span[9] == 42 && span[10] == 51 && span[11] == 1)
				{
					return num;
				}
			}
			return -1L;
		}
		finally
		{
			input.Position = position;
		}
	}

	private static ProsperoOuterBlockKind[] InferBlockKinds(ProsperoFihHeader fih, int blocks, int superblock)
	{
		ProsperoOuterBlockKind[] array = Enumerable.Repeat(ProsperoOuterBlockKind.Signed, blocks).ToArray();
		int num = checked((int)fih.InnerImageBlockCount);
		if (num > superblock)
		{
			throw new InvalidDataException("FIH inner-image block count crosses the superblock.");
		}
		for (int i = 0; i < num; i++)
		{
			array[i] = ProsperoOuterBlockKind.Data;
		}
		array[superblock] = ProsperoOuterBlockKind.Plaintext;
		return array;
	}

	private static int ResolveSuperblockIndex(ProsperoFihHeader fih, int blocks)
	{
		long num = checked((long)fih.NapsLayoutSize + 65536 - 1) / 65536;
		long num2 = checked(fih.InnerImageBlockCount + num);
		if (num2 < 0 || num2 >= blocks)
		{
			throw new InvalidDataException("FIH-derived outer superblock index is out of range.");
		}
		return (int)num2;
	}

	private static PfsReader.File FindFile(PfsReader pfs, string name)
	{
		return pfs.GetAllFiles().FirstOrDefault((PfsReader.File file) => string.Equals(Path.GetFileName(file.FullName), name, StringComparison.Ordinal)) ?? throw new InvalidDataException("Outer PFS does not contain " + name + ".");
	}

	private static void ValidateSuperblockShape(ReadOnlySpan<byte> sb, int blocks)
	{
		ulong num = BinaryPrimitives.ReadUInt64LittleEndian(sb);
		ulong num2 = BinaryPrimitives.ReadUInt64LittleEndian(sb.Slice(8));
		uint num3 = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(32));
		ulong num4 = BinaryPrimitives.ReadUInt64LittleEndian(sb.Slice(56));
		if (num - 1 > 1 || num2 <= 20130314 || num3 != 65536 || num4 != (ulong)blocks)
		{
			throw new InvalidDataException("FIH-derived block is not a valid outer PPR-PFS superblock.");
		}
	}

	private static string NormalizeRelativePath(string value)
	{
		string text = value.Replace('\\', '/').TrimStart('/');
		if (text.Length == 0 || text.Split('/').Any((string p) => ((p != null && p.Length == 0) || p == "." || p == "..") ? true : false))
		{
			throw new InvalidDataException("Unsafe package path: " + value);
		}
		return text;
	}

	private static string SafeTarget(string root, string relative)
	{
		string value = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
		string fullPath = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
		if (!fullPath.StartsWith(value, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
		{
			throw new InvalidDataException("Package path escapes output directory: " + relative);
		}
		return fullPath;
	}

	private static byte[] ReadRange(Stream input, long offset, int size)
	{
		RequireRange(input.Length, offset, size, "stream range");
		byte[] array = new byte[size];
		input.Position = offset;
		input.ReadExactly(array);
		return array;
	}

	private static void CopyRange(Stream input, Stream output, long offset, long size)
	{
		RequireRange(input.Length, offset, size, "stream range");
		input.Position = offset;
		byte[] array = new byte[1048576];
		while (size != 0L)
		{
			int count = (int)Math.Min(array.Length, size);
			int num = input.Read(array, 0, count);
			if (num == 0)
			{
				throw new EndOfStreamException();
			}
			output.Write(array, 0, num);
			size -= num;
		}
	}

	private static void RequireRange(long length, long offset, long size, string name)
	{
		if (offset < 0 || size < 0 || offset > length || size > length - offset)
		{
			throw new InvalidDataException(name + " lies outside the package.");
		}
	}

	private static void EnsureDistinctPaths(string inputPath, string outputPath)
	{
		string fullPath = Path.GetFullPath(inputPath);
		string fullPath2 = Path.GetFullPath(outputPath);
		StringComparison comparisonType = (OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
		if (string.Equals(fullPath, fullPath2, comparisonType))
		{
			throw new ArgumentException("Input package and output image paths must be different.", "outputPath");
		}
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

	private static long AlignUp(long value, int alignment)
	{
		checked
		{
			return unchecked(checked(value + alignment - 1) / alignment) * alignment;
		}
	}
}
