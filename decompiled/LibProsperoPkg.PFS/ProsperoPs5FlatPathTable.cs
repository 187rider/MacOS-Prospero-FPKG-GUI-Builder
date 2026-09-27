using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LibProsperoPkg.PFS;

/// <summary>
/// The PS5 <c>\x7fFLT</c> flat-path-table format (inode / apr variants) and its path hash for a
/// given inner-image file tree.
/// </summary>
public static class ProsperoPs5FlatPathTable
{
	/// <summary>One flat-path-table entry: a path hash and the packed 64-bit payload.</summary>
	public readonly struct Entry(ulong hash, ulong packed)
	{
		/// <summary>The path hash (table key; entries are sorted ascending by this value).</summary>
		public readonly ulong Hash = hash;

		/// <summary>The packed payload (inode/size in the low bits, flag bits 30/31, value in bits 40+).</summary>
		public readonly ulong Packed = packed;
	}

	private const ulong Seed0 = 10577419142525243217uL;

	private const ulong Seed1 = 701355796979237965uL;

	private const ulong RoundConst = 9223372039002292353uL;

	/// <summary>The 0x40-byte FLT header size; entries begin here.</summary>
	public const int HeaderSize = 64;

	/// <summary>The fixed 16-byte "seed" stored in the header at +0x30 (the two global seeds, little-endian).</summary>
	public static readonly byte[] HeaderSeed = new byte[16]
	{
		81, 79, 162, 38, 171, 138, 202, 146, 77, 196,
		27, 164, 97, 183, 187, 9
	};

	private const ulong FlagDirectory = 1073741824uL;

	private const ulong FlagSubtree = 2147483648uL;

	private const ulong DirValue = 16777215uL;

	private static ulong Rotl(ulong x, int n)
	{
		return (x << n) | (x >> 64 - n);
	}

	private static ulong Rotr(ulong x, int n)
	{
		return (x >> n) | (x << 64 - n);
	}

	/// <summary>
	/// Computes the PS5 flat-path-table 64-bit hash of a filesystem path. The path is uppercased and its
	/// leading <c>/</c> is stripped (e.g. <c>/sce_sys/keystone</c> → <c>SCE_SYS/KEYSTONE</c>) before hashing.
	/// </summary>
	public static ulong HashPath(string path)
	{
		if (path.Length > 0 && path[0] == '/')
		{
			path = path.Substring(1);
		}
		return HashBytes(Encoding.ASCII.GetBytes(path.ToUpperInvariant()));
	}

	/// <summary>The raw hash over exact bytes using the three-lane Keccak-ish sponge.</summary>
	public static ulong HashBytes(ReadOnlySpan<byte> str)
	{
		int length = str.Length;
		ulong num = 10577419142525243217uL;
		ulong num2 = Rotl(10577419142525243217uL, 11);
		ulong num3 = Rotl(10577419142525243217uL, 23);
		ulong num4 = 0uL;
		if (length != 0)
		{
			int num5 = length - 1 >> 3;
			ulong num6 = num;
			ulong num7 = num2;
			ulong num8 = num3;
			int num9 = 0;
			for (int i = 0; i < num5; i++)
			{
				ulong num10 = BinaryPrimitives.ReadUInt64LittleEndian(str.Slice(num9, 8));
				num9 += 8;
				num6 ^= num10;
				ulong num11 = Rotr(Rotl(num8 ^ num7, 5) ^ num6, 11);
				ulong num12 = Rotl(Rotl(num8 ^ num6, 17) ^ num7, 11);
				num8 = Rotr(Rotl(num7 ^ num6, 1) ^ num8, 5);
				num6 = (~num12 & num8) ^ num11 ^ 0x8000000080008081uL;
				num7 = (~num8 & num11) ^ num12;
				num8 = (~num11 & num12) ^ num8;
			}
			num = num6;
			num2 = num7;
			num3 = num8;
			int num13 = ((length - 1) & 7) + 1;
			for (int j = 0; j < num13; j++)
			{
				num4 |= (ulong)str[num9 + j] << 8 * j;
			}
		}
		ulong num14 = num2;
		ulong num15 = num3;
		ulong num16 = num4 ^ num ^ 0x9BBB761A41BC44DL;
		ulong x = Rotl(num15 ^ num14, 5) ^ num16;
		ulong x2 = Rotl(num15 ^ num16, 17) ^ num14;
		num15 = Rotl(num14 ^ num16, 1) ^ num15;
		return (~Rotl(x2, 11) & Rotr(num15, 5)) ^ Rotr(x, 11) ^ 0x8000000080008081uL;
	}

	/// <summary>
	/// Packs an <c>inode_flat_path_table</c> payload: low 24 bits = inode number, bit 30 = directory,
	/// bit 31 = subtree (non-apr) node, bits 40+ = the node's afid (0xffffff for directories).
	/// </summary>
	public static ulong PackInodeEntry(uint inode, bool isDirectory, bool isSubtree, uint afid)
	{
		ulong num = (ulong)inode & 0xFFFFFFuL;
		if (isDirectory)
		{
			num |= 0x40000000;
		}
		if (isSubtree)
		{
			num |= 0x80000000u;
		}
		return num | ((ulong)(isDirectory ? 16777215 : afid) << 40);
	}

	/// <summary>
	/// Packs an <c>apr_flat_path_table</c> payload: low 40 bits = the file's uncompressed size, bits 40+ = afid.
	/// </summary>
	public static ulong PackAprEntry(long uncompressedSize, uint afid)
	{
		return (ulong)(uncompressedSize & 0xFFFFFFFFFFL) | ((ulong)afid << 40);
	}

	/// <summary>
	/// Serializes a flat-path-table (header + entries sorted ascending by hash) to <paramref name="s" />.
	/// </summary>
	public static void Write(Stream s, IEnumerable<Entry> entries)
	{
		List<Entry> list = new List<Entry>(entries);
		list.Sort((Entry a, Entry b) => a.Hash.CompareTo(b.Hash));
		Span<byte> span = stackalloc byte[64];
		span.Clear();
		BinaryPrimitives.WriteUInt32LittleEndian(span, 1u);
		span[4] = 16;
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8), 64u);
		Encoding.ASCII.GetBytes("FLT").CopyTo(span.Slice(33));
		span[32] = 127;
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(44), (uint)list.Count);
		HeaderSeed.CopyTo(span.Slice(48));
		s.Write(span);
		Span<byte> span2 = stackalloc byte[16];
		foreach (Entry item in list)
		{
			BinaryPrimitives.WriteUInt64LittleEndian(span2, item.Hash);
			BinaryPrimitives.WriteUInt64LittleEndian(span2.Slice(8), item.Packed);
			s.Write(span2);
		}
	}

	/// <summary>Serializes a flat-path-table to a new byte array.</summary>
	public static byte[] ToBytes(IEnumerable<Entry> entries)
	{
		using MemoryStream memoryStream = new MemoryStream();
		Write(memoryStream, entries);
		return memoryStream.ToArray();
	}
}
