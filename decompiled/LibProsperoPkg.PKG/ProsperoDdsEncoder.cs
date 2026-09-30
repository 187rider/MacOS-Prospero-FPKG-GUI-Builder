using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using ImageMagick;

namespace LibProsperoPkg.PKG;

public static class ProsperoDdsEncoder
{
	public const int HeaderSize = 148;

	private const uint DxgiFormatBc7Unorm = 98u;

	private static readonly int[] Weights4 = new int[16]
	{
		0, 4, 9, 13, 17, 21, 26, 30, 34, 38,
		43, 47, 51, 55, 60, 64
	};

	public static string? PublisherP2dPath { get; set; }

	public static byte[] EncodePngToDds(byte[] pngBytes)
	{
		if (pngBytes == null || pngBytes.Length == 0)
		{
			throw new ArgumentException("Empty image.", "pngBytes");
		}
		string text = FindPublisherP2d();
		if (text != null)
		{
			return EncodePngWithPublisherP2d(pngBytes, text);
		}
		using MagickImage magickImage = new MagickImage(pngBytes);
		int width = (int)magickImage.Width;
		int height = (int)magickImage.Height;
		if (width <= 0 || height <= 0)
		{
			throw new InvalidDataException("Image has no pixels.");
		}
		using IPixelCollection<byte> pixelCollection = magickImage.GetPixels();
		return EncodeRgbaToDds(pixelCollection.ToByteArray(PixelMapping.RGBA) ?? throw new InvalidDataException("Unable to read RGBA pixels."), width, height);
	}

	private static string? FindPublisherP2d()
	{
		List<string> list = new List<string>(6)
		{
			PublisherP2dPath,
			Environment.GetEnvironmentVariable("LIBPROSPERO_P2D_PATH"),
			Path.Combine(AppContext.BaseDirectory, "p2d.exe"),
			Path.Combine(AppContext.BaseDirectory, "ext", "p2d.exe")
		};
		if (OperatingSystem.IsWindows())
		{
			list.Add("C:\\SCE\\Prospero\\Tools\\Publishing Tools\\bin\\ext\\p2d.exe");
		}
		foreach (string item in list)
		{
			if (!string.IsNullOrWhiteSpace(item) && File.Exists(item))
			{
				return Path.GetFullPath(item);
			}
		}
		return null;
	}

	private static byte[] EncodePngWithPublisherP2d(byte[] pngBytes, string p2dPath)
	{
		string text = Path.Combine(Path.GetTempPath(), "libprospero-p2d-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(text);
		string text2 = Path.Combine(text, "input.png");
		string text3 = Path.Combine(text, "output.dds");
		try
		{
			File.WriteAllBytes(text2, pngBytes);
			using Process process = Process.Start(new ProcessStartInfo
			{
				FileName = p2dPath,
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				ArgumentList = { "--high", "--st", text2, text3 }
			}) ?? throw new InvalidOperationException("Unable to start publisher DDS encoder: " + p2dPath);
			string text4 = process.StandardOutput.ReadToEnd();
			string text5 = process.StandardError.ReadToEnd();
			process.WaitForExit();
			if (!File.Exists(text3))
			{
				string text6 = $"Publisher DDS encoder failed with exit code {process.ExitCode}: ";
				_003C_003Ey__InlineArray2<string> buffer = default;
				buffer[0] = text4.Trim();
				buffer[1] = text5.Trim();
				throw new InvalidDataException(text6 + string.Join(" ", (ReadOnlySpan<string?>)buffer).Trim());
			}
			byte[] array = File.ReadAllBytes(text3);
			if (array.Length < 148 || array[0] != 68 || array[1] != 68 || array[2] != 83 || array[3] != 32 || BitConverter.ToUInt32(array, 128) != 98)
			{
				throw new InvalidDataException("Publisher DDS encoder returned a non-BC7 DDS file.");
			}
			return array;
		}
		finally
		{
			if (Directory.Exists(text))
			{
				Directory.Delete(text, recursive: true);
			}
		}
	}

	public static byte[] EncodeRgbaToDds(byte[] rgba, int width, int height)
	{
		if (width <= 0 || height <= 0)
		{
			throw new ArgumentOutOfRangeException("width", "Invalid image dimensions.");
		}
		if (rgba.Length < width * height * 4)
		{
			throw new ArgumentException("RGBA buffer is smaller than width*height*4.", "rgba");
		}
		int num = (width + 3) / 4;
		int num2 = (height + 3) / 4;
		int num3 = checked(num * num2 * 16);
		byte[] array = new byte[148 + num3];
		WriteHeader(array, width, height, (uint)num3);
		BcEncoder bcEncoder = new BcEncoder(CompressionFormat.Bc7);
		bcEncoder.OutputOptions.GenerateMipMaps = false;
		bcEncoder.OutputOptions.Quality = CompressionQuality.BestQuality;
		bcEncoder.Options.IsParallel = false;
		byte[][] array2 = bcEncoder.EncodeToRawBytes(rgba, width, height, PixelFormat.Rgba32);
		if (array2.Length != 1 || array2[0].Length != num3)
		{
			throw new InvalidDataException($"BC7 encoder returned {array2.Length} levels and {((array2.Length != 0) ? array2[0].Length : 0)} bytes; expected one {num3}-byte surface.");
		}
		array2[0].CopyTo(array, 148);
		return array;
	}

	private static void WriteHeader(byte[] dst, int width, int height, uint linearSize)
	{
		dst[0] = 68;
		dst[1] = 68;
		dst[2] = 83;
		dst[3] = 32;
		WriteU32(dst, 4, 124u);
		WriteU32(dst, 8, 659463u);
		WriteU32(dst, 12, (uint)height);
		WriteU32(dst, 16, (uint)width);
		WriteU32(dst, 20, linearSize);
		WriteU32(dst, 24, 0u);
		WriteU32(dst, 28, 1u);
		WriteU32(dst, 76, 32u);
		WriteU32(dst, 80, 4u);
		dst[84] = 68;
		dst[85] = 88;
		dst[86] = 49;
		dst[87] = 48;
		WriteU32(dst, 108, 4096u);
		WriteU32(dst, 128, 98u);
		WriteU32(dst, 132, 3u);
		WriteU32(dst, 136, 0u);
		WriteU32(dst, 140, 1u);
		WriteU32(dst, 144, 0u);
	}

	private static void WriteU32(byte[] dst, int offset, uint value)
	{
		dst[offset] = (byte)value;
		dst[offset + 1] = (byte)(value >> 8);
		dst[offset + 2] = (byte)(value >> 16);
		dst[offset + 3] = (byte)(value >> 24);
	}

	private static void GatherBlock(byte[] rgba, int width, int height, int x0, int y0, byte[] block)
	{
		for (int i = 0; i < 4; i++)
		{
			int num = Math.Min(y0 + i, height - 1);
			for (int j = 0; j < 4; j++)
			{
				int num2 = Math.Min(x0 + j, width - 1);
				int num3 = (num * width + num2) * 4;
				int num4 = (i * 4 + j) * 4;
				block[num4] = rgba[num3];
				block[num4 + 1] = rgba[num3 + 1];
				block[num4 + 2] = rgba[num3 + 2];
				block[num4 + 3] = rgba[num3 + 3];
			}
		}
	}

	private static void EncodeBlockMode6(byte[] block, byte[] dst, int offset)
	{
		int[] array = new int[4] { 255, 255, 255, 255 };
		int[] array2 = new int[4];
		for (int i = 0; i < 16; i++)
		{
			int num = i * 4;
			for (int j = 0; j < 4; j++)
			{
				int num2 = block[num + j];
				if (num2 < array[j])
				{
					array[j] = num2;
				}
				if (num2 > array2[j])
				{
					array2[j] = num2;
				}
			}
		}
		QuantizeEndpoint(array, out var best, out var bestP, out var recon);
		QuantizeEndpoint(array2, out var best2, out var bestP2, out var recon2);
		int[] array3 = new int[16];
		for (int k = 0; k < 16; k++)
		{
			array3[k] = BestIndex(recon, recon2, block, k * 4);
		}
		if (array3[0] >= 8)
		{
			int[] array4 = best2;
			best2 = best;
			best = array4;
			int num3 = bestP2;
			bestP2 = bestP;
			bestP = num3;
			for (int l = 0; l < 16; l++)
			{
				array3[l] = 15 - array3[l];
			}
		}
		ulong lo = 0uL;
		ulong hi = 0uL;
		int pos = 0;
		Put(ref lo, ref hi, ref pos, 64u, 7);
		Put(ref lo, ref hi, ref pos, (uint)best[0], 7);
		Put(ref lo, ref hi, ref pos, (uint)best2[0], 7);
		Put(ref lo, ref hi, ref pos, (uint)best[1], 7);
		Put(ref lo, ref hi, ref pos, (uint)best2[1], 7);
		Put(ref lo, ref hi, ref pos, (uint)best[2], 7);
		Put(ref lo, ref hi, ref pos, (uint)best2[2], 7);
		Put(ref lo, ref hi, ref pos, (uint)best[3], 7);
		Put(ref lo, ref hi, ref pos, (uint)best2[3], 7);
		Put(ref lo, ref hi, ref pos, (uint)bestP, 1);
		Put(ref lo, ref hi, ref pos, (uint)bestP2, 1);
		Put(ref lo, ref hi, ref pos, (uint)array3[0], 3);
		for (int m = 1; m < 16; m++)
		{
			Put(ref lo, ref hi, ref pos, (uint)array3[m], 4);
		}
		for (int n = 0; n < 8; n++)
		{
			dst[offset + n] = (byte)(lo >> n * 8);
		}
		for (int num4 = 0; num4 < 8; num4++)
		{
			dst[offset + 8 + num4] = (byte)(hi >> num4 * 8);
		}
	}

	private static void QuantizeEndpoint(int[] v, out int[] best, out int bestP, out int[] recon)
	{
		best = new int[4];
		recon = new int[4];
		bestP = 0;
		long num = long.MaxValue;
		for (int i = 0; i <= 1; i++)
		{
			long num2 = 0L;
			int[] array = new int[4];
			int[] array2 = new int[4];
			for (int j = 0; j < 4; j++)
			{
				int num3 = v[j] - i + 1 >> 1;
				if (num3 < 0)
				{
					num3 = 0;
				}
				else if (num3 > 127)
				{
					num3 = 127;
				}
				int num4 = (num3 << 1) | i;
				int num5 = num4 - v[j];
				num2 += (long)num5 * (long)num5;
				array[j] = num3;
				array2[j] = num4;
			}
			if (num2 < num)
			{
				num = num2;
				bestP = i;
				best = array;
				recon = array2;
			}
		}
	}

	private static int BestIndex(int[] e0, int[] e1, byte[] block, int po)
	{
		int result = 0;
		long num = long.MaxValue;
		for (int i = 0; i < 16; i++)
		{
			int num2 = Weights4[i];
			long num3 = 0L;
			for (int j = 0; j < 4; j++)
			{
				int num4 = (e0[j] * (64 - num2) + e1[j] * num2 + 32 >> 6) - block[po + j];
				num3 += (long)num4 * (long)num4;
			}
			if (num3 < num)
			{
				num = num3;
				result = i;
			}
		}
		return result;
	}

	private static void Put(ref ulong lo, ref ulong hi, ref int pos, uint value, int count)
	{
		for (int i = 0; i < count; i++)
		{
			if (((value >> i) & 1) != 0)
			{
				int num = pos + i;
				if (num < 64)
				{
					lo |= (ulong)(1L << num);
				}
				else
				{
					hi |= (ulong)(1L << num - 64);
				}
			}
		}
		pos += count;
	}
}
