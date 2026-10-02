using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PFS;

/// <summary>
/// Class allowing parallel readonly access to a PFS archive
/// </summary>
public class PfsReader
{
	/// <summary>
	/// Represents a file or directory in a PFS image.
	/// </summary>
	public abstract class Node
	{
		public Dir parent;

		public string name;

		public long offset;

		public long size;

		public long compressed_size;

		public uint ino;

		public string FullName
		{
			get
			{
				if (parent == null)
				{
					return name ?? "";
				}
				List<string> parts = new List<string>();
				Node? curr = this;
				HashSet<Node> visited = new HashSet<Node>();
				while (curr != null && visited.Add(curr))
				{
					if (!string.IsNullOrEmpty(curr.name))
					{
						parts.Add(curr.name);
					}
					curr = curr.parent;
				}
				parts.Reverse();
				return string.Join("/", parts);
			}
		}
	}

	/// <summary>
	/// Represents a directory in a PFS image.
	/// </summary>
	public class Dir : Node
	{
		public List<Node> children = new List<Node>();

		public Node Get(string name)
		{
			return children.Where((Node x) => x.name == name).FirstOrDefault();
		}

		public Node GetPath(string name)
		{
			string[] array = name.Split('/');
			Node node = this;
			int num = 0;
			while (node != null && num < array.Length)
			{
				node = (node as Dir)?.Get(array[num]);
				num++;
			}
			if (num < array.Length)
			{
				return null;
			}
			return node;
		}

		public IEnumerable<File> GetAllFiles()
		{
			Queue<Dir> dirQueue = new Queue<Dir>();
			HashSet<Dir> visited = new HashSet<Dir>();
			dirQueue.Enqueue(this);
			visited.Add(this);

			while (dirQueue.Count > 0)
			{
				Dir current = dirQueue.Dequeue();
				foreach (Node n in current.children)
				{
					if (n is File file)
					{
						yield return file;
					}
					else if (n is Dir subDir && visited.Add(subDir))
					{
						dirQueue.Enqueue(subDir);
					}
				}
			}
		}
	}

	/// <summary>
	/// Represents a file in a PFS image.
	/// </summary>
	public class File : Node
	{
		public InodeFlags flags;

		public int blockSize;

		public int[] blocks;

		private IMemoryReader reader;

		public File(IMemoryReader r)
		{
			reader = r;
		}

		public IMemoryReader GetView()
		{
			if (blocks != null)
			{
				return new ChunkedMemoryReader(reader, blockSize, blocks);
			}
			return new MemoryAccessor(reader, offset);
		}

		public void Save(string path, bool decompress = false)
		{
			using FileStream destination = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
			CopyTo(destination, decompress);
		}

		/// <summary>Copies this inode to an arbitrary writable stream.</summary>
		public void CopyTo(Stream destination, bool decompress = false)
		{
			if (destination == null)
			{
				throw new ArgumentNullException("destination");
			}
			if (!destination.CanWrite)
			{
				throw new ArgumentException("Destination must be writable.", "destination");
			}
			byte[] array = new byte[blockSize];
			bool flag = flags.HasFlag(InodeFlags.compressed);
			long num = size;
			long num2 = 0L;
			IMemoryReader memoryReader = GetView();
			if (decompress & flag)
			{
				num = compressed_size;
				memoryReader = new PFSCReader(memoryReader);
			}
			if (destination.CanSeek)
			{
				destination.SetLength(num);
			}
			while (num > 0)
			{
				int num3 = (int)Math.Min(num, array.Length);
				memoryReader.Read(num2, array, 0, num3);
				destination.Write(array, 0, num3);
				num2 += num3;
				num -= num3;
			}
		}

		/// <summary>Reads the complete inode into memory.</summary>
		public byte[] ReadAllBytes(bool decompress = false)
		{
			using MemoryStream memoryStream = new MemoryStream();
			CopyTo(memoryStream, decompress);
			return memoryStream.ToArray();
		}
	}

	private IMemoryReader reader;

	private PfsHeader hdr;

	private Inode[] dinodes;

	private Dir root;

	private Dir uroot;

	private byte[] sectorBuf;

	private Stream sectorStream;

	public PfsHeader Header => hdr;

	public PfsReader(MemoryMappedViewAccessor r, ulong pfs_flags = 0uL, byte[] ekpfs = null, byte[] tweak = null, byte[] data = null)
		: this(new MemoryMappedViewAccessor_(r), pfs_flags, ekpfs, tweak, data, 0L)
	{
	}

	public PfsReader(MemoryMappedViewAccessor r, long superblockOffset, bool encryptedDataAlreadyDecrypted = false, ulong pfs_flags = 0uL, byte[] ekpfs = null, byte[] tweak = null, byte[] data = null)
		: this(new MemoryMappedViewAccessor_(r), pfs_flags, ekpfs, tweak, data, superblockOffset, encryptedDataAlreadyDecrypted)
	{
	}

	public PfsReader(IMemoryReader r, ulong pfs_flags = 0uL, byte[] ekpfs = null, byte[] tweak = null, byte[] data = null, long superblockOffset = 0L, bool encryptedDataAlreadyDecrypted = false)
	{
		if (superblockOffset < 0)
		{
			throw new ArgumentOutOfRangeException("superblockOffset");
		}
		reader = r;
		byte[] array = new byte[1024];
		reader.Read(superblockOffset, array, 0, 1024);
		using (MemoryStream s = new MemoryStream(array))
		{
			hdr = PfsHeader.ReadFromStream(s);
		}
		bool flag = hdr.Mode.HasFlag(PfsMode.Is64Bit);
		Func<Stream, Inode> func;
		int num;
		if (hdr.Mode.HasFlag(PfsMode.PprDirectOffsets))
		{
			Inode[] array2 = new DinodePpr[hdr.DinodeCount];
			dinodes = array2;
			func = DinodePpr.ReadFromStream;
			num = 168;
		}
		else if (hdr.Mode.HasFlag(PfsMode.Signed))
		{
			if (flag)
			{
				Inode[] array2 = new DinodeS64[hdr.DinodeCount];
				dinodes = array2;
				func = DinodeS64.ReadFromStream;
				num = 784;
			}
			else
			{
				Inode[] array2 = new DinodeS32[hdr.DinodeCount];
				dinodes = array2;
				func = DinodeS32.ReadFromStream;
				num = 712;
			}
		}
		else
		{
			Inode[] array2 = new DinodeD32[hdr.DinodeCount];
			dinodes = array2;
			func = DinodeD32.ReadFromStream;
			num = 168;
		}
		if (hdr.Mode.HasFlag(PfsMode.Encrypted) && !encryptedDataAlreadyDecrypted)
		{
			uint startSector = hdr.BlockSize / 4096;
			if (ekpfs == null && (tweak == null || data == null))
			{
				throw new ArgumentException("PFS image is encrypted but no decryption key was provided");
			}
			if (ekpfs != null)
			{
				var (tweakKey, dataKey) = Crypto.PfsGenEncKey(ekpfs, hdr.Seed, (pfs_flags & 0x2000000000000000L) != 0);
				reader = new XtsDecryptReader(reader, dataKey, tweakKey, startSector);
			}
			else
			{
				reader = new XtsDecryptReader(reader, data, tweak, startSector);
			}
		}
		int num2 = 0;
		long num3 = hdr.BlockSize / num;
		sectorBuf = new byte[hdr.BlockSize];
		sectorStream = new MemoryStream(sectorBuf);
		long num4 = hdr.InodeBlockSig.StartBlock * hdr.BlockSize;
		for (int i = 0; i < hdr.DinodeBlockCount; i++)
		{
			long pos = num4 + hdr.BlockSize * i;
			reader.Read(pos, sectorBuf, 0, sectorBuf.Length);
			sectorStream.Position = 0L;
			for (int j = 0; j < num3; j++)
			{
				if (num2 >= hdr.DinodeCount)
				{
					break;
				}
				dinodes[num2++] = func(sectorStream);
			}
		}
		root = LoadDirectoryTree(0u);
		uroot = root.Get("uroot") as Dir;
		if (uroot == null)
		{
			uroot = root;
		}
		else
		{
			uroot.name = "uroot";
		}
	}

	public File GetFile(string fullPath)
	{
		return uroot.GetPath(fullPath) as File;
	}

	public IEnumerable<File> GetAllFiles()
	{
		return uroot.GetAllFiles();
	}

	public Dir GetURoot()
	{
		return uroot;
	}

	public Dir GetSuperRoot()
	{
		return root;
	}

	private Dir LoadDirectoryTree(uint rootDinode)
	{
		Dir rootDir = new Dir
		{
			name = "",
			parent = null,
			ino = rootDinode
		};

		HashSet<uint> visited = new HashSet<uint> { rootDinode };
		Queue<(uint dinode, Dir dirNode)> queue = new Queue<(uint dinode, Dir dirNode)>();
		queue.Enqueue((rootDinode, rootDir));

		while (queue.Count > 0)
		{
			var (currDinode, currDir) = queue.Dequeue();
			if (currDinode >= dinodes.Length || dinodes[currDinode] == null)
			{
				continue;
			}

			Inode inode = dinodes[currDinode];
			bool flag = inode is DinodePpr;
			int num = (flag ? checked((int)Math.Max(1L, unchecked(checked(inode.Size + hdr.BlockSize - 1) / hdr.BlockSize))) : ((int)inode.Blocks));
			long num2 = (flag ? ((DinodePpr)inode).DataOffset : (inode.StartBlock * hdr.BlockSize));
			if (num < 1 || num2 < 0 || num2 / hdr.BlockSize > 100000000 || num > 100000000)
			{
				continue;
			}

			for (int i = 0; i < num; i++)
			{
				long num3 = checked(num2 + i * hdr.BlockSize);
				long num4 = num3;
				reader.Read(num3, sectorBuf, 0, sectorBuf.Length);
				sectorStream.Position = 0L;
				PfsDirent dirent;
				for (; num4 < num3 + hdr.BlockSize; num4 += dirent.EntSize)
				{
					dirent = PfsDirent.ReadFromStream(sectorStream);
					if (dirent.EntSize == 0)
					{
						break;
					}

					// Skip self, parent, and invalid entries
					if (string.IsNullOrWhiteSpace(dirent.Name) ||
					    dirent.Name == "." || dirent.Name == ".." ||
					    dirent.Type == DirentType.Dot || dirent.Type == DirentType.DotDot ||
					    dirent.InodeNumber == currDinode)
					{
						continue;
					}

					if (dirent.InodeNumber >= dinodes.Length || dinodes[dirent.InodeNumber] == null)
					{
						continue;
					}

					switch (dirent.Type)
					{
					case DirentType.File:
						currDir.children.Add(LoadFile(dirent.InodeNumber, currDir, dirent.Name));
						break;

					case DirentType.Directory:
						if (visited.Add(dirent.InodeNumber))
						{
							Dir childDir = new Dir
							{
								name = dirent.Name,
								parent = currDir,
								ino = dirent.InodeNumber
							};
							currDir.children.Add(childDir);
							queue.Enqueue((dirent.InodeNumber, childDir));
						}
						break;
					}
				}
			}
		}

		return rootDir;
	}

	private File LoadFile(uint dinode, Dir parent, string name)
	{
		int[] blocks = null;
		int outputIndex;
		int remainingBlocks;
		BufferedMemoryReader bufferedReader;
		int entriesPerBlock;
		if (dinodes[dinode].Blocks > 1)
		{
			if (!hdr.Mode.HasFlag(PfsMode.Signed))
			{
				blocks = null;
			}
			else
			{
				int num = checked((int)dinodes[dinode].Blocks);
				blocks = new int[num];
				outputIndex = 0;
				remainingBlocks = num;
				int num2 = Math.Min(remainingBlocks, dinodes[dinode].DirectBlocks.Count);
				for (int i = 0; i < num2; i++)
				{
					int num3 = dinodes[dinode].DirectBlocks[i];
					if (num3 < 0)
					{
						throw new InvalidDataException($"Signed inode {dinode} has an invalid direct block {num3} at db[{i}].");
					}
					blocks[outputIndex++] = num3;
				}
				bufferedReader = new BufferedMemoryReader(reader, 65536);
				remainingBlocks -= num2;
				IList<int> indirectBlocks;
				checked
				{
					entriesPerBlock = (int)unchecked(hdr.BlockSize / 36);
					indirectBlocks = dinodes[dinode].IndirectBlocks;
				}
				for (int j = 0; j < indirectBlocks.Count; j++)
				{
					if (remainingBlocks <= 0)
					{
						break;
					}
					ReadIndirectNode(indirectBlocks[j], j + 1);
				}
				if (remainingBlocks != 0)
				{
					throw new InvalidDataException($"Signed inode {dinode} is missing mappings for {remainingBlocks} data blocks.");
				}
				bool flag = true;
				for (int k = 1; k < blocks.Length; k++)
				{
					if (blocks[k - 1] + 1 != blocks[k])
					{
						flag = false;
						break;
					}
				}
				if (flag)
				{
					blocks = null;
				}
			}
		}
		return new File(reader)
		{
			name = name,
			parent = parent,
			offset = ((dinodes[dinode] is DinodePpr dinodePpr) ? dinodePpr.DataOffset : (dinodes[dinode].StartBlock * hdr.BlockSize)),
			size = dinodes[dinode].Size,
			compressed_size = dinodes[dinode].SizeCompressed,
			ino = dinode,
			blocks = blocks,
			flags = dinodes[dinode].Flags,
			blockSize = (int)hdr.BlockSize
		};
		void ReadIndirectNode(int mapBlock, int depth)
		{
			if (mapBlock <= 0)
			{
				throw new InvalidDataException($"Signed inode {dinode} has a missing depth-{depth} indirect map.");
			}
			long num4 = checked(mapBlock * hdr.BlockSize);
			for (int l = 0; l < entriesPerBlock; l++)
			{
				if (remainingBlocks <= 0)
				{
					break;
				}
				int value;
				checked
				{
					bufferedReader.Read<int>(num4 + unchecked((long)l) * 36L + 32, out value);
					if (value < 0)
					{
						throw new InvalidDataException($"Signed inode {dinode} has a negative indirect block pointer.");
					}
				}
				if (depth == 1)
				{
					blocks[outputIndex++] = value;
					remainingBlocks--;
				}
				else
				{
					ReadIndirectNode(value, depth - 1);
				}
			}
		}
	}
}
