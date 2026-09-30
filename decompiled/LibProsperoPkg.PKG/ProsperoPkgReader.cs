using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LibProsperoPkg.PKG;

public static class ProsperoPkgReader
{
	public static ProsperoPkgType? DetectType(string path)
	{
		using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
		return DetectType(stream);
	}

	public static ProsperoPkgType? DetectType(Stream stream)
	{
		ArgumentNullException.ThrowIfNull(stream, "stream");
		if (stream.Length < 6)
		{
			return null;
		}
		stream.Position = 0L;
		Span<byte> span = stackalloc byte[4];
		if (stream.Read(span) != 4)
		{
			return null;
		}
		if (((ReadOnlySpan<byte>)span).SequenceEqual((ReadOnlySpan<byte>)ProsperoPkgLayout.CntMagic))
		{
			return ProsperoPkgType.Meta;
		}
		if (((ReadOnlySpan<byte>)span).SequenceEqual((ReadOnlySpan<byte>)ProsperoPkgLayout.FihMagic))
		{
			stream.Position = 5L;
			return stream.ReadByte() switch
			{
				128 => (ProsperoPkgType?)ProsperoPkgType.FullRetail, 
				0 => ProsperoPkgType.FullDebug, 
				_ => null, 
			};
		}
		return null;
	}

	public static ProsperoPkg Read(string path)
	{
		using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
		return Read(stream);
	}

	public static ProsperoPkg Read(Stream stream)
	{
		ArgumentNullException.ThrowIfNull(stream, "stream");
		ProsperoPkgType prosperoPkgType = DetectType(stream) ?? throw new InvalidDataException("Not a recognisable PS5 PKG (unknown magic).");
		if (prosperoPkgType == ProsperoPkgType.Meta)
		{
			ProsperoPkgHeader header = ReadHeader(stream, 0L);
			List<ProsperoPkgEntry> entries = ReadEntryTable(stream, header, 0L);
			ResolveNames(stream, entries, 0L);
			return new ProsperoPkg
			{
				Type = prosperoPkgType,
				Header = header,
				Entries = entries
			};
		}
		ProsperoFihHeader prosperoFihHeader = ReadFihHeader(stream);
		long embeddedCntOffset = (long)prosperoFihHeader.EmbeddedCntOffset;
		if (embeddedCntOffset <= 0 || embeddedCntOffset + 1440 > stream.Length)
		{
			return new ProsperoPkg
			{
				Type = prosperoPkgType,
				Fih = prosperoFihHeader
			};
		}
		ProsperoPkgHeader header2 = ReadHeader(stream, embeddedCntOffset);
		List<ProsperoPkgEntry> entries2 = ReadEntryTable(stream, header2, embeddedCntOffset);
		ResolveNames(stream, entries2, embeddedCntOffset);
		return new ProsperoPkg
		{
			Type = prosperoPkgType,
			Fih = prosperoFihHeader,
			Header = header2,
			Entries = entries2
		};
	}

	private static ProsperoFihHeader ReadFihHeader(Stream stream)
	{
		byte[] array = new byte[256];
		stream.Position = 0L;
		ReadExactly(stream, array, 0, array.Length);
		Span<byte> span = array.AsSpan();
		return new ProsperoFihHeader
		{
			SignedByte = array[5],
			PfsImageOffset = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(16)),
			PfsImageSize = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(24)),
			EmbeddedCntOffset = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(88)),
			InnerImageBlockCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(144)),
			MetadataBlockCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(148)),
			NapsLayoutSize = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(168))
		};
	}

	private static ProsperoPkgHeader ReadHeader(Stream stream, long baseOffset)
	{
		byte[] array = new byte[1440];
		stream.Position = baseOffset;
		ReadExactly(stream, array, 0, array.Length);
		Span<byte> span = array.AsSpan();
		return new ProsperoPkgHeader
		{
			Magic = array[..4],
			Flags = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(4)),
			EntryCount = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(16)),
			ScEntryCount = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(20)),
			EntryTableOffset = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(24)),
			BodyOffset = BinaryPrimitives.ReadUInt64BigEndian(span.Slice(32)),
			BodySize = BinaryPrimitives.ReadUInt64BigEndian(span.Slice(40)),
			ContentId = ReadNulTrimmedAscii(span.Slice(64, 48)),
			DrmType = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(112)),
			ContentType = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(116))
		};
	}

	private static List<ProsperoPkgEntry> ReadEntryTable(Stream stream, ProsperoPkgHeader header, long baseOffset)
	{
		long num = (stream.Length - (baseOffset + header.EntryTableOffset)) / 32;
		if (header.EntryCount > num || header.EntryCount > 65536)
		{
			throw new InvalidDataException("PS5 PKG entry table is malformed (entry count out of range).");
		}
		List<ProsperoPkgEntry> list = new List<ProsperoPkgEntry>((int)header.EntryCount);
		byte[] array = new byte[32];
		stream.Position = baseOffset + header.EntryTableOffset;
		for (uint num2 = 0u; num2 < header.EntryCount; num2++)
		{
			ReadExactly(stream, array, 0, array.Length);
			Span<byte> span = array.AsSpan();
			uint num3 = BinaryPrimitives.ReadUInt32BigEndian(span);
			list.Add(new ProsperoPkgEntry
			{
				RawId = num3,
				Id = (Enum.IsDefined(typeof(ProsperoEntryId), num3) ? ((ProsperoEntryId)num3) : ProsperoEntryId.Unknown),
				NameTableOffset = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(4)),
				Flags1 = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(8)),
				Flags2 = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(12)),
				DataOffset = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(16)),
				DataSize = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(20))
			});
		}
		return list;
	}

	private static void ResolveNames(Stream stream, List<ProsperoPkgEntry> entries, long baseOffset)
	{
		ProsperoPkgEntry prosperoPkgEntry = null;
		foreach (ProsperoPkgEntry entry in entries)
		{
			if (entry.Id == ProsperoEntryId.EntryNames)
			{
				prosperoPkgEntry = entry;
				break;
			}
		}
		if (prosperoPkgEntry == null || prosperoPkgEntry.DataSize == 0)
		{
			return;
		}
		byte[] array = new byte[prosperoPkgEntry.DataSize];
		stream.Position = baseOffset + prosperoPkgEntry.DataOffset;
		ReadExactly(stream, array, 0, array.Length);
		foreach (ProsperoPkgEntry entry2 in entries)
		{
			if (entry2.NameTableOffset != 0 && entry2.NameTableOffset < array.Length)
			{
				int nameTableOffset = (int)entry2.NameTableOffset;
				int i;
				for (i = nameTableOffset; i < array.Length && array[i] != 0; i++)
				{
				}
				entry2.Name = Encoding.ASCII.GetString(array, nameTableOffset, i - nameTableOffset);
			}
		}
	}

	private static string ReadNulTrimmedAscii(ReadOnlySpan<byte> span)
	{
		int num = span.IndexOf((byte)0);
		if (num < 0)
		{
			num = span.Length;
		}
		return Encoding.ASCII.GetString(span.Slice(0, num));
	}

	private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
	{
		int num;
		for (int i = 0; i < count; i += num)
		{
			num = stream.Read(buffer, offset + i, count - i);
			if (num == 0)
			{
				throw new EndOfStreamException("Unexpected end of PS5 PKG while reading container.");
			}
		}
	}
}
