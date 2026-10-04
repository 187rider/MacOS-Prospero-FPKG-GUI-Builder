using System;
using System.IO;
using LibProsperoPkg.PFS.Compression;
using Xunit;

namespace LibProsperoPkg.Tests;

public sealed class KrakenParallelCompressionTests
{
    [Fact]
    public void CompressedPfsFile_ParallelWriteAndDecompress_RoundTripsExactly()
    {
        // 1 MB of repetitive + structured data across multiple 256 KiB blocks
        byte[] original = new byte[1024 * 1024];
        var rng = new Random(42);
        for (int i = 0; i < original.Length; i++)
        {
            original[i] = (byte)(i % 251 ^ (i / 1024));
        }

        byte[] compressed = CompressedPfsFileWriter.WriteCompressed(original, level: 7, blockSize: 262144);
        Assert.NotEmpty(compressed);
        Assert.True(compressed.Length < original.Length, "Compressed data should be smaller than original");

        var parsed = CompressedPfsFile.Parse(compressed);
        Assert.True(parsed.Blocks.Count >= 4, "Should have at least 4 blocks");

        byte[] decompressed = parsed.Decompress();
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void ProsperoCompressedPfsFile_ParallelWriteAndDecompress_RoundTripsExactly()
    {
        byte[] original = new byte[768 * 1024];
        for (int i = 0; i < original.Length; i++)
        {
            original[i] = (byte)((i * 37) % 256);
        }

        byte[] compressed = ProsperoCompressedPfsFileWriter.WriteCompressed(original, level: 7, blockSize: 262144);
        Assert.NotEmpty(compressed);

        var parsed = ProsperoCompressedPfsFile.Parse(compressed);
        byte[] decompressed = parsed.Decompress();
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void PprPfsKraken_ParallelPackAndUnpack_RoundTripsExactly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ppr_kraken_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string srcFile = Path.Combine(tempDir, "source.bin");
        string pfscFile = Path.Combine(tempDir, "compressed.pfsc");
        string dstFile = Path.Combine(tempDir, "decompressed.bin");

        try
        {
            byte[] original = new byte[512 * 1024];
            for (int i = 0; i < original.Length; i++)
            {
                original[i] = (byte)((i ^ 0x5A) % 256);
            }
            File.WriteAllBytes(srcFile, original);

            var options = new PprPfsKrakenWriteOptions
            {
                Level = 8,
                MaxDegreeOfParallelism = Environment.ProcessorCount
            };
            var result = PprPfsKraken.PackFile(srcFile, pfscFile, options);
            Assert.True(result.CompressedBlockCount > 0);

            long unpackedBytes = PprPfsKraken.UnpackFile(pfscFile, dstFile);
            Assert.Equal(original.Length, unpackedBytes);

            byte[] decompressed = File.ReadAllBytes(dstFile);
            Assert.Equal(original, decompressed);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void ProsperoSha3_NistVectors_MatchExactly()
    {
        // NIST test vector: "" (empty)
        byte[] emptyHash = LibProsperoPkg.Util.ProsperoSha3.HashData(ReadOnlySpan<byte>.Empty);
        Assert.Equal("a7ffc6f8bf1ed76651c14756a061d662f580ff4de43b49fa82d80a4b80f8434a", Convert.ToHexString(emptyHash).ToLowerInvariant());

        // NIST test vector: "abc"
        byte[] abcHash = LibProsperoPkg.Util.ProsperoSha3.HashData(System.Text.Encoding.ASCII.GetBytes("abc"));
        Assert.Equal("3a985da74fe225b2045c172d6bd390bd855f086e3e9d525b46bfe24511431532", Convert.ToHexString(abcHash).ToLowerInvariant());
    }

    [Fact]
    public void WorkDirectory_Option_PropagatesCorrectly()
    {
        var options = new LibProsperoPkg.ProsperoBuildOptions
        {
            WorkDirectory = "/Volumes/FastNVMe/scratch"
        };
        Assert.Equal("/Volumes/FastNVMe/scratch", options.WorkDirectory);

        var props = new LibProsperoPkg.PKG.ProsperoPkgBuildProperties
        {
            SourceFolder = "/tmp/dummy",
            ContentId = "UP0000-PPSA00000_00-0000000000000000",
            WorkDirectory = options.WorkDirectory
        };
        Assert.Equal("/Volumes/FastNVMe/scratch", props.WorkDirectory);
    }

    [Fact]
    public void InnerImageAssembler_PakFiles_AreEligibleForKrakenCompression()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "pak_comp_test_" + Guid.NewGuid().ToString("N"));
        string outPfs = Path.Combine(Path.GetTempPath(), "inner_" + Guid.NewGuid().ToString("N") + ".dat");
        Directory.CreateDirectory(Path.Combine(tempDir, "paks"));

        try
        {
            // 512 KiB compressible test .pak file
            byte[] pakData = new byte[512 * 1024];
            for (int i = 0; i < pakData.Length; i++) pakData[i] = (byte)(i % 127);
            string pakPath = Path.Combine(tempDir, "paks", "pakchunk0-ps5.pak");
            File.WriteAllBytes(pakPath, pakData);

            var root = new LibProsperoPkg.PFS.FSDir();
            var subDir = new LibProsperoPkg.PFS.FSDir { name = "paks", Parent = root };
            root.Dirs.Add(subDir);
            var fsFile = new LibProsperoPkg.PFS.FSFile(pakPath) { name = "pakchunk0-ps5.pak", Parent = subDir };
            subDir.Files.Add(fsFile);

            var assembler = new LibProsperoPkg.PFS.ProsperoPs5InnerImageAssembler(
                pakData.Length, 0u, null, null, compress: true);

            var result = assembler.BuildFromFsTreeToFile(root, outPfs);
            Assert.NotEmpty(result.Placements);
            var pakPlacement = result.Placements[0];

            Assert.False(pakPlacement.StoreRaw, "Pak file must NOT be forced to raw storage when compressible");
            Assert.True(pakPlacement.OnDiskSize < pakData.Length, "Kraken on-disk size must be smaller than raw size");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            if (File.Exists(outPfs)) File.Delete(outPfs);
        }
    }
}

