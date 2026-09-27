using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LibProsperoPkg.PFS;

/// <summary>Reads and writes exact path-to-AFID assignments for publisher PPR-PFS images.</summary>
public static class ProsperoAfidMap
{
	/// <summary>
	/// Extracts every real AFID slot from an already-decrypted PPR-PFS image. Empty <c>-1</c>
	/// slots are preserved implicitly by gaps between the returned numeric assignments.
	/// </summary>
	public static IReadOnlyDictionary<string, uint> FromPfs(PfsReader reader)
	{
		ArgumentNullException.ThrowIfNull(reader, "reader");
		byte[] array = ((reader.GetSuperRoot().Get("afid_to_ino_table") as PfsReader.File) ?? throw new InvalidDataException("The PFS super-root has no afid_to_ino_table file.")).ReadAllBytes();
		if (array.Length < 12 || array.Length % 4 != 0)
		{
			throw new InvalidDataException($"afid_to_ino_table has invalid size 0x{array.Length:X}.");
		}
		int num = BinaryPrimitives.ReadInt32LittleEndian(array);
		if (num < 2 || num > array.Length / 4 - 1)
		{
			throw new InvalidDataException($"afid_to_ino_table declares invalid entry count {num}.");
		}
		Dictionary<uint, PfsReader.File> dictionary = reader.GetAllFiles().ToDictionary((PfsReader.File file) => file.ino);
		Dictionary<string, uint> dictionary2 = new Dictionary<string, uint>(StringComparer.Ordinal);
		for (int num2 = 0; num2 < num - 2; num2++)
		{
			checked
			{
				int num3 = BinaryPrimitives.ReadInt32LittleEndian(array.AsSpan((num2 + 1) * 4, 4));
				if (num3 >= 0)
				{
					if (!dictionary.TryGetValue((uint)num3, out var value))
					{
						throw new InvalidDataException($"AFID {num2} references inode {num3}, which is absent from the user tree.");
					}
					string text = NormalizeExtractedPath(value.FullName);
					if (!dictionary2.TryAdd(text, (uint)num2))
					{
						throw new InvalidDataException("AFID table resolves duplicate path '" + text + "'.");
					}
				}
			}
		}
		return dictionary2;
	}

	/// <summary>Loads a UTF-8 TSV map written by <see cref="M:LibProsperoPkg.PFS.ProsperoAfidMap.Save(System.String,System.Collections.Generic.IReadOnlyDictionary{System.String,System.UInt32})" />.</summary>
	public static IReadOnlyDictionary<string, uint> Load(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path, "path");
		Dictionary<string, uint> dictionary = new Dictionary<string, uint>(StringComparer.Ordinal);
		foreach (string item in File.ReadLines(path, Encoding.UTF8))
		{
			string text = item.Trim();
			if (text.Length != 0 && !text.StartsWith('#'))
			{
				int num = text.IndexOf('\t');
				if (num <= 0 || num == text.Length - 1 || !uint.TryParse(text.AsSpan(0, num), out var result))
				{
					throw new InvalidDataException("Invalid AFID map line: '" + item + "'. Expected <decimal-afid><TAB><path>.");
				}
				string text2 = NormalizeInputPath(text.Substring(num + 1));
				if (!dictionary.TryAdd(text2, result))
				{
					throw new InvalidDataException("AFID map contains duplicate path '" + text2 + "'.");
				}
			}
		}
		if (dictionary.Values.Distinct().Count() != dictionary.Count)
		{
			throw new InvalidDataException("AFID map assigns one slot to multiple paths.");
		}
		return dictionary;
	}

	/// <summary>Saves a stable UTF-8 TSV map ordered by AFID and then path.</summary>
	public static void Save(string path, IReadOnlyDictionary<string, uint> assignments)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path, "path");
		ArgumentNullException.ThrowIfNull(assignments, "assignments");
		List<(string, uint)> list = new List<(string, uint)>(assignments.Count);
		HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
		HashSet<uint> hashSet2 = new HashSet<uint>();
		foreach (var (text2, num2) in assignments)
		{
			string text3 = NormalizeInputPath(text2);
			if (text3.Contains('\t') || text3.Contains('\r') || text3.Contains('\n'))
			{
				throw new InvalidDataException("AFID path cannot be represented in TSV: '" + text2 + "'.");
			}
			if (!hashSet.Add(text3))
			{
				throw new InvalidDataException("AFID map contains duplicate normalized path '" + text3 + "'.");
			}
			if (!hashSet2.Add(num2))
			{
				throw new InvalidDataException($"AFID map assigns slot {num2} to multiple paths.");
			}
			list.Add((text3, num2));
		}
		string fullPath = Path.GetFullPath(path);
		Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory());
		using StreamWriter streamWriter = new StreamWriter(fullPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		streamWriter.WriteLine("# LibProsperoPkg AFID map v1");
		foreach (var (value, value2) in list.OrderBy(((string Path, uint Afid) row) => row.Afid).ThenBy(((string Path, uint Afid) row) => row.Path, StringComparer.Ordinal))
		{
			streamWriter.Write(value2);
			streamWriter.Write('\t');
			streamWriter.WriteLine(value);
		}
	}

	private static string NormalizeExtractedPath(string path)
	{
		string text = path.Replace('\\', '/');
		if (text.Equals("/uroot", StringComparison.Ordinal))
		{
			return "/";
		}
		if (text.StartsWith("/uroot/", StringComparison.Ordinal))
		{
			text = text.Substring("/uroot".Length);
		}
		return NormalizeInputPath(text);
	}

	private static string NormalizeInputPath(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path, "path");
		string text = path.Trim().Replace('\\', '/');
		if (!text.StartsWith('/'))
		{
			return "/" + text;
		}
		return text;
	}
}
