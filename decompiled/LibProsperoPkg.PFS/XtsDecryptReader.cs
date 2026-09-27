using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PFS;

/// <summary>
/// Provides XTS decryption on an IMemoryReader
/// </summary>
public class XtsDecryptReader : IMemoryReader, IDisposable
{
	public sealed class Ctx : IDisposable
	{
		public SymmetricAlgorithm cipher;

		public SymmetricAlgorithm tweakCipher;

		public ICryptoTransform decryptor;

		public ICryptoTransform tweakEncryptor;

		public byte[] tweak;

		public byte[] xor;

		public byte[] encryptedTweak;

		public void Dispose()
		{
			decryptor?.Dispose();
			tweakEncryptor?.Dispose();
			cipher?.Dispose();
			tweakCipher?.Dispose();
		}
	}

	private byte[] dataKey;

	private byte[] tweakKey;

	/// <summary>
	/// Size of each XEX sector
	/// </summary>
	private uint sectorSize;

	/// <summary>
	/// Sector at and after which the encryption is active
	/// </summary>
	private uint cryptStartSector;

	private IMemoryReader reader;

	private static byte[] zeroes = new byte[16];

	/// <summary>
	/// Creates an AES-XTS-128 stream.
	/// Reads will decrypt data.
	/// </summary>
	public XtsDecryptReader(IMemoryReader r, byte[] dataKey, byte[] tweakKey, uint startSector = 16u, uint sectorSize = 4096u)
	{
		cryptStartSector = startSector;
		this.sectorSize = sectorSize;
		this.dataKey = dataKey;
		this.tweakKey = tweakKey;
		reader = r;
	}

	public unsafe static void DecryptSector(Ctx context, byte[] sector, ulong sectorNum)
	{
		byte[] tweak = context.tweak;
		byte[] encryptedTweak = context.encryptedTweak;
		byte[] xor = context.xor;
		BinaryPrimitives.WriteUInt64LittleEndian(tweak, sectorNum);
		Buffer.BlockCopy(zeroes, 0, tweak, 8, 8);
		ICryptoTransform tweakEncryptor = context.tweakEncryptor;
		ICryptoTransform decryptor = context.decryptor;
		tweakEncryptor.TransformBlock(tweak, 0, 16, encryptedTweak, 0);
		for (int i = 0; i < sector.Length; i += 16)
		{
			fixed (byte* ptr = xor)
			{
				fixed (byte* ptr2 = encryptedTweak)
				{
					fixed (byte* ptr3 = &sector[i])
					{
						*(long*)ptr = *(long*)ptr3 ^ *(long*)ptr2;
						((long*)ptr)[1] = ((long*)ptr3)[1] ^ ((long*)ptr2)[1];
					}
				}
			}
			decryptor.TransformBlock(xor, 0, 16, xor, 0);
			fixed (byte* ptr4 = xor)
			{
				fixed (byte* ptr5 = encryptedTweak)
				{
					fixed (byte* ptr6 = &sector[i])
					{
						*(long*)ptr6 = *(long*)ptr4 ^ *(long*)ptr5;
						((long*)ptr6)[1] = ((long*)ptr4)[1] ^ ((long*)ptr5)[1];
					}
				}
			}
			int num = 0;
			for (int j = 0; j < 16; j++)
			{
				byte b = encryptedTweak[j];
				encryptedTweak[j] = (byte)((2 * encryptedTweak[j]) | num);
				num = (b & 0x80) >> 7;
			}
			if (num != 0)
			{
				encryptedTweak[0] ^= 135;
			}
		}
	}

	/// <summary>
	/// Precondition: activeSector is set
	/// Postconditions:
	/// - sectorOffset is reset to 0
	/// - sectorBuf[] is filled with decrypted sector
	/// - position is updated
	/// </summary>
	private void ReadSectorBuffer(Ctx ctx, int currentSector, byte[] sectorBuf)
	{
		reader.Read(currentSector * sectorSize, sectorBuf, 0, (int)sectorSize);
		if (currentSector >= cryptStartSector)
		{
			DecryptSector(ctx, sectorBuf, (ulong)currentSector);
		}
	}

	private Ctx MakeCtx()
	{
		Aes aes = CreateEcbAes(dataKey);
		Aes aes2 = CreateEcbAes(tweakKey);
		return new Ctx
		{
			cipher = aes,
			tweakCipher = aes2,
			decryptor = aes.CreateDecryptor(),
			tweakEncryptor = aes2.CreateEncryptor(),
			xor = new byte[16],
			encryptedTweak = new byte[16],
			tweak = new byte[16]
		};
	}

	/// <summary>
	/// Creates a single-block AES-128-ECB engine (no padding) used as the primitive for
	/// the manual XTS transform. Uses the modern <see cref="M:System.Security.Cryptography.Aes.Create" /> factory.
	/// </summary>
	private static Aes CreateEcbAes(byte[] key)
	{
		Aes aes = Aes.Create();
		aes.Mode = CipherMode.ECB;
		aes.KeySize = 128;
		aes.Key = key;
		aes.Padding = PaddingMode.None;
		aes.BlockSize = 128;
		return aes;
	}

	public void Read(long position, byte[] buffer, int offset, int count)
	{
		if (count <= 0)
		{
			return;
		}
		using Ctx ctx = MakeCtx();
		byte[] array = new byte[sectorSize];
		int num = (int)(position / sectorSize);
		int num2 = (int)(position - sectorSize * num);
		ReadSectorBuffer(ctx, num, array);
		int num3 = 0;
		while (count > 0)
		{
			if (num2 >= sectorSize)
			{
				num++;
				ReadSectorBuffer(ctx, num, array);
				num2 = 0;
			}
			int num4 = Math.Min((int)sectorSize - num2, count);
			Buffer.BlockCopy(array, num2, buffer, offset, num4);
			count -= num4;
			offset += num4;
			num3 += num4;
			num2 += num4;
			position += num4;
		}
	}

	public void Dispose()
	{
	}
}
