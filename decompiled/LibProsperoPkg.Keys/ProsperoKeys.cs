using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace LibProsperoPkg.Keys;

/// <summary>
/// Provides access to the embedded research/test key material used by the package pipeline.
/// <see cref="P:LibProsperoPkg.Keys.ProsperoKeys.IsAvailable" /> reports whether every embedded resource could be loaded.
/// </summary>
public static class ProsperoKeys
{
	private const string RsaPemResource = "LibProsperoPkg.Keys.Data.pkg_meta_rsa_key.pem";

	private const string PasscodeResource = "LibProsperoPkg.Keys.Data.passcode.bin";

	private const string MountImageResource = "LibProsperoPkg.Keys.Data.mount_image.bin";

	private const string TokenResource = "LibProsperoPkg.Keys.Data.token.hex";

	private static readonly Lazy<RSAParameters?> _metadataRsa = new Lazy<RSAParameters?>(LoadMetadataRsaParameters);

	private static readonly Lazy<byte[]?> _passcodeKey = new Lazy<byte[]>(() => TryLoadBytes("LibProsperoPkg.Keys.Data.passcode.bin"));

	private static readonly Lazy<byte[]?> _mountImageKey = new Lazy<byte[]>(() => TryLoadBytes("LibProsperoPkg.Keys.Data.mount_image.bin"));

	private static readonly Lazy<byte[]?> _tokenKey = new Lazy<byte[]>(() => TryLoadHex("LibProsperoPkg.Keys.Data.token.hex"));

	/// <summary>True when every embedded research/test resource was loaded successfully.</summary>
	public static bool IsAvailable
	{
		get
		{
			if (_metadataRsa.Value.HasValue)
			{
				return IsPublisherRsaProfileAvailable;
			}
			return false;
		}
	}

	/// <summary>
	/// True when all public RSA-3072 banks embedded in the matching <c>sc2.exe</c> profile are
	/// available: seven passcode moduli, the mount-image modulus and the token-verification modulus.
	/// </summary>
	public static bool IsPublisherRsaProfileAvailable
	{
		get
		{
			byte[] value = _passcodeKey.Value;
			if (value != null && value.Length == 2688)
			{
				value = _mountImageKey.Value;
				if (value != null && value.Length == 384)
				{
					value = _tokenKey.Value;
					if (value != null)
					{
						return value.Length == 384;
					}
					return false;
				}
			}
			return false;
		}
	}

	/// <summary>
	/// Seven concatenated RSA-3072 public moduli from the publishing profile's
	/// <c>&lt;passcode&gt;</c> bank.
	/// </summary>
	/// <exception cref="T:System.InvalidOperationException">The embedded key could not be loaded.</exception>
	public static ReadOnlySpan<byte> PasscodeKey => _passcodeKey.Value ?? throw new InvalidOperationException("The PS5 passcode key is unavailable.");

	/// <summary>
	/// RSA-3072 public modulus from <c>&lt;mount-image&gt;</c>; used to wrap the 32-byte
	/// PFS image key placed at the beginning of IMAGE_KEY.
	/// </summary>
	/// <exception cref="T:System.InvalidOperationException">The embedded key could not be loaded.</exception>
	public static ReadOnlySpan<byte> MountImageKey => _mountImageKey.Value ?? throw new InvalidOperationException("The PS5 mount-image key is unavailable.");

	/// <summary>
	/// RSA-3072 public modulus from <c>&lt;token&gt;</c>; used by <c>sc2.exe</c> to verify
	/// RS256 JWS/JWT authorization tokens. It is not used to build ordinary CNT/PPR-PFS images.
	/// </summary>
	public static ReadOnlySpan<byte> TokenKey => _tokenKey.Value ?? throw new InvalidOperationException("The PS5 token key is unavailable.");

	/// <summary>
	/// The PKG-metadata RSA-3072 private key. Returns a fresh
	/// <see cref="T:System.Security.Cryptography.RSA" /> instance the caller owns and must dispose.
	/// </summary>
	/// <exception cref="T:System.InvalidOperationException">The embedded key could not be loaded.</exception>
	public static RSA CreateMetadataRsa()
	{
		RSAParameters parameters = _metadataRsa.Value ?? throw new InvalidOperationException("The PS5 PKG-metadata RSA-3072 key is unavailable.");
		RSA rSA = RSA.Create();
		rSA.ImportParameters(parameters);
		return rSA;
	}

	/// <summary>Returns one RSA-3072 modulus from the seven-element passcode bank.</summary>
	public static ReadOnlySpan<byte> GetPasscodeModulus(int index)
	{
		if ((uint)index >= 7u)
		{
			throw new ArgumentOutOfRangeException("index");
		}
		return PasscodeKey.Slice(index * 384, 384);
	}

	private static RSAParameters? LoadMetadataRsaParameters()
	{
		try
		{
			string text = LoadText("LibProsperoPkg.Keys.Data.pkg_meta_rsa_key.pem");
			if (text == null)
			{
				return null;
			}
			using RSA rSA = RSA.Create();
			rSA.ImportFromPem(text);
			return rSA.ExportParameters(includePrivateParameters: true);
		}
		catch
		{
			return null;
		}
	}

	private static string? LoadText(string resourceName)
	{
		using Stream stream = OpenResource(resourceName);
		if (stream == null)
		{
			return null;
		}
		using StreamReader streamReader = new StreamReader(stream);
		return streamReader.ReadToEnd();
	}

	private static byte[]? TryLoadBytes(string resourceName)
	{
		try
		{
			using Stream stream = OpenResource(resourceName);
			if (stream == null)
			{
				return null;
			}
			using MemoryStream memoryStream = new MemoryStream();
			stream.CopyTo(memoryStream);
			return memoryStream.ToArray();
		}
		catch
		{
			return null;
		}
	}

	private static byte[]? TryLoadHex(string resourceName)
	{
		try
		{
			string text = LoadText(resourceName);
			return (text == null) ? null : Convert.FromHexString(text.Trim());
		}
		catch
		{
			return null;
		}
	}

	private static Stream? OpenResource(string resourceName)
	{
		return typeof(ProsperoKeys).GetTypeInfo().Assembly.GetManifestResourceStream(resourceName);
	}
}
