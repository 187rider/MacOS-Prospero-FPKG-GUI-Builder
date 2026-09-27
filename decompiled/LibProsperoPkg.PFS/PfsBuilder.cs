using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using LibProsperoPkg.PKG;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PFS;

/// <summary>
/// Contains the functionality to construct a PFS disk image.
/// </summary>
public class PfsBuilder
{
	private struct BlockSigInfo(long block, long offset, int size = 65536)
	{
		public long Block = block;

		public long SigOffset = offset;

		public int Size = size;
	}

	private PfsHeader hdr;

	private List<Inode> inodes;

	private List<PfsDirent> super_root_dirents;

	private Inode super_root_ino;

	private Inode fpt_ino;

	private Inode cr_ino;

	private List<FSDir> allDirs;

	private List<FSFile> allFiles;

	private List<FSNode> allNodes;

	private FlatPathTable fpt;

	private CollisionResolver colResolver;

	private PfsProperties properties;

	private int emptyBlock = 4;

	private const int xtsSectorSize = 4096;

	private Stack<BlockSigInfo> final_sigs = new Stack<BlockSigInfo>();

	private Stack<BlockSigInfo> data_sigs = new Stack<BlockSigInfo>();

	/// <summary>
	/// When set before writing an image, captures the <c>sce_sys/imagedigs.dat</c>
	/// preimage into <see cref="F:LibProsperoPkg.PFS.PfsBuilder.ImageDigests" />: one per-block descriptor digest for every
	/// block of the plaintext signed image, stored from last byte to first. The PS5 image builder
	/// gathers the signer's per-block HMAC-SHA256 descriptor digests and writes each digest from
	/// byte 31 down to byte 0; reproduced here from this image's own signing key. Populated only for a signed image.
	/// </summary>
	public bool CaptureImageDigests;

	/// <summary>
	/// The captured <c>imagedigs.dat</c> body (N * 32 bytes for N image blocks), or <c>null</c> until
	/// a signed image is written with <see cref="F:LibProsperoPkg.PFS.PfsBuilder.CaptureImageDigests" /> set. See that property.
	/// </summary>
	public byte[] ImageDigests;

	/// <summary>
	/// When set before writing an image, captures this image's superblock integrity
	/// value into <see cref="F:LibProsperoPkg.PFS.PfsBuilder.SuperblockIcv" />: the 32-byte HMAC-SHA256 self-signature of the
	/// superblock (final signature block 0 @ offset 0x380), computed from this image's own signing
	/// key during signing (before XTS encryption). Used for the supplemental <c>pfsimage.xml</c>
	/// <c>&lt;icv&gt;</c> element. Populated only for a signed image.
	/// </summary>
	public bool CaptureSuperblockIcv;

	/// <summary>
	/// The captured 32-byte superblock ICV (see <see cref="F:LibProsperoPkg.PFS.PfsBuilder.CaptureSuperblockIcv" />), or <c>null</c>
	/// until a signed image is written with that flag set.
	/// </summary>
	public byte[] SuperblockIcv;

	private Action<string> logger;

	private static int CeilDiv(int a, int b)
	{
		return a / b + ((a % b != 0) ? 1 : 0);
	}

	private static long CeilDiv(long a, long b)
	{
		return a / b + ((a % b != 0) ? 1 : 0);
	}

	private void Log(string s)
	{
		logger?.Invoke(s);
	}

	/// <summary>
	/// Constructs a PfsBuilder with the given properties and logger.
	/// </summary>
	/// <param name="p">Properties for the image to be built</param>
	/// <param name="logger">Function that is called to report realtime PFS build status.</param>
	public PfsBuilder(PfsProperties p, Action<string> logger = null)
	{
		this.logger = logger;
		properties = p;
		Setup();
	}

	/// <summary>
	/// Computes the final size of this image as it will be written to disk.
	/// </summary>
	/// <returns>PFS Image size</returns>
	public long CalculatePfsSize()
	{
		return checked(hdr.Ndblock * hdr.BlockSize);
	}

	/// <summary>
	/// Captures a self-consistent snapshot of this image's inode tree and geometry AFTER
	/// <see cref="M:LibProsperoPkg.PFS.PfsBuilder.WriteImage(System.IO.Stream)" /> has assigned every inode's block layout (and, for a signed
	/// image with <see cref="F:LibProsperoPkg.PFS.PfsBuilder.CaptureSuperblockIcv" /> set, computed the superblock ICV). The snapshot
	/// drives the supplemental <c>pfsimage.xml</c> sections (<c>&lt;pfs-image&gt;</c> /
	/// <c>&lt;nested-image&gt;</c>), which describe the exact bytes this builder produced.
	/// </summary>
	/// <returns>A snapshot of the built image's super-root tree and superblock geometry.</returns>
	public ProsperoPfsImageTreeInfo CaptureImageTree()
	{
		if (properties.DirectRootLayout)
		{
			ProsperoPfsImageNode root = ImageNodeFromDir(properties.root);
			return new ProsperoPfsImageTreeInfo
			{
				BlockSize = (int)hdr.BlockSize,
				ImageBlocks = hdr.Ndblock,
				InodeCount = inodes.Count,
				DinodeBlockCount = (int)hdr.DinodeBlockCount,
				RootInodeNumber = properties.root.ino.Number,
				DinodeBlock = hdr.InodeBlockSig.StartBlock,
				DinodeSize = hdr.InodeBlockSig.Size,
				DinodeFlags = (uint)hdr.InodeBlockSig.Flags,
				Seed = hdr.Seed,
				SuperblockIcv = SuperblockIcv,
				Signed = false,
				Encrypted = false,
				Root = root
			};
		}
		ProsperoPfsImageNode prosperoPfsImageNode = ImageNodeFromInode(super_root_ino, "", isDir: true, isInternal: false);
		prosperoPfsImageNode.Children.Add(ImageNodeFromInode(fpt_ino, "inode_flat_path_table", isDir: false, isInternal: true));
		if (cr_ino != null)
		{
			prosperoPfsImageNode.Children.Add(ImageNodeFromInode(cr_ino, "collision_resolver", isDir: false, isInternal: true));
		}
		prosperoPfsImageNode.Children.Add(ImageNodeFromDir(properties.root));
		return new ProsperoPfsImageTreeInfo
		{
			BlockSize = (int)hdr.BlockSize,
			ImageBlocks = hdr.Ndblock,
			InodeCount = inodes.Count,
			DinodeBlockCount = (int)hdr.DinodeBlockCount,
			RootInodeNumber = super_root_ino.Number,
			DinodeBlock = hdr.InodeBlockSig.StartBlock,
			DinodeSize = hdr.InodeBlockSig.Size,
			DinodeFlags = (uint)hdr.InodeBlockSig.Flags,
			Seed = hdr.Seed,
			SuperblockIcv = SuperblockIcv,
			Signed = hdr.Mode.HasFlag(PfsMode.Signed),
			Encrypted = hdr.Mode.HasFlag(PfsMode.Encrypted),
			Root = prosperoPfsImageNode
		};
	}

	private static ProsperoPfsImageNode ImageNodeFromInode(Inode ino, string name, bool isDir, bool isInternal)
	{
		return new ProsperoPfsImageNode
		{
			Name = name,
			IsDirectory = isDir,
			Internal = isInternal,
			InodeNumber = ino.Number,
			StoredSize = ino.Size,
			PlainSize = ((ino.SizeCompressed == 0L) ? ino.Size : ino.SizeCompressed),
			Flags = (uint)ino.Flags,
			Mode = (ushort)ino.Mode,
			Nlink = ino.Nlink,
			StartBlock = ino.StartBlock,
			Blocks = ino.Blocks,
			Compressed = ((ino.Flags & InodeFlags.compressed) != 0)
		};
	}

	private static ProsperoPfsImageNode ImageNodeFromDir(FSDir dir)
	{
		ProsperoPfsImageNode prosperoPfsImageNode = ImageNodeFromFsNode(dir, isDir: true);
		foreach (FSDir item in dir.Dirs.OrderBy((FSDir d) => d.name, StringComparer.Ordinal))
		{
			prosperoPfsImageNode.Children.Add(ImageNodeFromDir(item));
		}
		foreach (FSFile item2 in dir.Files.Where((FSFile f) => f.ino != null).OrderBy((FSFile f) => f.name, StringComparer.Ordinal))
		{
			prosperoPfsImageNode.Children.Add(ImageNodeFromFsNode(item2, isDir: false));
		}
		return prosperoPfsImageNode;
	}

	private static ProsperoPfsImageNode ImageNodeFromFsNode(FSNode n, bool isDir)
	{
		Inode ino = n.ino;
		return new ProsperoPfsImageNode
		{
			Name = (n.name ?? ""),
			IsDirectory = isDir,
			InodeNumber = (ino?.Number ?? 0),
			StoredSize = (ino?.Size ?? n.Size),
			PlainSize = ((ino == null) ? n.CompressedSize : ((ino.SizeCompressed == 0L) ? ino.Size : ino.SizeCompressed)),
			Flags = (uint)(ino?.Flags ?? ((InodeFlags)0u)),
			Mode = (ushort)(ino?.Mode ?? ((InodeMode)0)),
			Nlink = (ino?.Nlink ?? 0),
			StartBlock = (ino?.StartBlock ?? 0),
			Blocks = (ino?.Blocks ?? 0),
			Compressed = (ino != null && (ino.Flags & InodeFlags.compressed) != 0)
		};
	}

	/// <summary>
	/// This gets called by the constructor.
	/// </summary>
	private void Setup()
	{
		final_sigs.Push(new BlockSigInfo(0L, 896L, 1440));
		hdr = new PfsHeader
		{
			Version = properties.Version,
			BlockSize = properties.BlockSize,
			ReadOnly = 1,
			Mode = (PfsMode)((properties.Sign ? 1 : 0) | (properties.Encrypt ? 4 : 0) | 8),
			UnknownIndex = 1,
			Seed = ((properties.Encrypt || properties.Sign) ? properties.Seed : null)
		};
		inodes = new List<Inode>();
		Log("Setting up filesystem structure...");
		allDirs = properties.root.GetAllChildrenDirs();
		allFiles = properties.root.GetAllChildrenFiles().Where((FSFile f) =>
		{
			if (!properties.FilterOuterPackageEntries)
			{
				return true;
			}
			bool flag = false;
			string text = f.name;
			FSDir parent = f.Parent;
			while (parent != null && parent != properties.root)
			{
				if (parent.Parent == properties.root && parent.name == "sce_sys")
				{
					flag = true;
					break;
				}
				text = parent.name + "/" + text;
				parent = parent.Parent;
			}
			bool flag2 = text.Equals("param.json", StringComparison.Ordinal) || EntryNames.NameToId.ContainsKey(text);
			return !flag || !flag2;
		}).ToList();
		if (properties.OptimizeFileLayoutForReadSpeed)
		{
			allFiles = allFiles.OrderBy((FSFile file) => file.LayoutPriority).ThenBy((FSFile file) => file.FullPath(), StringComparer.Ordinal).ToList();
		}
		allNodes = new List<FSNode>(allDirs.OrderBy((FSDir d) => d.FullPath()).ToList());
		allNodes.AddRange(allFiles);
		if (properties.DirectRootLayout)
		{
			SetupDirectRootStructure();
		}
		else
		{
			SetupRootStructure(FlatPathTable.HasCollision(allNodes));
		}
		Log($"Creating inodes ({allDirs.Count} dirs and {allFiles.Count} files)...");
		addDirInodes();
		addFileInodes();
		if (!properties.DirectRootLayout)
		{
			(fpt, colResolver) = FlatPathTable.Create(allNodes);
		}
		Log("Calculating data block layout...");
		allNodes.Insert(0, properties.root);
		CalculateDataBlockLayout();
	}

	private void WriteData(Stream stream)
	{
		Log("Writing data...");
		hdr.WriteToStream(stream);
		if (properties.DirectRootLayout)
		{
			WriteDirectRootInodeBitmap(stream);
		}
		WriteInodes(stream);
		checked
		{
			if (!properties.DirectRootLayout)
			{
				WriteSuperrootDirents(stream);
				stream.Position = fpt_ino.StartBlock * hdr.BlockSize;
				fpt.WriteToStream(stream);
				if (colResolver != null)
				{
					stream.Position = cr_ino.StartBlock * hdr.BlockSize;
					colResolver.WriteToStream(stream);
				}
			}
		}
		for (int i = 0; i < allNodes.Count; i++)
		{
			FSNode fSNode = allNodes[i];
			stream.Position = fSNode.ino.StartBlock * hdr.BlockSize;
			WriteFSNode(stream, fSNode);
		}
		if (properties.DirectRootLayout)
		{
			WriteDirectRootIndirectBlocks(stream);
		}
	}

	/// <summary>
	/// Enumerates the sectors that should be encrypted with AES-XTS
	/// </summary>
	/// <returns>Sector indices</returns>
	private IEnumerable<long> XtsSectorGen()
	{
		long totalSectors = (CalculatePfsSize() + 4095) / 4096;
		for (long xtsSector = 16L; xtsSector < totalSectors; xtsSector++)
		{
			if (xtsSector / 16 == emptyBlock)
			{
				xtsSector += 16;
			}
			yield return xtsSector;
		}
	}

	/// <summary>
	/// Writes the PFS image using a memory mapped file. This allows for parallelization of signing and encrypting.
	/// </summary>
	/// <param name="file">The memory mapped file</param>
	/// <param name="offset">Start offset of the PFS image in the file</param>
	public void WriteImage(MemoryMappedFile file, long offset)
	{
		using (MemoryMappedViewStream stream = file.CreateViewStream(offset, CalculatePfsSize(), MemoryMappedFileAccess.ReadWrite))
		{
			WriteData(stream);
		}
		MemoryMappedViewAccessor view = file.CreateViewAccessor(offset, CalculatePfsSize(), MemoryMappedFileAccess.ReadWrite);
		try
		{
			if (hdr.Mode.HasFlag(PfsMode.Signed))
			{
				Log("Signing in parallel...");
				byte[] signKey = Crypto.PfsGenSignKey(properties.EKPFS, hdr.Seed);
				Parallel.ForEach((IEnumerable<BlockSigInfo>)data_sigs, (Func<Tuple<byte[], HMACSHA256>>)(() => Tuple.Create(new byte[properties.BlockSize], new HMACSHA256(signKey))), (Func<BlockSigInfo, ParallelLoopState, Tuple<byte[], HMACSHA256>, Tuple<byte[], HMACSHA256>>)((BlockSigInfo sig, ParallelLoopState status, Tuple<byte[], HMACSHA256> local) =>
				{
					var (array6, hMACSHA3) = local;
					long position2 = sig.Block * array6.Length;
					view.ReadArray(position2, array6, 0, array6.Length);
					position2 = sig.SigOffset;
					byte[] array7 = hMACSHA3.ComputeHash(array6);
					view.WriteArray(position2, array7, 0, array7.Length);
					view.Write(position2 + 32, (int)sig.Block);
					return local;
				}), (Action<Tuple<byte[], HMACSHA256>>)((Tuple<byte[], HMACSHA256> local) =>
				{
					local.Item2.Dispose();
				}));
				using HMACSHA256 hMACSHA = new HMACSHA256(signKey);
				foreach (BlockSigInfo final_sig in final_sigs)
				{
					byte[] array = new byte[final_sig.Size];
					long position = final_sig.Block * properties.BlockSize;
					view.ReadArray(position, array, 0, array.Length);
					position = final_sig.SigOffset;
					byte[] array2 = hMACSHA.ComputeHash(array);
					view.WriteArray(position, array2, 0, array2.Length);
					view.Write(position + 32, (int)final_sig.Block);
					if (CaptureSuperblockIcv && final_sig.Block == 0L && final_sig.SigOffset == 896)
					{
						SuperblockIcv = array2;
					}
				}
			}
			if (CaptureImageDigests && hdr.Mode.HasFlag(PfsMode.Signed))
			{
				Log("Capturing image digests in parallel...");
				byte[] idKey = Crypto.PfsGenSignKey(properties.EKPFS, hdr.Seed);
				int blockSize;
				int num;
				byte[] digests;
				checked
				{
					blockSize = (int)properties.BlockSize;
					num = (int)unchecked(CalculatePfsSize() / blockSize);
					digests = new byte[num * 32];
				}
				Parallel.For(0, num, () => Tuple.Create(new byte[blockSize], new HMACSHA256(idKey)), (int blockIndex, ParallelLoopState _, Tuple<byte[], HMACSHA256> local) =>
				{
					var (array6, hMACSHA3) = local;
					view.ReadArray((long)blockIndex * (long)blockSize, array6, 0, blockSize);
					byte[] array7 = hMACSHA3.ComputeHash(array6);
					Array.Reverse(array7);
					Buffer.BlockCopy(array7, 0, digests, blockIndex * 32, 32);
					return local;
				}, (Tuple<byte[], HMACSHA256> local) =>
				{
					local.Item2.Dispose();
				});
				ImageDigests = digests;
			}
			if (hdr.Mode.HasFlag(PfsMode.Encrypted))
			{
				Log("Encrypting in parallel...");
				var (tweakKey, dataKey) = Crypto.PfsGenEncKey(properties.EKPFS, hdr.Seed);
				Parallel.ForEach(XtsSectorGen(), () => Tuple.Create(new XtsBlockTransform(dataKey, tweakKey), new byte[4096]), (long xtsSector, ParallelLoopState loopState, Tuple<XtsBlockTransform, byte[]> localData) =>
				{
					var (xtsBlockTransform2, array6) = localData;
					long position2 = xtsSector * 4096;
					view.ReadArray(position2, array6, 0, 4096);
					xtsBlockTransform2.EncryptSector(array6, (ulong)xtsSector);
					view.WriteArray(position2, array6, 0, 4096);
					return localData;
				}, (Tuple<XtsBlockTransform, byte[]> local) =>
				{
					local.Item1.Dispose();
				});
			}
		}
		finally
		{
			if (view != null)
			{
				((IDisposable)view).Dispose();
			}
		}
	}

	/// <summary>
	/// Writes the PFS image to the given stream
	/// </summary>
	public void WriteImage(Stream stream)
	{
		WriteData(stream);
		if (hdr.Mode.HasFlag(PfsMode.Signed))
		{
			Log("Signing...");
			using HMACSHA256 hMACSHA = new HMACSHA256(Crypto.PfsGenSignKey(properties.EKPFS, hdr.Seed));
			foreach (BlockSigInfo item in data_sigs.Concat(final_sigs))
			{
				byte[] buffer = new byte[item.Size];
				stream.Position = item.Block * properties.BlockSize;
				stream.ReadExactly(buffer, 0, item.Size);
				stream.Position = item.SigOffset;
				byte[] array = hMACSHA.ComputeHash(buffer);
				stream.Write(array, 0, 32);
				stream.WriteLE((int)item.Block);
				if (CaptureSuperblockIcv && item.Block == 0L && item.SigOffset == 896)
				{
					SuperblockIcv = array;
				}
			}
		}
		if (CaptureImageDigests && hdr.Mode.HasFlag(PfsMode.Signed))
		{
			byte[] key = Crypto.PfsGenSignKey(properties.EKPFS, hdr.Seed);
			int blockSize = (int)properties.BlockSize;
			int num = (int)(CalculatePfsSize() / blockSize);
			byte[] array2 = new byte[num * 32];
			byte[] array3 = new byte[blockSize];
			using HMACSHA256 hMACSHA2 = new HMACSHA256(key);
			for (int i = 0; i < num; i++)
			{
				stream.Position = (long)i * (long)blockSize;
				stream.ReadExactly(array3);
				byte[] array4 = hMACSHA2.ComputeHash(array3);
				Array.Reverse(array4);
				array4.CopyTo(array2, i * 32);
			}
			ImageDigests = array2;
		}
		if (!hdr.Mode.HasFlag(PfsMode.Encrypted))
		{
			return;
		}
		Log("Encrypting...");
		var (tweakKey, dataKey) = Crypto.PfsGenEncKey(properties.EKPFS, hdr.Seed);
		using XtsBlockTransform xtsBlockTransform = new XtsBlockTransform(dataKey, tweakKey);
		byte[] array7 = new byte[4096];
		foreach (long item2 in XtsSectorGen())
		{
			stream.Position = item2 * 4096;
			stream.ReadExactly(array7, 0, 4096);
			xtsBlockTransform.EncryptSector(array7, (ulong)item2);
			stream.Position = item2 * 4096;
			stream.Write(array7, 0, 4096);
		}
	}

	/// <summary>
	/// Adds inodes for each dir.
	/// </summary>
	private void addDirInodes()
	{
		inodes.Add(properties.root.ino);
		foreach (FSDir item2 in allDirs.OrderBy((FSDir x) => x.FullPath()))
		{
			Inode inode = (item2.ino = MakeInode((InodeMode)(0x4000 | (properties.DirectRootLayout ? 493 : 365)), 1u, 65536L, 0L, 2, (uint)inodes.Count, (!properties.DirectRootLayout) ? InodeFlags.@readonly : ((InodeFlags)0u)));
			item2.Dirents.Add(new PfsDirent
			{
				Name = ".",
				InodeNumber = inode.Number,
				Type = DirentType.Dot
			});
			item2.Dirents.Add(new PfsDirent
			{
				Name = "..",
				InodeNumber = item2.Parent.ino.Number,
				Type = DirentType.DotDot
			});
			PfsDirent item = new PfsDirent
			{
				Name = item2.name,
				InodeNumber = (uint)inodes.Count,
				Type = DirentType.Directory
			};
			item2.Parent.Dirents.Add(item);
			item2.Parent.ino.Nlink++;
			inodes.Add(inode);
		}
	}

	/// <summary>
	/// Adds inodes for each file.
	/// </summary>
	private void addFileInodes()
	{
		foreach (FSFile item2 in allFiles.OrderBy((FSFile x) => x.FullPath()))
		{
			Inode inode = MakeInode((InodeMode)(0x8000 | (properties.DirectRootLayout ? 420 : 365)), Size: item2.PprKrakenCompression ? item2.CompressedSize : item2.Size, SizeCompressed: item2.CompressedSize, Number: (uint)inodes.Count, Blocks: checked((uint)CeilDiv(item2.Size, hdr.BlockSize)), Nlink: 1, Flags: (InodeFlags)(((!properties.DirectRootLayout) ? 16 : 0) | (item2.Compress ? 1 : 0)));
			if (properties.Sign)
			{
				inode.Flags &= ~InodeFlags.@readonly;
			}
			item2.ino = inode;
			PfsDirent item = new PfsDirent
			{
				Name = item2.name,
				Type = DirentType.File,
				InodeNumber = (uint)inodes.Count
			};
			item2.Parent.Dirents.Add(item);
			inodes.Add(inode);
		}
	}

	private long roundUpSizeToBlock(long size)
	{
		return CeilDiv(size, hdr.BlockSize) * hdr.BlockSize;
	}

	private long GetNodeStorageSize(FSNode node)
	{
		if (!(node is FSDir directory))
		{
			return node.Size;
		}
		return CalculateDirectoryStorageSize(directory);
	}

	private long CalculateDirectoryStorageSize(FSDir directory)
	{
		long num = 0L;
		foreach (PfsDirent dirent in directory.Dirents)
		{
			if (dirent.EntSize <= 0 || dirent.EntSize > hdr.BlockSize)
			{
				throw new InvalidDataException($"Directory entry '{dirent.Name}' has invalid size {dirent.EntSize} for block size 0x{hdr.BlockSize:X}.");
			}
			long num2 = num % hdr.BlockSize;
			long num3 = hdr.BlockSize - num2;
			if (dirent.EntSize > num3)
			{
				num += num3;
			}
			num += dirent.EntSize;
		}
		return num;
	}

	private long calculateIndirectBlocks(long size)
	{
		uint num = hdr.BlockSize / 36;
		long num2 = CeilDiv(size, hdr.BlockSize);
		long num3 = 0L;
		if (num2 > 12)
		{
			num2 -= 12;
			num3++;
		}
		if (num2 > num)
		{
			num2 -= num;
			num3 += 1 + CeilDiv(num2, num);
		}
		return num3;
	}

	/// <summary>
	/// Given an inode number and an index into the db[] array, returns the absolute offset of that array value.
	/// The inode table has tail padding in every filesystem block, so the inode's block boundary
	/// must be applied rather than treating all inode records as one contiguous run.
	/// </summary>
	private long inoNumberToOffset(uint number, int db = 0)
	{
		long num = (long)hdr.BlockSize / 712L;
		long num2 = 1 + number / num;
		long num3 = number % num * 712;
		return num2 * hdr.BlockSize + num3 + 100 + 36L * (long)db;
	}

	/// <summary>
	/// Sets the data blocks. Also updates header for total number of data blocks.
	/// </summary>
	private void CalculateDataBlockLayout()
	{
		if (properties.DirectRootLayout)
		{
			if (properties.Sign || properties.Encrypt)
			{
				throw new NotSupportedException("The publisher direct-root layout currently supports plaintext unsigned images only.");
			}
			long b = (long)hdr.BlockSize / 168L;
			hdr.DinodeCount = inodes.Count;
			hdr.DinodeBlockCount = CeilDiv(inodes.Count, b);
			hdr.InodeBlockSig.Blocks = checked((uint)hdr.DinodeBlockCount);
			hdr.InodeBlockSig.Size = hdr.DinodeBlockCount * hdr.BlockSize;
			hdr.InodeBlockSig.SizeCompressed = hdr.InodeBlockSig.Size;
			hdr.InodeBlockSig.SetTime(properties.FileTime);
			hdr.InodeBlockSig.SetDirectBlock(0, 2);
			for (int i = 1; i < hdr.DinodeBlockCount && i < 12; i++)
			{
				hdr.InodeBlockSig.SetDirectBlock(i, 2 + i);
			}
			hdr.Ndblock = 2 + hdr.DinodeBlockCount;
			foreach (FSNode allNode in allNodes)
			{
				long nodeStorageSize = GetNodeStorageSize(allNode);
				long num = CeilDiv(nodeStorageSize, hdr.BlockSize);
				allNode.ino.SetDirectBlock(0, (int)hdr.Ndblock);
				for (int j = 1; j < num && j < 12; j++)
				{
					allNode.ino.SetDirectBlock(j, checked((int)hdr.Ndblock + j));
				}
				allNode.ino.Blocks = checked((uint)num);
				ref long size = ref allNode.ino.Size;
				long num2;
				if (allNode is FSDir)
				{
					num2 = nodeStorageSize;
				}
				else
				{
					num2 = ((allNode is FSFile { PprKrakenCompression: not false } fSFile) ? fSFile.CompressedSize : allNode.Size);
				}
				size = num2;
				if (allNode is FSDir)
				{
					allNode.ino.SizeCompressed = nodeStorageSize;
				}
				else if (allNode.ino.SizeCompressed == 0L)
				{
					allNode.ino.SizeCompressed = allNode.ino.Size;
				}
				hdr.Ndblock += num;
				long num3 = hdr.BlockSize / 4;
				if (num > 12)
				{
					allNode.ino.IndirectBlocks[0] = checked((int)hdr.Ndblock++);
					long num4 = num - 12 - num3;
					if (num4 > 0)
					{
						allNode.ino.IndirectBlocks[1] = checked((int)hdr.Ndblock++);
						hdr.Ndblock += CeilDiv(num4, num3);
					}
				}
			}
			hdr.Ndblock = Math.Max(hdr.Ndblock, properties.MinBlocks);
			return;
		}
		if (properties.Sign)
		{
			hdr.Ndblock = 1L;
			long b2 = (long)hdr.BlockSize / 712L;
			hdr.DinodeCount = inodes.Count;
			hdr.DinodeBlockCount = CeilDiv(inodes.Count, b2);
			hdr.InodeBlockSig.Blocks = checked((uint)hdr.DinodeBlockCount);
			hdr.InodeBlockSig.Size = hdr.DinodeBlockCount * hdr.BlockSize;
			hdr.InodeBlockSig.SizeCompressed = hdr.DinodeBlockCount * hdr.BlockSize;
			hdr.InodeBlockSig.SetTime(properties.FileTime);
			hdr.InodeBlockSig.Flags = (InodeFlags)0u;
			for (int k = 0; k < hdr.DinodeBlockCount; k++)
			{
				hdr.InodeBlockSig.SetDirectBlock(k, 1 + k);
				final_sigs.Push(new BlockSigInfo(1 + k, 184 + 36 * k));
			}
			hdr.Ndblock += hdr.DinodeBlockCount;
			super_root_ino.SetDirectBlock(0, (int)(hdr.DinodeBlockCount + 1));
			final_sigs.Push(new BlockSigInfo(super_root_ino.StartBlock, inoNumberToOffset(super_root_ino.Number)));
			hdr.Ndblock += super_root_ino.Blocks;
			fpt_ino.SetDirectBlock(0, super_root_ino.StartBlock + 1);
			fpt_ino.Size = fpt.Size;
			fpt_ino.SizeCompressed = fpt.Size;
			fpt_ino.Blocks = checked((uint)CeilDiv(fpt.Size, hdr.BlockSize));
			final_sigs.Push(new BlockSigInfo(fpt_ino.StartBlock, inoNumberToOffset(fpt_ino.Number)));
			for (int l = 1; l < fpt_ino.Blocks && l < 12; l++)
			{
				fpt_ino.SetDirectBlock(l, (int)hdr.Ndblock++);
				final_sigs.Push(new BlockSigInfo(fpt_ino.StartBlock, inoNumberToOffset(fpt_ino.Number, l)));
			}
			hdr.Ndblock++;
			emptyBlock = (int)hdr.Ndblock;
			hdr.Ndblock++;
			long num5 = hdr.Ndblock;
			hdr.Ndblock += allNodes.Select((FSNode s) => calculateIndirectBlocks(GetNodeStorageSize(s))).Sum();
			uint num6 = hdr.BlockSize / 36;
			foreach (FSNode allNode2 in allNodes)
			{
				long nodeStorageSize2 = GetNodeStorageSize(allNode2);
				long num7 = CeilDiv(nodeStorageSize2, hdr.BlockSize);
				allNode2.ino.SetDirectBlock(0, (int)hdr.Ndblock);
				allNode2.ino.Blocks = checked((uint)num7);
				ref long size2 = ref allNode2.ino.Size;
				long num8;
				if (allNode2 is FSDir)
				{
					num8 = roundUpSizeToBlock(nodeStorageSize2);
				}
				else
				{
					num8 = ((allNode2 is FSFile { PprKrakenCompression: not false } fSFile2) ? fSFile2.CompressedSize : allNode2.Size);
				}
				size2 = num8;
				if (allNode2.ino.SizeCompressed == 0L)
				{
					allNode2.ino.SizeCompressed = allNode2.ino.Size;
				}
				for (int num9 = 0; num7 - num9 > 0 && num9 < 12; num9++)
				{
					data_sigs.Push(new BlockSigInfo((int)hdr.Ndblock++, inoNumberToOffset(allNode2.ino.Number, num9)));
				}
				if (num7 > 12)
				{
					final_sigs.Push(new BlockSigInfo(num5, inoNumberToOffset(allNode2.ino.Number, 12)));
					int num10 = 12;
					int num11 = 0;
					while (num7 - num10 > 0 && num10 < 12 + num6)
					{
						data_sigs.Push(new BlockSigInfo((int)hdr.Ndblock++, num5 * hdr.BlockSize + num11));
						num10++;
						num11 += 36;
					}
					num5++;
				}
				if (num7 <= 12 + num6)
				{
					continue;
				}
				uint num12 = 12 + num6;
				final_sigs.Push(new BlockSigInfo(num5, inoNumberToOffset(allNode2.ino.Number, 13)));
				long num13 = num5;
				for (int num14 = 0; num14 < num6; num14++)
				{
					if (num12 >= num7)
					{
						break;
					}
					final_sigs.Push(new BlockSigInfo((int)(++num5), num13 * hdr.BlockSize + num14 * 36));
					int num15 = 0;
					while (num15 < num6 && num12 < num7)
					{
						data_sigs.Push(new BlockSigInfo((int)hdr.Ndblock++, num5 * hdr.BlockSize + num15 * 36));
						num15++;
						num12++;
					}
				}
			}
		}
		else
		{
			hdr.Ndblock = 1L;
			long b3 = (long)hdr.BlockSize / 168L;
			hdr.DinodeCount = inodes.Count;
			hdr.DinodeBlockCount = CeilDiv(inodes.Count, b3);
			hdr.InodeBlockSig.Blocks = checked((uint)hdr.DinodeBlockCount);
			hdr.InodeBlockSig.Size = hdr.DinodeBlockCount * hdr.BlockSize;
			hdr.InodeBlockSig.SizeCompressed = hdr.DinodeBlockCount * hdr.BlockSize;
			hdr.InodeBlockSig.SetDirectBlock(0, (int)hdr.Ndblock++);
			hdr.InodeBlockSig.SetTime(properties.FileTime);
			for (int num16 = 1; num16 < hdr.DinodeBlockCount; num16++)
			{
				if (num16 < 12)
				{
					hdr.InodeBlockSig.SetDirectBlock(num16, -1);
				}
				hdr.Ndblock++;
			}
			super_root_ino.SetDirectBlock(0, (int)hdr.Ndblock);
			hdr.Ndblock += super_root_ino.Blocks;
			fpt_ino.SetDirectBlock(0, (int)hdr.Ndblock++);
			fpt_ino.Size = fpt.Size;
			fpt_ino.SizeCompressed = fpt.Size;
			fpt_ino.Blocks = checked((uint)CeilDiv(fpt.Size, hdr.BlockSize));
			for (int num17 = 1; num17 < fpt_ino.Blocks && num17 < 12; num17++)
			{
				fpt_ino.SetDirectBlock(num17, (int)hdr.Ndblock++);
			}
			if (cr_ino == null)
			{
				hdr.Ndblock++;
			}
			else
			{
				cr_ino.SetDirectBlock(0, (int)hdr.Ndblock++);
				cr_ino.Size = colResolver.Size;
				cr_ino.SizeCompressed = colResolver.Size;
				cr_ino.Blocks = checked((uint)CeilDiv(colResolver.Size, hdr.BlockSize));
				for (int num18 = 1; num18 < cr_ino.Blocks && num18 < 12; num18++)
				{
					cr_ino.SetDirectBlock(num18, (int)hdr.Ndblock++);
				}
			}
			foreach (FSNode allNode3 in allNodes)
			{
				long nodeStorageSize3 = GetNodeStorageSize(allNode3);
				long num19 = CeilDiv(nodeStorageSize3, hdr.BlockSize);
				allNode3.ino.SetDirectBlock(0, (int)hdr.Ndblock);
				allNode3.ino.Blocks = checked((uint)num19);
				ref long size3 = ref allNode3.ino.Size;
				long num20;
				if (allNode3 is FSDir)
				{
					num20 = roundUpSizeToBlock(nodeStorageSize3);
				}
				else
				{
					num20 = ((allNode3 is FSFile { PprKrakenCompression: not false } fSFile3) ? fSFile3.CompressedSize : allNode3.Size);
				}
				size3 = num20;
				if (allNode3.ino.SizeCompressed == 0L)
				{
					allNode3.ino.SizeCompressed = allNode3.ino.Size;
				}
				for (int num21 = 1; num21 < num19 && num21 < 12; num21++)
				{
					allNode3.ino.SetDirectBlock(num21, -1);
				}
				hdr.Ndblock += num19;
			}
		}
		hdr.Ndblock = Math.Max(hdr.Ndblock, properties.MinBlocks);
	}

	private Inode MakeInode(InodeMode Mode, uint Blocks, long Size = 0L, long SizeCompressed = 0L, ushort Nlink = 1, uint Number = 0u, InodeFlags Flags = (InodeFlags)0u)
	{
		Inode inode = ((!properties.Sign) ? ((Inode)new DinodeD32
		{
			Mode = Mode,
			Blocks = Blocks,
			Size = Size,
			SizeCompressed = SizeCompressed,
			Nlink = Nlink,
			Number = Number,
			Flags = Flags
		}) : ((Inode)new DinodeS32
		{
			Mode = Mode,
			Blocks = Blocks,
			Size = Size,
			SizeCompressed = SizeCompressed,
			Nlink = Nlink,
			Number = Number,
			Flags = (Flags | InodeFlags.unk2 | InodeFlags.unk3)
		}));
		inode.SetTime(properties.FileTime);
		return inode;
	}

	/// <summary>
	/// Creates inodes and dirents for superroot, flat_path_table, and uroot.
	/// Also, creates the root node for the FS tree.
	/// </summary>
	private void SetupRootStructure(bool hasCollision)
	{
		uint num = 0u;
		inodes.Add(super_root_ino = MakeInode(InodeMode.o_read | InodeMode.o_execute | InodeMode.g_read | InodeMode.g_execute | InodeMode.u_read | InodeMode.u_execute | InodeMode.dir, 1u, 65536L, 65536L, 1, num++, InodeFlags.@readonly | InodeFlags.@internal));
		inodes.Add(fpt_ino = MakeInode(InodeMode.o_read | InodeMode.o_execute | InodeMode.g_read | InodeMode.g_execute | InodeMode.u_read | InodeMode.u_execute | InodeMode.file, 1u, 0L, 0L, 1, num++, InodeFlags.@readonly | InodeFlags.@internal));
		if (hasCollision)
		{
			inodes.Add(cr_ino = MakeInode(InodeMode.o_read | InodeMode.o_execute | InodeMode.g_read | InodeMode.g_execute | InodeMode.u_read | InodeMode.u_execute | InodeMode.file, 1u, 0L, 0L, 1, num++, InodeFlags.@readonly | InodeFlags.@internal));
		}
		Inode inode = MakeInode(InodeMode.o_read | InodeMode.o_execute | InodeMode.g_read | InodeMode.g_execute | InodeMode.u_read | InodeMode.u_execute | InodeMode.dir, 1u, 65536L, 65536L, 3, num++, InodeFlags.@readonly);
		super_root_dirents = new List<PfsDirent>
		{
			new PfsDirent
			{
				InodeNumber = fpt_ino.Number,
				Name = "flat_path_table",
				Type = DirentType.File
			}
		};
		if (hasCollision)
		{
			super_root_dirents.Add(new PfsDirent
			{
				InodeNumber = cr_ino.Number,
				Name = "collision_resolver",
				Type = DirentType.File
			});
		}
		super_root_dirents.Add(new PfsDirent
		{
			InodeNumber = inode.Number,
			Name = "uroot",
			Type = DirentType.Directory
		});
		properties.root.name = "uroot";
		properties.root.ino = inode;
		properties.root.Dirents = new List<PfsDirent>
		{
			new PfsDirent
			{
				Name = ".",
				Type = DirentType.Dot,
				InodeNumber = inode.Number
			},
			new PfsDirent
			{
				Name = "..",
				Type = DirentType.DotDot,
				InodeNumber = inode.Number
			}
		};
		if (properties.Sign)
		{
			super_root_ino.Flags &= ~InodeFlags.@readonly;
			fpt_ino.Flags &= ~InodeFlags.@readonly;
			inode.Flags &= ~InodeFlags.@readonly;
		}
	}

	/// <summary>
	/// Creates the publisher PPR-PFS root, where inode 0 is the user root directly.
	/// </summary>
	private void SetupDirectRootStructure()
	{
		Inode ino = MakeInode(InodeMode.o_read | InodeMode.o_execute | InodeMode.g_read | InodeMode.g_execute | InodeMode.u_read | InodeMode.u_write | InodeMode.u_execute | InodeMode.dir, 1u, 65536L, 65536L, 2);
		properties.root.name = "";
		properties.root.ino = ino;
		properties.root.Dirents = new List<PfsDirent>
		{
			new PfsDirent
			{
				Name = ".",
				Type = DirentType.Dot,
				InodeNumber = 0u
			},
			new PfsDirent
			{
				Name = "..",
				Type = DirentType.DotDot,
				InodeNumber = 0u
			}
		};
	}

	/// <summary>
	/// Writes all the inodes to the image file. 
	/// </summary>
	/// <param name="s"></param>
	private void WriteInodes(Stream s)
	{
		s.Position = hdr.InodeBlockSig.StartBlock * hdr.BlockSize;
		foreach (Inode inode in inodes)
		{
			inode.WriteToStream(s);
			if (s.Position % hdr.BlockSize > hdr.BlockSize - (properties.Sign ? 712 : 168))
			{
				s.Position += hdr.BlockSize - s.Position % hdr.BlockSize;
			}
		}
	}

	/// <summary>
	/// Writes the inode allocation bitmap used by publisher PPR-PFS images in block 1.
	/// One low-to-high bit is set for every materialized inode.
	/// </summary>
	private void WriteDirectRootInodeBitmap(Stream stream)
	{
		stream.Position = hdr.BlockSize;
		int num = inodes.Count / 8;
		for (int i = 0; i < num; i++)
		{
			stream.WriteByte(byte.MaxValue);
		}
		int num2 = inodes.Count % 8;
		if (num2 != 0)
		{
			stream.WriteByte((byte)((1 << num2) - 1));
		}
	}

	/// <summary>
	/// Writes the unsigned 32-bit single- and double-indirect block maps used by the publisher
	/// direct-root profile. The reference layout places these maps immediately after each file's
	/// contiguous data extent: ib[0], ib[1], then the ib[1] leaf blocks.
	/// </summary>
	private void WriteDirectRootIndirectBlocks(Stream stream)
	{
		long num = hdr.BlockSize / 4;
		foreach (FSNode allNode in allNodes)
		{
			long num2 = allNode.ino.Blocks;
			if (num2 <= 12)
			{
				continue;
			}
			int startBlock = allNode.ino.StartBlock;
			int num3 = allNode.ino.IndirectBlocks[0];
			stream.Position = checked(num3 * hdr.BlockSize);
			long num4 = Math.Min(num2 - 12, num);
			for (long num5 = 0L; num5 < num4; num5++)
			{
				StreamExtensions.WriteLE(stream, checked(startBlock + 12 + (int)num5));
			}
			long num6 = num2 - 12 - num;
			if (num6 <= 0)
			{
				continue;
			}
			int num7 = allNode.ino.IndirectBlocks[1];
			int num8;
			long num9;
			checked
			{
				num8 = num7 + 1;
				num9 = CeilDiv(num6, num);
				stream.Position = num7 * hdr.BlockSize;
				for (int i = 0; i < num9; i = unchecked(i + 1))
				{
					stream.WriteLE(num8 + i);
				}
			}
			long num10 = 12 + num;
			for (int j = 0; j < num9; j++)
			{
				stream.Position = checked((num8 + j) * hdr.BlockSize);
				long num11 = Math.Min(num6, num);
				for (long num12 = 0L; num12 < num11; num12++)
				{
					StreamExtensions.WriteLE(stream, checked(startBlock + (int)num10++));
				}
				num6 -= num11;
			}
		}
	}

	/// <summary>
	/// Writes the dirents for the superroot, which precede the flat_path_table.
	/// </summary>
	/// <param name="stream"></param>
	private void WriteSuperrootDirents(Stream stream)
	{
		stream.Position = hdr.BlockSize * (hdr.DinodeBlockCount + 1);
		foreach (PfsDirent super_root_dirent in super_root_dirents)
		{
			super_root_dirent.WriteToStream(stream);
		}
	}

	/// <summary>
	/// Writes all the data blocks.
	/// </summary>
	/// <param name="s"></param>
	/// <param name="f"></param>
	private void WriteFSNode(Stream s, FSNode f)
	{
		if (f is FSDir)
		{
			FSDir fSDir = (FSDir)f;
			long position = s.Position;
			long num;
			checked
			{
				num = position + unchecked((long)f.ino.Blocks) * unchecked((long)hdr.BlockSize);
			}
			foreach (PfsDirent dirent in fSDir.Dirents)
			{
				if (dirent.EntSize <= 0 || dirent.EntSize > hdr.BlockSize)
				{
					throw new InvalidDataException($"Directory entry '{dirent.Name}' has invalid size {dirent.EntSize} for block size 0x{hdr.BlockSize:X}.");
				}
				long num2 = (s.Position - position) % hdr.BlockSize;
				long num3 = hdr.BlockSize - num2;
				if (dirent.EntSize > num3)
				{
					s.Position += num3;
				}
				dirent.WriteToStream(s);
			}
			if (s.Position > num)
			{
				throw new InvalidDataException("Directory '" + f.FullPath() + "' exceeded its planned extent.");
			}
		}
		else if (f is FSFile)
		{
			FSFile fSFile = (FSFile)f;
			long position2 = s.Position;
			fSFile.Write(s);
			long num4 = checked(position2 + fSFile.Size);
			if (s.Position != num4)
			{
				throw new InvalidDataException($"File writer for '{fSFile.FullPath()}' produced {s.Position - position2:N0} bytes; the planned extent is {fSFile.Size:N0} bytes.");
			}
		}
	}
}
