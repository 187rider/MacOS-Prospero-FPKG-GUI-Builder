using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibProsperoPkg.PFS.Compression;
using LibProsperoPkg.PFS.Compression.Oodle;
using LibProsperoPkg.PKG;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PFS;

/// <summary>
/// Builds a PS5 nwonly inner <c>pfs_image.dat</c> from a flat list of files. Handles the two flat-path
/// tables, the afid table, the data-first layout, and per-file Kraken compression.
/// </summary>
public sealed class ProsperoPs5InnerImageAssembler
{
	private sealed class Dir
	{
		public string Name = "";

		public string FullPath = "";

		public Dir? Parent;

		public readonly List<Dir> SubDirs = new List<Dir>();

		public readonly List<FileNode> Files = new List<FileNode>();

		public uint Inode;

		public int DirentOffsetInParent = -1;

		public readonly List<PfsDirent> Dirents = new List<PfsDirent>();
	}

	private sealed class FileNode
	{
		public string Name = "";

		public string FullPath = "";

		public byte[]? Data;

		public long Size;

		public Func<Stream>? OpenStream;

		public bool IsExecutable;

		public Dir Parent;

		public uint Inode;

		public uint Afid;

		public bool StoreRaw;

		public long LogicalOffset;

		public int DirentOffsetInParent = -1;

		/// <summary>The file's on-disk (data-region) byte offset in the built image (set during assembly).</summary>
		public long OnDiskOffset;

		public long OnDiskSize;

		/// <summary>The file's on-disk bytes (raw when StoreRaw, else the Kraken-compressed payload). Cached to
		/// avoid recompressing: it drives both the data-region geometry and the final image assembly.</summary>
		public byte[]? OnDiskData;

		/// <summary>Per-256 KiB compression geometry used directly by the NAPS CBI generator.</summary>
		public IReadOnlyList<ProsperoInnerDataBlockChunk> CompressionBlocks = Array.Empty<ProsperoInnerDataBlockChunk>();

		/// <summary>Per-256 KiB integrity hashes computed during compression/writing.</summary>
		public IReadOnlyList<ProsperoInnerBlockIntegrity> BlockIntegrities = Array.Empty<ProsperoInnerBlockIntegrity>();

		/// <summary>True for files in the sce_sys subtree. Drives the inode mode (base +0x20000) and the
		/// block-info Σ exclusion (sce_sys payload is not part of the uroot app-payload sum).</summary>
		public bool SceSys;

		/// <summary>True only for the DRM keystone, which is stored raw and occupies whole 64 KiB blocks
		/// (block-aligned start and end) so the packed data region begins on a fresh block after it. Other
		/// sce_sys payload files are Kraken-compressed and packed like app payload.</summary>
		public bool WholeBlockRaw;
	}

	private sealed record RenderedFsTree(IReadOnlyList<ProsperoPs5InnerFile> Files, IReadOnlyList<ProsperoPs5InnerDirectory> Directories);

	/// <summary>Inner-image block size (64 KiB).</summary>
	public const int BlockSize = 65536;

	private const string SceSysDir = "sce_sys";

	private readonly long _timeSec;

	private readonly uint _timeNsec;

	private readonly IReadOnlyDictionary<string, uint>? _explicitAfids;

	private readonly Action<string> _log;

	private readonly bool _compress;

	private readonly System.Threading.CancellationToken _cancellationToken;

	private readonly int _maxHashingThreads;

	/// <summary>
	/// PFSv3 build/SDK version stamped into every block-75 entry (little-endian u32). The low nibble
	/// encodes system/firmware 4.03.
	/// </summary>
	private const uint BlockInfoVersion = 4194307u;

	/// <summary>The constant value emitted for the first 31 slots of the block-75 table; independent
	/// of package content.</summary>
	private const uint BlockInfoTemplate = 16580391u;

	/// <summary>
	/// Global base constant (read big-endian) of the block-75 variable entry. See <see cref="M:LibProsperoPkg.PFS.ProsperoPs5InnerImageAssembler.BuildBlockInfoTable(System.Int64)" />.
	/// </summary>
	private const uint BlockInfoBase = 2621436u;

	/// <param name="buildTimeSec">Build timestamp seconds (package c_date/c_time — a deterministic build input).</param>
	/// <param name="buildTimeNsec">Build timestamp nanoseconds fraction.</param>
	/// <param name="explicitAfids">
	/// Optional exact path-to-AFID map. Unassigned slot numbers become sparse 256-KiB zero extents.
	/// </param>
	/// <param name="logger">Optional detailed progress sink.</param>
	/// <param name="compress">Whether to compress data files with Kraken (default true; false stores raw).</param>
	/// <param name="cancellationToken">Cancellation token to abort.</param>
	/// <param name="maxHashingThreads">Max CPU threads/profile for hashing and thermal pacing (default 2).</param>
	public ProsperoPs5InnerImageAssembler(long buildTimeSec, uint buildTimeNsec, IReadOnlyDictionary<string, uint>? explicitAfids = null, Action<string>? logger = null, bool compress = true, System.Threading.CancellationToken cancellationToken = default, int maxHashingThreads = 2)
	{
		_timeSec = buildTimeSec;
		_timeNsec = buildTimeNsec;
		_explicitAfids = explicitAfids;
		_log = logger ?? ((Action<string>)((string _) =>
		{
		}));
		_compress = compress;
		_cancellationToken = cancellationToken;
		_maxHashingThreads = Math.Max(1, maxHashingThreads);
	}

	/// <summary>
	/// Bridges the package builder's <see cref="T:LibProsperoPkg.PFS.FSDir" /> tree to the assembler: renders every file's
	/// bytes and its full path, then builds the data-first inner image. Convenience entry point for wiring the
	/// assembler into <c>ProsperoPkgBuilder</c> for the nwonly Kraken format.
	/// </summary>
	public ProsperoPs5InnerImageResult BuildFromFsTree(FSDir uroot)
	{
		RenderedFsTree renderedFsTree = RenderFsTree(uroot);
		return Build(renderedFsTree.Files, renderedFsTree.Directories);
	}

	/// <summary>
	/// File-backed counterpart of <see cref="M:LibProsperoPkg.PFS.ProsperoPs5InnerImageAssembler.BuildFromFsTree(LibProsperoPkg.PFS.FSDir)" />. The final physical image is written
	/// directly to <paramref name="outputPath" /> and <see cref="P:LibProsperoPkg.PFS.ProsperoPs5InnerImageResult.Image" />
	/// is empty; metadata and per-file integrity inputs remain available in the result.
	/// </summary>
	public ProsperoPs5InnerImageResult BuildFromFsTreeToFile(FSDir uroot, string outputPath)
	{
		RenderedFsTree renderedFsTree = RenderFsTree(uroot);
		return BuildToFile(renderedFsTree.Files, renderedFsTree.Directories, outputPath);
	}

	private RenderedFsTree RenderFsTree(FSDir uroot)
	{
		ArgumentNullException.ThrowIfNull(uroot, "uroot");
		List<FSFile> list = (from file in uroot.GetAllChildrenFiles()
			where !IsExcludedFromInner(file.FullPath())
			select file).ToList();
		long value = list.Sum((FSFile file) => file.Size);
		_log($"Scanning {list.Count:N0} inner files ({value:N0} bytes)...");
		List<ProsperoPs5InnerFile> list2 = new List<ProsperoPs5InnerFile>();
		int num = -1;
		for (int num2 = 0; num2 < list.Count; num2++)
		{
			FSFile fSFile = list[num2];
			byte[]? smallData = null;
			if (fSFile.Size <= 65536)
			{
				using MemoryStream memoryStream = new MemoryStream();
				fSFile.Write(memoryStream);
				smallData = memoryStream.ToArray();
			}
			list2.Add(new ProsperoPs5InnerFile
			{
				Path = fSFile.FullPath(),
				Size = fSFile.Size,
				Data = smallData,
				OpenStream = (smallData != null) ? (() => new MemoryStream(smallData, writable: false)) : fSFile.OpenRead
			});
			int num3 = ((list.Count == 0) ? 100 : ((num2 + 1) * 100 / list.Count));
			if (list.Count <= 20 || num3 == 100 || num3 / 5 != num / 5)
			{
				num = num3;
				_log($"  scanned {num2 + 1:N0}/{list.Count:N0} ({num3,3}%): {fSFile.FullPath()} ({fSFile.Size:N0} bytes)");
			}
		}
		List<ProsperoPs5InnerDirectory> list3 = (from directory in uroot.GetAllChildrenDirs()
			select new ProsperoPs5InnerDirectory
			{
				Path = directory.FullPath()
			}).ToList();
		_log($"Prepared inner tree: {list2.Count:N0} files, {list3.Count:N0} directories.");
		return new RenderedFsTree(list2, list3);
	}

	private static bool IsIncompressible(string fullPath)
	{
		string ext = Path.GetExtension(fullPath).ToLowerInvariant();
		return ext switch
		{
			".png" or ".jpg" or ".jpeg" or ".dds" or ".at9" or ".bik" or ".bk2" or ".mp4" or ".webm" or ".cas" => true,
			_ => Path.GetFileName(fullPath).StartsWith("._", StringComparison.Ordinal)
		};
	}

	private static bool IsExcludedFromInner(string fullPath)
	{
		if (fullPath.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) ||
		    fullPath.EndsWith(".esbak", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		if (!fullPath.StartsWith("/sce_sys/", StringComparison.Ordinal))
		{
			return false;
		}
		string text = fullPath.Substring("/sce_sys/".Length);
		if ((!(text == "param.json") && !(text == "pic2.png")) || 1 == 0)
		{
			return EntryNames.NameToId.ContainsKey(text);
		}
		return true;
	}

	/// <summary>Assembles the inner image from the supplied files.</summary>
	public ProsperoPs5InnerImageResult Build(IReadOnlyList<ProsperoPs5InnerFile> files)
	{
		return BuildCore(files, null, null);
	}

	/// <summary>
	/// Assembles the inner image from files and an explicit directory list.  Parent directories may
	/// be omitted because they are inferred; explicitly listed empty leaf directories are retained.
	/// </summary>
	public ProsperoPs5InnerImageResult Build(IReadOnlyList<ProsperoPs5InnerFile> files, IReadOnlyList<ProsperoPs5InnerDirectory> directories)
	{
		return BuildCore(files, directories, null);
	}

	/// <summary>Assembles the inner image directly into a file without retaining its full bytes.</summary>
	public ProsperoPs5InnerImageResult BuildToFile(IReadOnlyList<ProsperoPs5InnerFile> files, string outputPath)
	{
		return BuildToFile(files, null, outputPath);
	}

	/// <summary>
	/// File-backed counterpart accepting explicit directories, including empty GP5 directories.
	/// </summary>
	public ProsperoPs5InnerImageResult BuildToFile(IReadOnlyList<ProsperoPs5InnerFile> files, IReadOnlyList<ProsperoPs5InnerDirectory>? directories, string outputPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(outputPath, "outputPath");
		string fullPath = Path.GetFullPath(outputPath);
		Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory());
		return BuildCore(files, directories, fullPath);
	}

	private ProsperoPs5InnerImageResult BuildCore(IReadOnlyList<ProsperoPs5InnerFile> files, IReadOnlyList<ProsperoPs5InnerDirectory>? directories, string? outputPath)
	{
		ArgumentNullException.ThrowIfNull(files, "files");
		if (files.Count == 0)
		{
			throw new ArgumentException("At least one inner file is required.", "files");
		}
		long num = files.Sum((ProsperoPs5InnerFile file) => (file.Data != null ? file.Data.LongLength : file.Size));
		_log($"Planning nwonly inner image: {files.Count:N0} files, {num:N0} uncompressed bytes.");
		Dir dir = BuildTree(files, directories);
		List<Dir> list = new List<Dir>();
		CollectDirsPreOrder(dir, list);
		uint num2 = 0u;
		uint superRootInode = num2++;
		uint inodeFltInode = num2++;
		uint aprFltInode = num2++;
		uint afidTableInode = num2++;
		foreach (Dir item in list)
		{
			item.Inode = num2++;
		}
		List<Dir> list2 = new List<Dir>();
		CollectDirsPostOrder(dir, list2);
		List<FileNode> list3 = new List<FileNode>();
		foreach (Dir item2 in list2)
		{
			foreach (FileNode item3 in item2.Files.OrderBy((FileNode f) => f.Name, StringComparer.Ordinal))
			{
				item3.Inode = num2++;
				list3.Add(item3);
			}
		}
		List<FileNode> list4 = new List<FileNode>();
		Dir dir2 = dir.SubDirs.FirstOrDefault((Dir d) => d.Name == "sce_sys");
		if (dir2 != null)
		{
			List<FileNode> list5 = new List<FileNode>();
			CollectFilesPreOrder(dir2, list5);
			list4.AddRange(list5.OrderBy(SystemAfidRank).ThenBy((FileNode f) => f.FullPath, StringComparer.Ordinal));
		}
		CollectFilesEntryOrder(dir, dir2, list4);
		List<FileNode> list6 = list4.Where((FileNode file) => file.Size == 0).ToList();
		list4.RemoveAll((FileNode file) => file.Size == 0);
		List<FileNode> list7;
		if (_explicitAfids == null)
		{
			list7 = new List<FileNode>();
			long num3 = 0L;
			long num4 = -1L;
			int num5 = 0;
			foreach (FileNode item4 in list4)
			{
				long num6 = num3 / 262144;
				if (num6 != num4)
				{
					num4 = num6;
					num5 = 0;
				}
				int num7 = ((num4 == 0L) ? 28 : 30);
				if (num5 >= num7)
				{
					list7.Add(null);
					num3 = checked(num3 + 262144);
					num4 = num3 / 262144;
					num5 = 0;
				}
				checked
				{
					item4.Afid = (uint)list7.Count;
					list7.Add(item4);
					num3 += item4.Size;
				}
				num5++;
			}
		}
		else
		{
			Dictionary<string, uint> dictionary = new Dictionary<string, uint>(StringComparer.Ordinal);
			foreach (var (path, value) in _explicitAfids)
			{
				string text2 = NormalizeAfidPath(path);
				if (!dictionary.TryAdd(text2, value))
				{
					throw new InvalidDataException("The explicit AFID map contains duplicate path '" + text2 + "'.");
				}
			}
			if (dictionary.Count != list4.Count)
			{
				throw new InvalidDataException($"The explicit AFID map contains {dictionary.Count} paths but the inner image contains {list4.Count} files.");
			}
			uint num9 = 0u;
			foreach (FileNode item5 in list4)
			{
				if (!dictionary.TryGetValue(item5.FullPath, out var value2))
				{
					throw new InvalidDataException("The explicit AFID map has no assignment for '" + item5.FullPath + "'.");
				}
				item5.Afid = value2;
				num9 = Math.Max(num9, value2);
			}
			if (list4.Count == 0)
			{
				list7 = new List<FileNode>();
			}
			else
			{
				if (num9 > 10000000)
				{
					throw new InvalidDataException($"The explicit AFID map requires slot {num9}, exceeding the supported {10000000u}-slot diagnostic limit.");
				}
				list7 = Enumerable.Repeat<FileNode>(null, checked((int)num9 + 1)).ToList();
				foreach (FileNode item6 in list4)
				{
					if (list7[(int)item6.Afid] != null)
					{
						throw new InvalidDataException($"The explicit AFID map assigns slot {item6.Afid} more than once.");
					}
					list7[(int)item6.Afid] = item6;
				}
			}
		}
		list4 = (from file in list7
			where file != null
			select (file)).ToList();
		uint afid = checked((uint)list7.Count + (uint)list6.Count + 1);
		foreach (FileNode item7 in list6)
		{
			item7.Afid = afid;
		}
		_log($"AFID layout: {list4.Count:N0} data files, {list6.Count:N0} empty files, {list7.Count((FileNode file) => file == null):N0} sparse slots.");
		long num10 = 0L;
		long num11 = 0L;
		FileStream fileStream = ((outputPath == null) ? null : new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1048576, FileOptions.SequentialScan));
		long[] array = new long[list7.Count];
		List<ProsperoPs5SparseAfidHole> list8 = new List<ProsperoPs5SparseAfidHole>();
		long num12 = 0L;
		int num13 = 0;
		int num14 = -1;
		byte[] chunkBuffer = new byte[262144];
		_log("Compressing and writing AFID-ordered inner data...");
		try
		{
			for (int num15 = 0; num15 < list7.Count; num15++)
			{
				_cancellationToken.ThrowIfCancellationRequested();
				FileNode fileNode = list7[num15];
				array[num15] = num10;
				checked
				{
					if (fileNode == null)
					{
						list8.Add(new ProsperoPs5SparseAfidHole((uint)num15, num10, 262144L));
						num10 += 262144;
						continue;
					}
					fileNode.LogicalOffset = num10;
					num10 = unchecked(num10 + fileNode.Size);
					if (fileNode.Size >= 67108864)
					{
						_log($"  processing large file: {fileNode.FullPath} ({fileNode.Size:N0} bytes)");
					}

					fileNode.SceSys = fileNode.FullPath.StartsWith("/sce_sys/", StringComparison.Ordinal);
					fileNode.WholeBlockRaw = IsKeystone(fileNode.FullPath) || fileNode.IsExecutable;

					List<ProsperoInnerBlockIntegrity> blockIntegrities = new List<ProsperoInnerBlockIntegrity>();
					List<ProsperoInnerDataBlockChunk> compressionBlocks = new List<ProsperoInnerDataBlockChunk>();

					long startOffset = num11;
					if (fileNode.WholeBlockRaw)
					{
						startOffset = RoundUp(startOffset, 65536L);
					}
					fileNode.OnDiskOffset = startOffset;

					bool isRaw = !_compress || fileNode.WholeBlockRaw || IsIncompressible(fileNode.FullPath);

					if (isRaw)
					{
						fileNode.StoreRaw = true;
						fileNode.CompressionBlocks = Array.Empty<ProsperoInnerDataBlockChunk>();
						if (fileStream != null)
						{
							fileStream.Position = startOffset;
						}
						using (Stream stream = fileNode.OpenStream!())
						{
							long remaining = fileNode.Size;
							MemoryStream? memDest = (fileStream == null) ? new MemoryStream() : null;
							int chunkCount = 0;
							int lastReportedFilePct = -1;
							while (remaining > 0)
							{
								_cancellationToken.ThrowIfCancellationRequested();
								int toRead = (int)Math.Min(262144L, remaining);
								stream.ReadExactly(chunkBuffer, 0, toRead);
								ReadOnlySpan<byte> chunkSpan = chunkBuffer.AsSpan(0, toRead);

								byte[] sha3 = ProsperoImageDigests.Sha3_256(chunkSpan);
								ulong ihsh = ProsperoNapsMeta.ComputeInputChecksum(chunkSpan);
								ulong rhsh = ProsperoNapsMeta.ComputeRollingHash(chunkSpan);
								blockIntegrities.Add(new ProsperoInnerBlockIntegrity(sha3, ihsh, rhsh));

								if (fileStream != null)
								{
									fileStream.Write(chunkSpan);
								}
								else
								{
									memDest!.Write(chunkSpan);
								}
								remaining -= toRead;
								chunkCount++;

								if (fileNode.Size >= 67108864)
								{
									long processed = fileNode.Size - remaining;
									int filePct = (int)(processed * 100 / fileNode.Size);
									if (filePct >= lastReportedFilePct + 2 || remaining == 0)
									{
										lastReportedFilePct = filePct;
										long totalProcessed = num12 + processed;
										int overallPct = (int)(totalProcessed * 100 / Math.Max(1L, num));
										_log($"  data {overallPct,3}%: {fileNode.FullPath} ({filePct}% - {processed:N0}/{fileNode.Size:N0} bytes)");
									}
								}
							}
							if (fileStream == null)
							{
								fileNode.OnDiskData = memDest!.ToArray();
							}
						}
						num11 = startOffset + fileNode.Size;
						if (fileNode.WholeBlockRaw)
						{
							num11 = RoundUp(num11, 65536L);
						}
						fileNode.OnDiskSize = fileNode.Size;
					}
					else
					{
						if (fileStream != null)
						{
							fileStream.Position = startOffset;
						}
						MemoryStream? compMemDest = (fileStream == null) ? new MemoryStream() : null;
						long totalCompressedSize = 0L;

						using (Stream stream = fileNode.OpenStream!())
						{
							long remaining = fileNode.Size;
							int compChunkCount = 0;
							int lastReportedFilePct = -1;
							int batchCapacity = Math.Max(1, Math.Min(32, _maxHashingThreads > 0 ? _maxHashingThreads * 4 : 16));
							byte[][] batchBuffers = new byte[batchCapacity][];
							for (int b = 0; b < batchCapacity; b++)
							{
								batchBuffers[b] = new byte[262144];
							}
							int[] batchLengths = new int[batchCapacity];
							byte[][] batchSha3 = new byte[batchCapacity][];
							ulong[] batchIhsh = new ulong[batchCapacity];
							ulong[] batchRhsh = new ulong[batchCapacity];
							EncodedBlock?[] batchEnc = new EncodedBlock?[batchCapacity];

							while (remaining > 0)
							{
								_cancellationToken.ThrowIfCancellationRequested();
								int batchCount = 0;
								while (batchCount < batchCapacity && remaining > 0)
								{
									int toRead = (int)Math.Min(262144L, remaining);
									stream.ReadExactly(batchBuffers[batchCount], 0, toRead);
									batchLengths[batchCount] = toRead;
									remaining -= toRead;
									batchCount++;
								}

								ParallelOptions pOpts = new ParallelOptions
								{
									MaxDegreeOfParallelism = Math.Max(1, _maxHashingThreads),
									CancellationToken = _cancellationToken
								};

								Parallel.For(0, batchCount, pOpts, i =>
								{
									int len = batchLengths[i];
									ReadOnlySpan<byte> chunkSpan = batchBuffers[i].AsSpan(0, len);
									batchSha3[i] = ProsperoImageDigests.Sha3_256(chunkSpan);
									batchIhsh[i] = ProsperoNapsMeta.ComputeInputChecksum(chunkSpan);
									batchRhsh[i] = ProsperoNapsMeta.ComputeRollingHash(chunkSpan);
									batchEnc[i] = (len < 64) ? null : OodleKrakenEncoder.EncodeBlock(chunkSpan, useHuffmanArrays: true);
								});

								for (int i = 0; i < batchCount; i++)
								{
									int len = batchLengths[i];
									blockIntegrities.Add(new ProsperoInnerBlockIntegrity(batchSha3[i], batchIhsh[i], batchRhsh[i]));
									EncodedBlock? enc = batchEnc[i];

									if (enc.HasValue && enc.Value.Payload.Length < len)
									{
										EncodedBlock val = enc.Value;
										byte[] compPayload = val.Payload;
										if (fileStream != null)
										{
											fileStream.Write(compPayload);
										}
										else
										{
											compMemDest!.Write(compPayload);
										}
										totalCompressedSize += compPayload.Length;
										compressionBlocks.Add(new ProsperoInnerDataBlockChunk(
											compPayload.Length, len, IsStored: false, val.MultiChunk, val.FirstChunkCompSize, val.BoundaryFlags));
									}
									else
									{
										ReadOnlySpan<byte> rawSpan = batchBuffers[i].AsSpan(0, len);
										if (fileStream != null)
										{
											fileStream.Write(rawSpan);
										}
										else
										{
											compMemDest!.Write(rawSpan);
										}
										totalCompressedSize += len;
										compressionBlocks.Add(new ProsperoInnerDataBlockChunk(
											len, len, IsStored: true, IsMultiChunk: false, 0, 0));
									}
									compChunkCount++;
								}

								if (fileNode.Size >= 67108864)
								{
									long processed = fileNode.Size - remaining;
									int filePct = (int)(processed * 100 / fileNode.Size);
									if (filePct >= lastReportedFilePct + 2 || remaining == 0)
									{
										lastReportedFilePct = filePct;
										long totalProcessed = num12 + processed;
										int overallPct = (int)(totalProcessed * 100 / Math.Max(1L, num));
										_log($"  data {overallPct,3}%: {fileNode.FullPath} ({filePct}% - {processed:N0}/{fileNode.Size:N0} bytes)");
									}
								}
							}
						}

						bool keepCompressed = totalCompressedSize <= (long)fileNode.Size * 15L / 16L;
						if (keepCompressed)
						{
							fileNode.StoreRaw = false;
							fileNode.CompressionBlocks = compressionBlocks;
							fileNode.OnDiskSize = totalCompressedSize;
							num11 = startOffset + totalCompressedSize;
							if (fileStream == null)
							{
								fileNode.OnDiskData = compMemDest!.ToArray();
							}
						}
						else
						{
							fileNode.StoreRaw = true;
							fileNode.CompressionBlocks = Array.Empty<ProsperoInnerDataBlockChunk>();
							fileNode.OnDiskSize = fileNode.Size;
							if (fileStream != null)
							{
								fileStream.Position = startOffset;
								using Stream stream = fileNode.OpenStream!();
								stream.CopyTo(fileStream, 1048576);
							}
							else
							{
								using MemoryStream mem = new MemoryStream();
								using Stream stream = fileNode.OpenStream!();
								stream.CopyTo(mem, 1048576);
								fileNode.OnDiskData = mem.ToArray();
							}
							num11 = startOffset + fileNode.Size;
						}
					}

					fileNode.BlockIntegrities = blockIntegrities;
				}
				num13++;
				num12 += fileNode.Size;
				int num16;
				checked
				{
					num16 = ((num == 0L) ? 100 : ((int)Math.Min(100L, unchecked(checked(num12 * 100) / num))));
				}
				if (num13 == 1 || list4.Count <= 20 || num16 == 100 || num16 != num14 || num13 % 20 == 0 || fileNode.Size >= 67108864)
				{
					num14 = num16;
					double value3 = ((fileNode.Size == 0) ? 1.0 : ((double)fileNode.OnDiskSize / (double)fileNode.Size));
					_log($"  data {num16,3}% ({num13:N0}/{list4.Count:N0}): {fileNode.FullPath} -> {fileNode.OnDiskSize:N0} bytes ({(fileNode.StoreRaw ? "stored" : "Kraken")}, ratio {value3:P1})");
				}
			}
			foreach (FileNode item8 in list6)
			{
				item8.LogicalOffset = num10;
			}
		}
		finally
		{
			if (fileStream != null)
			{
				fileStream.SetLength(num11);
				fileStream.Flush(flushToDisk: true);
				fileStream.Dispose();
			}
		}
		long logicalDataBlocks = RoundUp(Math.Max(num10, 1L), 262144L) / 65536;
		BuildDirents(dir);
		List<byte[]> list9 = BuildMetadataPayloads(list, list3, list7, inodeFltInode, aprFltInode, afidTableInode, dir);
		_log($"Metadata tables prepared: {list.Count:N0} directories, {list3.Count:N0} file inodes, {list9.Count:N0} payloads.");
		List<ProsperoPs5MetaNode> nodes = BuildNodes(dir, list, list3, logicalDataBlocks, superRootInode, inodeFltInode, aprFltInode, afidTableInode, list9, out var ndblock, out var trailingMetadataBlocks);
		byte[] array3 = BuildMetadataPlaintext(nodes, list9, ndblock, trailingMetadataBlocks);
		byte[] image = BuildImage(list4, array3, num10, outputPath, num11, out long imageLength, out long blockInfoOnDisk, out long metadataOnDisk, out byte[] compressedMeta, out IReadOnlyList<ProsperoInnerMetaBlockChunk> metaBlocks);
		_log($"Inner image assembled: {imageLength:N0} bytes, Ndblock={ndblock:N0}, metadata={array3.Length:N0}->{compressedMeta.Length:N0} bytes.");
		long metaBaseLogical = ndblock * 65536 - array3.Length;
		List<ProsperoPs5InnerPlacement> placements = list4.Select((FileNode f) => new ProsperoPs5InnerPlacement
		{
			Afid = f.Afid,
			OnDiskOffset = f.OnDiskOffset,
			LogicalOffset = f.LogicalOffset,
			OnDiskSize = f.OnDiskSize,
			UncompressedSize = f.Size,
			PlainData = (f.Data != null && f.Data.Length > 0 && f.Data.Length <= 65536) ? f.Data : ReadOnlyMemory<byte>.Empty,
			StoreRaw = f.StoreRaw,
			CompressionBlocks = f.CompressionBlocks,
			BlockIntegrities = f.BlockIntegrities
		}).ToList();
		return new ProsperoPs5InnerImageResult
		{
			Image = image,
			ImagePath = outputPath,
			ImageLength = imageLength,
			MetadataPlaintext = array3,
			Nodes = nodes,
			Ndblock = ndblock,
			AfidLogicalOffsets = array,
			EmptyFileLogicalOffsets = list6.Select((FileNode file) => file.LogicalOffset).ToArray(),
			Placements = placements,
			SparseAfidHoles = list8,
			BlockInfoOnDiskOffset = blockInfoOnDisk,
			MetadataOnDiskOffset = metadataOnDisk,
			CompressedMetadata = compressedMeta,
			MetadataBlocks = metaBlocks,
			DataEndLogical = num10,
			MetaBaseLogical = metaBaseLogical
		};
		static string NormalizeAfidPath(string text3)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(text3, "path");
			string text4 = text3.Trim().Replace('\\', '/');
			if (!text4.StartsWith('/'))
			{
				return "/" + text4;
			}
			return text4;
		}
		static int SystemAfidRank(FileNode file)
		{
			if (IsKeystone(file.FullPath))
			{
				return 0;
			}
			if (file.IsExecutable)
			{
				return 1;
			}
			if (file.FullPath.Equals("/sce_sys/pfs-version.dat", StringComparison.Ordinal))
			{
				return 3;
			}
			return 2;
		}
	}

	private static Dir BuildTree(IReadOnlyList<ProsperoPs5InnerFile> files, IReadOnlyList<ProsperoPs5InnerDirectory>? directories)
	{
		Dir dir = new Dir
		{
			Name = "uroot",
			FullPath = ""
		};
		Dictionary<string, Dir> dirLookup = new Dictionary<string, Dir>(StringComparer.Ordinal) { [""] = dir };
		if (!files.Any((ProsperoPs5InnerFile file) => file.Path.Replace('\\', '/').TrimStart('/').Equals("eboot.bin", StringComparison.Ordinal)))
		{
			GetDir("data");
		}
		if (directories != null)
		{
			foreach (ProsperoPs5InnerDirectory item in directories.OrderBy((ProsperoPs5InnerDirectory directory) => directory.Path, StringComparer.Ordinal))
			{
				ArgumentException.ThrowIfNullOrWhiteSpace(item.Path, "directory.Path");
				string text = item.Path.Replace('\\', '/').Trim('/');
				if (text.Length != 0)
				{
					GetDir(text);
				}
			}
		}
		foreach (ProsperoPs5InnerFile item2 in files.OrderBy((ProsperoPs5InnerFile f) => f.Path, StringComparer.Ordinal))
		{
			string text2 = (item2.Path.StartsWith('/') ? item2.Path.Substring(1) : item2.Path);
			int num = text2.LastIndexOf('/');
			string fullPath = ((num < 0) ? "" : text2.Substring(0, num));
			string name = ((num < 0) ? text2 : text2.Substring(num + 1));
			Dir dir2 = GetDir(fullPath);
			long fileSize = (item2.Data != null) ? item2.Data.LongLength : item2.Size;
			bool isExec = CheckExecutable(item2.OpenStream, item2.Data);
			dir2.Files.Add(new FileNode
			{
				Name = name,
				FullPath = "/" + text2,
				Data = item2.Data,
				Size = fileSize,
				OpenStream = item2.OpenStream ?? (() => new MemoryStream(item2.Data ?? Array.Empty<byte>())),
				IsExecutable = isExec,
				Parent = dir2
			});
		}
		return dir;
		Dir GetDir(string text3)
		{
			if (dirLookup.TryGetValue(text3, out Dir value))
			{
				return value;
			}
			int num2 = text3.LastIndexOf('/');
			string fullPath2 = ((num2 <= 0) ? "" : text3.Substring(0, num2));
			string name2 = text3.Substring(num2 + 1);
			Dir dir3 = GetDir(fullPath2);
			Dir dir4 = new Dir
			{
				Name = name2,
				FullPath = text3,
				Parent = dir3
			};
			dir3.SubDirs.Add(dir4);
			dirLookup[text3] = dir4;
			return dir4;
		}
	}

	private static void CollectDirsPreOrder(Dir dir, List<Dir> outList)
	{
		outList.Add(dir);
		foreach (Dir item in dir.SubDirs.OrderBy((Dir d) => d.Name, StringComparer.Ordinal))
		{
			CollectDirsPreOrder(item, outList);
		}
	}

	private static void CollectDirsPostOrder(Dir dir, List<Dir> outList)
	{
		foreach (Dir item in dir.SubDirs.OrderBy((Dir d) => d.Name, StringComparer.Ordinal))
		{
			CollectDirsPostOrder(item, outList);
		}
		outList.Add(dir);
	}

	private static void CollectFilesPreOrder(Dir dir, List<FileNode> outList)
	{
		foreach (FileNode item in dir.Files.OrderBy((FileNode f) => f.Name, StringComparer.Ordinal))
		{
			outList.Add(item);
		}
		foreach (Dir item2 in dir.SubDirs.OrderBy((Dir d) => d.Name, StringComparer.Ordinal))
		{
			CollectFilesPreOrder(item2, outList);
		}
	}

	private static void CollectFilesEntryOrder(Dir directory, Dir? excludedSubtree, List<FileNode> outList)
	{
		foreach (var item3 in (from child in directory.SubDirs
			where child != excludedSubtree && !IsUnder(child, excludedSubtree)
			select ((string Name, Dir, FileNode))(Name: child.Name, child, null)).Concat(directory.Files.Select((FileNode file) => ((string Name, Dir, FileNode))(Name: file.Name, null, file))).OrderBy(((string Name, Dir, FileNode) entry) => entry.Name, StringComparer.Ordinal))
		{
			Dir item = item3.Item2;
			FileNode item2 = item3.Item3;
			if (item2 != null)
			{
				outList.Add(item2);
			}
			else if (item != null)
			{
				CollectFilesEntryOrder(item, excludedSubtree, outList);
			}
		}
	}

	private static bool IsUnder(Dir dir, Dir? ancestor)
	{
		if (ancestor == null)
		{
			return false;
		}
		for (Dir dir2 = dir; dir2 != null; dir2 = dir2.Parent)
		{
			if (dir2 == ancestor)
			{
				return true;
			}
		}
		return false;
	}

	private static bool IsKeystone(string fullPath)
	{
		return string.Equals(fullPath, "/sce_sys/keystone", StringComparison.Ordinal);
	}

	private static bool CheckExecutable(Func<Stream>? openStream, byte[]? data)
	{
		if (data != null && data.Length >= 4)
		{
			return IsExecutableModule(data);
		}
		if (openStream != null)
		{
			try
			{
				using Stream s = openStream();
				Span<byte> header = stackalloc byte[4];
				int read = s.Read(header);
				if (read == 4)
				{
					uint num = (uint)(header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24));
					return num == 490542415 || num == 1179403647 || num == 4009038932u;
				}
			}
			catch { }
		}
		return false;
	}

	private static bool IsExecutableModule(byte[]? data)
	{
		if (data == null || data.Length < 4)
		{
			return false;
		}
		uint num = (uint)(data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24));
		if (num == 490542415 || num == 1179403647 || num == 4009038932u)
		{
			return true;
		}
		return false;
	}

	private static void BuildDirents(Dir uroot)
	{
		BuildDirentsRecursive(uroot, isUroot: true);
	}

	private static void BuildDirentsRecursive(Dir dir, bool isUroot)
	{
		dir.Dirents.Clear();
		dir.Dirents.Add(new PfsDirent
		{
			Name = ".",
			InodeNumber = dir.Inode,
			Type = DirentType.Dot
		});
		uint inodeNumber = dir.Parent?.Inode ?? dir.Inode;
		dir.Dirents.Add(new PfsDirent
		{
			Name = "..",
			InodeNumber = inodeNumber,
			Type = DirentType.DotDot
		});
		foreach (Dir item in dir.SubDirs.OrderBy((Dir d) => d.Name, StringComparer.Ordinal))
		{
			dir.Dirents.Add(new PfsDirent
			{
				Name = item.Name,
				InodeNumber = item.Inode,
				Type = DirentType.Directory
			});
		}
		foreach (FileNode item2 in dir.Files.OrderBy((FileNode f) => f.Name, StringComparer.Ordinal))
		{
			dir.Dirents.Add(new PfsDirent
			{
				Name = item2.Name,
				InodeNumber = item2.Inode,
				Type = DirentType.File
			});
		}
		int num = 0;
		Dictionary<string, int> dictionary = new Dictionary<string, int>(StringComparer.Ordinal);
		for (int num2 = 0; num2 < dir.Dirents.Count; num2++)
		{
			PfsDirent pfsDirent = dir.Dirents[num2];
			int num3 = num % 65536;
			int num4 = 65536 - num3;
			checked
			{
				if (num3 != 0 && pfsDirent.EntSize > num4)
				{
					if (num2 == 0)
					{
						throw new InvalidDataException("The first directory entry exceeds one block.");
					}
					dir.Dirents[unchecked(num2 - 1)].EntSize = dir.Dirents[num2 - 1].EntSize + num4;
					num += num4;
				}
				dictionary[pfsDirent.Name] = num;
				num += pfsDirent.EntSize;
			}
		}
		foreach (Dir subDir in dir.SubDirs)
		{
			subDir.DirentOffsetInParent = dictionary[subDir.Name];
		}
		foreach (FileNode file in dir.Files)
		{
			file.DirentOffsetInParent = dictionary[file.Name];
		}
		foreach (Dir item3 in dir.SubDirs.OrderBy((Dir d) => d.Name, StringComparer.Ordinal))
		{
			BuildDirentsRecursive(item3, isUroot: false);
		}
	}

	private List<ProsperoPs5MetaNode> BuildNodes(Dir uroot, List<Dir> dirsPreOrder, List<FileNode> fileNodes, long logicalDataBlocks, uint superRootInode, uint inodeFltInode, uint aprFltInode, uint afidTableInode, IReadOnlyList<byte[]> metadataPayloads, out long ndblock, out int trailingMetadataBlocks)
	{
		if (metadataPayloads.Count != 4 + dirsPreOrder.Count)
		{
			throw new InvalidDataException("Inner metadata payload count does not match the filesystem tree.");
		}
		int num = ((IEnumerable<byte[]>)metadataPayloads).Sum((Func<byte[], int>)PayloadBlocks);
		int num2 = checked(4 + dirsPreOrder.Count + fileNodes.Count + 390 - 1) / 390;
		int num3;
		int num4;
		checked
		{
			num3 = 1 + num2 + num;
			num4 = (num3 + 1) & -2;
		}
		trailingMetadataBlocks = num4 - num3;
		long num5 = logicalDataBlocks + 60;
		long num6 = (num5 + 1 + num2) * 65536;
		ndblock = num5 + num4;
		long[] array = new long[metadataPayloads.Count];
		long num7 = num6;
		for (int i = 0; i < metadataPayloads.Count; i++)
		{
			array[i] = num7;
			checked
			{
				num7 += unchecked((long)PayloadBlocks(metadataPayloads[i])) * 65536L;
			}
		}
		List<ProsperoPs5MetaNode> list = new List<ProsperoPs5MetaNode>();
		list.Add(new ProsperoPs5MetaNode
		{
			Name = "",
			Inode = superRootInode,
			IsDirectory = true,
			Mode = 16749,
			Nlink = 1,
			Flags = 131088u,
			Size = (long)PayloadBlocks(metadataPayloads[0]) * 65536L,
			LogicalOffset = (ulong)array[0],
			ParentInode = -1,
			DirentOffset = -1
		});
		list.Add(MetaFileNode(inodeFltInode, (ulong)array[1], metadataPayloads[1].LongLength, 131088u, "inode_flat_path_table"));
		list.Add(MetaFileNode(aprFltInode, (ulong)array[2], metadataPayloads[2].LongLength, 131088u, "apr_flat_path_table"));
		list.Add(MetaFileNode(afidTableInode, (ulong)array[3], metadataPayloads[3].LongLength, 131088u, "afid_to_ino_table"));
		for (int j = 0; j < dirsPreOrder.Count; j++)
		{
			Dir dir = dirsPreOrder[j];
			bool flag = dir == uroot;
			bool flag2 = dir.FullPath.Equals("sce_sys", StringComparison.Ordinal) || dir.FullPath.StartsWith("sce_sys/", StringComparison.Ordinal);
			list.Add(new ProsperoPs5MetaNode
			{
				Name = dir.Name,
				FullPath = dir.FullPath,
				Inode = dir.Inode,
				IsDirectory = true,
				Mode = (ushort)((flag || !flag2) ? 16749u : 16744u),
				Nlink = (ushort)((uint)(2 + dir.SubDirs.Count) + (flag ? 1u : 0u)),
				Flags = ((flag || !flag2) ? 16u : 131088u),
				Size = (long)PayloadBlocks(metadataPayloads[4 + j]) * 65536L,
				LogicalOffset = (ulong)array[4 + j],
				ParentInode = (flag ? (-1) : ((int)dir.Parent.Inode)),
				DirentOffset = (flag ? (-1) : dir.DirentOffsetInParent)
			});
		}
		foreach (FileNode fileNode in fileNodes)
		{
			bool flag3 = fileNode.FullPath.StartsWith("/sce_sys/", StringComparison.Ordinal);
			bool flag4 = fileNode.IsExecutable;
			list.Add(new ProsperoPs5MetaNode
			{
				Name = fileNode.Name,
				FullPath = fileNode.FullPath,
				Inode = fileNode.Inode,
				IsDirectory = false,
				Mode = (ushort)(flag3 ? 33128u : 33133u),
				Nlink = 1,
				Flags = (uint)(0x10 | (flag4 ? 64 : 32) | (flag3 ? 131072 : 0)),
				Afid = fileNode.Afid,
				Size = fileNode.Size,
				LogicalOffset = (ulong)fileNode.LogicalOffset,
				ParentInode = (int)fileNode.Parent.Inode,
				DirentOffset = fileNode.DirentOffsetInParent
			});
		}
		return list.OrderBy((ProsperoPs5MetaNode n) => n.Inode).ToList();
		static int PayloadBlocks(byte[] payload)
		{
			return Math.Max(1, checked(payload.Length + 65536 - 1) / 65536);
		}
	}

	private ProsperoPs5MetaNode MetaFileNode(uint inode, ulong logOff, long size, uint flags, string name = "")
	{
		return new ProsperoPs5MetaNode
		{
			Name = name,
			Inode = inode,
			IsDirectory = false,
			Mode = 33133,
			Nlink = 1,
			Flags = flags,
			Size = size,
			LogicalOffset = logOff,
			ParentInode = -1,
			DirentOffset = -1
		};
	}

	/// <summary>Rounds <paramref name="value" /> up to a multiple of <paramref name="granularity" />.</summary>
	private static long RoundUp(long value, long granularity)
	{
		return (value + granularity - 1) / granularity * granularity;
	}

	private List<byte[]> BuildMetadataPayloads(List<Dir> dirsPreOrder, List<FileNode> fileNodes, IReadOnlyList<FileNode?> afidSlots, uint inodeFltInode, uint aprFltInode, uint afidTableInode, Dir uroot)
	{
		List<ProsperoPs5FlatPathTable.Entry> inodeFlt = new List<ProsperoPs5FlatPathTable.Entry>();
		List<ProsperoPs5FlatPathTable.Entry> aprFlt = new List<ProsperoPs5FlatPathTable.Entry>();
		foreach (Dir item4 in dirsPreOrder)
		{
			if (item4 != uroot)
			{
				bool subtreeApr = !item4.FullPath.Equals("sce_sys", StringComparison.Ordinal) && !item4.FullPath.StartsWith("sce_sys/", StringComparison.Ordinal);
				FI(item4.FullPath, item4.Inode, dir: true, subtreeApr, 0u);
			}
		}
		foreach (FileNode fileNode in fileNodes)
		{
			bool flag = !fileNode.FullPath.StartsWith("/sce_sys/", StringComparison.Ordinal);
			FI(fileNode.FullPath, fileNode.Inode, dir: false, flag, fileNode.Afid);
			if (flag)
			{
				FA(fileNode.FullPath, fileNode.Size, fileNode.Afid);
			}
		}
		byte[] item = ProsperoPs5FlatPathTable.ToBytes(inodeFlt);
		byte[] item2 = ProsperoPs5FlatPathTable.ToBytes(aprFlt);
		List<int> list;
		checked
		{
			int item3 = afidSlots.Count + 2;
			list = new List<int> { item3 };
			foreach (FileNode afidSlot in afidSlots)
			{
				list.Add((afidSlot == null) ? (-1) : ((int)afidSlot.Inode));
			}
			list.Add(-1);
			list.Add(-1);
		}
		byte[] array = new byte[list.Count * 4];
		for (int i = 0; i < list.Count; i++)
		{
			BitConverter.GetBytes(list[i]).CopyTo(array, i * 4);
		}
		List<byte[]> list2 = new List<byte[]>
		{
			DirentBytes(SuperRootDirents(inodeFltInode, aprFltInode, afidTableInode, uroot.Inode)),
			item,
			item2,
			array
		};
		foreach (Dir item5 in dirsPreOrder)
		{
			list2.Add(DirentBytes(item5.Dirents));
		}
		return list2;
		void FA(string path, long size, uint afid)
		{
			aprFlt.Add(new ProsperoPs5FlatPathTable.Entry(ProsperoPs5FlatPathTable.HashPath(path), ProsperoPs5FlatPathTable.PackAprEntry(size, afid)));
		}
		void FI(string path, uint inode, bool dir, bool flag2, uint afid)
		{
			inodeFlt.Add(new ProsperoPs5FlatPathTable.Entry(ProsperoPs5FlatPathTable.HashPath(path), ProsperoPs5FlatPathTable.PackInodeEntry(inode, dir, !flag2, afid)));
		}
	}

	private byte[] BuildMetadataPlaintext(List<ProsperoPs5MetaNode> nodes, List<byte[]> metadataPayloads, long ndblock, int trailingMetadataBlocks)
	{
		if ((trailingMetadataBlocks < 0 || trailingMetadataBlocks > 1) ? true : false)
		{
			throw new InvalidDataException("Publisher metadata padding must be zero or one block.");
		}
		List<byte[]> list = new List<byte[]>(metadataPayloads);
		if (trailingMetadataBlocks != 0)
		{
			list.Add(Array.Empty<byte>());
		}
		return new ProsperoPs5InnerMetadata(_timeSec, _timeNsec).Build(nodes, ndblock, list);
	}

	private static IEnumerable<PfsDirent> SuperRootDirents(uint inodeFlt, uint aprFlt, uint afid, uint uroot)
	{
		return new PfsDirent[4]
		{
			new PfsDirent
			{
				InodeNumber = inodeFlt,
				Name = "inode_flat_path_table",
				Type = DirentType.File
			},
			new PfsDirent
			{
				InodeNumber = aprFlt,
				Name = "apr_flat_path_table",
				Type = DirentType.File
			},
			new PfsDirent
			{
				InodeNumber = afid,
				Name = "afid_to_ino_table",
				Type = DirentType.File
			},
			new PfsDirent
			{
				InodeNumber = uroot,
				Name = "uroot",
				Type = DirentType.Directory
			}
		};
	}

	private static byte[] DirentBytes(IEnumerable<PfsDirent> dirents)
	{
		using MemoryStream memoryStream = new MemoryStream();
		foreach (PfsDirent dirent in dirents)
		{
			dirent.WriteToStream(memoryStream);
		}
		return memoryStream.ToArray();
	}

	private byte[] BuildImage(List<FileNode> afidOrder, byte[] metaPlain, long logicalDataEnd, string? outputPath, long prewrittenDataEnd, out long imageLength, out long blockInfoOnDisk, out long metadataOnDisk, out byte[] compressedMeta, out IReadOnlyList<ProsperoInnerMetaBlockChunk> metaBlocks)
	{
		List<ProsperoPs5InnerPayload> list = new List<ProsperoPs5InnerPayload>();
		long num = ((outputPath == null) ? 0 : prewrittenDataEnd);
		if (outputPath == null)
		{
			foreach (FileNode item in afidOrder)
			{
				bool wholeBlockRaw = item.WholeBlockRaw;
				bool wholeBlockRaw2 = item.WholeBlockRaw;
				ProsperoPs5InnerPayload prosperoPs5InnerPayload = new ProsperoPs5InnerPayload
				{
					Data = item.OnDiskData,
					StoreRaw = true,
					BlockAligned = wholeBlockRaw2,
					BlockAlignedAfter = wholeBlockRaw
				};
				list.Add(prosperoPs5InnerPayload);
				if (wholeBlockRaw2)
				{
					num = AlignUp(num, 65536L);
				}
				item.OnDiskOffset = num;
				item.OnDiskSize = prosperoPs5InnerPayload.Data.LongLength;
				num += prosperoPs5InnerPayload.Data.Length;
				if (wholeBlockRaw)
				{
					num = AlignUp(num, 65536L);
				}
			}
		}
		byte[] array = BuildBlockInfoTable(logicalDataEnd);
		num = (blockInfoOnDisk = AlignUp(num, 65536L)) + array.Length;
		if (outputPath == null)
		{
			list.Add(new ProsperoPs5InnerPayload
			{
				Data = array,
				StoreRaw = true,
				BlockAligned = true
			});
		}
		compressedMeta = ProsperoPs5InnerImageBuilder.CompressPayload(metaPlain, storeRaw: false, out ProsperoCompressedPfsFile compressedFile);
		IReadOnlyList<ProsperoInnerMetaBlockChunk> readOnlyList2;
		if (compressedFile != null)
		{
			IReadOnlyList<ProsperoInnerMetaBlockChunk> readOnlyList = compressedFile.Blocks.Select((ProsperoPfsBlock b) => new ProsperoInnerMetaBlockChunk(b.CompressedSize, b.UncompressedSize, b.IsMultiChunk, b.FirstChunkCompressedSize, b.Flags)).ToList();
			readOnlyList2 = readOnlyList;
		}
		else
		{
			IReadOnlyList<ProsperoInnerMetaBlockChunk> readOnlyList = Array.Empty<ProsperoInnerMetaBlockChunk>();
			readOnlyList2 = readOnlyList;
		}
		metaBlocks = readOnlyList2;
		num = AlignUp(num, 65536L);
		metadataOnDisk = num;
		if (outputPath == null)
		{
			list.Add(new ProsperoPs5InnerPayload
			{
				Data = compressedMeta,
				StoreRaw = true,
				BlockAligned = true
			});
		}
		ProsperoPs5InnerImageBuilder prosperoPs5InnerImageBuilder = new ProsperoPs5InnerImageBuilder();
		checked
		{
			if (outputPath == null)
			{
				byte[] array2 = prosperoPs5InnerImageBuilder.Build(list);
				int num2 = (int)AlignUp(array2.Length, 65536L);
				if (array2.Length != num2)
				{
					Array.Resize(ref array2, num2);
				}
				imageLength = array2.LongLength;
				return array2;
			}
			using (FileStream fileStream = new FileStream(outputPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1048576, FileOptions.SequentialScan))
			{
				fileStream.Position = blockInfoOnDisk;
				fileStream.Write(array);
				fileStream.Position = metadataOnDisk;
				fileStream.Write(compressedMeta);
				long v = metadataOnDisk + compressedMeta.LongLength;
				imageLength = AlignUp(v, 65536L);
				fileStream.SetLength(imageLength);
				fileStream.Flush(flushToDisk: true);
			}
			return Array.Empty<byte>();
		}
		static long AlignUp(long num3, long a)
		{
			return (num3 + a - 1) & ~(a - 1);
		}
	}

	/// <summary>
	/// Builds the 256-byte inner block-75 table that precedes the compressed metadata block.
	/// Each 8-byte entry is <c>{ u32 value, u32 version }</c> (little-endian): the first 31
	/// entries carry <see cref="F:LibProsperoPkg.PFS.ProsperoPs5InnerImageAssembler.BlockInfoTemplate" />; the final entry is derived from the
	/// logical end of all afid files.
	/// <para>The final entry, read as the three displayed bytes, equals
	/// <c>0x27FFFC − 4·(afidDataEnd mod 0x10000)</c>. For the publisher baseline
	/// <c>afidDataEnd=0xA</c> this produces <c>27 FF D4</c>; for the compressed AC sample
	/// <c>afidDataEnd mod 0x10000=0x8C</c> it produces <c>27 FD CC</c>. A larger
	/// <c>0x2B482</c> tail uses <c>0xB482</c> and produces <c>25 2D F4</c>.</para>
	/// </summary>
	/// <param name="afidDataEnd">Logical end of all afid file data, including sce_sys files.</param>
	private static byte[] BuildBlockInfoTable(long afidDataEnd)
	{
		uint num = checked((uint)((afidDataEnd & 0xFFFF) * 4));
		uint num2 = (2621436 - num) & 0xFFFFFF;
		uint entryValue = ((num2 & 0xFF) << 16) | (num2 & 0xFF00) | ((num2 >> 16) & 0xFF);
		byte[] array = new byte[256];
		for (int i = 0; i < 31; i++)
		{
			WriteBlockInfoEntry(array, i * 8, 16580391u);
		}
		WriteBlockInfoEntry(array, 248, entryValue);
		return array;
		static void WriteBlockInfoEntry(byte[] buffer, int offset, uint value)
		{
			BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), value);
			BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 4), 4194307u);
		}
	}
}
