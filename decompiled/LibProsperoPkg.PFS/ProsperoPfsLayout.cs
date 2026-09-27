using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using LibProsperoPkg.PFS.Compression;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PFS;

/// <summary>
/// Inner-PFS layout generator for PS5. See the file header for the scheme.
/// </summary>
public static class ProsperoPfsLayout
{
	private sealed class TemporaryFileSet : IDisposable
	{
		private readonly List<string> _paths = new List<string>();

		public void Add(string path)
		{
			_paths.Add(path);
		}

		public void Dispose()
		{
			foreach (string path in _paths)
			{
				TryDelete(path);
			}
		}
	}

	private sealed class TemporaryDirectory(string path) : IDisposable
	{
		public void Dispose()
		{
			TryDeleteDirectory(path);
		}
	}

	private sealed class ComparisonWriteStream(Stream expected, long expectedLength) : Stream
	{
		private readonly byte[] _buffer = new byte[1048576];

		private long _written;

		private bool _matches = true;

		public bool IsComplete
		{
			get
			{
				if (_matches)
				{
					return _written == expectedLength;
				}
				return false;
			}
		}

		public override bool CanRead => false;

		public override bool CanSeek => false;

		public override bool CanWrite => true;

		public override long Length => _written;

		public override long Position
		{
			get
			{
				return _written;
			}
			set
			{
				throw new NotSupportedException();
			}
		}

		public override void Flush()
		{
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			throw new NotSupportedException();
		}

		public override long Seek(long offset, SeekOrigin origin)
		{
			throw new NotSupportedException();
		}

		public override void SetLength(long value)
		{
			throw new NotSupportedException();
		}

		public override void Write(byte[] buffer, int offset, int count)
		{
			Write(buffer.AsSpan(offset, count));
		}

		public override void Write(ReadOnlySpan<byte> buffer)
		{
			if (_written > expectedLength - buffer.Length)
			{
				_matches = false;
				_written += buffer.Length;
				return;
			}
			int num;
			for (int i = 0; i < buffer.Length; i += num)
			{
				num = Math.Min(_buffer.Length, buffer.Length - i);
				ReadExact(expected, _buffer, num);
				if (!buffer.Slice(i, num).SequenceEqual(_buffer.AsSpan(0, num)))
				{
					_matches = false;
				}
			}
			_written += buffer.Length;
		}
	}

	/// <summary>
	/// Builds a plaintext inner-PFS image from <paramref name="sourceFolder" /> and writes it to
	/// <paramref name="outputPath" />. The result is unsigned and unencrypted; apply
	/// <see cref="T:LibProsperoPkg.PFS.ProsperoPfsImage" /> (AES-XTS) and/or <see cref="T:LibProsperoPkg.PFS.ProsperoPfsc" /> (compression)
	/// afterwards for the encrypted/compressed forms.
	/// </summary>
	/// <param name="sourceFolder">A prepared application folder (its tree becomes the image's uroot).</param>
	/// <param name="outputPath">Destination plaintext PFS image path.</param>
	/// <param name="options">Layout options. <c>null</c> uses the PS5 defaults.</param>
	/// <param name="logger">Optional progress sink.</param>
	public static ProsperoPfsLayoutResult BuildFromFolder(string sourceFolder, string outputPath, ProsperoPfsLayoutOptions? options = null, Action<string>? logger = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourceFolder, "sourceFolder");
		ArgumentException.ThrowIfNullOrWhiteSpace(outputPath, "outputPath");
		if (!Directory.Exists(sourceFolder))
		{
			throw new DirectoryNotFoundException("Source folder does not exist: " + sourceFolder);
		}
		if (options == null)
		{
			options = new ProsperoPfsLayoutOptions();
		}
		PfsFileCompressionMethod compression = ResolveCompression(options);
		ValidateOptions(options, compression);
		Action<string> log = logger ?? ((Action<string>)((string _) =>
		{
		}));
		string fullPath = Path.GetFullPath(sourceFolder);
		string fullPath2 = Path.GetFullPath(outputPath);
		string? obj = Path.GetDirectoryName(fullPath2) ?? Directory.GetCurrentDirectory();
		Directory.CreateDirectory(obj);
		string text = Path.Combine(obj, ".libprospero-build-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(text);
		using (new TemporaryDirectory(text))
		{
			string text2 = Path.Combine(text, "image.pfs.tmp");
			long num = 2L;
			string value = (options.UsePublisherPprLayout ? "ppr" : "classic");
			log($"Laying out the {value} PFS image (superblock version {num}, block size 0x{options.BlockSize:X}, compression {compression.ToString().ToLowerInvariant()}, level {options.CompressionLevel})...");
			using TemporaryFileSet temporaryFiles = new TemporaryFileSet();
			FSDir root = BuildTree(fullPath, options, temporaryFiles, log, text, fullPath2, out var fileCount, out var dirCount, out var compressedFileCount);
			log($"Filesystem tree: {dirCount} directories, {fileCount} files, {compressedFileCount} {compression.ToString().ToLowerInvariant()}-compressed files.");
			PfsBuilder pfsBuilder = new PfsBuilder(new PfsProperties
			{
				root = root,
				BlockSize = options.BlockSize,
				Version = num,
				Encrypt = false,
				Sign = false,
				FileTime = ToUnixSeconds(options.TimeStamp),
				DirectRootLayout = options.UsePublisherPprLayout,
				FilterOuterPackageEntries = options.FilterOuterPackageEntries,
				OptimizeFileLayoutForReadSpeed = options.OptimizeFileLayoutForReadSpeed
			}, (string s) =>
			{
				log(s);
			});
			long num2 = pfsBuilder.CalculatePfsSize();
			using (FileStream fileStream = new FileStream(text2, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1048576, FileOptions.SequentialScan))
			{
				pfsBuilder.WriteImage(fileStream);
				if (fileStream.Length < num2)
				{
					fileStream.SetLength(num2);
				}
				else if (fileStream.Length != num2)
				{
					throw new InvalidDataException($"PFS writer exceeded its calculated image size ({fileStream.Length:N0} > {num2:N0}).");
				}
			}
			File.Move(text2, fullPath2, overwrite: true);
			long length = new FileInfo(fullPath2).Length;
			log($"Done: {Path.GetFileName(fullPath2)} ({length:N0} bytes).");
			return new ProsperoPfsLayoutResult
			{
				OutputPath = fullPath2,
				ImageSize = length,
				BlockSize = options.BlockSize,
				Version = num,
				FileCount = fileCount,
				DirectoryCount = dirCount
			};
		}
	}

	/// <summary>
	/// Proves a freshly built plaintext layout is self-consistent: builds the image from
	/// <paramref name="sourceFolder" />, reads it back with <see cref="T:LibProsperoPkg.PFS.PfsReader" /> and verifies
	/// every source file is present with byte-identical content (and the superblock version
	/// matches the requested profile). This is the self-check that replaces on-hardware
	/// testing for the layout step.
	/// </summary>
	public static bool VerifyRoundTrip(string sourceFolder, ProsperoPfsLayoutOptions? options = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourceFolder, "sourceFolder");
		if (options == null)
		{
			options = new ProsperoPfsLayoutOptions();
		}
		string tempFileName = Path.GetTempFileName();
		try
		{
			ProsperoPfsLayoutResult prosperoPfsLayoutResult = BuildFromFolder(sourceFolder, tempFileName, options);
			using MemoryMappedFile memoryMappedFile = MemoryMappedFile.CreateFromFile(tempFileName, FileMode.Open, null, 0L, MemoryMappedFileAccess.Read);
			using MemoryMappedViewAccessor r = memoryMappedFile.CreateViewAccessor(0L, 0L, MemoryMappedFileAccess.Read);
			PfsReader pfsReader = new PfsReader(r, 0uL);
			if (pfsReader.Header.Version != prosperoPfsLayoutResult.Version)
			{
				return false;
			}
			foreach (var item3 in EnumerateSourceFiles(Path.GetFullPath(sourceFolder), options))
			{
				string item = item3.Relative;
				string item2 = item3.Full;
				PfsReader.File file = pfsReader.GetFile(item);
				if (file == null)
				{
					return false;
				}
				if (!FileMatchesNode(item2, file))
				{
					return false;
				}
			}
			return true;
		}
		catch
		{
			return false;
		}
		finally
		{
			TryDelete(tempFileName);
		}
	}

	private static FSDir BuildTree(string sourceFolder, ProsperoPfsLayoutOptions options, TemporaryFileSet temporaryFiles, Action<string> log, string buildWorkspace, string outputFullPath, out int fileCount, out int dirCount, out int compressedFileCount)
	{
		int files = 0;
		int dirs = 0;
		int compressed = 0;
		object resultLock = new object();
		Regex[] readPriorityMatchers = CompilePathGlobs(options.ReadPriorityPatterns);
		Regex[] compressionExcludeMatchers = CompilePathGlobs(options.KrakenExcludePatterns);
		PfsFileCompressionMethod selectedCompression = ResolveCompression(options);
		int num = selectedCompression switch
		{
			PfsFileCompressionMethod.Zlib => (options.ZlibMaxDegreeOfParallelism == 0) ? Math.Max(1, Environment.ProcessorCount) : options.ZlibMaxDegreeOfParallelism, 
			PfsFileCompressionMethod.Kraken => (options.KrakenMaxDegreeOfParallelism == 0) ? Math.Max(1, Environment.ProcessorCount) : options.KrakenMaxDegreeOfParallelism, 
			_ => 1, 
		};
		List<(FSDir Parent, int Index, string Path, string Name)> pendingCompressionFiles = new List<(FSDir, int, string, string)>();
		FSDir fSDir = new FSDir();
		Populate(fSDir, sourceFolder);
		if (pendingCompressionFiles.Count != 0)
		{
			FSFile[] results = new FSFile[pendingCompressionFiles.Count];
			int maxDegreeOfParallelism = Math.Min(num, pendingCompressionFiles.Count);
			int blockParallelism = ((pendingCompressionFiles.Count != 1) ? 1 : num);
			Parallel.For(0, pendingCompressionFiles.Count, new ParallelOptions
			{
				MaxDegreeOfParallelism = maxDegreeOfParallelism
			}, (int index) =>
			{
				(FSDir, int, string, string) tuple2 = pendingCompressionFiles[index];
				results[index] = BuildFile(tuple2.Item3, tuple2.Item4, tuple2.Item1, blockParallelism);
			});
			for (int num2 = 0; num2 < pendingCompressionFiles.Count; num2++)
			{
				(FSDir, int, string, string) tuple = pendingCompressionFiles[num2];
				tuple.Item1.Files[tuple.Item2] = results[num2];
			}
		}
		fileCount = files;
		dirCount = dirs;
		compressedFileCount = compressed;
		return fSDir;
		FSFile BuildFile(string path, string name, FSDir parent, int codecBlockParallelism)
		{
			FileInfo fileInfo = new FileInfo(path);
			string text = Path.GetRelativePath(sourceFolder, path).Replace('\\', '/');
			int layoutPriority = (options.OptimizeFileLayoutForReadSpeed ? ((!MatchesPathGlob(text, readPriorityMatchers)) ? ((options.ReadPrioritySmallFileSize > 0 && fileInfo.Length <= options.ReadPrioritySmallFileSize) ? 1 : 2) : 0) : 0);
			PfsFileCompressionMethod pfsFileCompressionMethod = selectedCompression;
			if (pfsFileCompressionMethod == PfsFileCompressionMethod.None || fileInfo.Length < options.MinimumKrakenFileSize || (pfsFileCompressionMethod == PfsFileCompressionMethod.Zlib && options.ZlibFileSelection == PfsZlibFileSelection.SizeMultipleOf64KiB && fileInfo.Length % 65536 != 0L) || MatchesPathGlob(text, compressionExcludeMatchers))
			{
				return new FSFile(path)
				{
					name = name,
					Parent = parent,
					LayoutPriority = layoutPriority
				};
			}
			string text2 = Path.Combine(buildWorkspace, "pfsc2_" + Guid.NewGuid().ToString("N") + ".tmp");
			int num3 = 0;
			int num4 = 0;
			long num5;
			int value;
			long value2;
			bool pprKrakenCompression;
			checked
			{
				if (pfsFileCompressionMethod == PfsFileCompressionMethod.Kraken)
				{
					PprPfsKrakenWriteResult pprPfsKrakenWriteResult = PprPfsKraken.PackFile(path, text2, new PprPfsKrakenWriteOptions
					{
						Level = options.CompressionLevel,
						MinimumSavingsPercent = options.KrakenMinimumSavingsPercent,
						RawRanges = (options.KrakenRawRangeProvider?.Invoke(text) ?? Array.Empty<PprPfsRawRange>()),
						MaxDegreeOfParallelism = codecBlockParallelism
					});
					if (pprPfsKrakenWriteResult.UncompressedSize != fileInfo.Length)
					{
						throw new IOException("Source file changed size while being compressed: " + path);
					}
					num5 = pprPfsKrakenWriteResult.StoredSize;
					value = pprPfsKrakenWriteResult.CompressedBlockCount;
					num3 = pprPfsKrakenWriteResult.ForcedRawBlockCount;
					num4 = pprPfsKrakenWriteResult.LowGainRawBlockCount;
					value2 = pprPfsKrakenWriteResult.BlockCount;
					pprKrakenCompression = true;
				}
				else
				{
					PfscEncodeStats pfscEncodeStats;
					using (FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.SequentialScan))
					{
						using FileStream output = new FileStream(text2, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1048576, FileOptions.SequentialScan);
						if (fileStream.Length != fileInfo.Length)
						{
							throw new IOException("Source file changed size while being compressed: " + path);
						}
						pfscEncodeStats = PfscEncoder.Encode(fileStream, fileInfo.Length, output, new PfscEncoderOptions
						{
							BlockSize = (int)options.BlockSize,
							ZlibLevel = options.CompressionLevel,
							MaxDegreeOfParallelism = codecBlockParallelism
						});
					}
					if (pfscEncodeStats.StoredRaw)
					{
						TryDelete(text2);
						return new FSFile(path)
						{
							name = name,
							Parent = parent,
							LayoutPriority = layoutPriority
						};
					}
					num5 = pfscEncodeStats.EncodedSize;
					value = (int)pfscEncodeStats.CompressedBlocks;
					value2 = pfscEncodeStats.BlockCount;
					pprKrakenCompression = false;
				}
				if (options.KrakenOnlyWhenSmaller && num5 >= fileInfo.Length)
				{
					TryDelete(text2);
					return new FSFile(path)
					{
						name = name,
						Parent = parent,
						LayoutPriority = layoutPriority
					};
				}
			}
			lock (resultLock)
			{
				temporaryFiles.Add(text2);
				compressed++;
				log($"{pfsFileCompressionMethod}: {Path.GetRelativePath(sourceFolder, path)} {fileInfo.Length:N0} -> {num5:N0} bytes ({value}/{value2} blocks" + ((num3 > 0) ? $", forced-raw {num3}" : string.Empty) + ((num4 > 0) ? $", low-gain-raw {num4}" : string.Empty) + ").");
			}
			string storedPath = text2;
			return new FSFile((Stream destination) =>
			{
				using FileStream fileStream2 = new FileStream(storedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.SequentialScan);
				fileStream2.CopyTo(destination, 1048576);
			}, name, num5, fileInfo.Length, compress: true)
			{
				Parent = parent,
				PprKrakenCompression = pprKrakenCompression,
				LayoutPriority = layoutPriority
			};
		}
		void Populate(FSDir node, string path)
		{
			foreach (string item in Directory.EnumerateDirectories(path).OrderBy(Path.GetFileName, StringComparer.Ordinal))
			{
				if (!PathsEqual(item, buildWorkspace))
				{
					FSDir fSDir2 = new FSDir
					{
						name = Path.GetFileName(item),
						Parent = node
					};
					node.Dirs.Add(fSDir2);
					dirs++;
					Populate(fSDir2, item);
				}
			}
			foreach (string item2 in Directory.EnumerateFiles(path).OrderBy(Path.GetFileName, StringComparer.Ordinal))
			{
				if (!PathsEqual(item2, outputFullPath))
				{
					string fileName = Path.GetFileName(item2);
					if (!IsExcluded(fileName, options))
					{
						PfsFileCompressionMethod pfsFileCompressionMethod = selectedCompression;
						if ((uint)(pfsFileCompressionMethod - 1) <= 1u)
						{
							int count = node.Files.Count;
							node.Files.Add(new FSFile(item2)
							{
								name = fileName,
								Parent = node
							});
							pendingCompressionFiles.Add((node, count, item2, fileName));
						}
						else
						{
							node.Files.Add(BuildFile(item2, fileName, node, 1));
						}
						files++;
					}
				}
			}
		}
	}

	private static PfsFileCompressionMethod ResolveCompression(ProsperoPfsLayoutOptions options)
	{
		if (options.FileCompression == PfsFileCompressionMethod.None)
		{
			if (!options.CompressFilesWithKraken)
			{
				return PfsFileCompressionMethod.None;
			}
			return PfsFileCompressionMethod.Kraken;
		}
		return options.FileCompression;
	}

	private static void ValidateOptions(ProsperoPfsLayoutOptions options, PfsFileCompressionMethod compression)
	{
		ArgumentNullException.ThrowIfNull(options.KrakenExcludePatterns, "options.KrakenExcludePatterns");
		ArgumentNullException.ThrowIfNull(options.ReadPriorityPatterns, "options.ReadPriorityPatterns");
		ArgumentNullException.ThrowIfNull(options.ExcludeFileNames, "options.ExcludeFileNames");
		ArgumentNullException.ThrowIfNull(options.ExcludeFileSuffixes, "options.ExcludeFileSuffixes");
		uint blockSize = options.BlockSize;
		bool flag = ((blockSize < 4096 || blockSize > 2097152) ? true : false);
		if (flag || (options.BlockSize & (options.BlockSize - 1)) != 0)
		{
			throw new ArgumentOutOfRangeException("BlockSize", "PFS block size must be a power of two from 0x1000 through 0x200000.");
		}
		if (options.MinimumKrakenFileSize < 0)
		{
			throw new ArgumentOutOfRangeException("MinimumKrakenFileSize");
		}
		int krakenMinimumSavingsPercent = options.KrakenMinimumSavingsPercent;
		if ((krakenMinimumSavingsPercent < 0 || krakenMinimumSavingsPercent > 100) ? true : false)
		{
			throw new ArgumentOutOfRangeException("KrakenMinimumSavingsPercent");
		}
		if (options.ReadPrioritySmallFileSize < 0)
		{
			throw new ArgumentOutOfRangeException("ReadPrioritySmallFileSize");
		}
		flag = compression == PfsFileCompressionMethod.Kraken;
		if (flag)
		{
			krakenMinimumSavingsPercent = options.CompressionLevel;
			bool flag2 = ((krakenMinimumSavingsPercent < -4 || krakenMinimumSavingsPercent > 9) ? true : false);
			flag = flag2;
		}
		if (flag)
		{
			throw new ArgumentOutOfRangeException("CompressionLevel", "Kraken level must be in the range -4..9.");
		}
		flag = compression == PfsFileCompressionMethod.Zlib;
		if (flag)
		{
			krakenMinimumSavingsPercent = options.CompressionLevel;
			bool flag2 = ((krakenMinimumSavingsPercent < 0 || krakenMinimumSavingsPercent > 9) ? true : false);
			flag = flag2;
		}
		if (flag)
		{
			throw new ArgumentOutOfRangeException("CompressionLevel", "Zlib level must be in the range 0..9.");
		}
		if (options.ZlibMaxDegreeOfParallelism < 0)
		{
			throw new ArgumentOutOfRangeException("ZlibMaxDegreeOfParallelism", "Zlib parallelism must be zero (automatic) or positive.");
		}
		if (options.KrakenMaxDegreeOfParallelism < 0)
		{
			throw new ArgumentOutOfRangeException("KrakenMaxDegreeOfParallelism", "Kraken parallelism must be zero (automatic) or positive.");
		}
		if (!Enum.IsDefined(options.ZlibFileSelection))
		{
			throw new ArgumentOutOfRangeException("ZlibFileSelection");
		}
		if (options.UsePublisherPprLayout && compression == PfsFileCompressionMethod.Zlib)
		{
			throw new NotSupportedException("The publisher direct-root runtime format uses PFSv2/v3 Kraken, not the distinct classic PFSC/zlib container. Use --layout classic with zlib, or select kraken/none.");
		}
	}

	private static Regex[] CompilePathGlobs(IReadOnlyCollection<string> patterns)
	{
		return patterns.Select((string patternValue) =>
		{
			string str = patternValue.Replace('\\', '/').TrimStart('/');
			return new Regex("^" + Regex.Escape(str).Replace("\\*\\*", ".*").Replace("\\*", "[^/]*")
				.Replace("\\?", "[^/]") + "$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
		}).ToArray();
	}

	private static bool MatchesPathGlob(string relativePath, IReadOnlyList<Regex> matchers)
	{
		return matchers.Any((Regex matcher) => matcher.IsMatch(relativePath));
	}

	private static IEnumerable<(string Relative, string Full)> EnumerateSourceFiles(string sourceFolder, ProsperoPfsLayoutOptions options)
	{
		foreach (string item2 in Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories))
		{
			if (!IsExcluded(Path.GetFileName(item2), options))
			{
				string item = Path.GetRelativePath(sourceFolder, item2).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
				yield return (Relative: item, Full: item2);
			}
		}
	}

	private static bool IsExcluded(string name, ProsperoPfsLayoutOptions options)
	{
		if (options.ExcludeFileNames.Any((string excluded) => string.Equals(excluded, name, StringComparison.OrdinalIgnoreCase)))
		{
			return true;
		}
		foreach (string excludeFileSuffix in options.ExcludeFileSuffixes)
		{
			if (name.EndsWith(excludeFileSuffix, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	private static bool FileMatchesNode(string fullPath, PfsReader.File node)
	{
		FileInfo fileInfo = new FileInfo(fullPath);
		long num = (node.flags.HasFlag(InodeFlags.compressed) ? node.compressed_size : node.size);
		if (fileInfo.Length != num)
		{
			return false;
		}
		IMemoryReader view = node.GetView();
		using FileStream fileStream = File.OpenRead(fullPath);
		if (node.flags.HasFlag(InodeFlags.compressed))
		{
			byte[] array = new byte[40];
			view.Read(0L, array, 0, array.Length);
			if (BitConverter.ToUInt32(array, 0) != 1129530960)
			{
				return false;
			}
			if (BitConverter.ToUInt32(array, 4) == 2)
			{
				long size = checked((long)BitConverter.ToUInt64(array, 32));
				using StreamWrapper source = new StreamWrapper(view, size);
				using ComparisonWriteStream comparisonWriteStream = new ComparisonWriteStream(fileStream, fileInfo.Length);
				PprPfsKraken.Unpack(source, 0L, comparisonWriteStream);
				return comparisonWriteStream.IsComplete;
			}
			using PFSCReader pFSCReader = new PFSCReader(view);
			byte[] array2 = new byte[65536];
			byte[] array3 = new byte[array2.Length];
			int num3;
			for (long num2 = 0L; num2 < fileInfo.Length; num2 += num3)
			{
				num3 = (int)Math.Min(array2.Length, fileInfo.Length - num2);
				pFSCReader.Read(num2, array2, 0, num3);
				ReadExact(fileStream, array3, num3);
				if (!array2.AsSpan(0, num3).SequenceEqual(array3.AsSpan(0, num3)))
				{
					return false;
				}
			}
			return true;
		}
		using StreamWrapper actual = new StreamWrapper(view, node.size);
		return StreamsMatch(fileStream, actual, fileInfo.Length);
	}

	private static bool StreamsMatch(Stream expected, Stream actual, long length)
	{
		byte[] array = new byte[65536];
		byte[] array2 = new byte[65536];
		long num = length;
		while (num > 0)
		{
			int num2 = (int)Math.Min(array.Length, num);
			ReadExact(expected, array, num2);
			ReadExact(actual, array2, num2);
			if (!array.AsSpan(0, num2).SequenceEqual(array2.AsSpan(0, num2)))
			{
				return false;
			}
			num -= num2;
		}
		return true;
	}

	private static void ReadExact(Stream s, byte[] buffer, int count)
	{
		int num;
		for (int i = 0; i < count; i += num)
		{
			num = s.Read(buffer, i, count - i);
			if (num == 0)
			{
				throw new EndOfStreamException("Unexpected end of stream while comparing PFS file data.");
			}
		}
	}

	private static long ToUnixSeconds(DateTime time)
	{
		return (long)time.ToUniversalTime().Subtract(DateTime.UnixEpoch).TotalSeconds;
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

	private static bool PathsEqual(string left, string right)
	{
		return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
	}

	private static void TryDeleteDirectory(string path)
	{
		try
		{
			if (Directory.Exists(path))
			{
				Directory.Delete(path, recursive: true);
			}
		}
		catch
		{
		}
	}
}
