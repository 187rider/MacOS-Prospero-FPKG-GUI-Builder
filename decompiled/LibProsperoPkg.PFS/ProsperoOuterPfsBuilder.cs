using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LibProsperoPkg.PFS;

/// <summary>
/// Assembles, signs, and encrypts the plaintext outer-PFS image of a PS5 nwonly finalized package.
/// See the file header for the full byte layout.
/// </summary>
public static class ProsperoOuterPfsBuilder
{
	private sealed class IndirectNodePlan
	{
		public required int BlockIndex { get; init; }

		public required int Depth { get; init; }

		public required int FirstDataOffset { get; init; }

		public required int DataBlockCount { get; init; }

		public List<IndirectNodePlan> Children { get; } = new List<IndirectNodePlan>();
	}

	private sealed class IndirectTreePlan
	{
		public required int InodeLevel { get; init; }

		public required IndirectNodePlan Root { get; init; }
	}

	private delegate void CopyDigest(int blockIndex, Span<byte> destination);

	/// <summary>The outer finalized-image PFS block size (one block = one AES-XTS data unit).</summary>
	public const int BlockSize = 65536;

	private const string FlatPathTableName = "inode_flat_path_table";

	private const string UrootName = "uroot";

	private const int MetadataInodeCount = 3;

	private const ushort ModeDir = 16749;

	private const ushort ModeFile = 33133;

	private const uint FlagsInternalMeta = 131084u;

	private const uint FlagsDir = 12u;

	private const uint FlagsFile = 13u;

	private const ulong FltRoundConstant = 9223372039002292353uL;

	private const ulong FltSeed0 = 10577419142525243217uL;

	private const ulong FltSeed1 = 701355796979237965uL;

	private const uint FltVersion = 1u;

	private const uint FltHeaderSize = 16u;

	private const uint FltDataOffset = 64u;

	private static readonly byte[] FltMagic = new byte[4] { 127, 70, 76, 84 };

	private const int DirectBlockSlots = 12;

	private const int SignedPointerSize = 36;

	private const int IndirectLevelCount = 5;

	private const int IndirectEntriesPerBlock = 1820;

	/// <summary>
	/// Identifies an explicitly selected plaintext/no-auth outer image while its PFS mode remains
	/// the kernel-supported encrypted mode. The seed is not used for key derivation in this profile.
	/// </summary>
	public static ReadOnlySpan<byte> PlaintextNoAuthSeedMarker => "PPRPLAIN-NOAUTH!"u8;

	/// <summary>
	/// Calculates which direct and indirect levels are needed for a file of
	/// <paramref name="dataBlockCount" /> blocks. No image or indirect-map buffers are allocated.
	/// </summary>
	public static ProsperoOuterAddressingGeometry GetAddressingGeometry(long dataBlockCount)
	{
		if (dataBlockCount < 0)
		{
			throw new ArgumentOutOfRangeException("dataBlockCount");
		}
		long num = Math.Min(dataBlockCount, 12L);
		long num2 = dataBlockCount - num;
		long[] array = new long[5];
		long[] array2 = new long[5];
		int highestIndirectLevel = -1;
		for (int i = 0; i < 5; i++)
		{
			if (num2 <= 0)
			{
				break;
			}
			int num3 = i + 1;
			long val = SaturatingPow(1820L, num3);
			long num4 = (array[i] = Math.Min(num2, val));
			array2[i] = CountIndirectTreeBlocks(num4, num3);
			num2 -= num4;
			highestIndirectLevel = i;
		}
		if (num2 > 0)
		{
			throw new NotSupportedException($"A signed outer-PFS inode cannot address {dataBlockCount} data blocks with {5} indirect levels.");
		}
		if (dataBlockCount <= int.MaxValue)
		{
			IndirectTreePlan[][] array3 = PlanIndirectTrees(new int[1] { checked((int)dataBlockCount) }, 0, out var _);
			Array.Clear(array);
			Array.Clear(array2);
			highestIndirectLevel = -1;
			IndirectTreePlan[] array4 = array3[0];
			foreach (IndirectTreePlan indirectTreePlan in array4)
			{
				array[indirectTreePlan.InodeLevel] = indirectTreePlan.Root.DataBlockCount;
				array2[indirectTreePlan.InodeLevel] = CountIndirectNodes(indirectTreePlan.Root);
				highestIndirectLevel = indirectTreePlan.InodeLevel;
			}
		}
		return new ProsperoOuterAddressingGeometry
		{
			DataBlocks = dataBlockCount,
			DirectDataBlocks = num,
			DataBlocksByIndirectLevel = array,
			MetadataBlocksByIndirectLevel = array2,
			HighestIndirectLevel = highestIndirectLevel
		};
	}

	/// <summary>
	/// Runs the production indirect-tree planner and serializer for one inode level using
	/// deterministic synthetic data digests. This is intentionally internal: it validates
	/// metadata bytes and pointer geometry but does not produce a usable filesystem image.
	/// </summary>
	internal static ProsperoOuterAddressingSerializationProbe BuildAddressingSerializationProbe(long dataBlockCount, int inodeLevel)
	{
		if (dataBlockCount < 0 || dataBlockCount > int.MaxValue)
		{
			throw new ArgumentOutOfRangeException("dataBlockCount");
		}
		if ((uint)inodeLevel >= 5u)
		{
			throw new ArgumentOutOfRangeException("inodeLevel");
		}
		checked
		{
			int num = (int)dataBlockCount;
			int firstMetadataBlock = num + 4;
			IndirectTreePlan[][] array = PlanIndirectTrees(new int[1] { num }, firstMetadataBlock, out var _);
			IndirectTreePlan indirectTreePlan = null;
			IndirectTreePlan[] array2 = array[0];
			foreach (IndirectTreePlan indirectTreePlan2 in array2)
			{
				if (indirectTreePlan2.InodeLevel == inodeLevel)
				{
					indirectTreePlan = indirectTreePlan2;
					break;
				}
			}
			if (indirectTreePlan == null)
			{
				throw new ArgumentException($"A {dataBlockCount}-block inode does not use ib[{inodeLevel}].", "inodeLevel");
			}
			Dictionary<int, byte[]> serialized = new Dictionary<int, byte[]>();
			byte[] rootHash = WriteIndirectTree(indirectTreePlan.Root, 0, (int dataBlock, Span<byte> destination) =>
			{
				destination.Clear();
				BinaryPrimitives.WriteInt32LittleEndian(destination, dataBlock);
				BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(28, 4), ~dataBlock);
			}, (int blockIndex, byte[] block) =>
			{
				serialized.Add(blockIndex, (byte[])block.Clone());
			});
			return new ProsperoOuterAddressingSerializationProbe
			{
				InodeLevel = indirectTreePlan.InodeLevel,
				RootBlockIndex = indirectTreePlan.Root.BlockIndex,
				FirstDataOffset = indirectTreePlan.Root.FirstDataOffset,
				DataBlockCount = indirectTreePlan.Root.DataBlockCount,
				RootHash = rootHash,
				MetadataBlocks = serialized
			};
		}
	}

	private static long SaturatingPow(long value, int exponent)
	{
		long num = 1L;
		for (int i = 0; i < exponent; i++)
		{
			if (num > long.MaxValue / value)
			{
				return long.MaxValue;
			}
			num *= value;
		}
		return num;
	}

	private static long CountIndirectTreeBlocks(long dataBlocks, int depth)
	{
		long num = 0L;
		long num2 = 1L;
		for (int i = 0; i < depth; i++)
		{
			num2 = ((num2 <= 5067786833436690L) ? (num2 * 1820) : long.MaxValue);
			long num3 = dataBlocks / num2 + ((dataBlocks % num2 != 0) ? 1 : 0);
			num = checked(num + num3);
		}
		return num;
	}

	private static IndirectTreePlan[][] PlanIndirectTrees(int[] fileBlockCounts, int firstMetadataBlock, out int nextMetadataBlock)
	{
		IndirectTreePlan[][] array = new IndirectTreePlan[fileBlockCounts.Length][];
		int nextBlock = firstMetadataBlock;
		for (int i = 0; i < fileBlockCounts.Length; i++)
		{
			int num = Math.Max(0, fileBlockCounts[i] - 12);
			int num2 = 12;
			List<IndirectTreePlan> list = new List<IndirectTreePlan>(5);
			for (int j = 0; j < 5; j++)
			{
				if (num <= 0)
				{
					break;
				}
				int num3 = j + 1;
				long val = SaturatingPow(1820L, num3);
				int num4;
				checked
				{
					num4 = (int)Math.Min(num, val);
					IndirectNodePlan root = AllocateIndirectNode(num3, num2, num4, ref nextBlock);
					list.Add(new IndirectTreePlan
					{
						InodeLevel = j,
						Root = root
					});
					num2 += num4;
				}
				num -= num4;
			}
			if (num > 0)
			{
				throw new NotSupportedException($"Outer file {i} spans {fileBlockCounts[i]} blocks and exceeds the five-level signed inode address space.");
			}
			array[i] = list.ToArray();
		}
		nextMetadataBlock = nextBlock;
		return array;
	}

	private static IndirectNodePlan AllocateIndirectNode(int depth, int firstDataOffset, int dataBlockCount, ref int nextBlock)
	{
		IndirectNodePlan indirectNodePlan = new IndirectNodePlan
		{
			BlockIndex = nextBlock,
			Depth = depth,
			FirstDataOffset = firstDataOffset,
			DataBlockCount = dataBlockCount
		};
		checked
		{
			nextBlock++;
			if (depth == 1)
			{
				return indirectNodePlan;
			}
		}
		long val = SaturatingPow(1820L, depth - 1);
		int num = dataBlockCount;
		int num2 = firstDataOffset;
		while (num > 0)
		{
			int num3 = checked((int)Math.Min(num, val));
			indirectNodePlan.Children.Add(AllocateIndirectNode(depth - 1, num2, num3, ref nextBlock));
			num2 = checked(num2 + num3);
			num -= num3;
		}
		return indirectNodePlan;
	}

	private static void MarkIndirectBlocksSigned(ProsperoOuterBlockKind[] kinds, IndirectTreePlan[][] plans)
	{
		foreach (IndirectTreePlan[] array in plans)
		{
			foreach (IndirectTreePlan indirectTreePlan in array)
			{
				MarkIndirectNodeSigned(kinds, indirectTreePlan.Root);
			}
		}
	}

	private static void MarkIndirectNodeSigned(ProsperoOuterBlockKind[] kinds, IndirectNodePlan node)
	{
		kinds[node.BlockIndex] = ProsperoOuterBlockKind.Signed;
		foreach (IndirectNodePlan child in node.Children)
		{
			MarkIndirectNodeSigned(kinds, child);
		}
	}

	private static long CountIndirectNodes(IndirectNodePlan node)
	{
		long num = 1L;
		foreach (IndirectNodePlan child in node.Children)
		{
			num = checked(num + CountIndirectNodes(child));
		}
		return num;
	}

	private static byte[] WriteIndirectTree(IndirectNodePlan node, int fileFirstBlock, CopyDigest copyDataDigest, Action<int, byte[]> writeBlock)
	{
		byte[] array = new byte[65536];
		if (node.Depth == 1)
		{
			for (int i = 0; i < node.DataBlockCount; i++)
			{
				int num = checked(fileFirstBlock + node.FirstDataOffset + i);
				int num2 = i * 36;
				copyDataDigest(num, array.AsSpan(num2, 32));
				BinaryPrimitives.WriteInt32LittleEndian(array.AsSpan(num2 + 32, 4), num);
			}
		}
		else
		{
			for (int j = 0; j < node.Children.Count; j++)
			{
				IndirectNodePlan indirectNodePlan = node.Children[j];
				byte[] array2 = WriteIndirectTree(indirectNodePlan, fileFirstBlock, copyDataDigest, writeBlock);
				int num3 = j * 36;
				array2.CopyTo(array, num3);
				BinaryPrimitives.WriteInt32LittleEndian(array.AsSpan(num3 + 32, 4), indirectNodePlan.BlockIndex);
			}
		}
		byte[] result = ProsperoOuterPfsSignature.ComputeBlockHash(array);
		writeBlock(node.BlockIndex, array);
		return result;
	}

	/// <summary>
	/// Builds the plaintext outer-PFS image (assembled + signed, not yet encrypted) for the given
	/// ordered outer <paramref name="files" />. The metadata inodes/dirents/FLT and the superblock
	/// (including all SHA3-256 block hashes and the ICV) are produced here. Use
	/// <see cref="M:LibProsperoPkg.PFS.ProsperoOuterPfsBuilder.Encrypt(LibProsperoPkg.PFS.ProsperoOuterPfsBuildResult,System.ReadOnlySpan{System.Byte},System.ReadOnlySpan{System.Byte})" /> (or <see cref="T:LibProsperoPkg.PFS.ProsperoOuterPfsImage" />'s block-kinds <c>Transform</c>
	/// overload with the returned <see cref="P:LibProsperoPkg.PFS.ProsperoOuterPfsBuildResult.BlockKinds" />) to encrypt.
	/// </summary>
	public static ProsperoOuterPfsBuildResult BuildPlaintext(IReadOnlyList<ProsperoOuterFile> files, ProsperoOuterPfsBuildParameters parameters)
	{
		ArgumentNullException.ThrowIfNull(files, "files");
		ArgumentNullException.ThrowIfNull(parameters, "parameters");
		ValidateImageMode(parameters.ImageMode, "parameters");
		ValidateImageSeed(parameters, "parameters");
		if (files.Count == 0)
		{
			throw new ArgumentException("At least one outer file is required.", "files");
		}
		int num = 0;
		int[] array = new int[files.Count];
		int[] array2 = new int[files.Count];
		for (int i = 0; i < files.Count; i++)
		{
			ProsperoOuterFile prosperoOuterFile = files[i];
			ArgumentNullException.ThrowIfNull(prosperoOuterFile, "f");
			int num2 = Math.Max(1, (prosperoOuterFile.Data.Length + 65536 - 1) / 65536);
			array[i] = num;
			array2[i] = num2;
			num += num2;
		}
		int num3 = num;
		int num4 = num + 1;
		int num5 = num + 2;
		int num6 = num + 3;
		int firstMetadataBlock = num6 + 1;
		IndirectTreePlan[][] array3 = PlanIndirectTrees(array2, firstMetadataBlock, out var nextMetadataBlock);
		int num7 = nextMetadataBlock;
		int num8 = num7 + 1;
		byte[] array4 = new byte[(long)num8 * 65536L];
		for (int j = 0; j < files.Count; j++)
		{
			byte[] data = files[j].Data;
			Buffer.BlockCopy(data, 0, array4, array[j] * 65536, data.Length);
		}
		BuildSuperRootDirents(array4.AsSpan(num5 * 65536, 65536));
		BuildFlatPathTable(array4.AsSpan(num6 * 65536, 65536), files);
		BuildUrootDirents(array4.AsSpan(num7 * 65536, 65536), files);
		BuildInodeTable(array4, num4, parameters, files, array, array2, array3, num5, num6, num7);
		byte[] inodeTableHash = ProsperoOuterPfsSignature.ComputeBlockHash(array4.AsSpan(num4 * 65536, 65536));
		BuildSuperblock(array4.AsSpan(num3 * 65536, 65536), parameters, files.Count, num8, num4, inodeTableHash);
		ProsperoOuterBlockKind[] array5 = new ProsperoOuterBlockKind[num8];
		for (int k = 0; k < files.Count; k++)
		{
			ProsperoOuterBlockKind prosperoOuterBlockKind = (files[k].Signed ? ProsperoOuterBlockKind.Signed : ProsperoOuterBlockKind.Data);
			for (int l = 0; l < array2[k]; l++)
			{
				array5[array[k] + l] = prosperoOuterBlockKind;
			}
		}
		array5[num3] = ProsperoOuterBlockKind.Plaintext;
		array5[num4] = ProsperoOuterBlockKind.Signed;
		array5[num5] = ProsperoOuterBlockKind.Signed;
		array5[num6] = ProsperoOuterBlockKind.Signed;
		MarkIndirectBlocksSigned(array5, array3);
		array5[num7] = ProsperoOuterBlockKind.Signed;
		return new ProsperoOuterPfsBuildResult
		{
			Plaintext = array4,
			BlockKinds = array5,
			SuperblockIndex = num3,
			FileFirstBlock = array,
			FileBlockCount = array2,
			InodeTableIndex = num4,
			SuperRootDirentIndex = num5,
			FltIndex = num6,
			UrootDirentIndex = num7
		};
	}

	/// <summary>
	/// Encrypts a plaintext outer-PFS image in place using the supplied AES-XTS key pair and the
	/// per-block classification from <see cref="M:LibProsperoPkg.PFS.ProsperoOuterPfsBuilder.BuildPlaintext(System.Collections.Generic.IReadOnlyList{LibProsperoPkg.PFS.ProsperoOuterFile},LibProsperoPkg.PFS.ProsperoOuterPfsBuildParameters)" />.
	/// </summary>
	public static void Encrypt(ProsperoOuterPfsBuildResult build, ReadOnlySpan<byte> tweakKey, ReadOnlySpan<byte> dataKey)
	{
		ArgumentNullException.ThrowIfNull(build, "build");
		ProsperoOuterPfsImage.Transform(build.Plaintext, tweakKey, dataKey, 65536, build.BlockKinds, encrypt: true);
	}

	/// <summary>
	/// Convenience: builds the plaintext image and encrypts it with keys derived from the package
	/// <paramref name="contentId" /> + <paramref name="passcode" /> and the build seed, returning the
	/// final on-disk ciphertext image.
	/// </summary>
	public static byte[] BuildEncrypted(IReadOnlyList<ProsperoOuterFile> files, ProsperoOuterPfsBuildParameters parameters, string contentId, string passcode)
	{
		ArgumentNullException.ThrowIfNull(parameters, "parameters");
		if (parameters.ImageMode != ProsperoPublisherImageMode.Native)
		{
			throw new ArgumentException("BuildEncrypted requires the native publisher image mode.", "parameters");
		}
		byte[] seed = parameters.Seed;
		if (seed == null || seed.Length != 16)
		{
			throw new ArgumentException("A 16-byte build seed is required to encrypt the image.", "parameters");
		}
		ProsperoOuterPfsBuildResult prosperoOuterPfsBuildResult = BuildPlaintext(files, parameters);
		(byte[] TweakKey, byte[] DataKey) tuple = ProsperoPfsKeys.DeriveImageEncryptionKeys(ProsperoPfsKeys.DeriveEkpfs(contentId, passcode), parameters.Seed);
		var (array, _) = tuple;
		Encrypt(dataKey: tuple.DataKey, build: prosperoOuterPfsBuildResult, tweakKey: array);
		return prosperoOuterPfsBuildResult.Plaintext;
	}

	/// <summary>
	/// Builds the finalized outer-PFS image for a package: assembles the data-first plaintext image,
	/// captures the per-block digest table and superblock ICV from the plaintext, encrypts it with keys
	/// derived from <paramref name="ekpfs" /> and the build seed, and produces the image-tree snapshot.
	/// </summary>
	/// <param name="files">The ordered outer files (nested image first, then the layout descriptor).</param>
	/// <param name="parameters">Build parameters; the 16-byte seed drives key derivation.</param>
	/// <param name="ekpfs">The 32-byte package image key.</param>
	public static ProsperoOuterPackageImage BuildForPackage(IReadOnlyList<ProsperoOuterFile> files, ProsperoOuterPfsBuildParameters parameters, byte[] ekpfs)
	{
		ArgumentNullException.ThrowIfNull(parameters, "parameters");
		if (parameters.ImageMode != ProsperoPublisherImageMode.Native)
		{
			throw new ArgumentException("BuildForPackage requires the native publisher image mode; use BuildForPackageToFile with encryptOutput=false for plaintext/no-auth images.", "parameters");
		}
		ArgumentNullException.ThrowIfNull(ekpfs, "ekpfs");
		byte[] array = parameters.Seed ?? new byte[16];
		if (array.Length != 16)
		{
			throw new ArgumentException("A 16-byte build seed is required.", "parameters");
		}
		ProsperoOuterPfsBuildResult prosperoOuterPfsBuildResult = BuildPlaintext(files, parameters);
		int blockCount = prosperoOuterPfsBuildResult.BlockCount;
		byte[] array2 = new byte[blockCount * 32];
		for (int i = 0; i < blockCount; i++)
		{
			ProsperoOuterPfsSignature.ComputeBlockHash(prosperoOuterPfsBuildResult.Plaintext.AsSpan(i * 65536, 65536)).CopyTo(array2, i * 32);
		}
		byte[] superblockIcv = ProsperoOuterPfsSignature.ComputeSuperblockIcv(prosperoOuterPfsBuildResult.Plaintext.AsSpan(prosperoOuterPfsBuildResult.SuperblockIndex * 65536, 65536));
		ProsperoPfsImageTreeInfo tree = BuildTreeInfo(prosperoOuterPfsBuildResult, files, array, superblockIcv);
		var (array3, array4) = ProsperoPfsKeys.DeriveImageEncryptionKeys(ekpfs, array);
		Encrypt(prosperoOuterPfsBuildResult, array3, array4);
		return new ProsperoOuterPackageImage
		{
			Ciphertext = prosperoOuterPfsBuildResult.Plaintext,
			PfsSize = prosperoOuterPfsBuildResult.Plaintext.LongLength,
			ImageDigests = array2,
			SuperblockIcv = superblockIcv,
			SuperblockIndex = prosperoOuterPfsBuildResult.SuperblockIndex,
			Tree = tree
		};
	}

	/// <summary>
	/// File-backed counterpart of <see cref="M:LibProsperoPkg.PFS.ProsperoOuterPfsBuilder.BuildForPackage(System.Collections.Generic.IReadOnlyList{LibProsperoPkg.PFS.ProsperoOuterFile},LibProsperoPkg.PFS.ProsperoOuterPfsBuildParameters,System.Byte[])" />. Payloads and the finished image are
	/// processed one 64-KiB block at a time; memory use is proportional to the digest table rather
	/// than to the image size. Set <paramref name="encryptOutput" /> to <see langword="false" /> and
	/// pass a null <paramref name="ekpfs" /> to retain the assembled plaintext diagnostic image.
	/// </summary>
	public static ProsperoOuterPackageFileResult BuildForPackageToFile(IReadOnlyList<ProsperoOuterFileSource> files, ProsperoOuterPfsBuildParameters parameters, byte[]? ekpfs, string outputPath, bool encryptOutput = true, Action<string>? log = null, CancellationToken cancellationToken = default, int maxHashingThreads = 2)
	{
		ArgumentNullException.ThrowIfNull(files, "files");
		ArgumentNullException.ThrowIfNull(parameters, "parameters");
		ValidateImageMode(parameters.ImageMode, "parameters");
		ValidateImageSeed(parameters, "parameters");
		if (encryptOutput)
		{
			ArgumentNullException.ThrowIfNull(ekpfs, "ekpfs");
		}
		if (encryptOutput && parameters.ImageMode == ProsperoPublisherImageMode.PlaintextNoAuth)
		{
			throw new ArgumentException("PLAINTEXT_NOAUTH cannot be combined with AES-XTS output encryption.", "parameters");
		}
		ArgumentException.ThrowIfNullOrWhiteSpace(outputPath, "outputPath");
		if (files.Count == 0)
		{
			throw new ArgumentException("At least one outer file is required.", "files");
		}
		byte[] array = parameters.Seed ?? new byte[16];
		if (array.Length != 16)
		{
			throw new ArgumentException("A 16-byte build seed is required.", "parameters");
		}
		byte[] seed = ((parameters.ImageMode == ProsperoPublisherImageMode.PlaintextNoAuth) ? PlaintextNoAuthSeedMarker.ToArray() : array);
		long[] array2 = new long[files.Count];
		int[] array3 = new int[files.Count];
		int[] array4 = new int[files.Count];
		int num = 0;
		for (int i = 0; i < files.Count; i++)
		{
			ProsperoOuterFileSource prosperoOuterFileSource = files[i] ?? throw new ArgumentException($"Outer file {i} is null.", "files");
			ArgumentException.ThrowIfNullOrWhiteSpace(prosperoOuterFileSource.Name, "file.Name");
			ArgumentException.ThrowIfNullOrWhiteSpace(prosperoOuterFileSource.Path, "file.Path");
			array2[i] = new FileInfo(prosperoOuterFileSource.Path).Length;
			long num2 = Math.Max(1L, checked(array2[i] + 65536 - 1) / 65536);
			checked
			{
				array4[i] = (int)num2;
				array3[i] = num;
				num += array4[i];
			}
		}
		int num3 = num;
		int num4;
		int num5;
		int num6;
		IndirectTreePlan[][] array5;
		int num7;
		int num8;
		long num9;
		ProsperoOuterBlockKind[] array6;
		checked
		{
			num4 = num + 1;
			num5 = num + 2;
			num6 = num + 3;
			int firstMetadataBlock = num6 + 1;
			array5 = PlanIndirectTrees(array4, firstMetadataBlock, out var nextMetadataBlock);
			num7 = nextMetadataBlock;
			num8 = num7 + 1;
			num9 = unchecked((long)num8) * 65536L;
			array6 = new ProsperoOuterBlockKind[num8];
		}
		for (int j = 0; j < files.Count; j++)
		{
			ProsperoOuterBlockKind prosperoOuterBlockKind = (files[j].Signed ? ProsperoOuterBlockKind.Signed : ProsperoOuterBlockKind.Data);
			for (int k = 0; k < array4[j]; k++)
			{
				array6[array3[j] + k] = prosperoOuterBlockKind;
			}
		}
		MarkIndirectBlocksSigned(array6, array5);
		array6[num3] = ProsperoOuterBlockKind.Plaintext;
		array6[num4] = ProsperoOuterBlockKind.Signed;
		array6[num5] = ProsperoOuterBlockKind.Signed;
		array6[num6] = ProsperoOuterBlockKind.Signed;
		array6[num7] = ProsperoOuterBlockKind.Signed;
		string fullPath = Path.GetFullPath(outputPath);
		string text = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
		Directory.CreateDirectory(text);
		StringComparison comparisonType = (OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
		foreach (ProsperoOuterFileSource file in files)
		{
			if (string.Equals(Path.GetFullPath(file.Path), fullPath, comparisonType))
			{
				throw new ArgumentException("An outer-PFS source cannot also be the output file.", "outputPath");
			}
		}
		string text2 = Guid.NewGuid().ToString("N");
		string text3 = Path.Combine(text, ".libprospero-outer-plain-" + text2 + ".tmp");
		string text4 = Path.Combine(text, ".libprospero-outer-cipher-" + text2 + ".tmp");
		byte[] imageDigests = new byte[checked(num8 * 32)];
		try
		{
			FileStream image = new FileStream(text3, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1048576, FileOptions.RandomAccess);
			byte[] superblockIcv;
			try
			{
				image.SetLength(num9);
				byte[] array7 = new byte[65536];
				const int ChunkBlocks = 128; // 8 MiB per chunk
				const int BlockSize = 65536;
				byte[] chunkBuffer = new byte[ChunkBlocks * BlockSize];

				for (int l = 0; l < files.Count; l++)
				{
					using FileStream fileStream = new FileStream(files[l].Path, FileMode.Open, FileAccess.Read, FileShare.Read, 4194304, FileOptions.SequentialScan);
					long num10 = array2[l];
					int totalFileBlocks = array4[l];
					int fileStartBlock = array3[l];
					int blocksDone = 0;
					int lastReportedPercent = -1;

					for (int blockOffset = 0; blockOffset < totalFileBlocks; blockOffset += ChunkBlocks)
					{
						cancellationToken.ThrowIfCancellationRequested();
						int blocksInThisChunk = Math.Min(ChunkBlocks, totalFileBlocks - blockOffset);
						int bytesToRead = (int)Math.Min((long)blocksInThisChunk * BlockSize, num10);

						if (bytesToRead > 0)
						{
							fileStream.ReadExactly(chunkBuffer.AsSpan(0, bytesToRead));
							num10 -= bytesToRead;
						}

						int totalChunkBytes = blocksInThisChunk * BlockSize;
						if (bytesToRead < totalChunkBytes)
						{
							chunkBuffer.AsSpan(bytesToRead, totalChunkBytes - bytesToRead).Clear();
						}

						long targetPosition = unchecked((long)(fileStartBlock + blockOffset)) * (long)BlockSize;
						image.Position = targetPosition;
						image.Write(chunkBuffer.AsSpan(0, totalChunkBytes));

						Parallel.For(0, blocksInThisChunk, new ParallelOptions
						{
							CancellationToken = cancellationToken,
							MaxDegreeOfParallelism = Math.Max(1, maxHashingThreads)
						}, i =>
						{
							int currentBlockIndex = fileStartBlock + blockOffset + i;
							ReadOnlySpan<byte> blockSpan = chunkBuffer.AsSpan(i * BlockSize, BlockSize);
							ProsperoOuterPfsSignature.ComputeBlockHash(blockSpan, imageDigests.AsSpan(currentBlockIndex * 32, 32));
						});

						if (maxHashingThreads <= 1)
						{
							Thread.Sleep(20); // Single core: 20ms cooling pause every 8MB
						}
						else if (maxHashingThreads <= 2)
						{
							Thread.Sleep(12); // Cool & quiet: 12ms cooling pause every 8MB
						}
						else if (maxHashingThreads <= 4)
						{
							Thread.Sleep(4); // Balanced: 4ms cooling pause every 8MB
						}

						blocksDone += blocksInThisChunk;
						if (totalFileBlocks > 1000)
						{
							int percent = (int)((long)blocksDone * 100 / totalFileBlocks);
							if (percent >= lastReportedPercent + 2)
							{
								lastReportedPercent = percent;
								log?.Invoke($"[stage 3/5] Hashing outer PFS: {percent}% ({blocksDone:N0}/{totalFileBlocks:N0} blocks)...");
							}
						}
					}
				}
				Array.Clear(array7);
				BuildSuperRootDirents(array7);
				WriteBlock(num5, array7);
				Array.Clear(array7);
				BuildFlatPathTable(array7, files.Count, (int index) => files[index].Name);
				WriteBlock(num6, array7);
				Array.Clear(array7);
				BuildUrootDirents(array7, files.Count, (int index) => files[index].Name);
				WriteBlock(num7, array7);
				List<DinodeS32> list = new List<DinodeS32>(3 + files.Count);
				list.Add(MakeMeta(16749, 1, 131084u, 65536L, num5));
				list.Add(MakeMeta(33133, 1, 131084u, 64 + (long)files.Count * 16L, num6));
				list.Add(MakeMeta(16749, 3, 12u, 65536L, num7));
				for (int num12 = 0; num12 < files.Count; num12++)
				{
					DinodeS32 dinodeS = new DinodeS32
					{
						Mode = (InodeMode.o_read | InodeMode.o_execute | InodeMode.g_read | InodeMode.g_execute | InodeMode.u_read | InodeMode.u_execute | InodeMode.file),
						Nlink = 1,
						Flags = (InodeFlags.compressed | InodeFlags.unk2 | InodeFlags.unk3),
						Size = array2[num12],
						SizeCompressed = (files[num12].SizeCompressed ?? array2[num12]),
						Blocks = checked((uint)array4[num12])
					};
					StampTime(dinodeS, parameters);
					int num13 = Math.Min(array4[num12], 12);
					for (int num14 = 0; num14 < num13; num14++)
					{
						int num15 = array3[num12] + num14;
						dinodeS.db[num14].sig = imageDigests.AsSpan(num15 * 32, 32).ToArray();
						dinodeS.db[num14].block = num15;
					}
					IndirectTreePlan[] array8 = array5[num12];
					foreach (IndirectTreePlan indirectTreePlan in array8)
					{
						byte[] sig = WriteIndirectTree(indirectTreePlan.Root, array3[num12], CopyStoredDigest, (int mapBlock, byte[] bytes) =>
						{
							WriteBlock(mapBlock, bytes);
						});
						dinodeS.ib[indirectTreePlan.InodeLevel].sig = sig;
						dinodeS.ib[indirectTreePlan.InodeLevel].block = indirectTreePlan.Root.BlockIndex;
					}
					list.Add(dinodeS);
				}
				Array.Clear(array7);
				using (MemoryStream s = new MemoryStream(array7, writable: true))
				{
					foreach (DinodeS32 item in list)
					{
						item.WriteToStream(s);
					}
				}
				WriteBlock(num4, array7);
				Array.Clear(array7);
				BuildSuperblock(array7, parameters, files.Count, num8, num4, imageDigests.AsSpan(num4 * 32, 32).ToArray());
				WriteBlock(num3, array7);
				superblockIcv = array7.AsSpan(896, 32).ToArray();
				image.Flush(flushToDisk: true);
			}
			finally
			{
				if (image != null)
				{
					((IDisposable)image).Dispose();
				}
			}
			if (encryptOutput)
			{
				var (array9, array10) = ProsperoPfsKeys.DeriveImageEncryptionKeys(ekpfs, array);
				using (FileStream input = new FileStream(text3, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.SequentialScan))
				{
					using FileStream fileStream2 = new FileStream(text4, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1048576, FileOptions.SequentialScan);
					ProsperoOuterPfsImage.Transform(input, fileStream2, num9, array9, array10, 65536, array6, encrypt: true);
					fileStream2.Flush(flushToDisk: true);
				}
				File.Move(text4, fullPath, overwrite: true);
			}
			else
			{
				File.Move(text3, fullPath, overwrite: true);
			}
			ProsperoPfsImageTreeInfo tree = BuildTreeInfo(num8, num4, num5, num6, num7, array3, array4, files, array2, seed, superblockIcv, parameters.ImageMode == ProsperoPublisherImageMode.Native);
			return new ProsperoOuterPackageFileResult
			{
				PfsSize = num9,
				ImageDigests = imageDigests,
				SuperblockIcv = superblockIcv,
				SuperblockIndex = num3,
				InodeTableIndex = num4,
				SuperRootDirentIndex = num5,
				FltIndex = num6,
				UrootDirentIndex = num7,
				FileFirstBlock = array3,
				FileBlockCount = array4,
				BlockKinds = array6,
				Tree = tree
			};
			void WriteBlock(int num17, ReadOnlySpan<byte> bytes)
			{
				if (bytes.Length != 65536)
				{
					throw new ArgumentException("Structural outer-PFS blocks must be exactly 64 KiB.");
				}
				checked
				{
					image.Position = unchecked((long)num17) * 65536L;
					image.Write(bytes);
					StoreDigest(num17, bytes);
				}
			}
		}
		finally
		{
			TryDeleteFile(text3);
			TryDeleteFile(text4);
		}
		void CopyStoredDigest(int dataBlock, Span<byte> destination)
		{
			imageDigests.AsSpan(dataBlock * 32, 32).CopyTo(destination);
		}
		DinodeS32 MakeMeta(ushort mode, ushort nlink, uint flags, long size, int ownedBlock)
		{
			DinodeS32 dinodeS2 = new DinodeS32
			{
				Mode = (InodeMode)mode,
				Nlink = nlink,
				Flags = (InodeFlags)flags,
				Size = size,
				SizeCompressed = size,
				Blocks = 1u
			};
			StampTime(dinodeS2, parameters);
			dinodeS2.db[0].sig = imageDigests.AsSpan(ownedBlock * 32, 32).ToArray();
			dinodeS2.db[0].block = ownedBlock;
			return dinodeS2;
		}
		void StoreDigest(int num17, ReadOnlySpan<byte> bytes)
		{
			ProsperoOuterPfsSignature.ComputeBlockHash(bytes).CopyTo(imageDigests, num17 * 32);
		}
	}

	private static ProsperoPfsImageTreeInfo BuildTreeInfo(ProsperoOuterPfsBuildResult build, IReadOnlyList<ProsperoOuterFile> files, byte[] seed, byte[] superblockIcv)
	{
		long num = 64 + (long)files.Count * 16L;
		bool compressed = true;
		ProsperoPfsImageNode prosperoPfsImageNode = new ProsperoPfsImageNode
		{
			Name = "",
			IsDirectory = true,
			Internal = false,
			InodeNumber = 0u,
			StartBlock = build.SuperRootDirentIndex,
			Blocks = 1u,
			StoredSize = 65536L,
			PlainSize = 65536L,
			Flags = 131084u,
			Mode = 16749,
			Nlink = 1
		};
		prosperoPfsImageNode.Children.Add(new ProsperoPfsImageNode
		{
			Name = "inode_flat_path_table",
			IsDirectory = false,
			Internal = true,
			InodeNumber = 1u,
			StartBlock = build.FltIndex,
			Blocks = 1u,
			StoredSize = num,
			PlainSize = num,
			Flags = 131084u,
			Mode = 33133,
			Nlink = 1
		});
		ProsperoPfsImageNode prosperoPfsImageNode2 = new ProsperoPfsImageNode
		{
			Name = "uroot",
			IsDirectory = true,
			InodeNumber = 2u,
			StartBlock = build.UrootDirentIndex,
			Blocks = 1u,
			StoredSize = 65536L,
			PlainSize = 65536L,
			Flags = 12u,
			Mode = 16749,
			Nlink = 3
		};
		for (int i = 0; i < files.Count; i++)
		{
			ProsperoOuterFile prosperoOuterFile = files[i];
			prosperoPfsImageNode2.Children.Add(new ProsperoPfsImageNode
			{
				Name = prosperoOuterFile.Name,
				IsDirectory = false,
				InodeNumber = (uint)(3 + i),
				StartBlock = build.FileFirstBlock[i],
				Blocks = (uint)build.FileBlockCount[i],
				StoredSize = prosperoOuterFile.Data.Length,
				PlainSize = (prosperoOuterFile.SizeCompressed ?? prosperoOuterFile.Data.Length),
				Flags = 13u,
				Mode = 33133,
				Nlink = 1,
				Compressed = compressed
			});
		}
		prosperoPfsImageNode.Children.Add(prosperoPfsImageNode2);
		return new ProsperoPfsImageTreeInfo
		{
			BlockSize = 65536,
			ImageBlocks = build.BlockCount,
			InodeCount = 3 + files.Count,
			DinodeBlockCount = 1,
			RootInodeNumber = 0u,
			DinodeBlock = build.InodeTableIndex,
			DinodeSize = 65536L,
			DinodeFlags = 0u,
			Seed = seed,
			SuperblockIcv = superblockIcv,
			Signed = true,
			Encrypted = true,
			Root = prosperoPfsImageNode
		};
	}

	private static ProsperoPfsImageTreeInfo BuildTreeInfo(int imageBlocks, int inodeTableIndex, int superRootDirentIndex, int fltIndex, int urootDirentIndex, int[] firstBlocks, int[] blockCounts, IReadOnlyList<ProsperoOuterFileSource> files, long[] lengths, byte[] seed, byte[] superblockIcv, bool encrypted)
	{
		long num = 64 + (long)files.Count * 16L;
		bool compressed = true;
		ProsperoPfsImageNode prosperoPfsImageNode = new ProsperoPfsImageNode
		{
			Name = "",
			IsDirectory = true,
			Internal = false,
			InodeNumber = 0u,
			StartBlock = superRootDirentIndex,
			Blocks = 1u,
			StoredSize = 65536L,
			PlainSize = 65536L,
			Flags = 131084u,
			Mode = 16749,
			Nlink = 1
		};
		prosperoPfsImageNode.Children.Add(new ProsperoPfsImageNode
		{
			Name = "inode_flat_path_table",
			IsDirectory = false,
			Internal = true,
			InodeNumber = 1u,
			StartBlock = fltIndex,
			Blocks = 1u,
			StoredSize = num,
			PlainSize = num,
			Flags = 131084u,
			Mode = 33133,
			Nlink = 1
		});
		ProsperoPfsImageNode prosperoPfsImageNode2 = new ProsperoPfsImageNode
		{
			Name = "uroot",
			IsDirectory = true,
			InodeNumber = 2u,
			StartBlock = urootDirentIndex,
			Blocks = 1u,
			StoredSize = 65536L,
			PlainSize = 65536L,
			Flags = 12u,
			Mode = 16749,
			Nlink = 3
		};
		for (int i = 0; i < files.Count; i++)
		{
			prosperoPfsImageNode2.Children.Add(new ProsperoPfsImageNode
			{
				Name = files[i].Name,
				IsDirectory = false,
				InodeNumber = (uint)(3 + i),
				StartBlock = firstBlocks[i],
				Blocks = (uint)blockCounts[i],
				StoredSize = lengths[i],
				PlainSize = (files[i].SizeCompressed ?? lengths[i]),
				Flags = 13u,
				Mode = 33133,
				Nlink = 1,
				Compressed = compressed
			});
		}
		prosperoPfsImageNode.Children.Add(prosperoPfsImageNode2);
		return new ProsperoPfsImageTreeInfo
		{
			BlockSize = 65536,
			ImageBlocks = imageBlocks,
			InodeCount = 3 + files.Count,
			DinodeBlockCount = 1,
			RootInodeNumber = 0u,
			DinodeBlock = inodeTableIndex,
			DinodeSize = 65536L,
			DinodeFlags = 0u,
			Seed = seed,
			SuperblockIcv = superblockIcv,
			Signed = true,
			Encrypted = encrypted,
			Root = prosperoPfsImageNode
		};
	}

	private static void BuildSuperRootDirents(Span<byte> block)
	{
		using MemoryStream memoryStream = new MemoryStream(65536);
		PfsDirent pfsDirent = new PfsDirent();
		pfsDirent.InodeNumber = 1u;
		pfsDirent.Type = DirentType.File;
		pfsDirent.Name = "inode_flat_path_table";
		pfsDirent.WriteToStream(memoryStream);
		PfsDirent pfsDirent2 = new PfsDirent();
		pfsDirent2.InodeNumber = 2u;
		pfsDirent2.Type = DirentType.Directory;
		pfsDirent2.Name = "uroot";
		pfsDirent2.WriteToStream(memoryStream);
		CopyStream(memoryStream, block);
	}

	private static void BuildUrootDirents(Span<byte> block, IReadOnlyList<ProsperoOuterFile> files)
	{
		BuildUrootDirents(block, files.Count, (int i) => files[i].Name);
	}

	private static void BuildUrootDirents(Span<byte> block, int fileCount, Func<int, string> getName)
	{
		using MemoryStream memoryStream = new MemoryStream(65536);
		PfsDirent pfsDirent = new PfsDirent();
		pfsDirent.InodeNumber = 2u;
		pfsDirent.Type = DirentType.Dot;
		pfsDirent.Name = ".";
		pfsDirent.WriteToStream(memoryStream);
		PfsDirent pfsDirent2 = new PfsDirent();
		pfsDirent2.InodeNumber = 2u;
		pfsDirent2.Type = DirentType.DotDot;
		pfsDirent2.Name = "..";
		pfsDirent2.WriteToStream(memoryStream);
		for (int i = 0; i < fileCount; i++)
		{
			PfsDirent pfsDirent3 = new PfsDirent();
			pfsDirent3.InodeNumber = (uint)(3 + i);
			pfsDirent3.Type = DirentType.File;
			pfsDirent3.Name = getName(i);
			pfsDirent3.WriteToStream(memoryStream);
		}
		CopyStream(memoryStream, block);
	}

	private static void BuildFlatPathTable(Span<byte> block, IReadOnlyList<ProsperoOuterFile> files)
	{
		BuildFlatPathTable(block, files.Count, (int i) => files[i].Name);
	}

	private static void BuildFlatPathTable(Span<byte> block, int fileCount, Func<int, string> getName)
	{
		BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(0), 1u);
		BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(4), 16u);
		BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(8), 64u);
		FltMagic.CopyTo(block.Slice(32));
		BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(44), (uint)fileCount);
		BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(48), 10577419142525243217uL);
		BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(56), 701355796979237965uL);
		int num = 64;
		for (int i = 0; i < fileCount; i++)
		{
			ulong value = (uint)(3 + i) | ((ulong)(uint)i << 40);
			BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(num), FltPathHash(getName(i)));
			BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(num + 8), value);
			num += 16;
		}
	}

	private static void BuildInodeTable(byte[] image, int inodeTableIndex, ProsperoOuterPfsBuildParameters p, IReadOnlyList<ProsperoOuterFile> files, int[] fileFirstBlock, int[] fileBlockCount, IndirectTreePlan[][] indirectPlans, int superRootDirentIndex, int fltIndex, int urootDirentIndex)
	{
		List<DinodeS32> list = new List<DinodeS32>(3 + files.Count);
		list.Add(MakeMetaInode(16749, 1, 131084u, 65536L, p, image, new int[1] { superRootDirentIndex }));
		long size = 64 + (long)files.Count * 16L;
		list.Add(MakeMetaInode(33133, 1, 131084u, size, p, image, new int[1] { fltIndex }));
		list.Add(MakeMetaInode(16749, 3, 12u, 65536L, p, image, new int[1] { urootDirentIndex }));
		for (int i = 0; i < files.Count; i++)
		{
			ProsperoOuterFile prosperoOuterFile = files[i];
			int num = fileFirstBlock[i];
			int num2 = fileBlockCount[i];
			DinodeS32 dinodeS = new DinodeS32
			{
				Mode = (InodeMode.o_read | InodeMode.o_execute | InodeMode.g_read | InodeMode.g_execute | InodeMode.u_read | InodeMode.u_execute | InodeMode.file),
				Nlink = 1,
				Flags = (InodeFlags.compressed | InodeFlags.unk2 | InodeFlags.unk3),
				Size = prosperoOuterFile.Data.Length,
				SizeCompressed = (prosperoOuterFile.SizeCompressed ?? prosperoOuterFile.Data.Length),
				Blocks = (uint)num2
			};
			StampTime(dinodeS, p);
			int num3 = Math.Min(num2, dinodeS.db.Length);
			for (int j = 0; j < num3; j++)
			{
				int num4 = num + j;
				dinodeS.db[j].sig = ProsperoOuterPfsSignature.ComputeBlockHash(image.AsSpan(num4 * 65536, 65536));
				dinodeS.db[j].block = num4;
			}
			IndirectTreePlan[] array = indirectPlans[i];
			foreach (IndirectTreePlan indirectTreePlan in array)
			{
				byte[] sig = WriteIndirectTree(indirectTreePlan.Root, num, CopyImageDigest, StoreMapBlock);
				dinodeS.ib[indirectTreePlan.InodeLevel].sig = sig;
				dinodeS.ib[indirectTreePlan.InodeLevel].block = indirectTreePlan.Root.BlockIndex;
			}
			list.Add(dinodeS);
		}
		using (MemoryStream memoryStream = new MemoryStream(65536))
		{
			foreach (DinodeS32 item in list)
			{
				item.WriteToStream(memoryStream);
			}
			CopyStream(memoryStream, image.AsSpan(inodeTableIndex * 65536, 65536));
		}
		void CopyImageDigest(int dataBlock, Span<byte> destination)
		{
			ProsperoOuterPfsSignature.ComputeBlockHash(image.AsSpan(dataBlock * 65536, 65536)).CopyTo(destination);
		}
		void StoreMapBlock(int mapBlock, byte[] bytes)
		{
			bytes.CopyTo(image, mapBlock * 65536);
		}
	}

	private static DinodeS32 MakeMetaInode(ushort mode, ushort nlink, uint flags, long size, ProsperoOuterPfsBuildParameters p, byte[] image, int[] ownedBlocks)
	{
		DinodeS32 dinodeS = new DinodeS32
		{
			Mode = (InodeMode)mode,
			Nlink = nlink,
			Flags = (InodeFlags)flags,
			Size = size,
			SizeCompressed = size
		};
		StampTime(dinodeS, p);
		FillSignedBlocks(dinodeS, image, ownedBlocks);
		return dinodeS;
	}

	private static void StampTime(DinodeS32 di, ProsperoOuterPfsBuildParameters p)
	{
		di.Time1_sec = (di.Time2_sec = (di.Time3_sec = (di.Time4_sec = p.TimestampSeconds)));
		di.Time1_nsec = (di.Time2_nsec = (di.Time3_nsec = (di.Time4_nsec = p.TimestampNanoseconds)));
	}

	private static void FillSignedBlocks(DinodeS32 di, byte[] image, int[] blocks)
	{
		di.Blocks = (uint)blocks.Length;
		for (int i = 0; i < blocks.Length; i++)
		{
			int num = blocks[i];
			byte[] sig = ProsperoOuterPfsSignature.ComputeBlockHash(image.AsSpan(num * 65536, 65536));
			di.db[i].sig = sig;
			di.db[i].block = num;
		}
	}

	private static void BuildSuperblock(Span<byte> block, ProsperoOuterPfsBuildParameters p, int fileCount, int totalBlocks, int inodeTableIndex, byte[] inodeTableHash)
	{
		int num = 3 + fileCount;
		DinodeS64 dinodeS = new DinodeS64
		{
			Mode = (InodeMode)0,
			Nlink = 1,
			Flags = (InodeFlags)0u,
			Size = 65536L,
			SizeCompressed = 65536L,
			Blocks = 1u
		};
		dinodeS.Time1_sec = (dinodeS.Time2_sec = (dinodeS.Time3_sec = (dinodeS.Time4_sec = p.TimestampSeconds)));
		dinodeS.Time1_nsec = (dinodeS.Time2_nsec = (dinodeS.Time3_nsec = (dinodeS.Time4_nsec = p.TimestampNanoseconds)));
		dinodeS.db[0].sig = inodeTableHash;
		dinodeS.db[0].block = inodeTableIndex;
		PfsHeader pfsHeader = new PfsHeader
		{
			Version = 2L,
			ReadOnly = 1,
			Mode = (PfsMode.Signed | PfsMode.Encrypted | PfsMode.UnknownFlagAlwaysSet),
			BlockSize = 65536u,
			NBlock = 1L,
			DinodeCount = num,
			Ndblock = totalBlocks,
			DinodeBlockCount = 1L,
			UnknownIndex = 1,
			Seed = ((p.ImageMode == ProsperoPublisherImageMode.PlaintextNoAuth) ? PlaintextNoAuthSeedMarker.ToArray() : (p.Seed ?? new byte[16])),
			InodeBlockSig = dinodeS
		};
		using MemoryStream memoryStream = new MemoryStream(65536);
		pfsHeader.WriteToStream(memoryStream);
		CopyStream(memoryStream, block);
		ProsperoOuterPfsSignature.WriteSuperblockIcv(block);
	}

	private static void ValidateImageMode(ProsperoPublisherImageMode mode, string parameterName)
	{
		if (mode != ProsperoPublisherImageMode.Native && mode != ProsperoPublisherImageMode.PlaintextNoAuth)
		{
			throw new ArgumentOutOfRangeException(parameterName, mode, "Publisher image mode must be Native or PlaintextNoAuth.");
		}
	}

	private static void ValidateImageSeed(ProsperoOuterPfsBuildParameters parameters, string parameterName)
	{
		byte[] seed = parameters.Seed;
		if (seed != null && seed.Length != 16)
		{
			throw new ArgumentException("A build seed must contain exactly 16 bytes.", parameterName);
		}
		if (parameters.ImageMode == ProsperoPublisherImageMode.Native)
		{
			byte[] seed2 = parameters.Seed;
			if (seed2 != null && seed2.AsSpan().SequenceEqual(PlaintextNoAuthSeedMarker))
			{
				throw new ArgumentException("The PPRPLAIN-NOAUTH! seed marker is reserved for PLAINTEXT_NOAUTH.", parameterName);
			}
		}
	}

	private static ulong Rotl(ulong x, int n)
	{
		return (x << n) | (x >> 64 - n);
	}

	private static ulong Rotr(ulong x, int n)
	{
		return (x >> n) | (x << 64 - n);
	}

	/// <summary>
	/// The \x7fFLT path hash: a custom 3-lane reduced-Keccak permutation over the UPPERCASED ASCII
	/// file name, with two fixed 64-bit seeds and round constant 0x8000000080008081.
	/// </summary>
	internal static ulong FltPathHash(string name)
	{
		byte[] array = new byte[name.Length];
		for (int i = 0; i < name.Length; i++)
		{
			char c = name[i];
			if (c >= 'a' && c <= 'z')
			{
				c = (char)(c - 32);
			}
			array[i] = (byte)c;
		}
		int num = array.Length;
		ulong num2 = 10577419142525243217uL;
		ulong num3 = Rotl(10577419142525243217uL, 11);
		ulong num4 = Rotl(10577419142525243217uL, 23);
		ulong num5 = 0uL;
		if (num != 0)
		{
			int num6 = num - 1 >> 3;
			int num7 = 0;
			for (int j = 0; j < num6; j++)
			{
				ulong num8 = BinaryPrimitives.ReadUInt64LittleEndian(array.AsSpan(num7, 8));
				num2 ^= num8;
				ulong num9 = Rotr(Rotl(num4 ^ num3, 5) ^ num2, 11);
				ulong num10 = Rotl(Rotl(num4 ^ num2, 17) ^ num3, 11);
				ulong num11 = Rotr(Rotl(num3 ^ num2, 1) ^ num4, 5);
				num2 = (~num10 & num11) ^ num9 ^ 0x8000000080008081uL;
				num3 = (~num11 & num9) ^ num10;
				num4 = (~num9 & num10) ^ num11;
				num7 += 8;
			}
			int length = ((num - 1) & 7) + 1;
			Span<byte> span = stackalloc byte[8];
			span.Clear();
			array.AsSpan(num7, length).CopyTo(span);
			num5 = BinaryPrimitives.ReadUInt64LittleEndian(span);
		}
		ulong num12 = num3;
		ulong num13 = num4;
		ulong num14 = num5 ^ num2 ^ 0x9BBB761A41BC44DL;
		ulong x = Rotl(num13 ^ num12, 5) ^ num14;
		ulong x2 = Rotl(num13 ^ num14, 17) ^ num12;
		ulong x3 = Rotl(num12 ^ num14, 1) ^ num13;
		return (~Rotl(x2, 11) & Rotr(x3, 5)) ^ Rotr(x, 11) ^ 0x8000000080008081uL;
	}

	private static void CopyStream(MemoryStream ms, Span<byte> dest)
	{
		if (ms.Length > dest.Length)
		{
			throw new InvalidOperationException($"Serialized {ms.Length} bytes exceeds the {dest.Length}-byte block.");
		}
		ms.GetBuffer().AsSpan(0, (int)ms.Length).CopyTo(dest);
	}

	private static void TryDeleteFile(string path)
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
