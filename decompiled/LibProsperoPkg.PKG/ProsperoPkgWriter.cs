using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LibProsperoPkg.PKG;

public static class ProsperoPkgWriter
{
	public static byte[] Write(ProsperoPkgWriterOptions options)
	{
		ArgumentNullException.ThrowIfNull(options, "options");
		if (string.IsNullOrEmpty(options.ContentId) || options.ContentId.Length > 48)
		{
			throw new ArgumentException($"Content id is missing or exceeds the {48}-byte field.", "options");
		}
		List<ProsperoPkgWriterEntry> list = options.Entries.ToList();
		bool flag = list.Any((ProsperoPkgWriterEntry e) => e.Id == 512);
		Dictionary<int, uint> dictionary = new Dictionary<int, uint>();
		using MemoryStream memoryStream = new MemoryStream();
		memoryStream.WriteByte(0);
		for (int num = 0; num < list.Count; num++)
		{
			if (!string.IsNullOrEmpty(list[num].Name))
			{
				dictionary[num] = (uint)memoryStream.Position;
				byte[] bytes = Encoding.ASCII.GetBytes(list[num].Name);
				memoryStream.Write(bytes, 0, bytes.Length);
				memoryStream.WriteByte(0);
			}
		}
		byte[] data = memoryStream.ToArray();
		if (!flag)
		{
			list.Add(new ProsperoPkgWriterEntry
			{
				Id = 512u,
				Data = data
			});
		}
		else
		{
			int index = list.FindIndex((ProsperoPkgWriterEntry e) => e.Id == 512);
			ProsperoPkgWriterEntry prosperoPkgWriterEntry = list[index];
			list[index] = new ProsperoPkgWriterEntry
			{
				Id = prosperoPkgWriterEntry.Id,
				Name = prosperoPkgWriterEntry.Name,
				Flags1 = prosperoPkgWriterEntry.Flags1,
				Flags2 = prosperoPkgWriterEntry.Flags2,
				Data = data
			};
		}
		int count = list.Count;
		uint num2 = 1440u;
		uint num3 = num2 + (uint)(count * 32);
		uint[] array = new uint[count];
		uint value = num3;
		for (int num4 = 0; num4 < count; num4++)
		{
			value = (array[num4] = Align(value, 16u)) + (uint)list[num4].Data.Length;
		}
		uint num5 = Align(value, 16u);
		byte[] array2 = new byte[num5];
		Span<byte> destination = array2.AsSpan(0, 1440);
		ProsperoPkgLayout.CntMagic.CopyTo(destination);
		BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(4), options.Flags);
		BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(16), (uint)count);
		BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(20), options.ScEntryCount);
		BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(24), num2);
		BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(32), num3);
		BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(40), num5 - num3);
		Encoding.ASCII.GetBytes(options.ContentId).CopyTo(destination.Slice(64));
		BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(112), options.DrmType);
		BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(116), options.ContentType);
		for (int num6 = 0; num6 < count; num6++)
		{
			ProsperoPkgWriterEntry prosperoPkgWriterEntry2 = list[num6];
			int start = (int)num2 + num6 * 32;
			Span<byte> destination2 = array2.AsSpan(start, 32);
			BinaryPrimitives.WriteUInt32BigEndian(destination2, prosperoPkgWriterEntry2.Id);
			BinaryPrimitives.WriteUInt32BigEndian(destination2.Slice(4), dictionary.TryGetValue(num6, out var value2) ? value2 : 0u);
			BinaryPrimitives.WriteUInt32BigEndian(destination2.Slice(8), prosperoPkgWriterEntry2.Flags1);
			BinaryPrimitives.WriteUInt32BigEndian(destination2.Slice(12), prosperoPkgWriterEntry2.Flags2);
			BinaryPrimitives.WriteUInt32BigEndian(destination2.Slice(16), array[num6]);
			BinaryPrimitives.WriteUInt32BigEndian(destination2.Slice(20), (uint)prosperoPkgWriterEntry2.Data.Length);
			if (prosperoPkgWriterEntry2.Data.Length != 0)
			{
				prosperoPkgWriterEntry2.Data.CopyTo(array2.AsSpan((int)array[num6]));
			}
		}
		return array2;
	}

	public static void WriteToFile(ProsperoPkgWriterOptions options, string path)
	{
		File.WriteAllBytes(path, Write(options));
	}

	private static uint Align(uint value, uint alignment)
	{
		return (value + alignment - 1) / alignment * alignment;
	}
}
