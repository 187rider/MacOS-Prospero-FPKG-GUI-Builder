using System;
using System.IO;
using System.Security.Cryptography;
using LibProsperoPkg.Keys;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PFS;

/// <summary>
/// Derivation of the PFS image keys: EKPFS, the AES-XTS tweak/data pair, and the block sign key.
/// </summary>
public static class ProsperoPfsKeys
{
	/// <summary>The key-ladder index that yields the EKPFS / encryption key material.</summary>
	public const uint EkpfsIndex = 1u;

	/// <summary>
	/// Derives the 32-byte EKPFS from the package <paramref name="contentId" /> (36 chars) and the
	/// 32-char <paramref name="passcode" /> using SHA3-256.
	/// </summary>
	public static byte[] DeriveEkpfs(string contentId, string passcode)
	{
		ArgumentNullException.ThrowIfNull(contentId, "contentId");
		ArgumentNullException.ThrowIfNull(passcode, "passcode");
		return Crypto.ComputeKeys(contentId, passcode, 1u, useSha3: true);
	}

	/// <summary>
	/// Reproduces the 32-byte <c>pfs-image-key</c> returned by
	/// <c>sc2 --estimate</c>:
	/// <c>HMAC-SHA256(EKPFS(primaryId, passcode), pfsImageSeed)</c>.
	/// </summary>
	public static byte[] DerivePublisherPfsImageKey(string primaryId, string passcode, byte[] pfsImageSeed)
	{
		ValidateSeed(pfsImageSeed);
		return HMACSHA256.HashData(DeriveEkpfs(primaryId, passcode), pfsImageSeed);
	}

	/// <summary>
	/// Builds the exact 0x800-byte publisher CNT <c>IMAGE_KEY</c> payload used by
	/// <c>sc2 --build</c>. The first 0x180 bytes are the deterministically padded
	/// RSA-3072 wrap of <paramref name="pfsImageKey" /> under the mount-image modulus.
	/// The remaining 0x680 bytes are
	/// <c>SHAKE128(SHA3-256(pfsImageKey), 0x680)</c>.
	/// </summary>
	public static byte[] BuildPublisherImageKey(byte[] pfsImageKey)
	{
		ValidateEkpfs(pfsImageKey);
		byte[] array = ProsperoKeys.MountImageKey.ToArray();
		if (array.Length != 384)
		{
			throw new InvalidDataException("The publisher mount-image modulus must be exactly 0x180 bytes.");
		}
		byte[] array2 = new byte[2048];
		Crypto.RsaPkcs1EncryptKey(array, pfsImageKey, deterministic: true).CopyTo(array2, 0);
		ProsperoSha3.Shake128Data(ProsperoSha3.HashData(pfsImageKey), array2.AsSpan(384));
		return array2;
	}

	/// <summary>
	/// Derives the AES-XTS (tweak, data) key pair for the outer PFS image from the
	/// <paramref name="ekpfs" /> and the 16-byte superblock <paramref name="seed" />.
	/// </summary>
	public static (byte[] TweakKey, byte[] DataKey) DeriveImageEncryptionKeys(byte[] ekpfs, byte[] seed)
	{
		ValidateEkpfs(ekpfs);
		ValidateSeed(seed);
		Tuple<byte[], byte[]> tuple = Crypto.PfsGenEncKey(ekpfs, seed, newCrypt: true);
		return (TweakKey: tuple.Item1, DataKey: tuple.Item2);
	}

	/// <summary>
	/// Convenience overload: derives the EKPFS from <paramref name="contentId" />/<paramref name="passcode" />
	/// and then the AES-XTS (tweak, data) key pair in one step.
	/// </summary>
	public static (byte[] TweakKey, byte[] DataKey) DeriveImageEncryptionKeys(string contentId, string passcode, byte[] seed)
	{
		return DeriveImageEncryptionKeys(DeriveEkpfs(contentId, passcode), seed);
	}

	/// <summary>
	/// Derives the 32-byte sign key for the outer PFS image's signed metadata blocks from the
	/// <paramref name="ekpfs" /> and the 16-byte superblock <paramref name="seed" />.
	/// </summary>
	public static byte[] DeriveImageSignKey(byte[] ekpfs, byte[] seed)
	{
		ValidateEkpfs(ekpfs);
		ValidateSeed(seed);
		return Crypto.PfsGenSignKey(ekpfs, seed, newCrypt: true);
	}

	private static void ValidateEkpfs(byte[] ekpfs)
	{
		ArgumentNullException.ThrowIfNull(ekpfs, "ekpfs");
		if (ekpfs.Length != 32)
		{
			throw new ArgumentException($"EKPFS must be exactly 32 bytes (was {ekpfs.Length}).", "ekpfs");
		}
	}

	private static void ValidateSeed(byte[] seed)
	{
		ArgumentNullException.ThrowIfNull(seed, "seed");
		if (seed.Length != 16)
		{
			throw new ArgumentException($"PFS seed must be exactly 16 bytes (was {seed.Length}).", "seed");
		}
	}
}
