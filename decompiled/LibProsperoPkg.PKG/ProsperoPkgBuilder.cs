using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Enumeration;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using LibProsperoPkg.Content;
using LibProsperoPkg.GP5;
using LibProsperoPkg.PFS;
using LibProsperoPkg.PFS.Compression;
using LibProsperoPkg.PlayGo;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PKG;

public static class ProsperoPkgBuilder
{
	private readonly record struct ParamJsonInfo(string ContentVersion, string MasterVersion, string SdkVersion, string RequiredSystemSoftwareVersion, string ApplicationDrmType, string TitleName);

	private const uint DrmTypePs5 = 16u;

	private const uint ContentTypeGd = 32u;

	private const uint ContentTypeAc = 33u;

	private const uint ContentTypeAl = 34u;

	private const uint HeaderProfileCodePs5 = 12u;

	private const uint FlagsPs5 = 131073u;

	private const ulong LegacyPfsFlags = 9223372036854776780uL;

	private const ulong PublisherPfsFlags = 11529215046068470540uL;

	private const ulong BodyOffset = 8192uL;

	private const ulong PfsImageOffset = 524288uL;

	private const int BlockSize = 65536;

	private const uint ImagedigsEntryId = 1034u;

	private const uint PlayGoChunkDatEntryId = 4097u;

	private const int PfsSeedOffset = 880;

	private static readonly uint[] SystemMediaIds = new uint[10] { 4102u, 4109u, 4608u, 4640u, 4672u, 4736u, 4768u, 4800u, 8256u, 8288u };

	private static readonly uint[] PlaygoIds = new uint[3] { 4097u, 8208u, 8209u };

	private static readonly (string Name, uint Id)[] MediaFiles = new (string, uint)[7]
	{
		("icon0.png", 4608u),
		("pic0.png", 4640u),
		("pic1.png", 4102u),
		("pic2.png", 8256u),
		("snd0.at9", 4672u),
		("save_data.png", 4109u),
		("playgo-chunk.dat", 4097u)
	};

	private static readonly (string Png, string Dds, uint Id)[] DdsMedia = new (string, string, uint)[4]
	{
		("icon0.png", "icon0.dds", 4736u),
		("pic0.png", "pic0.dds", 4768u),
		("pic1.png", "pic1.dds", 4800u),
		("pic2.png", "pic2.dds", 8288u)
	};

	private static readonly HashSet<uint> GeneratedEntryIds = new HashSet<uint> { 4096u };

	public static uint ContentTypeFor(ProsperoVolumeType type)
	{
		return type switch
		{
			ProsperoVolumeType.AdditionalContentData => 33u, 
			ProsperoVolumeType.AdditionalContentNoData => 34u, 
			_ => 32u, 
		};
	}

	public static bool IsAdditionalContent(ProsperoVolumeType type)
	{
		if ((uint)(type - 1) <= 1u)
		{
			return true;
		}
		return false;
	}

	private static ContentFlags ContentFlagsFor(ProsperoVolumeType type)
	{
		return type switch
		{
			ProsperoVolumeType.AdditionalContentNoData => ContentFlags.UPGRADABLE_APPLICATION, 
			ProsperoVolumeType.AdditionalContentData => ContentFlags.GD_AC | ContentFlags.UPGRADABLE_APPLICATION, 
			_ => ContentFlags.GD_AC, 
		};
	}

	public static string Build(ProsperoPkgBuildProperties props, string outputPath, Action<string>? logger = null)
	{
		byte[] nestedImageDigest;
		ProsperoSiBuildInputs siInputs;
		return Build(props, outputPath, out nestedImageDigest, out siInputs, logger);
	}

	internal static string Build(ProsperoPkgBuildProperties props, string outputPath, out byte[]? nestedImageDigest, out ProsperoSiBuildInputs? siInputs, Action<string>? logger = null)
	{
		nestedImageDigest = null;
		siInputs = null;
		ArgumentNullException.ThrowIfNull(props, "props");
		ArgumentException.ThrowIfNullOrWhiteSpace(outputPath, "outputPath");
		Action<string> log = logger ?? ((Action<string>)((string _) =>
		{
		}));
		if (string.IsNullOrWhiteSpace(props.SourceFolder) || !Directory.Exists(props.SourceFolder))
		{
			throw new ArgumentException("Source folder does not exist.", "props");
		}
		string contentId = props.ContentId;
		if (contentId == null || contentId.Length != 36)
		{
			throw new ArgumentException("Content id must be exactly 36 characters.", "props");
		}
		contentId = props.PrimaryId;
		if (contentId != null && contentId.Length != 36)
		{
			throw new ArgumentException("Primary id must be exactly 36 characters.", "props");
		}
		contentId = props.Passcode;
		if (contentId == null || contentId.Length != 32)
		{
			throw new ArgumentException("Passcode must be exactly 32 characters.", "props");
		}
		int legacyZlibCompressionLevel = props.LegacyZlibCompressionLevel;
		if (legacyZlibCompressionLevel < 0 || legacyZlibCompressionLevel > 9)
		{
			throw new ArgumentOutOfRangeException("props", "Legacy zlib compression level must be in the range 0..9.");
		}
		if (props.LegacyZlibMaxDegreeOfParallelism < 0)
		{
			throw new ArgumentOutOfRangeException("props", "Legacy zlib parallelism must be zero (automatic) or positive.");
		}
		legacyZlibCompressionLevel = props.PlayGoChunkCount;
		if (legacyZlibCompressionLevel < 1 || legacyZlibCompressionLevel > 64)
		{
			throw new ArgumentOutOfRangeException("props", "PlayGo chunk count must be in the range 1..64.");
		}
		if (props.PlayGoChunkCount != 1 && !props.UsePublisherPprNaps)
		{
			throw new ArgumentException("Automatic multi-chunk PlayGo currently requires the publisher PPR-PFS/NAPS path.", "props");
		}
		ProsperoPublisherImageMode publisherImageMode = props.PublisherImageMode;
		if (publisherImageMode != ProsperoPublisherImageMode.Native && publisherImageMode != ProsperoPublisherImageMode.PlaintextNoAuth)
		{
			throw new ArgumentOutOfRangeException("props", props.PublisherImageMode, "Publisher image mode must be Native or PlaintextNoAuth.");
		}
		byte[] napsOuterBlockCmacKey = props.NapsOuterBlockCmacKey;
		if (napsOuterBlockCmacKey != null && napsOuterBlockCmacKey.Length != 16)
		{
			throw new ArgumentException("NAPS outer-block CMAC key must be exactly 16 bytes.", "props");
		}
		if (props.PublisherImageMode == ProsperoPublisherImageMode.PlaintextNoAuth && (!props.UsePublisherPprNaps || props.VolumeType == ProsperoVolumeType.AdditionalContentNoData))
		{
			throw new ArgumentException("PLAINTEXT_NOAUTH requires a data-bearing publisher PPR-PFS/NAPS package.", "props");
		}
		if (props.PublisherImageMode == ProsperoPublisherImageMode.PlaintextNoAuth && props.NapsOuterBlockCmacKey != null)
		{
			throw new ArgumentException("PLAINTEXT_NOAUTH cannot contain keyed NAPS outer-block authentication tags.", "props");
		}
		napsOuterBlockCmacKey = props.NapsPfsImageKey;
		if (napsOuterBlockCmacKey != null && napsOuterBlockCmacKey.Length != 32)
		{
			throw new ArgumentException("NAPS pfs-image-key must contain exactly 32 bytes.", "props");
		}
		napsOuterBlockCmacKey = props.NapsPfsImageSeed;
		if (napsOuterBlockCmacKey != null && napsOuterBlockCmacKey.Length != 16)
		{
			throw new ArgumentException("NAPS pfs-image-seed must contain exactly 16 bytes.", "props");
		}
		napsOuterBlockCmacKey = props.PublisherImageKey;
		if (napsOuterBlockCmacKey != null && napsOuterBlockCmacKey.Length != 2048)
		{
			throw new ArgumentException("Publisher IMAGE_KEY must contain exactly 0x800 bytes.", "props");
		}
		napsOuterBlockCmacKey = props.PublisherEntryKeys;
		if (napsOuterBlockCmacKey != null && napsOuterBlockCmacKey.Length != 2944)
		{
			throw new ArgumentException("Publisher ENTRY_KEYS must contain exactly 0xB80 bytes.", "props");
		}
		if (props.OuterPfsSeed != null && props.NapsPfsImageSeed != null && !((ReadOnlySpan<byte>)props.OuterPfsSeed.AsSpan()).SequenceEqual((ReadOnlySpan<byte>)props.NapsPfsImageSeed))
		{
			throw new ArgumentException("OuterPfsSeed and NapsPfsImageSeed identify the same publisher superblock seed and must match.", "props");
		}
		string sourceFolder = Path.GetFullPath(props.SourceFolder);
		byte[] ekpfs = ProsperoPfsKeys.DeriveEkpfs(props.ContentId, props.Passcode);
		string directoryName = Path.GetDirectoryName(Path.GetFullPath(outputPath));
		if (!string.IsNullOrEmpty(directoryName))
		{
			Directory.CreateDirectory(directoryName);
		}
		long fileTime = ToUnixSeconds(props.TimeStamp);
		byte[] capturedNestedDigest = null;
		ProsperoSiBuildInputs capturedSi = null;
		BuildImageOnce();
		nestedImageDigest = capturedNestedDigest;
		siInputs = capturedSi;
		log($"Done: {Path.GetFileName(outputPath)} ({new FileInfo(outputPath).Length:N0} bytes).");
		return outputPath;
		void BuildImageOnce()
		{
			if (props.VolumeType == ProsperoVolumeType.AdditionalContentNoData)
			{
				BuildAdditionalContentNoData(props, ekpfs, sourceFolder, outputPath, log);
				capturedNestedDigest = null;
				capturedSi = null;
			}
			else
			{
				log("Preparing PS5 inner PFS (superblock version 2)...");
				FSDir fSDir = BuildInnerTree(sourceFolder, props.Passcode, props.VolumeType);
				List<FSFile> allChildrenFiles = fSDir.GetAllChildrenFiles();
				uint playgoFileCount = (uint)Math.Min(allChildrenFiles.Count, 1048576);
				Math.Max(0L, allChildrenFiles.Sum((FSFile f) => f.Size));
				if (!props.UsePublisherPprNaps)
				{
					PfsBuilder pfsBuilder = new PfsBuilder(new PfsProperties
					{
						root = fSDir,
						BlockSize = 65536u,
						MinBlocks = 0u,
						Version = 2L,
						Encrypt = false,
						Sign = false,
						FileTime = fileTime
					}, (string s) =>
					{
						log(" [inner] " + s);
					});
					ProsperoInnerCompression prosperoInnerCompression = ResolveInnerCompression(props);
					byte[] innerImageDigest = null;
					if (prosperoInnerCompression != ProsperoInnerCompression.Zlib)
					{
						long num = pfsBuilder.CalculatePfsSize();
						if (num > 0 && num <= Array.MaxLength)
						{
							using MemoryStream memoryStream = new MemoryStream(checked((int)num));
							memoryStream.SetLength(num);
							pfsBuilder.WriteImage(memoryStream);
							innerImageDigest = (memoryStream.TryGetBuffer(out var buffer) ? ProsperoImageDigests.Sha3_256(buffer.AsSpan(0, (int)num)) : ProsperoImageDigests.Sha3_256(memoryStream.ToArray()));
						}
					}
					log("Preparing PS5 outer PFS (encrypted + signed)...");
					FSDir fSDir2 = new FSDir();
					string tmpRaw = null;
					string tmpPfsc = null;
					try
					{
						FSFile fSFile = prosperoInnerCompression switch
						{
							ProsperoInnerCompression.Zlib => BuildCompressedInnerFile(pfsBuilder, props.LegacyZlibCompressionLevel, props.LegacyZlibMaxDegreeOfParallelism, log, out tmpRaw, out tmpPfsc, out innerImageDigest), 
							ProsperoInnerCompression.Kraken => BuildKrakenInnerFile(pfsBuilder, log, out tmpRaw, out tmpPfsc), 
							_ => new FSFile(pfsBuilder), 
						};
						capturedNestedDigest = innerImageDigest;
						fSFile.Parent = fSDir2;
						fSDir2.Files.Add(fSFile);
						long num2 = (fSFile.Size + 65536 - 1) / 65536 * 65536;
						PfsBuilder pfsBuilder2 = new PfsBuilder(new PfsProperties
						{
							root = fSDir2,
							BlockSize = 65536u,
							Version = 2L,
							Encrypt = true,
							Sign = true,
							EKPFS = ekpfs,
							Seed = new byte[16],
							FileTime = fileTime
						}, (string s) =>
						{
							log(" [outer] " + s);
						})
						{
							CaptureImageDigests = true,
							CaptureSuperblockIcv = true
						};
						long num3 = pfsBuilder2.CalculatePfsSize();
						int imagedigsSize;
						ulong num4;
						checked
						{
							imagedigsSize = (int)unchecked(num3 / 65536) * 32;
							num4 = 65536 + (ulong)num3;
						}
						ulong num5 = Math.Min((ulong)num2, num4);
						ulong mchunk1Size = num4 - num5;
						Pkg pkg = BuildContainer(props, ekpfs, sourceFolder, (ulong)num3, imagedigsSize, playgoFileCount, num5, mchunk1Size);
						GenericEntry genericEntry = (GenericEntry)pkg.Entries.First((Entry e) => e.Id == EntryId.IMAGEDIGS_DAT);
						long num6 = (long)(pkg.Header.body_offset + pkg.Header.body_size + pkg.Header.pfs_image_size);
						using FileStream fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
						fileStream.SetLength(num6);
						log($"Writing outer PFS image at 0x{pkg.Header.pfs_image_offset:X} ({num3:N0} bytes)...");
						using (MemoryMappedFile file = MemoryMappedFile.CreateFromFile(fileStream, null, num6, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true))
						{
							pfsBuilder2.WriteImage(file, (long)pkg.Header.pfs_image_offset);
						}
						fileStream.Flush();
						byte[] imageDigests = pfsBuilder2.ImageDigests;
						if (imageDigests != null && imageDigests.Length != 0 && imageDigests.Length == genericEntry.FileData.Length)
						{
							genericEntry.FileData = ProsperoImageDigests.ToStoredImageDigestTable(imageDigests);
						}
						ProsperoPfsImageXmlOptions prosperoPfsImageXmlOptions = FinishContainer(pkg, fileStream, props, innerImageDigest, log, 0L, 0L);
						byte[] array = (pkg.Entries.FirstOrDefault((Entry e) => e.Id == EntryId.PLAYGO_CHUNK_DAT) as GenericEntry)?.FileData;
						long num7 = prosperoPfsImageXmlOptions.PfsImageOffset + prosperoPfsImageXmlOptions.PfsImageSize;
						prosperoPfsImageXmlOptions.OuterPfsTree = pfsBuilder2.CaptureImageTree();
						prosperoPfsImageXmlOptions.NestedPfsTree = pfsBuilder.CaptureImageTree();
						prosperoPfsImageXmlOptions.ChunkInfo = new ProsperoChunkInfoModel
						{
							PlayGoChunkDatSize = (array?.Length ?? 0),
							TotalSize = num7,
							Outer0Size = num2,
							Outer1Size = num7 - num2
						};
						capturedSi = new ProsperoSiBuildInputs
						{
							Xml = prosperoPfsImageXmlOptions,
							PlayGoChunkDat = array,
							InnerImageSize = num2,
							NapsLayoutSize = 0uL
						};
						return;
					}
					finally
					{
						TryDeleteTemp(tmpRaw);
						TryDeleteTemp(tmpPfsc);
					}
				}
				BuildPublisherImage(props, sourceFolder, outputPath, fSDir, log, out capturedNestedDigest, out capturedSi);
			}
		}
	}

	private static void BuildAdditionalContentNoData(ProsperoPkgBuildProperties props, byte[] ekpfs, string sourceFolder, string outputPath, Action<string> log)
	{
		log("Preparing PS5 Additional Content (PSAL) metadata-only CNT...");
		Pkg pkg = BuildContainer(props, ekpfs, sourceFolder, 0uL, 0, 0u, 0uL, 0uL);
		long num = checked((long)(pkg.Header.body_offset + pkg.Header.body_size));
		using FileStream fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
		fileStream.SetLength(num);
		FinishAdditionalContentNoDataContainer(pkg, fileStream, props);
		fileStream.Position = 0L;
		byte[] array = new byte[num];
		fileStream.ReadExactly(array);
		byte[] array2 = ProsperoSiArchive.WriteZip(ProsperoSiArchive.BuildMembers(props.ContentId, null, null, null, null, null, array));
		fileStream.Position = num;
		fileStream.Write(array2);
		log($"Appended PSAL SI segment: 0x{array2.Length:X} bytes.");
	}

	private static void BuildPublisherImage(ProsperoPkgBuildProperties props, string sourceFolder, string outputPath, FSDir innerRoot, Action<string> log, out byte[] nestedImageDigest, out ProsperoSiBuildInputs? siInputs)
	{
		bool flag = props.PublisherImageMode == ProsperoPublisherImageMode.PlaintextNoAuth;
		log(flag ? "Preparing PLAINTEXT_NOAUTH publisher PPR-PFS and NAPS image..." : "Preparing native publisher data-first PPR-PFS and NAPS image...");
		long num = ToUnixSeconds(props.TimeStamp);
		string text = Path.Combine(Path.GetTempPath(), "libprospero-publisher-" + Guid.NewGuid().ToString("N"));
		string text2 = text + ".pfs_image.dat";
		string path = text + ".naps_pkg_layout.dat";
		string text3 = text + ".outer.pfs";
		BuildCleaner.RegisterTempPath(text2);
		BuildCleaner.RegisterTempPath(path);
		BuildCleaner.RegisterTempPath(text3);
		Stopwatch stopwatch = Stopwatch.StartNew();
		log("[stage 1/5] Building and compressing the inner pfs_image.dat...");
		bool compress = props.InnerCompression == ProsperoInnerCompression.Kraken;
		ProsperoPs5InnerImageResult prosperoPs5InnerImageResult = new ProsperoPs5InnerImageAssembler(num, 0u, props.PublisherAfidAssignments, (string message) =>
		{
			log(" [inner] " + message);
		}, compress, props.CancellationToken, props.MaxHashingThreads).BuildFromFsTreeToFile(innerRoot, text2);
		props.CancellationToken.ThrowIfCancellationRequested();
		log($"[stage 1/5] Inner image complete: {prosperoPs5InnerImageResult.ImageLength:N0} bytes in {stopwatch.Elapsed:hh\\:mm\\:ss\\.fff}.");
		stopwatch.Restart();
		log("[stage 2/5] Generating NAPS file, block and integrity tables...");
		props.CancellationToken.ThrowIfCancellationRequested();
		byte[] array = ProsperoNwonlyNapsGenerator.Generate(prosperoPs5InnerImageResult, null, props.NapsOuterBlockCmacKey);
		props.CancellationToken.ThrowIfCancellationRequested();
		log($"[stage 2/5] NAPS layout complete: {array.Length:N0} bytes in {stopwatch.Elapsed:hh\\:mm\\:ss\\.fff}.");
		nestedImageDigest = ProsperoImageDigests.Sha3_256(array);
		List<string> list = (from n in prosperoPs5InnerImageResult.Nodes
			where !n.IsDirectory && n.ParentInode >= 0
			orderby n.Afid
			select n.FullPath).ToList();
		uint playgoFileCount;
		uint num2;
		int num3;
		uint num4;
		checked
		{
			playgoFileCount = (uint)list.Count * 2;
			num2 = (uint)ProsperoNapsLayout.Parse(array).Counts.NumFiles - 1;
			num3 = prosperoPs5InnerImageResult.Nodes.Count((ProsperoPs5MetaNode n) => !n.IsDirectory && n.ParentInode >= 0 && n.Mode == 33133);
			num4 = ContentVersionHigh(ReadParamJsonInfo(sourceFolder).ContentVersion);
		}
		long nestedMetaBaseBlocks = prosperoPs5InnerImageResult.MetaBaseLogical / 65536;
		byte[] outerPfsSeed = props.OuterPfsSeed;
		if (outerPfsSeed != null && outerPfsSeed.Length != 16)
		{
			throw new ArgumentException("Outer PFS seed must contain exactly 16 bytes.", "props");
		}
		byte[] array2 = props.NapsPfsImageSeed?.AsSpan().ToArray() ?? props.OuterPfsSeed?.AsSpan().ToArray() ?? (props.DeterministicBuild ? DeriveDeterministicOuterSeed(props.ContentId, props.Passcode) : RandomNumberGenerator.GetBytes(16));
		byte[] array3 = ProsperoPfsKeys.DerivePublisherPfsImageKey(props.PrimaryId ?? props.ContentId, props.Passcode, array2);
		if (props.NapsPfsImageKey != null && !CryptographicOperations.FixedTimeEquals(props.NapsPfsImageKey, array3))
		{
			throw new InvalidDataException("The supplied NAPS pfs-image-key does not match primary id, passcode and the effective outer-PFS seed.");
		}
		byte[] array4 = ProsperoPfsKeys.DeriveEkpfs(props.ContentId, props.Passcode);
		File.WriteAllBytes(path, array);
		bool flag2 = false;
		ProsperoOuterPackageFileResult prosperoOuterPackageFileResult;
		try
		{
			stopwatch.Restart();
			log(flag ? "[stage 3/5] Building plaintext/no-auth outer PFS..." : "[stage 3/5] Building and AES-XTS encrypting outer PFS...");
			prosperoOuterPackageFileResult = ProsperoOuterPfsBuilder.BuildForPackageToFile(new _003C_003Ez__ReadOnlyArray<ProsperoOuterFileSource>(new ProsperoOuterFileSource[2]
			{
				new ProsperoOuterFileSource
				{
					Name = "pfs_image.dat",
					Path = text2,
					SizeCompressed = prosperoPs5InnerImageResult.Ndblock * 65536,
					Signed = false
				},
				new ProsperoOuterFileSource
				{
					Name = "naps_pkg_layout.dat",
					Path = path,
					Signed = true
				}
			}), new ProsperoOuterPfsBuildParameters
			{
				TimestampSeconds = num,
				Seed = array2,
				ImageMode = props.PublisherImageMode
			}, flag ? null : array4, text3, !flag, log, props.CancellationToken, props.MaxHashingThreads);
			flag2 = true;
			log($"[stage 3/5] Outer PFS complete: {prosperoOuterPackageFileResult.PfsSize:N0} bytes, {prosperoOuterPackageFileResult.ImageDigests.Length:N0} digest blocks in {stopwatch.Elapsed:hh\\:mm\\:ss\\.fff}.");
		}
		finally
		{
			TryDeleteTemp(path);
			if (!flag2)
			{
				TryDeleteTemp(text2);
			}
		}
		ulong num5 = Align((ulong)prosperoPs5InnerImageResult.ImageLength, 65536uL);
		ulong num6 = checked(65536 + (ulong)prosperoOuterPackageFileResult.PfsSize);
		ulong num7 = Math.Min((num5 >= 65536) ? (num5 - 65536) : 0, num6);
		ulong mchunk1Size = num6 - num7;
		Pkg pkg = BuildContainer(props, array4, sourceFolder, (ulong)prosperoOuterPackageFileResult.PfsSize, prosperoOuterPackageFileResult.ImageDigests.Length, playgoFileCount, num7, mchunk1Size, list, publisherNwonly: true, array3);
		((GenericEntry)pkg.Entries.First((Entry e) => e.Id == EntryId.IMAGEDIGS_DAT)).FileData = ProsperoImageDigests.ToStoredImageDigestTable(prosperoOuterPackageFileResult.ImageDigests);
		bool flag3 = false;
		try
		{
			stopwatch.Restart();
			log($"[stage 4/5] Writing CNT bodies and outer image ({pkg.Entries.Count:N0} entries)...");
			long num8 = checked((long)(pkg.Header.body_offset + pkg.Header.body_size + pkg.Header.pfs_image_size));
			using FileStream fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
			fileStream.SetLength(num8);
			fileStream.Position = (long)pkg.Header.pfs_image_offset;
			using (FileStream fileStream2 = new FileStream(text3, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.SequentialScan))
			{
				byte[] array5 = new byte[1048576];
				long num9 = 0L;
				int num10;
				while ((num10 = fileStream2.Read(array5, 0, array5.Length)) > 0)
				{
					fileStream.Write(array5, 0, num10);
					num9 += num10;
					if ((num9 & 0x1FFFFFF) == 0L)
					{
						Thread.Sleep(8);
					}
				}
			}
			log($"Writing publisher outer PFS at 0x{pkg.Header.pfs_image_offset:X} ({prosperoOuterPackageFileResult.PfsSize:N0} bytes)...");
			ProsperoPfsImageXmlOptions prosperoPfsImageXmlOptions = FinishContainer(pkg, fileStream, props, nestedImageDigest, log, array.Length, nestedMetaBaseBlocks, num4, (int)num2, num3, prosperoPs5InnerImageResult.SparseAfidHoles.Count, prosperoPs5InnerImageResult.EmptyFileLogicalOffsets.Count, prosperoOuterPackageFileResult.SuperblockIndex);
			log($"[stage 4/5] CNT image complete: {num8:N0} bytes in {stopwatch.Elapsed:hh\\:mm\\:ss\\.fff}.");
			byte[] array6 = (pkg.Entries.FirstOrDefault((Entry e) => e.Id == EntryId.PLAYGO_CHUNK_DAT) as GenericEntry)?.FileData;
			long num11 = prosperoPfsImageXmlOptions.PfsImageOffset + prosperoPfsImageXmlOptions.PfsImageSize;
			prosperoPfsImageXmlOptions.OuterPfsTree = prosperoOuterPackageFileResult.Tree;
			prosperoPfsImageXmlOptions.ChunkInfo = new ProsperoChunkInfoModel
			{
				PlayGoChunkDatSize = (array6?.Length ?? 0),
				TotalSize = num11,
				Outer0Size = checked((long)num5),
				Outer1Size = num11 - (long)num5
			};
			List<(string, long)> list2 = (from n in prosperoPs5InnerImageResult.Nodes
				where !n.IsDirectory && n.ParentInode >= 0 && n.Size != 0
				orderby n.Afid
				select (Path: n.FullPath.TrimStart('/'), Size: n.Size)).ToList();
			list2.Add(("*PFSmetadata", prosperoOuterPackageFileResult.PfsSize));
			siInputs = new ProsperoSiBuildInputs
			{
				Xml = prosperoPfsImageXmlOptions,
				PlayGoChunkDat = array6,
				InnerImageSize = (long)num5,
				NapsLayoutSize = (ulong)array.Length,
				FihNapsFileCount = num2,
				SparseAfidCount = prosperoPs5InnerImageResult.SparseAfidHoles.Count,
				EmptyFileCount = prosperoPs5InnerImageResult.EmptyFileLogicalOffsets.Count,
				NapsMeta18 = props.NapsMeta18,
				NapsIntegrityProvider = props.NapsIntegrityProvider,
				NapsPfsImageKey = array3,
				NapsPfsImageSeed = array2,
				IncludePfsImageXml = false,
				ContentFiles = list2,
				InnerImage = prosperoPs5InnerImageResult,
				TemporaryInnerImagePath = text2,
				NestedMetaBaseBlocks = nestedMetaBaseBlocks,
				ContentVersionHigh = num4,
				AppFileCount = num3,
				OuterSuperblockIndex = prosperoOuterPackageFileResult.SuperblockIndex
			};
			log($"[stage 5/5] Finalization inputs ready: {list2.Count:N0} content records, PlayGo={props.PlayGoChunkCount} chunk(s), SI will be generated from the final mount image.");
			flag3 = true;
		}
		finally
		{
			TryDeleteTemp(text3);
			if (!flag3)
			{
				TryDeleteTemp(text2);
			}
		}
	}

	private static IReadOnlyList<(string Path, long Size)> ReadPfsContentFiles(string imagePath)
	{
		using FileStream s = File.OpenRead(imagePath);
		using LibProsperoPkg.Util.StreamReader r = new LibProsperoPkg.Util.StreamReader(s, 0L);
		return new PfsReader(r, 0uL, null, null, null, 0L, encryptedDataAlreadyDecrypted: true).GetAllFiles().Select((PfsReader.File file) =>
		{
			string text = file.FullName.Replace('\\', '/').TrimStart('/');
			if (text.StartsWith("uroot/", StringComparison.Ordinal))
			{
				text = text.Substring(6);
			}
			return (Path: text, Size: file.size);
		}).ToList();
	}

	private static ProsperoInnerCompression ResolveInnerCompression(ProsperoPkgBuildProperties props)
	{
		if (props.InnerCompression == ProsperoInnerCompression.None)
		{
			if (!props.CompressInnerImage)
			{
				return ProsperoInnerCompression.None;
			}
			return ProsperoInnerCompression.Zlib;
		}
		return props.InnerCompression;
	}

	private static FSFile BuildKrakenInnerFile(PfsBuilder innerPfs, Action<string> log, out string? tmpRaw, out string? tmpKraken)
	{
		tmpRaw = null;
		tmpKraken = null;
		long num = innerPfs.CalculatePfsSize();
		if (num > Array.MaxLength)
		{
			log($"Inner image is {num:N0} bytes; too large for the in-memory Kraken packer — storing it raw.");
			return new FSFile(innerPfs);
		}
		string text = Path.Combine(Path.GetTempPath(), "psmt_pfs_" + Guid.NewGuid().ToString("N") + ".raw");
		string text2 = Path.Combine(Path.GetTempPath(), "psmt_pfs_" + Guid.NewGuid().ToString("N") + ".kpfs");
		log($"Compressing inner pfs_image.dat ({num:N0} bytes raw) with Kraken (PFSv3)...");
		byte[] array;
		using (FileStream fileStream = new FileStream(text, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
		{
			innerPfs.WriteImage(fileStream);
			tmpRaw = text;
			fileStream.Flush();
			long length = fileStream.Length;
			fileStream.Position = 0L;
			array = new byte[length];
			fileStream.ReadExactly(array, 0, array.Length);
		}
		byte[] array2 = ProsperoCompressedPfsImage.Pack(array);
		byte[] array3 = CompressedPfsFile.Parse(array2).Decompress();
		bool flag = array3.Length == array.Length && ((ReadOnlySpan<byte>)array3.AsSpan()).SequenceEqual((ReadOnlySpan<byte>)array);
		if (!flag || array2.Length >= array.Length)
		{
			log(flag ? "Inner image is incompressible with Kraken; storing it raw." : "Kraken round-trip validation failed; storing the inner image raw.");
			TryDeleteTemp(tmpRaw);
			tmpRaw = null;
			return new FSFile(innerPfs);
		}
		File.WriteAllBytes(text2, array2);
		tmpKraken = text2;
		TryDeleteTemp(tmpRaw);
		tmpRaw = null;
		log($"Inner pfs_image.dat Kraken-compressed to {array2.Length:N0} bytes ({(double)array2.Length / (double)array.Length:P1} of raw).");
		long size = array2.Length;
		string krakenPath = text2;
		return new FSFile((Stream s) =>
		{
			using FileStream fileStream2 = File.OpenRead(krakenPath);
			fileStream2.CopyTo(s);
		}, "pfs_image.dat", size);
	}

	private static FSFile BuildCompressedInnerFile(PfsBuilder innerPfs, int zlibLevel, int maxDegreeOfParallelism, Action<string> log, out string? tmpRaw, out string? tmpPfsc, out byte[] innerImageDigest)
	{
		tmpRaw = null;
		tmpPfsc = null;
		innerImageDigest = Array.Empty<byte>();
		long num = innerPfs.CalculatePfsSize();
		int num2 = ((maxDegreeOfParallelism == 0) ? Math.Max(1, Environment.ProcessorCount) : maxDegreeOfParallelism);
		string text = Path.Combine(Path.GetTempPath(), "psmt_pfs_" + Guid.NewGuid().ToString("N") + ".raw");
		string text2 = Path.Combine(Path.GetTempPath(), "psmt_pfs_" + Guid.NewGuid().ToString("N") + ".pfsc");
		log($"Compressing inner pfs_image.dat ({num:N0} bytes raw, zlib level {zlibLevel}, {num2} worker(s))...");
		using (FileStream fileStream = new FileStream(text, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
		{
			fileStream.SetLength(num);
			fileStream.Position = 0L;
			innerPfs.WriteImage(fileStream);
			tmpRaw = text;
			fileStream.Flush();
			innerImageDigest = Crypto.Sha3_256(fileStream);
			PfscEncodeStats pfscEncodeStats;
			using (FileStream output = new FileStream(text2, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
			{
				fileStream.Position = 0L;
				pfscEncodeStats = PfscEncoder.Encode(fileStream, num, output, new PfscEncoderOptions
				{
					BlockSize = 65536,
					ZlibLevel = zlibLevel,
					MaxDegreeOfParallelism = num2
				});
			}
			tmpPfsc = text2;
			long length = new FileInfo(text2).Length;
			if (pfscEncodeStats.StoredRaw || length >= num)
			{
				log("Inner image is incompressible; storing it raw (size-stable PFSC wrapper).");
				TryDeleteTemp(tmpPfsc);
				tmpPfsc = null;
				return BuildStoredInnerFile(text, num);
			}
			log($"Inner pfs_image.dat compressed to {length:N0} bytes ({(double)length / (double)num:P1} of raw, {pfscEncodeStats.CompressedBlocks}/{pfscEncodeStats.BlockCount} blocks).");
		}
		TryDeleteTemp(tmpRaw);
		tmpRaw = null;
		string pfscPath = text2;
		long length2 = new FileInfo(pfscPath).Length;
		return new FSFile((Stream s) =>
		{
			using FileStream fileStream2 = File.OpenRead(pfscPath);
			fileStream2.CopyTo(s);
		}, "pfs_image.dat", length2, num, compress: true);
	}

	private static FSFile BuildStoredInnerFile(string rawPath, long rawSize)
	{
		PFSCWriter pfsc = new PFSCWriter(rawSize);
		return new FSFile((Stream destination) =>
		{
			pfsc.WritePFSCHeader(destination);
			using FileStream fileStream = new FileStream(rawPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.SequentialScan);
			fileStream.CopyTo(destination, 1048576);
		}, "pfs_image.dat", checked(rawSize + pfsc.HeaderSize), rawSize, compress: true);
	}

	private static void TryDeleteTemp(string? path)
	{
		if (string.IsNullOrEmpty(path))
		{
			return;
		}
		try
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	private static void TryDeleteTempDirectory(string path)
	{
		try
		{
			string fullPath = Path.GetFullPath(path);
			string fullPath2 = Path.GetFullPath(Path.GetTempPath());
			if (fullPath.StartsWith(fullPath2, StringComparison.OrdinalIgnoreCase) && Directory.Exists(fullPath))
			{
				Directory.Delete(fullPath, recursive: true);
			}
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	internal static FSDir BuildInnerTree(string sourceFolder, string passcode, ProsperoVolumeType volumeType)
	{
		FSDir fSDir = new FSDir();
		string text = Directory.EnumerateFiles(sourceFolder, "*.gp5", SearchOption.TopDirectoryOnly).OrderBy((string path) => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase).FirstOrDefault();
		if (text == null)
		{
			Populate(fSDir, sourceFolder);
		}
		else
		{
			Gp5Project gp5Project = Gp5Project.ReadFrom(text);
			string directoryName = Path.GetDirectoryName(text);
			if (gp5Project.Layout == Gp5Layout.Flat)
			{
				foreach (Gp5File file in gp5Project.Files)
				{
					AddManifestFile(fSDir, file, directoryName, "");
				}
				foreach (Gp5Dir folder in gp5Project.Folders)
				{
					ApplyManifestDirectory(fSDir, folder, directoryName, "");
				}
			}
			else
			{
				string text2 = ResolveManifestPath(directoryName, gp5Project.RootDir.SourcePath, directoryName);
				if (!Directory.Exists(text2))
				{
					throw new DirectoryNotFoundException("GP5 rootdir source was not found: " + text2);
				}
				string[] readOnlyList = ParseExcludeMasks(gp5Project.RootDir.DirExclude);
				string[] readOnlyList2 = ParseExcludeMasks(gp5Project.RootDir.FileExclude);
				string[] readOnlyList3 = ParseExcludeMasks(gp5Project.GlobalExclude);
				Populate(fSDir, text2, text2, readOnlyList, readOnlyList2, readOnlyList3);
				foreach (Gp5Dir directory in gp5Project.RootDir.Directories)
				{
					ApplyManifestDirectory(fSDir, directory, directoryName, "");
				}
				foreach (Gp5File file2 in gp5Project.RootDir.Files)
				{
					AddManifestFile(fSDir, file2, directoryName, "", replaceExisting: true);
				}
			}
		}
		if (volumeType == ProsperoVolumeType.Application)
		{
			ConvertLooseElfExecutables(fSDir);
		}
		FSDir fSDir2 = fSDir.Dirs.FirstOrDefault((FSDir d) => d.name == "sce_sys");
		if (fSDir2 != null)
		{
			if (!fSDir2.Files.Any((FSFile f) => f.name == "pfs-version.dat"))
			{
				byte[] bytes = Encoding.ASCII.GetBytes(ReadParamJsonInfo(sourceFolder).ContentVersion);
				AddFile(fSDir2, "pfs-version.dat", bytes);
			}
			if (volumeType == ProsperoVolumeType.Application && !fSDir2.Files.Any((FSFile f) => f.name == "keystone"))
			{
				byte[] array = Crypto.CreateKeystone(passcode, 3);
				AddFile(fSDir2, "keystone", array);
			}
			if (volumeType == ProsperoVolumeType.Application)
			{
				EnsureAboutRightSprx(fSDir2);
			}
			EnsureUcpArchives(fSDir2);
		}
		return fSDir;
		static void AddFile(FSDir dir, string name, byte[] array2)
		{
			dir.Files.Add(new FSFile((Stream s) =>
			{
				s.Write(array2, 0, array2.Length);
			}, name, array2.Length)
			{
				Parent = dir
			});
		}
		static void AddManifestFile(FSDir rootDir, Gp5File file, string projectDirectory, string destinationPrefix, bool replaceExisting = false)
		{
			string text3 = CombineDestination(destinationPrefix, file.DestinationPath);
			if (!string.IsNullOrWhiteSpace(text3))
			{
				string text4 = ResolveManifestPath(projectDirectory, file.SourcePath, Path.Combine(projectDirectory, text3.Replace('/', Path.DirectorySeparatorChar)));
				if (!File.Exists(text4))
				{
					throw new FileNotFoundException("GP5 source file was not found for '" + text3 + "'.", text4);
				}
				AddMappedFile(rootDir, text3, text4, replaceExisting);
			}
		}
		static void AddMappedFile(FSDir rootDir, string destination, string sourcePath, bool replaceExisting = false)
		{
			string[] parts = SplitDestination(destination);
			if (parts.Length != 0)
			{
				FSDir fSDir3 = rootDir;
				int i;
				for (i = 0; i < parts.Length - 1; i++)
				{
					FSDir fSDir4 = fSDir3.Dirs.FirstOrDefault((FSDir d) => d.name == parts[i]);
					if (fSDir4 == null)
					{
						fSDir4 = new FSDir
						{
							name = parts[i],
							Parent = fSDir3
						};
						fSDir3.Dirs.Add(fSDir4);
					}
					fSDir3 = fSDir4;
				}
				string name = parts[^1];
				FSFile fSFile = fSDir3.Files.FirstOrDefault((FSFile file) => string.Equals(file.name, name, StringComparison.Ordinal));
				if (fSFile != null && !replaceExisting)
				{
					throw new InvalidDataException("GP5 declares destination '" + destination + "' more than once.");
				}
				if (fSFile != null)
				{
					fSDir3.Files.Remove(fSFile);
				}
				fSDir3.Files.Add(new FSFile(sourcePath)
				{
					name = name,
					Parent = fSDir3
				});
			}
		}
		static void ApplyManifestDirectory(FSDir rootDir, Gp5Dir mapping, string projectDirectory, string destinationPrefix)
		{
			string text3 = CombineDestination(destinationPrefix, mapping.DestinationPath);
			FSDir node = EnsureMappedDirectory(rootDir, text3, !mapping.Virtual);
			if (!string.IsNullOrWhiteSpace(mapping.SourcePath))
			{
				string text4 = ResolveManifestPath(projectDirectory, mapping.SourcePath, projectDirectory);
				if (!Directory.Exists(text4))
				{
					throw new DirectoryNotFoundException("GP5 source directory was not found for '" + text3 + "': " + text4);
				}
				Populate(node, text4);
			}
			foreach (Gp5Dir directory2 in mapping.Directories)
			{
				ApplyManifestDirectory(rootDir, directory2, projectDirectory, text3);
			}
			foreach (Gp5File file3 in mapping.Files)
			{
				AddManifestFile(rootDir, file3, projectDirectory, text3, replaceExisting: true);
			}
		}
		static string CombineDestination(string prefix, string child)
		{
			return string.Join('/', from value in new string[2] { prefix, child }
				where !string.IsNullOrWhiteSpace(value)
				select value.Replace('\\', '/').Trim('/'));
		}
		static void ConvertLooseElfExecutables(FSDir rootDir)
		{
			byte[] applicationSceVersion = null;
			FSFile fSFile = rootDir.GetAllChildrenFiles().FirstOrDefault((FSFile file) => file.FullPath().Replace('\\', '/').Equals("/eboot.bin", StringComparison.Ordinal));
			if (fSFile != null)
			{
				ProsperoFself.TryGetSceVersionRecord(ReadNode(fSFile), out applicationSceVersion);
				if (applicationSceVersion.Length == 0)
				{
					applicationSceVersion = null;
				}
			}
			ConvertDirectory(rootDir);
			void ConvertDirectory(FSDir directory)
			{
				for (int i = 0; i < directory.Files.Count; i++)
				{
					FSFile fSFile2 = directory.Files[i];
					string text3 = fSFile2.FullPath().Replace('\\', '/');
					string text4 = Path.GetExtension(fSFile2.name).ToLowerInvariant();
					bool flag = text3.Equals("/eboot.bin", StringComparison.Ordinal) || text3.StartsWith("/sce_module/", StringComparison.Ordinal) || text3.StartsWith("/sce_sys/about/", StringComparison.Ordinal);
					if (!flag)
					{
						bool flag2;
						switch (text4)
						{
						case ".prx":
						case ".sprx":
						case ".elf":
						case ".bin":
							flag2 = true;
							break;
						default:
							flag2 = false;
							break;
						}
						flag = flag2;
					}
					if (flag)
					{
						byte[] array2 = ReadNode(fSFile2);
						if (ProsperoFself.IsElf(array2))
						{
							FselfOptions options = null;
							if (!text3.Equals("/eboot.bin", StringComparison.Ordinal) && applicationSceVersion != null && !ProsperoFself.TryGetSceVersionRecord(array2, out var _))
							{
								options = new FselfOptions
								{
									SceVersionName = Path.GetFileNameWithoutExtension(fSFile2.name),
									SceVersionRecord = applicationSceVersion
								};
							}
							byte[] fself = ProsperoFself.MakeFself(array2, options);
							directory.Files[i] = new FSFile((Stream output) =>
							{
								output.Write(fself, 0, fself.Length);
							}, fSFile2.name, fself.Length)
							{
								Parent = directory,
								LayoutPriority = fSFile2.LayoutPriority
							};
						}
					}
				}
				foreach (FSDir dir in directory.Dirs)
				{
					ConvertDirectory(dir);
				}
			}
		}
		static FSDir EnsureMappedDirectory(FSDir rootDir, string destination, bool replaceLeaf)
		{
			string[] parts = SplitDestination(destination);
			FSDir fSDir3 = rootDir;
			int i;
			for (i = 0; i < parts.Length; i++)
			{
				FSDir fSDir4 = fSDir3.Dirs.FirstOrDefault((FSDir child) => string.Equals(child.name, parts[i], StringComparison.Ordinal));
				if (((fSDir4 != null) & replaceLeaf) && i == parts.Length - 1)
				{
					fSDir3.Dirs.Remove(fSDir4);
					fSDir4 = null;
				}
				if (fSDir4 == null)
				{
					fSDir4 = new FSDir
					{
						name = parts[i],
						Parent = fSDir3
					};
					fSDir3.Dirs.Add(fSDir4);
				}
				fSDir3 = fSDir4;
			}
			return fSDir3;
		}
		static bool IsExcluded(string name, string relativePath, IReadOnlyList<string> masks)
		{
			string normalized = relativePath.Replace('\\', '/');
			return masks.Any((string mask) => FileSystemName.MatchesSimpleExpression(mask, name) || FileSystemName.MatchesSimpleExpression(mask.Replace('\\', '/'), normalized));
		}
		static string[] ParseExcludeMasks(string? value)
		{
			if (!string.IsNullOrWhiteSpace(value))
			{
				return value.Split(new char[2] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			}
			return Array.Empty<string>();
		}
		static void Populate(FSDir node, string path, string? sourceRoot = null, IReadOnlyList<string>? readOnlyList4 = null, IReadOnlyList<string>? readOnlyList5 = null, IReadOnlyList<string>? readOnlyList6 = null)
		{
			if (sourceRoot == null)
			{
				sourceRoot = path;
			}
			if (readOnlyList4 == null)
			{
				readOnlyList4 = Array.Empty<string>();
			}
			if (readOnlyList5 == null)
			{
				readOnlyList5 = Array.Empty<string>();
			}
			if (readOnlyList6 == null)
			{
				readOnlyList6 = Array.Empty<string>();
			}
			foreach (string item in Directory.EnumerateDirectories(path).OrderBy(Path.GetFileName, StringComparer.Ordinal))
			{
				string relativePath = Path.GetRelativePath(sourceRoot, item);
				if (!IsExcluded(Path.GetFileName(item), relativePath, readOnlyList4) && !IsExcluded(Path.GetFileName(item), relativePath, readOnlyList6))
				{
					FSDir fSDir3 = new FSDir
					{
						name = Path.GetFileName(item),
						Parent = node
					};
					node.Dirs.Add(fSDir3);
					Populate(fSDir3, item, sourceRoot, readOnlyList4, readOnlyList5, readOnlyList6);
				}
			}
			foreach (string item2 in Directory.EnumerateFiles(path).OrderBy(Path.GetFileName, StringComparer.Ordinal))
			{
				string fileName = Path.GetFileName(item2);
				if (!fileName.EndsWith(".gp4", StringComparison.OrdinalIgnoreCase) && !fileName.EndsWith(".gp5", StringComparison.OrdinalIgnoreCase))
				{
					string relativePath2 = Path.GetRelativePath(sourceRoot, item2);
					if (!IsExcluded(fileName, relativePath2, readOnlyList5) && !IsExcluded(fileName, relativePath2, readOnlyList6))
					{
						node.Files.Add(new FSFile(item2)
						{
							name = fileName,
							Parent = node
						});
					}
				}
			}
		}
		static string ResolveManifestPath(string projectDirectory, string? source, string fallback)
		{
			string name = (string.IsNullOrWhiteSpace(source) ? fallback : source);
			name = Environment.ExpandEnvironmentVariables(name).Replace('\\', Path.DirectorySeparatorChar);
			return Path.GetFullPath(Path.IsPathRooted(name) ? name : Path.Combine(projectDirectory, name));
		}
		static string[] SplitDestination(string destination)
		{
			return destination.Replace('\\', '/').Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
		}
	}

	private static void EnsureAboutRightSprx(FSDir sceSys)
	{
		FSDir fSDir = FindDir(sceSys, "about");
		byte[] rightSprx = ProsperoPlayGo.GetRightSprx();
		if (rightSprx != null && rightSprx.Length != 0)
		{
			if (fSDir == null)
			{
				fSDir = new FSDir
				{
					name = "about",
					Parent = sceSys
				};
				sceSys.Dirs.Add(fSDir);
			}
			fSDir.Files.RemoveAll((FSFile f) => string.Equals(f.name, "right.sprx", StringComparison.OrdinalIgnoreCase));
			AddInMemoryFile(fSDir, "right.sprx", rightSprx);
		}
	}

	private static void EnsureUcpArchives(FSDir sceSys)
	{
		string[] array = new string[2] { "trophy2", "uds" };
		foreach (string name in array)
		{
			FSDir fSDir = FindDir(sceSys, name);
			if (fSDir == null)
			{
				continue;
			}
			for (int j = 0; j < fSDir.Files.Count; j++)
			{
				FSFile fSFile = fSDir.Files[j];
				if (!fSFile.name.EndsWith(".ucp", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				byte[] array2 = ReadNode(fSFile);
				if (ProsperoUcp.IsUcp(array2) && !ProsperoUcp.VerifyDigest(array2))
				{
					byte[] repaired = ProsperoUcp.WithRepairedDigest(array2);
					fSDir.Files[j] = new FSFile((Stream s) =>
					{
						s.Write(repaired, 0, repaired.Length);
					}, fSFile.name, repaired.Length)
					{
						Parent = fSDir
					};
				}
			}
		}
	}

	private static FSDir? FindDir(FSDir parent, string name)
	{
		return parent.Dirs.FirstOrDefault((FSDir d) => d.name == name);
	}

	private static void AddInMemoryFile(FSDir dir, string name, byte[] data)
	{
		dir.Files.Add(new FSFile((Stream s) =>
		{
			s.Write(data, 0, data.Length);
		}, name, data.Length)
		{
			Parent = dir
		});
	}

	private static byte[] ReadNode(FSFile file)
	{
		using MemoryStream memoryStream = new MemoryStream();
		file.Write(memoryStream);
		return memoryStream.ToArray();
	}

	private static Pkg BuildContainer(ProsperoPkgBuildProperties props, byte[] ekpfs, string sourceFolder, ulong pfsSize, int imagedigsSize, uint playgoFileCount, ulong mchunk0Size, ulong mchunk1Size, IReadOnlyList<string>? playgoPaths = null, bool publisherNwonly = false, byte[]? publisherPfsImageKey = null)
	{
		bool flag = props.VolumeType == ProsperoVolumeType.AdditionalContentNoData;
		bool flag2 = props.VolumeType == ProsperoVolumeType.Application && ReadParamJsonInfo(sourceFolder).ApplicationDrmType.Equals("upgradable", StringComparison.OrdinalIgnoreCase);
		uint content_type = ContentTypeFor(props.VolumeType);
		Pkg pkg = new Pkg();
		Header header = new Header
		{
			CNTMagic = "\u007fCNT",
			flags = (PKGFlags)131073u,
			ps5_profile_marker = 2147483648u,
			header_profile_code = 12u,
			entry_count = 0u,
			sc_entry_count = (ushort)(flag ? 5u : 6u),
			entry_count_2 = 0,
			entry_table_offset = 0u,
			main_ent_data_size = 0u,
			body_offset = 8192uL,
			body_size = 0uL,
			content_id = props.ContentId,
			drm_type = (((props.VolumeType != ProsperoVolumeType.Application) | flag2) ? 16u : 0u),
			content_type = content_type,
			content_flags = (ContentFlags)((uint)ContentFlagsFor(props.VolumeType) | (uint)((props.UsePublisherPprNaps && !flag) ? 131072 : 0) | (uint)(flag2 ? 134217728 : 0)),
			promote_size = 0u,
			version_date = 539231496u,
			version_hash = 152027073u,
			iro_tag = IROTag.None
		};
		ProsperoVolumeType volumeType = props.VolumeType;
		bool flag3 = ((volumeType == ProsperoVolumeType.Application || volumeType == ProsperoVolumeType.AdditionalContentNoData) ? true : false);
		header.ekc_version = ((!flag3 | flag2) ? 1u : 0u);
		header.sc_entries1_hash = new byte[32];
		header.sc_entries2_hash = new byte[32];
		header.digest_table_hash = new byte[32];
		header.body_digest = new byte[32];
		header.pfs_descriptor_presence = ((!flag) ? 1u : 0u);
		header.pfs_image_count = ((!flag) ? 1u : 0u);
		ulong pfs_flags;
		if (flag)
		{
			pfs_flags = 0uL;
		}
		else
		{
			pfs_flags = (props.UsePublisherPprNaps ? 11529215046068470540uL : 9223372036854776780uL);
		}
		header.pfs_flags = pfs_flags;
		header.pfs_image_offset = (ulong)(int)((!flag) ? 524288u : 0u);
		header.pfs_image_size = pfsSize;
		header.mount_image_offset = 0uL;
		header.mount_image_size = 0uL;
		header.package_size = (flag ? 0 : (524288 + pfsSize));
		header.pfs_signed_size = ((!flag) ? 65536u : 0u);
		header.pfs_cache_size = ((!flag) ? ((!props.UsePublisherPprNaps) ? 851968u : 0u) : 0u);
		header.pfs_image_digest = new byte[32];
		header.pfs_signed_digest = new byte[32];
		header.pfs_split_size_nth_0 = 0uL;
		header.pfs_split_size_nth_1 = 0uL;
		header.image_seed = new byte[16];
		header.cnt_region_offset = 0uL;
		header.cnt_region_size = 0uL;
		header.desc_digest = new byte[64];
		pkg.Header = header;
		pkg.HeaderDigest = new byte[32];
		pkg.HeaderSignature = new byte[384];
		Pkg pkg2 = pkg;
		pkg2.EntryKeys = ((props.PublisherEntryKeys != null) ? KeysEntry.FromPublisherBytes(props.PublisherEntryKeys) : new KeysEntry(props.ContentId, props.Passcode, props.UsePublisherPprNaps, props.UsePublisherPprNaps || props.DeterministicBuild, props.PrimaryId));
		byte[] array = null;
		if (!flag && props.UsePublisherPprNaps)
		{
			if (publisherPfsImageKey == null || publisherPfsImageKey.Length != 32)
			{
				throw new InvalidDataException("The publisher IMAGE_KEY producer requires the derived 32-byte pfs-image-key.");
			}
			array = props.PublisherImageKey?.AsSpan().ToArray() ?? ProsperoPfsKeys.BuildPublisherImageKey(publisherPfsImageKey);
		}
		else if (!flag)
		{
			array = Crypto.RSA2048EncryptKey(RSAKeyset.FakeKeyset.Modulus, ekpfs);
		}
		if (array != null)
		{
			pkg2.ImageKey = new GenericEntry(EntryId.IMAGE_KEY)
			{
				FileData = array
			};
		}
		pkg2.GeneralDigests = new GeneralDigestsEntry
		{
			type = 258
		};
		pkg2.Metas = new MetasEntry();
		pkg2.Digests = new GenericEntry(EntryId.DIGESTS);
		pkg2.EntryNames = new NameTableEntry();
		byte[] array2 = BuildPublisherParamJson(sourceFolder, props);
		GenericEntry item = new GenericEntry((EntryId)8192u, "param.json")
		{
			FileData = array2
		};
		pkg2.Entries = new List<Entry> { pkg2.EntryKeys, pkg2.GeneralDigests, pkg2.Metas, pkg2.Digests, pkg2.EntryNames, item };
		if (pkg2.ImageKey != null)
		{
			pkg2.Entries.Insert(1, pkg2.ImageKey);
		}
		foreach (Entry media in CollectMediaEntries(sourceFolder, props.VolumeType, props.ContentId, props.LicenseProvider, !flag))
		{
			if (!pkg2.Entries.Any((Entry existing) => existing.Id == media.Id))
			{
				pkg2.Entries.Add(media);
			}
		}
		if (!flag)
		{
			(uint, string, byte[])[] array3 = new (uint, string, byte[])[4]
			{
				(1034u, null, new byte[imagedigsSize]),
				(4097u, "playgo-chunk.dat", ProsperoPlayGo.BuildChunkDat(props.ContentId, mchunk0Size, mchunk1Size, publisherNwonly, publisherNwonly && props.VolumeType == ProsperoVolumeType.Application, props.PlayGoChunkCount)),
				(8208u, "playgo-hash-table.dat", (playgoPaths != null) ? ProsperoPlayGo.BuildHashTable(playgoPaths) : ProsperoPlayGo.BuildHashTable(playgoFileCount / 2)),
				(8209u, "playgo-ficm.dat", publisherNwonly ? ProsperoPlayGo.BuildFicm(ProsperoPlayGo.BuildAutomaticFileChunkIds(checked((int)unchecked(playgoFileCount / 2)), props.PlayGoChunkCount)) : ProsperoPlayGo.BuildFicm(playgoFileCount))
			};
			for (int num = 0; num < array3.Length; num++)
			{
				var (id, name, fileData) = array3[num];
				if (!pkg2.Entries.Any((Entry e) => e.Id == (EntryId)id))
				{
					pkg2.Entries.Add(new GenericEntry((EntryId)id, name)
					{
						FileData = fileData
					});
				}
			}
		}
		Entry[] first = pkg2.Entries.Take(6).ToArray();
		Entry[] second = pkg2.Entries.Skip(6).OrderBy(PublisherBodyRank).ThenBy((Entry e) => (uint)e.Id)
			.ToArray();
		pkg2.Entries = first.Concat(second).ToList();
		pkg2.Digests.FileData = new byte[pkg2.Entries.Count * 32];
		LayOutEntries(pkg2, array2, props.VolumeType, ReadParamJsonInfo(sourceFolder).ApplicationDrmType);
		return pkg2;
	}

	private static int PublisherBodyRank(Entry entry)
	{
		uint id = (uint)entry.Id;
		bool flag;
		switch (id)
		{
		case 8192u:
			return 0;
		case 1034u:
		case 4097u:
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (flag)
		{
			return 2;
		}
		if (id - 8208 <= 1)
		{
			return 4;
		}
		flag = id >= 4608 && id < 8192;
		if (!flag)
		{
			flag = ((id == 4102 || id == 4109) ? true : false);
		}
		if (flag)
		{
			return 3;
		}
		return 1;
	}

	private static void LayOutEntries(Pkg pkg, byte[] paramJson, ProsperoVolumeType volumeType, string applicationDrmType)
	{
		foreach (Entry item in pkg.Entries.OrderBy((Entry e) => (uint)e.Id))
		{
			if (ProsperoCntEntryPolicy.Resolve((uint)item.Id, volumeType, item.Name, applicationDrmType).IncludeName)
			{
				pkg.EntryNames.GetOffset(item.Name);
			}
		}
		ulong num = pkg.Header.body_offset;
		foreach (Entry entry in pkg.Entries)
		{
			ProsperoCntEntryProfile prosperoCntEntryProfile = ProsperoCntEntryPolicy.Resolve((uint)entry.Id, volumeType, entry.Name, applicationDrmType);
			MetaEntry metaEntry = new MetaEntry
			{
				id = entry.Id,
				NameTableOffset = (prosperoCntEntryProfile.IncludeName ? pkg.EntryNames.GetOffset(entry.Name) : 0u),
				DataOffset = (uint)num,
				DataSize = entry.Length,
				Flags1 = prosperoCntEntryProfile.Flags1,
				Flags2 = prosperoCntEntryProfile.Flags2
			};
			pkg.Metas.Metas.Add(metaEntry);
			if (entry == pkg.Metas)
			{
				metaEntry.DataSize = (uint)(pkg.Entries.Count * 32);
			}
			num = Align(num + metaEntry.DataSize, 16uL);
			entry.meta = metaEntry;
		}
		ulong num2 = num - pkg.Header.body_offset;
		pkg.Metas.Metas.Sort((MetaEntry a, MetaEntry b) => a.id.CompareTo(b.id));
		pkg.Header.entry_count = (uint)pkg.Entries.Count;
		pkg.Header.entry_count_2 = (ushort)pkg.Entries.Count;
		pkg.Header.entry_table_offset = pkg.Metas.meta.DataOffset;
		ulong align = (ulong)(int)((pkg.EntryKeys.Length == 2944) ? 65536u : 524288u);
		pkg.Header.body_size = Align(pkg.Header.body_offset + num2, align) - pkg.Header.body_offset;
		if (pkg.Header.content_type == 34)
		{
			pkg.Header.body_size = Math.Max(pkg.Header.body_size, 122880uL);
		}
		pkg.Header.main_ent_data_size = checked((uint)pkg.Entries.Take(pkg.Header.sc_entry_count - 1).Sum((Entry x) => x.Length));
		bool flag = pkg.Header.content_type == 34;
		pkg.Header.pfs_image_offset = (flag ? 0 : (pkg.Header.body_offset + pkg.Header.body_size));
		ulong pfs_image_offset = pkg.Header.pfs_image_offset;
		if (pkg.Header.content_type == 32)
		{
			pkg.Header.promote_size = checked((uint)pfs_image_offset);
		}
		bool flag2 = pkg.EntryKeys.Length == 2944;
		ulong num3 = (ulong)(int)(flag2 ? 65536u : 0u);
		pkg.Header.package_size = (pkg.Header.mount_image_size = (flag ? 0 : (num3 + pkg.Header.pfs_image_size + pfs_image_offset)));
		if (flag)
		{
			pkg.Header.mandatory_size = ((IEnumerable<MetaEntry>)(from m in pkg.Metas.Metas.Where((MetaEntry m) =>
				{
					uint id = (uint)m.id;
					return id >= 4096 && id < 8192;
				})
				orderby m.DataOffset
				select m)).Select((Func<MetaEntry, ulong>)((MetaEntry m) => m.DataOffset)).DefaultIfEmpty(pkg.Header.body_offset + pkg.Header.body_size).First();
		}
		else if (flag2)
		{
			MetaEntry metaEntry2 = pkg.Metas.Metas.First((MetaEntry m) => m.id == EntryId.IMAGEDIGS_DAT);
			pkg.Header.mandatory_size = metaEntry2.DataOffset;
			pkg.Header.cnt_region_offset = 65536 + pkg.Header.pfs_image_size;
			pkg.Header.cnt_region_size = pfs_image_offset;
			pkg.Header.desc_image_key_offset = pkg.ImageKey.meta.DataOffset;
			pkg.Header.desc_image_key_size = pkg.ImageKey.meta.DataSize;
			pkg.Header.desc_mandatory_offset = metaEntry2.DataOffset;
			pkg.Header.desc_mandatory_size = metaEntry2.DataSize;
		}
	}

	private static void FinishAdditionalContentNoDataContainer(Pkg pkg, Stream stream, ProsperoPkgBuildProperties props)
	{
		foreach (KeyValuePair<GeneralDigest, byte[]> item in ComputeGeneralDigests(pkg))
		{
			pkg.GeneralDigests.Set(item.Key, item.Value);
		}
		PkgWriter pkgWriter = new PkgWriter(stream);
		pkgWriter.WriteBody(pkg, props.ContentId, props.Passcode);
		CalcBodyDigests(pkg, stream);
		stream.Position = 0L;
		pkgWriter.WriteHeader(in pkg.Header);
		stream.Position = 0L;
		byte[] array = new byte[4064];
		stream.ReadExactly(array);
		pkg.HeaderDigest = ProsperoImageDigests.ComputePackageDigest(array);
		stream.Position = 4064L;
		stream.Write(pkg.HeaderDigest);
		stream.Position = 0L;
		byte[] array2 = new byte[4096];
		stream.ReadExactly(array2);
		pkg.HeaderSignature = ProsperoPublisherRsa.BuildCntHeaderWrap(array2);
		stream.Position = 4096L;
		stream.Write(pkg.HeaderSignature);
	}

	private static ProsperoPfsImageXmlOptions FinishContainer(Pkg pkg, Stream s, ProsperoPkgBuildProperties props, byte[]? nestedImageDigest, Action<string> log, long nestedImageSize = 0L, long nestedMetaBaseBlocks = 0L, uint nwonlyContentVersionHi = 0u, int nwonlyNapsFileCount = 0, int nwonlyAppFileCount = 0, int nwonlySparseAfidCount = 0, int nwonlyEmptyFileCount = 0, int knownOuterSuperblockIndex = -1)
	{
		log("Calculating PFS image digests (SHA3-256)...");
		long num2;
		byte[] array;
		byte[] array3;
		checked
		{
			long num = (long)pkg.Header.pfs_image_offset;
			long size = (long)pkg.Header.pfs_image_size;
			num2 = ((knownOuterSuperblockIndex >= 0) ? (unchecked((long)knownOuterSuperblockIndex) * 65536L) : LocateSuperblockInRange(s, num, size));
			array = null;
			if (num2 >= 0)
			{
				byte[] array2 = ReadStreamRange(s, num + num2, 65536);
				array = ProsperoImageDigests.ComputeSblockDigest(array2);
				array3 = array;
				pkg.Header.image_seed = array2.AsSpan(880, 16).ToArray();
			}
			else
			{
				array3 = HashStreamRange(s, num, size);
			}
			pkg.Header.pfs_image_digest = array3;
		}
		byte[] array4 = ProsperoFihBuilder.BuildFihHeaderBlock(ProsperoFihVariant.Debug, pkg.Header.pfs_image_size, 65536 + pkg.Header.pfs_image_size, num2, array, array3, null, nestedImageDigest, nestedImageSize, nestedMetaBaseBlocks, nwonlyContentVersionHi, nwonlyNapsFileCount, nwonlyAppFileCount, nwonlySparseAfidCount, nwonlyEmptyFileCount);
		pkg.Header.pfs_signed_digest = ProsperoImageDigests.ComputeFixedInfoDigest(array4);
		foreach (KeyValuePair<GeneralDigest, byte[]> item in ComputeGeneralDigests(pkg))
		{
			pkg.GeneralDigests.Set(item.Key, item.Value);
		}
		PkgWriter pkgWriter = new PkgWriter(s);
		pkgWriter.WriteBody(pkg, props.ContentId, props.Passcode);
		CalcBodyDigests(pkg, s);
		if (pkg.Header.desc_image_key_size != 0 && pkg.Header.desc_mandatory_size != 0)
		{
			pkg.Header.desc_digest = ComputeDescriptorDigest(s, in pkg.Header);
		}
		s.Position = 0L;
		pkgWriter.WriteHeader(in pkg.Header);
		s.Position = 0L;
		byte[] array5 = new byte[4064];
		s.ReadExactly(array5);
		BinaryPrimitives.WriteUInt64BigEndian(array5.AsSpan(1040, 8), 65536uL);
		pkg.HeaderDigest = ProsperoImageDigests.ComputePackageDigest(array5);
		s.Position = 4064L;
		s.Write(pkg.HeaderDigest, 0, pkg.HeaderDigest.Length);
		s.Position = 0L;
		byte[] array6 = new byte[4096];
		s.ReadExactly(array6);
		BinaryPrimitives.WriteUInt64BigEndian(array6.AsSpan(1040, 8), 65536uL);
		s.Position = 4096L;
		pkg.HeaderSignature = ProsperoPublisherRsa.BuildCntHeaderWrap(array6);
		s.Write(pkg.HeaderSignature, 0, pkg.HeaderSignature.Length);
		return BuildSiXmlOptions(pkg, pkg.Header.image_seed, Path.GetFullPath(props.SourceFolder), props.PrimaryId ?? props.ContentId);
	}

	private static byte[] ComputeDescriptorDigest(Stream stream, in Header header)
	{
		byte[] array = new byte[header.desc_image_key_size];
		stream.Position = header.desc_image_key_offset;
		stream.ReadExactly(array);
		byte[] array2 = new byte[header.desc_mandatory_size];
		stream.Position = header.desc_mandatory_offset;
		stream.ReadExactly(array2);
		byte[] array3 = new byte[64];
		ProsperoImageDigests.Sha3_256(array).CopyTo(array3, 0);
		ProsperoImageDigests.Sha3_256(array2).CopyTo(array3, 32);
		return array3;
	}

	private static ProsperoPfsImageXmlOptions BuildSiXmlOptions(Pkg pkg, byte[] imageSeed, string sourceFolder, string primaryId)
	{
		byte[] pfsImageSeed = ((imageSeed != null && imageSeed.Length == 16) ? imageSeed.ToArray() : new byte[16]);
		ParamJsonInfo paramJsonInfo = ReadParamJsonInfo(sourceFolder);
		long pfs_image_size = (long)pkg.Header.pfs_image_size;
		long pfs_image_offset = (long)pkg.Header.pfs_image_offset;
		long body_offset = (long)pkg.Header.body_offset;
		long mandatorySize = pkg.Metas.Metas.First((MetaEntry m) => m.id == EntryId.IMAGEDIGS_DAT).DataOffset;
		long packageSize = 65536 + pfs_image_size + pfs_image_offset;
		List<ProsperoPfsImageEntry> entries = (from m in pkg.Metas.Metas
			where m.id >= EntryId.LICENSE_DAT
			select new ProsperoPfsImageEntry(EntryDisplayName(pkg, m.id), m.DataOffset, m.DataSize)).OrderBy((ProsperoPfsImageEntry e) =>
		{
			ProsperoPfsImageEntry prosperoPfsImageEntry = e;
			return prosperoPfsImageEntry.Offset;
		}).ToList();
		return new ProsperoPfsImageXmlOptions
		{
			ContentId = pkg.Header.content_id,
			PrimaryId = primaryId,
			TitleName = paramJsonInfo.TitleName,
			ContentVersion = paramJsonInfo.ContentVersion,
			DrmType = "none",
			ApplicationDrmType = paramJsonInfo.ApplicationDrmType,
			ContentType = ContentTypeString(pkg.Header.content_type),
			ApplicationType = "free",
			MasterVersion = paramJsonInfo.MasterVersion,
			RequiredSystemSoftwareVersion = paramJsonInfo.RequiredSystemSoftwareVersion,
			SdkVersion = paramJsonInfo.SdkVersion,
			PackageSize = packageSize,
			PfsImageOffset = 65536L,
			PfsImageSize = pfs_image_size,
			PfsImageSeed = pfsImageSeed,
			ContainerSize = pfs_image_offset,
			MandatorySize = mandatorySize,
			BodyOffset = body_offset,
			SupplementalOffset = pfs_image_offset,
			Entries = entries,
			ContentDigest = Dig(GeneralDigest.ContentDigest),
			GameDigest = pkg.Header.pfs_image_digest,
			HeaderDigest = Dig(GeneralDigest.HeaderDigest),
			SystemDigest = Dig(GeneralDigest.SystemDigest),
			ParamDigest = Dig(GeneralDigest.ParamDigest),
			PackageDigest = pkg.HeaderDigest,
			BodyDigest = pkg.Header.body_digest,
			SblockDigest = pkg.Header.pfs_image_digest,
			FixedInfoDigest = pkg.Header.pfs_signed_digest
		};
		byte[]? Dig(GeneralDigest d)
		{
			if (!pkg.GeneralDigests.Digests.TryGetValue(d, out var value))
			{
				return null;
			}
			return value;
		}
	}

	private static long LocateSuperblockInRange(Stream stream, long offset, long size)
	{
		if (!stream.CanRead || !stream.CanSeek)
		{
			throw new ArgumentException("Image stream must be readable and seekable.", "stream");
		}
		Span<byte> span = stackalloc byte[12];
		for (long num = 0L; num <= size - 65536; num += 65536)
		{
			stream.Position = checked(offset + num);
			stream.ReadExactly(span);
			if (BinaryPrimitives.ReadUInt64LittleEndian(span) == 2 && span[8] == 11 && span[9] == 42 && span[10] == 51 && span[11] == 1)
			{
				return num;
			}
		}
		return -1L;
	}

	private static byte[] ReadStreamRange(Stream stream, long offset, int size)
	{
		if (offset < 0 || size < 0 || offset > stream.Length || size > stream.Length - offset)
		{
			throw new InvalidDataException("Requested image range is outside the stream.");
		}
		byte[] array = new byte[size];
		stream.Position = offset;
		stream.ReadExactly(array);
		return array;
	}

	private static byte[] HashStreamRange(Stream stream, long offset, long size)
	{
		if (offset < 0 || size < 0 || offset > stream.Length || size > stream.Length - offset)
		{
			throw new InvalidDataException("Requested image range is outside the stream.");
		}
		ProsperoSha3.Incremental incremental = new ProsperoSha3.Incremental();
		byte[] array = new byte[1048576];
		stream.Position = offset;
		while (size != 0L)
		{
			int count = (int)Math.Min(array.Length, size);
			int num = stream.Read(array, 0, count);
			if (num == 0)
			{
				throw new EndOfStreamException();
			}
			incremental.AppendData(array.AsSpan(0, num));
			size -= num;
		}
		return incremental.GetHashAndReset();
	}

	private static string ContentTypeString(uint contentType)
	{
		return contentType switch
		{
			33u => "PS5AC", 
			34u => "PS5AL", 
			_ => "PS5GD", 
		};
	}

	private static string EntryDisplayName(Pkg pkg, EntryId id)
	{
		if (id == EntryId.IMAGEDIGS_DAT)
		{
			return "imagedigs.dat";
		}
		string text = pkg.Entries.FirstOrDefault((Entry x) => x.Id == id)?.Name;
		if (text != null && text.Length > 0)
		{
			return text;
		}
		if (!EntryNames.IdToName.TryGetValue(id, out var value))
		{
			return $"0x{(uint)id:x4}.bin";
		}
		return value;
	}

	private static uint ContentVersionHigh(string contentVersion)
	{
		if (string.IsNullOrWhiteSpace(contentVersion))
		{
			return 0u;
		}
		string text = string.Concat(contentVersion.Where(char.IsDigit));
		if (text.Length != 8)
		{
			return 0u;
		}
		uint num = 0u;
		string text2 = text;
		foreach (char c in text2)
		{
			num = (num << 4) | (uint)(c - 48);
		}
		return num;
	}

	private static ParamJsonInfo ReadParamJsonInfo(string sourceFolder)
	{
		string text = "01.000.000";
		string text2 = "01.00";
		string text3 = "0x0000000000000000";
		string text4 = "0x0000000000000000";
		string text5 = "free";
		string titleName = "";
		try
		{
			byte[] array = ReadParamJson(sourceFolder);
			if (array != null && array.Length != 0)
			{
				using JsonDocument jsonDocument = JsonDocument.Parse(array);
				JsonElement rootElement = jsonDocument.RootElement;
				JsonElement value;
				if (rootElement.ValueKind == JsonValueKind.Object)
				{
					text = Str(rootElement, "contentVersion") ?? text;
					text2 = Str(rootElement, "masterVersion") ?? text2;
					text3 = Str(rootElement, "sdkVersion") ?? text3;
					text4 = Str(rootElement, "requiredSystemSoftwareVersion") ?? text4;
					text5 = Str(rootElement, "applicationDrmType") ?? text5;
					if (rootElement.TryGetProperty("localizedParameters", out value) && value.ValueKind == JsonValueKind.Object)
					{
						string propertyName = Str(value, "defaultLanguage") ?? "en-US";
						if (!value.TryGetProperty(propertyName, out var value2) || value2.ValueKind != JsonValueKind.Object)
						{
							goto IL_0137;
						}
						string text6 = Str(value2, "titleName");
						if (text6 == null || text6.Length <= 0)
						{
							goto IL_0137;
						}
						titleName = text6;
					}
				}
				goto end_IL_0055;
				IL_0137:
				foreach (JsonProperty item in value.EnumerateObject())
				{
					if (item.Value.ValueKind == JsonValueKind.Object)
					{
						string text7 = Str(item.Value, "titleName");
						if (text7 != null && text7.Length > 0)
						{
							titleName = text7;
							break;
						}
					}
				}
				end_IL_0055:;
			}
		}
		catch (Exception ex) when (((ex is JsonException || ex is IOException || ex is ArgumentException) ? 1 : 0) != 0)
		{
		}
		return new ParamJsonInfo(text, text2, text3, text4, text5, titleName);
		static string? Str(JsonElement o, string name)
		{
			if (!o.TryGetProperty(name, out var value3) || value3.ValueKind != JsonValueKind.String)
			{
				return null;
			}
			return value3.GetString();
		}
	}

	private static Dictionary<GeneralDigest, byte[]> ComputeGeneralDigests(Pkg pkg)
	{
		byte[] pfs_image_digest = pkg.Header.pfs_image_digest;
		bool flag = pkg.Header.content_type != 34;
		Dictionary<GeneralDigest, byte[]> dictionary = new Dictionary<GeneralDigest, byte[]>
		{
			{
				GeneralDigest.HeaderDigest,
				ComputeHeaderDigest(pkg)
			},
			{
				GeneralDigest.ContentDigest,
				ComputeContentDigest(pkg, pfs_image_digest, flag)
			}
		};
		if (flag)
		{
			dictionary[GeneralDigest.GameDigest] = pfs_image_digest;
			dictionary[GeneralDigest.TargetDigest] = pfs_image_digest;
		}
		byte[] array = ComputeConcatOverEntries(pkg, SystemMediaIds);
		if (array != null)
		{
			dictionary[GeneralDigest.SystemDigest] = array;
		}
		byte[] array2 = ComputeConcatOverEntries(pkg, PlaygoIds);
		if (array2 != null)
		{
			dictionary[GeneralDigest.PlaygoDigest] = array2;
		}
		if (pkg.Entries.FirstOrDefault((Entry e) => e.Id == (EntryId)8192u) is GenericEntry { FileData: { } fileData })
		{
			dictionary[GeneralDigest.ParamDigest] = ProsperoImageDigests.ComputeEntryDigest(fileData);
		}
		return dictionary;
	}

	private static byte[]? ComputeConcatOverEntries(Pkg pkg, uint[] ids)
	{
		HashSet<uint> set = new HashSet<uint>(ids);
		List<byte[]> list = (from e in pkg.Entries
			where set.Contains((uint)e.Id) && e is GenericEntry genericEntry && genericEntry.FileData != null
			orderby (uint)e.Id
			select ProsperoImageDigests.ComputeEntryDigest(((GenericEntry)e).FileData)).ToList();
		if (list.Count != 0)
		{
			return ProsperoImageDigests.ComputeConcatDigest(list);
		}
		return null;
	}

	private static byte[] ComputeHeaderDigest(Pkg pkg)
	{
		using MemoryStream memoryStream = new MemoryStream();
		new PkgWriter(memoryStream).WriteHeader(in pkg.Header);
		byte[] array = new byte[64];
		memoryStream.Position = 0L;
		memoryStream.ReadExactly(array);
		byte[] array2 = new byte[128];
		memoryStream.Position = 1024L;
		memoryStream.ReadExactly(array2);
		return ProsperoImageDigests.ComputeHeaderDigest(array, ProsperoImageDigests.ForceFihRelativeImageOffset(array2));
	}

	private static byte[] ComputeContentDigest(Pkg pkg, byte[] game, bool includeGame)
	{
		byte[] array = new byte[56];
		byte[] bytes = Encoding.ASCII.GetBytes(pkg.Header.content_id);
		Array.Copy(bytes, 0, array, 0, Math.Min(bytes.Length, 36));
		BinaryPrimitives.WriteUInt32BigEndian(array.AsSpan(48, 4), pkg.Header.drm_type);
		BinaryPrimitives.WriteUInt32BigEndian(array.AsSpan(52, 4), pkg.Header.content_type);
		return ProsperoImageDigests.ComputeContentDigest(array, includeGame ? game : null, new byte[32], includeGame);
	}

	private static void CalcBodyDigests(Pkg pkg, Stream s)
	{
		GenericEntry digests = pkg.Digests;
		uint dataOffset = pkg.Metas.Metas.First((MetaEntry m) => m.id == EntryId.DIGESTS).DataOffset;
		for (int num = 1; num < pkg.Metas.Metas.Count; num++)
		{
			MetaEntry metaEntry = pkg.Metas.Metas[num];
			long length = (uint)(metaEntry.Encrypted ? ((int)checked(metaEntry.DataSize + 15) & -16) : ((int)metaEntry.DataSize));
			byte[] array = Crypto.Sha3_256(s, metaEntry.DataOffset, length);
			Buffer.BlockCopy(array, 0, digests.FileData, 32 * num, 32);
			s.Position = dataOffset + 32 * num;
			s.Write(array, 0, 32);
		}
		pkg.Header.body_digest = Crypto.Sha3_256(s, (long)pkg.Header.body_offset, (long)pkg.Header.body_size);
		pkg.Header.digest_table_hash = Crypto.Sha3_256(pkg.Digests.FileData);
		using MemoryStream memoryStream = new MemoryStream();
		List<Entry> list = new List<Entry> { pkg.EntryKeys };
		if (pkg.ImageKey != null)
		{
			list.Add(pkg.ImageKey);
		}
		list.Add(pkg.GeneralDigests);
		list.Add(pkg.Metas);
		list.Add(pkg.Digests);
		foreach (Entry item in list)
		{
			new SubStream(s, item.meta.DataOffset, item.meta.DataSize).CopyTo(memoryStream);
		}
		pkg.Header.sc_entries1_hash = Crypto.Sha3_256(memoryStream);
		memoryStream.SetLength(0L);
		foreach (Entry item2 in list.Take(list.Count - 1))
		{
			long length2 = ((item2.Id == EntryId.METAS) ? ((long)(pkg.Header.sc_entry_count * 32)) : ((long)item2.meta.DataSize));
			new SubStream(s, item2.meta.DataOffset, length2).CopyTo(memoryStream);
		}
		pkg.Header.sc_entries2_hash = Crypto.Sha3_256(memoryStream);
	}

	private static byte[] ReadParamJson(string sourceFolder)
	{
		return File.ReadAllBytes(ResolveSourceFile(sourceFolder, "sce_sys/param.json") ?? throw new FileNotFoundException("sce_sys/param.json is required to build a PS5 package (either as a loose file or a GP5 mapping)."));
	}

	private static byte[] BuildPublisherParamJson(string sourceFolder, ProsperoPkgBuildProperties props)
	{
		byte[] array = ReadParamJson(sourceFolder);
		JsonNode jsonNode;
		try
		{
			jsonNode = JsonNode.Parse(array);
		}
		catch (JsonException innerException)
		{
			throw new InvalidDataException("sce_sys/param.json is not valid JSON.", innerException);
		}
		if (!(jsonNode is JsonObject jsonObject))
		{
			throw new InvalidDataException("sce_sys/param.json must contain a JSON object.");
		}
		if (props.VolumeType != ProsperoVolumeType.AdditionalContentNoData)
		{
			string text = jsonObject["versionFileUri"]?.GetValue<string>() ?? string.Empty;
			if (text.Length > 255)
			{
				throw new InvalidDataException($"param.json versionFileUri is {text.Length} characters; the publisher field is limited to 255.");
			}
			jsonObject["versionFileUri"] = text.PadRight(255, ' ');
		}
		DateTime dateTime = props.TimeStamp.Kind switch
		{
			DateTimeKind.Utc => props.TimeStamp, 
			DateTimeKind.Local => props.TimeStamp.ToUniversalTime(), 
			_ => DateTime.SpecifyKind(props.TimeStamp, DateTimeKind.Utc), 
		};
		JsonNode jsonNode2 = (jsonObject["pubtools"] as JsonObject)?["loudnessSnd0"]?.DeepClone();
		JsonObject jsonObject2 = new JsonObject
		{
			["creationDate"] = dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
			["toolVersion"] = "2.79"
		};
		if (jsonNode2 != null)
		{
			jsonObject2["loudnessSnd0"] = jsonNode2;
		}
		jsonObject["pubtools"] = jsonObject2;
		if (props.VolumeType == ProsperoVolumeType.AdditionalContentNoData)
		{
			jsonObject.Remove("applicationCategoryType");
			jsonObject.Remove("contentVersion");
			jsonObject.Remove("versionFileUri");
			JsonObject jsonObject3 = jsonObject;
			if (jsonObject3["conceptId"] == null)
			{
				JsonNode jsonNode3 = (jsonObject3["conceptId"] = "10000000");
			}
			jsonObject3 = jsonObject;
			if (jsonObject3["requiredSystemSoftwareVersion"] == null)
			{
				JsonNode jsonNode3 = (jsonObject3["requiredSystemSoftwareVersion"] = "0x0500000000000000");
			}
			((JsonObject)jsonObject["pubtools"])["submission"] = true;
		}
		else if (props.VolumeType == ProsperoVolumeType.Application)
		{
			ulong sdkVersion = 0uL;
			bool flag = false;
			bool flag2 = false;
			string text2 = ResolveSourceFile(sourceFolder, "eboot.bin");
			if (text2 != null)
			{
				byte[] array2 = File.ReadAllBytes(text2);
				flag2 = ProsperoFself.IsElf(array2);
				flag = ProsperoFself.TryGetSdkVersion(array2, out sdkVersion);
			}
			if (!flag & flag2)
			{
				sdkVersion = 311029849265274880uL;
			}
			ulong num = sdkVersion;
			jsonObject["sdkVersion"] = VersionText(num);
			ulong value = Math.Max(Math.Min(HexVersion(jsonObject["requiredSystemSoftwareVersion"]), 648518346341351424uL), num);
			jsonObject["requiredSystemSoftwareVersion"] = VersionText(value);
			JsonObject jsonObject4 = jsonObject;
			if (jsonObject4["applicationDrmType"] == null)
			{
				JsonNode jsonNode3 = (jsonObject4["applicationDrmType"] = "standard");
			}
			jsonObject4 = jsonObject;
			if (jsonObject4["attribute"] == null)
			{
				JsonNode jsonNode3 = (jsonObject4["attribute"] = 0);
			}
			jsonObject4 = jsonObject;
			if (jsonObject4["attribute2"] == null)
			{
				JsonNode jsonNode3 = (jsonObject4["attribute2"] = 0);
			}
			jsonObject4 = jsonObject;
			if (jsonObject4["attribute3"] == null)
			{
				JsonNode jsonNode3 = (jsonObject4["attribute3"] = 0);
			}
			jsonObject4 = jsonObject;
			if (jsonObject4["conceptId"] == null)
			{
				JsonNode jsonNode3 = (jsonObject4["conceptId"] = "10000000");
			}
			jsonObject4 = jsonObject;
			if (jsonObject4["masterVersion"] == null)
			{
				JsonNode jsonNode3 = (jsonObject4["masterVersion"] = "01.00");
			}
			jsonObject4 = jsonObject;
			if (jsonObject4["contentVersion"] == null)
			{
				JsonNode jsonNode3 = (jsonObject4["contentVersion"] = "01.000.000");
			}
			jsonObject4 = jsonObject;
			if (jsonObject4["ageLevel"] == null)
			{
				JsonNode jsonNode3 = (jsonObject4["ageLevel"] = new JsonObject
				{
					["JP"] = 0,
					["US"] = 0,
					["default"] = 0
				});
			}
			jsonObject4 = jsonObject;
			if (jsonObject4["contentBadgeType"] == null)
			{
				JsonNode jsonNode3 = (jsonObject4["contentBadgeType"] = 1);
			}
			jsonObject.Remove("originContentVersion");
			jsonObject.Remove("targetContentVersion");
			jsonObject["addcont"] = new JsonObject { ["serviceIdForSharing"] = new JsonArray(Enumerable.Range(0, 7).Select((Func<int, JsonNode>)((int _) => JsonValue.Create(new string(' ', 19)))).ToArray()) };
		}
		string text3 = SortJsonNode(jsonObject).ToJsonString(new JsonSerializerOptions
		{
			WriteIndented = true,
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
		});
		text3 = text3.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n";
		return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text3);
		static ulong HexVersion(JsonNode? jsonNode15)
		{
			string text4 = jsonNode15?.GetValue<string>();
			if (text4 == null || !text4.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || !ulong.TryParse(text4.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var result))
			{
				return 0uL;
			}
			return result;
		}
		static string VersionText(ulong value2)
		{
			return $"0x{value2:X16}".ToLowerInvariant();
		}
	}

	private static JsonNode? SortJsonNode(JsonNode? node)
	{
		if (node is JsonObject source)
		{
			JsonObject jsonObject = new JsonObject();
			{
				foreach (KeyValuePair<string, JsonNode> item in source.OrderBy((KeyValuePair<string, JsonNode> p) => p.Key, StringComparer.Ordinal))
				{
					jsonObject[item.Key] = SortJsonNode(item.Value);
				}
				return jsonObject;
			}
		}
		if (node is JsonArray jsonArray)
		{
			JsonArray jsonArray2 = new JsonArray();
			{
				foreach (JsonNode item2 in jsonArray)
				{
					jsonArray2.Add(SortJsonNode(item2));
				}
				return jsonArray2;
			}
		}
		return node?.DeepClone();
	}

	internal static string? ResolveSourceFile(string sourceFolder, string packagePath)
	{
		string text = packagePath.Replace('\\', '/').Trim('/');
		string text2 = Directory.EnumerateFiles(sourceFolder, "*.gp5", SearchOption.TopDirectoryOnly).OrderBy((string path3) => Path.GetFileName(path3), StringComparer.OrdinalIgnoreCase).FirstOrDefault();
		if (text2 == null)
		{
			string path = Path.Combine(sourceFolder, text.Replace('/', Path.DirectorySeparatorChar));
			if (!File.Exists(path))
			{
				return null;
			}
			return Path.GetFullPath(path);
		}
		Gp5Project gp5Project = Gp5Project.ReadFrom(text2);
		string directoryName = Path.GetDirectoryName(text2);
		if (gp5Project.Layout == Gp5Layout.Normal)
		{
			string path2 = Path.Combine(ResolveGp5SourcePath(directoryName, gp5Project.RootDir.SourcePath, directoryName), text.Replace('/', Path.DirectorySeparatorChar));
			if (!File.Exists(path2))
			{
				return null;
			}
			return Path.GetFullPath(path2);
		}
		string text3 = null;
		foreach (Gp5File file in gp5Project.Files)
		{
			string destinationPath = file.DestinationPath;
			if (!string.IsNullOrWhiteSpace(destinationPath) && string.Equals(destinationPath.Replace('\\', '/').Trim('/'), text, StringComparison.OrdinalIgnoreCase))
			{
				string text4 = ResolveGp5SourcePath(directoryName, file.SourcePath, Path.Combine(directoryName, destinationPath.Replace('/', Path.DirectorySeparatorChar)));
				if (!File.Exists(text4))
				{
					throw new FileNotFoundException("GP5 source file was not found for '" + destinationPath + "'.", text4);
				}
				if (text3 != null)
				{
					throw new InvalidDataException("GP5 contains duplicate destination '" + text + "'.");
				}
				text3 = text4;
			}
		}
		return text3;
	}

	private static IReadOnlyDictionary<string, string> ResolveSceSysFiles(string sourceFolder)
	{
		SortedDictionary<string, string> sortedDictionary = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		string text = Directory.EnumerateFiles(sourceFolder, "*.gp5", SearchOption.TopDirectoryOnly).OrderBy((string path3) => Path.GetFileName(path3), StringComparer.OrdinalIgnoreCase).FirstOrDefault();
		if (text == null)
		{
			string text2 = Path.Combine(sourceFolder, "sce_sys");
			if (!Directory.Exists(text2))
			{
				return sortedDictionary;
			}
			{
				foreach (string item in Directory.EnumerateFiles(text2, "*", SearchOption.AllDirectories))
				{
					string key = Path.GetRelativePath(text2, item).Replace('\\', '/');
					sortedDictionary.Add(key, Path.GetFullPath(item));
				}
				return sortedDictionary;
			}
		}
		Gp5Project gp5Project = Gp5Project.ReadFrom(text);
		string directoryName = Path.GetDirectoryName(text);
		if (gp5Project.Layout == Gp5Layout.Normal)
		{
			string text3 = ResolveGp5SourcePath(directoryName, gp5Project.RootDir.SourcePath, directoryName);
			string text4 = Path.Combine(text3, "sce_sys");
			string[] dirMasks = ParseGp5ExcludeMasks(gp5Project.RootDir.DirExclude);
			string[] masks = ParseGp5ExcludeMasks(gp5Project.RootDir.FileExclude);
			string[] array = ParseGp5ExcludeMasks(gp5Project.GlobalExclude);
			if (Directory.Exists(text4))
			{
				foreach (string item2 in Directory.EnumerateFiles(text4, "*", SearchOption.AllDirectories))
				{
					string text5 = Path.GetRelativePath(text3, item2).Replace('\\', '/');
					string fileName = Path.GetFileName(item2);
					if (!MatchesGp5Exclude(fileName, text5, masks) && !MatchesGp5Exclude(fileName, text5, array) && !IsInGp5ExcludedDirectory(text5, dirMasks, array))
					{
						string key2 = Path.GetRelativePath(text4, item2).Replace('\\', '/');
						sortedDictionary[key2] = Path.GetFullPath(item2);
					}
				}
			}
		}
		else
		{
			foreach (Gp5File file in gp5Project.Files)
			{
				string destinationPath = file.DestinationPath;
				if (string.IsNullOrWhiteSpace(destinationPath))
				{
					continue;
				}
				string text6 = destinationPath.Replace('\\', '/').Trim('/');
				if (text6.StartsWith("sce_sys/", StringComparison.OrdinalIgnoreCase))
				{
					string key3 = text6.Substring("sce_sys/".Length);
					string text7 = ResolveGp5SourcePath(directoryName, file.SourcePath, Path.Combine(directoryName, text6.Replace('/', Path.DirectorySeparatorChar)));
					if (!File.Exists(text7))
					{
						throw new FileNotFoundException("GP5 source file was not found for '" + destinationPath + "'.", text7);
					}
					if (!sortedDictionary.TryAdd(key3, text7))
					{
						throw new InvalidDataException("GP5 contains duplicate destination '" + text6 + "'.");
					}
				}
			}
		}
		string path = Path.Combine(sourceFolder, "sce_sys");
		string[] array2 = new string[2] { "license.dat", "license.info" };
		foreach (string text8 in array2)
		{
			string path2 = Path.Combine(path, text8);
			if (!sortedDictionary.ContainsKey(text8) && File.Exists(path2))
			{
				sortedDictionary.Add(text8, Path.GetFullPath(path2));
			}
		}
		return sortedDictionary;
	}

	private static string[] ParseGp5ExcludeMasks(string? value)
	{
		if (!string.IsNullOrWhiteSpace(value))
		{
			return value.Split(new char[2] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		}
		return Array.Empty<string>();
	}

	private static bool MatchesGp5Exclude(string name, string relativePath, IReadOnlyList<string> masks)
	{
		string normalized = relativePath.Replace('\\', '/');
		return masks.Any((string mask) => FileSystemName.MatchesSimpleExpression(mask, name) || FileSystemName.MatchesSimpleExpression(mask.Replace('\\', '/'), normalized));
	}

	private static bool IsInGp5ExcludedDirectory(string fileRelativePath, IReadOnlyList<string> dirMasks, IReadOnlyList<string> globalMasks)
	{
		string[] array = fileRelativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
		string text = "";
		for (int i = 0; i < array.Length - 1; i++)
		{
			text = ((text.Length == 0) ? array[i] : (text + "/" + array[i]));
			if (MatchesGp5Exclude(array[i], text, dirMasks) || MatchesGp5Exclude(array[i], text, globalMasks))
			{
				return true;
			}
		}
		return false;
	}

	private static byte[]? ResolveGp5EntitlementKey(string sourceFolder)
	{
		string text = Directory.EnumerateFiles(sourceFolder, "*.gp5", SearchOption.TopDirectoryOnly).OrderBy((string path) => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase).FirstOrDefault();
		if (text == null)
		{
			return null;
		}
		string entitlementKey = Gp5Project.ReadFrom(text).Volume.Package.EntitlementKey;
		if (string.IsNullOrWhiteSpace(entitlementKey))
		{
			return null;
		}
		byte[] array;
		try
		{
			array = Convert.FromHexString(entitlementKey);
		}
		catch (FormatException innerException)
		{
			throw new InvalidDataException("GP5 package entitlement_key must contain hexadecimal bytes.", innerException);
		}
		if (array.Length != 16)
		{
			throw new InvalidDataException($"GP5 package entitlement_key must be 16 bytes (got {array.Length}).");
		}
		return array;
	}

	private static string ResolveGp5SourcePath(string projectDirectory, string? source, string fallback)
	{
		string name = (string.IsNullOrWhiteSpace(source) ? fallback : source);
		name = Environment.ExpandEnvironmentVariables(name).Replace('\\', Path.DirectorySeparatorChar);
		return Path.GetFullPath(Path.IsPathRooted(name) ? name : Path.Combine(projectDirectory, name));
	}

	private static byte[] DeriveDeterministicOuterSeed(string contentId, string passcode)
	{
		return ProsperoImageDigests.Sha3_256(Encoding.ASCII.GetBytes("LibProsperoPkg deterministic outer seed\0" + contentId + "\0" + passcode)).AsSpan(0, 16).ToArray();
	}

	private static IEnumerable<Entry> CollectMediaEntries(string sourceFolder, ProsperoVolumeType volumeType, string contentId, IProsperoLicenseProvider? licenseProvider, bool generateDds = true)
	{
		IReadOnlyDictionary<string, string> sceSysFiles = ResolveSceSysFiles(sourceFolder);
		byte[] entitlementKey = (IsAdditionalContent(volumeType) ? ResolveGp5EntitlementKey(sourceFolder) : null);
		ProsperoLicenseArtifacts providedLicense = null;
		if (licenseProvider != null)
		{
			ProsperoLicenseRequest request = new ProsperoLicenseRequest
			{
				VolumeType = volumeType,
				ContentId = contentId,
				EntitlementKey = entitlementKey
			};
			providedLicense = licenseProvider.GetLicense(request) ?? throw new InvalidDataException("The license provider returned no artifacts.");
			providedLicense.Validate(request);
		}
		if (IsAdditionalContent(volumeType) && providedLicense == null)
		{
			string[] array = new string[2] { "license.dat", "license.info" };
			foreach (string text in array)
			{
				if (!sceSysFiles.ContainsKey(text))
				{
					throw new FileNotFoundException($"{volumeType} requires an existing backend-issued sce_sys/{text}. " + "Place the decrypted sidecar beside the GP5/source tree; LibProsperoPkg can validate and re-encrypt it but cannot issue a new backend license.");
				}
			}
		}
		HashSet<uint> emitted = new HashSet<uint>();
		if (providedLicense != null)
		{
			emitted.Add(1024u);
			yield return new GenericEntry(EntryId.LICENSE_DAT)
			{
				FileData = providedLicense.LicenseDat.ToArray()
			};
			emitted.Add(1025u);
			yield return new GenericEntry(EntryId.LICENSE_INFO)
			{
				FileData = providedLicense.LicenseInfo.ToArray()
			};
		}
		(string Name, uint Id)[] mediaFiles = MediaFiles;
		for (int j = 0; j < mediaFiles.Length; j++)
		{
			var (text2, num) = mediaFiles[j];
			if (sceSysFiles.TryGetValue(text2, out var value))
			{
				emitted.Add(num);
				byte[] fileData = File.ReadAllBytes(value);
				yield return new GenericEntry((EntryId)num, text2)
				{
					FileData = fileData
				};
			}
		}
		(string Png, string Dds, uint Id)[] array2 = (generateDds ? DdsMedia : Array.Empty<(string, string, uint)>());
		for (int j = 0; j < array2.Length; j++)
		{
			var (key, text3, num2) = array2[j];
			byte[] fileData2;
			if (sceSysFiles.TryGetValue(text3, out var value2))
			{
				fileData2 = File.ReadAllBytes(value2);
			}
			else
			{
				if (!sceSysFiles.TryGetValue(key, out var value3))
				{
					continue;
				}
				try
				{
					fileData2 = ProsperoDdsEncoder.EncodePngToDds(File.ReadAllBytes(value3));
				}
				catch
				{
					continue;
				}
			}
			emitted.Add(num2);
			yield return new GenericEntry((EntryId)num2, text3)
			{
				FileData = fileData2
			};
		}
		foreach (var (text6, path) in sceSysFiles)
		{
			if (!EntryNames.NameToId.TryGetValue(text6, out var value4))
			{
				continue;
			}
			uint num3 = (uint)value4;
			if (!text6.EndsWith(".dds", StringComparison.Ordinal) && !GeneratedEntryIds.Contains(num3) && emitted.Add(num3))
			{
				byte[] array3 = File.ReadAllBytes(path);
				if (!((text6 == "license.dat") ? ProsperoSystemFiles.ValidateLicenseDat(array3, contentId, out string error) : ((!(text6 == "license.info")) ? ProsperoSystemFiles.Validate(text6, array3, out error) : ProsperoSystemFiles.ValidateLicenseInfo(array3, contentId, entitlementKey, out error))))
				{
					throw new InvalidDataException("sce_sys/" + text6 + ": " + error);
				}
				string name = (ProsperoCntEntryPolicy.Resolve(num3, volumeType, text6).IncludeName ? text6 : null);
				yield return new GenericEntry(value4, name)
				{
					FileData = array3
				};
			}
		}
	}

	private static ulong Align(ulong value, ulong align)
	{
		ulong num = value % align;
		if (num != 0L)
		{
			return value + (align - num);
		}
		return value;
	}

	private static long ToUnixSeconds(DateTime time)
	{
		return (long)time.ToUniversalTime().Subtract(DateTime.UnixEpoch).TotalSeconds;
	}
}
