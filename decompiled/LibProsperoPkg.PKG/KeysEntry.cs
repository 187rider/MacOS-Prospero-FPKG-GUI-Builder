using System;
using System.IO;
using System.Text;
using LibProsperoPkg.Keys;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PKG;

public class KeysEntry : Entry
{
	private readonly bool publisherProfile;

	public byte[] seedDigest;

	public PkgEntryKey[] Keys;

	public override EntryId Id => EntryId.ENTRY_KEYS;

	public override string Name => null;

	public override uint Length
	{
		get
		{
			if (!publisherProfile)
			{
				return 2048u;
			}
			return 2944u;
		}
	}

	public KeysEntry(byte[] digest, PkgEntryKey[] keys)
		: this(digest, keys, publisherProfile: false)
	{
	}

	private KeysEntry(byte[] digest, PkgEntryKey[] keys, bool publisherProfile)
	{
		seedDigest = digest;
		Keys = keys;
		this.publisherProfile = publisherProfile;
	}

	public KeysEntry(string contentId, string passcode)
		: this(contentId, passcode, publisherProfile: false)
	{
	}

	public KeysEntry(string contentId, string passcode, bool publisherProfile, bool deterministic = false, string primaryId = null)
	{
		this.publisherProfile = publisherProfile;
		Keys = new PkgEntryKey[7];
		seedDigest = (publisherProfile ? Crypto.Sha3_256(Encoding.ASCII.GetBytes(contentId.PadRight(48, '\0'))) : Crypto.Sha256(Encoding.ASCII.GetBytes(contentId.PadRight(48, '\0'))));
		ReadOnlySpan<byte> readOnlySpan;
		for (uint num = 0u; num < 7; num++)
		{
			byte[] array = Crypto.ComputeKeys((publisherProfile && num == 1) ? (primaryId ?? contentId) : contentId, passcode, num, publisherProfile);
			if (publisherProfile)
			{
				readOnlySpan = ProsperoKeys.PasscodeKey.Slice((int)(num * 384), 384);
				byte[] modulus = readOnlySpan.ToArray();
				Keys[num] = new PkgEntryKey
				{
					digest = Crypto.Sha3_256(array).Xor(array),
					key = Crypto.RsaPkcs1EncryptKey(modulus, array, deterministic: true)
				};
			}
			else
			{
				int num2 = Math.Min((int)num, CryptoKeys.PkgPublicKeys.Length - 1);
				Keys[num] = new PkgEntryKey
				{
					digest = Crypto.Sha256(array).Xor(array),
					key = Crypto.RSA2048EncryptKey(CryptoKeys.PkgPublicKeys[num2], array)
				};
			}
		}
		PkgEntryKey obj = Keys[0];
		byte[] key;
		if (!publisherProfile)
		{
			key = Crypto.RSA2048EncryptKey(CryptoKeys.PkgPublicKeys[0], Encoding.ASCII.GetBytes(passcode));
		}
		else
		{
			readOnlySpan = ProsperoKeys.PasscodeKey;
			key = Crypto.RsaPkcs1EncryptKey(readOnlySpan.Slice(0, 384).ToArray(), Encoding.ASCII.GetBytes(passcode), deterministic: true);
		}
		obj.key = key;
	}

	public override void Write(Stream s)
	{
		s.Write(seedDigest, 0, 32);
		PkgEntryKey[] keys = Keys;
		foreach (PkgEntryKey pkgEntryKey in keys)
		{
			s.Write(pkgEntryKey.digest, 0, 32);
		}
		keys = Keys;
		foreach (PkgEntryKey pkgEntryKey2 in keys)
		{
			s.Write(pkgEntryKey2.key, 0, pkgEntryKey2.key.Length);
		}
	}

	public static KeysEntry Read(MetaEntry e, Stream pkg)
	{
		long num = (long)e.DataSize - 256L;
		if (num <= 0 || num % 7 != 0L)
		{
			throw new InvalidDataException($"ENTRY_KEYS has invalid size {e.DataSize}: expected a 32-byte seed digest, {7} 32-byte key digests and {7} equal-size wrapped keys.");
		}
		int num2;
		byte[] digest;
		byte[][] array;
		PkgEntryKey[] array2;
		checked
		{
			num2 = (int)unchecked(num / 7);
			pkg.Position = e.DataOffset;
			digest = pkg.ReadBytes(32);
			array = new byte[7][];
			array2 = new PkgEntryKey[7];
		}
		for (int i = 0; i < 7; i++)
		{
			array[i] = pkg.ReadBytes(32);
		}
		for (int j = 0; j < 7; j++)
		{
			array2[j] = new PkgEntryKey
			{
				digest = array[j],
				key = pkg.ReadBytes(num2)
			};
		}
		return new KeysEntry(digest, array2, num2 == 384)
		{
			meta = e
		};
	}

	public static KeysEntry FromPublisherBytes(byte[] bytes)
	{
		ArgumentNullException.ThrowIfNull(bytes, "bytes");
		if (bytes.Length != 2944)
		{
			throw new InvalidDataException($"Publisher ENTRY_KEYS must contain exactly 0xB80 bytes, not 0x{bytes.Length:X}.");
		}
		MetaEntry e = new MetaEntry
		{
			id = EntryId.ENTRY_KEYS,
			DataOffset = 0u,
			DataSize = checked((uint)bytes.Length)
		};
		using MemoryStream pkg = new MemoryStream(bytes, writable: false);
		return Read(e, pkg);
	}
}
