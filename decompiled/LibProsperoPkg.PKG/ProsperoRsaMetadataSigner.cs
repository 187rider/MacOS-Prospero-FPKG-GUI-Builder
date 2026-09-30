using System;
using System.IO;
using System.Security.Cryptography;

namespace LibProsperoPkg.PKG;

public sealed class ProsperoRsaMetadataSigner : IProsperoMetadataSigner, IProsperoMetadataSignatureVerifier, IDisposable
{
	private readonly RSA rsa;

	public string ProfileName { get; }

	private ProsperoRsaMetadataSigner(RSA rsa, string profileName)
	{
		this.rsa = rsa;
		ProfileName = profileName;
		if (rsa.KeySize != 3072)
		{
			throw new ArgumentException($"Publisher metadata key must be RSA-3072, not RSA-{rsa.KeySize}.");
		}
	}

	public static ProsperoRsaMetadataSigner LoadPem(string path, string? profileName = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path, "path");
		RSA rSA = RSA.Create();
		try
		{
			rSA.ImportFromPem(File.ReadAllText(path));
			return new ProsperoRsaMetadataSigner(rSA, profileName ?? Path.GetFileName(path));
		}
		catch
		{
			rSA.Dispose();
			throw;
		}
	}

	public byte[] SignSha256(ReadOnlySpan<byte> sha256Digest)
	{
		if (sha256Digest.Length != 32)
		{
			throw new ArgumentException("A SHA-256 digest is exactly 32 bytes.", "sha256Digest");
		}
		byte[] array = rsa.SignHash(sha256Digest, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		if (array.Length != 384)
		{
			throw new CryptographicException("Publisher metadata signer returned a non-RSA-3072 signature.");
		}
		return array;
	}

	public bool VerifySha256(ReadOnlySpan<byte> sha256Digest, ReadOnlySpan<byte> signature)
	{
		if (sha256Digest.Length != 32 || signature.Length != 384)
		{
			return false;
		}
		return rsa.VerifyHash(sha256Digest, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
	}

	public void Dispose()
	{
		rsa.Dispose();
	}
}
