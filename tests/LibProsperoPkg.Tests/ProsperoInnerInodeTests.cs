using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using LibProsperoPkg.PFS;
using Xunit;

namespace LibProsperoPkg.Tests;

public class ProsperoInnerInodeTests
{
    private readonly ProsperoPs5InnerMetadata _serializer = new(1700000000L, 123456u);

    [Theory]
    [InlineData(0x00000000FFFFFFFFUL)]
    [InlineData(0x0000000100000000UL)]
    [InlineData(0x00000004EC400000UL)]
    [InlineData(0x0000000FFFFFFFFFUL)]
    public void SerializeInode_Preserves64BitDataOffset(ulong expectedOffset)
    {
        byte[] dst = new byte[ProsperoPs5InnerMetadata.InodeSize];
        var node = new ProsperoPs5MetaNode
        {
            Name = "test_file.dat",
            Inode = 1,
            IsDirectory = false,
            Mode = 33133,
            Nlink = 1,
            Flags = 0x30,
            Size = 1048576L,
            LogicalOffset = expectedOffset,
            ParentInode = 0,
            DirentOffset = 24
        };

        _serializer.WriteInode(dst, node);

        ulong actualOffset = BinaryPrimitives.ReadUInt64LittleEndian(dst.AsSpan(96, 8));
        Assert.Equal(expectedOffset, actualOffset);

        // Verify trailing fields at 104, 108, 112 are not displaced
        int actualAfid = BinaryPrimitives.ReadInt32LittleEndian(dst.AsSpan(104, 4));
        int actualParent = BinaryPrimitives.ReadInt32LittleEndian(dst.AsSpan(108, 4));
        int actualDirentOffset = BinaryPrimitives.ReadInt32LittleEndian(dst.AsSpan(112, 4));

        Assert.Equal(0, actualAfid); // Afid is 0 for node
        Assert.Equal(0, actualParent);
        Assert.Equal(24, actualDirentOffset);

        // Verify reserved tail (bytes 116..167) is entirely zeroed
        for (int i = 116; i < 168; i++)
        {
            Assert.Equal(0, dst[i]);
        }
    }

    [Fact]
    public void Build_RoundTrip_ValidatesAllOffsetsAndBoundaries()
    {
        // 25 GB inner image: 400,000 blocks of 64 KiB
        long ndblock = 400000L;
        ulong largeOffset = 0x00000004EC400000UL; // ~20.5 GB

        var nodes = new List<ProsperoPs5MetaNode>
        {
            new ProsperoPs5MetaNode
            {
                Name = "",
                Inode = 0,
                IsDirectory = true,
                Mode = 16749,
                Nlink = 1,
                Flags = 131088u,
                Size = 65536L,
                LogicalOffset = largeOffset,
                ParentInode = -1,
                DirentOffset = -1
            },
            new ProsperoPs5MetaNode
            {
                Name = "large_asset.pak",
                Inode = 1,
                IsDirectory = false,
                Mode = 33133,
                Nlink = 1,
                Flags = 0x30,
                Size = 1073741824L, // 1 GB
                LogicalOffset = 0x0000000100000000UL, // 4 GB
                ParentInode = 0,
                DirentOffset = 24
            }
        };

        var payloads = new List<byte[]>
        {
            new byte[128] // Root dirent payload
        };

        byte[] metadataPlaintext = _serializer.Build(nodes, ndblock, payloads);

        Assert.NotNull(metadataPlaintext);
        Assert.True(metadataPlaintext.Length >= 65536 * 2);

        // Read back Inode 0 from block 1 (offset 65536)
        ulong readBackOffset0 = BinaryPrimitives.ReadUInt64LittleEndian(metadataPlaintext.AsSpan(65536 + 96, 8));
        Assert.Equal(largeOffset, readBackOffset0);

        // Read back Inode 1 from block 1
        ulong readBackOffset1 = BinaryPrimitives.ReadUInt64LittleEndian(metadataPlaintext.AsSpan(65536 + 168 + 96, 8));
        Assert.Equal(0x0000000100000000UL, readBackOffset1);
    }

    [Fact]
    public void Build_ThrowsWhenInodeExtentExceedsImageBoundary()
    {
        // Small image: only 10 blocks (655,360 bytes)
        long ndblock = 10L;

        var nodes = new List<ProsperoPs5MetaNode>
        {
            new ProsperoPs5MetaNode
            {
                Name = "overflow.bin",
                Inode = 0,
                IsDirectory = false,
                Mode = 33133,
                Nlink = 1,
                Flags = 0x30,
                Size = 1000000L, // 1 MB > 10 * 64K = 640K
                LogicalOffset = 0x0000000000010000UL,
                ParentInode = -1,
                DirentOffset = -1
            }
        };

        var payloads = new List<byte[]> { new byte[64] };

        Assert.Throws<InvalidDataException>(() => _serializer.Build(nodes, ndblock, payloads));
    }
}
