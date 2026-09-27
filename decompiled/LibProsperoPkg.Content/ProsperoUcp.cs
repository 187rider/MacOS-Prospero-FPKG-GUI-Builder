using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace LibProsperoPkg.Content;

/// <summary>
/// Reader and producer for the UCP archive format used by the trophy and user-data-system components
/// of a PS5 package.
/// </summary>
/// <remarks>
/// Layout (big-endian scalars):
/// <list type="bullet">
/// <item>Header, 0x60 bytes: 0x00 u32 magic <c>0xB228C60A</c>; 0x04 u32 version (1); 0x08 u64 total
/// file size; 0x10 u32 entry count; 0x14 u32 entry-record size (0x40); 0x18 u32 zero; 0x1C 20-byte
/// SHA-1 digest; 0x30..0x60 reserved zero.</item>
/// <item>Entry table at 0x60, one 0x40-byte record per entry: 32-byte name (NUL-padded), u64 offset,
/// u64 size, 16 reserved bytes.</item>
/// <item>Blob region: entries in ascending name order, each blob followed by padding to the next
/// 16-byte boundary (a full 16 bytes when the end is already aligned); the file size is padded the
/// same way. The digest covers the whole file with the digest field held zero.</item>
/// </list>
/// </remarks>
public static class ProsperoUcp
{
	/// <summary>Archive magic (big-endian) at file offset 0x00.</summary>
	public const uint Magic = 2989016586u;

	/// <summary>Format version at file offset 0x04.</summary>
	public const uint Version = 1u;

	private const int HeaderSize = 96;

	private const int EntryRecordSize = 64;

	private const int NameFieldSize = 32;

	private const int CountOffset = 16;

	private const int RecordSizeOffset = 20;

	private const int DigestOffset = 28;

	private const int DigestSize = 20;

	private const int BlobAlignment = 16;

	/// <summary>Returns whether the buffer begins with a UCP header.</summary>
	public static bool IsUcp(ReadOnlySpan<byte> data)
	{
		if (data.Length >= 96)
		{
			return BinaryPrimitives.ReadUInt32BigEndian(data) == 2989016586u;
		}
		return false;
	}

	/// <summary>Reads the entry list from a UCP archive.</summary>
	/// <exception cref="T:System.IO.InvalidDataException">The buffer is not a structurally valid archive.</exception>
	public static IReadOnlyList<UcpEntry> Read(ReadOnlySpan<byte> data)
	{
		if (!Validate(data, out string error))
		{
			throw new InvalidDataException(error);
		}
		int num = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16));
		List<UcpEntry> list = new List<UcpEntry>(num);
		for (int i = 0; i < num; i++)
		{
			int num2 = 96 + i * 64;
			ReadOnlySpan<byte> readOnlySpan = data.Slice(num2, 32);
			int num3 = readOnlySpan.IndexOf((byte)0);
			string name = Encoding.Latin1.GetString((num3 < 0) ? readOnlySpan : readOnlySpan.Slice(0, num3));
			ulong num4 = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(num2 + 32));
			ulong num5 = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(num2 + 32 + 8));
			list.Add(new UcpEntry(name, data.Slice((int)num4, (int)num5).ToArray()));
		}
		return list;
	}

	/// <summary>Builds a UCP archive from a set of named blobs.</summary>
	/// <exception cref="T:System.ArgumentException">A name is empty, duplicated, or longer than the name field.</exception>
	public static byte[] Build(IEnumerable<UcpEntry> entries)
	{
		ArgumentNullException.ThrowIfNull(entries, "entries");
		UcpEntry[] array = entries.OrderBy((UcpEntry e) => e.Name, StringComparer.Ordinal).ToArray();
		byte[][] array2 = new byte[array.Length][];
		HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
		for (int num = 0; num < array.Length; num++)
		{
			string name = array[num].Name;
			if (string.IsNullOrEmpty(name))
			{
				throw new ArgumentException("A UCP entry name must not be empty.", "entries");
			}
			byte[] bytes = Encoding.Latin1.GetBytes(name);
			if (bytes.Length > 32)
			{
				throw new ArgumentException($"UCP entry name '{name}' exceeds {32} bytes.", "entries");
			}
			if (!hashSet.Add(name))
			{
				throw new ArgumentException("Duplicate UCP entry name '" + name + "'.", "entries");
			}
			array2[num] = bytes;
		}
		long[] array3 = new long[array.Length];
		long num2 = 96 + (long)array.Length * 64L;
		for (int num3 = 0; num3 < array.Length; num3++)
		{
			array3[num3] = num2;
			num2 = AlignUpStrict(num2 + array[num3].Data.Length);
		}
		long num4 = ((array.Length == 0) ? 96 : num2);
		byte[] array4 = new byte[num4];
		Span<byte> destination = array4.AsSpan();
		BinaryPrimitives.WriteUInt32BigEndian(destination, 2989016586u);
		BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(4), 1u);
		BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(8), (ulong)num4);
		BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(16), (uint)array.Length);
		BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(20), 64u);
		for (int num5 = 0; num5 < array.Length; num5++)
		{
			int num6 = 96 + num5 * 64;
			array2[num5].CopyTo(destination.Slice(num6));
			BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(num6 + 32), (ulong)array3[num5]);
			BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(num6 + 32 + 8), (ulong)array[num5].Data.Length);
			array[num5].Data.CopyTo(destination.Slice((int)array3[num5]));
		}
		WriteDigest(array4);
		return array4;
	}

	/// <summary>
	/// Builds a UCP archive from the top-level files of a directory, using each file name as the entry
	/// name. Subdirectories are ignored.
	/// </summary>
	public static byte[] BuildFromDirectory(string directory)
	{
		ArgumentException.ThrowIfNullOrEmpty(directory, "directory");
		if (!Directory.Exists(directory))
		{
			throw new DirectoryNotFoundException("UCP source directory not found: " + directory);
		}
		return Build(from p in Directory.EnumerateFiles(directory)
			select new UcpEntry(Path.GetFileName(p), File.ReadAllBytes(p)));
	}

	/// <summary>
	/// Validates the header and entry table of a UCP archive. Does not verify the digest; use
	/// <see cref="M:LibProsperoPkg.Content.ProsperoUcp.VerifyDigest(System.ReadOnlySpan{System.Byte})" /> for that.
	/// </summary>
	public static bool Validate(ReadOnlySpan<byte> data, out string? error)
	{
		if (data.Length < 96)
		{
			error = "Buffer is smaller than a UCP header.";
			return false;
		}
		if (BinaryPrimitives.ReadUInt32BigEndian(data) != 2989016586u)
		{
			error = "Bad UCP magic.";
			return false;
		}
		if (BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4)) != 1)
		{
			error = "Unsupported UCP version.";
			return false;
		}
		ulong num = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(8));
		if (num != (ulong)data.Length)
		{
			error = $"UCP size field ({num}) does not match buffer length ({data.Length}).";
			return false;
		}
		uint num2 = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20));
		if (num2 != 64)
		{
			error = $"Unexpected UCP entry-record size {num2}.";
			return false;
		}
		long num3 = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16));
		long num4 = 96 + num3 * 64;
		if (num4 > data.Length)
		{
			error = "UCP entry table overruns the buffer.";
			return false;
		}
		for (int i = 0; i < num3; i++)
		{
			int num5 = 96 + i * 64;
			ulong num6 = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(num5 + 32));
			ulong num7 = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(num5 + 32 + 8));
			if (num6 < (ulong)num4 || num6 + num7 > num)
			{
				error = $"UCP entry {i} range [{num6},{num6 + num7}) is outside the blob region.";
				return false;
			}
		}
		error = null;
		return true;
	}

	/// <summary>Recomputes the stored SHA-1 digest and reports whether it matches.</summary>
	public static bool VerifyDigest(ReadOnlySpan<byte> data)
	{
		if (data.Length < 96)
		{
			return false;
		}
		Span<byte> span = stackalloc byte[20];
		data.Slice(28, 20).CopyTo(span);
		byte[] array = data.ToArray();
		Array.Clear(array, 28, 20);
		Span<byte> span2 = stackalloc byte[20];
		SHA1.HashData(array, span2);
		return span2.SequenceEqual(span);
	}

	/// <summary>
	/// Returns a copy of the archive with its SHA-1 digest recomputed, correcting a stale or wrong
	/// digest without touching the entry data.
	/// </summary>
	public static byte[] WithRepairedDigest(ReadOnlySpan<byte> data)
	{
		if (!IsUcp(data))
		{
			throw new InvalidDataException("Buffer is not a UCP archive.");
		}
		byte[] array = data.ToArray();
		WriteDigest(array);
		return array;
	}

	private static void WriteDigest(byte[] buffer)
	{
		Array.Clear(buffer, 28, 20);
		Span<byte> destination = stackalloc byte[20];
		SHA1.HashData(buffer, destination);
		destination.CopyTo(buffer.AsSpan(28));
	}

	private static long AlignUpStrict(long value)
	{
		return (value / 16 + 1) * 16;
	}
}
