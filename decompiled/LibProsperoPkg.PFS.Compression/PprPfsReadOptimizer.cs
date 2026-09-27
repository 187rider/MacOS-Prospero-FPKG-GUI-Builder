using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text.RegularExpressions;

namespace LibProsperoPkg.PFS.Compression;

/// <summary>Builds a group-aligned fast-read plan from an existing plaintext PFS image.</summary>
public static class PprPfsReadOptimizer
{
	/// <summary>Inspect file extents and return 256 KiB groups that should bypass Kraken.</summary>
	public static PprPfsReadOptimizationPlan AnalyzePfsImage(string imagePath, PprPfsReadOptimizationOptions? options = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(imagePath, "imagePath");
		if (options == null)
		{
			options = new PprPfsReadOptimizationOptions();
		}
		if (options.SmallFileRawThreshold < 0)
		{
			throw new ArgumentOutOfRangeException("SmallFileRawThreshold");
		}
		FileInfo fileInfo = new FileInfo(imagePath);
		if (!fileInfo.Exists)
		{
			throw new FileNotFoundException("Inner PFS image was not found.", imagePath);
		}
		if (fileInfo.Length == 0L)
		{
			throw new InvalidDataException("Inner PFS image is empty.");
		}
		if (options.ForceAllRaw)
		{
			return CreatePlan(new PprPfsRawRange[1]
			{
				new PprPfsRawRange(0L, fileInfo.Length)
			}, fileInfo.Length, 0, fileInfo.Length);
		}
		Regex[] array = CompilePathGlobs(options.RawFilePatterns);
		if (!options.KeepMetadataRaw && options.SmallFileRawThreshold == 0L && array.Length == 0)
		{
			return CreatePlan(Array.Empty<PprPfsRawRange>(), fileInfo.Length, 0, 0L);
		}
		using MemoryMappedFile memoryMappedFile = MemoryMappedFile.CreateFromFile(imagePath, FileMode.Open, null, 0L, MemoryMappedFileAccess.Read);
		using MemoryMappedViewAccessor r = memoryMappedFile.CreateViewAccessor(0L, 0L, MemoryMappedFileAccess.Read);
		PfsReader pfsReader = new PfsReader(r, 0uL);
		PfsReader.File[] array2 = pfsReader.GetAllFiles().ToArray();
		PfsReader.File[] array3 = array2;
		foreach (PfsReader.File file in array3)
		{
			if (file.offset < 0 || file.offset > fileInfo.Length || file.size < 0 || (file.offset == fileInfo.Length && file.size != 0L))
			{
				throw new InvalidDataException("Inner PFS file '" + file.FullName + "' has an invalid extent.");
			}
		}
		List<PprPfsRawRange> list = new List<PprPfsRawRange>();
		long num = 0L;
		if (options.KeepMetadataRaw)
		{
			num = ((array2.Length == 0) ? fileInfo.Length : Math.Clamp(array2.Min((PfsReader.File file3) => file3.offset), 0L, fileInfo.Length));
			if (num > 0)
			{
				list.Add(new PprPfsRawRange(0L, num));
			}
		}
		int num2 = 0;
		array3 = array2;
		foreach (PfsReader.File file2 in array3)
		{
			string relativePath = ImageRelativePath(file2.FullName);
			if ((options.SmallFileRawThreshold > 0 && file2.size <= options.SmallFileRawThreshold) || MatchesAnyPattern(relativePath, array))
			{
				num2++;
				AddFileRanges(list, file2, pfsReader.Header.BlockSize, fileInfo.Length);
			}
		}
		return CreatePlan(list, fileInfo.Length, num2, num);
	}

	private static void AddFileRanges(List<PprPfsRawRange> ranges, PfsReader.File file, uint blockSize, long imageLength)
	{
		if (blockSize == 0)
		{
			throw new InvalidDataException("Inner PFS has a zero filesystem block size.");
		}
		long num = Math.Min(imageLength, Align(Math.Max(file.size, 1L), checked((int)blockSize)));
		int[] blocks = file.blocks;
		if (blocks != null && blocks.Length > 0)
		{
			long num2 = num;
			blocks = file.blocks;
			foreach (int num3 in blocks)
			{
				if (num2 > 0)
				{
					long num4;
					checked
					{
						if (num3 < 0 || num3 * blockSize >= imageLength)
						{
							throw new InvalidDataException("Inner PFS file '" + file.FullName + "' has an invalid block map.");
						}
						num4 = Math.Min(blockSize, num2);
						ranges.Add(new PprPfsRawRange(num3 * blockSize, num4));
					}
					num2 -= num4;
					continue;
				}
				break;
			}
		}
		else
		{
			long num5 = Math.Min(num, imageLength - file.offset);
			if (num5 > 0)
			{
				ranges.Add(new PprPfsRawRange(file.offset, num5));
			}
		}
	}

	private static PprPfsReadOptimizationPlan CreatePlan(IEnumerable<PprPfsRawRange> ranges, long imageLength, int rawFiles, long metadataPrefix)
	{
		PprPfsRawRange[] array = MergeGroupAlignedRanges(ranges, imageLength);
		long rawLogicalBytes = array.Sum((PprPfsRawRange range) => range.Length);
		checked
		{
			int rawGroupCount = (int)array.Sum((PprPfsRawRange range) => unchecked(checked(range.Length + 262144 - 1) / 262144));
			return new PprPfsReadOptimizationPlan
			{
				RawRanges = array,
				RawLogicalBytes = rawLogicalBytes,
				RawGroupCount = rawGroupCount,
				RawFileCount = rawFiles,
				MetadataPrefixBytes = metadataPrefix
			};
		}
	}

	private static PprPfsRawRange[] MergeGroupAlignedRanges(IEnumerable<PprPfsRawRange> ranges, long imageLength)
	{
		List<PprPfsRawRange> list = new List<PprPfsRawRange>();
		foreach (PprPfsRawRange range in ranges)
		{
			if (range.Offset < 0 || range.Length < 0)
			{
				throw new InvalidDataException("Read-optimization ranges cannot be negative.");
			}
			if (range.Length != 0L && range.Offset < imageLength)
			{
				long num = range.Offset / 262144 * 262144;
				long num2 = Math.Min(imageLength, Align(checked(range.Offset + range.Length), 262144));
				list.Add(new PprPfsRawRange(num, num2 - num));
			}
		}
		list.Sort((PprPfsRawRange left, PprPfsRawRange right) => left.Offset.CompareTo(right.Offset));
		List<PprPfsRawRange> list2 = new List<PprPfsRawRange>(list.Count);
		foreach (PprPfsRawRange item in list)
		{
			if (list2.Count == 0)
			{
				list2.Add(item);
				continue;
			}
			PprPfsRawRange pprPfsRawRange = list2[list2.Count - 1];
			long num3;
			long val;
			checked
			{
				num3 = pprPfsRawRange.Offset + pprPfsRawRange.Length;
				val = item.Offset + item.Length;
			}
			if (item.Offset <= num3)
			{
				list2[list2.Count - 1] = new PprPfsRawRange(pprPfsRawRange.Offset, Math.Max(num3, val) - pprPfsRawRange.Offset);
			}
			else
			{
				list2.Add(item);
			}
		}
		return list2.ToArray();
	}

	private static Regex[] CompilePathGlobs(IReadOnlyCollection<string>? patterns)
	{
		if (patterns == null)
		{
			return Array.Empty<Regex>();
		}
		return patterns.Select((string patternValue) =>
		{
			string str = patternValue.Replace('\\', '/').TrimStart('/');
			return new Regex("^" + Regex.Escape(str).Replace("\\*\\*", ".*").Replace("\\*", "[^/]*")
				.Replace("\\?", "[^/]") + "$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
		}).ToArray();
	}

	private static bool MatchesAnyPattern(string relativePath, IReadOnlyList<Regex> patterns)
	{
		return patterns.Any((Regex pattern) => pattern.IsMatch(relativePath));
	}

	private static string ImageRelativePath(string fullName)
	{
		string text = fullName.TrimStart('/');
		if (!text.StartsWith("uroot/", StringComparison.OrdinalIgnoreCase))
		{
			return text;
		}
		return text.Substring(6);
	}

	private static long Align(long value, int alignment)
	{
		checked
		{
			return unchecked(checked(value + alignment - 1) / alignment) * alignment;
		}
	}
}
