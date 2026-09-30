using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PKG;

public sealed class ProsperoDirectoryRetailFinalizationProvider : IProsperoRetailFinalizationProvider
{
	public const string FihRequestDigestFileName = "retail_fih_request.sha3";

	public const string FihFinalizationFileName = "retail_fih_finalization.bin";

	public const string CntRequestDigestFileName = "retail_cnt_request.sha3";

	public const string CntAuthenticationFileName = "retail_cnt_authentication.bin";

	private readonly string directory;

	public ProsperoDirectoryRetailFinalizationProvider(string directory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory, "directory");
		this.directory = Path.GetFullPath(directory);
	}

	public ProsperoRetailFinalizationResult FinalizeFih(ProsperoRetailFinalizationRequest request)
	{
		ArgumentNullException.ThrowIfNull(request, "request");
		ValidateFihRequest(request.FihHeader.Span);
		ValidateBinding(request.FihHeader.Span, "retail_fih_request.sha3", "FIH");
		return new ProsperoRetailFinalizationResult
		{
			FihFinalizationMaterial = ReadExact("retail_fih_finalization.bin", 768)
		};
	}

	public byte[] FinalizeCntHeader(ProsperoRetailCntFinalizationRequest request)
	{
		ArgumentNullException.ThrowIfNull(request, "request");
		if (request.CntHeader.Length != 4096)
		{
			throw new InvalidDataException($"The standard-Retail CNT request must contain exactly 0x1000 bytes, not 0x{request.CntHeader.Length:X}.");
		}
		ValidateBinding(request.CntHeader.Span, "retail_cnt_request.sha3", "CNT");
		return ReadExact("retail_cnt_authentication.bin", 384);
	}

	public static IReadOnlyList<string> ExportFromPackage(string packagePath, string outputDirectory, bool overwrite = false)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(packagePath, "packagePath");
		ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory, "outputDirectory");
		string fullPath = Path.GetFullPath(packagePath);
		string output = Path.GetFullPath(outputDirectory);
		using FileStream fileStream = File.OpenRead(fullPath);
		ProsperoFihHeader prosperoFihHeader = ProsperoPkgReader.Read(fileStream).Fih ?? throw new InvalidDataException("A finalized FIH package is required to export Retail artifacts.");
		if (!prosperoFihHeader.IsOfficial)
		{
			return Array.Empty<string>();
		}
		if (prosperoFihHeader.EmbeddedCntOffset > long.MaxValue)
		{
			throw new InvalidDataException("The embedded CNT offset exceeds the supported range.");
		}
		byte[] array = ReadRange(fileStream, 0L, 65536);
		byte[] array2 = array.AsSpan(61440, 768).ToArray();
		if (IsAllZero(array2))
		{
			throw new InvalidDataException("The official FIH has no standard-Retail finalization material.");
		}
		array.AsSpan(61440, 768).Clear();
		ValidateFihRequest(array);
		List<(string, byte[])> list;
		string[] array5;
		checked
		{
			long num = (long)prosperoFihHeader.EmbeddedCntOffset;
			byte[] array3 = ReadRange(fileStream, num, 4096);
			byte[] array4 = ReadRange(fileStream, num + 4096, 384);
			if (IsAllZero(array4))
			{
				throw new InvalidDataException("The official CNT has no standard-Retail authentication material.");
			}
			list = new List<(string, byte[])>
			{
				("retail_fih_request.sha3", ProsperoSha3.HashData(array)),
				("retail_fih_finalization.bin", array2),
				("retail_cnt_request.sha3", ProsperoSha3.HashData(array3)),
				("retail_cnt_authentication.bin", array4)
			};
			array5 = list.Select(((string Name, byte[] Data) file) => Path.Combine(output, file.Name)).ToArray();
			if (!overwrite)
			{
				string text = array5.FirstOrDefault(File.Exists);
				if (text != null)
				{
					throw new IOException("Refusing to overwrite an existing Retail artifact: " + text);
				}
			}
			Directory.CreateDirectory(output);
		}
		for (int num2 = 0; num2 < list.Count; num2++)
		{
			File.WriteAllBytes(array5[num2], list[num2].Item2);
		}
		return array5;
	}

	private void ValidateBinding(ReadOnlySpan<byte> request, string digestFileName, string displayName)
	{
		byte[] array = ReadExact(digestFileName, 32);
		byte[] array2 = ProsperoSha3.HashData(request);
		if (!CryptographicOperations.FixedTimeEquals(array, array2))
		{
			throw new InvalidDataException($"The {displayName} Retail artifact is bound to another request (expected SHA3-256 {Convert.ToHexString(array)}, actual {Convert.ToHexString(array2)}).");
		}
	}

	private byte[] ReadExact(string fileName, int expectedLength)
	{
		string text = Path.Combine(directory, fileName);
		if (!File.Exists(text))
		{
			throw new FileNotFoundException("Required standard-Retail artifact '" + fileName + "' was not found.", text);
		}
		byte[] array = File.ReadAllBytes(text);
		if (array.Length != expectedLength)
		{
			throw new InvalidDataException($"{fileName} must contain exactly 0x{expectedLength:X} bytes, not 0x{array.Length:X}.");
		}
		if (IsAllZero(array))
		{
			throw new InvalidDataException(fileName + " is all zero.");
		}
		return array;
	}

	private static void ValidateFihRequest(ReadOnlySpan<byte> fih)
	{
		if (fih.Length != 65536)
		{
			throw new InvalidDataException($"The standard-Retail FIH request must contain exactly 0x{65536:X} bytes.");
		}
		if (!fih.Slice(0, ProsperoPkgLayout.FihMagic.Length).SequenceEqual(ProsperoPkgLayout.FihMagic) || fih[5] != 128)
		{
			throw new InvalidDataException("The Retail finalization request is not an official FIH header.");
		}
		if (!IsAllZero(fih.Slice(61440, 768)))
		{
			throw new InvalidDataException("The FIH Retail finalization area must be zero in the signed request.");
		}
	}

	private static byte[] ReadRange(Stream input, long offset, int length)
	{
		if (offset < 0 || offset > input.Length - length)
		{
			throw new InvalidDataException($"Retail artifact range 0x{offset:X}+0x{length:X} is outside the package.");
		}
		byte[] array = new byte[length];
		input.Position = offset;
		input.ReadExactly(array);
		return array;
	}

	private static bool IsAllZero(ReadOnlySpan<byte> value)
	{
		byte b = 0;
		ReadOnlySpan<byte> readOnlySpan = value;
		for (int i = 0; i < readOnlySpan.Length; i++)
		{
			byte b2 = readOnlySpan[i];
			b |= b2;
		}
		return b == 0;
	}
}
