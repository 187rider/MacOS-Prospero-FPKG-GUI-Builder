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

    [Fact]
    public void ProsperoFself_UnfselfAndMakeFself_AllDumpBinaries_Succeeds()
    {
        string baseDir = Environment.GetEnvironmentVariable("PS5_TEST_DUMP_DIR") ?? "";
        if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir)) return;

        foreach (string file in Directory.EnumerateFiles(baseDir, "*", SearchOption.AllDirectories))
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext != ".bin" && ext != ".prx" && ext != ".sprx" && ext != ".elf") continue;

            byte[] selfBytes = File.ReadAllBytes(file);
            if (selfBytes.Length < 64) continue;
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(selfBytes);
            byte[] elf;
            if (magic == 0xEEF51454 || magic == 0x1D3D154F)
            {
                bool ok = LibProsperoPkg.Content.ProsperoFself.TryUnfself(selfBytes, out byte[]? extractedElf);
                Assert.True(ok, $"Failed to unfself {Path.GetFileName(file)}");
                Assert.NotNull(extractedElf);
                elf = extractedElf;
            }
            else if (LibProsperoPkg.Content.ProsperoFself.IsElf(selfBytes))
            {
                elf = selfBytes;
            }
            else
            {
                continue;
            }

            if (elf.Length >= 64)
            {
                ulong e_shoff = BinaryPrimitives.ReadUInt64LittleEndian(elf.AsSpan(40, 8));
                ushort e_shentsize = BinaryPrimitives.ReadUInt16LittleEndian(elf.AsSpan(58, 2));
                ushort e_shnum = BinaryPrimitives.ReadUInt16LittleEndian(elf.AsSpan(60, 2));
                long tableEnd = (long)e_shoff + ((long)e_shnum * (long)e_shentsize);
                if (tableEnd > elf.Length || (long)e_shoff >= elf.Length)
                {
                    elf.AsSpan(40, 8).Clear();
                    elf.AsSpan(58, 6).Clear();
                }
            }

            byte[] fself = LibProsperoPkg.Content.ProsperoFself.MakeFself(elf);
            Assert.NotNull(fself);
            Assert.True(LibProsperoPkg.Content.ProsperoFself.IsSelf(fself), $"Output not SELF for {Path.GetFileName(file)}");
        }
    }

    [Fact]
    public void RecursiveMakeFself_ProcessesAndSanitizesPS5Self()
    {
        string sample = Environment.GetEnvironmentVariable("PS5_TEST_SAMPLE_SPRX") ?? "";
        if (string.IsNullOrEmpty(sample) || !File.Exists(sample)) return;

        string tempDir = Path.Combine(Path.GetTempPath(), "fself_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string dest = Path.Combine(tempDir, "right.sprx");
            File.Copy(sample, dest);

            var logs = new List<string>();
            int converted = LibProsperoPkg.Content.ProsperoFself.RecursiveMakeFself(tempDir, logs.Add);

            Assert.Equal(1, converted);
            byte[] processed = File.ReadAllBytes(dest);
            Assert.True(LibProsperoPkg.Content.ProsperoFself.IsSelf(processed));

            // Verify ELF extracted from it has sanitized section table
            bool unOk = LibProsperoPkg.Content.ProsperoFself.TryUnfself(processed, out byte[]? elf);
            Assert.True(unOk);
            Assert.NotNull(elf);
            ulong e_shoff = BinaryPrimitives.ReadUInt64LittleEndian(elf.AsSpan(40, 8));
            Assert.Equal(0UL, e_shoff);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void SubStream_ZipArchive_ReadsEntriesWithoutTruncation()
    {
        byte[] zipBytes;
        using (var zipMs = new MemoryStream())
        {
            using (var zipCreator = new System.IO.Compression.ZipArchive(zipMs, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry1 = zipCreator.CreateEntry("test1.txt");
                using (var writer = new StreamWriter(entry1.Open())) writer.Write("Hello World 1");

                var entry2 = zipCreator.CreateEntry("test2.txt");
                using (var writer = new StreamWriter(entry2.Open())) writer.Write("Hello World 2");
            }
            zipBytes = zipMs.ToArray();
        }

        using var ms = new MemoryStream();
        byte[] prefix = new byte[1024];
        new Random(42).NextBytes(prefix);
        ms.Write(prefix);

        long zipStart = ms.Position;
        ms.Write(zipBytes);
        long zipLength = zipBytes.Length;

        // Verify using SubStream
        using var subStream = new LibProsperoPkg.Util.SubStream(ms, zipStart, zipLength);
        using var reader = new System.IO.Compression.ZipArchive(subStream, System.IO.Compression.ZipArchiveMode.Read);
        Assert.Equal(2, reader.Entries.Count);
        Assert.NotNull(reader.GetEntry("test1.txt"));
        Assert.NotNull(reader.GetEntry("test2.txt"));
    }

    [Fact]
    public void VerifyGothicPkg_IfPresent_PassesQuickVerify()
    {
        string pkgPath = Environment.GetEnvironmentVariable("PS5_TEST_PKG_PATH") ?? "";
        if (string.IsNullOrEmpty(pkgPath) || !File.Exists(pkgPath)) return;

        using var fs = File.OpenRead(pkgPath);
        var map = LibProsperoPkg.PKG.ProsperoPackageArchive.Inspect(fs);
        using var siStream = new LibProsperoPkg.Util.SubStream(fs, map.SupplementOffset, map.SupplementSize);
        using var zip = new System.IO.Compression.ZipArchive(siStream, System.IO.Compression.ZipArchiveMode.Read);
        Assert.Equal(7, zip.Entries.Count);
    }
}

