using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LibProsperoPkg.PFS;
using LibProsperoPkg.PFS.Compression;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PKG;

public static class ProsperoAprImageBuilder
{
	private sealed class A53BuildState
	{
		public int Version { get; set; } = 1;

		public required string OuterSeedHex { get; set; }

		public long TimestampSeconds { get; set; }

		public long LogicalImageSize { get; set; }

		public long MetadataBase { get; set; }

		public required string PackedImageSha3Hex { get; set; }

		public required string PlaceholderLayoutSha3Hex { get; set; }

		public bool Finalized { get; set; }
	}

	private const int BlockSize = 65536;

	private const string A53StateFileName = "ppr-a53-build.json";

	private const string A53CmacManifestFileName = "ppr-a53-cmac.manifest";

	public static ProsperoAprImageBuildResult Build(ProsperoAprImageBuildOptions options, Action<string>? logger = null)
	{
		ArgumentNullException.ThrowIfNull(options, "options");
		ArgumentException.ThrowIfNullOrWhiteSpace(options.SourceFolder, "options.SourceFolder");
		ArgumentException.ThrowIfNullOrWhiteSpace(options.OutputDirectory, "options.OutputDirectory");
		if (!Directory.Exists(options.SourceFolder))
		{
			throw new DirectoryNotFoundException(options.SourceFolder);
		}
		if (options.ContentId.Length != 36)
		{
			throw new ArgumentException("Content id must be exactly 36 characters.", "options");
		}
		if (options.Passcode.Length != 32)
		{
			throw new ArgumentException("Passcode must be exactly 32 characters.", "options");
		}
		byte[] napsOuterBlockCmacKey = options.NapsOuterBlockCmacKey;
		if (napsOuterBlockCmacKey != null && napsOuterBlockCmacKey.Length != 16)
		{
			throw new ArgumentException("NAPS outer-block CMAC key must be exactly 16 bytes.", "options");
		}
		napsOuterBlockCmacKey = options.OuterSeed;
		if (napsOuterBlockCmacKey != null && napsOuterBlockCmacKey.Length != 16)
		{
			throw new ArgumentException("Outer seed must be exactly 16 bytes.", "options");
		}
		if (options.VolumeType == ProsperoVolumeType.AdditionalContentNoData)
		{
			throw new ArgumentException("Entitlement-only additional content has no APR image to build.", "options");
		}
		if (options.DeferOuterBlockDigestsToA53 && (options.NapsOuterBlockCmacKey != null || options.EncryptOuterPfs))
		{
			throw new ArgumentException("The A53 preparation pass requires a zero placeholder CMAC and plaintext outer PFS.", "options");
		}
		if (!options.EncryptOuterPfs && !options.DeferOuterBlockDigestsToA53 && options.NapsOuterBlockCmacKey != null)
		{
			throw new ArgumentException("PLAINTEXT_NOAUTH cannot contain keyed NAPS outer-block authentication tags.", "options");
		}
		Action<string> action = logger ?? ((Action<string>)((string _) =>
		{
		}));
		string fullPath = Path.GetFullPath(options.OutputDirectory);
		Directory.CreateDirectory(fullPath);
		string text = Path.Combine(fullPath, "pfs_image.dat");
		string text2 = Path.Combine(fullPath, "logical.ppr-pfs");
		string text3 = Path.Combine(fullPath, "naps_pkg_layout.dat");
		string text4 = Path.Combine(fullPath, "outer.pfs");
		string text5 = Path.Combine(fullPath, "ppr-a53-image.manifest");
		string text6 = Path.Combine(fullPath, "ppr-a53-cmac.manifest");
		string text7 = Path.Combine(fullPath, "ppr-a53-build.json");
		long num = new DateTimeOffset(options.TimeStamp.ToUniversalTime()).ToUnixTimeSeconds();
		byte[] array = options.OuterSeed?.AsSpan().ToArray() ?? (options.DeterministicBuild ? ProsperoImageDigests.Sha3_256(Encoding.ASCII.GetBytes("LibProsperoPkg deterministic APR outer seed\0" + options.ContentId + "\0" + options.Passcode)).AsSpan(0, 16).ToArray() : RandomNumberGenerator.GetBytes(16));
		action("Preparing the publisher inner tree and APR path/AFID metadata...");
		FSDir uroot = ProsperoPkgBuilder.BuildInnerTree(Path.GetFullPath(options.SourceFolder), options.Passcode, options.VolumeType);
		ProsperoPs5InnerImageResult prosperoPs5InnerImageResult = new ProsperoPs5InnerImageAssembler(num, 0u, options.AfidAssignments).BuildFromFsTreeToFile(uroot, text);
		action("Generating the APR NAPS FIDX/U2C/CBI map and outer-block digests...");
		byte[] array2 = ProsperoNwonlyNapsGenerator.Generate(prosperoPs5InnerImageResult, null, options.NapsOuterBlockCmacKey);
		File.WriteAllBytes(text3, array2);
		NapsLayoutDocument napsLayoutDocument = ProsperoNapsLayout.Parse(array2);
		ValidatePhysicalImage(text, prosperoPs5InnerImageResult, napsLayoutDocument, options.NapsOuterBlockCmacKey);
		if (options.DeferOuterBlockDigestsToA53)
		{
			WriteCmacManifest(text6, prosperoPs5InnerImageResult.ImageLength, array);
		}
		action("Reconstructing and validating the logical APR mount image...");
		using (FileStream pfsImage = new FileStream(text, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.RandomAccess))
		{
			using FileStream destination = new FileStream(text2, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1048576, FileOptions.SequentialScan);
			ProsperoNapsImage.Decompress(pfsImage, napsLayoutDocument, destination);
		}
		long num2 = checked(prosperoPs5InnerImageResult.Ndblock * 65536);
		if (new FileInfo(text2).Length != num2)
		{
			throw new InvalidDataException("The reconstructed APR mount size does not match Ndblock.");
		}
		int num3 = ValidateLogicalImage(text2, prosperoPs5InnerImageResult.MetaBaseLogical);
		int num4 = prosperoPs5InnerImageResult.Nodes.Count((ProsperoPs5MetaNode node) => !node.IsDirectory && node.ParentInode >= 0);
		if (num3 != num4)
		{
			throw new InvalidDataException($"APR logical image exposes {num3} files; expected {num4}.");
		}
		byte[] ekpfs = (options.EncryptOuterPfs ? ProsperoPfsKeys.DeriveEkpfs(options.ContentId, options.Passcode) : null);
		action(options.EncryptOuterPfs ? "Building and encrypting the APR carrier outer PFS..." : "Building the plaintext APR carrier outer PFS...");
		ProsperoOuterPackageFileResult prosperoOuterPackageFileResult = ProsperoOuterPfsBuilder.BuildForPackageToFile(new _003C_003Ez__ReadOnlyArray<ProsperoOuterFileSource>(new ProsperoOuterFileSource[2]
		{
			new ProsperoOuterFileSource
			{
				Name = "pfs_image.dat",
				Path = text,
				SizeCompressed = num2,
				Signed = false
			},
			new ProsperoOuterFileSource
			{
				Name = "naps_pkg_layout.dat",
				Path = text3,
				Signed = true
			}
		}), new ProsperoOuterPfsBuildParameters
		{
			Seed = array,
			TimestampSeconds = num,
			TimestampNanoseconds = 0u,
			ImageMode = ((!options.EncryptOuterPfs && !options.DeferOuterBlockDigestsToA53) ? ProsperoPublisherImageMode.PlaintextNoAuth : ProsperoPublisherImageMode.Native)
		}, ekpfs, text4, options.EncryptOuterPfs);
		WriteEncryptionManifest(text5, prosperoOuterPackageFileResult.BlockKinds, array, prosperoOuterPackageFileResult.SuperblockIndex);
		if (options.DeferOuterBlockDigestsToA53)
		{
			A53BuildState value = new A53BuildState
			{
				OuterSeedHex = Convert.ToHexString(array).ToLowerInvariant(),
				TimestampSeconds = num,
				LogicalImageSize = num2,
				MetadataBase = prosperoPs5InnerImageResult.MetaBaseLogical,
				PackedImageSha3Hex = Sha3FileHex(text),
				PlaceholderLayoutSha3Hex = Convert.ToHexString(ProsperoImageDigests.Sha3_256(array2)).ToLowerInvariant()
			};
			File.WriteAllText(text7, JsonSerializer.Serialize(value, new JsonSerializerOptions
			{
				WriteIndented = true
			}) + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		}
		return new ProsperoAprImageBuildResult
		{
			PackedImagePath = text,
			LogicalImagePath = text2,
			NapsLayoutPath = text3,
			OuterPfsPath = text4,
			EncryptionManifestPath = text5,
			OuterSeed = array,
			OuterSuperblockIndex = prosperoOuterPackageFileResult.SuperblockIndex,
			OuterTree = prosperoOuterPackageFileResult.Tree,
			ImageDigests = prosperoOuterPackageFileResult.ImageDigests,
			PackedImageSize = prosperoPs5InnerImageResult.ImageLength,
			LogicalImageSize = num2,
			MetadataBase = prosperoPs5InnerImageResult.MetaBaseLogical,
			InnerFileCount = num4,
			InnerInodeCount = prosperoPs5InnerImageResult.Nodes.Count,
			SparseAfidCount = prosperoPs5InnerImageResult.SparseAfidHoles.Count,
			EmptyFileCount = prosperoPs5InnerImageResult.EmptyFileLogicalOffsets.Count,
			NapsCounts = napsLayoutDocument.Counts,
			OuterBlockDigestsKeyed = (options.NapsOuterBlockCmacKey != null),
			OuterPfsEncrypted = options.EncryptOuterPfs,
			CmacManifestPath = (options.DeferOuterBlockDigestsToA53 ? text6 : null),
			BuildStatePath = (options.DeferOuterBlockDigestsToA53 ? text7 : null),
			AwaitingA53Digests = options.DeferOuterBlockDigestsToA53
		};
	}

	public static ProsperoAprA53FinalizeResult FinalizeFromA53Digests(string outputDirectory, string digestPath, Action<string>? logger = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory, "outputDirectory");
		ArgumentException.ThrowIfNullOrWhiteSpace(digestPath, "digestPath");
		Action<string> action = logger ?? ((Action<string>)((string _) =>
		{
		}));
		string fullPath = Path.GetFullPath(outputDirectory);
		string text = Path.Combine(fullPath, "pfs_image.dat");
		string text2 = Path.Combine(fullPath, "logical.ppr-pfs");
		string text3 = Path.Combine(fullPath, "naps_pkg_layout.dat");
		string text4 = Path.Combine(fullPath, "outer.pfs");
		string text5 = Path.Combine(fullPath, "ppr-a53-image.manifest");
		string text6 = text3 + ".a53-finalizing";
		string text7 = text4 + ".a53-finalizing";
		string text8 = text5 + ".a53-finalizing";
		string text9 = Path.Combine(fullPath, "ppr-a53-build.json");
		string[] array = new string[4] { text, text2, text3, text9 };
		foreach (string text10 in array)
		{
			if (!File.Exists(text10))
			{
				throw new FileNotFoundException("Missing A53 preparation artifact.", text10);
			}
		}
		if (!File.Exists(digestPath))
		{
			throw new FileNotFoundException("A53 digest file was not found.", digestPath);
		}
		A53BuildState a53BuildState = JsonSerializer.Deserialize<A53BuildState>(File.ReadAllText(text9)) ?? throw new InvalidDataException("The A53 build-state file is empty.");
		if (a53BuildState.Version != 1)
		{
			throw new InvalidDataException($"Unsupported A53 build-state version {a53BuildState.Version}.");
		}
		if (a53BuildState.Finalized)
		{
			throw new InvalidOperationException("This A53 preparation directory was already finalized.");
		}
		byte[] array2 = Convert.FromHexString(a53BuildState.OuterSeedHex);
		if (array2.Length != 16)
		{
			throw new InvalidDataException("The persisted outer-PFS seed is invalid.");
		}
		byte[] array3 = File.ReadAllBytes(text3);
		if (!string.Equals(Sha3FileHex(text), a53BuildState.PackedImageSha3Hex, StringComparison.OrdinalIgnoreCase) || !string.Equals(Convert.ToHexString(ProsperoImageDigests.Sha3_256(array3)), a53BuildState.PlaceholderLayoutSha3Hex, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("The prepared image or placeholder layout changed before A53 finalization.");
		}
		NapsLayoutDocument napsLayoutDocument = ProsperoNapsLayout.Parse(array3);
		byte[] array4 = File.ReadAllBytes(Path.GetFullPath(digestPath));
		int num2 = checked(napsLayoutDocument.Counts.NumOuterBlocks * 8);
		if (array4.Length != num2)
		{
			throw new InvalidDataException($"A53 digest sidecar is {array4.Length} bytes; expected {num2}.");
		}
		if (new FileInfo(text).Length != (long)napsLayoutDocument.Counts.NumOuterBlocks * 65536L)
		{
			throw new InvalidDataException("The physical image no longer matches the prepared NAPS layout.");
		}
		int index = checked((int)napsLayoutDocument.Map.OuterBlockDigest.Offset);
		array4.CopyTo(array3, index);
		NapsLayoutDocument napsLayoutDocument2 = ProsperoNapsLayout.Parse(array3);
		if (napsLayoutDocument2.OuterBlockDigests.SelectMany((byte[] tag) => tag).All((byte value) => value == 0))
		{
			throw new InvalidDataException("A53 returned only zero OuterBlockDigest values.");
		}
		ProsperoNapsImage.BuildPlan(napsLayoutDocument2);
		File.WriteAllBytes(text6, array3);
		try
		{
			action("Reconstructing the image after importing A53 OuterBlockDigest values...");
			using (FileStream pfsImage = new FileStream(text, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.RandomAccess))
			{
				using FileStream destination = new FileStream(text2, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1048576, FileOptions.SequentialScan);
				ProsperoNapsImage.Decompress(pfsImage, napsLayoutDocument2, destination);
			}
			if (new FileInfo(text2).Length != a53BuildState.LogicalImageSize)
			{
				throw new InvalidDataException("The reconstructed logical size differs from the preparation pass.");
			}
			ValidateLogicalImage(text2, a53BuildState.MetadataBase);
			action("Rebuilding the plaintext outer PFS over the final signed NAPS layout...");
			ProsperoOuterPackageFileResult prosperoOuterPackageFileResult = ProsperoOuterPfsBuilder.BuildForPackageToFile(new _003C_003Ez__ReadOnlyArray<ProsperoOuterFileSource>(new ProsperoOuterFileSource[2]
			{
				new ProsperoOuterFileSource
				{
					Name = "pfs_image.dat",
					Path = text,
					SizeCompressed = a53BuildState.LogicalImageSize,
					Signed = false
				},
				new ProsperoOuterFileSource
				{
					Name = "naps_pkg_layout.dat",
					Path = text6,
					Signed = true
				}
			}), new ProsperoOuterPfsBuildParameters
			{
				Seed = array2,
				TimestampSeconds = a53BuildState.TimestampSeconds,
				TimestampNanoseconds = 0u
			}, null, text7, encryptOutput: false);
			WriteEncryptionManifest(text8, prosperoOuterPackageFileResult.BlockKinds, array2, prosperoOuterPackageFileResult.SuperblockIndex);
			File.Move(text6, text3, overwrite: true);
			File.Move(text7, text4, overwrite: true);
			File.Move(text8, text5, overwrite: true);
			a53BuildState.Finalized = true;
			File.WriteAllText(text9, JsonSerializer.Serialize(a53BuildState, new JsonSerializerOptions
			{
				WriteIndented = true
			}) + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			return new ProsperoAprA53FinalizeResult
			{
				NapsLayoutPath = text3,
				OuterPfsPath = text4,
				EncryptionManifestPath = text5,
				OuterBlockDigestCount = napsLayoutDocument2.Counts.NumOuterBlocks,
				OuterSuperblockIndex = prosperoOuterPackageFileResult.SuperblockIndex,
				OuterPfsSize = new FileInfo(text4).Length
			};
		}
		finally
		{
			File.Delete(text6);
			File.Delete(text7);
			File.Delete(text8);
		}
	}

	private static void ValidatePhysicalImage(string packedPath, ProsperoPs5InnerImageResult inner, NapsLayoutDocument layout, byte[]? cmacKey)
	{
		long length = new FileInfo(packedPath).Length;
		if (length != inner.ImageLength || (length & 0xFFFF) != 0L)
		{
			throw new InvalidDataException("The APR physical image is not an exact 64-KiB block image.");
		}
		int num;
		checked
		{
			num = (int)unchecked(length / 65536);
			if (layout.Counts.NumOuterBlocks != num || layout.OuterBlockDigests.Count != num)
			{
				throw new InvalidDataException("NAPS OuterBlockDigest count does not match the physical APR image.");
			}
		}
		using FileStream fileStream = File.OpenRead(packedPath);
		byte[] array = new byte[65536];
		for (int i = 0; i < num; i++)
		{
			fileStream.ReadExactly(array);
			ReadOnlySpan<byte> readOnlySpan = layout.OuterBlockDigests[i];
			if (cmacKey == null)
			{
				if (readOnlySpan.IndexOfAnyExcept((byte)0) >= 0)
				{
					throw new InvalidDataException("A keyless NAPS layout contains a non-zero outer tag.");
				}
			}
			else if (!CryptographicOperations.FixedTimeEquals(ProsperoNapsImage.ComputeOuterBlockDigest(array, cmacKey), readOnlySpan))
			{
				throw new InvalidDataException($"NAPS outer-block CMAC {i} did not verify.");
			}
		}
		ProsperoNapsImage.BuildPlan(layout);
	}

	private static int ValidateLogicalImage(string logicalPath, long superblockOffset)
	{
		using FileStream s = File.OpenRead(logicalPath);
		using LibProsperoPkg.Util.StreamReader r = new LibProsperoPkg.Util.StreamReader(s, 0L);
		PfsReader pfsReader = new PfsReader(r, 0uL, null, null, null, superblockOffset, encryptedDataAlreadyDecrypted: true);
		ProsperoAfidMap.FromPfs(pfsReader);
		return pfsReader.GetAllFiles().Count();
	}

	private static void WriteEncryptionManifest(string path, IReadOnlyList<ProsperoOuterBlockKind> kinds, ReadOnlySpan<byte> outerSeed, int superblockIndex)
	{
		if (outerSeed.Length != 16)
		{
			throw new ArgumentException("Outer seed must be exactly 16 bytes.", "outerSeed");
		}
		if (superblockIndex < 0 || superblockIndex >= kinds.Count || kinds[superblockIndex] != ProsperoOuterBlockKind.Plaintext)
		{
			throw new ArgumentOutOfRangeException("superblockIndex");
		}
		using StreamWriter streamWriter = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		streamWriter.WriteLine("# Generated outer-PFS AES-XTS map; one data unit is 0x10000 bytes.");
		streamWriter.WriteLine("# Data tweak = block index; signed tweak = bit 47 | block index.");
		streamWriter.WriteLine("# outer_seed=" + Convert.ToHexString(outerSeed).ToLowerInvariant());
		streamWriter.WriteLine($"# outer_superblock_offset=0x{(long)superblockIndex * 65536L:x16}");
		int num = 0;
		while (num < kinds.Count)
		{
			ProsperoOuterBlockKind prosperoOuterBlockKind = kinds[num];
			int i;
			for (i = num + 1; i < kinds.Count && kinds[i] == prosperoOuterBlockKind; i++)
			{
			}
			long value = (long)num * 65536L;
			long value2 = (long)(i - num) * 65536L;
			if (prosperoOuterBlockKind == ProsperoOuterBlockKind.Plaintext)
			{
				streamWriter.WriteLine($"copy 0x{value:x16} 0x{value2:x16}");
			}
			else
			{
				ulong num2 = checked((ulong)num);
				if (prosperoOuterBlockKind == ProsperoOuterBlockKind.Signed)
				{
					num2 |= 0x800000000000L;
				}
				streamWriter.WriteLine($"encrypt 0x{value:x16} 0x{value2:x16} 0x{num2:x16}");
			}
			num = i;
		}
	}

	private static void WriteCmacManifest(string path, long imageSize, ReadOnlySpan<byte> outerSeed)
	{
		if (imageSize <= 0 || (imageSize & 0xFFFF) != 0L)
		{
			throw new ArgumentOutOfRangeException("imageSize");
		}
		if (outerSeed.Length != 16)
		{
			throw new ArgumentException("Outer seed must be exactly 16 bytes.", "outerSeed");
		}
		using StreamWriter streamWriter = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		streamWriter.WriteLine("# A53 pass 1: compute one eight-byte OuterBlockDigest per pfs_image block.");
		streamWriter.WriteLine("# The XTS output of this pass is discarded; only the tag sidecar is imported.");
		streamWriter.WriteLine("# outer_seed=" + Convert.ToHexString(outerSeed).ToLowerInvariant());
		streamWriter.WriteLine($"encrypt 0x0000000000000000 0x{imageSize:x16} 0x0000000000000000");
	}

	private static string Sha3FileHex(string path)
	{
		using FileStream stream = File.OpenRead(path);
		return Convert.ToHexString(ProsperoSha3.HashData(stream)).ToLowerInvariant();
	}
}
