using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PKG;

public abstract class Entry
{
	public MetaEntry meta;

	public abstract EntryId Id { get; }

	public abstract uint Length { get; }

	public abstract string Name { get; }

	public abstract void Write(Stream s);

	public void WriteEncrypted(Stream s, string contentId, string passcode)
	{
		WriteEncrypted(s, contentId, passcode, publisherProfile: false);
	}

	public void WriteEncrypted(Stream s, string contentId, string passcode, bool publisherProfile)
	{
		byte[] second = Crypto.ComputeKeys(contentId, passcode, meta.KeyIndex, publisherProfile);
		byte[] data = meta.GetBytes().Concat(second).ToArray();
		byte[] source = (publisherProfile ? Crypto.Sha3_256(data) : Crypto.Sha256(data));
		byte[] array = new byte[checked((int)((Length + 15) & 0xFFFFFFF0u))];
		using (MemoryStream s2 = new MemoryStream(array))
		{
			Write(s2);
		}
		Crypto.AesCbcCfb128Encrypt(array, array, array.Length, source.Skip(16).Take(16).ToArray(), source.Take(16).ToArray());
		s.Write(array, 0, array.Length);
	}

	private static byte[] Decrypt(byte[] entryBytes, byte[] keySeed, MetaEntry meta, bool publisherProfile)
	{
		byte[] data = meta.GetBytes().Concat(keySeed).ToArray();
		byte[] source = (publisherProfile ? Crypto.Sha3_256(data) : Crypto.Sha256(data));
		byte[] array = new byte[entryBytes.Length];
		Crypto.AesCbcCfb128Decrypt(array, entryBytes, array.Length, source.Skip(16).Take(16).ToArray(), source.Take(16).ToArray());
		int num = checked((int)meta.DataSize);
		if (array.Length < num)
		{
			throw new InvalidDataException("Protected CNT entry is shorter than its logical size.");
		}
		if ((num & 0xF) != 0 && array.Length < ((num + 15) & -16))
		{
			uint id = (uint)meta.id;
			if ((id != 1027 && id - 8224 > 1) || num != 532 || array[0] != 210 || array[1] != 148 || array[2] != 160 || array[3] != 24)
			{
				throw new CryptographicException($"Protected CNT entry 0x{id:X4} has a {num & 0xF}-byte " + "truncated CBC tail which cannot be generically decrypted.");
			}
			SHA1.HashData(array.AsSpan(0, 512)).CopyTo(array.AsSpan(512, 20));
		}
		return array.AsSpan(0, num).ToArray();
	}

	public static byte[] Decrypt(byte[] entryBytes, string contentId, string passcode, MetaEntry meta)
	{
		return Decrypt(entryBytes, contentId, passcode, meta, publisherProfile: false);
	}

	public static byte[] Decrypt(byte[] entryBytes, string contentId, string passcode, MetaEntry meta, bool publisherProfile)
	{
		byte[] keySeed = Crypto.ComputeKeys(contentId, passcode, meta.KeyIndex, publisherProfile);
		return Decrypt(entryBytes, keySeed, meta, publisherProfile);
	}

	public static byte[] Decrypt(byte[] entryBytes, Pkg pkg, MetaEntry meta)
	{
		if (meta.KeyIndex != 3)
		{
			throw new Exception("We only have the key for encryption key 3");
		}
		return Decrypt(entryBytes, Crypto.RSA2048Decrypt(pkg.EntryKeys.Keys[3].key, RSAKeyset.PkgDerivedKey3Keyset), meta, publisherProfile: false);
	}
}
