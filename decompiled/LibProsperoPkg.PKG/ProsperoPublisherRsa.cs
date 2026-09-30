using System;
using System.Security.Cryptography;
using LibProsperoPkg.Keys;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PKG;

public static class ProsperoPublisherRsa
{
	public const int ModulusSize = 384;

	public const int PasscodeBankCount = 7;

	public const int CntHeaderSize = 4096;

	public const int CntWrapPasscodeIndex = 3;

	public static byte[] BuildCntHeaderWrap(ReadOnlySpan<byte> cntHeader)
	{
		if (cntHeader.Length < 4096)
		{
			throw new ArgumentException($"The CNT header wrap requires at least 0x{4096:X} bytes.", "cntHeader");
		}
		byte[] data = ProsperoSha3.HashData(cntHeader.Slice(0, 4096));
		return Crypto.RsaPkcs1EncryptKey(ProsperoKeys.GetPasscodeModulus(3).ToArray(), data, deterministic: true);
	}

	public static bool VerifyCntHeaderWrap(ReadOnlySpan<byte> cntHeader, ReadOnlySpan<byte> storedWrap)
	{
		if (storedWrap.Length != 384)
		{
			return false;
		}
		return CryptographicOperations.FixedTimeEquals(BuildCntHeaderWrap(cntHeader), storedWrap);
	}

	public static bool VerifyTokenRs256(ReadOnlySpan<byte> signedData, ReadOnlySpan<byte> signature)
	{
		if (signature.Length != 384)
		{
			return false;
		}
		using RSA rSA = RSA.Create();
		rSA.ImportParameters(new RSAParameters
		{
			Modulus = ProsperoKeys.TokenKey.ToArray(),
			Exponent = new byte[3] { 1, 0, 1 }
		});
		return rSA.VerifyData(signedData, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
	}
}
