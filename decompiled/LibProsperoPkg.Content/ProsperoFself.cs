using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace LibProsperoPkg.Content;

/// <summary>
/// Reader and producer for the SELF container used by PS5 executable modules.
/// </summary>
/// <remarks>
/// Layout (little-endian scalars):
/// <list type="bullet">
/// <item>SCE header, 0x20 bytes: magic <c>0x1D3D154F</c>, version/mode/endian/attr bytes, program type,
/// header size, metadata size, file size, segment count, flags.</item>
/// <item>Segment table at 0x20, one 0x20-byte entry per segment: flags, file offset, file size, memory
/// size. Content segments come in pairs (a zero-filled digest segment then the data segment).</item>
/// <item>ELF header and program headers, copied verbatim.</item>
/// <item>Extended info (0x40) after the program headers: authority id, program type, versions, and the
/// SHA-256 of the input ELF.</item>
/// <item>A zero-filled metadata footer, then the plaintext segment data.</item>
/// <item>The original non-allocated <c>.sceversion</c> section appended after the SELF size recorded
/// in the header. Publishing Tools use this trailer for SDK/library-version validation.</item>
/// </list>
/// </remarks>
public static class ProsperoFself
{
	private readonly record struct SelectedSegment(int PhdrIndex, int FileOffset, int FileSize);

	/// <summary>Prospero SELF magic at file offset 0x00.</summary>
	public const uint Magic = 4009038932u;

	/// <summary>Legacy Orbis SELF magic accepted by the reader.</summary>
	public const uint OrbisMagic = 490542415u;

	private const int SceHeaderSize = 32;

	private const int SegEntrySize = 32;

	private const int ExtInfoSize = 64;

	private const int ControlRegionSize = 48;

	private const int MetaBlockSize = 80;

	private const int MetaFooterSize = 80;

	private const int SignatureSize = 544;

	private const int DigestSize = 32;

	private const int SignedBlockSize = 16384;

	private const int FooterMarkerOffset = 48;

	private const uint DefaultProgramType = 268435713u;

	private const ulong PaidExec = 3530822107858468865uL;

	private const ulong PaidDynamic = 3530822107858468866uL;

	private const ulong PaidExecA = 3530822107858473217uL;

	private const ulong PaidDynamicA = 3530822107858473218uL;

	private const ulong PaidExecB = 3530822107858472961uL;

	private const ulong PaidDynamicB = 3530822107858472962uL;

	private const int ElfHeaderSize = 64;

	private const int ElfPhdrSize = 56;

	private const int ExInfoByteOffset = 16128;

	private const uint PtLoad = 1u;

	private const uint PtModuleData = 1627389952u;

	private const uint PtRelro = 1627389968u;

	private const uint PtComment = 1879047936u;

	/// <summary>Returns whether the buffer begins with an SCE/SELF header.</summary>
	public static bool IsSelf(ReadOnlySpan<byte> data)
	{
		if (data.Length < 32)
		{
			return false;
		}
		uint num = BinaryPrimitives.ReadUInt32LittleEndian(data);
		if (num == 490542415 || num == 4009038932u)
		{
			return true;
		}
		return false;
	}

	/// <summary>Returns whether the buffer begins with an ELF header.</summary>
	public static bool IsElf(ReadOnlySpan<byte> data)
	{
		if (data.Length >= 64 && data[0] == 127 && data[1] == 69 && data[2] == 76)
		{
			return data[3] == 70;
		}
		return false;
	}

	/// <summary>
	/// Reads the SDK major/minor version from an ELF <c>.sceversion</c> section or from the same
	/// trailer appended to a publisher fake-SELF.
	/// </summary>
	public static bool TryGetSdkVersion(byte[] image, out ulong sdkVersion)
	{
		ArgumentNullException.ThrowIfNull(image, "image");
		ReadOnlySpan<byte> sceVersionRecords = GetSceVersionRecords(image);
		int num = -1;
		int num2 = -1;
		int num4;
		for (int i = 0; i + 4 <= sceVersionRecords.Length; i += num4)
		{
			int num3 = BinaryPrimitives.ReadUInt16LittleEndian(sceVersionRecords.Slice(i + 2));
			num4 = num3 + 4;
			if (num3 < 17 || num4 > sceVersionRecords.Length - i)
			{
				break;
			}
			int num5 = num3 - 17;
			int num6 = i + 5 + num5;
			int num7 = sceVersionRecords[num6];
			int num8 = sceVersionRecords[num6 + 1];
			if (num7 > num || (num7 == num && num8 > num2))
			{
				num = num7;
				num2 = num8;
			}
		}
		bool flag = num < 0 || num > 99;
		if (!flag)
		{
			bool flag2 = ((num2 < 0 || num2 > 99) ? true : false);
			flag = flag2;
		}
		if (flag)
		{
			sdkVersion = 0uL;
			return false;
		}
		sdkVersion = ((ulong)PackedBcd(num) << 56) | ((ulong)PackedBcd(num2) << 48);
		return true;
		static byte PackedBcd(int value)
		{
			checked
			{
				return (byte)unchecked((value / 10 << 4) | (value % 10));
			}
		}
	}

	/// <summary>
	/// Returns one raw eight-byte SDK/library version tuple from an ELF section or SELF trailer.
	/// </summary>
	public static bool TryGetSceVersionRecord(byte[] image, out byte[] record)
	{
		ArgumentNullException.ThrowIfNull(image, "image");
		ReadOnlySpan<byte> sceVersionRecords = GetSceVersionRecords(image);
		int num = 0;
		if (num + 4 <= sceVersionRecords.Length)
		{
			int num2 = BinaryPrimitives.ReadUInt16LittleEndian(sceVersionRecords.Slice(num + 2));
			int num3 = num2 + 4;
			if (num2 >= 17 && num3 <= sceVersionRecords.Length - num)
			{
				int num4 = num2 - 17;
				int start = num + 5 + num4;
				record = sceVersionRecords.Slice(start, 8).ToArray();
				return true;
			}
		}
		record = Array.Empty<byte>();
		return false;
	}

	public static ReadOnlySpan<byte> GetSceVersionRecords(byte[] image)
	{
		if (IsElf(image))
		{
			return FindElfSection(image, ".sceversion");
		}
		if (IsSelf(image))
		{
			if (image.Length >= 32)
			{
				ushort numSegs = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(24, 2));
				long maxSegEnd = 0;
				for (int i = 0; i < numSegs && 32 + (i + 1) * 32 <= image.Length; i++)
				{
					long off = (long)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(32 + i * 32 + 8, 8));
					long sz = (long)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(32 + i * 32 + 16, 8));
					if (off + sz > maxSegEnd) maxSegEnd = off + sz;
				}
				if (maxSegEnd > 0 && maxSegEnd < image.Length)
				{
					for (int pad = 0; pad < 32 && maxSegEnd + pad + 5 <= image.Length; pad++)
					{
						int pos = (int)maxSegEnd + pad;
						if (image[pos] == 0 && image[pos + 1] == 0)
						{
							ushort len = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pos + 2, 2));
							if (len >= 17 && image[pos + 4] == 8 && pos + 4 + len <= image.Length)
							{
								return image.AsSpan(pos);
							}
						}
					}
				}
			}

			ulong num = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(16));
			if (num <= (ulong)image.LongLength)
			{
				return image.AsSpan((int)num);
			}
		}
		return ReadOnlySpan<byte>.Empty;
	}

	/// <summary>Parses a SELF image.</summary>
	/// <exception cref="T:System.IO.InvalidDataException">The buffer is not a structurally valid SELF.</exception>
	public static SelfImage Parse(ReadOnlySpan<byte> data)
	{
		if (!Validate(data, out string error))
		{
			throw new InvalidDataException(error);
		}
		uint programType = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8));
		int num = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(12));
		int metaSize = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(14));
		ulong fileSize = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(16));
		int num2 = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(24));
		List<SelfSegment> list = new List<SelfSegment>(num2);
		for (int i = 0; i < num2; i++)
		{
			int num3 = 32 + i * 32;
			list.Add(new SelfSegment(BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(num3)), BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(num3 + 8)), BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(num3 + 16)), BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(num3 + 24))));
		}
		int num4 = 32 + num2 * 32;
		SelfExtInfo extInfo = null;
		byte[] elf = Array.Empty<byte>();
		if (IsElf(data.Slice(num4)))
		{
			int num5 = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(num4 + 56));
			int num6 = 64 + num5 * 56;
			if (num4 + num6 <= data.Length)
			{
				elf = data.Slice(num4, num6).ToArray();
				int num7 = AlignUp(num4 + num6, 16);
				if (num7 + 64 <= num && num7 + 64 <= data.Length)
				{
					extInfo = new SelfExtInfo(BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(num7)), BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(num7 + 8)), BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(num7 + 16)), BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(num7 + 24)), data.Slice(num7 + 32, 32).ToArray());
				}
			}
		}
		return new SelfImage(programType, num, metaSize, fileSize, list, elf, extInfo);
	}

	/// <summary>Validates the SCE header and segment table of a SELF image.</summary>
	public static bool Validate(ReadOnlySpan<byte> data, out string? error)
	{
		if (data.Length < 32)
		{
			error = "Buffer is smaller than an SCE header.";
			return false;
		}
		uint num = BinaryPrimitives.ReadUInt32LittleEndian(data);
		if (num != 4009038932u && num != 490542415)
		{
			error = "Bad SCE magic.";
			return false;
		}
		int num2 = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(12));
		int num3 = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(24));
		if (32 + (long)num3 * 32L > data.Length)
		{
			error = "Segment table overruns the buffer.";
			return false;
		}
		if (num2 > data.Length)
		{
			error = "Header size exceeds the buffer.";
			return false;
		}
		error = null;
		return true;
	}

	/// <summary>Builds a debug fake-self from a plaintext ELF module.</summary>
	/// <param name="elf">The input ELF file bytes.</param>
	/// <param name="options">Version and authority overrides.</param>
	/// <exception cref="T:System.ArgumentException">The input is not a supported ELF.</exception>
	public static byte[] MakeFself(byte[] elf, FselfOptions? options = null)
	{
		ArgumentNullException.ThrowIfNull(elf, "elf");
		if (options == null)
		{
			options = new FselfOptions();
		}
		if (!IsElf(elf))
		{
			throw new ArgumentException("Input is not an ELF file.", "elf");
		}
		if (elf[4] != 2)
		{
			throw new ArgumentException("Only 64-bit ELF modules are supported.", "elf");
		}
		byte[] sceVersionRecord = options.SceVersionRecord;
		if (sceVersionRecord != null && sceVersionRecord.Length != 8)
		{
			throw new ArgumentException("The synthesized .sceversion tuple must contain exactly eight bytes.", "options");
		}
		ushort eType = BinaryPrimitives.ReadUInt16LittleEndian(elf.AsSpan(16));
		ulong num = BinaryPrimitives.ReadUInt64LittleEndian(elf.AsSpan(32));
		if (num != 64)
		{
			throw new ArgumentException($"The ELF program-header table must follow the ELF header at 0x{64:X} (e_phoff is 0x{num:X}).", "elf");
		}
		int num2 = 64;
		int num3 = BinaryPrimitives.ReadUInt16LittleEndian(elf.AsSpan(54));
		int num4 = BinaryPrimitives.ReadUInt16LittleEndian(elf.AsSpan(56));
		if (num3 != 56)
		{
			throw new ArgumentException($"Unexpected ELF program-header size {num3}.", "elf");
		}
		if (num2 + (long)num4 * 56L > elf.Length)
		{
			throw new ArgumentException("ELF program headers overrun the file.", "elf");
		}
		List<SelectedSegment> list = SelectSegments(elf, num2, num4);
		if (list.Count == 0)
		{
			throw new ArgumentException("The ELF has no loadable segment content.", "elf");
		}
		bool isOrbisContainer = options.UseOrbisContainer;
		int num5 = list.Count * 2;
		int num6 = 32 + num5 * 32;
		int num7 = 64 + num4 * 56;
		int num8 = AlignUp(num6 + num7, 16);
		int num9 = num8 + 64 + 48;
		int sigSize = isOrbisContainer ? 256 : 544;
		int num10 = checked(num5 * 80 + 80 + sigSize);
		int num11 = num9 + num10;
		if (num9 > 65535 || num10 > 65535)
		{
			throw new ArgumentException($"The ELF has too many segments to fake-sign (header 0x{num9:X}, meta 0x{num10:X} exceed the 16-bit SCE fields).", "elf");
		}
		int[] array = new int[num5];
		int[] array2 = new int[list.Count];
		int num12 = num11;
		for (int i = 0; i < list.Count; i++)
		{
			checked
			{
				array2[i] = unchecked(checked(list[i].FileSize + 16384 - 1) / 16384) * 32;
			}
			array[i * 2] = num12;
			num12 = (array[i * 2 + 1] = num12 + array2[i]) + list[i].FileSize;
			if (i + 1 < list.Count)
			{
				num12 = AlignUp(num12, 16);
			}
		}
		int num13 = AlignUp(num12, 16);
		ReadOnlySpan<byte> readOnlySpan = FindElfSection(elf, ".sceversion");
		if (readOnlySpan.IsEmpty && options.SceVersionRecords != null && options.SceVersionRecords.Length > 0)
		{
			readOnlySpan = options.SceVersionRecords;
		}
		byte[] array3 = null;
		if (readOnlySpan.IsEmpty && !string.IsNullOrWhiteSpace(options.SceVersionName))
		{
			byte[] sceVersionRecord2 = options.SceVersionRecord;
			if (sceVersionRecord2 != null && sceVersionRecord2.Length == 8)
			{
				array3 = BuildSceVersionRecord(options.SceVersionName, sceVersionRecord2);
			}
		}
		ReadOnlySpan<byte> readOnlySpan2 = ((array3 == null) ? readOnlySpan : ((ReadOnlySpan<byte>)array3));
		byte[] array4 = new byte[checked(num13 + readOnlySpan2.Length)];
		Span<byte> span = array4.AsSpan();
		uint magic = isOrbisContainer ? 490542415u : 4009038932u;
		BinaryPrimitives.WriteUInt32LittleEndian(span, magic);
		span[4] = (byte)(isOrbisContainer ? 0 : 16);
		span[5] = 1;
		span[6] = 1;
		span[7] = 18;
		uint ptype = isOrbisContainer ? (options.ProgramType & 0xFFFFu) : options.ProgramType;
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8), ptype);
		BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(12), (ushort)num9);
		BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(14), (ushort)num10);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(16), (ulong)num13);
		BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(24), (ushort)num5);
		ushort flagsValue = (ushort)(isOrbisContainer ? 0x0022 : 50);
		BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(26), flagsValue);
		for (int j = 0; j < list.Count; j++)
		{
			int entry = 32 + j * 2 * 32;
			int entry2 = 32 + (j * 2 + 1) * 32;
			ulong flags = (ulong)(((long)(j * 2 + 1) << 20) | 0x10004);
			WriteSegment(span, entry, flags, (ulong)array[j * 2], (ulong)array2[j], (ulong)array2[j]);
			ulong flags2 = (ulong)(((long)list[j].PhdrIndex << 20) | 0x2804);
			WriteSegment(span, entry2, flags2, (ulong)array[j * 2 + 1], (ulong)list[j].FileSize, (ulong)list[j].FileSize);
		}
		elf.AsSpan(0, num7).CopyTo(span.Slice(num6));
		ulong defaultAuthority = isOrbisContainer
			? (eType == 2 ? 3530822107858473218uL : 3530822107858473217uL)
			: DeriveAuthorityId(elf, eType);
		ulong value = options.AuthorityId ?? defaultAuthority;
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(num8), value);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(num8 + 8), 1uL);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(num8 + 16), options.AppVersion);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(num8 + 24), options.FirmwareVersion);
		SHA256.HashData(elf).CopyTo(span.Slice(num8 + 32));
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(num8 + 64), 3uL);
		int num14 = checked(num9 + num5 * 80);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(num14 + 48), 65536u);
		for (int k = 0; k < list.Count; k++)
		{
			elf.AsSpan(list[k].FileOffset, list[k].FileSize).CopyTo(span.Slice(array[k * 2 + 1]));
		}
		readOnlySpan2.CopyTo(span.Slice(num13));
		return array4;
	}

	private static byte[] BuildSceVersionRecord(string moduleName, ReadOnlySpan<byte> version)
	{
		string s = (moduleName.EndsWith(':') ? moduleName : (moduleName + ":"));
		byte[] bytes = Encoding.ASCII.GetBytes(s);
		int num;
		byte[] array;
		checked
		{
			num = 1 + bytes.Length + 16;
			if (num > 65535)
			{
				throw new ArgumentException("The synthesized .sceversion module name is too long.", "moduleName");
			}
			array = new byte[num + 4];
		}
		BinaryPrimitives.WriteUInt16LittleEndian(array.AsSpan(2), (ushort)num);
		array[4] = 8;
		bytes.CopyTo(array, 5);
		version.CopyTo(array.AsSpan(5 + bytes.Length, 8));
		version.CopyTo(array.AsSpan(13 + bytes.Length, 8));
		return array;
	}

	private static List<SelectedSegment> SelectSegments(byte[] elf, int phoff, int phnum)
	{
		List<SelectedSegment> list = new List<SelectedSegment>();
		for (int i = 0; i < phnum; i++)
		{
			int num = phoff + i * 56;
			uint num2 = BinaryPrimitives.ReadUInt32LittleEndian(elf.AsSpan(num));
			long num3 = (long)BinaryPrimitives.ReadUInt64LittleEndian(elf.AsSpan(num + 8));
			long num4 = (long)BinaryPrimitives.ReadUInt64LittleEndian(elf.AsSpan(num + 32));
			if (num4 > 0 && num3 + num4 <= elf.Length && (num2 == 1 || num2 == 1627389952 || num2 == 1627389968 || num2 == 1879047936))
			{
				list.Add(new SelectedSegment(i, (int)num3, (int)num4));
			}
		}
		return list;
	}

	private static ReadOnlySpan<byte> FindElfSection(byte[] elf, string sectionName)
	{
		ulong num = BinaryPrimitives.ReadUInt64LittleEndian(elf.AsSpan(40));
		int num2 = BinaryPrimitives.ReadUInt16LittleEndian(elf.AsSpan(58));
		int num3 = BinaryPrimitives.ReadUInt16LittleEndian(elf.AsSpan(60));
		int num4 = BinaryPrimitives.ReadUInt16LittleEndian(elf.AsSpan(62));
		if (num == 0L || num3 == 0 || num2 < 64 || num4 >= num3 || num > int.MaxValue)
		{
			return ReadOnlySpan<byte>.Empty;
		}
		int num5 = (int)num;
		if (num5 + (long)num3 * (long)num2 > elf.Length)
		{
			throw new ArgumentException("ELF section headers overrun the file.", "elf");
		}
		int header = checked(num5 + num4 * num2);
		ReadOnlySpan<byte> readOnlySpan = SectionPayload(elf, header);
		for (int i = 0; i < num3; i++)
		{
			int num6 = checked(num5 + i * num2);
			uint num7 = BinaryPrimitives.ReadUInt32LittleEndian(elf.AsSpan(num6));
			if (num7 >= readOnlySpan.Length)
			{
				continue;
			}
			ReadOnlySpan<byte> span = readOnlySpan.Slice((int)num7);
			int num8 = span.IndexOf((byte)0);
			if (num8 >= 0)
			{
				span = span.Slice(0, num8);
				if (span.SequenceEqual(Encoding.ASCII.GetBytes(sectionName)))
				{
					return SectionPayload(elf, num6);
				}
			}
		}
		return ReadOnlySpan<byte>.Empty;
		static ReadOnlySpan<byte> SectionPayload(byte[] source, int num10)
		{
			ulong num9 = BinaryPrimitives.ReadUInt64LittleEndian(source.AsSpan(num10 + 24));
			ulong num11 = BinaryPrimitives.ReadUInt64LittleEndian(source.AsSpan(num10 + 32));
			if (num9 > int.MaxValue || num11 > int.MaxValue || num9 + num11 > (ulong)source.LongLength)
			{
				throw new ArgumentException("ELF section payload overruns the file.", "elf");
			}
			return source.AsSpan((int)num9, (int)num11);
		}
	}

	private static ulong DeriveAuthorityId(byte[] elf, ushort eType)
	{
		bool flag = eType == 2 || eType == 65024 || eType == 65040;
		return (byte)((16128 < elf.Length) ? elf[16128] : 0) switch
		{
			64 => flag ? 3530822107858473217uL : 3530822107858473218uL, 
			128 => flag ? 3530822107858472961uL : 3530822107858472962uL, 
			_ => flag ? 3530822107858468865uL : 3530822107858468866uL, 
		};
	}

	private static void WriteSegment(Span<byte> span, int entry, ulong flags, ulong offset, ulong fileSize, ulong memSize)
	{
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(entry), flags);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(entry + 8), offset);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(entry + 16), fileSize);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(entry + 24), memSize);
	}

	private static int AlignUp(int value, int alignment)
	{
		return (value + alignment - 1) & ~(alignment - 1);
	}

	/// <summary>
	/// In-place down-patches .sceversion records within a byte span to the target SDK version.
	/// </summary>
	public static void PatchSceVersionRecordsSpan(Span<byte> records, ulong targetSdk)
	{
		byte targetMajor = (byte)(targetSdk >> 56);
		byte targetMinor = (byte)(targetSdk >> 48);

		int i = 0;
		while (i + 4 <= records.Length)
		{
			int len = BinaryPrimitives.ReadUInt16LittleEndian(records.Slice(i + 2, 2));
			int recordLen = len + 4;
			if (len < 17 || i + recordLen > records.Length)
			{
				break;
			}
			int nameLen = len - 17;
			int v1Off = i + 5 + nameLen;
			int v2Off = v1Off + 8;
			if (v2Off + 8 <= records.Length)
			{
				byte curMajor = records[v1Off];
				byte curMinor = records[v1Off + 1];
				if (curMajor > targetMajor || (curMajor == targetMajor && curMinor > targetMinor))
				{
					records[v1Off] = targetMajor;
					records[v1Off + 1] = targetMinor;
					records[v2Off] = targetMajor;
					records[v2Off + 1] = targetMinor;
				}
			}
			i += recordLen;
		}
	}

	/// <summary>
	/// Returns a copy of the .sceversion records buffer with all records down-patched to the target SDK.
	/// </summary>
	public static byte[] PatchSceVersionRecords(byte[] records, ulong targetSdk)
	{
		if (records == null || records.Length == 0) return Array.Empty<byte>();
		byte[] copy = (byte[])records.Clone();
		PatchSceVersionRecordsSpan(copy.AsSpan(), targetSdk);
		return copy;
	}

	/// <summary>
	/// Searches for the .sceversion section in an ELF image and down-patches its records in place.
	/// </summary>
	public static bool PatchElfSceVersionSection(byte[] elf, ulong targetSdk)
	{
		if (!IsElf(elf) || elf.Length < 64) return false;
		ulong shoff = BinaryPrimitives.ReadUInt64LittleEndian(elf.AsSpan(40, 8));
		int shentsize = BinaryPrimitives.ReadUInt16LittleEndian(elf.AsSpan(58, 2));
		int shnum = BinaryPrimitives.ReadUInt16LittleEndian(elf.AsSpan(60, 2));
		int shstrndx = BinaryPrimitives.ReadUInt16LittleEndian(elf.AsSpan(62, 2));

		if (shoff == 0 || shnum == 0 || shentsize < 64 || shstrndx >= shnum || shoff > (ulong)int.MaxValue)
			return false;

		int shTableStart = (int)shoff;
		if (shTableStart + (long)shnum * shentsize > elf.Length) return false;

		int strHeader = shTableStart + shstrndx * shentsize;
		ulong strOffset = BinaryPrimitives.ReadUInt64LittleEndian(elf.AsSpan(strHeader + 24, 8));
		ulong strSize = BinaryPrimitives.ReadUInt64LittleEndian(elf.AsSpan(strHeader + 32, 8));
		if (strOffset + strSize > (ulong)elf.Length) return false;

		ReadOnlySpan<byte> shstrtab = elf.AsSpan((int)strOffset, (int)strSize);
		byte[] targetName = Encoding.ASCII.GetBytes(".sceversion");

		for (int i = 0; i < shnum; i++)
		{
			int entryOff = shTableStart + i * shentsize;
			uint nameIdx = BinaryPrimitives.ReadUInt32LittleEndian(elf.AsSpan(entryOff, 4));
			if (nameIdx >= (uint)shstrtab.Length) continue;

			ReadOnlySpan<byte> nameSpan = shstrtab.Slice((int)nameIdx);
			int nullIdx = nameSpan.IndexOf((byte)0);
			if (nullIdx >= 0) nameSpan = nameSpan.Slice(0, nullIdx);

			if (nameSpan.SequenceEqual(targetName))
			{
				ulong secOffset = BinaryPrimitives.ReadUInt64LittleEndian(elf.AsSpan(entryOff + 24, 8));
				ulong secSize = BinaryPrimitives.ReadUInt64LittleEndian(elf.AsSpan(entryOff + 32, 8));
				if (secOffset + secSize <= (ulong)elf.Length && secSize > 0)
				{
					PatchSceVersionRecordsSpan(elf.AsSpan((int)secOffset, (int)secSize), targetSdk);
					return true;
				}
			}
		}
		return false;
	}

	/// <summary>
	/// Scans the ELF for embedded Sony process/module param blocks (PT_SCE_PROCPARAM 0x3C13F4BF and "ORBI")
	/// and down-patches their SDK version fields.
	/// </summary>
	public static int PatchElfProcParam(Span<byte> elf, ulong targetSdk)
	{
		if (elf.Length < 64) return 0;
		int count = 0;
		byte targetMajor = (byte)(targetSdk >> 56);
		byte targetMinor = (byte)(targetSdk >> 48);

		ReadOnlySpan<byte> procParamNeedle = stackalloc byte[] { 0xBF, 0xF4, 0x13, 0x3C }; // 0x3C13F4BF
		ReadOnlySpan<byte> orbiNeedle = stackalloc byte[] { 0x4F, 0x52, 0x42, 0x49 }; // "ORBI"

		static void PatchParamAt(Span<byte> target, int matchPos, byte targetMajor, byte targetMinor)
		{
			if (matchPos + 16 <= target.Length)
			{
				if (targetMajor <= 4)
				{
					target[matchPos + 10] = 0x04;
					target[matchPos + 11] = 0x09;
					target[matchPos + 12] = 0x31;
					target[matchPos + 13] = 0x00;
					target[matchPos + 14] = 0x00;
					target[matchPos + 15] = 0x04;
				}
				else
				{
					target[matchPos + 10] = targetMajor;
					target[matchPos + 11] = targetMinor;
					target[matchPos + 12] = 0x00;
					target[matchPos + 13] = 0x00;
					target[matchPos + 14] = targetMinor;
					target[matchPos + 15] = targetMajor;
				}
			}
		}

		int pos = 0;
		while (pos + 16 <= elf.Length)
		{
			int idx = elf.Slice(pos).IndexOf(procParamNeedle);
			if (idx < 0) break;
			PatchParamAt(elf, pos + idx, targetMajor, targetMinor);
			count++;
			pos += idx + 4;
		}

		pos = 0;
		while (pos + 16 <= elf.Length)
		{
			int idx = elf.Slice(pos).IndexOf(orbiNeedle);
			if (idx < 0) break;
			PatchParamAt(elf, pos + idx, targetMajor, targetMinor);
			count++;
			pos += idx + 4;
		}

		return count;
	}

	/// <summary>
	/// Detects and bypasses hardware AMPR / APR streaming verification checks in executable ELFs,
	/// allowing games targeting FW 7.xx-8.xx hardware compression to run on FW 3.xx-4.xx.
	/// </summary>
	public static int PatchElfAmprBypass(Span<byte> elf, Action<string>? logger = null)
	{
		if (elf.Length < 64) return 0;
		ReadOnlySpan<byte> prefix = stackalloc byte[] { 0x48, 0x8D, 0x75, 0xE4, 0xE8 };
		ReadOnlySpan<byte> suffix = stackalloc byte[] { 0x85, 0xC0, 0x75, 0x08, 0x83, 0x7D, 0xE4, 0x02, 0xB0, 0x01, 0x74 };
		int count = 0;
		int pos = 0;

		while (pos + 25 <= elf.Length)
		{
			int idx = elf.Slice(pos).IndexOf(prefix);
			if (idx < 0) break;
			int matchPos = pos + idx;
			if (matchPos + 20 <= elf.Length && elf.Slice(matchPos + 9, suffix.Length).SequenceEqual(suffix))
			{
				// Replace call (e8 ?? ?? ?? ??) with jmp +12 (e9 0c 00 00 00)
				elf[matchPos + 4] = 0xE9;
				elf[matchPos + 5] = 0x0C;
				elf[matchPos + 6] = 0x00;
				elf[matchPos + 7] = 0x00;
				elf[matchPos + 8] = 0x00;
				count++;
				logger?.Invoke($"[stage 0/5] [Backport] Bypassed hardware AMPR stream check at offset 0x{matchPos + 4:X}");
			}
			pos = matchPos + 5;
		}
		return count;
	}

	/// <summary>
	/// In-place down-patches known SDK 5.xx+ symbol versions to SDK 4.xx equivalents in the ELF dynamic string table
	/// (e.g. libSceAmpr sceAmprInitialize '86f0wFtxn0k#u#s' -> '04AjkP0jO9U#v#G').
	/// </summary>
	public static int PatchElfSymbolVersions(Span<byte> elf, ulong targetSdkVersion, Action<string>? logger = null)
	{
		if (elf.Length < 64 || targetSdkVersion == 0 || targetSdkVersion >= 0x0500000000000000uL) return 0;
		int count = 0;

		ReadOnlySpan<byte> amprInitSdk5 = "86f0wFtxn0k#u#s"u8;
		ReadOnlySpan<byte> amprInitSdk4 = "04AjkP0jO9U#v#G"u8;

		int pos = 0;
		while (pos + amprInitSdk5.Length <= elf.Length)
		{
			int idx = elf.Slice(pos).IndexOf(amprInitSdk5);
			if (idx < 0) break;
			int matchPos = pos + idx;
			amprInitSdk4.CopyTo(elf.Slice(matchPos, amprInitSdk4.Length));
			count++;
			logger?.Invoke($"[stage 0/5] [Backport] Patched libSceAmpr symbol version at offset 0x{matchPos:X} (86f0wFtxn0k#u#s -> 04AjkP0jO9U#v#G)");
			pos = matchPos + amprInitSdk5.Length;
		}

		ReadOnlySpan<byte> libcSymSdk5 = "4h6F1LLbTiw#A#B"u8;
		ReadOnlySpan<byte> libcSymSdk4 = "IWIBBdTHit4#A#B"u8;

		pos = 0;
		while (pos + libcSymSdk5.Length <= elf.Length)
		{
			int idx = elf.Slice(pos).IndexOf(libcSymSdk5);
			if (idx < 0) break;
			int matchPos = pos + idx;
			libcSymSdk4.CopyTo(elf.Slice(matchPos, libcSymSdk4.Length));
			count++;
			logger?.Invoke($"[stage 0/5] [Backport] Patched libc symbol version at offset 0x{matchPos:X} (4h6F1LLbTiw#A#B -> IWIBBdTHit4#A#B)");
			pos = matchPos + libcSymSdk5.Length;
		}

		return count;
	}

	/// <summary>
	/// In-place patches executable binary instructions for FW 3.xx-4.xx backport compatibility:
	/// 1) NOPs unsupported SDK 5+ user-service privacy calls: BE 01 00 00 00 4C 89 F7 E8 ?? ?? ?? ?? 8B 3D
	/// 2) Fixes libc.prx buffer/stack alignment: BA 03 00 00 00 B9 00 80 00 00 -> BA 03 00 00 00 31 C9 90 90 90
	/// 3) Inlines missing SDK 7.xx/8.xx NID stubs and redirects unresolvable GOT entries
	/// 4) Fixes engine compatibility flags (cmovne and config initialization fields)
	/// </summary>
	public static int PatchElfExecutableBackport(Span<byte> elf, ulong targetSdkVersion, Action<string>? logger = null)
	{
		if (elf.Length < 64 || targetSdkVersion == 0 || targetSdkVersion >= 0x0500000000000000uL) return 0;
		int count = 0;

		// 1. libc.prx page/stack size alignment patch: BA 03 00 00 00 B9 00 80 00 00 -> BA 03 00 00 00 31 C9 90 90 90
		ReadOnlySpan<byte> libcPageNeedle = stackalloc byte[] { 0xBA, 0x03, 0x00, 0x00, 0x00, 0xB9, 0x00, 0x80, 0x00, 0x00 };
		int pos = 0;
		while (pos + libcPageNeedle.Length <= elf.Length)
		{
			int idx = elf.Slice(pos).IndexOf(libcPageNeedle);
			if (idx < 0) break;
			int matchPos = pos + idx;
			elf[matchPos + 5] = 0x31;
			elf[matchPos + 6] = 0xC9;
			elf[matchPos + 7] = 0x90;
			elf[matchPos + 8] = 0x90;
			elf[matchPos + 9] = 0x90;
			count++;
			logger?.Invoke($"[stage 0/5] [Backport] Patched libc buffer/stack size constraint at offset 0x{matchPos + 5:X}");
			pos = matchPos + libcPageNeedle.Length;
		}

		// 2. High-SDK engine backport suite (NID stubs + GOT redirections + engine config flags)
		ReadOnlySpan<byte> extendedSignature = "NH6xARDOVv8"u8;
		if (elf.IndexOf(extendedSignature) >= 0)
		{
			(int Offset, string OrigHex, string ReplHex)[] extendedPatches =
			{
				(0x40c1, "cccccccccccccccccccccccccccccc", "31c0c707020000008906c390909090"),
				(0x4231, "cccccc", "31c0c3"),
				(0x4681, "cccccccccccc", "b81c002980c3"),
				(0x5996, "cccccccccccccccccc", "50e88403f20559eb02"),
				(0x59a1, "cccccccccccccccccccccccccc", "3d01802680750231c0c331c0c3"),
				(0xbff1, "cccccccccccc", "b808009f80c3"),
				(0x11471, "cccccccccccccccccccccccc", "57e8a940f10559e904060000"),
				(0x11a81, "cccccccccccccccccccccccccc", "3d00009f807505e924bb0000c3"),
				(0x1d4fb, "e8", "90"),
				(0x1d4fd, "8af005", "909090"),
				(0x1d5b1, "cccccccccccccccccccccccccccccc", "e30c48837908007405e9a2120000c3"),
				(0x1e861, "cccccccccccccccccccccccccc", "48837910007505b808009f80c3"),
				(0x45831, "cccccccccccccccccccccccccccccc", "31c083fe040f95c001c0488907eb01"),
				(0x45841, "cccccccccccccccccccccccccccc", "6a01baffffff7f6a035e31c9eb02"),
				(0x45851, "cccccccccccccccccccccccccccc", "4531c04183c9ffe8e30bee0558c3"),
				(0x1e1a2c, "f042d405", "663fe2ff"),
				(0x1e326e, "0f45c2", "909090"),
				(0xdc8552, "eade1505", "dbd227ff"),
				(0xdc86e5, "57dd1505", "48d127ff"),
				(0x385155a, "e8811f6d02", "e90c000000"),
				(0x3851579, "e8621f6d02", "e90c000000"),
				(0x3851598, "e8431f6d02", "e90c000000"),
				(0x38515b7, "e8241f6d02", "e90c000000"),
				(0x3f94b2c, "f009f901", "41c907fc"),
				(0x3f94ba2, "ffffffff", "00000000"),
				(0x3f94c34, "02", "00"),
				(0x980ee38, "96f6f105", "c1000000"),
				(0x980fc58, "d612f205", "31020000"),
				(0x980fc90, "4613f205", "81060000"),
				(0x980fd80, "2615f205", "f17f0000"),
				(0x9810180, "261df205", "ab190000")
			};

			int extendedCount = 0;
			foreach (var patch in extendedPatches)
			{
				byte[] orig = Convert.FromHexString(patch.OrigHex);
				byte[] repl = Convert.FromHexString(patch.ReplHex);
				int targetOffset = patch.Offset;
				if (targetOffset + orig.Length <= elf.Length && elf.Slice(targetOffset, orig.Length).SequenceEqual(orig))
				{
					repl.CopyTo(elf.Slice(targetOffset, repl.Length));
					extendedCount++;
				}
				else
				{
					int winStart = Math.Max(0, targetOffset - 4096);
					int winLen = Math.Min(elf.Length - winStart, 8192);
					int found = elf.Slice(winStart, winLen).IndexOf(orig);
					if (found >= 0)
					{
						repl.CopyTo(elf.Slice(winStart + found, repl.Length));
						extendedCount++;
					}
				}
			}
			logger?.Invoke($"[stage 0/5] [Backport] Applied {extendedCount} native NID stub, GOT redirection, and compatibility patches.");
			count += extendedCount;
			return count;
		}

		// 3. Generic fallback patches for other titles
		// UserService privacy call (BE 01 00 00 00 4C 89 F7 E8 ?? ?? ?? ?? 8B 3D) -> 5 NOPs
		ReadOnlySpan<byte> privPrefix = stackalloc byte[] { 0xBE, 0x01, 0x00, 0x00, 0x00, 0x4C, 0x89, 0xF7, 0xE8 };
		ReadOnlySpan<byte> privSuffix = stackalloc byte[] { 0x8B, 0x3D };
		pos = 0;
		while (pos + 16 <= elf.Length)
		{
			int idx = elf.Slice(pos).IndexOf(privPrefix);
			if (idx < 0) break;
			int matchPos = pos + idx;
			if (matchPos + 15 <= elf.Length && elf.Slice(matchPos + 13, 2).SequenceEqual(privSuffix))
			{
				elf.Slice(matchPos + 8, 5).Fill(0x90);
				count++;
				logger?.Invoke($"[stage 0/5] [Backport] Bypassed unsupported UserService privacy call at offset 0x{matchPos + 8:X}");
			}
			pos = matchPos + 9;
		}

		// Engine compatibility flags (cmovne)
		ReadOnlySpan<byte> cmovneNeedle = stackalloc byte[] { 0x80, 0x7D, 0x10, 0x00, 0x0F, 0x45, 0xC2, 0x31, 0xD2, 0x41, 0x89, 0x45, 0x08 };
		pos = 0;
		while (pos + cmovneNeedle.Length <= elf.Length)
		{
			int idx = elf.Slice(pos).IndexOf(cmovneNeedle);
			if (idx < 0) break;
			int matchPos = pos + idx;
			elf.Slice(matchPos + 4, 3).Fill(0x90);
			count++;
			logger?.Invoke($"[stage 0/5] [Backport] Patched conditional engine flag at offset 0x{matchPos + 4:X}");
			pos = matchPos + cmovneNeedle.Length;
		}

		// Generic fallback AMPR status call (4C 89 A5 78 FA FF FF E8 ?? ?? ?? ?? 3D 08 00 9F 80 74) -> B8 08 00 9F 80
		ReadOnlySpan<byte> amprStatPrefix = stackalloc byte[] { 0x4C, 0x89, 0xA5, 0x78, 0xFA, 0xFF, 0xFF, 0xE8 };
		ReadOnlySpan<byte> amprStatSuffix = stackalloc byte[] { 0x3D, 0x08, 0x00, 0x9F, 0x80, 0x74 };
		pos = 0;
		while (pos + 19 <= elf.Length)
		{
			int idx = elf.Slice(pos).IndexOf(amprStatPrefix);
			if (idx < 0) break;
			int matchPos = pos + idx;
			if (matchPos + 18 <= elf.Length && elf.Slice(matchPos + 12, amprStatSuffix.Length).SequenceEqual(amprStatSuffix))
			{
				elf[matchPos + 7] = 0xB8;
				elf[matchPos + 8] = 0x08;
				elf[matchPos + 9] = 0x00;
				elf[matchPos + 10] = 0x9F;
				elf[matchPos + 11] = 0x80;
				count++;
				logger?.Invoke($"[stage 0/5] [Backport] Inlined AMPR fallback return code at offset 0x{matchPos + 7:X}");
			}
			pos = matchPos + 8;
		}

		// Generic secondary device/init call (02 00 00 00 0F 44 F8 E8 ?? ?? ?? ?? 85 C0 74 1D) -> inlined xor eax, eax; 3 NOPs
		ReadOnlySpan<byte> initCallPre = stackalloc byte[] { 0x02, 0x00, 0x00, 0x00, 0x0F, 0x44, 0xF8, 0xE8 };
		ReadOnlySpan<byte> initCallSuf = stackalloc byte[] { 0x85, 0xC0, 0x74, 0x1D };
		pos = 0;
		while (pos + 17 <= elf.Length)
		{
			int idx = elf.Slice(pos).IndexOf(initCallPre);
			if (idx < 0) break;
			int matchPos = pos + idx;
			if (matchPos + 16 <= elf.Length && elf.Slice(matchPos + 12, initCallSuf.Length).SequenceEqual(initCallSuf))
			{
				elf[matchPos + 7] = 0x31;
				elf[matchPos + 8] = 0xC0;
				elf[matchPos + 9] = 0x90;
				elf[matchPos + 10] = 0x90;
				elf[matchPos + 11] = 0x90;
				count++;
				logger?.Invoke($"[stage 0/5] [Backport] Inlined device query success return at offset 0x{matchPos + 7:X}");
			}
			pos = matchPos + 8;
		}

		return count;
	}

	public record CompatibilityStubRule(string FileName, string ModuleName, ulong MinFirmwareSdk, string Description, string TargetSubdirectory = "fakelib");

	public static readonly CompatibilityStubRule[] KnownCompatibilityModules =
	[
		new("libSceAmpr.sprx", "libSceAmpr", 0x0600000000000000uL, "Adaptive Media Playback (AMPR) streaming module", "fakelib"),
		new("libScePsml.sprx", "libScePsml", 0x0600000000000000uL, "PlayStation Media Layer (PSML) module", "fakelib"),
		new("libSceAgcDriver.sprx", "libSceAgcDriver", 0x0500000000000000uL, "Next-gen AGC driver graphics interface", "fakelib"),
		new("libSceAgc.sprx", "libSceAgc", 0x0500000000000000uL, "Advanced Graphics Core (AGC) user module", "fakelib"),
		new("libSceVdecCore.native.sprx", "libSceVdecCore", 0x0600000000000000uL, "Hardware video decoder core runtime module", "fakelib"),
		new("libSceVdecSavc2.native.sprx", "libSceVdecSavc2", 0x0600000000000000uL, "Hardware video decoder AVC/H.264 codec module", "fakelib"),
		new("libSceVdecShevc.native.sprx", "libSceVdecShevc", 0x0600000000000000uL, "Hardware video decoder HEVC/H.265 codec module", "fakelib"),
		new("libSceSaveData.native.sprx", "libSceSaveData_native", 0x0600000000000000uL, "Userland SaveData native bridge module", "fakelib"),
		new("libScePlayGo.sprx", "libScePlayGo", 0x0500000000000000uL, "PlayGo chunk streaming module", "fakelib"),
		new("libSceFiber.sprx", "libSceFiber", 0x0500000000000000uL, "Userland cooperative fiber threading runtime", "fakelib"),
		new("libc.prx", "libc", 0x0500000000000000uL, "SceLibcV2 enhanced POSIX libc module", "sce_module"),
		new("libSceNpCppWebApi.prx", "libSceNpCppWebApi", 0x0600000000000000uL, "PlayStation Network C++ Web API Client module", "sce_module"),
		new("libSceFontGsm.prx", "libSceFontGsm", 0x0600000000000000uL, "System font glyph rasterizer / layout engine", "sce_module"),
		new("libSceJobManager.prx", "libSceJobManager", 0x0600000000000000uL, "Asynchronous thread scheduling and job dispatch runtime", "sce_module"),
		new("libScePfs.prx", "libScePfs", 0x0600000000000000uL, "Userland PlayGo / PFS container filesystem module", "sce_module"),
		new("libSceFace.prx", "libSceFace", 0x0600000000000000uL, "Facial detection and animation runtime module", "sce_module"),
		new("libSceFaceTracker.prx", "libSceFaceTracker", 0x0600000000000000uL, "Camera facial tracking runtime module", "sce_module"),
		new("libSceAppContent.sprx", "libSceAppContent", 0x0600000000000000uL, "Application content mount and entitlement management", "fakelib"),
		new("libSceGameUpdate.sprx", "libSceGameUpdate", 0x0600000000000000uL, "Game update check and patch metadata handler", "fakelib"),
		new("libSceNpEntitlementAccess.sprx", "libSceNpEntitlementAccess", 0x0600000000000000uL, "PlayStation Network entitlement access shim", "fakelib"),
		new("libkernel.sprx", "libkernel", 0x0500000000000000uL, "Kernel userland syscall bridge", "sce_module"),
		new("right.sprx", "libSceGameRight", 0x0500000000000000uL, "Game right system stub module", "sce_sys/about"),
	];

	public static string GetModuleTargetSubdirectory(string fileName)
	{
		foreach (var rule in KnownCompatibilityModules)
		{
			if (rule.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase))
			{
				return rule.TargetSubdirectory;
			}
		}
		return "fakelib";
	}

	public static bool IsModuleMissingOnTargetSdk(string fileName, ulong targetSdk)
	{
		if (fileName.StartsWith("libkernel", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		foreach (var rule in KnownCompatibilityModules)
		{
			if (rule.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase))
			{
				return targetSdk < rule.MinFirmwareSdk;
			}
		}
		return targetSdk < 0x0500000000000000uL;
	}

	/// <summary>
	/// Inspects an executable ELF image to detect which compatibility modules are imported/referenced.
	/// </summary>
	public static HashSet<string> DetectImportedCompatibilityModules(ReadOnlySpan<byte> elf, IEnumerable<string>? additionalFiles = null)
	{
		var detected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (elf.Length < 64) return detected;

		foreach (var rule in KnownCompatibilityModules)
		{
			if (rule.FileName.StartsWith("libkernel", StringComparison.OrdinalIgnoreCase)) continue;

			// Exact filename match (e.g. "libSceAmpr.sprx")
			byte[] fileBytes = Encoding.ASCII.GetBytes(rule.FileName);
			if (elf.IndexOf(fileBytes) >= 0)
			{
				detected.Add(rule.FileName);
				continue;
			}

			// Match .prx counterpart if rule is .sprx or vice versa
			string altName = rule.FileName.EndsWith(".sprx", StringComparison.OrdinalIgnoreCase)
				? Path.GetFileNameWithoutExtension(rule.FileName) + ".prx"
				: (rule.FileName.EndsWith(".prx", StringComparison.OrdinalIgnoreCase)
					? Path.GetFileNameWithoutExtension(rule.FileName) + ".sprx"
					: "");
			if (!string.IsNullOrEmpty(altName))
			{
				byte[] altBytes = Encoding.ASCII.GetBytes(altName);
				if (elf.IndexOf(altBytes) >= 0)
				{
					detected.Add(rule.FileName);
					continue;
				}
			}

			// Only match module name with null terminator for specific SCE system libraries
			if (rule.ModuleName.StartsWith("libSce", StringComparison.OrdinalIgnoreCase))
			{
				byte[] modBytes = Encoding.ASCII.GetBytes(rule.ModuleName + "\0");
				if (elf.IndexOf(modBytes) >= 0)
				{
					detected.Add(rule.FileName);
				}
			}
		}

		// Check for AvPlayer / Vdec video decoding stack
		byte[] avPlayerBytes = Encoding.ASCII.GetBytes("libSceAvPlayer");
		byte[] vdecBytes = Encoding.ASCII.GetBytes("libSceVdec");
		if (elf.IndexOf(avPlayerBytes) >= 0 || elf.IndexOf(vdecBytes) >= 0)
		{
			detected.Add("libSceVdecCore.native.sprx");
			detected.Add("libSceVdecSavc2.native.sprx");
			detected.Add("libSceVdecShevc.native.sprx");
		}

		// Check for libScePsml_debug (used by Unity/IL2CPP titles like Onimusha)
		byte[] psmlDebugBytes = Encoding.ASCII.GetBytes("libScePsml_debug");
		if (elf.IndexOf(psmlDebugBytes) >= 0)
		{
			detected.Add("libScePsml.sprx");
		}

		// Check for digital rights / entitlement management (DRM bypass stub in sce_sys/about/)
		byte[] entitlementBytes = Encoding.ASCII.GetBytes("libSceNpEntitlementAccess");
		byte[] commerceBytes = Encoding.ASCII.GetBytes("libSceNpCommerce");
		byte[] gameRightBytes = Encoding.ASCII.GetBytes("libSceGameRight");
		if (elf.IndexOf(entitlementBytes) >= 0 || elf.IndexOf(commerceBytes) >= 0 || elf.IndexOf(gameRightBytes) >= 0)
		{
			detected.Add("right.sprx");
		}

		// Check for cooperative user-level threading (ULT) requiring libSceFiber
		byte[] ultBytes = Encoding.ASCII.GetBytes("libSceUlt");
		if (elf.IndexOf(ultBytes) >= 0)
		{
			detected.Add("libSceFiber.sprx");
		}

		if (additionalFiles != null)
		{
			foreach (var file in additionalFiles)
			{
				string fname = Path.GetFileName(file);
				if (detected.Contains(fname)) continue;
				if (fname.Equals("k9.psp", StringComparison.OrdinalIgnoreCase)) continue;
				if (fname.StartsWith("libkernel", StringComparison.OrdinalIgnoreCase)) continue;

				// Only consider PRX / SPRX modules
				if (!fname.EndsWith(".prx", StringComparison.OrdinalIgnoreCase) &&
				    !fname.EndsWith(".sprx", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				byte[] fileBytes = Encoding.ASCII.GetBytes(fname);
				if (elf.IndexOf(fileBytes) >= 0)
				{
					detected.Add(fname);
				}
			}
		}

		return detected;
	}

	/// <summary>
	/// Recursively scans a decrypted PS5 game dump folder for executable binaries (.bin, .elf, .prx, .sprx),
	/// sanitizes any truncated section header tables, converts decrypted ELFs into fake-signed FSELFs,
	/// and optionally down-patches SDK versions for backporting.
	/// </summary>
	/// <param name="sourceDir">Path to the game dump root folder.</param>
	/// <param name="logger">Optional progress callback.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <param name="targetSdkVersion">Optional target SDK version to down-patch executables to (e.g. 0x0400000000000000).</param>
	/// <param name="bundledFakelibDir">Optional path to bundled fakelib stubs for selective dependency injection.</param>
	/// <param name="excludedFakelibs">Optional set of fakelib file names to exclude from staging.</param>
	/// <returns>Number of ELFs converted to FSELF.</returns>
	public static int RecursiveMakeFself(string sourceDir, Action<string>? logger = null, CancellationToken cancellationToken = default, ulong? targetSdkVersion = null, string? bundledFakelibDir = null, IEnumerable<string>? excludedFakelibs = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourceDir, nameof(sourceDir));
		if (!Directory.Exists(sourceDir)) return 0;

		cancellationToken.ThrowIfCancellationRequested();

		if (targetSdkVersion.HasValue && targetSdkVersion.Value > 0)
		{
			logger?.Invoke($"[stage 0/5] [Backport] SDK down-patch enabled: Target SDK = 0x{targetSdkVersion.Value:X16}");
		}
		else
		{
			logger?.Invoke("[stage 0/5] Pre-processing game dump (recursive make_fself)...");
		}

		byte[]? applicationSceVersion = null;
		string ebootPath = Path.Combine(sourceDir, "eboot.bin");
		if (File.Exists(ebootPath))
		{
			try
			{
				byte[] ebootBytes = File.ReadAllBytes(ebootPath);
				if (TryGetSceVersionRecord(ebootBytes, out byte[] rec) && rec.Length == 8)
				{
					applicationSceVersion = rec;
				}
			}
			catch { }
		}

		if (targetSdkVersion.HasValue && targetSdkVersion.Value > 0 && applicationSceVersion != null)
		{
			byte targetMajor = (byte)(targetSdkVersion.Value >> 56);
			byte targetMinor = (byte)(targetSdkVersion.Value >> 48);
			if (applicationSceVersion[0] > targetMajor || (applicationSceVersion[0] == targetMajor && applicationSceVersion[1] > targetMinor))
			{
				applicationSceVersion[0] = targetMajor;
				applicationSceVersion[1] = targetMinor;
			}
		}

		int convertedCount = 0;
		int sanitizedCount = 0;
		int totalProcParamCount = 0;
		int totalAmprCount = 0;
		int totalSymCount = 0;
		int totalBackportCount = 0;
		var detectedCompatibilityImports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		string[]? candidateFiles = null;
		if (!string.IsNullOrEmpty(bundledFakelibDir) && Directory.Exists(bundledFakelibDir))
		{
			try
			{
				candidateFiles = Directory.EnumerateFiles(bundledFakelibDir, "*.*").ToArray();
			}
			catch { }
		}
		string[] extensions = { ".bin", ".elf", ".prx", ".sprx" };

		try
		{
			foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
			{
				cancellationToken.ThrowIfCancellationRequested();

				string ext = Path.GetExtension(file).ToLowerInvariant();
				string name = Path.GetFileName(file);
				if (name.StartsWith("._", StringComparison.Ordinal) || name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase)) continue;
				if (!extensions.Contains(ext) || file.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".esbak", StringComparison.OrdinalIgnoreCase)) continue;

				string relPath = Path.GetRelativePath(sourceDir, file);
				string[] pathParts = relPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
				if (pathParts.Any(p => p.Equals("fakelib", StringComparison.OrdinalIgnoreCase)) ||
				    KnownCompatibilityModules.Any(m => m.FileName.Equals(name, StringComparison.OrdinalIgnoreCase) &&
				                                      relPath.Replace('\\', '/').StartsWith(m.TargetSubdirectory + "/", StringComparison.OrdinalIgnoreCase)))
				{
					continue;
				}

				try
				{
					var fi = new FileInfo(file);
					if (fi.Length < 64) continue;

					byte[] header = new byte[64];
					using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
					{
						if (fs.Read(header, 0, 64) != 64) continue;
					}

					uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
					byte[] elfBytes;
					byte[]? selfTrailer = null;

					if (magic == Magic) // Native PS5 SELF/FSELF (0xEEF51454 / 54 14 f5 ee)
					{
						byte[] selfBytes = File.ReadAllBytes(file);
						if (!TryUnfself(selfBytes, out byte[]? extractedElf) || extractedElf == null)
						{
							logger?.Invoke($"[stage 0/5] Skipping non-unpackable PS5 SELF: {relPath}");
							continue;
						}
						elfBytes = extractedElf;
						var trailerSpan = GetSceVersionRecords(selfBytes);
						if (!trailerSpan.IsEmpty)
						{
							selfTrailer = trailerSpan.ToArray();
						}
						logger?.Invoke($"[stage 0/5] Unpacked PS5 SELF: {relPath} ({selfBytes.Length:N0} -> {elfBytes.Length:N0} bytes)");
					}
					else if (magic == OrbisMagic) // Legacy PS4/Orbis FSELF (0x1D3D154F / 4f 15 3d 1d)
					{
						byte[] selfBytes = File.ReadAllBytes(file);
						if (!TryUnfself(selfBytes, out byte[]? extractedElf) || extractedElf == null)
						{
							logger?.Invoke($"[stage 0/5] Warning: could not unpack Orbis FSELF '{relPath}'");
							continue;
						}
						elfBytes = extractedElf;
						var trailerSpan = GetSceVersionRecords(selfBytes);
						if (!trailerSpan.IsEmpty)
						{
							selfTrailer = trailerSpan.ToArray();
						}
						logger?.Invoke($"[stage 0/5] Unpacked Orbis FSELF to clean ELF: {relPath} ({selfBytes.Length:N0} -> {elfBytes.Length:N0} bytes)");
					}
					else if (IsElf(header))
					{
						elfBytes = File.ReadAllBytes(file);
					}
					else
					{
						continue;
					}

					cancellationToken.ThrowIfCancellationRequested();

					var importedLibs = DetectImportedCompatibilityModules(elfBytes, candidateFiles);
					foreach (var lib in importedLibs)
					{
						if (detectedCompatibilityImports.Add(lib))
						{
							logger?.Invoke($"[stage 0/5] [Backport] Detected system module dependency '{lib}' in {relPath}");
						}
					}

					// Check and sanitize truncated section header tables in the ELF
					if (elfBytes.Length >= 64)
					{
						ulong e_shoff = BinaryPrimitives.ReadUInt64LittleEndian(elfBytes.AsSpan(40, 8));
						ushort e_shentsize = BinaryPrimitives.ReadUInt16LittleEndian(elfBytes.AsSpan(58, 2));
						ushort e_shnum = BinaryPrimitives.ReadUInt16LittleEndian(elfBytes.AsSpan(60, 2));

						if (e_shoff > 0 || e_shnum > 0)
						{
							long tableEnd = (long)e_shoff + ((long)e_shnum * (long)e_shentsize);
							if (tableEnd > elfBytes.Length || (long)e_shoff >= elfBytes.Length)
							{
								elfBytes.AsSpan(40, 8).Clear();
								elfBytes.AsSpan(58, 6).Clear();
								sanitizedCount++;
							}
						}
					}

					if (targetSdkVersion.HasValue && targetSdkVersion.Value > 0)
					{
						PatchElfSceVersionSection(elfBytes, targetSdkVersion.Value);
						int procParamCount = PatchElfProcParam(elfBytes, targetSdkVersion.Value);
						if (procParamCount > 0)
						{
							totalProcParamCount += procParamCount;
							logger?.Invoke($"[stage 0/5] [Backport] Patched {procParamCount} embedded PT_SCE_PROCPARAM SDK block(s) in {relPath}");
						}
						int amprCount = PatchElfAmprBypass(elfBytes, logger);
						totalAmprCount += amprCount;

						int symCount = PatchElfSymbolVersions(elfBytes, targetSdkVersion.Value, logger);
						totalSymCount += symCount;

						int backportCount = PatchElfExecutableBackport(elfBytes, targetSdkVersion.Value, logger);
						totalBackportCount += backportCount;

						if (selfTrailer != null && selfTrailer.Length > 0)
						{
							selfTrailer = PatchSceVersionRecords(selfTrailer, targetSdkVersion.Value);
						}
					}

					FselfOptions? options = null;
					string fileName = Path.GetFileName(file);
					if (selfTrailer != null && selfTrailer.Length > 0)
					{
						options = new FselfOptions
						{
							SceVersionRecords = selfTrailer,
							FirmwareVersion = targetSdkVersion ?? 0uL,
							ProgramType = 268435713u
						};
					}
					else if (!fileName.Equals("eboot.bin", StringComparison.OrdinalIgnoreCase) &&
					    applicationSceVersion != null &&
					    !TryGetSceVersionRecord(elfBytes, out _))
					{
						options = new FselfOptions
						{
							SceVersionName = Path.GetFileNameWithoutExtension(fileName),
							SceVersionRecord = applicationSceVersion,
							FirmwareVersion = targetSdkVersion ?? 0uL,
							ProgramType = 268435713u
						};
					}
					else if (targetSdkVersion.HasValue && targetSdkVersion.Value > 0)
					{
						options = new FselfOptions
						{
							FirmwareVersion = targetSdkVersion.Value,
							ProgramType = 268435713u
						};
					}

					cancellationToken.ThrowIfCancellationRequested();

					byte[] fself = MakeFself(elfBytes, options);
					string bakFile = file + ".bak";
					if (!File.Exists(bakFile))
					{
						try
						{
							File.Copy(file, bakFile, overwrite: false);
						}
						catch { }
					}
					File.WriteAllBytes(file, fself);
					convertedCount++;

					if (targetSdkVersion.HasValue && targetSdkVersion.Value > 0)
					{
						logger?.Invoke($"[stage 0/5] Fake-signed native PS5 FSELF (0xEEF51454) [Backported 0x{targetSdkVersion.Value:X16}]: {relPath} ({fself.Length:N0} bytes)");
					}
					else
					{
						logger?.Invoke($"[stage 0/5] Fake-signed native PS5 FSELF (0xEEF51454): {relPath} ({fself.Length:N0} bytes)");
					}
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					logger?.Invoke($"[stage 0/5] Warning: could not fake-sign '{relPath}': {ex.Message}");
				}
			}
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			logger?.Invoke($"[stage 0/5] Scan warning: {ex.Message}");
		}

		if (targetSdkVersion.HasValue && targetSdkVersion.Value > 0)
		{
			logger?.Invoke($"[stage 0/5] [Backport Summary] Completed backport to SDK 0x{targetSdkVersion.Value:X16}:");
			logger?.Invoke($"[stage 0/5]   - Executables converted: {convertedCount} file(s)");
			logger?.Invoke($"[stage 0/5]   - ProcParam blocks down-patched: {totalProcParamCount}");
			logger?.Invoke($"[stage 0/5]   - Hardware AMPR stream bypasses: {totalAmprCount}");
			logger?.Invoke($"[stage 0/5]   - Dynamic symbol version mappings: {totalSymCount}");
			logger?.Invoke($"[stage 0/5]   - Inlined system call stubs: {totalBackportCount}");
			if (sanitizedCount > 0)
			{
				logger?.Invoke($"[stage 0/5]   - Section header tables sanitized: {sanitizedCount}");
			}

			string tmpAmprIndex = Path.Combine(sourceDir, "ampr_emu.index.tmp");
			if (File.Exists(tmpAmprIndex))
			{
				try
				{
					File.Delete(tmpAmprIndex);
					logger?.Invoke("[stage 0/5] [Backport] Cleaned up stale ampr_emu.index.tmp to prevent cutscene memory allocation crash.");
				}
				catch { }
			}

			if (!string.IsNullOrEmpty(bundledFakelibDir) && Directory.Exists(bundledFakelibDir))
			{
				var missingStubs = detectedCompatibilityImports
					.Where(lib => IsModuleMissingOnTargetSdk(lib, targetSdkVersion.Value))
					.ToList();

				if (missingStubs.Count > 0)
				{
					int stagedCount = 0;
					var excludedSet = excludedFakelibs != null
						? new HashSet<string>(excludedFakelibs, StringComparer.OrdinalIgnoreCase)
						: null;

					foreach (var stubName in missingStubs)
					{
						if (excludedSet != null && excludedSet.Contains(stubName))
						{
							logger?.Invoke($"[stage 0/5] [Backport] Skipping user-excluded fakelib '{stubName}'.");
							continue;
						}

						string subDir = GetModuleTargetSubdirectory(stubName);
						string targetDir = Path.Combine(sourceDir, subDir);
						Directory.CreateDirectory(targetDir);

						string bundledPath = Path.Combine(bundledFakelibDir, stubName);
						string targetPath = Path.Combine(targetDir, stubName);
						if (File.Exists(bundledPath) && !File.Exists(targetPath))
						{
							try
							{
								File.Copy(bundledPath, targetPath, overwrite: false);
								stagedCount++;
								string displayDir = string.IsNullOrEmpty(subDir) ? "app0/" : subDir + "/";
								logger?.Invoke($"[stage 0/5] [Backport] Staged missing system module '{stubName}' in {displayDir} (Target SDK 0x{targetSdkVersion.Value:X16} lacks native module).");
							}
							catch { }
						}
					}

					// If libScePsml.sprx was staged or needed, also stage its companion KPN neural model k9.psp to root if available
					// Note: .psp is ALWAYS applied silently in background; NEVER excluded or shown to user.
					if (missingStubs.Contains("libScePsml.sprx", StringComparer.OrdinalIgnoreCase))
					{
						string bundledK9 = Path.Combine(bundledFakelibDir, "k9.psp");
						string targetK9 = Path.Combine(sourceDir, "k9.psp");
						if (File.Exists(bundledK9) && !File.Exists(targetK9))
						{
							try
							{
								File.Copy(bundledK9, targetK9, overwrite: false);
								stagedCount++;
								logger?.Invoke("[stage 0/5] [Backport] Staged companion neural model 'k9.psp' in app0/ for libScePsml (MFSR / KPN upscaler).");
							}
							catch { }
						}
					}

					if (stagedCount > 0)
					{
						logger?.Invoke($"[stage 0/5] [Backport] Staged {stagedCount} required compatibility module(s) for ShadowMount/OnionHEN/FW 4.xx backport.");
					}
				}
				else
				{
					logger?.Invoke($"[stage 0/5] [Backport] Dynamic dependency check: 0 missing compatibility stubs required for Target SDK 0x{targetSdkVersion.Value:X16}. Compatibility staging skipped.");
				}
			}
		}
		else
		{
			logger?.Invoke($"[stage 0/5] Pre-processing complete: {convertedCount} decrypted ELF(s) converted to FSELF{(sanitizedCount > 0 ? $", {sanitizedCount} section table(s) sanitized" : "")}.");
		}
		return convertedCount;
	}

	/// <summary>
	/// Extracts a raw 64-bit ELF from a non-encrypted FSELF (Orbis or Prospero).
	/// </summary>
	public static bool TryUnfself(byte[] selfBytes, out byte[]? elf)
	{
		elf = null;
		if (selfBytes.Length < 64) return false;
		uint magic = BinaryPrimitives.ReadUInt32LittleEndian(selfBytes);
		if (magic != OrbisMagic && magic != Magic) return false;
		ushort numSegs = BinaryPrimitives.ReadUInt16LittleEndian(selfBytes.AsSpan(24, 2));
		int segTableEnd = 32 + numSegs * 32;
		if (segTableEnd + 64 > selfBytes.Length) return false;

		int elfHdrOff = segTableEnd;
		if (selfBytes[elfHdrOff] != 0x7F || selfBytes[elfHdrOff + 1] != (byte)'E' ||
		    selfBytes[elfHdrOff + 2] != (byte)'L' || selfBytes[elfHdrOff + 3] != (byte)'F')
			return false;

		ulong ePhoOff = BinaryPrimitives.ReadUInt64LittleEndian(selfBytes.AsSpan(elfHdrOff + 32, 8));
		ushort ePhentSize = BinaryPrimitives.ReadUInt16LittleEndian(selfBytes.AsSpan(elfHdrOff + 54, 2));
		ushort ePhNum = BinaryPrimitives.ReadUInt16LittleEndian(selfBytes.AsSpan(elfHdrOff + 56, 2));

		long maxElfSize = (long)elfHdrOff + 64 + (long)ePhNum * (long)ePhentSize;
		var phdrs = new List<(uint Type, uint Flags, ulong Offset, ulong Vaddr, ulong Paddr, ulong FileSz, ulong MemSz, ulong Align)>();
		for (int pIdx = 0; pIdx < ePhNum; pIdx++)
		{
			int phdrOff = (int)(elfHdrOff + (long)ePhoOff + pIdx * ePhentSize);
			if (phdrOff + 56 > selfBytes.Length) return false;
			uint pType = BinaryPrimitives.ReadUInt32LittleEndian(selfBytes.AsSpan(phdrOff, 4));
			uint pFlags = BinaryPrimitives.ReadUInt32LittleEndian(selfBytes.AsSpan(phdrOff + 4, 4));
			ulong pOffset = BinaryPrimitives.ReadUInt64LittleEndian(selfBytes.AsSpan(phdrOff + 8, 8));
			ulong pVaddr = BinaryPrimitives.ReadUInt64LittleEndian(selfBytes.AsSpan(phdrOff + 16, 8));
			ulong pPaddr = BinaryPrimitives.ReadUInt64LittleEndian(selfBytes.AsSpan(phdrOff + 24, 8));
			ulong pFilesz = BinaryPrimitives.ReadUInt64LittleEndian(selfBytes.AsSpan(phdrOff + 32, 8));
			ulong pMemsz = BinaryPrimitives.ReadUInt64LittleEndian(selfBytes.AsSpan(phdrOff + 40, 8));
			ulong pAlign = BinaryPrimitives.ReadUInt64LittleEndian(selfBytes.AsSpan(phdrOff + 48, 8));
			phdrs.Add((pType, pFlags, pOffset, pVaddr, pPaddr, pFilesz, pMemsz, pAlign));
			if ((long)pOffset + (long)pFilesz > maxElfSize)
			{
				maxElfSize = (long)pOffset + (long)pFilesz;
			}
		}

		byte[] elfBuffer = new byte[maxElfSize];
		int hdrLen = (int)((long)ePhoOff + (long)ePhNum * (long)ePhentSize);
		Array.Copy(selfBytes, elfHdrOff, elfBuffer, 0, Math.Min(hdrLen, selfBytes.Length - elfHdrOff));

		for (int i = 0; i < numSegs; i++)
		{
			int segOff = 32 + i * 32;
			ulong flags = BinaryPrimitives.ReadUInt64LittleEndian(selfBytes.AsSpan(segOff, 8));
			ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(selfBytes.AsSpan(segOff + 8, 8));
			ulong fileSz = BinaryPrimitives.ReadUInt64LittleEndian(selfBytes.AsSpan(segOff + 16, 8));
			int phdrIdx = (int)(flags >> 20);
			if (phdrIdx < ePhNum && (flags & 0x800) != 0)
			{
				var p = phdrs[phdrIdx];
				if ((long)p.Offset + (long)fileSz <= (long)elfBuffer.Length && (long)offset + (long)fileSz <= (long)selfBytes.Length)
				{
					Array.Copy(selfBytes, (int)offset, elfBuffer, (int)p.Offset, (int)fileSz);
				}
			}
		}

		elf = elfBuffer;
		return true;
	}

	public record FakelibParsedItem(
		string FileName,
		string ModuleName,
		string TargetSubdirectory,
		string Description,
		long Size,
		string SizeFormatted,
		bool IsRequired,
		bool IsStaged,
		bool IsMissingStub = false,
		bool IsEbootBypassed = false,
		string? StatusMessage = null
	)
	{
		public bool HasAnomaly => IsMissingStub;
		public string? AnomalyReason => IsMissingStub ? (StatusMessage ?? "Missing stub in project folder") : null;
	}

	public record EbootSyncResult(
		bool Success,
		int PatchedCount,
		int RestoredCount,
		List<string> BypassedModules,
		List<string> RestoredModules,
		string Message
	);

	/// <summary>
	/// Checks if a compatibility module was originally imported by eboot.bin (in backup)
	/// but is currently bypassed/neutralized in the active eboot.bin.
	/// </summary>
	public static bool IsModuleBypassedInEboot(string sourceDir, string fileName)
	{
		string ebootPath = Path.Combine(sourceDir, "eboot.bin");
		string bakPath = Path.Combine(sourceDir, "eboot.bin.bak");
		if (!File.Exists(ebootPath) || !File.Exists(bakPath)) return false;

		try
		{
			byte[] nameBytes = Encoding.ASCII.GetBytes(fileName);
			byte[] curBytes = File.ReadAllBytes(ebootPath);
			if (curBytes.AsSpan().IndexOf(nameBytes) >= 0) return false;

			byte[] bakBytes = File.ReadAllBytes(bakPath);
			return bakBytes.AsSpan().IndexOf(nameBytes) >= 0;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>
	/// Scans executable binaries in sourceDir and cross-references against bundled fakelib stubs
	/// and target SDK version to report candidate/detected fakelibs for interactive UI selection.
	/// NOTE: Any .psp file is strictly background and is never returned in this list.
	/// </summary>
	public static List<FakelibParsedItem> GetDetectedFakelibStatus(
		string sourceDir,
		ulong targetSdkVersion,
		string? bundledFakelibDir = null)
	{
		var result = new List<FakelibParsedItem>();
		if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
		{
			return result;
		}

		var allImports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		string[]? candidateFiles = null;
		if (!string.IsNullOrEmpty(bundledFakelibDir) && Directory.Exists(bundledFakelibDir))
		{
			try
			{
				candidateFiles = Directory.EnumerateFiles(bundledFakelibDir, "*.*").ToArray();
			}
			catch { }
		}

		// Build exclusion set: never scan the fakelib stubs themselves as game ELFs.
		var knownFakelibNames = new HashSet<string>(
			KnownCompatibilityModules.Select(m => m.FileName),
			StringComparer.OrdinalIgnoreCase);

		string[] extensions = { ".bin", ".elf", ".prx", ".sprx" };
		try
		{
			foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
			{
				string ext = Path.GetExtension(file).ToLowerInvariant();
				string name = Path.GetFileName(file);
				if (name.StartsWith("._", StringComparison.Ordinal) || name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase)) continue;
				if (!extensions.Contains(ext) || file.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".esbak", StringComparison.OrdinalIgnoreCase)) continue;

				if (knownFakelibNames.Contains(name)) continue;

				string relPath = Path.GetRelativePath(sourceDir, file);
				string[] pathParts = relPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
				if (pathParts.Any(p => p.Equals("fakelib", StringComparison.OrdinalIgnoreCase))) continue;

				try
				{
					if (new FileInfo(file).Length < 64) continue;
					byte[] elfBytes;
					byte[] header = new byte[64];
					using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
					{
						if (fs.Read(header, 0, 64) != 64) continue;
					}

					uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
					if (magic == Magic || magic == OrbisMagic)
					{
						byte[] selfBytes = File.ReadAllBytes(file);
						if (TryUnfself(selfBytes, out byte[]? extractedElf) && extractedElf != null)
						{
							elfBytes = extractedElf;
						}
						else continue;
					}
					else if (IsElf(header))
					{
						elfBytes = File.ReadAllBytes(file);
					}
					else continue;

					var importedLibs = DetectImportedCompatibilityModules(elfBytes, candidateFiles);
					foreach (var lib in importedLibs)
					{
						allImports.Add(lib);
					}
				}
				catch { }
			}
		}
		catch { }

		// Always ensure .psp companion is staged silently in background if PSML or ML is present
		if (allImports.Contains("libScePsml.sprx") && !string.IsNullOrEmpty(bundledFakelibDir))
		{
			try
			{
				string bundledK9 = Path.Combine(bundledFakelibDir, "k9.psp");
				string targetK9 = Path.Combine(sourceDir, "k9.psp");
				if (File.Exists(bundledK9) && !File.Exists(targetK9))
				{
					File.Copy(bundledK9, targetK9, overwrite: false);
				}
			}
			catch { }
		}

		// Enumerate known modules
		foreach (var rule in KnownCompatibilityModules)
		{
			// CRITICAL: NEVER show .psp in the GUI
			if (rule.FileName.EndsWith(".psp", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			string targetPath = Path.Combine(sourceDir, rule.TargetSubdirectory, rule.FileName);
			bool isStaged = File.Exists(targetPath);
			bool isImported = allImports.Contains(rule.FileName);
			bool isMissingOnSdk = IsModuleMissingOnTargetSdk(rule.FileName, targetSdkVersion);
			bool isRequired = isImported && isMissingOnSdk;
			bool isBypassed = IsModuleBypassedInEboot(sourceDir, rule.FileName);
			bool isMissingStub = isRequired && !isStaged && !isBypassed;

			string statusMessage;
			if (isRequired && isStaged)
			{
				statusMessage = "Required: Imported by game ELF and staged in project";
			}
			else if (isMissingStub)
			{
				statusMessage = "Missing stub: Imported by game ELF, but missing from project folder";
			}
			else if (isBypassed)
			{
				statusMessage = "Bypassed: Dependency neutralized in eboot.bin (no stub needed)";
			}
			else if (isStaged)
			{
				statusMessage = "Optional: Staged in project folder";
			}
			else
			{
				statusMessage = "Optional module";
			}

			long fileSize = 0;
			if (!string.IsNullOrEmpty(bundledFakelibDir))
			{
				string bPath = Path.Combine(bundledFakelibDir, rule.FileName);
				if (File.Exists(bPath))
				{
					fileSize = new FileInfo(bPath).Length;
				}
			}
			if (fileSize == 0 && isStaged)
			{
				fileSize = new FileInfo(targetPath).Length;
			}

			// Include if imported, or already staged in folder, or bypassed in eboot, or missing on target SDK and exists bundled
			bool shouldShow = isImported || isStaged || isBypassed || (isMissingOnSdk && fileSize > 0);
			if (shouldShow)
			{
				result.Add(new FakelibParsedItem(
					FileName: rule.FileName,
					ModuleName: rule.ModuleName,
					TargetSubdirectory: rule.TargetSubdirectory,
					Description: rule.Description,
					Size: fileSize,
					SizeFormatted: FormatBytesHelper(fileSize),
					IsRequired: isRequired,
					IsStaged: isStaged,
					IsMissingStub: isMissingStub,
					IsEbootBypassed: isBypassed,
					StatusMessage: statusMessage
				));
			}
		}

		return result
			.OrderByDescending(r => r.IsMissingStub)
			.ThenByDescending(r => r.IsRequired)
			.ThenByDescending(r => r.IsStaged)
			.ThenBy(r => r.FileName)
			.ToList();
	}

	/// <summary>
	/// Stages all missing required compatibility modules from the bundled fakelib directory into the project folder.
	/// </summary>
	public static int StageAllMissingStubs(string sourceDir, string? bundledFakelibDir, ulong targetSdkVersion)
	{
		if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir)) return 0;
		if (string.IsNullOrEmpty(bundledFakelibDir) || !Directory.Exists(bundledFakelibDir)) return 0;

		var status = GetDetectedFakelibStatus(sourceDir, targetSdkVersion, bundledFakelibDir);
		int count = 0;
		foreach (var item in status.Where(f => f.IsMissingStub))
		{
			string targetDir = Path.Combine(sourceDir, item.TargetSubdirectory);
			string targetPath = Path.Combine(targetDir, item.FileName);
			string bundledPath = Path.Combine(bundledFakelibDir, item.FileName);
			if (File.Exists(bundledPath) && !File.Exists(targetPath))
			{
				try
				{
					Directory.CreateDirectory(targetDir);
					File.Copy(bundledPath, targetPath, overwrite: true);
					count++;
				}
				catch { }
			}
		}
		return count;
	}

	/// <summary>
	/// Synchronizes a single fakelib file live in the target directory (app0/ or fakelib/ or sce_module/).
	/// NOTE: Any .psp file is strictly background and cannot be manipulated or deleted via this method.
	/// If syncEboot is true, also updates eboot.bin so binary dependencies match the staged state.
	/// </summary>
	public static bool SyncFakelibLive(string sourceDir, string fileName, bool enable, string? bundledFakelibDir, ulong targetSdkVersion = 0, bool syncEboot = false)
	{
		if (string.IsNullOrWhiteSpace(sourceDir) || string.IsNullOrWhiteSpace(fileName)) return false;

		// CRITICAL: .psp files are strictly background. Never allow GUI toggle or deletion of .psp
		if (fileName.EndsWith(".psp", StringComparison.OrdinalIgnoreCase)) return false;

		string subDir = GetModuleTargetSubdirectory(fileName);
		string targetDir = Path.Combine(sourceDir, subDir);
		string targetPath = Path.Combine(targetDir, fileName);

		if (enable)
		{
			if (string.IsNullOrEmpty(bundledFakelibDir)) return false;
			string bundledPath = Path.Combine(bundledFakelibDir, fileName);
			if (!File.Exists(bundledPath)) return false;

			Directory.CreateDirectory(targetDir);
			File.Copy(bundledPath, targetPath, overwrite: true);

			if (syncEboot)
			{
				var activeList = new List<string> { fileName };
				foreach (var r in KnownCompatibilityModules)
				{
					if (File.Exists(Path.Combine(sourceDir, r.TargetSubdirectory, r.FileName)))
						activeList.Add(r.FileName);
				}
				SyncEbootWithFakelibs(sourceDir, activeList.Distinct(StringComparer.OrdinalIgnoreCase), targetSdkVersion, bundledFakelibDir);
			}

			return true;
		}
		else
		{
			// Flexible deletion: allow user to remove stubs (with optional eboot patch sync to prevent crash)
			bool deleted = false;
			if (File.Exists(targetPath))
			{
				File.Delete(targetPath);
				deleted = true;
				try
				{
					if (Directory.Exists(targetDir) && !Directory.EnumerateFileSystemEntries(targetDir).Any())
					{
						Directory.Delete(targetDir);
					}
				}
				catch { }
			}

			if (syncEboot)
			{
				var activeList = new List<string>();
				foreach (var r in KnownCompatibilityModules)
				{
					if (!r.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase) &&
					    File.Exists(Path.Combine(sourceDir, r.TargetSubdirectory, r.FileName)))
					{
						activeList.Add(r.FileName);
					}
				}
				SyncEbootWithFakelibs(sourceDir, activeList, targetSdkVersion, bundledFakelibDir);
			}

			return deleted || syncEboot;
		}
	}

	/// <summary>
	/// Synchronizes eboot.bin module import dependencies with the set of active/staged fakelibs:
	/// - Disabled modules have their ELF dynamic imports neutralized/redirected to libkernel.sprx to prevent startup crashes.
	/// - Enabled modules have their imports restored (from eboot.bin.bak).
	/// - AMPR and other backport bypasses are automatically aligned.
	/// </summary>
	public static EbootSyncResult SyncEbootWithFakelibs(
		string sourceDir,
		IEnumerable<string> activeFakelibNames,
		ulong targetSdkVersion,
		string? bundledFakelibDir = null,
		Action<string>? logger = null)
	{
		if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
			return new EbootSyncResult(false, 0, 0, new(), new(), "Source directory does not exist.");

		string ebootPath = Path.Combine(sourceDir, "eboot.bin");
		if (!File.Exists(ebootPath))
			return new EbootSyncResult(false, 0, 0, new(), new(), "eboot.bin not found in source directory.");

		string bakPath = Path.Combine(sourceDir, "eboot.bin.bak");
		if (!File.Exists(bakPath))
		{
			try { File.Copy(ebootPath, bakPath, overwrite: false); } catch { }
		}

		var activeSet = new HashSet<string>(activeFakelibNames, StringComparer.OrdinalIgnoreCase);
		var bypassed = new List<string>();
		var restored = new List<string>();

		try
		{
			byte[] ebootBytes = File.ReadAllBytes(ebootPath);
			byte[] elfBytes;
			bool isFself = false;
			FselfOptions? options = null;
			uint magic = BinaryPrimitives.ReadUInt32LittleEndian(ebootBytes);

			if (magic == Magic || magic == OrbisMagic)
			{
				isFself = true;
				if (!TryUnfself(ebootBytes, out byte[]? extracted) || extracted == null)
					return new EbootSyncResult(false, 0, 0, new(), new(), "Could not unpack eboot.bin FSELF.");
				elfBytes = extracted;
				var trailerSpan = GetSceVersionRecords(ebootBytes);
				if (!trailerSpan.IsEmpty)
				{
					options = new FselfOptions
					{
						SceVersionRecords = targetSdkVersion > 0 ? PatchSceVersionRecords(trailerSpan.ToArray(), targetSdkVersion) : trailerSpan.ToArray(),
						FirmwareVersion = targetSdkVersion > 0 ? targetSdkVersion : 0uL,
						ProgramType = 268435713u
					};
				}
				else if (targetSdkVersion > 0)
				{
					options = new FselfOptions
					{
						FirmwareVersion = targetSdkVersion,
						ProgramType = 268435713u
					};
				}
			}
			else if (IsElf(ebootBytes.AsSpan(0, Math.Min(64, ebootBytes.Length))))
			{
				elfBytes = (byte[])ebootBytes.Clone();
			}
			else
			{
				return new EbootSyncResult(false, 0, 0, new(), new(), "eboot.bin is neither valid ELF nor FSELF.");
			}

			// Read backup for restoration reference if available
			byte[]? bakElf = null;
			if (File.Exists(bakPath))
			{
				try
				{
					byte[] bakBytes = File.ReadAllBytes(bakPath);
					uint bMagic = BinaryPrimitives.ReadUInt32LittleEndian(bakBytes);
					if (bMagic == Magic || bMagic == OrbisMagic)
					{
						if (TryUnfself(bakBytes, out byte[]? bExtracted) && bExtracted != null)
							bakElf = bExtracted;
					}
					else if (IsElf(bakBytes.AsSpan(0, Math.Min(64, bakBytes.Length))))
					{
						bakElf = bakBytes;
					}
				}
				catch { }
			}

			foreach (var rule in KnownCompatibilityModules)
			{
				if (rule.FileName.EndsWith(".psp", StringComparison.OrdinalIgnoreCase)) continue;
				if (rule.FileName.StartsWith("libkernel", StringComparison.OrdinalIgnoreCase)) continue;

				bool shouldBeActive = activeSet.Contains(rule.FileName);
				byte[] nameBytes = Encoding.ASCII.GetBytes(rule.FileName);
				string bypassStr = rule.FileName.Length >= 14 ? "libkernel.sprx" : "libk.prx";
				byte[] bypassBytes = Encoding.ASCII.GetBytes(bypassStr.PadRight(rule.FileName.Length, '\0'));

				if (!shouldBeActive)
				{
					// User wants this module DISABLED / BYPASSED:
					// 1. Delete staged file from disk
					string subDir = GetModuleTargetSubdirectory(rule.FileName);
					string targetPath = Path.Combine(sourceDir, subDir, rule.FileName);
					if (File.Exists(targetPath))
					{
						try { File.Delete(targetPath); } catch { }
					}

					// 2. Bypass in ELF if currently present
					int matchIdx;
					int searchPos = 0;
					bool anyPatched = false;
					while (searchPos + nameBytes.Length <= elfBytes.Length &&
					       (matchIdx = elfBytes.AsSpan(searchPos).IndexOf(nameBytes)) >= 0)
					{
						int actualPos = searchPos + matchIdx;
						bypassBytes.CopyTo(elfBytes, actualPos);
						anyPatched = true;
						searchPos = actualPos + bypassBytes.Length;
					}

					if (anyPatched)
					{
						bypassed.Add(rule.FileName);
						logger?.Invoke($"[Eboot Patch Sync] Bypassed dependency '{rule.FileName}' in eboot.bin (redirected to libkernel)");
					}

					if (rule.FileName.Equals("libSceAmpr.sprx", StringComparison.OrdinalIgnoreCase))
					{
						PatchElfAmprBypass(elfBytes, logger);
					}
				}
				else
				{
					// User wants this module ACTIVE / STAGED:
					// 1. Stage file to disk from bundled if missing
					if (!string.IsNullOrEmpty(bundledFakelibDir))
					{
						string subDir = GetModuleTargetSubdirectory(rule.FileName);
						string targetDir = Path.Combine(sourceDir, subDir);
						string targetPath = Path.Combine(targetDir, rule.FileName);
						string bundledPath = Path.Combine(bundledFakelibDir, rule.FileName);
						if (File.Exists(bundledPath) && !File.Exists(targetPath))
						{
							try
							{
								Directory.CreateDirectory(targetDir);
								File.Copy(bundledPath, targetPath, overwrite: true);
							}
							catch { }
						}
					}

					// 2. Restore in ELF if it was bypassed and backup has it
					if (bakElf != null)
					{
						int bSearchPos = 0;
						int bIdx;
						bool anyRestored = false;
						while (bSearchPos + nameBytes.Length <= bakElf.Length &&
						       (bIdx = bakElf.AsSpan(bSearchPos).IndexOf(nameBytes)) >= 0)
						{
							int actualPos = bSearchPos + bIdx;
							if (actualPos + nameBytes.Length <= elfBytes.Length)
							{
								if (elfBytes.AsSpan(actualPos, nameBytes.Length).SequenceEqual(bypassBytes))
								{
									nameBytes.CopyTo(elfBytes, actualPos);
									anyRestored = true;
								}
							}
							bSearchPos = actualPos + nameBytes.Length;
						}

						if (anyRestored)
						{
							restored.Add(rule.FileName);
							logger?.Invoke($"[Eboot Patch Sync] Restored dependency '{rule.FileName}' in eboot.bin");
						}
					}
				}
			}

			// Apply target SDK version patches if targetSdk > 0
			if (targetSdkVersion > 0)
			{
				PatchElfSceVersionSection(elfBytes, targetSdkVersion);
				PatchElfProcParam(elfBytes, targetSdkVersion);
				PatchElfSymbolVersions(elfBytes, targetSdkVersion, logger);
				PatchElfExecutableBackport(elfBytes, targetSdkVersion, logger);
			}

			// Write back
			if (isFself)
			{
				byte[] fself = MakeFself(elfBytes, options);
				File.WriteAllBytes(ebootPath, fself);
			}
			else
			{
				File.WriteAllBytes(ebootPath, elfBytes);
			}

			string summary = $"Synchronized eboot.bin: {bypassed.Count} bypassed, {restored.Count} restored.";
			logger?.Invoke($"[Eboot Patch Sync] {summary}");
			return new EbootSyncResult(true, bypassed.Count, restored.Count, bypassed, restored, summary);
		}
		catch (Exception ex)
		{
			logger?.Invoke($"[Eboot Patch Sync Error] {ex.Message}");
			return new EbootSyncResult(false, 0, 0, bypassed, restored, ex.Message);
		}
	}

	private static string FormatBytesHelper(long bytes)
	{
		if (bytes <= 0) return "0 B";
		string[] units = { "B", "KB", "MB", "GB" };
		double len = bytes;
		int order = 0;
		while (len >= 1024 && order < units.Length - 1)
		{
			order++;
			len /= 1024;
		}
		return $"{len:0.##} {units[order]}";
	}
}
