using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using LibProsperoPkg.PKG;
using Xunit;

namespace LibProsperoPkg.Tests;

public class PackageValidationTests
{
    [Fact]
    public void ValidatePackage_NonexistentFile_ReturnsInvalidReport()
    {
        string fakePath = Path.Combine(Path.GetTempPath(), "nonexistent_" + Guid.NewGuid().ToString("N") + ".pkg");
        var report = ProsperoPackageArchive.ValidatePackage(fakePath);

        Assert.False(report.IsValid);
        Assert.NotEmpty(report.Errors);
        Assert.Contains(report.Errors, e => e.Contains("does not exist"));
    }

    [Fact]
    public void ValidatePackage_SmallFile_ReturnsInvalidReport()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "truncated_" + Guid.NewGuid().ToString("N") + ".pkg");
        try
        {
            File.WriteAllBytes(tempFile, new byte[100]);
            var report = ProsperoPackageArchive.ValidatePackage(tempFile);

            Assert.False(report.IsValid);
            Assert.NotEmpty(report.Errors);
            Assert.Contains(report.Errors, e => e.Contains("too small"));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ValidatePackage_InvalidMagic_ReturnsInvalidReport()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "badmagic_" + Guid.NewGuid().ToString("N") + ".pkg");
        try
        {
            byte[] bad = new byte[8192];
            bad[0] = 0x50; bad[1] = 0x4B; // PK
            File.WriteAllBytes(tempFile, bad);
            var report = ProsperoPackageArchive.ValidatePackage(tempFile);

            Assert.False(report.IsValid);
            Assert.NotEmpty(report.Errors);
            Assert.Contains(report.Errors, e => e.Contains("magic"));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void PlayGoAutoRepair_MismatchContentIdAndMask_IsFixedDuringBuild()
    {
        // Construct a synthetic playgo-chunk.dat buffer with mismatched Content ID and bad mask
        byte[] chunk = new byte[128];
        "plgx"u8.CopyTo(chunk.AsSpan(0, 4));
        // Put corrupt mask
        BinaryPrimitives.WriteUInt64LittleEndian(chunk.AsSpan(56, 8), 0x4000000000000000UL);
        // Put mismatched content ID
        byte[] oldCid = Encoding.ASCII.GetBytes("EP4389-PPSA09827_00-GOTHIC1REMAKE000");
        oldCid.CopyTo(chunk.AsSpan(64));

        string targetCid = "UP4389-PPSA09826_00-GOTHIC1REMAKE000";

        // Create temporary source folder with param.json and sce_sys/playgo-chunk.dat
        string tempDir = Path.Combine(Path.GetTempPath(), "test_pkg_src_" + Guid.NewGuid().ToString("N"));
        string sceSys = Path.Combine(tempDir, "sce_sys");
        Directory.CreateDirectory(sceSys);

        try
        {
            var paramObj = new
            {
                contentId = targetCid,
                titleId = "PPSA09826",
                sdkVersion = "0x0400000000000000",
                requiredSystemSoftwareVersion = "0x0100000000000000"
            };
            File.WriteAllText(Path.Combine(sceSys, "param.json"), JsonSerializer.Serialize(paramObj));
            File.WriteAllBytes(Path.Combine(sceSys, "playgo-chunk.dat"), chunk);

            // Add dummy eboot.bin in root
            File.WriteAllBytes(Path.Combine(tempDir, "eboot.bin"), new byte[1024]);

            string outPkg = Path.Combine(Path.GetTempPath(), "test_output_" + Guid.NewGuid().ToString("N") + ".pkg");
            try
            {
                var props = new ProsperoPkgBuildProperties
                {
                    SourceFolder = tempDir,
                    ContentId = targetCid,
                    PublisherImageMode = ProsperoPublisherImageMode.PlaintextNoAuth,
                    AutoValidate = true
                };

                string builtPath = ProsperoPkgBuilder.Build(props, outPkg);
                Assert.True(File.Exists(builtPath));

                // Validate the newly built package
                var report = ProsperoPackageArchive.ValidatePackage(builtPath);
                Assert.True(report.IsValid, "Built package should pass automatic validation after repair.");
                Assert.Equal(targetCid, report.ContentId);
                Assert.True(report.PlayGoValid, "PlayGo chunk should be automatically repaired.");
                Assert.Equal(targetCid, report.PlayGoContentId);
                Assert.Equal(0xFFFFFFFFFFFFFFFFUL, report.PlayGoChunkMask);
            }
            finally
            {
                if (File.Exists(outPkg)) File.Delete(outPkg);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ValidateAndEditPlayGoChunk_RepairsMismatchedChunkAndQuarantinesConflictingTables()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "test_playgo_val_" + Guid.NewGuid().ToString("N"));
        string sceSys = Path.Combine(tempDir, "sce_sys");
        Directory.CreateDirectory(sceSys);

        try
        {
            // Create mismatched chunk
            byte[] chunk = new byte[128];
            "plgx"u8.CopyTo(chunk.AsSpan(0, 4));
            BinaryPrimitives.WriteUInt64LittleEndian(chunk.AsSpan(56, 8), 0x4000000000000000UL);
            Encoding.ASCII.GetBytes("EP4389-PPSA09827_00-GOTHIC1REMAKE000").CopyTo(chunk.AsSpan(64));
            string chunkFile = Path.Combine(sceSys, "playgo-chunk.dat");
            File.WriteAllBytes(chunkFile, chunk);

            // Create conflicting table files
            string hashFile = Path.Combine(sceSys, "playgo-hash-table.dat");
            string ficmFile = Path.Combine(sceSys, "playgo-ficm.dat");
            File.WriteAllBytes(hashFile, new byte[100]);
            File.WriteAllText(ficmFile, "{ \"test\": 123 }");

            string targetCid = "UP4389-PPSA09826_00-GOTHIC1REMAKE000";
            List<string> logs = new();
            bool result = LibProsperoPkg.PlayGo.ProsperoPlayGo.ValidateAndEditPlayGoChunk(tempDir, targetCid, logs.Add);

            Assert.True(result);
            Assert.True(File.Exists(chunkFile));
            Assert.True(File.Exists(chunkFile + ".bak"), "Backup of original chunk should exist");
            Assert.False(File.Exists(hashFile), "Conflicting hash table should be quarantined");
            Assert.True(File.Exists(hashFile + ".bak"));
            Assert.False(File.Exists(ficmFile), "Conflicting ficm table should be quarantined");
            Assert.True(File.Exists(ficmFile + ".bak"));

            // Check that the repaired chunk on disk has the right CID and mask
            byte[] updated = File.ReadAllBytes(chunkFile);
            string cidInFile = Encoding.ASCII.GetString(updated, 64, 36);
            ulong maskInFile = BinaryPrimitives.ReadUInt64LittleEndian(updated.AsSpan(56, 8));
            Assert.Equal(targetCid, cidInFile);
            Assert.Equal(0xFFFFFFFFFFFFFFFFUL, maskInFile);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ValidateAndEditPlayGoChunk_GeneratesMissingChunk()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "test_playgo_missing_" + Guid.NewGuid().ToString("N"));
        string sceSys = Path.Combine(tempDir, "sce_sys");
        Directory.CreateDirectory(sceSys);

        try
        {
            string chunkFile = Path.Combine(sceSys, "playgo-chunk.dat");
            string targetCid = "UP4389-PPSA09826_00-GOTHIC1REMAKE000";
            bool result = LibProsperoPkg.PlayGo.ProsperoPlayGo.ValidateAndEditPlayGoChunk(tempDir, targetCid);

            Assert.True(result);
            Assert.True(File.Exists(chunkFile));
            byte[] data = File.ReadAllBytes(chunkFile);
            Assert.True(data.AsSpan(0, 4).SequenceEqual("plgx"u8));
            string cidInFile = Encoding.ASCII.GetString(data, 64, 36);
            Assert.Equal(targetCid, cidInFile);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Build_SourceWithZeroSizePlayGoChunk_RepairsChunkExtentAndPassesValidation()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "test_playgo_zerosize_" + Guid.NewGuid().ToString("N"));
        string sceSys = Path.Combine(tempDir, "sce_sys");
        Directory.CreateDirectory(sceSys);

        string targetCid = "UP4389-PPSA09826_00-GOTHIC1REMAKE000";

        try
        {
            // Build a 416-byte playgo-chunk.dat that has 0 in chunkDataSize (offset 328)
            byte[] stubChunk = LibProsperoPkg.PlayGo.ProsperoPlayGo.BuildChunkDat(targetCid, chunkDataSize: 0, chunkTailSize: 0);
            File.WriteAllBytes(Path.Combine(sceSys, "playgo-chunk.dat"), stubChunk);

            var paramObj = new
            {
                contentId = targetCid,
                titleId = "PPSA09826",
                sdkVersion = "0x0400000000000000",
                requiredSystemSoftwareVersion = "0x0100000000000000"
            };
            File.WriteAllText(Path.Combine(sceSys, "param.json"), JsonSerializer.Serialize(paramObj));
            File.WriteAllBytes(Path.Combine(tempDir, "eboot.bin"), new byte[1024]);

            string outPkg = Path.Combine(Path.GetTempPath(), "test_zerosize_" + Guid.NewGuid().ToString("N") + ".pkg");
            try
            {
                var props = new ProsperoPkgBuildProperties
                {
                    SourceFolder = tempDir,
                    ContentId = targetCid,
                    PublisherImageMode = ProsperoPublisherImageMode.PlaintextNoAuth,
                    AutoValidate = true
                };

                string builtPath = ProsperoPkgBuilder.Build(props, outPkg);
                Assert.True(File.Exists(builtPath));

                var report = ProsperoPackageArchive.ValidatePackage(builtPath);
                Assert.True(report.IsValid, "Package should be valid after chunk size auto-repair.");
                Assert.True(report.PlayGoValid, "PlayGo chunk should be valid.");
                Assert.DoesNotContain(report.Errors, e => e.Contains("size is 0 bytes"));
            }
            finally
            {
                if (File.Exists(outPkg)) File.Delete(outPkg);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}

