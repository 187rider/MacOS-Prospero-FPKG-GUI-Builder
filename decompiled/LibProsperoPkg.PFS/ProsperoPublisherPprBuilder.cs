using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using LibProsperoPkg.PFS.Compression;
using LibProsperoPkg.PKG;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PFS;

/// <summary>
/// Legacy generic direct-offset PPR/NAPS artifact builder. It does not emit the specialized APR
/// flat-path/AFID metadata used by the current publisher image path; new callers should use
/// <see cref="T:LibProsperoPkg.PKG.ProsperoNwonlyNapsGenerator" /> through <see cref="T:LibProsperoPkg.PKG.ProsperoAprImageBuilder" />.
/// </summary>
[Obsolete("Use LibProsperoPkg.PKG.ProsperoAprImageBuilder for APR publisher artifacts.")]
public static class ProsperoPublisherPprBuilder
{
	public const int NestedPfsOffset = 4194304;

	public static ReadOnlySpan<byte> PfsVersion => "01.000.000"u8;

	/// <summary>
	/// Builds the publisher artifact chain without materializing logical, packed-NAPS, or outer-PFS
	/// images in managed arrays.
	/// </summary>
	public static ProsperoPublisherPprFileBuildResult BuildFileBacked(ProsperoPublisherPprBuildOptions options, Action<string>? logger = null)
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
		byte[] outerSeed = options.OuterSeed;
		if (outerSeed != null && outerSeed.Length != 16)
		{
			throw new ArgumentException("Outer seed must be exactly 16 bytes.", "options");
		}
		if (!options.EncryptOuterPfs && options.NapsOptions.OuterBlockCmacKey != null)
		{
			throw new ArgumentException("PLAINTEXT_NOAUTH cannot contain keyed NAPS outer-block authentication tags.", "options");
		}
		Action<string> action = logger ?? ((Action<string>)((string _) =>
		{
		}));
		string fullPath = Path.GetFullPath(options.OutputDirectory);
		Directory.CreateDirectory(fullPath);
		string text = Path.Combine(fullPath, "inner.ppr-pfs");
		string text2 = Path.Combine(fullPath, "logical.ppr-pfs");
		string text3 = Path.Combine(fullPath, "pfs_image.dat");
		string text4 = Path.Combine(fullPath, "naps_pkg_layout.dat");
		string text5 = Path.Combine(fullPath, "outer.pfs");
		ProsperoPfsLayoutOptions pfsOptions = options.PfsOptions;
		bool usePublisherPprLayout = pfsOptions.UsePublisherPprLayout;
		bool filterOuterPackageEntries = pfsOptions.FilterOuterPackageEntries;
		try
		{
			pfsOptions.UsePublisherPprLayout = true;
			pfsOptions.FilterOuterPackageEntries = true;
			action("Building publisher direct-offset PPR-PFS...");
			ProsperoPfsLayout.BuildFromFolder(options.SourceFolder, text, pfsOptions, action);
		}
		finally
		{
			pfsOptions.UsePublisherPprLayout = usePublisherPprLayout;
			pfsOptions.FilterOuterPackageEntries = filterOuterPackageEntries;
		}
		int innerInodeCount;
		using (FileStream s = File.OpenRead(text))
		{
			innerInodeCount = checked((int)PfsHeader.ReadFromStream(s).DinodeCount);
		}
		BuildLogicalImage(text, text2);
		long length = new FileInfo(text2).Length;
		action("Packing the logical PPR-PFS stream as NAPS...");
		ProsperoNapsBuildOptions napsOptions = options.NapsOptions;
		ProsperoNapsBuildOptions options2 = new ProsperoNapsBuildOptions
		{
			CompressionLevel = napsOptions.CompressionLevel,
			Compress = napsOptions.Compress,
			VerifyRoundTrip = napsOptions.VerifyRoundTrip,
			OuterBlockCmacKey = napsOptions.OuterBlockCmacKey,
			FileBoundaries = new long[4] { 0L, PfsVersion.Length, 4194304L, length }
		};
		ProsperoNapsFileBuildResult prosperoNapsFileBuildResult;
		using (FileStream logicalInput = new FileStream(text2, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.RandomAccess))
		{
			using FileStream packedOutput = new FileStream(text3, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1048576, FileOptions.RandomAccess);
			prosperoNapsFileBuildResult = ProsperoNapsImage.Pack(logicalInput, length, packedOutput, options2);
		}
		File.WriteAllBytes(text4, prosperoNapsFileBuildResult.LayoutBytes);
		byte[] array = options.OuterSeed?.AsSpan().ToArray() ?? (options.DeterministicBuild ? ProsperoImageDigests.Sha3_256(Encoding.ASCII.GetBytes("LibProsperoPkg deterministic outer seed\0" + options.ContentId + "\0" + options.Passcode)).AsSpan(0, 16).ToArray() : RandomNumberGenerator.GetBytes(16));
		long timestampSeconds = new DateTimeOffset(options.TimeStamp.ToUniversalTime()).ToUnixTimeSeconds();
		byte[] ekpfs = ProsperoPfsKeys.DeriveEkpfs(options.ContentId, options.Passcode);
		action(options.EncryptOuterPfs ? "Building and encrypting the data-first outer PFS..." : "Building the plaintext data-first outer PFS (AES-XTS disabled)...");
		ProsperoOuterPackageFileResult prosperoOuterPackageFileResult = ProsperoOuterPfsBuilder.BuildForPackageToFile(new _003C_003Ez__ReadOnlyArray<ProsperoOuterFileSource>(new ProsperoOuterFileSource[2]
		{
			new ProsperoOuterFileSource
			{
				Name = "pfs_image.dat",
				Path = text3,
				SizeCompressed = length,
				Signed = false
			},
			new ProsperoOuterFileSource
			{
				Name = "naps_pkg_layout.dat",
				Path = text4,
				Signed = true
			}
		}), new ProsperoOuterPfsBuildParameters
		{
			Seed = array,
			TimestampSeconds = timestampSeconds,
			TimestampNanoseconds = 0u,
			ImageMode = ((!options.EncryptOuterPfs) ? ProsperoPublisherImageMode.PlaintextNoAuth : ProsperoPublisherImageMode.Native)
		}, ekpfs, text5, options.EncryptOuterPfs);
		int innerFileCount = ValidateInner(text2);
		byte[] logicalImageDigest;
		using (FileStream stream = File.OpenRead(text2))
		{
			logicalImageDigest = ProsperoSha3.HashData(stream);
		}
		return new ProsperoPublisherPprFileBuildResult
		{
			InnerPfsPath = text,
			LogicalImagePath = text2,
			PackedImagePath = text3,
			NapsLayoutPath = text4,
			OuterPfsPath = text5,
			OuterSeed = array,
			OuterSuperblockIndex = prosperoOuterPackageFileResult.SuperblockIndex,
			InnerFileCount = innerFileCount,
			InnerInodeCount = innerInodeCount,
			ImageDigests = prosperoOuterPackageFileResult.ImageDigests,
			LogicalImageDigest = logicalImageDigest,
			Naps = prosperoNapsFileBuildResult,
			OuterTree = prosperoOuterPackageFileResult.Tree,
			OuterPfsEncrypted = options.EncryptOuterPfs
		};
	}

	public static ProsperoPublisherPprBuildResult Build(ProsperoPublisherPprBuildOptions options, Action<string>? logger = null)
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
		byte[] outerSeed = options.OuterSeed;
		if (outerSeed != null && outerSeed.Length != 16)
		{
			throw new ArgumentException("Outer seed must be exactly 16 bytes.", "options");
		}
		Action<string> action = logger ?? ((Action<string>)((string _) =>
		{
		}));
		string fullPath = Path.GetFullPath(options.OutputDirectory);
		Directory.CreateDirectory(fullPath);
		string text = Path.Combine(fullPath, "inner.ppr-pfs");
		string text2 = Path.Combine(fullPath, "logical.ppr-pfs");
		string text3 = Path.Combine(fullPath, "pfs_image.dat");
		string text4 = Path.Combine(fullPath, "naps_pkg_layout.dat");
		string text5 = Path.Combine(fullPath, "outer.pfs");
		ProsperoPfsLayoutOptions pfsOptions = options.PfsOptions;
		bool usePublisherPprLayout = pfsOptions.UsePublisherPprLayout;
		bool filterOuterPackageEntries = pfsOptions.FilterOuterPackageEntries;
		ProsperoPfsLayoutResult prosperoPfsLayoutResult;
		try
		{
			pfsOptions.UsePublisherPprLayout = true;
			pfsOptions.FilterOuterPackageEntries = true;
			action("Building publisher direct-offset PPR-PFS...");
			prosperoPfsLayoutResult = ProsperoPfsLayout.BuildFromFolder(options.SourceFolder, text, pfsOptions, action);
		}
		finally
		{
			pfsOptions.UsePublisherPprLayout = usePublisherPprLayout;
			pfsOptions.FilterOuterPackageEntries = filterOuterPackageEntries;
		}
		if (prosperoPfsLayoutResult.ImageSize > Array.MaxLength - 4194304)
		{
			throw new InvalidDataException("Publisher logical image exceeds the in-memory builder limit.");
		}
		byte[] array = File.ReadAllBytes(text);
		int innerInodeCount;
		using (MemoryStream s = new MemoryStream(array, writable: false))
		{
			innerInodeCount = checked((int)PfsHeader.ReadFromStream(s).DinodeCount);
		}
		byte[] array2 = BuildLogicalImage(array);
		File.WriteAllBytes(text2, array2);
		action("Packing the logical PPR-PFS stream as NAPS...");
		ProsperoNapsBuildOptions napsOptions = options.NapsOptions;
		ProsperoNapsBuildOptions options2 = new ProsperoNapsBuildOptions
		{
			CompressionLevel = napsOptions.CompressionLevel,
			Compress = napsOptions.Compress,
			VerifyRoundTrip = napsOptions.VerifyRoundTrip,
			OuterBlockCmacKey = napsOptions.OuterBlockCmacKey,
			FileBoundaries = new long[4] { 0L, PfsVersion.Length, 4194304L, array2.Length }
		};
		ProsperoNapsBuildResult prosperoNapsBuildResult = ProsperoNapsImage.Pack(array2, options2);
		File.WriteAllBytes(text3, prosperoNapsBuildResult.PackedImage);
		File.WriteAllBytes(text4, prosperoNapsBuildResult.LayoutBytes);
		byte[] array3 = options.OuterSeed?.AsSpan().ToArray() ?? (options.DeterministicBuild ? ProsperoImageDigests.Sha3_256(Encoding.ASCII.GetBytes("LibProsperoPkg deterministic outer seed\0" + options.ContentId + "\0" + options.Passcode)).AsSpan(0, 16).ToArray() : RandomNumberGenerator.GetBytes(16));
		long timestampSeconds = new DateTimeOffset(options.TimeStamp.ToUniversalTime()).ToUnixTimeSeconds();
		ProsperoOuterPfsBuildParameters parameters = new ProsperoOuterPfsBuildParameters
		{
			Seed = array3,
			TimestampSeconds = timestampSeconds,
			TimestampNanoseconds = 0u
		};
		ProsperoOuterFile[] files = new ProsperoOuterFile[2]
		{
			new ProsperoOuterFile
			{
				Name = "pfs_image.dat",
				Data = prosperoNapsBuildResult.PackedImage,
				SizeCompressed = array2.Length,
				Signed = false
			},
			new ProsperoOuterFile
			{
				Name = "naps_pkg_layout.dat",
				Data = prosperoNapsBuildResult.LayoutBytes,
				SizeCompressed = prosperoNapsBuildResult.LayoutBytes.Length,
				Signed = true
			}
		};
		action(options.EncryptOuterPfs ? "Building and encrypting the data-first outer PFS..." : "Building the plaintext data-first outer PFS (AES-XTS disabled)...");
		ProsperoOuterPfsBuildResult prosperoOuterPfsBuildResult = ProsperoOuterPfsBuilder.BuildPlaintext(files, parameters);
		byte[] array4 = prosperoOuterPfsBuildResult.Plaintext.AsSpan().ToArray();
		byte[] imageDigests = BuildImageDigests(array4);
		if (options.EncryptOuterPfs)
		{
			(byte[], byte[]) tuple = ProsperoPfsKeys.DeriveImageEncryptionKeys(ProsperoPfsKeys.DeriveEkpfs(options.ContentId, options.Passcode), array3);
			ProsperoOuterPfsBuilder.Encrypt(prosperoOuterPfsBuildResult, tuple.Item1, tuple.Item2);
			byte[] array5 = prosperoOuterPfsBuildResult.Plaintext.AsSpan().ToArray();
			ProsperoOuterPfsImage.Transform(array5, tuple.Item1, tuple.Item2, 65536, prosperoOuterPfsBuildResult.BlockKinds, encrypt: false);
			if (!array5.AsSpan().SequenceEqual(array4))
			{
				throw new InvalidDataException("Outer PFS AES-XTS round-trip failed.");
			}
		}
		File.WriteAllBytes(text5, prosperoOuterPfsBuildResult.Plaintext);
		int innerFileCount = ValidateInner(array2);
		return new ProsperoPublisherPprBuildResult
		{
			InnerPfsPath = text,
			LogicalImagePath = text2,
			PackedImagePath = text3,
			NapsLayoutPath = text4,
			OuterPfsPath = text5,
			OuterSeed = array3,
			OuterSuperblockIndex = prosperoOuterPfsBuildResult.SuperblockIndex,
			InnerFileCount = innerFileCount,
			InnerInodeCount = innerInodeCount,
			ImageDigests = imageDigests,
			LogicalImageDigest = ProsperoImageDigests.Sha3_256(array2),
			Naps = prosperoNapsBuildResult,
			OuterPfsEncrypted = options.EncryptOuterPfs
		};
	}

	private static byte[] BuildImageDigests(byte[] plaintext)
	{
		if (plaintext.Length % 65536 != 0)
		{
			throw new InvalidDataException("Outer PFS is not block aligned.");
		}
		int num = plaintext.Length / 65536;
		byte[] array = new byte[checked(num * 32)];
		for (int i = 0; i < num; i++)
		{
			byte[] array2 = ProsperoOuterPfsSignature.ComputeBlockHash(plaintext.AsSpan(i * 65536, 65536));
			Array.Reverse(array2);
			array2.CopyTo(array, i * 32);
		}
		return array;
	}

	private static int ValidateInner(byte[] logical)
	{
		using MemoryStream s = new MemoryStream(logical, writable: false);
		using LibProsperoPkg.Util.StreamReader r = new LibProsperoPkg.Util.StreamReader(s, 0L);
		PfsReader.File[] array = new PfsReader(r, 0uL, null, null, null, 4194304L, encryptedDataAlreadyDecrypted: true).GetAllFiles().ToArray();
		PfsReader.File[] array2 = array;
		for (int i = 0; i < array2.Length; i++)
		{
			array2[i].CopyTo(Stream.Null);
		}
		return array.Length;
	}

	private static int ValidateInner(string logicalPath)
	{
		using FileStream s = new FileStream(logicalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.RandomAccess);
		using LibProsperoPkg.Util.StreamReader r = new LibProsperoPkg.Util.StreamReader(s, 0L);
		PfsReader.File[] array = new PfsReader(r, 0uL, null, null, null, 4194304L, encryptedDataAlreadyDecrypted: true).GetAllFiles().ToArray();
		PfsReader.File[] array2 = array;
		for (int i = 0; i < array2.Length; i++)
		{
			array2[i].CopyTo(Stream.Null);
		}
		return array.Length;
	}

	private static void BuildLogicalImage(string standalonePath, string logicalPath)
	{
		using FileStream fileStream = new FileStream(standalonePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.RandomAccess);
		PfsHeader pfsHeader = PfsHeader.ReadFromStream(fileStream);
		if (pfsHeader.BlockSize != 65536 || pfsHeader.Mode.HasFlag(PfsMode.Signed))
		{
			throw new InvalidDataException("Publisher relocation requires an unsigned 64-KiB PFS image.");
		}
		int num = 4194304 / checked((int)pfsHeader.BlockSize);
		long num2 = pfsHeader.InodeBlockSig.StartBlock;
		if (num2 < 0 || num2 > fileStream.Length / pfsHeader.BlockSize)
		{
			throw new InvalidDataException("Standalone PFS has an invalid inode-table block.");
		}
		checked
		{
			using FileStream fileStream2 = new FileStream(logicalPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1048576, FileOptions.RandomAccess);
			fileStream2.SetLength(4194304 + fileStream.Length);
			fileStream2.Position = 0L;
			fileStream2.Write(PfsVersion);
			fileStream.Position = 0L;
			fileStream2.Position = 4194304L;
			fileStream.CopyTo(fileStream2, 1048576);
			pfsHeader.Mode |= PfsMode.PprDirectOffsets;
			pfsHeader.Ndblock += num;
			for (int i = 0; i < pfsHeader.InodeBlockSig.db.Length; i = unchecked(i + 1))
			{
				long block = pfsHeader.InodeBlockSig.db[i].block;
				if (block > 0)
				{
					pfsHeader.InodeBlockSig.db[i].block = block + num;
				}
			}
			fileStream2.Position = 4194304L;
			pfsHeader.WriteToStream(fileStream2);
			fileStream.Position = num2 * pfsHeader.BlockSize;
			fileStream2.Position = (num2 + num) * pfsHeader.BlockSize;
			for (long num3 = 0L; num3 < pfsHeader.DinodeCount; num3 = unchecked(num3 + 1))
			{
				DinodeD32 dinodeD = DinodeD32.ReadFromStream(fileStream);
				DinodePpr dinodePpr = new DinodePpr();
				dinodePpr.Mode = dinodeD.Mode;
				dinodePpr.Nlink = dinodeD.Nlink;
				dinodePpr.Flags = dinodeD.Flags;
				dinodePpr.Size = dinodeD.Size;
				dinodePpr.SizeCompressed = dinodeD.SizeCompressed;
				dinodePpr.Time1_sec = dinodeD.Time1_sec;
				dinodePpr.Time2_sec = dinodeD.Time2_sec;
				dinodePpr.Time3_sec = dinodeD.Time3_sec;
				dinodePpr.Time4_sec = dinodeD.Time4_sec;
				dinodePpr.Time1_nsec = dinodeD.Time1_nsec;
				dinodePpr.Time2_nsec = dinodeD.Time2_nsec;
				dinodePpr.Time3_nsec = dinodeD.Time3_nsec;
				dinodePpr.Time4_nsec = dinodeD.Time4_nsec;
				dinodePpr.Uid = dinodeD.Uid;
				dinodePpr.Gid = dinodeD.Gid;
				dinodePpr.Unk1 = dinodeD.Unk1;
				dinodePpr.Unk2 = dinodeD.Unk2;
				dinodePpr.Blocks = dinodeD.Blocks;
				dinodePpr.DataOffset = (dinodeD.StartBlock + num) * pfsHeader.BlockSize;
				dinodePpr.WriteToStream(fileStream2);
			}
			fileStream2.Flush(flushToDisk: true);
		}
	}

	private static byte[] BuildLogicalImage(byte[] standalone)
	{
		using MemoryStream memoryStream = new MemoryStream(standalone, writable: false);
		PfsHeader pfsHeader = PfsHeader.ReadFromStream(memoryStream);
		if (pfsHeader.BlockSize != 65536 || pfsHeader.Mode.HasFlag(PfsMode.Signed))
		{
			throw new InvalidDataException("Publisher relocation requires an unsigned 64-KiB PFS image.");
		}
		int num = 4194304 / checked((int)pfsHeader.BlockSize);
		long num2 = pfsHeader.InodeBlockSig.StartBlock;
		if (num2 < 0 || num2 > standalone.Length / pfsHeader.BlockSize)
		{
			throw new InvalidDataException("Standalone PFS has an invalid inode-table block.");
		}
		checked
		{
			byte[] array = new byte[4194304 + standalone.Length];
			PfsVersion.CopyTo(array);
			standalone.CopyTo(array, 4194304);
			pfsHeader.Mode |= PfsMode.PprDirectOffsets;
			pfsHeader.Ndblock += num;
			for (int i = 0; i < pfsHeader.InodeBlockSig.db.Length; i = unchecked(i + 1))
			{
				long block = pfsHeader.InodeBlockSig.db[i].block;
				if (block > 0)
				{
					pfsHeader.InodeBlockSig.db[i].block = block + num;
				}
			}
			using (MemoryStream memoryStream2 = new MemoryStream(array, writable: true))
			{
				memoryStream2.Position = 4194304L;
				pfsHeader.WriteToStream(memoryStream2);
				memoryStream.Position = num2 * pfsHeader.BlockSize;
				memoryStream2.Position = (num2 + num) * pfsHeader.BlockSize;
				for (long num3 = 0L; num3 < pfsHeader.DinodeCount; num3 = unchecked(num3 + 1))
				{
					DinodeD32 dinodeD = DinodeD32.ReadFromStream(memoryStream);
					DinodePpr dinodePpr = new DinodePpr();
					dinodePpr.Mode = dinodeD.Mode;
					dinodePpr.Nlink = dinodeD.Nlink;
					dinodePpr.Flags = dinodeD.Flags;
					dinodePpr.Size = dinodeD.Size;
					dinodePpr.SizeCompressed = dinodeD.SizeCompressed;
					dinodePpr.Time1_sec = dinodeD.Time1_sec;
					dinodePpr.Time2_sec = dinodeD.Time2_sec;
					dinodePpr.Time3_sec = dinodeD.Time3_sec;
					dinodePpr.Time4_sec = dinodeD.Time4_sec;
					dinodePpr.Time1_nsec = dinodeD.Time1_nsec;
					dinodePpr.Time2_nsec = dinodeD.Time2_nsec;
					dinodePpr.Time3_nsec = dinodeD.Time3_nsec;
					dinodePpr.Time4_nsec = dinodeD.Time4_nsec;
					dinodePpr.Uid = dinodeD.Uid;
					dinodePpr.Gid = dinodeD.Gid;
					dinodePpr.Unk1 = dinodeD.Unk1;
					dinodePpr.Unk2 = dinodeD.Unk2;
					dinodePpr.Blocks = dinodeD.Blocks;
					dinodePpr.DataOffset = (dinodeD.StartBlock + num) * pfsHeader.BlockSize;
					dinodePpr.WriteToStream(memoryStream2);
				}
			}
			return array;
		}
	}
}
