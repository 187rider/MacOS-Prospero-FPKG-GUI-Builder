using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace LibProsperoPkg.PFS;

/// <summary>
/// Builds the PS5 nwonly inner-image metadata region as block-aligned plaintext. This plaintext is
/// Kraken-compressed into the inner image's metadata block.
/// </summary>
public sealed class ProsperoPs5InnerMetadata
{
	/// <summary>The inner-image block size (64 KiB).</summary>
	public const int BlockSize = 65536;

	public const int InodeSize = 168;

	public const int InodesPerBlock = 390;

	private readonly long _timeSec;

	private readonly uint _timeNsec;

	/// <param name="buildTimeSec">Build timestamp seconds (the package c_date/c_time — a deterministic build input).</param>
	/// <param name="buildTimeNsec">Build timestamp nanoseconds fraction.</param>
	public ProsperoPs5InnerMetadata(long buildTimeSec, uint buildTimeNsec)
	{
		_timeSec = buildTimeSec;
		_timeNsec = buildTimeNsec;
	}

	/// <summary>Writes one 0xA8 unsigned inode for <paramref name="n" /> to <paramref name="dst" />.</summary>
	public void WriteInode(Span<byte> dst, ProsperoPs5MetaNode n)
	{
		dst.Clear();
		BinaryPrimitives.WriteUInt16LittleEndian(dst, n.Mode);
		BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(2), n.Nlink);
		BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(4), n.Flags);
		BinaryPrimitives.WriteInt64LittleEndian(dst.Slice(8), n.Size);
		BinaryPrimitives.WriteInt64LittleEndian(dst.Slice(16), n.Size);
		for (int i = 0; i < 4; i++)
		{
			BinaryPrimitives.WriteInt64LittleEndian(dst.Slice(24 + i * 8), _timeSec);
		}
		for (int j = 0; j < 4; j++)
		{
			BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(56 + j * 4), _timeNsec);
		}
		BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(96), n.LogicalOffset);
		int value;
		if (n.ParentInode < 0)
		{
			value = -1;
		}
		else
		{
			value = (n.IsDirectory ? (-1) : ((int)n.Afid));
		}
		BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(104), value);
		BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(108), n.ParentInode);
		BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(112), n.DirentOffset);
	}

	/// <summary>Builds the superblock block (block 0).</summary>
	private byte[] BuildSuperblock(int inodeCount, int inodeBlockCount, long ndblock, long metadataStartBlock)
	{
		byte[] array = new byte[873];
		BinaryPrimitives.WriteInt64LittleEndian(array, 2L);
		BinaryPrimitives.WriteInt64LittleEndian(array.AsSpan(8), 20130315L);
		array[26] = 1;
		BinaryPrimitives.WriteUInt16LittleEndian(array.AsSpan(28), 24);
		BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(32), 65536u);
		BinaryPrimitives.WriteInt64LittleEndian(array.AsSpan(40), 1L);
		BinaryPrimitives.WriteInt64LittleEndian(array.AsSpan(48), inodeCount);
		BinaryPrimitives.WriteInt64LittleEndian(array.AsSpan(56), ndblock);
		BinaryPrimitives.WriteInt64LittleEndian(array.AsSpan(64), inodeBlockCount);
		BinaryPrimitives.WriteUInt16LittleEndian(array.AsSpan(80), 0);
		BinaryPrimitives.WriteUInt16LittleEndian(array.AsSpan(82), 1);
		BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(84), 16u);
		checked
		{
			long value = unchecked((long)inodeBlockCount) * 65536L;
			BinaryPrimitives.WriteInt64LittleEndian(array.AsSpan(88), value);
			BinaryPrimitives.WriteInt64LittleEndian(array.AsSpan(96), value);
		}
		for (int i = 0; i < 4; i++)
		{
			BinaryPrimitives.WriteInt64LittleEndian(array.AsSpan(104 + i * 8), _timeSec);
		}
		for (int j = 0; j < 4; j++)
		{
			BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(136 + j * 4), _timeNsec);
		}
		BinaryPrimitives.WriteInt64LittleEndian(array.AsSpan(176), inodeBlockCount);
		BinaryPrimitives.WriteInt64LittleEndian(array.AsSpan(216), checked(metadataStartBlock + 1));
		array[872] = 1;
		return array;
	}

	private static byte[] Dirents(IEnumerable<PfsDirent> dirents)
	{
		using MemoryStream memoryStream = new MemoryStream();
		foreach (PfsDirent dirent in dirents)
		{
			dirent.WriteToStream(memoryStream);
		}
		return memoryStream.ToArray();
	}

	/// <summary>
	/// Assembles the full metadata plaintext. <paramref name="blocks" /> is the ordered list of metadata
	/// blocks to emit after the superblock (block 0) and inode table (block 1): each is either a dirent list,
	/// a flat-path table, or the afid table, at its own 64 KiB block. Returns a buffer whose length is a
	/// multiple of <see cref="F:LibProsperoPkg.PFS.ProsperoPs5InnerMetadata.BlockSize" />.
	/// </summary>
	public byte[] Build(IReadOnlyList<ProsperoPs5MetaNode> inodes, long ndblock, IReadOnlyList<byte[]> blocks)
	{
		int num = checked(inodes.Count + 390 - 1) / 390;
		int num2 = 0;
		byte[] array;
		checked
		{
			foreach (byte[] block in blocks)
			{
				num2 += AllocatedBlocks(block.Length);
			}
			int num3 = 1 + num + num2;
			array = new byte[unchecked(num3 * 65536)];
			long num4 = ndblock - num3;
			if (num4 < 0)
			{
				throw new ArgumentException("Metadata region exceeds the declared inner mount.", "ndblock");
			}
			BuildSuperblock(inodes.Count, num, ndblock, num4).CopyTo(array, 0);
		}
		long innerPfsImageLength = checked(ndblock * 65536L);
		for (int i = 0; i < inodes.Count; i++)
		{
			int num5 = i / 390;
			int num6 = i % 390;
			int start = checked((1 + num5) * 65536 + num6 * 168);
			WriteInode(array.AsSpan(start, 168), inodes[i]);

			ulong actualOffset = BinaryPrimitives.ReadUInt64LittleEndian(array.AsSpan(start + 96, 8));
			if (actualOffset != inodes[i].LogicalOffset)
			{
				throw new InvalidDataException($"Inode #{i} ({inodes[i].Name}) DataOffset mismatch: expected 0x{inodes[i].LogicalOffset:X}, but serialized 0x{actualOffset:X}");
			}
			if (inodes[i].Size > 0 && checked((long)actualOffset + inodes[i].Size) > innerPfsImageLength)
			{
				throw new InvalidDataException($"Inode #{i} ({inodes[i].Name}) extent exceeds inner PFS image length: 0x{actualOffset:X} + {inodes[i].Size} > 0x{innerPfsImageLength:X}");
			}
		}
		int num7 = checked((1 + num) * 65536);
		for (int j = 0; j < blocks.Count; j++)
		{
			blocks[j].CopyTo(array, num7);
			num7 = checked(num7 + AllocatedBlocks(blocks[j].Length) * 65536);
		}
		return array;
	}

	private static int AllocatedBlocks(int length)
	{
		return Math.Max(1, checked(length + 65536 - 1) / 65536);
	}
}
