using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using LibProsperoPkg.Keys;

namespace LibProsperoPkg.PKG;

public static class ProsperoPkgSigner
{
	private sealed class EmbeddedSigner : IProsperoMetadataSigner, IProsperoMetadataSignatureVerifier
	{
		public string ProfileName => "embedded research RSA-3072";

		public byte[] SignSha256(ReadOnlySpan<byte> sha256Digest)
		{
			return SignDigest(sha256Digest.ToArray());
		}

		public bool VerifySha256(ReadOnlySpan<byte> sha256Digest, ReadOnlySpan<byte> signature)
		{
			return VerifyDigest(sha256Digest.ToArray(), signature.ToArray());
		}
	}

	public const int SignatureSize = 384;

	private static readonly byte[] EmbeddedModulusPrefix = new byte[16]
	{
		171, 29, 189, 67, 57, 73, 51, 22, 163, 92,
		64, 78, 44, 34, 151, 184
	};

	public static IProsperoMetadataSigner EmbeddedMetadataSigner { get; } = new EmbeddedSigner();

	public static bool IsAvailable => ProsperoKeys.IsAvailable;

	public static byte[] SignMetadata(ReadOnlySpan<byte> data)
	{
		using RSA rSA = ProsperoKeys.CreateMetadataRsa();
		return rSA.SignData(data.ToArray(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
	}

	public static bool VerifyMetadata(ReadOnlySpan<byte> data, byte[] signature)
	{
		ArgumentNullException.ThrowIfNull(signature, "signature");
		using RSA rSA = ProsperoKeys.CreateMetadataRsa();
		return rSA.VerifyData(data.ToArray(), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
	}

	public static byte[] SignDigest(byte[] sha256Digest)
	{
		ArgumentNullException.ThrowIfNull(sha256Digest, "sha256Digest");
		if (sha256Digest.Length != 32)
		{
			throw new ArgumentException("A SHA-256 digest is exactly 32 bytes.", "sha256Digest");
		}
		using RSA rSA = ProsperoKeys.CreateMetadataRsa();
		return rSA.SignHash(sha256Digest, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
	}

	public static bool VerifyDigest(byte[] sha256Digest, byte[] signature)
	{
		ArgumentNullException.ThrowIfNull(sha256Digest, "sha256Digest");
		ArgumentNullException.ThrowIfNull(signature, "signature");
		using RSA rSA = ProsperoKeys.CreateMetadataRsa();
		return rSA.VerifyHash(sha256Digest, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
	}

	public static byte[] MetadataModulus()
	{
		using RSA rSA = ProsperoKeys.CreateMetadataRsa();
		return rSA.ExportParameters(includePrivateParameters: false).Modulus ?? throw new InvalidOperationException("The PKG-metadata key exposes no modulus.");
	}

	public static bool VerifyKeyMaterial()
	{
		if (!IsAvailable)
		{
			return false;
		}
		byte[] array = MetadataModulus();
		if (array.Length != 384)
		{
			return false;
		}
		for (int i = 0; i < EmbeddedModulusPrefix.Length; i++)
		{
			if (array[i] != EmbeddedModulusPrefix[i])
			{
				return false;
			}
		}
		byte[] sha256Digest = SHA256.HashData(Encoding.ASCII.GetBytes("PSMT-PS5-PKG-SIGNER"));
		byte[] array2 = SignDigest(sha256Digest);
		if (array2.Length == 384)
		{
			return VerifyDigest(sha256Digest, array2);
		}
		return false;
	}

	public static byte[] ComputeEkpfs(string contentId, string passcode)
	{
		return ComputeKeys(contentId, passcode, 1u);
	}

	public static byte[] ComputeKeys(string contentId, string passcode, uint index)
	{
		ArgumentNullException.ThrowIfNull(contentId, "contentId");
		ArgumentNullException.ThrowIfNull(passcode, "passcode");
		if (contentId.Length != 36)
		{
			throw new ArgumentException($"Content id must be exactly 36 characters (was {contentId.Length}).", "contentId");
		}
		if (passcode.Length != 32)
		{
			throw new ArgumentException($"Passcode must be exactly 32 characters (was {passcode.Length}).", "passcode");
		}
		Span<byte> span = stackalloc byte[4];
		BinaryPrimitives.WriteUInt32BigEndian(span, index);
		byte[] array = new byte[96];
		SHA256.HashData(span).CopyTo(array.AsSpan(0));
		SHA256.HashData(Encoding.ASCII.GetBytes(contentId.PadRight(48, '\0'))).CopyTo(array.AsSpan(32));
		Encoding.ASCII.GetBytes(passcode).CopyTo(array.AsSpan(64));
		return SHA256.HashData(array);
	}

	public static (byte[] TweakKey, byte[] DataKey) DerivePfsEncryptionKeys(byte[] ekpfs, byte[] seed, bool newCrypt = false)
	{
		ArgumentNullException.ThrowIfNull(ekpfs, "ekpfs");
		ArgumentNullException.ThrowIfNull(seed, "seed");
		byte[] array = PfsGenCryptoKey(newCrypt ? HMACSHA256.HashData(ekpfs, seed) : ekpfs, seed, 1u);
		if (array.Length < 32)
		{
			throw new InvalidOperationException("PFS key derivation returned an undersized key.");
		}
		byte[] subArray = array[..16];
		byte[] subArray2 = array[16..32];
		return (TweakKey: subArray, DataKey: subArray2);
	}

	public static byte[] DerivePfsSignKey(byte[] ekpfs, byte[] seed)
	{
		return PfsGenCryptoKey(ekpfs, seed, 2u);
	}

	private static byte[] PfsGenCryptoKey(byte[] ekpfs, byte[] seed, uint index)
	{
		ArgumentNullException.ThrowIfNull(ekpfs, "ekpfs");
		ArgumentNullException.ThrowIfNull(seed, "seed");
		byte[] array = new byte[4 + seed.Length];
		BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(0, 4), index);
		seed.CopyTo(array.AsSpan(4));
		return HMACSHA256.HashData(ekpfs, array);
	}
}
