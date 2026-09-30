using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PKG;

public static class ProsperoFlexibleContentFinalizer
{
	private readonly record struct Manifest(string ContentId, long PfsMetadataOffset);

	private sealed class FlexibleContentToken
	{
		public required int FormatVersion { get; init; }

		public required (byte[] First, byte[] Second) PfsCertificates { get; init; }

		public required (byte[] First, byte[] Second) FihCertificates { get; init; }

		public required (byte[] First, byte[] Second) CntCertificates { get; init; }

		public required byte[] AccessToken { get; init; }

		public required ulong RequiredSystemSoftwareVersion { get; init; }

		public static FlexibleContentToken Load(string path, string contentId, string passcode)
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(File.ReadAllBytes(path));
			JsonElement rootElement = jsonDocument.RootElement;
			int @int = rootElement.GetProperty("tokenFormatVersion").GetInt32();
			JsonElement property2;
			byte[] array;
			JsonElement property4;
			switch (@int)
			{
			case 0:
			{
				JsonElement property5 = rootElement.GetProperty("binary").GetProperty("flexibleContent");
				property2 = property5.GetProperty("certificates");
				array = Base64UrlDecode(property5.GetProperty("accessTokens").GetProperty(contentId).GetString());
				property4 = rootElement.GetProperty("config").GetProperty("flexibleContent");
				break;
			}
			case 1:
			{
				JsonElement property = rootElement.GetProperty("binary").GetProperty("flexibleContents");
				property2 = property.GetProperty("certificates").GetProperty(contentId);
				JsonElement property3 = property.GetProperty("accessTokens").GetProperty(contentId);
				Span<byte> span = stackalloc byte[16];
				ProsperoSha3.Shake128Data(Encoding.ASCII.GetBytes("encryptby" + passcode + "4token"), span);
				array = DecryptCompactJwe(property3.GetString(), span);
				if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(TrimHexPrefix(rootElement.GetProperty("digest").GetProperty("flexibleContents").GetProperty("accessTokens")
					.GetProperty(contentId)
					.GetString())), ProsperoSha3.HashData(array)))
				{
					throw new InvalidDataException("FGC access-token digest does not match token JSON.");
				}
				property4 = rootElement.GetProperty("config").GetProperty("flexibleContents").GetProperty(contentId);
				break;
			}
			default:
				throw new InvalidDataException($"FGC tokenFormatVersion {@int} is not supported.");
			}
			Span<byte> span2 = stackalloc byte[32];
			ProsperoSha3.Shake128Data(Encoding.ASCII.GetBytes("passcode" + passcode), span2);
			if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(TrimHexPrefix(property4.GetProperty("passcodeDigest").GetString())), span2))
			{
				throw new InvalidDataException("FGC passcodeDigest does not match the supplied passcode.");
			}
			return new FlexibleContentToken
			{
				FormatVersion = @int,
				PfsCertificates = ReadCertificates(property2, "PFS0", "PFS1"),
				FihCertificates = ReadCertificates(property2, "FIH0", "FIH1"),
				CntCertificates = ReadCertificates(property2, "CNT0", "CNT1"),
				AccessToken = array,
				RequiredSystemSoftwareVersion = Convert.ToUInt64(TrimHexPrefix(property4.GetProperty("requiredSystemSoftwareVersion").GetString()), 16)
			};
		}

		public void ValidatePartnerModulus(RSA rsa)
		{
			byte[] array = rsa.ExportParameters(includePrivateParameters: false).Modulus ?? throw new CryptographicException("FGC private key exposes no modulus.");
			byte[][] array2 = new byte[6][] { PfsCertificates.First, PfsCertificates.Second, FihCertificates.First, FihCertificates.Second, CntCertificates.First, CntCertificates.Second };
			foreach (byte[] array3 in array2)
			{
				if (array3.Length != 896 || !CryptographicOperations.FixedTimeEquals(array3.AsSpan(128, 384), array))
				{
					throw new InvalidDataException("FGC token certificate modulus does not match the partner private key.");
				}
			}
		}

		private static (byte[] First, byte[] Second) ReadCertificates(JsonElement element, string first, string second)
		{
			return (First: Base64UrlDecode(element.GetProperty(first).GetString()), Second: Base64UrlDecode(element.GetProperty(second).GetString()));
		}
	}

	private const int BlockSize = 65536;

	private const int CertificateSize = 896;

	private const int RsaSize = 384;

	private const int AuthenticationSize = 2560;

	private const int PfsSignOffset = 49152;

	private const int FihSignOffset = 61440;

	private const int CntSignOffset = 4096;

	private const int SuperblockSystemVersionOffset = 864;

	private const int SuperblockIcvOffset = 896;

	private const int SuperblockIcvPreimageSize = 1440;

	private const int FihStateOffset = 4;

	private const int FihSuperblockDigestOffset = 48;

	private const int FihSuperblockOffsetField = 32;

	private const uint ImageDigestsEntryId = 1034u;

	public static ProsperoFlexibleContentFinalizationResult Finalize(ProsperoFlexibleContentFinalizationOptions options)
	{
		ArgumentNullException.ThrowIfNull(options, "options");
		ValidateInputFiles(options);
		string text;
		using (FileStream stream = OpenReadWrite(options.SubcontainerPath))
		{
			text = ReadCntContentId(stream);
		}
		Manifest manifest = ReadManifest(options.ManifestPath);
		if (!string.Equals(text, manifest.ContentId, StringComparison.Ordinal))
		{
			throw new InvalidDataException($"Manifest contentId '{manifest.ContentId}' does not match CNT content id '{text}'.");
		}
		FlexibleContentToken flexibleContentToken = FlexibleContentToken.Load(options.TokenPath, text, options.Passcode);
		using RSA rsa = LoadPartnerPrivateKey(options.PartnerPrivateKeyPath);
		flexibleContentToken.ValidatePartnerModulus(rsa);
		long num;
		long num2;
		checked
		{
			using (FileStream stream2 = OpenReadWrite(options.FixedInfoHeaderPath))
			{
				byte[] array = ReadRange(stream2, 0L, 65536);
				EnsureMagic(array, ProsperoPkgLayout.FihMagic, "FIH");
				num = (long)BinaryPrimitives.ReadUInt64LittleEndian(array.AsSpan(32, 8));
			}
			num2 = num - manifest.PfsMetadataOffset;
		}
		if (num2 < 0 || num2 > new FileInfo(options.PfsMetadataPath).Length - 65536)
		{
			throw new InvalidDataException("The FIH superblock offset is outside the pfsmeta extent from manifest.json.");
		}
		byte[] superblockDigest = FinalizeSuperblock(options.PfsMetadataPath, num2, flexibleContentToken, rsa);
		byte[] fixedInfoDigest = FinalizeFih(options.FixedInfoHeaderPath, superblockDigest, flexibleContentToken, rsa);
		FinalizeCnt(options.SubcontainerPath, num, superblockDigest, fixedInfoDigest, flexibleContentToken, options.Passcode, rsa);
		return new ProsperoFlexibleContentFinalizationResult
		{
			SuperblockDigest = superblockDigest,
			FixedInfoDigest = fixedInfoDigest,
			SuperblockOffsetInPfsMetadata = num2,
			TokenFormatVersion = flexibleContentToken.FormatVersion
		};
	}

	private static byte[] FinalizeSuperblock(string path, long offset, FlexibleContentToken token, RSA rsa)
	{
		using FileStream stream = OpenReadWrite(path);
		byte[] array = ReadRange(stream, offset, 65536);
		BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(864, 8), token.RequiredSystemSoftwareVersion);
		array.AsSpan(896, 32).Clear();
		ProsperoSha3.HashData(array.AsSpan(0, 1440)).CopyTo(array, 896);
		WriteAuthentication(array, 49152, token.PfsCertificates, rsa, ProsperoSha3.HashData(array.AsSpan(0, 49152)));
		WriteRange(stream, offset, array);
		return ProsperoSha3.HashData(array);
	}

	private static byte[] FinalizeFih(string path, byte[] superblockDigest, FlexibleContentToken token, RSA rsa)
	{
		using FileStream stream = OpenReadWrite(path);
		byte[] array = ReadRange(stream, 0L, 65536);
		EnsureMagic(array, ProsperoPkgLayout.FihMagic, "FIH");
		uint num = BinaryPrimitives.ReadUInt32LittleEndian(array.AsSpan(4, 4));
		BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(4, 4), num | 0x8010);
		superblockDigest.CopyTo(array, 48);
		WriteAuthentication(array, 61440, token.FihCertificates, rsa, ProsperoSha3.HashData(array.AsSpan(0, 61440)));
		WriteRange(stream, 0L, array);
		return ProsperoSha3.HashData(array);
	}

	private static void FinalizeCnt(string cntPath, long absoluteSuperblockOffset, byte[] superblockDigest, byte[] fixedInfoDigest, FlexibleContentToken token, string passcode, RSA rsa)
	{
		using FileStream stream = OpenReadWrite(cntPath);
		ProsperoPkg prosperoPkg = ProsperoPkgReader.Read(stream);
		ProsperoPkgHeader prosperoPkgHeader = prosperoPkg.Header ?? throw new InvalidDataException("FGC subcontainer has no CNT header.");
		if (prosperoPkg.Type != ProsperoPkgType.Meta)
		{
			throw new InvalidDataException("FGC subcontainer must be a standalone CNT file.");
		}
		byte[] array = ReadRange(stream, 0L, 65536);
		EnsureMagic(array, ProsperoPkgLayout.CntMagic, "CNT");
		BinaryPrimitives.WriteUInt32BigEndian(array.AsSpan(4, 4), BinaryPrimitives.ReadUInt32BigEndian(array.AsSpan(4, 4)) | 0x80000000u);
		superblockDigest.CopyTo(array, 1088);
		fixedInfoDigest.CopyTo(array, 1120);
		WriteRange(stream, 0L, array);
		ReplaceAccessToken(stream, prosperoPkg.Entries, token.AccessToken);
		UpdateImageDigests(stream, prosperoPkg.Entries, prosperoPkgHeader.ContentId, passcode, absoluteSuperblockOffset, superblockDigest);
		ResealCnt(stream, prosperoPkg.Entries, prosperoPkgHeader);
		array = ReadRange(stream, 0L, 65536);
		WriteAuthentication(array, 4096, token.CntCertificates, rsa, ProsperoSha3.HashData(array.AsSpan(0, 4096)));
		WriteRange(stream, 0L, array);
	}

	private static void ReplaceAccessToken(Stream stream, IReadOnlyList<ProsperoPkgEntry> entries, byte[] accessToken)
	{
		ProsperoPkgEntry prosperoPkgEntry = FindEntry(entries, 32u);
		if (accessToken.Length != prosperoPkgEntry.DataSize)
		{
			throw new InvalidDataException($"FGC access token is 0x{accessToken.Length:X} bytes, but CNT IMAGE_KEY reserves 0x{prosperoPkgEntry.DataSize:X} bytes.");
		}
		WriteRange(stream, prosperoPkgEntry.DataOffset, accessToken);
	}

	private static void UpdateImageDigests(Stream stream, IReadOnlyList<ProsperoPkgEntry> entries, string contentId, string passcode, long absoluteSuperblockOffset, byte[] superblockDigest)
	{
		ProsperoPkgEntry prosperoPkgEntry = FindEntry(entries, 1034u);
		byte[] array = ReadRange(stream, prosperoPkgEntry.DataOffset, checked((int)prosperoPkgEntry.DataSize));
		byte[] array2 = (prosperoPkgEntry.Encrypted ? Entry.Decrypt(array, contentId, passcode, ToMeta(prosperoPkgEntry), publisherProfile: true) : array);
		if (absoluteSuperblockOffset % 65536 != 0L)
		{
			throw new InvalidDataException("FIH superblock offset is not 64-KiB aligned.");
		}
		checked
		{
			long num = (unchecked(absoluteSuperblockOffset / 65536) - 1) * 32;
			if (num < 0 || num > unchecked(array2.Length - 32))
			{
				throw new InvalidDataException("Superblock imagedigs slot is outside imagedigs.dat.");
			}
			byte[] array3 = superblockDigest.ToArray();
			Array.Reverse(array3);
			array3.CopyTo(array2, (int)num);
			if (!prosperoPkgEntry.Encrypted)
			{
				WriteRange(stream, prosperoPkgEntry.DataOffset, array2);
				return;
			}
		}
		GenericEntry genericEntry = new GenericEntry((EntryId)prosperoPkgEntry.RawId)
		{
			FileData = array2,
			meta = ToMeta(prosperoPkgEntry)
		};
		stream.Position = prosperoPkgEntry.DataOffset;
		genericEntry.WriteEncrypted(stream, contentId, passcode, publisherProfile: true);
	}

	private static void ResealCnt(FileStream stream, IReadOnlyList<ProsperoPkgEntry> entries, ProsperoPkgHeader header)
	{
		ProsperoPkgEntry prosperoPkgEntry = FindEntry(entries, 1u);
		byte[] array;
		checked
		{
			array = ReadRange(stream, prosperoPkgEntry.DataOffset, (int)prosperoPkgEntry.DataSize);
			if (array.Length < entries.Count * 32)
			{
				throw new InvalidDataException("CNT digest table is shorter than the entry table.");
			}
		}
		for (int i = 1; i < entries.Count; i++)
		{
			ProsperoPkgEntry prosperoPkgEntry2 = entries[i];
			Crypto.Sha3_256(stream, prosperoPkgEntry2.DataOffset, prosperoPkgEntry2.DataSize).CopyTo(array, i * 32);
		}
		WriteRange(stream, prosperoPkgEntry.DataOffset, array);
		byte[] array2 = HashConcatenatedEntries(stream, entries.Take(Math.Max(0, header.ScEntryCount - 1)), useMetaPrefix: false, header.ScEntryCount);
		byte[] array3 = HashConcatenatedEntries(stream, entries.Take(Math.Max(0, header.ScEntryCount - 2)), useMetaPrefix: true, header.ScEntryCount);
		byte[] array4 = ProsperoSha3.HashData(array);
		byte[] array5 = checked(Crypto.Sha3_256(stream, (long)header.BodyOffset, (long)header.BodySize));
		WriteRange(stream, 256L, array2);
		WriteRange(stream, 288L, array3);
		WriteRange(stream, 320L, array4);
		WriteRange(stream, 352L, array5);
		byte[] array6 = ReadRange(stream, 1296L, 16);
		uint num = BinaryPrimitives.ReadUInt32BigEndian(array6.AsSpan(0, 4));
		uint num2 = BinaryPrimitives.ReadUInt32BigEndian(array6.AsSpan(4, 4));
		uint num3 = BinaryPrimitives.ReadUInt32BigEndian(array6.AsSpan(8, 4));
		uint num4 = BinaryPrimitives.ReadUInt32BigEndian(array6.AsSpan(12, 4));
		if (num2 != 0 && num4 != 0)
		{
			byte[] array7 = new byte[64];
			Crypto.Sha3_256(stream, num, num2).CopyTo(array7, 0);
			Crypto.Sha3_256(stream, num3, num4).CopyTo(array7, 32);
			WriteRange(stream, 1312L, array7);
		}
		byte[] array8 = ReadRange(stream, 0L, 48);
		ulong num5 = BinaryPrimitives.ReadUInt64BigEndian(array8.AsSpan(32, 8));
		uint num6 = BinaryPrimitives.ReadUInt32BigEndian(array8.AsSpan(28, 4));
		if (num5 > (ulong)stream.Length || (ulong)num6 > (ulong)(stream.Length - (long)num5))
		{
			throw new InvalidDataException("CNT header rollup range is outside the subcontainer.");
		}
		byte[] array9 = Crypto.Sha3_256(stream, checked((long)num5), num6);
		WriteRange(stream, 256L, array9);
		byte[] array10 = ProsperoImageDigests.ComputePackageDigest(ReadRange(stream, 0L, 4064));
		WriteRange(stream, 4064L, array10);
	}

	private static byte[] HashConcatenatedEntries(Stream stream, IEnumerable<ProsperoPkgEntry> entries, bool useMetaPrefix, ushort scEntryCount)
	{
		using MemoryStream memoryStream = new MemoryStream();
		foreach (ProsperoPkgEntry entry in entries)
		{
			long size = ((useMetaPrefix && entry.RawId == 256) ? ((long)checked(scEntryCount * 32)) : ((long)entry.DataSize));
			CopyRange(stream, entry.DataOffset, size, memoryStream);
		}
		return ProsperoSha3.HashData(memoryStream.ToArray());
	}

	private static void WriteAuthentication(byte[] target, int offset, (byte[] First, byte[] Second) certificates, RSA rsa, byte[] digest)
	{
		if (certificates.First.Length != 896 || certificates.Second.Length != 896)
		{
			throw new InvalidDataException("Every FGC presigned certificate must be exactly 0x380 bytes.");
		}
		if (target.Length < offset + 2560)
		{
			throw new InvalidDataException("FGC authentication area is outside its 64-KiB target.");
		}
		byte[] array = rsa.SignHash(digest, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		if (array.Length != 384)
		{
			throw new CryptographicException("FGC partner key did not produce an RSA-3072 signature.");
		}
		int num = offset;
		certificates.First.CopyTo(target, num);
		num += 896;
		array.CopyTo(target, num);
		num += 384;
		certificates.Second.CopyTo(target, num);
		num += 896;
		array.CopyTo(target, num);
	}

	private static void ValidateInputFiles(ProsperoFlexibleContentFinalizationOptions options)
	{
		string[] array = new string[6] { options.FixedInfoHeaderPath, options.PfsMetadataPath, options.SubcontainerPath, options.ManifestPath, options.TokenPath, options.PartnerPrivateKeyPath };
		foreach (string text in array)
		{
			if (string.IsNullOrWhiteSpace(text) || !File.Exists(text))
			{
				throw new FileNotFoundException("Required FGC input file was not found.", text);
			}
		}
		if (options.Passcode.Length != 32 || options.Passcode.Any((char c) =>
		{
			bool flag = char.IsAsciiLetterOrDigit(c);
			if (!flag)
			{
				flag = ((c == '-' || c == '_') ? true : false);
			}
			return !flag;
		}))
		{
			throw new ArgumentException("FGC passcode must contain exactly 32 ASCII letters, digits, '-' or '_'.", "options");
		}
	}

	private static RSA LoadPartnerPrivateKey(string path)
	{
		RSA rSA = RSA.Create();
		try
		{
			rSA.ImportFromPem(File.ReadAllText(path));
			RSAParameters rSAParameters = rSA.ExportParameters(includePrivateParameters: true);
			if (rSA.KeySize == 3072)
			{
				byte[] exponent = rSAParameters.Exponent;
				if (exponent != null && exponent.Length == 3 && exponent[0] == 1 && exponent[1] == 0 && exponent[2] == 1 && rSAParameters.D != null)
				{
					return rSA;
				}
			}
			throw new InvalidDataException("FGC partner key must be a private RSA-3072 key with exponent 0x10001.");
		}
		catch
		{
			rSA.Dispose();
			throw;
		}
	}

	private static Manifest ReadManifest(string path)
	{
		using JsonDocument jsonDocument = JsonDocument.Parse(File.ReadAllBytes(path));
		JsonElement rootElement = jsonDocument.RootElement;
		string contentId = rootElement.GetProperty("contentId").GetString() ?? throw new InvalidDataException("manifest.json has no contentId.");
		foreach (JsonElement item in rootElement.GetProperty("pkgExtents").EnumerateArray())
		{
			if (item.GetProperty("type").GetString() == "pfsmeta")
			{
				return new Manifest(contentId, ReadJsonInteger(item.GetProperty("offsetInPkg")));
			}
		}
		throw new InvalidDataException("manifest.json has no pfsmeta extent.");
	}

	private static long ReadJsonInteger(JsonElement value)
	{
		if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var value2))
		{
			return value2;
		}
		if (value.ValueKind == JsonValueKind.String)
		{
			string text = value.GetString();
			if (!text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
			{
				return long.Parse(text, CultureInfo.InvariantCulture);
			}
			return Convert.ToInt64(text.Substring(2), 16);
		}
		throw new InvalidDataException("Expected a numeric manifest offset.");
	}

	private static string ReadCntContentId(Stream stream)
	{
		byte[] array = ReadRange(stream, 0L, 112);
		EnsureMagic(array, ProsperoPkgLayout.CntMagic, "CNT");
		ReadOnlySpan<byte> readOnlySpan = array.AsSpan(64, 48);
		int num = readOnlySpan.IndexOf((byte)0);
		return Encoding.ASCII.GetString((num < 0) ? readOnlySpan : readOnlySpan.Slice(0, num));
	}

	private static ProsperoPkgEntry FindEntry(IReadOnlyList<ProsperoPkgEntry> entries, uint rawId)
	{
		return entries.FirstOrDefault((ProsperoPkgEntry entry) => entry.RawId == rawId) ?? throw new InvalidDataException($"CNT entry 0x{rawId:X8} is missing.");
	}

	private static MetaEntry ToMeta(ProsperoPkgEntry entry)
	{
		return new MetaEntry
		{
			id = (EntryId)entry.RawId,
			NameTableOffset = entry.NameTableOffset,
			Flags1 = entry.Flags1,
			Flags2 = entry.Flags2,
			DataOffset = entry.DataOffset,
			DataSize = entry.DataSize
		};
	}

	private static FileStream OpenReadWrite(string path)
	{
		return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
	}

	private static byte[] ReadRange(Stream stream, long offset, int size)
	{
		byte[] array = new byte[size];
		stream.Position = offset;
		stream.ReadExactly(array);
		return array;
	}

	private static void WriteRange(Stream stream, long offset, ReadOnlySpan<byte> data)
	{
		stream.Position = offset;
		stream.Write(data);
	}

	private static void CopyRange(Stream source, long offset, long size, Stream destination)
	{
		byte[] array = new byte[1048576];
		source.Position = offset;
		while (size > 0)
		{
			int num = source.Read(array, 0, (int)Math.Min(array.Length, size));
			if (num == 0)
			{
				throw new EndOfStreamException("CNT entry ends before its declared size.");
			}
			destination.Write(array, 0, num);
			size -= num;
		}
	}

	private static void EnsureMagic(byte[] data, ReadOnlySpan<byte> magic, string name)
	{
		if (!((ReadOnlySpan<byte>)data.AsSpan(0, magic.Length)).SequenceEqual(magic))
		{
			throw new InvalidDataException(name + " magic is invalid.");
		}
	}

	private static byte[] DecryptCompactJwe(string compact, ReadOnlySpan<byte> key)
	{
		string[] array = compact.Split('.');
		if (array.Length != 5)
		{
			throw new InvalidDataException("FGC access token is not a five-part compact JWE.");
		}
		using JsonDocument jsonDocument = JsonDocument.Parse(Base64UrlDecode(array[0]));
		if (jsonDocument.RootElement.GetProperty("alg").GetString() != "dir")
		{
			throw new InvalidDataException("FGC token JWE must use direct key management.");
		}
		string text = jsonDocument.RootElement.GetProperty("enc").GetString();
		if (Base64UrlDecode(array[1]).Length != 0)
		{
			throw new InvalidDataException("Direct-key FGC JWE must have an empty encrypted-key part.");
		}
		byte[] array2 = Base64UrlDecode(array[2]);
		byte[] array3 = Base64UrlDecode(array[3]);
		byte[] array4 = Base64UrlDecode(array[4]);
		byte[] bytes = Encoding.ASCII.GetBytes(array[0]);
		if (text == "A128GCM")
		{
			if (key.Length != 16 || array4.Length != 16)
			{
				throw new InvalidDataException("A128GCM FGC token has invalid key/tag length.");
			}
			byte[] array5 = new byte[array3.Length];
			using AesGcm aesGcm = new AesGcm(key, array4.Length);
			aesGcm.Decrypt(array2, array3, array4, array5, bytes);
			return array5;
		}
		if (text == "A128CBC-HS256")
		{
			if (key.Length != 32 || array4.Length != 16)
			{
				throw new InvalidDataException("A128CBC-HS256 FGC token has invalid key/tag length.");
			}
			byte[] array6 = new byte[bytes.Length + array2.Length + array3.Length + 8];
			int num = 0;
			bytes.CopyTo(array6, num);
			num += bytes.Length;
			array2.CopyTo(array6, num);
			num += array2.Length;
			array3.CopyTo(array6, num);
			BinaryPrimitives.WriteUInt64BigEndian(array6.AsSpan(array6.Length - 8), checked((ulong)bytes.Length * 8));
			if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key.Slice(0, 16), array6).AsSpan(0, 16), array4))
			{
				throw new CryptographicException("FGC JWE authentication tag is invalid.");
			}
			using Aes aes = Aes.Create();
			aes.Key = key.Slice(16).ToArray();
			aes.IV = array2;
			aes.Mode = CipherMode.CBC;
			aes.Padding = PaddingMode.PKCS7;
			using ICryptoTransform cryptoTransform = aes.CreateDecryptor();
			return cryptoTransform.TransformFinalBlock(array3, 0, array3.Length);
		}
		throw new InvalidDataException("Unsupported FGC JWE encryption '" + text + "'.");
	}

	private static byte[] Base64UrlDecode(string value)
	{
		string text = value.Replace('-', '+').Replace('_', '/');
		text += new string('=', (4 - text.Length % 4) % 4);
		return Convert.FromBase64String(text);
	}

	private static string TrimHexPrefix(string value)
	{
		if (!value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
		{
			return value;
		}
		return value.Substring(2);
	}
}
