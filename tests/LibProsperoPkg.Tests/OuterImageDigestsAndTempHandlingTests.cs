using System;
using System.IO;
using System.Security.Cryptography;
using LibProsperoPkg.PFS;
using LibProsperoPkg.PKG;
using Xunit;

namespace LibProsperoPkg.Tests;

public class OuterImageDigestsAndTempHandlingTests
{
    [Fact]
    public void OpenImage_WhenImagePathDoesNotExist_ReturnsSafeEmptyMemoryStream()
    {
        var innerResult = new ProsperoPs5InnerImageResult
        {
            Image = Array.Empty<byte>(),
            ImagePath = Path.Combine(Path.GetTempPath(), "nonexistent_" + Guid.NewGuid().ToString("N") + ".pfs_image.dat"),
            ImageLength = 131072,
            MetadataPlaintext = new byte[65536],
            Nodes = Array.Empty<ProsperoPs5MetaNode>(),
            Ndblock = 2,
            AfidLogicalOffsets = Array.Empty<long>()
        };

        // Must not throw FileNotFoundException
        using var stream = innerResult.OpenImage();
        Assert.NotNull(stream);
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public void BuildMeta18_WithOuterImageDigests_SucceedsEvenIfTempFilePruned()
    {
        ulong innerImageSize = 131072; // 2 blocks = 128 KiB
        long mountImageSize = 262144;  // 4 blocks = 256 KiB
        uint numBlocks = (uint)(innerImageSize / 65536);

        // Simulated outer image digests for 2 blocks
        byte[] digests = new byte[numBlocks * 32];
        for (int i = 0; i < digests.Length; i++)
        {
            digests[i] = (byte)(i + 1);
        }

        // Inner result pointing to a deleted/purged file on disk
        var innerResult = new ProsperoPs5InnerImageResult
        {
            Image = Array.Empty<byte>(),
            ImagePath = Path.Combine(Path.GetTempPath(), "purged_" + Guid.NewGuid().ToString("N") + ".pfs_image.dat"),
            ImageLength = (long)innerImageSize,
            MetadataPlaintext = new byte[65536],
            Nodes = Array.Empty<ProsperoPs5MetaNode>(),
            Ndblock = 2,
            AfidLogicalOffsets = Array.Empty<long>()
        };

        // When outerImageDigests is provided, it must not throw and must succeed
        byte[] meta18 = ProsperoNapsMeta.BuildMeta18(
            innerImageSize: innerImageSize,
            mountImageSize: mountImageSize,
            contentFiles: Array.Empty<(string, long)>(),
            inner: innerResult,
            outerImageDigests: digests);

        Assert.NotNull(meta18);
        Assert.True(meta18.Length > 0);
    }

    [Fact]
    public void BuildMeta18_FallbackToMountImageStream_WhenDigestsNullAndFileMissing()
    {
        ulong innerImageSize = 131072; // 2 blocks = 128 KiB
        long mountImageSize = 65536L + 131072L + 65536L; // Header (64K) + innerImage (128K) + extra (64K)

        // Create a mountImage stream containing dummy data at offset 65536
        byte[] mountBytes = new byte[mountImageSize];
        RandomNumberGenerator.Fill(mountBytes);
        using var mountStream = new MemoryStream(mountBytes);

        var innerResult = new ProsperoPs5InnerImageResult
        {
            Image = Array.Empty<byte>(),
            ImagePath = Path.Combine(Path.GetTempPath(), "purged_" + Guid.NewGuid().ToString("N") + ".pfs_image.dat"),
            ImageLength = (long)innerImageSize,
            MetadataPlaintext = new byte[65536],
            Nodes = Array.Empty<ProsperoPs5MetaNode>(),
            Ndblock = 2,
            AfidLogicalOffsets = Array.Empty<long>()
        };

        // outerImageDigests is null, innerImage.ImagePath is missing, but mountImageStream is provided!
        byte[] meta18 = ProsperoNapsMeta.BuildMeta18(
            innerImageSize: innerImageSize,
            mountImageSize: mountImageSize,
            contentFiles: Array.Empty<(string, long)>(),
            inner: innerResult,
            mountImageStream: mountStream,
            outerImageDigests: null);

        Assert.NotNull(meta18);
        Assert.True(meta18.Length > 0);
    }

    [Fact]
    public void CompletionSound_WavFileExistsAndIsValidRiffPcm()
    {
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.GetFullPath(Path.Combine(baseDir, "../../../../../gui/wwwroot/sounds/success.wav")),
            Path.GetFullPath(Path.Combine(baseDir, "../../../../../gui/Resources/sounds/success.wav")),
            Path.GetFullPath("gui/wwwroot/sounds/success.wav"),
            Path.GetFullPath("gui/Resources/sounds/success.wav")
        ];

        string? soundPath = candidates.FirstOrDefault(File.Exists);
        Assert.NotNull(soundPath);
        Assert.True(File.Exists(soundPath));

        byte[] header = File.ReadAllBytes(soundPath);
        Assert.True(header.Length >= 44);
        // Check RIFF header
        Assert.Equal((byte)'R', header[0]);
        Assert.Equal((byte)'I', header[1]);
        Assert.Equal((byte)'F', header[2]);
        Assert.Equal((byte)'F', header[3]);
        // Check WAVE format
        Assert.Equal((byte)'W', header[8]);
        Assert.Equal((byte)'A', header[9]);
        Assert.Equal((byte)'V', header[10]);
        Assert.Equal((byte)'E', header[11]);
    }
}
