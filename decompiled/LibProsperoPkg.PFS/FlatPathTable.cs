using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PFS;

/// <summary>
/// Represents the flat_path_table file, which is a mapping of filename hash to inode number
/// that the package runtime can use to speed up lookups.
/// </summary>
public class FlatPathTable
{
	private SortedDictionary<uint, uint> hashMap;

	public int Size => hashMap.Count * 8;

	public static bool HasCollision(List<FSNode> nodes)
	{
		HashSet<uint> hashSet = new HashSet<uint>();
		foreach (FSNode node in nodes)
		{
			uint item = HashFunction(node.FullPath());
			if (hashSet.Contains(item))
			{
				return true;
			}
			hashSet.Add(item);
		}
		return false;
	}

	public static Tuple<FlatPathTable, CollisionResolver> Create(List<FSNode> nodes)
	{
		SortedDictionary<uint, uint> sortedDictionary = new SortedDictionary<uint, uint>();
		Dictionary<uint, List<FSNode>> dictionary = new Dictionary<uint, List<FSNode>>();
		bool flag = false;
		foreach (FSNode node in nodes)
		{
			uint key = HashFunction(node.FullPath());
			if (sortedDictionary.ContainsKey(key))
			{
				sortedDictionary[key] = 2147483648u;
				dictionary[key].Add(node);
				flag = true;
			}
			else
			{
				sortedDictionary[key] = node.ino.Number | (uint)((node is FSDir) ? 536870912 : 0);
				dictionary[key] = new List<FSNode>();
				dictionary[key].Add(node);
			}
		}
		if (!flag)
		{
			return Tuple.Create<FlatPathTable, CollisionResolver>(new FlatPathTable(sortedDictionary), null);
		}
		uint num = 0u;
		List<List<PfsDirent>> list = new List<List<PfsDirent>>();
		foreach (KeyValuePair<uint, uint> item in sortedDictionary.Where((KeyValuePair<uint, uint> kv) => kv.Value == 2147483648u).ToList())
		{
			sortedDictionary[item.Key] = 0x80000000u | num;
			List<PfsDirent> list2 = new List<PfsDirent>();
			list.Add(list2);
			foreach (FSNode item2 in dictionary[item.Key])
			{
				PfsDirent pfsDirent = new PfsDirent
				{
					InodeNumber = item2.ino.Number,
					Type = ((item2 is FSDir) ? DirentType.Directory : DirentType.File),
					Name = item2.FullPath()
				};
				list2.Add(pfsDirent);
				num += (uint)pfsDirent.EntSize;
			}
			num += 24;
		}
		return Tuple.Create(new FlatPathTable(sortedDictionary), new CollisionResolver(list));
	}

	/// <summary>
	/// Construct a flat_path_table out of the given filesystem nodes.
	/// </summary>
	/// <param name="hashMap"></param>
	public FlatPathTable(SortedDictionary<uint, uint> hashMap)
	{
		this.hashMap = hashMap;
	}

	/// <summary>
	/// Write this file to the stream.
	/// </summary>
	/// <param name="s"></param>
	public void WriteToStream(Stream s)
	{
		foreach (uint key in hashMap.Keys)
		{
			s.WriteUInt32LE(key);
			s.WriteUInt32LE(hashMap[key]);
		}
	}

	/// <summary>
	/// Hashes the given name for the table.
	/// </summary>
	/// <param name="name"></param>
	/// <returns></returns>
	private static uint HashFunction(string name)
	{
		uint num = 0u;
		for (int i = 0; i < name.Length; i++)
		{
			num = char.ToUpperInvariant(name[i]) + 31 * num;
		}
		return num;
	}
}
