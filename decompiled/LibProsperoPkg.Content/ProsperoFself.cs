using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

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

	private static ReadOnlySpan<byte> GetSceVersionRecords(byte[] image)
	{
		if (IsElf(image))
		{
			return FindElfSection(image, ".sceversion");
		}
		if (IsSelf(image))
		{
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
		int num5 = list.Count * 2;
		int num6 = 32 + num5 * 32;
		int num7 = 64 + num4 * 56;
		int num8 = AlignUp(num6 + num7, 16);
		int num9 = num8 + 64 + 48;
		int num10 = checked(num5 * 80 + 80 + 544);
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
		int num13 = num12;
		ReadOnlySpan<byte> readOnlySpan = FindElfSection(elf, ".sceversion");
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
		BinaryPrimitives.WriteUInt32LittleEndian(span, 4009038932u);
		span[4] = 16;
		span[5] = 1;
		span[6] = 1;
		span[7] = 18;
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8), 268435713u);
		BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(12), (ushort)num9);
		BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(14), (ushort)num10);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(16), (ulong)num13);
		BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(24), (ushort)num5);
		BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(26), 50);
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
		ulong value = options.AuthorityId ?? DeriveAuthorityId(elf, eType);
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
	/// Recursively scans a decrypted PS5 game dump folder for executable binaries (.bin, .elf, .prx, .sprx),
	/// sanitizes any truncated section header tables, and converts decrypted ELFs into fake-signed FSELFs.
	/// Equivalent to the alex-free/ps5-make-fself-recursive tool.
	/// </summary>
	/// <param name="sourceDir">Path to the game dump root folder.</param>
	/// <param name="logger">Optional progress callback.</param>
	/// <returns>Number of ELFs converted to FSELF.</returns>
	public static int RecursiveMakeFself(string sourceDir, Action<string>? logger = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourceDir, nameof(sourceDir));
		if (!Directory.Exists(sourceDir)) return 0;

		logger?.Invoke("[stage 0/5] Pre-processing game dump (recursive make_fself)...");

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

		int convertedCount = 0;
		int sanitizedCount = 0;
		string[] extensions = { ".bin", ".elf", ".prx", ".sprx" };

		try
		{
			foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
			{
				string ext = Path.GetExtension(file).ToLowerInvariant();
				if (!extensions.Contains(ext)) continue;

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
					string relPath = Path.GetRelativePath(sourceDir, file);
					byte[] elfBytes;

					// If already a native PS5 SELF/FSELF (0xEEF51454 / 54 14 f5 ee), nothing to do
					if (magic == Magic)
					{
						continue;
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

					FselfOptions? options = null;
					string fileName = Path.GetFileName(file);
					if (!fileName.Equals("eboot.bin", StringComparison.OrdinalIgnoreCase) &&
					    applicationSceVersion != null &&
					    !TryGetSceVersionRecord(elfBytes, out _))
					{
						options = new FselfOptions
						{
							SceVersionName = Path.GetFileNameWithoutExtension(fileName),
							SceVersionRecord = applicationSceVersion
						};
					}

					byte[] fself = MakeFself(elfBytes, options);
					File.WriteAllBytes(file, fself);
					convertedCount++;

					logger?.Invoke($"[stage 0/5] Fake-signed native PS5 FSELF (0xEEF51454): {relPath} ({fself.Length:N0} bytes)");
				}
				catch (Exception ex)
				{
					string relPath = Path.GetRelativePath(sourceDir, file);
					logger?.Invoke($"[stage 0/5] Warning: could not fake-sign '{relPath}': {ex.Message}");
				}
			}
		}
		catch (Exception ex)
		{
			logger?.Invoke($"[stage 0/5] Scan warning: {ex.Message}");
		}

		logger?.Invoke($"[stage 0/5] Pre-processing complete: {convertedCount} decrypted ELF(s) converted to FSELF{(sanitizedCount > 0 ? $", {sanitizedCount} section table(s) sanitized" : "")}.");
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
}
