using System;

namespace LibProsperoPkg.Util;

/// <summary>
/// Mersenne Twister PRNG
/// </summary>
public class MersenneTwister
{
	public const int N = 624;

	private const uint M = 397u;

	private const uint DefaultSeed = 19650218u;

	private const uint MatrixA = 2567483615u;

	private const uint UpperMask = 2147483648u;

	private const uint LowerMask = 2147483647u;

	private const uint Constant1 = 1812433253u;

	private const uint Constant2 = 1664525u;

	private const uint Constant3 = 1566083941u;

	private const uint Constant4 = 2636928640u;

	private const uint Constant5 = 4022730752u;

	public uint[] mt = new uint[624];

	private uint mti;

	private uint Mask(int val)
	{
		return (uint)(~(-1 << val));
	}

	private uint TwoToThe(int val)
	{
		return (uint)(1 << val);
	}

	public MersenneTwister(uint seed = 19650218u)
	{
		mt[0] = seed;
		for (mti = 1u; mti < 624; mti++)
		{
			mt[mti] = mti + 1812433253 * (mt[mti - 1] ^ (mt[mti - 1] >> 30));
		}
	}

	public MersenneTwister(uint[] seed)
		: this()
	{
		uint num = 1u;
		uint num2 = 0u;
		for (int num3 = Math.Max(624, seed.Length); num3 > 0; num3--)
		{
			mt[num] = (mt[num] ^ ((mt[num - 1] ^ (mt[num - 1] >> 30)) * 1664525)) + seed[num2] + num2;
			num++;
			num2++;
			if (num >= 624)
			{
				mt[0] = mt[623];
				num = 1u;
			}
			if (num2 >= seed.Length)
			{
				num2 = 0u;
			}
		}
		for (int i = 0; i < 623; i++)
		{
			mt[num] = (mt[num] ^ ((mt[num - 1] ^ (mt[num - 1] >> 30)) * 1566083941)) - num;
			num++;
			if (num >= 624)
			{
				mt[0] = mt[623];
				num = 1u;
			}
		}
		mt[0] = 2147483648u;
	}

	public uint Int32()
	{
		uint[] array = new uint[2] { 0u, 2567483615u };
		uint num2;
		if (mti >= 624)
		{
			uint num;
			for (num = 0u; num < 227; num++)
			{
				num2 = (mt[num] & 0x80000000u) | (mt[num + 1] & 0x7FFFFFFF);
				mt[num] = mt[num + 397] ^ ((num2 >> 1) & Mask(31)) ^ array[num2 & 1];
			}
			for (; num < 623; num++)
			{
				num2 = (mt[num] & 0x80000000u) | (mt[num + 1] & 0x7FFFFFFF);
				mt[num] = mt[num + 397 - 624] ^ ((num2 >> 1) & Mask(31)) ^ array[num2 & 1];
			}
			num2 = (mt[623] & 0x80000000u) | (mt[0] & 0x7FFFFFFF);
			mt[623] = mt[396] ^ ((num2 >> 1) & Mask(31)) ^ array[num2 & 1];
			mti = 0u;
		}
		num2 = mt[mti++];
		num2 ^= (num2 >> 11) & Mask(21);
		num2 ^= (num2 << 7) & 0x9D2C5680u;
		num2 ^= (num2 << 15) & 0xEFC60000u;
		return num2 ^ ((num2 >> 18) & Mask(14));
	}

	public uint Int31()
	{
		return Int32() & Mask(31);
	}
}
