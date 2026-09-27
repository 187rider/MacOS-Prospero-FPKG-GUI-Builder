using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace LibProsperoPkg.Util;

/// <summary>
/// AES-XTS-128 sector transform. The AES engines and their block transforms are created once and
/// reused across sectors. A single instance is not thread-safe; use one instance per worker.
/// </summary>
public sealed class XtsBlockTransform : IDisposable
{
	private readonly Aes cipher;

	private readonly Aes tweakCipher;

	private readonly ICryptoTransform encryptor;

	private readonly ICryptoTransform decryptor;

	private readonly ICryptoTransform tweakEncryptor;

	private readonly byte[] tweak = new byte[16];

	private readonly byte[] xor = new byte[16];

	private readonly byte[] encryptedTweak = new byte[16];

	/// <summary>
	/// Creates an AES-XTS-128 transformer
	/// </summary>
	public XtsBlockTransform(byte[] dataKey, byte[] tweakKey)
	{
		cipher = CreateEcbAes(dataKey);
		tweakCipher = CreateEcbAes(tweakKey);
		encryptor = cipher.CreateEncryptor();
		decryptor = cipher.CreateDecryptor();
		tweakEncryptor = tweakCipher.CreateEncryptor();
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

	public void EncryptSector(byte[] sector, ulong sectorNum)
	{
		CryptSector(sector, sectorNum, encrypt: true);
	}

	public void DecryptSector(byte[] sector, ulong sectorNum)
	{
		CryptSector(sector, sectorNum);
	}

	/// <summary>
	/// Encrypts or decrypts the given sector with XEX.
	/// </summary>
	/// <param name="sector">Sector plain/ciphertext</param>
	/// <param name="sectorNum">Sector index number</param>
	/// <param name="encrypt">If this is set to true, encrypt the sector</param>
	public void CryptSector(byte[] sector, ulong sectorNum, bool encrypt = false)
	{
		BinaryPrimitives.WriteUInt64LittleEndian(tweak, sectorNum);
		for (int i = 8; i < 16; i++)
		{
			tweak[i] = 0;
		}
		tweakEncryptor.TransformBlock(tweak, 0, 16, encryptedTweak, 0);
		ICryptoTransform cryptoTransform = (encrypt ? encryptor : decryptor);
		for (int j = 0; j < sector.Length; j += 16)
		{
			for (int k = 0; k < 16; k++)
			{
				xor[k] = (byte)(sector[k + j] ^ encryptedTweak[k]);
			}
			cryptoTransform.TransformBlock(xor, 0, 16, xor, 0);
			for (int l = 0; l < 16; l++)
			{
				sector[l + j] = (byte)(xor[l] ^ encryptedTweak[l]);
			}
			int num = 0;
			for (int m = 0; m < 16; m++)
			{
				byte b = encryptedTweak[m];
				encryptedTweak[m] = (byte)((2 * encryptedTweak[m]) | num);
				num = (b & 0x80) >> 7;
			}
			if (num != 0)
			{
				encryptedTweak[0] ^= 135;
			}
		}
	}

	public void Dispose()
	{
		encryptor.Dispose();
		decryptor.Dispose();
		tweakEncryptor.Dispose();
		cipher.Dispose();
		tweakCipher.Dispose();
	}
}
