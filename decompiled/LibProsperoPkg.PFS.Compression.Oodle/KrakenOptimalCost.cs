using System;
using System.Buffers.Binary;
using System.Numerics;

namespace LibProsperoPkg.PFS.Compression.Oodle;

/// <summary>
/// Byte-exact integer implementation of the reference newLZ optimal-parse cost model (the
/// fixed-point log2 quantizer behind histogram-to-code-cost conversion). Deterministic; no floating
/// point is used on any path that influences a cost. All costs are expressed in
/// <c>bits * 32</c> (the 1/32-bit fixed-point the parser compares).
/// </summary>
internal static class KrakenOptimalCost
{
	/// <summary>
	/// The per-pass quantized cost tables (<c>codecosts</c>, serialized size ~0x1808 bytes). Every
	/// table is a 256-entry <c>bits*32</c> cost array built by <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.KrakenOptimalCost.BuildFromPassinfo(System.ReadOnlySpan{System.Int32},System.Int32)" />. Byte
	/// offsets of the serialized layout are noted for each field.
	/// </summary>
	internal sealed class CodeCosts
	{
		/// <summary>codecosts[0]: literal model — 1 = raw literals, otherwise "sub" (delta) literals.</summary>
		internal int LitMode;

		/// <summary>codecosts+0x04: literal-delta mask — 0 (raw) or 0xff (sub).</summary>
		internal int LitSubMask;

		/// <summary>codecosts+0x08: literal-byte cost table, indexed by <c>(byte - (ref &amp; mask)) &amp; 0xff</c>.</summary>
		internal readonly int[] Lit = new int[256];

		/// <summary>codecosts+0x408: command/packet cost table, indexed by the newLZ command byte.</summary>
		internal readonly int[] Packet = new int[256];

		/// <summary>codecosts+0x808: offset alt-modulo (offset coding mode; 0 = standard bucket reader).</summary>
		internal int OffsetAltModulo;

		/// <summary>codecosts+0x80c: offset-bucket cost table, indexed by the packed-offset bucket byte.</summary>
		internal readonly int[] OffsetBucket = new int[256];

		/// <summary>codecosts+0xc0c: offset alt cost table (only built when <see cref="F:LibProsperoPkg.PFS.Compression.Oodle.KrakenOptimalCost.CodeCosts.OffsetAltModulo" /> &gt; 1).</summary>
		internal readonly int[] OffsetAlt = new int[256];

		/// <summary>codecosts+0x100c: length cost table; [0..0xFE] per-length, [0xFF] = escape base.</summary>
		internal readonly int[] Length = new int[256];
	}

	/// <summary>Alphabet size every newLZ entropy array is built over (asserted == 256 by the format).</summary>
	internal const int Alphabet = 256;

	/// <summary>
	/// 65-entry unsigned-16 mantissa table. Entry
	/// <c>k = round( log2(1 + k/64) * 8192 )</c>; <c>Table[0] = 0</c> (log2 1), <c>Table[64] = 0x2000</c>
	/// (log2 2 = one full bit, 8192). Interpolated by <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.KrakenOptimalCost.Log2Fix(System.UInt32)" />.
	/// </summary>
	private static readonly ushort[] Log2Mantissa = new ushort[65]
	{
		0, 183, 364, 541, 716, 889, 1059, 1227, 1392, 1555,
		1716, 1874, 2031, 2186, 2338, 2489, 2637, 2784, 2929, 3072,
		3214, 3354, 3492, 3629, 3764, 3897, 4029, 4160, 4289, 4417,
		4543, 4668, 4792, 4914, 5036, 5156, 5274, 5392, 5509, 5624,
		5738, 5851, 5963, 6074, 6184, 6293, 6401, 6508, 6614, 6719,
		6823, 6926, 7029, 7130, 7231, 7330, 7429, 7527, 7625, 7721,
		7817, 7912, 8006, 8099, 8192
	};

	/// <summary>
	/// <c>bitlen(x)</c> = <c>x == 0 ? 0 : 32 - clz(x)</c>. The index of
	/// the most-significant set bit plus one.
	/// </summary>
	internal static int BitLen(uint x)
	{
		return 32 - BitOperations.LeadingZeroCount(x);
	}

	/// <summary>
	/// Fixed-point <c>log2</c> of the reciprocal, scaled by
	/// 2^13: returns <c>round( log2(2^32 / x) * 8192 )</c>. Exact for powers of two
	/// (<c>Log2Fix(2^k) = (32 - k) * 8192</c>); table-interpolated otherwise. <paramref name="x" /> must be
	/// non-zero (the caller always passes <c>count*4+1 &gt;= 1</c>).
	/// </summary>
	internal static int Log2Fix(uint x)
	{
		int num = BitLen(x) - 1;
		uint num2 = x << ((32 - num) & 0x1F);
		int num3 = (int)(num2 >> 26);
		int num4 = Log2Mantissa[num3];
		ushort num5 = Log2Mantissa[num3 + 1];
		int num6 = (int)((num2 & 0x3FFFFFF) >> 10);
		int num7 = (num5 - num4) * num6 + 32768 >> 16;
		return (32 - num) * 8192 - num4 - num7;
	}

	/// <summary>
	/// Builds a per-symbol bit-cost table (units = bits*32)
	/// from a symbol histogram, then applies the entropy-flatten clamp.
	/// </summary>
	/// <param name="histo">Symbol counts, length <see cref="F:LibProsperoPkg.PFS.Compression.Oodle.KrakenOptimalCost.Alphabet" /> (256).</param>
	/// <param name="cost">Destination cost table, length <see cref="F:LibProsperoPkg.PFS.Compression.Oodle.KrakenOptimalCost.Alphabet" /> (256); overwritten.</param>
	/// <param name="bias">Per-table additive bias (from <c>passinfo_to_codecost</c>).</param>
	/// <param name="threshold">Entropy-flatten threshold (expected in [7*32, 32*8] = [224, 256]).</param>
	internal static void HistoToCodeCost(ReadOnlySpan<int> histo, Span<int> cost, int bias, int threshold)
	{
		if (histo.Length < 256 || cost.Length < 256)
		{
			throw new ArgumentException("histo/cost must have length >= 256.");
		}
		int num = 0;
		for (int i = 0; i < 256; i++)
		{
			num += histo[i];
		}
		int num2 = num * 4 + 256;
		int num3 = Log2Fix((uint)num2);
		long num4 = 0L;
		for (int j = 0; j < 256; j++)
		{
			int num5 = histo[j] * 4 + 1;
			int num6 = (Log2Fix((uint)num5) - num3) * 32 >> 13;
			num4 += (long)num6 * (long)num5;
			cost[j] = num6 + bias;
		}
		if ((long)threshold * (long)num2 < num4)
		{
			int num7 = bias + 256;
			for (int k = 0; k < 256; k++)
			{
				cost[k] = num7;
			}
		}
	}

	/// <summary>
	/// Builds the five cost tables from a pass
	/// histogram block via <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.KrakenOptimalCost.HistoToCodeCost(System.ReadOnlySpan{System.Int32},System.Span{System.Int32},System.Int32,System.Int32)" /> with the fixed per-table biases (threshold 0xff).
	/// The <paramref name="passinfo" /> block is int-indexed:
	/// <c>[0..0xFF]</c> lit-raw histo, <c>[0x100..0x1FF]</c> lit-sub histo, <c>[0x200..0x2FF]</c> packet,
	/// <c>[0x300..0x3FF]</c> length, <c>[0x400]</c> offset_alt_modulo, <c>[0x401..0x500]</c> offset-bucket,
	/// <c>[0x501..0x600]</c> offset-alt. <paramref name="litMode" /> is the carried codecosts[0] flag.
	/// </summary>
	internal static CodeCosts BuildFromPassinfo(ReadOnlySpan<int> passinfo, int litMode)
	{
		if (passinfo.Length < 1537)
		{
			throw new ArgumentException("passinfo must have length >= 0x601.");
		}
		CodeCosts codeCosts = new CodeCosts
		{
			LitMode = litMode,
			LitSubMask = ((litMode != 1) ? 255 : 0),
			OffsetAltModulo = passinfo[1024]
		};
		HistoToCodeCost(passinfo.Slice(1025, 256), codeCosts.OffsetBucket, 36, 255);
		if (passinfo[1024] > 1)
		{
			HistoToCodeCost(passinfo.Slice(1281, 256), codeCosts.OffsetAlt, 0, 255);
		}
		HistoToCodeCost(passinfo.Slice(512, 256), codeCosts.Packet, 18, 255);
		HistoToCodeCost(passinfo.Slice(768, 256), codeCosts.Length, 12, 255);
		HistoToCodeCost((litMode == 1) ? passinfo.Slice(0, 256) : passinfo.Slice(256, 256), codeCosts.Lit, 0, 255);
		return codeCosts;
	}

	/// <summary>
	/// Diagnostic: reconstruct a <see cref="T:LibProsperoPkg.PFS.Compression.Oodle.KrakenOptimalCost.CodeCosts" /> from a raw 0x1808-byte codecost blob. Reads
	/// the int32-LE serialized fields directly so the DP can be driven with exact reference codecosts, bypassing
	/// <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.KrakenOptimalCost.BuildFromPassinfo(System.ReadOnlySpan{System.Int32},System.Int32)" />. Layout: +0x04 lit-mask, +0x08 Lit[256], +0x408 Packet[256],
	/// +0x808 offset-alt-modulo, +0x80c OffsetBucket[256], +0xc0c OffsetAlt[256], +0x100c Length[256].
	/// </summary>
	internal static CodeCosts FromRawBlob(ReadOnlySpan<byte> blob)
	{
		if (blob.Length < 6152)
		{
			throw new ArgumentException("codecost blob must be >= 0x1808 bytes.");
		}
		int num = BinaryPrimitives.ReadInt32LittleEndian(blob.Slice(4, 4));
		CodeCosts codeCosts = new CodeCosts
		{
			LitMode = ((num == 0) ? 1 : 0),
			LitSubMask = ((num != 0) ? 255 : 0),
			OffsetAltModulo = BinaryPrimitives.ReadInt32LittleEndian(blob.Slice(2056, 4))
		};
		for (int i = 0; i < 256; i++)
		{
			codeCosts.Lit[i] = BinaryPrimitives.ReadInt32LittleEndian(blob.Slice(8 + i * 4, 4));
			codeCosts.Packet[i] = BinaryPrimitives.ReadInt32LittleEndian(blob.Slice(1032 + i * 4, 4));
			codeCosts.OffsetBucket[i] = BinaryPrimitives.ReadInt32LittleEndian(blob.Slice(2060 + i * 4, 4));
			codeCosts.OffsetAlt[i] = BinaryPrimitives.ReadInt32LittleEndian(blob.Slice(3084 + i * 4, 4));
			codeCosts.Length[i] = BinaryPrimitives.ReadInt32LittleEndian(blob.Slice(4108 + i * 4, 4));
		}
		return codeCosts;
	}

	/// <summary>
	/// Cost of a <paramref name="lrl" />-byte literal run that
	/// starts at <paramref name="start" /> in <paramref name="buf" />, against the most-recent offset
	/// <paramref name="lo" /> (&gt;= 8). Each byte costs <c>Lit[(buf[p] - (buf[p-lo] &amp; mask)) &amp; 0xff]</c>.
	/// </summary>
	internal static int CostLiterals(ReadOnlySpan<byte> buf, int start, int lrl, int lo, CodeCosts cc)
	{
		if (lrl == 0)
		{
			return 0;
		}
		int litSubMask = cc.LitSubMask;
		int num = 0;
		for (int i = 0; i < lrl; i++)
		{
			int num2 = start + i;
			int num3 = ((litSubMask != 0) ? (buf[num2 - lo] & litSubMask) : 0);
			int num4 = (buf[num2] - num3) & 0xFF;
			num += cc.Lit[num4];
		}
		return num;
	}

	/// <summary>
	/// Cost of a single literal at <paramref name="pos" />
	/// against the most-recent offset <paramref name="lo" /> (&gt;= 8). In raw mode (<c>LitSubMask == 0</c>)
	/// this is simply <c>Lit[buf[pos]]</c>; in sub mode it is the delta against <c>buf[pos-lo]</c>.
	/// </summary>
	internal static int CostAddLiteral(ReadOnlySpan<byte> buf, int pos, int lo, CodeCosts cc)
	{
		int num = ((cc.LitSubMask != 0) ? (buf[pos - lo] & cc.LitSubMask) : 0);
		int num2 = (buf[pos] - num) & 0xFF;
		return cc.Lit[num2];
	}

	/// <summary>
	/// Packed-offset bucket byte for <paramref name="dist" /> (&gt;= 8), identical to
	/// <c>KrakenBitWriter.WriteDistance</c> for the
	/// standard window (bucket &lt; 0xF0, dist &lt;= ~256 KiB). Outputs the extra-bit count in
	/// <paramref name="extraBits" />.
	/// </summary>
	internal static int OffsetBucketByte(int dist, out int extraBits)
	{
		uint num = (uint)(dist + 248);
		int num2 = 31 - BitOperations.LeadingZeroCount(num) - 8;
		uint num3 = (uint)(((int)num - (1 << num2 + 8)) & 0xF);
		extraBits = num2 + 4;
		return (num2 << 4) | (int)num3;
	}

	/// <summary>
	/// Standard mode-0 offset path: <c>OffsetBucket[bucket] +
	/// extraBits*32</c> (+0xc for the &gt; 0x7FFF07 huge-offset bucket). Valid for the newLZ window the
	/// nwonly encoder emits (offset coding mode 0).
	/// </summary>
	internal static int CostOffset(int dist, CodeCosts cc)
	{
		int num = OffsetBucketByte(dist, out var extraBits);
		int num2 = cc.OffsetBucket[num] + extraBits * 32;
		if (dist > 8388359)
		{
			num2 += 12;
		}
		return num2;
	}

	/// <summary>Elias-gamma bit count: <c>2*floor(log2(v+1)) + 1</c>.</summary>
	internal static int VarBitsEliasGamma(int val)
	{
		return (BitLen((uint)(val + 1)) - 1) * 2 + 1;
	}

	/// <summary>Length-escape bit count: <c>VarBitsEliasGamma(val &gt;&gt; nbits) + nbits</c>.</summary>
	internal static int CountBits(uint val, int nbits)
	{
		return VarBitsEliasGamma((int)(val >> nbits)) + nbits;
	}

	/// <summary>
	/// Length cost: <c>Length[v]</c> for <c>v &lt; 0xFF</c>; otherwise the escape base
	/// <c>Length[0xFF] + CountBits(v - 0xFF, 6)*32</c>.
	/// </summary>
	internal static int CostLen(CodeCosts cc, int v)
	{
		if (v < 255)
		{
			return cc.Length[v];
		}
		return cc.Length[255] + CountBits((uint)(v - 255), 6) * 32;
	}

	/// <summary>
	/// Cost of a repeat-offset match of length
	/// <paramref name="ml" /> with literal field <paramref name="litField" /> (0..3) and recent-offset
	/// index <paramref name="loi" /> (0..2). Short match (<c>ml &lt; 17</c>) → one packet-table lookup;
	/// long match → <c>CostLen(ml-17)</c> plus the escape packet entry.
	/// </summary>
	internal static int CostLoMatch(CodeCosts cc, int litField, int ml, int loi)
	{
		int num = ml - 17;
		if (num < 0)
		{
			return cc.Packet[litField + (ml - 2) * 4 + loi * 64];
		}
		return CostLen(cc, num) + cc.Packet[litField + 60 + loi * 64];
	}

	/// <summary>
	/// Packet/length cost of a new-offset match
	/// (offset index 3) of length <paramref name="ml" /> with literal field <paramref name="litField" />.
	/// The caller adds <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.KrakenOptimalCost.CostOffset(System.Int32,LibProsperoPkg.PFS.Compression.Oodle.KrakenOptimalCost.CodeCosts)" /> for the new distance separately.
	/// </summary>
	internal static int CostNormalMatch(CodeCosts cc, int litField, int ml)
	{
		int num = ml - 17;
		if (num < 0)
		{
			return cc.Packet[litField + (ml - 2) * 4 + 192];
		}
		return CostLen(cc, num) + cc.Packet[litField + 252];
	}
}
