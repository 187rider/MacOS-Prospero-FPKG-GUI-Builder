using System;
using System.Buffers.Binary;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace LibProsperoPkg.PKG;

public static class ProsperoSystemFiles
{
	public const uint NpbindMagic = 3532955672u;

	public const int NpbindSize = 532;

	public const int NptitleSize = 160;

	public const int LicenseDatSize = 1024;

	public const int LicenseInfoSize = 512;

	private const ushort NpbindCommIdTag = 16;

	private const int NpbindTlvStart = 128;

	private const int NpbindTlvEnd = 512;

	private static ReadOnlySpan<byte> NptitleMagic => "NPTD"u8;

	private static ReadOnlySpan<byte> RifMagic => "RIF\0"u8;

	public static bool ValidateNpbind(ReadOnlySpan<byte> data, out string? commId, out string? error)
	{
		commId = null;
		if (data.Length != 532)
		{
			error = $"npbind.dat must be {532} bytes (got {data.Length}).";
			return false;
		}
		if (BinaryPrimitives.ReadUInt32BigEndian(data) != 3532955672u)
		{
			error = "npbind.dat has a bad magic.";
			return false;
		}
		if (BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4)) != 1)
		{
			error = "npbind.dat has an unsupported version.";
			return false;
		}
		uint num = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(12));
		if (num != 532)
		{
			error = $"npbind.dat size field ({num}) does not match its length.";
			return false;
		}
		ushort num3;
		for (int i = 128; i + 4 <= 512; i += 4 + num3)
		{
			ushort num2 = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i));
			num3 = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i + 2));
			if (num2 == 0)
			{
				break;
			}
			if (i + 4 + num3 > data.Length)
			{
				error = "npbind.dat has a truncated record.";
				return false;
			}
			if (num2 == 16)
			{
				commId = Encoding.ASCII.GetString(data.Slice(i + 4, num3)).TrimEnd('\0');
				break;
			}
		}
		if (string.IsNullOrEmpty(commId))
		{
			error = "npbind.dat is missing the communication id record.";
			return false;
		}
		Span<byte> span = stackalloc byte[20];
		SHA1.HashData(data.Slice(0, 512), span);
		if (!((ReadOnlySpan<byte>)span).SequenceEqual(data.Slice(512)))
		{
			error = "npbind.dat has an invalid trailing SHA-1 authentication code.";
			return false;
		}
		error = null;
		return true;
	}

	public static bool ValidateNptitle(ReadOnlySpan<byte> data, out string? titleId, out string? error)
	{
		titleId = null;
		if (data.Length != 160)
		{
			error = $"nptitle.dat must be {160} bytes (got {data.Length}).";
			return false;
		}
		if (!data.Slice(0, 4).SequenceEqual(NptitleMagic))
		{
			error = "nptitle.dat has a bad magic.";
			return false;
		}
		uint num = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4));
		if (num != 128)
		{
			error = $"nptitle.dat has an unexpected signature offset (0x{num:x}).";
			return false;
		}
		titleId = Encoding.ASCII.GetString(data.Slice(16, 16)).TrimEnd('\0');
		if (titleId.Length == 0)
		{
			error = "nptitle.dat is missing its title id.";
			return false;
		}
		error = null;
		return true;
	}

	public static bool ValidateLicenseDat(ReadOnlySpan<byte> data, out string? error)
	{
		return ValidateLicenseDat(data, null, out error);
	}

	public static bool ValidateLicenseDat(ReadOnlySpan<byte> data, string? expectedContentId, out string? error)
	{
		if (data.Length != 1024)
		{
			error = $"license.dat must be {1024} bytes (got {data.Length}).";
			return false;
		}
		if (!data.Slice(0, RifMagic.Length).SequenceEqual(RifMagic))
		{
			error = "license.dat has a bad RIF magic.";
			return false;
		}
		return ValidateEmbeddedContentId(data, 32, "license.dat", expectedContentId, out error);
	}

	public static bool ValidateLicenseInfo(ReadOnlySpan<byte> data, out string? error)
	{
		return ValidateLicenseInfo(data, null, out error);
	}

	public static bool ValidateLicenseInfo(ReadOnlySpan<byte> data, string? expectedContentId, out string? error)
	{
		return ValidateLicenseInfo(data, expectedContentId, default, out error);
	}

	public static bool ValidateLicenseInfo(ReadOnlySpan<byte> data, string? expectedContentId, ReadOnlySpan<byte> expectedEntitlementKey, out string? error)
	{
		if (data.Length != 512)
		{
			error = $"license.info must be {512} bytes (got {data.Length}).";
			return false;
		}
		if (!ValidateEmbeddedContentId(data, 0, "license.info", expectedContentId, out error))
		{
			return false;
		}
		if (!expectedEntitlementKey.IsEmpty && !data.Slice(48, 16).SequenceEqual(expectedEntitlementKey))
		{
			error = "license.info entitlement key does not match the GP5 package entitlement_key.";
			return false;
		}
		return true;
	}

	public static bool Validate(string relativeName, ReadOnlySpan<byte> data, out string? error)
	{
		string commId;
		switch (relativeName)
		{
		case "npbind.dat":
			return ValidateNpbind(data, out commId, out error);
		case "nptitle.dat":
			return ValidateNptitle(data, out commId, out error);
		case "license.dat":
			return ValidateLicenseDat(data, out error);
		case "license.info":
			return ValidateLicenseInfo(data, out error);
		default:
			error = null;
			return true;
		}
	}

	private static bool ValidateEmbeddedContentId(ReadOnlySpan<byte> data, int offset, string name, string? expectedContentId, out string? error)
	{
		string text = Encoding.ASCII.GetString(data.Slice(offset, 36));
		if (text.Any((char c) => (c < ' ' || c > '~') ? true : false))
		{
			error = name + " contains a non-ASCII content id.";
			return false;
		}
		if (expectedContentId != null && !string.Equals(text, expectedContentId, StringComparison.Ordinal))
		{
			error = $"{name} content id '{text}' does not match package content id '{expectedContentId}'.";
			return false;
		}
		error = null;
		return true;
	}
}
