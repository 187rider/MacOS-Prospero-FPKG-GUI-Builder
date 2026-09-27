using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;

namespace LibProsperoPkg.Util;

/// <summary>Portable SHA3-256 with platform acceleration when available.</summary>
public static class ProsperoSha3
{
	public sealed class Incremental
	{
		private ManagedSha3 state = new ManagedSha3();

		public void AppendData(ReadOnlySpan<byte> data)
		{
			state.AppendData(data);
		}

		public byte[] GetHashAndReset()
		{
			byte[] hashAndReset = state.GetHashAndReset();
			state = new ManagedSha3();
			return hashAndReset;
		}
	}

	private sealed class ManagedSha3
	{
		private const int Rate = 136;

		private static readonly ulong[] RoundConstants = new ulong[24]
		{
			1uL, 32898uL, 9223372036854808714uL, 9223372039002292224uL, 32907uL, 2147483649uL, 9223372039002292353uL, 9223372036854808585uL, 138uL, 136uL,
			2147516425uL, 2147483658uL, 2147516555uL, 9223372036854775947uL, 9223372036854808713uL, 9223372036854808579uL, 9223372036854808578uL, 9223372036854775936uL, 32778uL, 9223372039002259466uL,
			9223372039002292353uL, 9223372036854808704uL, 2147483649uL, 9223372039002292232uL
		};

		private static readonly int[] Rotation = new int[25]
		{
			0, 1, 62, 28, 27, 36, 44, 6, 55, 20,
			3, 10, 43, 25, 39, 41, 45, 15, 21, 8,
			18, 2, 61, 56, 14
		};

		private readonly ulong[] lanes = new ulong[25];

		private readonly byte[] pending = new byte[136];

		private int pendingLength;

		private bool finalized;

		public void AppendData(ReadOnlySpan<byte> data)
		{
			if (finalized)
			{
				throw new InvalidOperationException("SHA3 state is finalized.");
			}
			if (pendingLength != 0)
			{
				int num = Math.Min(136 - pendingLength, data.Length);
				data.Slice(0, num).CopyTo(pending.AsSpan(pendingLength));
				pendingLength += num;
				data = data.Slice(num);
				if (pendingLength == 136)
				{
					Absorb(pending);
					pendingLength = 0;
				}
			}
			while (data.Length >= 136)
			{
				Absorb(data.Slice(0, 136));
				data = data.Slice(136);
			}
			data.CopyTo(pending);
			pendingLength = data.Length;
		}

		public byte[] GetHashAndReset()
		{
			if (finalized)
			{
				throw new InvalidOperationException("SHA3 state is finalized.");
			}
			finalized = true;
			pending.AsSpan(pendingLength).Clear();
			pending[pendingLength] ^= 6;
			pending[135] ^= 128;
			Absorb(pending);
			byte[] array = new byte[32];
			for (int i = 0; i < array.Length / 8; i++)
			{
				BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(i * 8), lanes[i]);
			}
			return array;
		}

		private void Absorb(ReadOnlySpan<byte> block)
		{
			for (int i = 0; i < 17; i++)
			{
				lanes[i] ^= BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(i * 8));
			}
			Permute(lanes);
		}

		public static void Permute(ulong[] a)
		{
			Permute(a.AsSpan());
		}

		public static void Permute(Span<ulong> a)
		{
			Span<ulong> span = stackalloc ulong[5];
			Span<ulong> span2 = stackalloc ulong[5];
			Span<ulong> span3 = stackalloc ulong[25];
			ulong[] roundConstants = RoundConstants;
			foreach (ulong num in roundConstants)
			{
				for (int j = 0; j < 5; j++)
				{
					span[j] = a[j] ^ a[j + 5] ^ a[j + 10] ^ a[j + 15] ^ a[j + 20];
				}
				for (int k = 0; k < 5; k++)
				{
					span2[k] = span[(k + 4) % 5] ^ BitOperations.RotateLeft(span[(k + 1) % 5], 1);
				}
				for (int l = 0; l < 5; l++)
				{
					for (int m = 0; m < 5; m++)
					{
						a[m + 5 * l] ^= span2[m];
					}
				}
				for (int n = 0; n < 5; n++)
				{
					for (int num2 = 0; num2 < 5; num2++)
					{
						span3[n + 5 * ((2 * num2 + 3 * n) % 5)] = BitOperations.RotateLeft(a[num2 + 5 * n], Rotation[num2 + 5 * n]);
					}
				}
				for (int num3 = 0; num3 < 5; num3++)
				{
					for (int num4 = 0; num4 < 5; num4++)
					{
						a[num4 + 5 * num3] = span3[num4 + 5 * num3] ^ (~span3[(num4 + 1) % 5 + 5 * num3] & span3[(num4 + 2) % 5 + 5 * num3]);
					}
				}
				a[0] ^= num;
			}
		}
	}

	public const int DigestSize = 32;

	public static bool IsSupported => true;

	public static byte[] HashData(ReadOnlySpan<byte> data)
	{
		byte[] result = new byte[32];
		HashData(data, result);
		return result;
	}

	public static int HashData(ReadOnlySpan<byte> data, Span<byte> destination)
	{
		if (destination.Length < 32)
		{
			throw new ArgumentException("Destination needs 32 bytes.", "destination");
		}
		if (SHA3_256.IsSupported)
		{
			return SHA3_256.HashData(data, destination);
		}

		Span<ulong> lanes = stackalloc ulong[25];
		lanes.Clear();

		ReadOnlySpan<byte> rem = data;
		while (rem.Length >= 136)
		{
			for (int i = 0; i < 17; i++)
			{
				lanes[i] ^= BinaryPrimitives.ReadUInt64LittleEndian(rem.Slice(i * 8));
			}
			ManagedSha3.Permute(lanes);
			rem = rem.Slice(136);
		}

		Span<byte> pending = stackalloc byte[136];
		pending.Clear();
		rem.CopyTo(pending);
		pending[rem.Length] ^= 6;
		pending[135] ^= 128;
		for (int j = 0; j < 17; j++)
		{
			lanes[j] ^= BinaryPrimitives.ReadUInt64LittleEndian(pending.Slice(j * 8));
		}
		ManagedSha3.Permute(lanes);

		for (int k = 0; k < 4; k++)
		{
			BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(k * 8, 8), lanes[k]);
		}
		return 32;
	}

	/// <summary>
	/// Portable SHAKE128 extendable-output function. This is kept managed because the platform
	/// <see cref="T:System.Security.Cryptography.Shake128" /> API may be present at compile time while remaining unsupported by
	/// the active Windows cryptographic provider.
	/// </summary>
	public static void Shake128Data(ReadOnlySpan<byte> data, Span<byte> destination)
	{
		ulong[] array = new ulong[25];
		while (data.Length >= 168)
		{
			for (int i = 0; i < 21; i++)
			{
				array[i] ^= BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(i * 8));
			}
			ManagedSha3.Permute(array);
			data = data.Slice(168);
		}
		Span<byte> destination2 = stackalloc byte[168];
		destination2.Clear();
		data.CopyTo(destination2);
		destination2[data.Length] ^= 31;
		destination2[167] ^= 128;
		for (int j = 0; j < 21; j++)
		{
			array[j] ^= BinaryPrimitives.ReadUInt64LittleEndian(destination2.Slice(j * 8));
		}
		ManagedSha3.Permute(array);
		Span<byte> destination3 = stackalloc byte[8];
		while (!destination.IsEmpty)
		{
			int num = Math.Min(168, destination.Length);
			int num2 = num / 8;
			for (int k = 0; k < num2; k++)
			{
				BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(k * 8), array[k]);
			}
			int num3 = num - num2 * 8;
			if (num3 != 0)
			{
				BinaryPrimitives.WriteUInt64LittleEndian(destination3, array[num2]);
				destination3.Slice(0, num3).CopyTo(destination.Slice(num2 * 8));
			}
			destination = destination.Slice(num);
			if (!destination.IsEmpty)
			{
				ManagedSha3.Permute(array);
			}
		}
	}

	public static byte[] HashData(Stream stream)
	{
		ArgumentNullException.ThrowIfNull(stream, "stream");
		ManagedSha3 managedSha = new ManagedSha3();
		byte[] array = new byte[1048576];
		int length;
		while ((length = stream.Read(array, 0, array.Length)) != 0)
		{
			managedSha.AppendData(array.AsSpan(0, length));
		}
		return managedSha.GetHashAndReset();
	}
}
