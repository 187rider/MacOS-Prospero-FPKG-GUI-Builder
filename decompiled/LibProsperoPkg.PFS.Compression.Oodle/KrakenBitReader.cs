using System;
using System.Numerics;

namespace LibProsperoPkg.PFS.Compression.Oodle;

/// <summary>
/// A single Kraken bit reader over a byte region. Operates either forward (from the region
/// start) or backward (from the region end), mirroring the dual readers Kraken uses to code offsets
/// and lengths. MSB-first with a 24-bit refill window.
/// </summary>
internal ref struct KrakenBitReader
{
	private readonly ReadOnlySpan<byte> _region;

	private readonly bool _backward;

	/// <summary>The bit-position accounting value (number of bits owed; may go slightly negative).</summary>
	public int BitPos;

	/// <summary>The 32-bit accumulator; valid bits live at the top.</summary>
	public uint Bits;

	/// <summary>The current byte index into the region.</summary>
	public int P;

	/// <summary>The byte index where reading truly stopped (correcting for the refill look-ahead).</summary>
	public int SeamIndex
	{
		get
		{
			if (!_backward)
			{
				return P - (24 - BitPos >> 3);
			}
			return P + (24 - BitPos >> 3);
		}
	}

	private KrakenBitReader(ReadOnlySpan<byte> region, bool backward, int start)
	{
		_region = region;
		_backward = backward;
		BitPos = 24;
		Bits = 0u;
		P = start;
	}

	/// <summary>Creates a forward reader starting at the beginning of <paramref name="region" />.</summary>
	public static KrakenBitReader CreateForward(ReadOnlySpan<byte> region)
	{
		KrakenBitReader result = new KrakenBitReader(region, backward: false, 0);
		result.Refill();
		return result;
	}

	/// <summary>Creates a backward reader starting at the end of <paramref name="region" />.</summary>
	public static KrakenBitReader CreateBackward(ReadOnlySpan<byte> region)
	{
		KrakenBitReader result = new KrakenBitReader(region, backward: true, region.Length);
		result.Refill();
		return result;
	}

	private byte At(int i)
	{
		if ((uint)i >= (uint)_region.Length)
		{
			return 0;
		}
		return _region[i];
	}

	/// <summary>Refills the accumulator to keep at least 24 valid bits, in the reader's direction.</summary>
	public void Refill()
	{
		if (_backward)
		{
			while (BitPos > 0)
			{
				P--;
				Bits |= (uint)(At(P) << BitPos);
				BitPos -= 8;
			}
		}
		else
		{
			while (BitPos > 0)
			{
				Bits |= (uint)(At(P) << BitPos);
				BitPos -= 8;
				P++;
			}
		}
	}

	private uint ReadBitsNoRefill(int n)
	{
		uint result = Bits >> 32 - n;
		Bits <<= n;
		BitPos += n;
		return result;
	}

	private uint ReadBitsNoRefillZero(int n)
	{
		uint result = Bits >> 1 >> 31 - n;
		Bits <<= n;
		BitPos += n;
		return result;
	}

	/// <summary>Reads <paramref name="n" /> bits (n may exceed 24), refilling as needed.</summary>
	public uint ReadMoreThan24Bits(int n)
	{
		uint result;
		if (n <= 24)
		{
			result = ReadBitsNoRefillZero(n);
		}
		else
		{
			result = ReadBitsNoRefill(24) << n - 24;
			Refill();
			result += ReadBitsNoRefill(n - 24);
		}
		Refill();
		return result;
	}

	/// <summary>Reads a Kraken distance code parameterized by <paramref name="v" /> (a packed-offset byte).</summary>
	public uint ReadDistance(uint v)
	{
		uint result;
		if (v < 240)
		{
			int num = (int)((v >> 4) + 4);
			uint num2 = BitOperations.RotateLeft(Bits | 1, num);
			BitPos += num;
			uint num3 = (uint)((2 << num) - 1);
			Bits = num2 & ~num3;
			result = ((num2 & num3) << 4) + (v & 0xF) - 248;
		}
		else
		{
			int num = (int)(v - 240 + 4);
			uint num2 = BitOperations.RotateLeft(Bits | 1, num);
			BitPos += num;
			uint num3 = (uint)((2 << num) - 1);
			Bits = num2 & ~num3;
			result = 8322816 + ((num2 & num3) << 12);
			Refill();
			result += Bits >> 20;
			BitPos += 12;
			Bits <<= 12;
		}
		Refill();
		return result;
	}

	/// <summary>Reads a Kraken length code into <paramref name="value" />; returns false on malformed input.</summary>
	public bool ReadLength(out uint value)
	{
		value = 0u;
		int num = BitOperations.LeadingZeroCount(Bits);
		if (num > 12)
		{
			return false;
		}
		BitPos += num;
		Bits <<= num;
		Refill();
		num += 7;
		BitPos += num;
		value = (Bits >> 32 - num) - 64;
		Bits <<= num;
		Refill();
		return true;
	}

	/// <summary>
	/// Reads the Elias-gamma length-stream-size prefix used when the excess flag is clear. Returns
	/// false when the accumulator lacks the sanity minimum the format guarantees.
	/// </summary>
	public bool TryReadLenStreamSizePrefix(out int size)
	{
		size = 0;
		if (Bits < 8192)
		{
			return false;
		}
		int num = BitOperations.LeadingZeroCount(Bits);
		BitPos += num;
		Bits <<= num;
		Refill();
		num++;
		size = (int)((Bits >> 32 - num) - 1);
		BitPos += num;
		Bits <<= num;
		Refill();
		return true;
	}
}
