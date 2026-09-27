using System;
using System.IO;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PFS;

/// <summary>
/// This wraps a Memory mapped file view of a PFSC file so that you can access it
/// as though it were uncompressed.
/// </summary>
public class PFSCReader : IMemoryReader, IDisposable
{
	[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 48)]
	private struct PFSCHdr
	{
		public int Magic;

		public int Unk4;

		public int Unk8;

		public int BlockSz;

		public long BlockSz2;

		public long BlockOffsets;

		public ulong DataStart;

		public long DataLength;
	}

	public const int Magic = 1129530960;

	private IMemoryAccessor _accessor;

	private PFSCHdr hdr;

	private long[] sectorMap;

	public int SectorSize => hdr.BlockSz;

	/// <summary>The logical (decompressed) length in bytes represented by this PFSC image.</summary>
	public long DataLength => hdr.DataLength;

	/// <summary>
	/// Creates a PFSCReader
	/// </summary>
	/// <param name="va">An IMemoryAccessor containing the PFSC file</param>
	/// <exception cref="T:System.ArgumentException">Thrown when the accessor is not a view of a PFSC file.</exception>
	public PFSCReader(IMemoryAccessor va)
	{
		_accessor = va;
		_accessor.Read<PFSCHdr>(0L, out hdr);
		if (hdr.Magic != 1129530960)
		{
			throw new ArgumentException("Not a PFSC file: missing PFSC magic");
		}
		if (hdr.Unk4 != 0)
		{
			throw new ArgumentException($"Not a PFSC file: unknown data at 0x4 (expected 0, got {hdr.Unk4})");
		}
		if (hdr.BlockSz != (int)hdr.BlockSz2)
		{
			throw new ArgumentException("Not a PFSC file: block size mismatch");
		}
		int num = (int)(hdr.DataLength / hdr.BlockSz2);
		sectorMap = new long[num + 1];
		_accessor.ReadArray(hdr.BlockOffsets, sectorMap, 0, num + 1);
	}

	/// <summary>
	/// Creates a PFSCReader
	/// </summary>
	/// <param name="va">A ViewAccessor containing the PFSC file</param>
	/// <exception cref="T:System.ArgumentException">Thrown when the accessor is not a view of a PFSC file.</exception>
	public PFSCReader(MemoryMappedViewAccessor va)
		: this(new MemoryMappedViewAccessor_(va))
	{
	}

	public PFSCReader(IMemoryReader r)
		: this(new MemoryAccessor(r, 0L))
	{
	}

	/// <summary>
	/// Reads the sector at the given index into the given byte array.
	/// </summary>
	/// <param name="idx">sector index (multiply by SectorSize to get the byte offset)</param>
	/// <param name="output">byte array where sector will be written</param>
	public void ReadSector(int idx, byte[] output)
	{
		if (idx < 0 || idx > sectorMap.Length - 1)
		{
			throw new ArgumentException("Invalid index", "idx");
		}
		long num = sectorMap[idx];
		long num2 = sectorMap[idx + 1] - num;
		if (num2 == hdr.BlockSz2)
		{
			_accessor.Read(num, output, 0, hdr.BlockSz);
			return;
		}
		if (num2 > hdr.BlockSz2)
		{
			Array.Clear(output, 0, hdr.BlockSz);
			return;
		}
		byte[] array = new byte[(int)num2 - 2];
		_accessor.Read(num + 2, array, 0, (int)num2 - 2);
		using MemoryStream stream = new MemoryStream(array);
		using DeflateStream deflateStream = new DeflateStream(stream, CompressionMode.Decompress);
		deflateStream.ReadExactly(output, 0, hdr.BlockSz);
	}

	private void Read(long src, long count, Action<byte[], int, int> Write)
	{
		if (src + count > hdr.DataLength)
		{
			throw new ArgumentException("Attempt to read beyond end of file");
		}
		if (count <= 0)
		{
			return;
		}
		int blockSz = hdr.BlockSz;
		byte[] array = new byte[blockSz];
		int num = (int)(src / blockSz);
		int num2 = (int)(src - blockSz * num);
		ReadSector(num, array);
		while (count > 0 && src < hdr.DataLength)
		{
			if (num2 >= blockSz)
			{
				num++;
				ReadSector(num, array);
				num2 = 0;
			}
			int num3 = (int)Math.Min(blockSz - num2, count);
			Write(array, num2, num3);
			count -= num3;
			num2 += num3;
			src += num3;
		}
	}

	/// <summary>
	/// Read `count` bytes at location `src` into the writeable Stream `dest`
	/// </summary>
	/// <param name="src">Byte offset into PFSC</param>
	/// <param name="count">Number of bytes to read</param>
	/// <param name="dest">Output stream</param>
	public void Read(long src, long count, Stream dest)
	{
		Read(src, count, dest.Write);
	}

	/// <summary>
	/// Read `count` bytes at location `src` into the byte array at offset `offset`
	/// </summary>
	/// <param name="src">Byte offset into PFSC</param>
	/// <param name="buffer">Output byte array</param>
	/// <param name="offset">Offset into byte array</param>
	/// <param name="count">Number of bytes to read</param>
	public void Read(long src, byte[] buffer, int offset, int count)
	{
		Read(src, count, (byte[] sectorBuffer, int offsetIntoSector, int bufferedRead) =>
		{
			Buffer.BlockCopy(sectorBuffer, offsetIntoSector, buffer, offset, bufferedRead);
			offset += bufferedRead;
		});
	}

	public void Dispose()
	{
		_accessor.Dispose();
	}
}
