using System;

namespace LibProsperoPkg.Util;

/// <summary>
/// Standard reflected CRC-32C (Castagnoli, reflected polynomial 0x82F63B78, init/xorout
/// 0xFFFFFFFF). This is the exact reducer reference tool uses to build
/// <c>playgo-chunk.crc</c>; see the file header for the decoded layout and validation.
/// </summary>
public static class ProsperoCrc32C
{
	/// <summary>The reflected CRC-32C generator polynomial (0x1EDC6F41 reflected).</summary>
	public const uint ReflectedPolynomial = 2197175160u;

	private static readonly uint[] Table = BuildTable();

	private static uint[] BuildTable()
	{
		uint[] array = new uint[256];
		for (uint num = 0u; num < 256; num++)
		{
			uint num2 = num;
			for (int i = 0; i < 8; i++)
			{
				num2 = (((num2 & 1) != 0) ? (0x82F63B78u ^ (num2 >> 1)) : (num2 >> 1));
			}
			array[num] = num2;
		}
		return array;
	}

	/// <summary>
	/// Continues a running CRC-32C over <paramref name="data" />. Pass <see cref="F:System.UInt32.MaxValue" />
	/// as the initial <paramref name="crc" /> for a fresh checksum; the returned value is the
	/// *internal* running register (NOT yet finalized). Finalize with <c>~result</c> or use
	/// <see cref="M:LibProsperoPkg.Util.ProsperoCrc32C.Compute(System.ReadOnlySpan{System.Byte})" /> for one-shot use.
	/// </summary>
	public static uint Update(uint crc, ReadOnlySpan<byte> data)
	{
		uint num = crc;
		ReadOnlySpan<byte> readOnlySpan = data;
		for (int i = 0; i < readOnlySpan.Length; i++)
		{
			byte b = readOnlySpan[i];
			num = Table[(num ^ b) & 0xFF] ^ (num >> 8);
		}
		return num;
	}

	/// <summary>Computes the finalized standard CRC-32C of <paramref name="data" />.</summary>
	public static uint Compute(ReadOnlySpan<byte> data)
	{
		return ~Update(uint.MaxValue, data);
	}
}
