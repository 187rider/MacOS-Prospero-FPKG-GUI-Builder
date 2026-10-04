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
			Span<ulong> c = stackalloc ulong[5];
			Span<ulong> d = stackalloc ulong[5];
			Span<ulong> b = stackalloc ulong[25];

			ulong[] roundConstants = RoundConstants;
			for (int r = 0; r < 24; r++)
			{
				// Theta step
				c[0] = a[0] ^ a[5] ^ a[10] ^ a[15] ^ a[20];
				c[1] = a[1] ^ a[6] ^ a[11] ^ a[16] ^ a[21];
				c[2] = a[2] ^ a[7] ^ a[12] ^ a[17] ^ a[22];
				c[3] = a[3] ^ a[8] ^ a[13] ^ a[18] ^ a[23];
				c[4] = a[4] ^ a[9] ^ a[14] ^ a[19] ^ a[24];

				d[0] = c[4] ^ BitOperations.RotateLeft(c[1], 1);
				d[1] = c[0] ^ BitOperations.RotateLeft(c[2], 1);
				d[2] = c[1] ^ BitOperations.RotateLeft(c[3], 1);
				d[3] = c[2] ^ BitOperations.RotateLeft(c[4], 1);
				d[4] = c[3] ^ BitOperations.RotateLeft(c[0], 1);

				ulong a00 = a[0] ^ d[0];
				ulong a01 = a[1] ^ d[1];
				ulong a02 = a[2] ^ d[2];
				ulong a03 = a[3] ^ d[3];
				ulong a04 = a[4] ^ d[4];

				ulong a05 = a[5] ^ d[0];
				ulong a06 = a[6] ^ d[1];
				ulong a07 = a[7] ^ d[2];
				ulong a08 = a[8] ^ d[3];
				ulong a09 = a[9] ^ d[4];

				ulong a10 = a[10] ^ d[0];
				ulong a11 = a[11] ^ d[1];
				ulong a12 = a[12] ^ d[2];
				ulong a13 = a[13] ^ d[3];
				ulong a14 = a[14] ^ d[4];

				ulong a15 = a[15] ^ d[0];
				ulong a16 = a[16] ^ d[1];
				ulong a17 = a[17] ^ d[2];
				ulong a18 = a[18] ^ d[3];
				ulong a19 = a[19] ^ d[4];

				ulong a20 = a[20] ^ d[0];
				ulong a21 = a[21] ^ d[1];
				ulong a22 = a[22] ^ d[2];
				ulong a23 = a[23] ^ d[3];
				ulong a24 = a[24] ^ d[4];

				// Rho and Pi steps
				b[0]  = a00;
				b[10] = BitOperations.RotateLeft(a01, 1);
				b[20] = BitOperations.RotateLeft(a02, 62);
				b[5]  = BitOperations.RotateLeft(a03, 28);
				b[15] = BitOperations.RotateLeft(a04, 27);

				b[16] = BitOperations.RotateLeft(a05, 36);
				b[1]  = BitOperations.RotateLeft(a06, 44);
				b[11] = BitOperations.RotateLeft(a07, 6);
				b[21] = BitOperations.RotateLeft(a08, 55);
				b[6]  = BitOperations.RotateLeft(a09, 20);

				b[7]  = BitOperations.RotateLeft(a10, 3);
				b[17] = BitOperations.RotateLeft(a11, 10);
				b[2]  = BitOperations.RotateLeft(a12, 43);
				b[12] = BitOperations.RotateLeft(a13, 25);
				b[22] = BitOperations.RotateLeft(a14, 39);

				b[23] = BitOperations.RotateLeft(a15, 41);
				b[8]  = BitOperations.RotateLeft(a16, 45);
				b[18] = BitOperations.RotateLeft(a17, 15);
				b[3]  = BitOperations.RotateLeft(a18, 21);
				b[13] = BitOperations.RotateLeft(a19, 8);

				b[14] = BitOperations.RotateLeft(a20, 18);
				b[24] = BitOperations.RotateLeft(a21, 2);
				b[9]  = BitOperations.RotateLeft(a22, 61);
				b[19] = BitOperations.RotateLeft(a23, 56);
				b[4]  = BitOperations.RotateLeft(a24, 14);

				// Chi & Iota steps
				a[0]  = (b[0]  ^ (~b[1]  & b[2])) ^ roundConstants[r];
				a[1]  = b[1]  ^ (~b[2]  & b[3]);
				a[2]  = b[2]  ^ (~b[3]  & b[4]);
				a[3]  = b[3]  ^ (~b[4]  & b[0]);
				a[4]  = b[4]  ^ (~b[0]  & b[1]);

				a[5]  = b[5]  ^ (~b[6]  & b[7]);
				a[6]  = b[6]  ^ (~b[7]  & b[8]);
				a[7]  = b[7]  ^ (~b[8]  & b[9]);
				a[8]  = b[8]  ^ (~b[9]  & b[5]);
				a[9]  = b[9]  ^ (~b[5]  & b[6]);

				a[10] = b[10] ^ (~b[11] & b[12]);
				a[11] = b[11] ^ (~b[12] & b[13]);
				a[12] = b[12] ^ (~b[13] & b[14]);
				a[13] = b[13] ^ (~b[14] & b[10]);
				a[14] = b[14] ^ (~b[10] & b[11]);

				a[15] = b[15] ^ (~b[16] & b[17]);
				a[16] = b[16] ^ (~b[17] & b[18]);
				a[17] = b[17] ^ (~b[18] & b[19]);
				a[18] = b[18] ^ (~b[19] & b[15]);
				a[19] = b[19] ^ (~b[15] & b[16]);

				a[20] = b[20] ^ (~b[21] & b[22]);
				a[21] = b[21] ^ (~b[22] & b[23]);
				a[22] = b[22] ^ (~b[23] & b[24]);
				a[23] = b[23] ^ (~b[24] & b[20]);
				a[24] = b[24] ^ (~b[20] & b[21]);
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
