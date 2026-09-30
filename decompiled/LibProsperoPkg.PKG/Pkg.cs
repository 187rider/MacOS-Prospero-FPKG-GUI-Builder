using System;
using System.Collections.Generic;
using System.Linq;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PKG;

public class Pkg
{
	public Header Header;

	public byte[] HeaderDigest;

	public byte[] HeaderSignature;

	public KeysEntry EntryKeys;

	public GenericEntry ImageKey;

	public GeneralDigestsEntry GeneralDigests;

	public MetasEntry Metas;

	public GenericEntry Digests;

	public NameTableEntry EntryNames;

	public List<Entry> Entries;

	private const uint PKG_FLAG_FINALIZED = 2147483648u;

	private const ulong PKG_PFS_FLAG_NESTED_IMAGE = 9223372036854775808uL;

	public const int PKG_TABLE_ENTRY_SIZE = 32;

	public const int PKG_ENTRY_KEYSET_SIZE = 32;

	public const int HASH_SIZE = 32;

	public const string MAGIC = "\u007fCNT";

	private const int PKG_MAX_ENTRY_KEYS = 7;

	private const int PKG_CONTENT_ID_HASH_SIZE = 32;

	private const int PKG_ENTRY_KEYS_XHASHES_SIZE = 224;

	private const int PKG_PASSCODE_KEY_SIZE = 256;

	private const int PKG_IMAGE_KEY_SIZE = 256;

	private const int PKG_ENTRY_KEY_SIZE = 256;

	private const int PKG_PLAYGO_CHUNK_HASH_TABLE_OFFSET = 64;

	private const int PKG_PLAYGO_CHUNK_HASH_SIZE = 4;

	private const int PKG_PLAYGO_PFS_CHUNK_SIZE = 65536;

	private const int PKG_SHAREPARAM_FILE_VERSION_MAJOR = 1;

	private const int PKG_SHAREPARAM_FILE_VERSION_MINOR = 10;

	public const int PKG_CONTENT_ID_SIZE = 48;

	public const int PKG_HEADER_SIZE = 1440;

	public const int PKG_ENTRY_KEYSET_ENC_SIZE = 256;

	public byte[] GetEkpfs()
	{
		try
		{
			byte[] second = Crypto.RSA2048Decrypt(EntryKeys.Keys[3].key, RSAKeyset.PkgDerivedKey3Keyset);
			byte[] source = Crypto.Sha256(ImageKey.meta.GetBytes().Concat(second).ToArray());
			byte[] obj = ImageKey.FileData.Clone() as byte[];
			Crypto.AesCbcCfb128Decrypt(obj, obj, obj.Length, source.Skip(16).Take(16).ToArray(), source.Take(16).ToArray());
			return Crypto.RSA2048Decrypt(obj, RSAKeyset.FakeKeyset);
		}
		catch
		{
			return null;
		}
	}

	public bool CheckPasscode(string passcode)
	{
		if (passcode == null || passcode.Length != 32)
		{
			return false;
		}
		bool flag = EntryKeys.Keys.Length != 0 && EntryKeys.Keys[0].key.Length == 384;
		byte[] array = Crypto.ComputeKeys(Header.content_id, passcode, 0u, flag);
		return Enumerable.SequenceEqual((flag ? Crypto.Sha3_256(array) : Crypto.Sha256(array)).Xor(array), EntryKeys.Keys[0].digest);
	}

	public bool CheckDerivedKey(byte[] dk, int index)
	{
		if (index < 0 || index > 6)
		{
			throw new ArgumentException("Invalid derived key index: " + index);
		}
		if (dk == null || dk.Length != 32)
		{
			return false;
		}
		return Enumerable.SequenceEqual(((EntryKeys.Keys.Length != 0 && EntryKeys.Keys[0].key.Length == 384) ? Crypto.Sha3_256(dk) : Crypto.Sha256(dk)).Xor(dk), EntryKeys.Keys[index].digest);
	}

	public bool CheckEkpfs(byte[] dk1)
	{
		return CheckDerivedKey(dk1, 1);
	}
}
