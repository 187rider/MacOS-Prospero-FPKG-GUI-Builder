using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LibProsperoPkg.GP5;
using LibProsperoPkg.Keys;
using LibProsperoPkg.PFS;
using LibProsperoPkg.PFS.Compression;
using LibProsperoPkg.PKG;

namespace LibProsperoPkg;

/// <summary>
/// Folder -&gt; PS5 package builder. See the file header for the architecture.
/// </summary>
public static class ProsperoPackageBuilder
{
	private static readonly Regex ContentIdRegex = new Regex("^[A-Z]{2}[0-9]{4}-[A-Z]{4}[0-9]{5}_00-[A-Z0-9]{16}$", RegexOptions.Compiled);

	private static readonly Regex TitleIdRegex = new Regex("^[A-Z]{4}[0-9]{5}$", RegexOptions.Compiled);

	/// <summary>True when the wired-in PS5 publishing key material is available.</summary>
	public static bool KeysAvailable => ProsperoKeys.IsPublisherRsaProfileAvailable;

	/// <summary>
	/// Encrypts a prepared (plaintext) inner PFS image with AES-XTS, deriving the
	/// EKPFS from the package content id + passcode, then the (tweak, data) keys from the EKPFS
	/// plus the image header seed. Offered as a standalone, round-trip-checked primitive.
	/// </summary>
	/// <param name="pfsImagePath">A prepared plaintext PFS image (in place).</param>
	/// <param name="contentId">The 36-character content id.</param>
	/// <param name="passcode">The 32-character passcode.</param>
	/// <param name="seed">Optional 16-byte header seed; <c>null</c> uses the image's own seed or generates one.</param>
	/// <param name="logger">Optional progress sink.</param>
	public static ProsperoPfsImageResult EncryptPfsImage(string pfsImagePath, string contentId, string passcode, byte[]? seed = null, Action<string>? logger = null)
	{
		byte[] ekpfs = ProsperoPkgSigner.ComputeEkpfs(contentId, passcode);
		ProsperoPfsImageOptions options = new ProsperoPfsImageOptions
		{
			Ekpfs = ekpfs,
			Seed = seed
		};
		return ProsperoPfsImage.EncryptInPlace(pfsImagePath, options, logger);
	}

	/// <summary>
	/// Lays out a prepared folder into a plaintext PS5 inner-PFS image. The
	/// produced image is unsigned/unencrypted; pair it with <see cref="M:LibProsperoPkg.ProsperoPackageBuilder.EncryptPfsImage(System.String,System.String,System.String,System.Byte[],System.Action{System.String})" />
	/// for the encrypted form, or with <see cref="M:LibProsperoPkg.ProsperoPackageBuilder.BuildInnerImage(System.String,System.String,System.String,System.String,LibProsperoPkg.InnerImageForm,System.Action{System.String})" /> for the full pipeline.
	/// </summary>
	/// <param name="sourceFolder">A prepared application folder (its tree becomes the image's uroot).</param>
	/// <param name="outputPath">Destination plaintext inner-PFS image path.</param>
	/// <param name="logger">Optional progress sink.</param>
	public static ProsperoPfsLayoutResult BuildInnerPfsLayout(string sourceFolder, string outputPath, Action<string>? logger = null)
	{
		ProsperoPfsLayoutOptions options = new ProsperoPfsLayoutOptions();
		return ProsperoPfsLayout.BuildFromFolder(sourceFolder, outputPath, options, logger);
	}

	/// <summary>
	/// Runs the full inner-PFS pipeline end to end: lays out the folder into a plaintext
	/// inner-PFS image (<see cref="M:LibProsperoPkg.ProsperoPackageBuilder.BuildInnerPfsLayout(System.String,System.String,System.Action{System.String})" />), then renders it in the requested
	/// <paramref name="form" /> — left plaintext, AES-XTS-encrypted with the EKPFS derived from the
	/// content id + passcode (<see cref="M:LibProsperoPkg.ProsperoPackageBuilder.EncryptPfsImage(System.String,System.String,System.String,System.Byte[],System.Action{System.String})" />), or PFSC-compressed
	/// (<see cref="T:LibProsperoPkg.PFS.ProsperoPfsc" />). The forms are mutually exclusive: an encrypted
	/// image carries the plaintext PFS superblock the kernel needs, while a compressed image is a
	/// PFSC container — composing both is handled by the outer-PFS layer.
	/// </summary>
	/// <param name="sourceFolder">A prepared application folder.</param>
	/// <param name="outputPath">Destination inner-PFS image path.</param>
	/// <param name="contentId">The 36-character content id (used to derive the EKPFS when encrypting).</param>
	/// <param name="passcode">The 32-character passcode (used to derive the EKPFS when encrypting).</param>
	/// <param name="form">The inner-image representation to produce. Default <see cref="F:LibProsperoPkg.InnerImageForm.Encrypted" />.</param>
	/// <param name="logger">Optional progress sink.</param>
	/// <returns>The final inner-PFS image path.</returns>
	public static string BuildInnerImage(string sourceFolder, string outputPath, string contentId, string passcode, InnerImageForm form = InnerImageForm.Encrypted, Action<string>? logger = null)
	{
		Action<string> action = logger ?? ((Action<string>)((string _) =>
		{
		}));
		BuildInnerPfsLayout(sourceFolder, outputPath, action);
		switch (form)
		{
		case InnerImageForm.Encrypted:
			action("AES-XTS-encrypting the laid-out inner PFS image...");
			EncryptPfsImage(outputPath, contentId, passcode, null, action);
			break;
		case InnerImageForm.Compressed:
		{
			action("Compressing the inner PFS image (PFSC)...");
			string text2 = outputPath + ".pfsc.tmp";
			ProsperoPfscOptions options = new ProsperoPfscOptions
			{
				BlockSize = 65536
			};
			ProsperoPfsc.PackFile(outputPath, text2, options, action);
			File.Delete(outputPath);
			File.Move(text2, outputPath);
			break;
		}
		case InnerImageForm.KrakenCompressed:
		{
			action("Compressing the inner PFS image (PFSv3)...");
			string text = outputPath + ".pfsc.tmp";
			ProsperoCompressedPfsImage.PackFile(outputPath, text, 7, 262144, action);
			File.Delete(outputPath);
			File.Move(text, outputPath);
			break;
		}
		default:
			throw new ArgumentOutOfRangeException("form", form, "Unknown inner-image form.");
		case InnerImageForm.Plaintext:
			break;
		}
		return outputPath;
	}

	/// <summary>Returns true when <paramref name="contentId" /> is a well-formed 36-char content id.</summary>
	public static bool IsValidContentId(string? contentId)
	{
		if (!string.IsNullOrEmpty(contentId))
		{
			return ContentIdRegex.IsMatch(contentId);
		}
		return false;
	}

	/// <summary>Returns true when <paramref name="titleId" /> looks like <c>PPSAxxxxx</c>.</summary>
	public static bool IsValidTitleId(string? titleId)
	{
		if (!string.IsNullOrEmpty(titleId))
		{
			return TitleIdRegex.IsMatch(titleId);
		}
		return false;
	}

	/// <summary>
	/// Builds a content id from a publisher prefix, a title id and a 16-char label.
	/// Missing pieces are padded so the result is always 36 characters.
	/// </summary>
	public static string ComposeContentId(string? publisher, string? titleId, string? label)
	{
		publisher = (publisher ?? "UP9000").ToUpperInvariant();
		if (publisher.Length < 6)
		{
			publisher = publisher.PadRight(6, '0');
		}
		publisher = publisher.Substring(0, 6);
		titleId = (titleId ?? "PPSA00000").ToUpperInvariant();
		if (titleId.Length < 9)
		{
			titleId = titleId.PadRight(9, '0');
		}
		titleId = titleId.Substring(0, 9);
		label = (label ?? "").ToUpperInvariant();
		label = new string(label.Where((char c) => (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')).ToArray());
		if (label.Length < 16)
		{
			label = label.PadRight(16, '0');
		}
		label = label.Substring(0, 16);
		return $"{publisher}-{titleId}_00-{label}";
	}

	/// <summary>The Prospero volume type used for a given mode.</summary>
	public static Gp5VolumeType VolumeTypeForMode(ProsperoPackageMode mode)
	{
		return mode switch
		{
			ProsperoPackageMode.AdditionalContentData => Gp5VolumeType.prospero_ac, 
			ProsperoPackageMode.AdditionalContentNoData => Gp5VolumeType.prospero_al, 
			_ => Gp5VolumeType.prospero_app, 
		};
	}

	/// <summary>The PS5 PKG builder volume kind used for a given mode.</summary>
	public static ProsperoVolumeType ProsperoVolumeTypeForMode(ProsperoPackageMode mode)
	{
		return mode switch
		{
			ProsperoPackageMode.AdditionalContentData => ProsperoVolumeType.AdditionalContentData, 
			ProsperoPackageMode.AdditionalContentNoData => ProsperoVolumeType.AdditionalContentNoData, 
			_ => ProsperoVolumeType.Application, 
		};
	}

	/// <summary>True when the mode produces additional-content (DLC) packages.</summary>
	public static bool IsDlcMode(ProsperoPackageMode mode)
	{
		if ((uint)(mode - 2) <= 1u)
		{
			return true;
		}
		return false;
	}

	/// <summary>The PS5 application category type written into a generated param.json for a mode.</summary>
	private static int CategoryTypeForMode(ProsperoPackageMode mode)
	{
		return 0;
	}

	/// <summary>
	/// Builds the PS5 package described by <paramref name="options" />.
	/// </summary>
	/// <param name="options">The build description.</param>
	/// <param name="logger">Optional sink for progress messages.</param>
	/// <returns>The finished package path and any non-fatal warnings.</returns>
	/// <exception cref="T:System.ArgumentException">A required option is missing or malformed.</exception>
	/// <exception cref="T:System.InvalidOperationException">The build failed.</exception>
	public static ProsperoBuildResult Build(ProsperoBuildOptions options, Action<string>? logger = null)
	{
		ArgumentNullException.ThrowIfNull(options, "options");
		Action<string> sink = logger ?? ((Action<string>)((string _) =>
		{
		}));
		Stopwatch buildTimer = Stopwatch.StartNew();
		Action<string> action = (string message) =>
		{
			sink($"[+{buildTimer.Elapsed:hh\\:mm\\:ss\\.fff}] {message}");
		};
		List<string> list = new List<string>();
		if (string.IsNullOrWhiteSpace(options.SourceFolder) || !Directory.Exists(options.SourceFolder))
		{
			throw new ArgumentException("Source folder does not exist.", "options");
		}
		if (string.IsNullOrWhiteSpace(options.OutputFolder))
		{
			throw new ArgumentException("Output folder was not specified.", "options");
		}
		if (string.IsNullOrEmpty(options.Passcode) || options.Passcode.Length != 32)
		{
			throw new ArgumentException("Passcode must be exactly 32 characters.", "options");
		}
		if (!IsValidContentId(options.ContentId))
		{
			throw new ArgumentException("Content ID is not in the format XXYYYY-XXXXYYYYY_00-ZZZZZZZZZZZZZZZZ.", "options");
		}
		if (options.PrimaryId != null && !IsValidContentId(options.PrimaryId))
		{
			throw new ArgumentException("Primary ID is not in the format XXYYYY-XXXXYYYYY_00-ZZZZZZZZZZZZZZZZ.", "options");
		}
		int legacyZlibCompressionLevel = options.LegacyZlibCompressionLevel;
		if ((legacyZlibCompressionLevel < 0 || legacyZlibCompressionLevel > 9) ? true : false)
		{
			throw new ArgumentOutOfRangeException("options", "Legacy zlib compression level must be in the range 0..9.");
		}
		if (options.LegacyZlibMaxDegreeOfParallelism < 0)
		{
			throw new ArgumentOutOfRangeException("options", "Legacy zlib parallelism must be zero (automatic) or positive.");
		}
		legacyZlibCompressionLevel = options.PlayGoChunkCount;
		if ((legacyZlibCompressionLevel < 1 || legacyZlibCompressionLevel > 64) ? true : false)
		{
			throw new ArgumentOutOfRangeException("options", "PlayGo chunk count must be in the range 1..64.");
		}
		if (options.PlayGoChunkCount != 1 && !options.UsePublisherPprNaps)
		{
			throw new ArgumentException("Automatic multi-chunk PlayGo currently requires the publisher PPR-PFS/NAPS path.", "options");
		}
		ProsperoPublisherImageMode publisherImageMode = options.PublisherImageMode;
		if (publisherImageMode != ProsperoPublisherImageMode.Native && publisherImageMode != ProsperoPublisherImageMode.PlaintextNoAuth)
		{
			throw new ArgumentOutOfRangeException("options", options.PublisherImageMode, "Publisher image mode must be Native or PlaintextNoAuth.");
		}
		byte[] outerPfsSeed = options.OuterPfsSeed;
		if (outerPfsSeed != null && outerPfsSeed.Length != 16)
		{
			throw new ArgumentException("Outer PFS seed must contain exactly 16 bytes.", "options");
		}
		outerPfsSeed = options.NapsPfsImageKey;
		if (outerPfsSeed != null && outerPfsSeed.Length != 32)
		{
			throw new ArgumentException("NAPS pfs-image-key must contain exactly 32 bytes.", "options");
		}
		outerPfsSeed = options.NapsPfsImageSeed;
		if (outerPfsSeed != null && outerPfsSeed.Length != 16)
		{
			throw new ArgumentException("NAPS pfs-image-seed must contain exactly 16 bytes.", "options");
		}
		outerPfsSeed = options.PublisherImageKey;
		if (outerPfsSeed != null && outerPfsSeed.Length != 2048)
		{
			throw new ArgumentException("Publisher IMAGE_KEY must contain exactly 0x800 bytes.", "options");
		}
		outerPfsSeed = options.PublisherEntryKeys;
		if (outerPfsSeed != null && outerPfsSeed.Length != 2944)
		{
			throw new ArgumentException("Publisher ENTRY_KEYS must contain exactly 0xB80 bytes.", "options");
		}
		if (options.OuterPfsSeed != null && options.NapsPfsImageSeed != null && !options.OuterPfsSeed.AsSpan().SequenceEqual(options.NapsPfsImageSeed))
		{
			throw new ArgumentException("OuterPfsSeed and NapsPfsImageSeed identify the same publisher superblock seed and must match.", "options");
		}
		if (options.NapsPfsImageKey != null && options.NapsPfsImageSeed == null && options.OuterPfsSeed == null)
		{
			throw new ArgumentException("An expected NAPS pfs-image-key requires NapsPfsImageSeed or OuterPfsSeed.", "options");
		}
		if (options.PublisherImageMode == ProsperoPublisherImageMode.PlaintextNoAuth && !options.UsePublisherPprNaps)
		{
			throw new ArgumentException("PLAINTEXT_NOAUTH requires the publisher PPR-PFS/NAPS image path.", "options");
		}
		if (options.PublisherImageMode == ProsperoPublisherImageMode.PlaintextNoAuth && options.Mode == ProsperoPackageMode.AdditionalContentNoData)
		{
			throw new ArgumentException("PLAINTEXT_NOAUTH is not applicable to entitlement-only packages without PFS data.", "options");
		}
		if (options.PublisherImageMode == ProsperoPublisherImageMode.PlaintextNoAuth && options.NapsOuterBlockCmacKey != null)
		{
			throw new ArgumentException("PLAINTEXT_NOAUTH cannot contain keyed NAPS outer-block authentication tags.", "options");
		}
		if (options.PublisherImageMode == ProsperoPublisherImageMode.PlaintextNoAuth && options.OutputFormat == ProsperoOutputFormat.RetailImage)
		{
			throw new ArgumentException("PLAINTEXT_NOAUTH is a debug/research image mode and cannot be finalized as RetailImage.", "options");
		}
		if (options.PublisherImageMode == ProsperoPublisherImageMode.PlaintextNoAuth && options.RequirePublisherCompatibility)
		{
			throw new ArgumentException("PLAINTEXT_NOAUTH is intentionally incompatible with strict publisher mode.", "options");
		}
		if (options.OutputFormat == ProsperoOutputFormat.RetailImage && options.RetailFinalizationProvider == null)
		{
			throw new ArgumentException("RetailImage requires a trusted RetailFinalizationProvider; a signed byte of 0x80 alone is not a finalized Retail package.", "options");
		}
		if (options.OutputFormat == ProsperoOutputFormat.RetailImage && options.Mode == ProsperoPackageMode.AdditionalContentNoData)
		{
			throw new ArgumentException("RetailImage is not available for the direct PSAL AdditionalContentNoData layout.", "options");
		}
		Directory.CreateDirectory(options.OutputFolder);
		string fullPath = Path.GetFullPath(options.SourceFolder);
		using var quarantine = SceSysQuarantine.Apply(fullPath, action);
		action($"Build configuration: mode={options.Mode}, output={options.OutputFormat}, image={options.PublisherImageMode}, PlayGo chunks={options.PlayGoChunkCount}, deterministic={options.DeterministicBuild}.");
		action("Source: " + fullPath);
		action("Output directory: " + Path.GetFullPath(options.OutputFolder));
		action($"Content ID: {options.ContentId}; title ID: {options.TitleId}; version: {options.Version}.");
		action(KeysAvailable ? "PS5 public RSA profile loaded (passcode[7] + mount-image + token)." : "Warning: the PS5 public RSA profile is unavailable; publisher wrapping is disabled.");
		if (!KeysAvailable)
		{
			list.Add("PS5 publishing keys are unavailable.");
		}
		EnsureParamJson(options, fullPath, action, list);
		FileInfo[] array = new DirectoryInfo(fullPath).EnumerateFiles("*", SearchOption.AllDirectories).ToArray();
		int value = new DirectoryInfo(fullPath).EnumerateDirectories("*", SearchOption.AllDirectories).Count() + 1;
		long value2 = array.Sum((FileInfo file) => file.Length);
		action($"Source scan: {array.Length:N0} files in {value:N0} directories, {value2:N0} bytes ({FormatByteSize(value2)}).");
		foreach (FileInfo item in array.OrderByDescending((FileInfo file) => file.Length).ThenBy((FileInfo file) => file.FullName, StringComparer.Ordinal).Take(5))
		{
			action($"  input: {Path.GetRelativePath(fullPath, item.FullName)} ({item.Length:N0} bytes; {FormatByteSize(item.Length)})");
		}
		string playGoDirectory = Path.Combine(fullPath, "sce_sys");
		string[] array2 = new string[3] { "playgo-chunk.dat", "playgo-hash-table.dat", "playgo-ficm.dat" };
		int num = array2.Count((string name) => File.Exists(Path.Combine(playGoDirectory, name)));
		Action<string> action2 = action;
		string obj;
		if (num == array2.Length)
		{
			obj = "PlayGo input: complete prepared set found; its layout will be preserved.";
		}
		else
		{
			obj = ((num == 0) ? $"PlayGo input: no prepared files; generating {options.PlayGoChunkCount} automatic chunk(s)." : $"PlayGo input: {num}/3 prepared files found; missing entries will be generated.");
		}
		action2(obj);
		ProsperoBuildResult prosperoBuildResult = BuildCore(options, fullPath, action, list);
		long value3 = (File.Exists(prosperoBuildResult.OutputPath) ? new FileInfo(prosperoBuildResult.OutputPath).Length : 0);
		action($"Build finished in {buildTimer.Elapsed:hh\\:mm\\:ss\\.fff}; output {value3:N0} bytes ({FormatByteSize(value3)}), warnings={list.Count}.");
		return prosperoBuildResult;
	}

	/// <summary>
	/// Produces the final PS5 package via <see cref="T:LibProsperoPkg.PKG.ProsperoPkgBuilder" />.
	/// The output is a complete <c>\x7FCNT</c> package with the inner + AES-XTS-encrypted outer PFS,
	/// all entries, every metadata digest and the CNT header public wrap. The result is checked
	/// in-process with the reader and an outer-PFS decrypt round-trip. On-console acceptance
	/// depends on console mode and firmware.
	/// </summary>
	private static ProsperoBuildResult BuildCore(ProsperoBuildOptions options, string sourceFolder, Action<string> log, List<string> warnings)
	{
		byte[] array = ((options.PublisherImageMode == ProsperoPublisherImageMode.Native) ? (options.NapsOuterBlockCmacKey ?? ProsperoPublishingSidecar.TryLoadNapsCmacKey()) : null);
		byte[] array2 = options.NapsPfsImageKey;
		byte[] array3 = options.NapsPfsImageSeed;
		byte[] array4 = options.PublisherImageKey ?? ProsperoPublishingSidecar.TryLoadPublisherImageKey();
		byte[] array5 = options.PublisherEntryKeys ?? ProsperoPublishingSidecar.TryLoadPublisherEntryKeys();
		byte[] array6 = options.NapsMeta18 ?? ProsperoPublishingSidecar.TryLoadNapsMeta18();
		if (array3 == null)
		{
			array3 = ProsperoPublishingSidecar.TryLoadNapsPfsImageSeed();
		}
		if (array2 == null && (array3 != null || options.OuterPfsSeed != null))
		{
			array2 = ProsperoPublishingSidecar.TryLoadNapsPfsImageKey();
		}
		if (options.OuterPfsSeed != null && array3 != null && !options.OuterPfsSeed.AsSpan().SequenceEqual(array3))
		{
			throw new InvalidDataException("pfs_image_seed.bin does not match OuterPfsSeed.");
		}
		if (options.NapsOuterBlockCmacKey == null && array != null)
		{
			log($"Loaded {"naps_cmac_key.bin"} from {ProsperoPublishingSidecar.DefaultDirectory}.");
		}
		if (options.NapsPfsImageSeed == null && array3 != null)
		{
			log($"Loaded {"pfs_image_seed.bin"} from {ProsperoPublishingSidecar.DefaultDirectory}.");
		}
		if (options.NapsPfsImageKey == null && array2 != null)
		{
			log($"Loaded expected {"pfs_image_key.bin"} from {ProsperoPublishingSidecar.DefaultDirectory}.");
		}
		if (options.PublisherImageKey == null && array4 != null)
		{
			log($"Loaded {"pkg_image_key.bin"} from {ProsperoPublishingSidecar.DefaultDirectory}.");
		}
		if (options.PublisherEntryKeys == null && array5 != null)
		{
			log($"Loaded {"pkg_entry_keys.bin"} from {ProsperoPublishingSidecar.DefaultDirectory}.");
		}
		if (options.NapsMeta18 == null && array6 != null)
		{
			log($"Loaded {"naps_meta_18.dat"} from {ProsperoPublishingSidecar.DefaultDirectory}.");
		}
		string text = Path.Combine(options.OutputFolder, ComposePkgFileName(options.ContentId, options.Version));
		bool flag = options.OutputFormat != ProsperoOutputFormat.MetadataContainer && options.Mode != ProsperoPackageMode.AdditionalContentNoData;
		bool flag2 = options.OutputFormat == ProsperoOutputFormat.RetailImage;
		string text2 = (flag ? Path.Combine(options.OutputFolder, "." + Path.GetFileName(text) + ".cnt.tmp") : text);
		LibProsperoPkg.Util.BuildCleaner.RegisterTempPath(text2);
		ProsperoPkgBuildProperties props = new ProsperoPkgBuildProperties
		{
			CancellationToken = options.CancellationToken,
			SourceFolder = sourceFolder,
			ContentId = options.ContentId,
			PrimaryId = options.PrimaryId,
			Passcode = options.Passcode,
			VolumeType = ProsperoVolumeTypeForMode(options.Mode),
			TimeStamp = options.TimeStamp,
			CompressInnerImage = options.CompressInnerImage,
			InnerCompression = options.InnerCompression,
			LegacyZlibCompressionLevel = options.LegacyZlibCompressionLevel,
			LegacyZlibMaxDegreeOfParallelism = options.LegacyZlibMaxDegreeOfParallelism,
			MaxHashingThreads = options.MaxHashingThreads,
			UsePublisherPprNaps = options.UsePublisherPprNaps,
			PlayGoChunkCount = options.PlayGoChunkCount,
			PublisherImageMode = options.PublisherImageMode,
			NapsOuterBlockCmacKey = array,
			NapsMeta18 = array6,
			NapsIntegrityProvider = options.NapsIntegrityProvider,
			PublisherAfidAssignments = options.PublisherAfidAssignments,
			NapsPfsImageKey = array2,
			NapsPfsImageSeed = array3,
			PublisherImageKey = array4,
			PublisherEntryKeys = array5,
			OuterPfsSeed = options.OuterPfsSeed,
			DeterministicBuild = options.DeterministicBuild,
			MetadataSigner = options.MetadataSigner,
			LicenseProvider = options.LicenseProvider ?? new LibProsperoPkg.PKG.FakeLicenseProvider()
		};
		bool num = options.UsePublisherPprNaps && options.Mode != ProsperoPackageMode.AdditionalContentNoData;
		if (options.RequirePublisherCompatibility && !ProsperoKeys.IsPublisherRsaProfileAvailable)
		{
			throw new InvalidOperationException("Strict publisher compatibility requires the complete sc2 public RSA profile.");
		}
		if (num && array == null)
		{
			log("NAPS outer-block CMAC is disabled: Publishing Tools 2.79 debug/AC leaves the eight-byte tags zero unless a keyed profile is explicitly selected.");
		}
		if (options.PublisherImageMode == ProsperoPublisherImageMode.PlaintextNoAuth)
		{
			log("PLAINTEXT_NOAUTH: outer PFS AES-XTS and keyed NAPS outer-block authentication are disabled; ordinary PFS SHA3 hashes remain enabled.");
		}
		if (options.PlayGoChunkCount > 1)
		{
			log($"PlayGo: generating experimental single-scenario layout with {options.PlayGoChunkCount} chunks.");
		}
		log("Building the PS5 package...");
		ProsperoPkgBuilder.Build(props, text2, out byte[] nestedImageDigest, out ProsperoSiBuildInputs siInputs, log);
		if (!File.Exists(text2))
		{
			throw new InvalidOperationException("The PS5 PKG builder did not produce an output package.");
		}
		try
		{
			ProsperoPkgType? value = ProsperoPkgReader.DetectType(text2);
			if (!value.HasValue)
			{
				warnings.Add("The produced package is not a recognisable PS5 PKG.");
			}
			else
			{
				log($"Validated intermediate container: {value} PS5 CNT (metadata only).");
			}
		}
		catch (Exception ex)
		{
			warnings.Add("Output container validation failed: " + ex.Message);
		}
		if (!flag)
		{
			string text3 = siInputs?.TemporaryInnerImagePath;
			if (text3 != null)
			{
				TryDelete(text3);
			}
			log((options.Mode == ProsperoPackageMode.AdditionalContentNoData) ? "Done (PSAL CNT+SI package; no PFS/FIH layer)." : "Done (CNT metadata container).");
			return new ProsperoBuildResult
			{
				OutputPath = text2,
				Warnings = warnings
			};
		}
		try
		{
			log(flag2 ? "Finalizing the CNT into a Retail (FIH) image..." : "Finalizing the CNT into a debug (FIH) image...");
			Func<Stream, byte[]> siArchiveStreamFactory = ((flag2 || siInputs == null) ? null : ((Func<Stream, byte[]>)((Stream mountImage) => ProsperoSiArchive.BuildDebugSiSegment(siInputs.Xml, siInputs.PlayGoChunkDat, mountImage, siInputs.InnerImageSize, warnings, siInputs.NapsMeta18, siInputs.IncludePfsImageXml, siInputs.ContentFiles, siInputs.InnerImage, siInputs.NapsIntegrityProvider, siInputs.NapsPfsImageKey, siInputs.NapsPfsImageSeed, log, options.MaxHashingThreads))));
			IReadOnlyList<string> collection = ProsperoFihBuilder.BuildFromCnt(text2, text, flag2 ? ProsperoFihVariant.Official : ProsperoFihVariant.Debug, log, null, null, siArchiveStreamFactory, nestedImageDigest, (long)(siInputs?.NapsLayoutSize ?? 0), siInputs?.NestedMetaBaseBlocks ?? 0, siInputs?.ContentVersionHigh ?? 0, (int)(siInputs?.FihNapsFileCount ?? 0), siInputs?.AppFileCount ?? 0, siInputs?.SparseAfidCount ?? 0, siInputs?.EmptyFileCount ?? 0, siInputs?.OuterSuperblockIndex ?? (-1), options.RetailFinalizationProvider);
			warnings.AddRange(collection);
			ProsperoPkgType? prosperoPkgType = ProsperoPkgReader.DetectType(text);
			ProsperoPkgType prosperoPkgType2 = (flag2 ? ProsperoPkgType.FullRetail : ProsperoPkgType.FullDebug);
			if (prosperoPkgType != prosperoPkgType2)
			{
				warnings.Add($"Produced FIH image was detected as {prosperoPkgType}, expected {prosperoPkgType2}.");
			}
			else
			{
				log($"Validated output container: {prosperoPkgType2} PS5 FIH image.");
			}
		}
		finally
		{
			TryDelete(text2);
			string text4 = siInputs?.TemporaryInnerImagePath;
			if (text4 != null)
			{
				TryDelete(text4);
			}
		}
		log(flag2 ? "Done (Retail FIH)." : "Done (debug FIH).");
		return new ProsperoBuildResult
		{
			OutputPath = text,
			Warnings = warnings
		};
	}

	/// <summary>Best-effort deletion of an intermediate build artifact.</summary>
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

	private static string FormatByteSize(long value)
	{
		string[] array = new string[5] { "B", "KiB", "MiB", "GiB", "TiB" };
		double num = Math.Max(0L, value);
		int num2 = 0;
		while (num >= 1024.0 && num2 < array.Length - 1)
		{
			num /= 1024.0;
			num2++;
		}
		if (num2 == 0)
		{
			return $"{num:F0} {array[num2]}";
		}
		return $"{num:F2} {array[num2]}";
	}

	/// <summary>
	/// Compares two PS5 containers field-by-field (parsed header and entry table). Useful to verify
	/// that a candidate package matches a known-good reference container.
	/// </summary>
	/// <returns>An empty list when the containers match; otherwise the differences found.</returns>
	public static IReadOnlyList<string> CompareContainers(string referencePkg, string candidatePkg)
	{
		List<string> list = new List<string>();
		ProsperoPkg prosperoPkg = ProsperoPkgReader.Read(referencePkg);
		ProsperoPkg prosperoPkg2 = ProsperoPkgReader.Read(candidatePkg);
		if (prosperoPkg.Type != prosperoPkg2.Type)
		{
			list.Add($"Type: {prosperoPkg.Type} != {prosperoPkg2.Type}");
		}
		ProsperoPkgHeader header = prosperoPkg.Header;
		if (header != null)
		{
			ProsperoPkgHeader header2 = prosperoPkg2.Header;
			if (header2 != null)
			{
				if (header.EntryCount != header2.EntryCount)
				{
					list.Add($"EntryCount: {header.EntryCount} != {header2.EntryCount}");
				}
				if (header.EntryTableOffset != header2.EntryTableOffset)
				{
					list.Add($"EntryTableOffset: {header.EntryTableOffset:X} != {header2.EntryTableOffset:X}");
				}
				if (header.ContentId != header2.ContentId)
				{
					list.Add("ContentId: " + header.ContentId + " != " + header2.ContentId);
				}
				if (header.ContentType != header2.ContentType)
				{
					list.Add($"ContentType: {header.ContentType} != {header2.ContentType}");
				}
			}
		}
		int num = Math.Min(prosperoPkg.Entries.Count, prosperoPkg2.Entries.Count);
		for (int i = 0; i < num; i++)
		{
			ProsperoPkgEntry prosperoPkgEntry = prosperoPkg.Entries[i];
			ProsperoPkgEntry prosperoPkgEntry2 = prosperoPkg2.Entries[i];
			if (prosperoPkgEntry.RawId != prosperoPkgEntry2.RawId || prosperoPkgEntry.DataSize != prosperoPkgEntry2.DataSize || prosperoPkgEntry.Flags1 != prosperoPkgEntry2.Flags1)
			{
				list.Add($"Entry[{i}] {prosperoPkgEntry.Id}/{prosperoPkgEntry2.Id}: id={prosperoPkgEntry.RawId:X}/{prosperoPkgEntry2.RawId:X} size={prosperoPkgEntry.DataSize}/{prosperoPkgEntry2.DataSize} flags={prosperoPkgEntry.Flags1:X}/{prosperoPkgEntry2.Flags1:X}");
			}
		}
		if (prosperoPkg.Entries.Count != prosperoPkg2.Entries.Count)
		{
			list.Add($"Entry count differs: {prosperoPkg.Entries.Count} vs {prosperoPkg2.Entries.Count}");
		}
		return list;
	}

	private static void EnsureParamJson(ProsperoBuildOptions options, string sourceFolder, Action<string> log, List<string> warnings)
	{
		string text = ProsperoPkgBuilder.ResolveSourceFile(sourceFolder, "sce_sys/param.json");
		if (text != null)
		{
			string fullPath = Path.GetFullPath(Path.Combine(sourceFolder, "sce_sys", "param.json"));
			log(string.Equals(Path.GetFullPath(text), fullPath, StringComparison.OrdinalIgnoreCase) ? "Using existing sce_sys/param.json." : ("Using GP5-mapped sce_sys/param.json from " + text + "."));
			return;
		}
		if (!options.GenerateParamJsonIfMissing)
		{
			throw new InvalidOperationException("sce_sys/param.json is missing and auto-generation is disabled.");
		}
		string text2 = Path.Combine(sourceFolder, "sce_sys");
		string path = Path.Combine(text2, "param.json");
		Directory.CreateDirectory(text2);
		log("sce_sys/param.json not found - generating a minimal one from the supplied metadata.");
		File.WriteAllText(path, BuildMinimalParamJson(options), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		warnings.Add("A minimal param.json was generated; review it for store-grade packages.");
	}

	private static string BuildMinimalParamJson(ProsperoBuildOptions options)
	{
		string text = (IsValidTitleId(options.TitleId) ? options.TitleId : options.ContentId.Substring(7, 9));
		string text2 = (string.IsNullOrWhiteSpace(options.Title) ? text : options.Title);
		string text3 = NormalizeVersion(options.Version);
		JsonObject jsonObject = new JsonObject
		{
			["conceptId"] = "10000000",
			["contentId"] = options.ContentId,
			["masterVersion"] = text3,
			["requiredSystemSoftwareVersion"] = "00.00.00.00",
			["titleId"] = text,
			["localizedParameters"] = new JsonObject
			{
				["defaultLanguage"] = "en-US",
				["en-US"] = new JsonObject { ["titleName"] = text2 }
			}
		};
		if (options.Mode != ProsperoPackageMode.AdditionalContentNoData)
		{
			jsonObject["applicationCategoryType"] = CategoryTypeForMode(options.Mode);
			jsonObject["contentVersion"] = text3;
		}
		return jsonObject.ToJsonString(new JsonSerializerOptions
		{
			WriteIndented = true
		});
	}

	private static string ComposePkgFileName(string contentId, string version)
	{
		string text = NormalizeVersion(version).Replace(".", "");
		if (text.Length < 4)
		{
			text = text.PadLeft(4, '0');
		}
		return $"{contentId}-A{text.Substring(0, 4)}-V{text.Substring(0, 4)}.pkg";
	}

	private static string NormalizeVersion(string? version)
	{
		if (string.IsNullOrWhiteSpace(version))
		{
			return "01.00";
		}
		version = version.Trim();
		if (!Regex.IsMatch(version, "^[0-9]{2}\\.[0-9]{2}$"))
		{
			return "01.00";
		}
		return version;
	}
}
