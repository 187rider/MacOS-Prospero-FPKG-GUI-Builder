using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace LibProsperoPkg.Util;

public static class Crypto
{
	/// <summary>
	/// Wraps data with an RSA public modulus (public exponent 65537) using
	/// EME-PKCS#1-v1_5. Prospero publisher key slots use 384-byte RSA-3072
	/// moduli, so the returned ciphertext has the same length as
	/// <paramref name="modulus" />.
	/// </summary>
	public static byte[] RsaPkcs1EncryptKey(byte[] modulus, byte[] data, bool deterministic = false)
	{
		ArgumentNullException.ThrowIfNull(modulus, "modulus");
		ArgumentNullException.ThrowIfNull(data, "data");
		if (data.Length > modulus.Length - 11)
		{
			throw new ArgumentException("RSA PKCS#1 v1.5 input is too large for the modulus.", "data");
		}
		if (deterministic)
		{
			byte[] array = new byte[modulus.Length];
			array[1] = 2;
			int num = array.Length - data.Length - 3;
			byte[] array2 = SHA256.HashData(SHA256.HashData(modulus.Concat(data).ToArray()));
			uint[] array3 = new uint[array2.Length / 4];
			for (int i = 0; i < array3.Length; i++)
			{
				int num2 = i * 4;
				array3[i] = (uint)((array2[num2] << 24) | (array2[num2 + 1] << 16) | (array2[num2 + 2] << 8) | array2[num2 + 3]);
			}
			MersenneTwister mersenneTwister = new MersenneTwister(array3);
			int num3 = 2;
			while (num3 < 2 + num)
			{
				byte[] array4 = new byte[48];
				for (int j = 0; j < 12; j++)
				{
					uint num4 = mersenneTwister.Int32();
					int num5 = j * 4;
					array4[num5] = (byte)(num4 >> 24);
					array4[num5 + 1] = (byte)(num4 >> 16);
					array4[num5 + 2] = (byte)(num4 >> 8);
					array4[num5 + 3] = (byte)num4;
				}
				byte[] array5 = SHA256.HashData(array4);
				foreach (byte b in array5)
				{
					if (b != 0)
					{
						array[num3++] = b;
						if (num3 == 2 + num)
						{
							break;
						}
					}
				}
			}
			array[2 + num] = 0;
			data.CopyTo(array, 3 + num);
			return RsaPublicModExp(array, modulus, 65537);
		}
		using RSA rSA = RSA.Create();
		rSA.ImportParameters(new RSAParameters
		{
			Modulus = modulus,
			Exponent = new byte[3] { 1, 0, 1 }
		});
		return rSA.Encrypt(data, RSAEncryptionPadding.Pkcs1);
	}

	private static byte[] RsaPublicModExp(byte[] value, byte[] modulusBytes, int exponentValue)
	{
		BigInteger value2 = new BigInteger(value, isUnsigned: true, isBigEndian: true);
		BigInteger modulus = new BigInteger(modulusBytes, isUnsigned: true, isBigEndian: true);
		BigInteger exponent = new BigInteger(exponentValue);
		byte[] array = BigInteger.ModPow(value2, exponent, modulus).ToByteArray(isUnsigned: true, isBigEndian: true);
		if (array.Length > modulusBytes.Length)
		{
			throw new CryptographicException("RSA result exceeds the modulus size.");
		}
		if (array.Length == modulusBytes.Length)
		{
			return array;
		}
		byte[] array2 = new byte[modulusBytes.Length];
		array.CopyTo(array2, array2.Length - array.Length);
		return array2;
	}

	/// <summary>
	/// Key-derivation step:
	/// a common function to generate a final key for PFS
	/// </summary>
	public static byte[] PfsGenCryptoKey(byte[] ekpfs, byte[] seed, uint index)
	{
		byte[] array = new byte[4 + seed.Length];
		Array.Copy(BitConverter.GetBytes(index), array, 4);
		Array.Copy(seed, 0, array, 4, seed.Length);
		using HMACSHA256 hMACSHA = new HMACSHA256(ekpfs);
		return hMACSHA.ComputeHash(array);
	}

	/// <summary>
	/// Generates a (tweak, data) key pair for XTS
	/// </summary>
	public static Tuple<byte[], byte[]> PfsGenEncKey(byte[] ekpfs, byte[] seed, bool newCrypt = false)
	{
		byte[] src = PfsGenCryptoKey(newCrypt ? HMACSHA256.HashData(ekpfs, seed) : ekpfs, seed, 1u);
		byte[] array = new byte[16];
		byte[] array2 = new byte[16];
		Buffer.BlockCopy(src, 0, array2, 0, 16);
		Buffer.BlockCopy(src, 16, array, 0, 16);
		return Tuple.Create(array2, array);
	}

	/// <summary>
	/// Key-derivation step:
	/// asigning key generator based on EKPFS and PFS header seed
	/// </summary>
	public static byte[] PfsGenSignKey(byte[] ekpfs, byte[] seed, bool newCrypt = false)
	{
		return PfsGenCryptoKey(newCrypt ? HMACSHA256.HashData(ekpfs, seed) : ekpfs, seed, 2u);
	}

	/// <summary>
	/// sceSblPfsSetKeys: Turns the EEKPfs to an EKPfs
	/// </summary>
	public static byte[] DecryptEEKPfs(byte[] eekpfs, RSAKeyset keyset)
	{
		RSAParameters parameters = new RSAParameters
		{
			D = keyset.PrivateExponent,
			DP = keyset.Exponent1,
			DQ = keyset.Exponent2,
			Exponent = keyset.PublicExponent,
			InverseQ = keyset.Coefficient,
			Modulus = keyset.Modulus,
			P = keyset.Prime1,
			Q = keyset.Prime2
		};
		using RSA rSA = RSA.Create();
		rSA.KeySize = 2048;
		rSA.ImportParameters(parameters);
		return RsaRawModExp(eekpfs, keyset.Modulus, keyset.PrivateExponent);
	}

	/// <summary>
	/// Textbook (unpadded) RSA: computes <c>value^exponent mod modulus</c>. All inputs and
	/// the 256-byte result are big-endian. Used for the raw RSA EEKPFS operation.
	/// </summary>
	private static byte[] RsaRawModExp(byte[] value, byte[] modulus, byte[] exponent)
	{
		BigInteger value2 = new BigInteger(value.AsEnumerable().Reverse().Concat(new byte[1])
			.ToArray());
		BigInteger modulus2 = new BigInteger(modulus.AsEnumerable().Reverse().Concat(new byte[1])
			.ToArray());
		BigInteger exponent2 = new BigInteger(exponent.AsEnumerable().Reverse().Concat(new byte[1])
			.ToArray());
		byte[] array = BigInteger.ModPow(value2, exponent2, modulus2).ToByteArray().Take(256)
			.ToArray();
		return array.Concat(Enumerable.Range(0, 256 - array.Length).Select((Func<int, byte>)((int _) => 0))).Reverse().ToArray();
	}

	/// <summary>
	/// Encrypts the given hash with the given public key (modulus)
	/// </summary>
	/// <param name="modulus"></param>
	/// <param name="hash"></param>
	/// <returns></returns>
	public static byte[] RSA2048EncryptKey(byte[] modulus, byte[] hash)
	{
		byte[] array = new byte[288];
		Buffer.BlockCopy(modulus, 0, array, 0, 256);
		Buffer.BlockCopy(hash, 0, array, 256, 32);
		byte[] array2 = Sha256(Sha256(array));
		uint[] array3 = new uint[8];
		for (int i = 0; i < 32; i += 4)
		{
			array3[i / 4] = (uint)((array2[i] << 24) | (array2[1 + i] << 16) | (array2[2 + i] << 8) | array2[3 + i]);
		}
		MersenneTwister mersenneTwister = new MersenneTwister(array3);
		MemoryStream memoryStream = new MemoryStream(48);
		byte[] array4 = new byte[256];
		array4[0] = 0;
		array4[1] = 2;
		array4[223] = 0;
		Buffer.BlockCopy(hash, 0, array4, 224, 32);
		int num = 2;
		while (num < 223)
		{
			memoryStream.Position = 0L;
			for (int j = 0; j < 12; j++)
			{
				memoryStream.WriteUInt32BE(mersenneTwister.Int32());
			}
			byte[] array5 = Sha256(memoryStream);
			foreach (byte b in array5)
			{
				if (num >= 223)
				{
					break;
				}
				if (b != 0)
				{
					array4[num++] = b;
				}
			}
		}
		return RSA2048Encrypt(array4, modulus);
	}

	/// <summary>
	/// Sign the given SHA-256 hash with PKCS1 padding
	/// </summary>
	/// <param name="sha256Hash">Hash</param>
	/// <param name="keyset">Keys to use</param>
	/// <returns>RSA 2048 signature of the hash</returns>
	public static byte[] RSA2048SignSha256(byte[] sha256Hash, RSAKeyset keyset)
	{
		using RSA rSA = RSA.Create();
		rSA.ImportParameters(new RSAParameters
		{
			P = keyset.Prime1,
			Q = keyset.Prime2,
			Exponent = keyset.PublicExponent,
			Modulus = keyset.Modulus,
			DP = keyset.Exponent1,
			DQ = keyset.Exponent2,
			InverseQ = keyset.Coefficient,
			D = keyset.PrivateExponent
		});
		return rSA.SignHash(sha256Hash, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
	}

	public static bool RSA2048VerifySha256(byte[] sha256Hash, byte[] signature, RSAKeyset keyset)
	{
		using RSA rSA = RSA.Create();
		rSA.ImportParameters(new RSAParameters
		{
			P = keyset.Prime1,
			Q = keyset.Prime2,
			Exponent = keyset.PublicExponent,
			Modulus = keyset.Modulus,
			DP = keyset.Exponent1,
			DQ = keyset.Exponent2,
			InverseQ = keyset.Coefficient,
			D = keyset.PrivateExponent
		});
		return rSA.VerifyHash(sha256Hash, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
	}

	/// <summary>
	/// Encrypts the value with 2048 bit RSA.
	/// Accepts and returns Big-Endian values
	/// </summary>
	/// <param name="value"></param>
	/// <param name="mod"></param>
	/// <param name="exp"></param>
	/// <returns></returns>
	public static byte[] RSA2048Encrypt(byte[] value, byte[] mod, int exp = 65537)
	{
		BigInteger value2 = new BigInteger(value.AsEnumerable().Reverse().ToArray());
		BigInteger modulus = new BigInteger(mod.AsEnumerable().Reverse().Concat(new byte[1])
			.ToArray());
		BigInteger exponent = new BigInteger(exp);
		IEnumerable<byte> enumerable = BigInteger.ModPow(value2, exponent, modulus).ToByteArray().Take(256);
		return enumerable.Concat(Enumerable.Range(0, 256 - enumerable.Count()).Select((Func<int, byte>)((int x) => 0))).Reverse().ToArray();
	}

	public static byte[] RSA2048Decrypt(byte[] ciphertext, RSAKeyset keyset)
	{
		using RSA rSA = RSA.Create();
		rSA.ImportParameters(new RSAParameters
		{
			P = keyset.Prime1,
			Q = keyset.Prime2,
			Exponent = keyset.PublicExponent,
			Modulus = keyset.Modulus,
			DP = keyset.Exponent1,
			DQ = keyset.Exponent2,
			InverseQ = keyset.Coefficient,
			D = keyset.PrivateExponent
		});
		return rSA.Decrypt(ciphertext, RSAEncryptionPadding.Pkcs1);
	}

	public static int AesCbcCfb128Encrypt(byte[] @out, byte[] @in, int size, byte[] key, byte[] iv)
	{
		AesCbcCfb128Crypt(@out, @in, size, key, iv, decrypt: false);
		return 0;
	}

	public static int AesCbcCfb128Decrypt(byte[] @out, byte[] @in, int size, byte[] key, byte[] iv)
	{
		AesCbcCfb128Crypt(@out, @in, size, key, iv, decrypt: true);
		return 0;
	}

	private static void AesCbcCfb128Crypt(byte[] output, byte[] input, int size, byte[] key, byte[] iv, bool decrypt)
	{
		ArgumentNullException.ThrowIfNull(output, "output");
		ArgumentNullException.ThrowIfNull(input, "input");
		ArgumentNullException.ThrowIfNull(key, "key");
		ArgumentNullException.ThrowIfNull(iv, "iv");
		if (size < 0 || size > input.Length || size > output.Length)
		{
			throw new ArgumentOutOfRangeException("size");
		}
		if (key.Length != 16)
		{
			throw new ArgumentException("AES-CBC-CFB128 requires a 16-byte key.", "key");
		}
		if (iv.Length != 16)
		{
			throw new ArgumentException("AES-CBC-CFB128 requires a 16-byte IV.", "iv");
		}
		if (size == 0)
		{
			return;
		}
		using Aes aes = Aes.Create();
		aes.Mode = CipherMode.ECB;
		aes.KeySize = 128;
		aes.Key = key;
		aes.Padding = PaddingMode.None;
		using ICryptoTransform cryptoTransform = aes.CreateEncryptor();
		using ICryptoTransform cryptoTransform2 = (decrypt ? aes.CreateDecryptor() : null);
		byte[] array = iv.ToArray();
		byte[] array2 = new byte[16];
		byte[] array3 = new byte[16];
		int num = size & -16;
		int i;
		for (i = 0; i < num; i += 16)
		{
			Buffer.BlockCopy(input, i, array2, 0, 16);
			if (decrypt)
			{
				cryptoTransform2.TransformBlock(array2, 0, 16, array3, 0);
				for (int j = 0; j < 16; j++)
				{
					output[i + j] = (byte)(array3[j] ^ array[j]);
				}
				Buffer.BlockCopy(array2, 0, array, 0, 16);
			}
			else
			{
				for (int k = 0; k < 16; k++)
				{
					array3[k] = (byte)(array2[k] ^ array[k]);
				}
				cryptoTransform.TransformBlock(array3, 0, 16, array, 0);
				Buffer.BlockCopy(array, 0, output, i, 16);
			}
		}
		int num2 = size - num;
		if (num2 == 0)
		{
			return;
		}
		if (decrypt)
		{
			Array.Clear(output, i, num2);
			return;
		}
		Array.Clear(array2);
		Buffer.BlockCopy(input, i, array2, 0, num2);
		for (int l = 0; l < 16; l++)
		{
			array3[l] = (byte)(array2[l] ^ array[l]);
		}
		cryptoTransform.TransformBlock(array3, 0, 16, array2, 0);
		Buffer.BlockCopy(array2, 0, output, i, num2);
	}

	/// <summary>
	/// Computes the SHA256 hash of the given data.
	/// </summary>
	public static byte[] Sha256(byte[] data)
	{
		return SHA256.HashData(data);
	}

	public static byte[] Sha256(Stream data)
	{
		data.Position = 0L;
		return SHA256.HashData(data);
	}

	/// <summary>
	/// Computes the SHA256 hash of the data in the stream between (start) and (start+length)
	/// </summary>
	public static byte[] Sha256(Stream data, long start, long length)
	{
		using SubStream data2 = new SubStream(data, start, length);
		return Sha256(data2);
	}

	/// <summary>
	/// Computes the SHA3-256 hash of the given data. SHA3-256 is the digest primitive used by the
	/// PS5 PFS: outer-image EKPFS key derivation, the compressed-file 'PFSC' digests, and
	/// the per-block hashes. Requires a runtime/platform that provides SHA-3 (verified on .NET 10).
	/// </summary>
	public static byte[] Sha3_256(byte[] data)
	{
		return ProsperoSha3.HashData(data);
	}

	/// <summary>Computes the SHA3-256 hash over the whole stream (used for PS5 CNT body/entry digests).</summary>
	public static byte[] Sha3_256(Stream data)
	{
		data.Position = 0L;
		return ProsperoSha3.HashData(data);
	}

	/// <summary>
	/// Computes the SHA3-256 hash of the data in the stream between (start) and (start+length). This is
	/// the PS5 CNT digest primitive (per-entry table, body-digest, sc-entry rollups).
	/// </summary>
	public static byte[] Sha3_256(Stream data, long start, long length)
	{
		using SubStream data2 = new SubStream(data, start, length);
		return Sha3_256(data2);
	}

	public static byte[] HmacSha256(byte[] key, byte[] data)
	{
		return HMACSHA256.HashData(key, data);
	}

	public static byte[] HmacSha256(byte[] key, Stream data)
	{
		data.Position = 0L;
		return HMACSHA256.HashData(key, data);
	}

	public static byte[] HmacSha256(byte[] key, Stream data, long start, long length)
	{
		using SubStream data2 = new SubStream(data, start, length);
		return HmacSha256(key, data2);
	}

	/// <summary>
	/// Computes keys for the package.
	/// The key is the result of a SHA256 hash of the concatenation of:
	///  - The SHA256 hash of the index (4 bytes big-endian)
	///  - The SHA256 hash of the Contend ID (36 bytes padded to 48 with nulls)
	///  - The passcode
	/// The EKPFS is Index 1. 
	/// </summary>
	public static byte[] ComputeKeys(string ContentId, string Passcode, uint Index)
	{
		return ComputeKeys(ContentId, Passcode, Index, useSha3: false);
	}

	/// <summary>
	/// Computes keys for the package, selecting the per-generation digest primitive.
	/// EKPFS (Index 1) = H( H(Index, 4 bytes big-endian) || H(ContentId padded to 48 with nulls) || Passcode ),
	/// where H = SHA3-256 (useSha3: true) or SHA-256 (useSha3: false).
	/// The SHA3 form yields the EKPFS used for the outer PFS image; combine it
	/// with <see cref="M:LibProsperoPkg.Util.Crypto.PfsGenEncKey(System.Byte[],System.Byte[],System.Boolean)" />/<see cref="M:LibProsperoPkg.Util.Crypto.PfsGenSignKey(System.Byte[],System.Byte[],System.Boolean)" /> using <c>newCrypt: true</c>.
	/// </summary>
	public static byte[] ComputeKeys(string ContentId, string Passcode, uint Index, bool useSha3)
	{
		if (ContentId.Length != 36)
		{
			throw new Exception("Content ID must be 36 characters long");
		}
		if (Passcode.Length != 32)
		{
			throw new Exception("Passcode must be 32 characters long");
		}
		Func<byte[], byte[]> func = (useSha3 ? new Func<byte[], byte[]>(Sha3_256) : new Func<byte[], byte[]>(Sha256));
		byte[] array = new byte[96];
		Buffer.BlockCopy(func(BitConverter.GetBytes(Index).AsEnumerable().Reverse()
			.ToArray()), 0, array, 0, 32);
		Buffer.BlockCopy(func(Encoding.ASCII.GetBytes(ContentId.PadRight(48, '\0'))), 0, array, 32, 32);
		Buffer.BlockCopy(Encoding.ASCII.GetBytes(Passcode), 0, array, 64, 32);
		return func(array);
	}

	public static byte[] CreateKeystone(string passcode, ushort version = 2)
	{
		byte[] array = new byte[32];
		Encoding.ASCII.GetBytes("keystone").CopyTo(array, 0);
		BitConverter.GetBytes(version).CopyTo(array, 8);
		array[10] = 1;
		byte[] key;
		byte[] key2;
		if (version < 3)
		{
			byte[] keystone_hmac_key = CryptoKeys.keystone_hmac_key;
			byte[] keystone_mac_data = CryptoKeys.keystone_mac_data;
			key = keystone_mac_data;
			key2 = keystone_hmac_key;
		}
		else
		{
			byte[] keystone_hmac_key_ps = CryptoKeys.keystone_hmac_key_ps5;
			byte[] keystone_mac_data = CryptoKeys.keystone_mac_data_ps5;
			key = keystone_mac_data;
			key2 = keystone_hmac_key_ps;
		}
		byte[] second = HmacSha256(key2, Encoding.ASCII.GetBytes(passcode));
		byte[] second2 = HmacSha256(key, array.Concat(second).ToArray());
		return array.Concat(second).Concat(second2).ToArray();
	}

	/// <summary>
	/// XORs a with b and stores the result in a
	/// </summary>
	public static byte[] Xor(this byte[] a, byte[] b)
	{
		for (int i = 0; i < a.Length; i++)
		{
			a[i] ^= b[i];
		}
		return a;
	}

	public static string AsHexCompact(this byte[] k)
	{
		StringBuilder stringBuilder = new StringBuilder(k.Length * 2);
		foreach (byte b in k)
		{
			stringBuilder.AppendFormat("{0:X2}", b);
		}
		return stringBuilder.ToString();
	}

	public static byte[] FromHexCompact(this string k)
	{
		List<byte> list = new List<byte>();
		string text = k.Replace(" ", "");
		int i = 0;
		while (i < text.Length - 1)
		{
			byte b = 0;
			for (int j = 0; j < 2; j++, i++)
			{
				b <<= 4;
				int num;
				if (text[i] >= '0' && text[i] <= '9')
				{
					num = 48;
				}
				else if (text[i] >= 'a' && text[i] <= 'f')
				{
					num = 87;
				}
				else
				{
					if (text[i] < 'A' || text[i] > 'F')
					{
						continue;
					}
					num = 55;
				}
				b |= (byte)(text[i] - num);
			}
			list.Add(b);
		}
		return list.ToArray();
	}

	public static int CombineHashCodes(params int[] hashCodes)
	{
		int num = 352654597;
		int num2 = num;
		int num3 = 0;
		foreach (int num4 in hashCodes)
		{
			if (num3 % 2 == 0)
			{
				num = ((num << 5) + num + (num >> 27)) ^ num4;
			}
			else
			{
				num2 = ((num2 << 5) + num2 + (num2 >> 27)) ^ num4;
			}
			num3++;
		}
		return num + num2 * 1566083941;
	}
}
