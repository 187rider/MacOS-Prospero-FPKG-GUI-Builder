using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using LibProsperoPkg.Content;
using Xunit;

namespace LibProsperoPkg.Tests;

public class FakelibAutoDetectionTests
{
    private static byte[] CreateDummyElf(string[]? importedModules = null)
    {
        // Minimal valid 64-bit ELF with 1 PT_LOAD segment (e_phoff=64, e_phentsize=56, e_phnum=1)
        int totalSize = 512;
        if (importedModules != null)
        {
            totalSize += importedModules.Sum(m => m.Length + 1);
        }

        byte[] elf = new byte[totalSize];
        // ELF header (64 bytes)
        elf[0] = 0x7F;
        elf[1] = (byte)'E';
        elf[2] = (byte)'L';
        elf[3] = (byte)'F';
        elf[4] = 2; // 64-bit
        elf[5] = 1; // Little-endian
        elf[6] = 1; // ELF version
        elf[7] = 9; // FreeBSD / Prospero ABI
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(16, 2), 3); // ET_DYN
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(18, 2), 0x3E); // AMD x86-64
        BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(20, 4), 1); // EV_CURRENT
        BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(32, 8), 64); // e_phoff = 64
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(54, 2), 56); // e_phentsize = 56
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(56, 2), 1);  // e_phnum = 1

        // Program header 0 (at offset 64, 56 bytes)
        BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(64, 4), 1); // p_type = PT_LOAD
        BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(68, 4), 5); // p_flags = PF_R | PF_X
        BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(72, 8), 0); // p_offset = 0
        BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(80, 8), 0x400000); // p_vaddr
        BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(88, 8), 0x400000); // p_paddr
        BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(96, 8), (ulong)totalSize); // p_filesz
        BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(104, 8), (ulong)totalSize); // p_memsz
        BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(112, 8), 0x1000); // p_align

        if (importedModules != null && importedModules.Length > 0)
        {
            int offset = 120;
            foreach (var mod in importedModules)
            {
                byte[] modBytes = Encoding.ASCII.GetBytes(mod + "\0");
                modBytes.CopyTo(elf, offset);
                offset += modBytes.Length;
            }
        }

        return elf;
    }

    [Fact]
    public void DetectImportedCompatibilityModules_CleanElf_ReturnsEmpty()
    {
        byte[] elf = CreateDummyElf();
        var detected = ProsperoFself.DetectImportedCompatibilityModules(elf);
        Assert.Empty(detected);
    }

    [Fact]
    public void DetectImportedCompatibilityModules_WithAmpr_DetectsAmprOnly()
    {
        byte[] elf = CreateDummyElf(["libSceAmpr.sprx", "libkernel.sprx"]);
        var detected = ProsperoFself.DetectImportedCompatibilityModules(elf);
        Assert.Single(detected);
        Assert.Contains("libSceAmpr.sprx", detected);
        Assert.DoesNotContain("libScePsml.sprx", detected);
        Assert.DoesNotContain("libSceAgcDriver.sprx", detected);
    }

    [Fact]
    public void DetectImportedCompatibilityModules_WithMultipleModules_DetectsAllPresent()
    {
        byte[] elf = CreateDummyElf(["libSceAmpr.sprx", "libScePsml.sprx", "libSceVideoOut.sprx"]);
        var detected = ProsperoFself.DetectImportedCompatibilityModules(elf);
        Assert.Equal(2, detected.Count);
        Assert.Contains("libSceAmpr.sprx", detected);
        Assert.Contains("libScePsml.sprx", detected);
        Assert.DoesNotContain("libSceAgc.sprx", detected);
    }

    [Theory]
    [InlineData("libSceAmpr.sprx", 0x0400000000000000uL, true)]   // SDK 4.00 lacks AMPR (FW 6.00+)
    [InlineData("libSceAmpr.sprx", 0x0500000000000000uL, true)]   // SDK 5.00 lacks AMPR (FW 6.00+)
    [InlineData("libSceAmpr.sprx", 0x0600000000000000uL, false)]  // SDK 6.00 natively has AMPR
    [InlineData("libScePsml.sprx", 0x0400000000000000uL, true)]   // SDK 4.00 lacks PSML (FW 6.00+)
    [InlineData("libScePsml.sprx", 0x0600000000000000uL, false)]  // SDK 6.00 natively has PSML
    [InlineData("libSceAgcDriver.sprx", 0x0400000000000000uL, true)]  // SDK 4.00 lacks AGC driver (FW 5.00+)
    [InlineData("libSceAgcDriver.sprx", 0x0500000000000000uL, false)] // SDK 5.00 natively has AGC driver
    [InlineData("libc.prx", 0x0400000000000000uL, true)]              // SDK 4.00 lacks SceLibcV2 (FW 5.00+)
    [InlineData("libc.prx", 0x0500000000000000uL, false)]             // SDK 5.00 natively has SceLibcV2
    [InlineData("right.sprx", 0x0400000000000000uL, true)]            // SDK 4.00 lacks SceGameRight (FW 5.00+)
    [InlineData("right.sprx", 0x0500000000000000uL, false)]           // SDK 5.00 natively has SceGameRight
    [InlineData("libSceNpCppWebApi.prx", 0x0400000000000000uL, true)] // SDK 4.00 lacks NpCppWebApi (FW 6.00+)
    [InlineData("libSceNpCppWebApi.prx", 0x0600000000000000uL, false)]// SDK 6.00 natively has NpCppWebApi
    [InlineData("libSceFontGsm.prx", 0x0400000000000000uL, true)]     // SDK 4.00 lacks FontGsm (FW 6.00+)
    [InlineData("libSceFontGsm.prx", 0x0600000000000000uL, false)]    // SDK 6.00 natively has FontGsm
    [InlineData("libSceJobManager.prx", 0x0400000000000000uL, true)]  // SDK 4.00 lacks JobManager (FW 6.00+)
    [InlineData("libSceJobManager.prx", 0x0600000000000000uL, false)] // SDK 6.00 natively has JobManager
    [InlineData("libScePfs.prx", 0x0400000000000000uL, true)]         // SDK 4.00 lacks Pfs (FW 6.00+)
    [InlineData("libScePfs.prx", 0x0600000000000000uL, false)]        // SDK 6.00 natively has Pfs
    [InlineData("libSceSaveData.native.sprx", 0x0400000000000000uL, true)] // SDK 4.00 lacks SaveData native bridge (FW 6.00+)
    [InlineData("libSceSaveData.native.sprx", 0x0600000000000000uL, false)]// SDK 6.00 natively has SaveData native bridge
    public void IsModuleMissingOnTargetSdk_CorrectlyEvaluatesFirmwarePresence(string modFile, ulong targetSdk, bool expectedMissing)
    {
        bool isMissing = ProsperoFself.IsModuleMissingOnTargetSdk(modFile, targetSdk);
        Assert.Equal(expectedMissing, isMissing);
    }

    [Theory]
    [InlineData("libSceAmpr.sprx", "fakelib")]
    [InlineData("libSceAgc.sprx", "fakelib")]
    [InlineData("libSceSaveData.native.sprx", "fakelib")]
    [InlineData("libc.prx", "sce_module")]
    [InlineData("libSceNpCppWebApi.prx", "sce_module")]
    [InlineData("libSceFontGsm.prx", "sce_module")]
    [InlineData("libSceJobManager.prx", "sce_module")]
    [InlineData("libScePfs.prx", "sce_module")]
    [InlineData("right.sprx", "sce_sys/about")]
    [InlineData("unknown.sprx", "fakelib")]
    public void GetModuleTargetSubdirectory_ReturnsExpectedDirectory(string file, string expectedSubdir)
    {
        string subdir = ProsperoFself.GetModuleTargetSubdirectory(file);
        Assert.Equal(expectedSubdir, subdir);
    }

    [Fact]
    public void RecursiveMakeFself_GameWithoutDependencies_DoesNotCreateFakelibFolder()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"fpkg-clean-{Guid.NewGuid():N}");
        string bundledFakelib = Path.Combine(Path.GetTempPath(), $"bundled-fake-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(bundledFakelib);

        try
        {
            // Set up bundled stubs
            File.WriteAllBytes(Path.Combine(bundledFakelib, "libSceAmpr.sprx"), new byte[100]);
            File.WriteAllBytes(Path.Combine(bundledFakelib, "libScePsml.sprx"), new byte[100]);

            // Clean game with no missing module dependencies
            byte[] cleanElf = CreateDummyElf(["libkernel.sprx", "libSceVideoOut.sprx"]);
            File.WriteAllBytes(Path.Combine(tempDir, "eboot.bin"), cleanElf);

            // Run backport to SDK 4.00
            int converted = ProsperoFself.RecursiveMakeFself(tempDir, null, default, 0x0400000000000000uL, bundledFakelib);
            Assert.Equal(1, converted);

            // fakelib folder should NOT exist!
            string fakelibDir = Path.Combine(tempDir, "fakelib");
            Assert.False(Directory.Exists(fakelibDir), "fakelib folder must not be created when game does not import missing modules");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            if (Directory.Exists(bundledFakelib)) Directory.Delete(bundledFakelib, recursive: true);
        }
    }

    [Fact]
    public void RecursiveMakeFself_GameWithAmprOnly_StagesOnlyAmprStub()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"fpkg-ampr-{Guid.NewGuid():N}");
        string bundledFakelib = Path.Combine(Path.GetTempPath(), $"bundled-fake-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(bundledFakelib);

        try
        {
            // Set up 3 bundled stubs
            File.WriteAllBytes(Path.Combine(bundledFakelib, "libSceAmpr.sprx"), new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(bundledFakelib, "libScePsml.sprx"), new byte[] { 4, 5, 6 });
            File.WriteAllBytes(Path.Combine(bundledFakelib, "libSceAgcDriver.sprx"), new byte[] { 7, 8, 9 });

            // Game imports AMPR but NOT PSML or AGC
            byte[] amprElf = CreateDummyElf(["libSceAmpr.sprx", "libkernel.sprx"]);
            File.WriteAllBytes(Path.Combine(tempDir, "eboot.bin"), amprElf);

            // Run backport to SDK 4.00
            int converted = ProsperoFself.RecursiveMakeFself(tempDir, null, default, 0x0400000000000000uL, bundledFakelib);
            Assert.Equal(1, converted);

            // fakelib folder should exist and contain ONLY libSceAmpr.sprx
            string fakelibDir = Path.Combine(tempDir, "fakelib");
            Assert.True(Directory.Exists(fakelibDir));
            Assert.True(File.Exists(Path.Combine(fakelibDir, "libSceAmpr.sprx")));
            Assert.False(File.Exists(Path.Combine(fakelibDir, "libScePsml.sprx")));
            Assert.False(File.Exists(Path.Combine(fakelibDir, "libSceAgcDriver.sprx")));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            if (Directory.Exists(bundledFakelib)) Directory.Delete(bundledFakelib, recursive: true);
        }
    }

    [Fact]
    public void RecursiveMakeFself_GameWithPsml_StagesPsmlAndCompanionK9Psp()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"fpkg-psml-{Guid.NewGuid():N}");
        string bundledFakelib = Path.Combine(Path.GetTempPath(), $"bundled-fake-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(bundledFakelib);

        try
        {
            File.WriteAllBytes(Path.Combine(bundledFakelib, "libScePsml.sprx"), new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(bundledFakelib, "k9.psp"), new byte[] { 4, 5, 6, 7 });

            byte[] psmlElf = CreateDummyElf(["libScePsml.sprx", "libkernel.sprx"]);
            File.WriteAllBytes(Path.Combine(tempDir, "eboot.bin"), psmlElf);

            int converted = ProsperoFself.RecursiveMakeFself(tempDir, null, default, 0x0400000000000000uL, bundledFakelib);
            Assert.Equal(1, converted);

            string fakelibPsml = Path.Combine(tempDir, "fakelib", "libScePsml.sprx");
            string rootK9 = Path.Combine(tempDir, "k9.psp");
            Assert.True(File.Exists(fakelibPsml), "libScePsml.sprx must be staged to fakelib/");
            Assert.True(File.Exists(rootK9), "Companion k9.psp must be staged to app0/ root");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            if (Directory.Exists(bundledFakelib)) Directory.Delete(bundledFakelib, recursive: true);
        }
    }

    [Fact]
    public void Test_Scan_PPSA03001_Detection()
    {
        string targetDir = Environment.GetEnvironmentVariable("PS5_TEST_PPSA03001_DIR") ?? "";
        if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir)) return;

        var allDetected = new Dictionary<string, List<string>>();
        int scannedCount = 0;
        string[] extensions = [".bin", ".elf", ".prx", ".sprx"];

        foreach (string file in Directory.EnumerateFiles(targetDir, "*", SearchOption.AllDirectories))
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (!extensions.Contains(ext) || file.EndsWith(".bak") || file.EndsWith(".esbak")) continue;

            string rel = Path.GetRelativePath(targetDir, file);
            if (rel.StartsWith("fakelib", StringComparison.OrdinalIgnoreCase)) continue;

            byte[] fileBytes = File.ReadAllBytes(file);
            byte[]? elfBytes = null;
            if (ProsperoFself.TryUnfself(fileBytes, out byte[]? unpacked) && unpacked != null)
            {
                elfBytes = unpacked;
            }
            else if (ProsperoFself.IsElf(fileBytes))
            {
                elfBytes = fileBytes;
            }

            if (elfBytes == null) continue;
            scannedCount++;

            var detected = ProsperoFself.DetectImportedCompatibilityModules(elfBytes);
            if (detected.Count > 0)
            {
                allDetected[rel] = detected.ToList();
            }
        }

        // Print results to test output
        var sb = new StringBuilder();
        sb.AppendLine($"[TEST SCAN RESULTS] Scanned {scannedCount} executable(s) in {targetDir}:");
        if (allDetected.Count == 0)
        {
            sb.AppendLine("  -> ZERO missing compatibility modules detected!");
            sb.AppendLine("  -> Result: Clean pass. NO fakelib folder should be created or staged.");
        }
        else
        {
            foreach (var kvp in allDetected)
            {
                sb.AppendLine($"  -> {kvp.Key}: imported [{string.Join(", ", kvp.Value)}]");
            }
        }

        // Output to console for xunit
        Console.WriteLine(sb.ToString());
    }

    [Fact]
    public void RecursiveMakeFself_GameWithLibcDependency_StagesInSceModule()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"fpkg-libc-{Guid.NewGuid():N}");
        string bundledFakelib = Path.Combine(Path.GetTempPath(), $"bundled-fake-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(bundledFakelib);

        try
        {
            File.WriteAllBytes(Path.Combine(bundledFakelib, "libc.prx"), new byte[100]);

            byte[] elfWithLibc = CreateDummyElf(["libc.prx"]);
            File.WriteAllBytes(Path.Combine(tempDir, "eboot.bin"), elfWithLibc);

            int converted = ProsperoFself.RecursiveMakeFself(tempDir, null, default, 0x0400000000000000uL, bundledFakelib);
            Assert.Equal(1, converted);

            string targetFile = Path.Combine(tempDir, "sce_module", "libc.prx");
            Assert.True(File.Exists(targetFile));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            if (Directory.Exists(bundledFakelib)) Directory.Delete(bundledFakelib, true);
        }
    }

    [Fact]
    public void DetectImportedCompatibilityModules_WithSaveDataNativePrx_DetectsModule()
    {
        byte[] elf = CreateDummyElf(["libSceSaveData.native.prx", "libkernel.sprx"]);
        var detected = ProsperoFself.DetectImportedCompatibilityModules(elf);
        Assert.Contains("libSceSaveData.native.sprx", detected);
    }

    [Fact]
    public void DetectImportedCompatibilityModules_WithSaveDataNativeModuleName_DetectsModule()
    {
        byte[] elf = CreateDummyElf(["libSceSaveData_native", "libkernel.sprx"]);
        var detected = ProsperoFself.DetectImportedCompatibilityModules(elf);
        Assert.Contains("libSceSaveData.native.sprx", detected);
    }

    [Fact]
    public void DetectImportedCompatibilityModules_WithStandardSaveData_DoesNotFalselyTriggerSaveDataNative()
    {
        byte[] elf = CreateDummyElf(["libSceSaveData.sprx", "libSceSaveData"]);
        var detected = ProsperoFself.DetectImportedCompatibilityModules(elf);
        Assert.DoesNotContain("libSceSaveData.native.sprx", detected);
    }

    private static string GetDownloadsDatasetPath(string subDir) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", subDir);

    [Fact]
    public void PPSA31246_Dataset_DetectsAllRequiredCompatibilityModules()
    {
        string d1 = GetDownloadsDatasetPath("[DLPSGAME.COM]-4xx[PPSA31246][01.200.000]");
        string d2 = GetDownloadsDatasetPath("[DLPSGAME.COM]-4xx[PPSA31246][01.200.000] 2");
        string datasetDir = Directory.Exists(d1) ? d1 : d2;
        string ebootPath = Path.Combine(datasetDir, "eboot.bin");
        if (!File.Exists(ebootPath)) return;

        byte[] ebootBytes = File.ReadAllBytes(ebootPath);
        byte[] elfBytes;
        if (ProsperoFself.TryUnfself(ebootBytes, out byte[]? unpacked) && unpacked != null)
        {
            elfBytes = unpacked;
        }
        else
        {
            elfBytes = ebootBytes;
        }

        var detected = ProsperoFself.DetectImportedCompatibilityModules(elfBytes);

        // Required 4xx modules present in dataset fakelib/ and sce_module/
        Assert.Contains("libSceSaveData.native.sprx", detected);
        Assert.Contains("libSceAgc.sprx", detected);
        Assert.Contains("libSceAgcDriver.sprx", detected);
        Assert.Contains("libScePsml.sprx", detected);
        Assert.Contains("libc.prx", detected);

        // Validate target staging directories
        Assert.Equal("fakelib", ProsperoFself.GetModuleTargetSubdirectory("libSceSaveData.native.sprx"));
        Assert.Equal("fakelib", ProsperoFself.GetModuleTargetSubdirectory("libSceAgc.sprx"));
        Assert.Equal("fakelib", ProsperoFself.GetModuleTargetSubdirectory("libSceAgcDriver.sprx"));
        Assert.Equal("fakelib", ProsperoFself.GetModuleTargetSubdirectory("libScePsml.sprx"));
        Assert.Equal("sce_module", ProsperoFself.GetModuleTargetSubdirectory("libc.prx"));
    }

    [Fact]
    public void DetectImportedCompatibilityModules_WithNpEntitlement_DetectsRightSprx()
    {
        byte[] elf = CreateDummyElf(["libSceNpEntitlementAccess", "libkernel.sprx"]);
        var detected = ProsperoFself.DetectImportedCompatibilityModules(elf);
        Assert.Contains("right.sprx", detected);
        Assert.Equal("sce_sys/about", ProsperoFself.GetModuleTargetSubdirectory("right.sprx"));
    }

    [Fact]
    public void DetectImportedCompatibilityModules_WithNpCommerce_DetectsRightSprx()
    {
        byte[] elf = CreateDummyElf(["libSceNpCommerce", "libkernel.sprx"]);
        var detected = ProsperoFself.DetectImportedCompatibilityModules(elf);
        Assert.Contains("right.sprx", detected);
        Assert.Equal("sce_sys/about", ProsperoFself.GetModuleTargetSubdirectory("right.sprx"));
    }

    [Fact]
    public void PPSA34299_WWE2K26_Dataset_DetectsAllRequiredCompatibilityModules()
    {
        string datasetDir = GetDownloadsDatasetPath("[DLPSGAME.COM]-FW 4xx PPSA34299 WWE 2K26 (01.006.000) backport files");
        string ebootPath = Path.Combine(datasetDir, "eboot.bin");
        if (!File.Exists(ebootPath)) return;

        byte[] ebootBytes = File.ReadAllBytes(ebootPath);
        byte[] elfBytes;
        if (ProsperoFself.TryUnfself(ebootBytes, out byte[]? unpacked) && unpacked != null)
        {
            elfBytes = unpacked;
        }
        else
        {
            elfBytes = ebootBytes;
        }

        var detected = ProsperoFself.DetectImportedCompatibilityModules(elfBytes);

        // Required modules present in dataset fakelib/, sce_module/, and sce_sys/about/
        Assert.Contains("libSceAgc.sprx", detected);
        Assert.Contains("libSceAgcDriver.sprx", detected);
        Assert.Contains("libSceSaveData.native.sprx", detected);
        Assert.Contains("libc.prx", detected);
        Assert.Contains("right.sprx", detected);

        // Validate target staging directories
        Assert.Equal("fakelib", ProsperoFself.GetModuleTargetSubdirectory("libSceAgc.sprx"));
        Assert.Equal("fakelib", ProsperoFself.GetModuleTargetSubdirectory("libSceAgcDriver.sprx"));
        Assert.Equal("fakelib", ProsperoFself.GetModuleTargetSubdirectory("libSceSaveData.native.sprx"));
        Assert.Equal("sce_module", ProsperoFself.GetModuleTargetSubdirectory("libc.prx"));
        Assert.Equal("sce_sys/about", ProsperoFself.GetModuleTargetSubdirectory("right.sprx"));
    }

    [Fact]
    public void PPSA15421_UntilDawn_Dataset_DetectsAllRequiredCompatibilityModules()
    {
        string datasetDir = GetDownloadsDatasetPath("[DLPSGAME.COM]-4xx_by_Kira[01.005.000][PPSA15421]");
        string ebootPath = Path.Combine(datasetDir, "eboot.bin");
        if (!File.Exists(ebootPath)) return;

        byte[] ebootBytes = File.ReadAllBytes(ebootPath);
        byte[] elfBytes;
        if (ProsperoFself.TryUnfself(ebootBytes, out byte[]? unpacked) && unpacked != null)
        {
            elfBytes = unpacked;
        }
        else
        {
            elfBytes = ebootBytes;
        }

        var detected = ProsperoFself.DetectImportedCompatibilityModules(elfBytes);

        // Required modules in Until Dawn (2024 Remake / UE5)
        Assert.Contains("libSceAgc.sprx", detected);
        Assert.Contains("libSceAgcDriver.sprx", detected);
        Assert.Contains("libSceAmpr.sprx", detected);
        Assert.Contains("libSceNpCppWebApi.prx", detected);
        Assert.Contains("libScePsml.sprx", detected);
        Assert.Contains("libSceSaveData.native.sprx", detected);
        Assert.Contains("libSceVdecCore.native.sprx", detected);
        Assert.Contains("libc.prx", detected);
        Assert.Contains("right.sprx", detected);

        // Validate target staging directories
        Assert.Equal("fakelib", ProsperoFself.GetModuleTargetSubdirectory("libSceAgc.sprx"));
        Assert.Equal("fakelib", ProsperoFself.GetModuleTargetSubdirectory("libSceAgcDriver.sprx"));
        Assert.Equal("fakelib", ProsperoFself.GetModuleTargetSubdirectory("libSceAmpr.sprx"));
        Assert.Equal("fakelib", ProsperoFself.GetModuleTargetSubdirectory("libScePsml.sprx"));
        Assert.Equal("fakelib", ProsperoFself.GetModuleTargetSubdirectory("libSceSaveData.native.sprx"));
        Assert.Equal("fakelib", ProsperoFself.GetModuleTargetSubdirectory("libSceVdecCore.native.sprx"));
        Assert.Equal("sce_module", ProsperoFself.GetModuleTargetSubdirectory("libSceNpCppWebApi.prx"));
        Assert.Equal("sce_module", ProsperoFself.GetModuleTargetSubdirectory("libc.prx"));
        Assert.Equal("sce_sys/about", ProsperoFself.GetModuleTargetSubdirectory("right.sprx"));

        // Check Wwise audio plugins in prx/
        string prxDir = Path.Combine(datasetDir, "prx");
        if (Directory.Exists(prxDir))
        {
            var prxFiles = Directory.GetFiles(prxDir, "*.prx");
            Assert.True(prxFiles.Length >= 20, "Should detect all Wwise audio PRX plugins");
            foreach (var prx in prxFiles)
            {
                byte[] prxBytes = File.ReadAllBytes(prx);
                Assert.True(ProsperoFself.IsSelf(prxBytes) || ProsperoFself.IsElf(prxBytes), $"Plugin {Path.GetFileName(prx)} must be valid ELF/FSELF");
            }
        }
    }

    [Fact]
    public void PPSA21203_OblivionRemastered_Dataset_DetectsAllRequiredCompatibilityModules()
    {
        string datasetDir = GetDownloadsDatasetPath("[DLPSGAME.COM]-4xx_by_Kira[01.512.105][PPSA21203]");
        string ebootPath = Path.Combine(datasetDir, "eboot.bin");
        if (!File.Exists(ebootPath)) return;

        byte[] ebootBytes = File.ReadAllBytes(ebootPath);
        byte[] elfBytes;
        if (ProsperoFself.TryUnfself(ebootBytes, out byte[]? unpacked) && unpacked != null)
        {
            elfBytes = unpacked;
        }
        else
        {
            elfBytes = ebootBytes;
        }

        var detected = ProsperoFself.DetectImportedCompatibilityModules(elfBytes);

        // Required modules in Oblivion Remastered (Bethesda / Unreal Engine)
        Assert.Contains("libSceAgc.sprx", detected);
        Assert.Contains("libSceAgcDriver.sprx", detected);
        Assert.Contains("libSceAmpr.sprx", detected);
        Assert.Contains("libSceNpCppWebApi.prx", detected);
        Assert.Contains("libSceSaveData.native.sprx", detected);
        Assert.Contains("libScePlayGo.sprx", detected);
        Assert.Contains("libSceVdecCore.native.sprx", detected);
        Assert.Contains("libc.prx", detected);
        Assert.Contains("right.sprx", detected);

        // Oblivion Remastered does NOT use PSML, so libScePsml must NOT be falsely detected
        Assert.DoesNotContain("libScePsml.sprx", detected);

        // Validate target staging directories
        Assert.Equal("fakelib", ProsperoFself.GetModuleTargetSubdirectory("libSceAgc.sprx"));
        Assert.Equal("fakelib", ProsperoFself.GetModuleTargetSubdirectory("libSceAgcDriver.sprx"));
        Assert.Equal("fakelib", ProsperoFself.GetModuleTargetSubdirectory("libSceAmpr.sprx"));
        Assert.Equal("fakelib", ProsperoFself.GetModuleTargetSubdirectory("libSceSaveData.native.sprx"));
        Assert.Equal("fakelib", ProsperoFself.GetModuleTargetSubdirectory("libScePlayGo.sprx"));
        Assert.Equal("fakelib", ProsperoFself.GetModuleTargetSubdirectory("libSceVdecCore.native.sprx"));
        Assert.Equal("sce_module", ProsperoFself.GetModuleTargetSubdirectory("libSceNpCppWebApi.prx"));
        Assert.Equal("sce_module", ProsperoFself.GetModuleTargetSubdirectory("libc.prx"));
        Assert.Equal("sce_sys/about", ProsperoFself.GetModuleTargetSubdirectory("right.sprx"));

        // Check 28 Wwise/audio plugins in prx/ (including akimpacter, akreflect, izotope)
        string prxDir = Path.Combine(datasetDir, "prx");
        if (Directory.Exists(prxDir))
        {
            var prxFiles = Directory.GetFiles(prxDir, "*.prx");
            Assert.Equal(28, prxFiles.Length);
            foreach (var prx in prxFiles)
            {
                byte[] prxBytes = File.ReadAllBytes(prx);
                Assert.True(ProsperoFself.IsSelf(prxBytes) || ProsperoFself.IsElf(prxBytes), $"Plugin {Path.GetFileName(prx)} must be valid ELF/FSELF");
            }
        }
    }

    [Fact]
    public void DetectImportedCompatibilityModules_WithLibSceUlt_DetectsFiberSprx()
    {
        byte[] elf = CreateDummyElf(["libSceUlt", "libkernel.sprx"]);
        var detected = ProsperoFself.DetectImportedCompatibilityModules(elf);
        Assert.Contains("libSceFiber.sprx", detected);
        Assert.Equal("fakelib", ProsperoFself.GetModuleTargetSubdirectory("libSceFiber.sprx"));
    }

    [Fact]
    public void SWO_Dataset_DetectsAllRequiredCompatibilityModules()
    {
        string datasetDir = GetDownloadsDatasetPath("SWO_4xx-5.xx-[DLPSGAME.COM]");
        string ebootPath = Path.Combine(datasetDir, "eboot.bin");
        if (!File.Exists(ebootPath)) return;

        byte[] ebootBytes = File.ReadAllBytes(ebootPath);
        byte[] elfBytes;
        if (ProsperoFself.TryUnfself(ebootBytes, out byte[]? unpacked) && unpacked != null)
        {
            elfBytes = unpacked;
        }
        else
        {
            elfBytes = ebootBytes;
        }

        var detected = ProsperoFself.DetectImportedCompatibilityModules(elfBytes);

        // Required modules in Star Wars Outlaws (Snowdrop engine)
        Assert.Contains("libSceAgc.sprx", detected);
        Assert.Contains("libSceAgcDriver.sprx", detected);
        Assert.Contains("libSceAmpr.sprx", detected);
        Assert.Contains("libSceFiber.sprx", detected);
        Assert.Contains("libSceNpCppWebApi.prx", detected);
        Assert.Contains("libScePsml.sprx", detected);
        Assert.Contains("libSceSaveData.native.sprx", detected);
        Assert.Contains("libc.prx", detected);
        Assert.Contains("right.sprx", detected);

        // Validate target staging directories
        Assert.Equal("fakelib", ProsperoFself.GetModuleTargetSubdirectory("libSceAgc.sprx"));
        Assert.Equal("fakelib", ProsperoFself.GetModuleTargetSubdirectory("libSceAgcDriver.sprx"));
        Assert.Equal("fakelib", ProsperoFself.GetModuleTargetSubdirectory("libSceAmpr.sprx"));
        Assert.Equal("fakelib", ProsperoFself.GetModuleTargetSubdirectory("libSceFiber.sprx"));
        Assert.Equal("fakelib", ProsperoFself.GetModuleTargetSubdirectory("libScePsml.sprx"));
        Assert.Equal("fakelib", ProsperoFself.GetModuleTargetSubdirectory("libSceSaveData.native.sprx"));
        Assert.Equal("sce_module", ProsperoFself.GetModuleTargetSubdirectory("libSceNpCppWebApi.prx"));
        Assert.Equal("sce_module", ProsperoFself.GetModuleTargetSubdirectory("libc.prx"));
        Assert.Equal("sce_sys/about", ProsperoFself.GetModuleTargetSubdirectory("right.sprx"));
    }

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr objc_getClass(string name);

    [Fact]
    public void GetDetectedFakelibStatus_NeverReturnsPspFileAndStagesK9InBackground()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"fpkg-status-{Guid.NewGuid():N}");
        string bundledFakelib = Path.Combine(Path.GetTempPath(), $"bundled-status-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(bundledFakelib);

        try
        {
            File.WriteAllBytes(Path.Combine(bundledFakelib, "libScePsml.sprx"), new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(bundledFakelib, "k9.psp"), new byte[] { 4, 5, 6, 7 });
            File.WriteAllBytes(Path.Combine(bundledFakelib, "libSceSaveData.native.sprx"), new byte[] { 8, 9, 10 });

            byte[] elfBytes = CreateDummyElf(["libScePsml.sprx", "libSceSaveData_native", "libkernel.sprx"]);
            File.WriteAllBytes(Path.Combine(tempDir, "eboot.bin"), elfBytes);

            var list = ProsperoFself.GetDetectedFakelibStatus(tempDir, 0x0400000000000000uL, bundledFakelib);

            // Rule check: .psp MUST NEVER be present in the returned list
            Assert.DoesNotContain(list, item => item.FileName.EndsWith(".psp", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(list, item => item.FileName == "libScePsml.sprx" && item.IsRequired);
            Assert.Contains(list, item => item.FileName == "libSceSaveData.native.sprx" && item.IsRequired);

            // Background check: k9.psp was automatically staged to root in background
            Assert.True(File.Exists(Path.Combine(tempDir, "k9.psp")), "k9.psp must be staged silently in background");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            if (Directory.Exists(bundledFakelib)) Directory.Delete(bundledFakelib, recursive: true);
        }
    }

    [Fact]
    public void SyncFakelibLive_CanStageAndRemoveFakelibs_AndBlocksPsp()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"fpkg-live-{Guid.NewGuid():N}");
        string bundledFakelib = Path.Combine(Path.GetTempPath(), $"bundled-live-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(bundledFakelib);

        try
        {
            File.WriteAllBytes(Path.Combine(bundledFakelib, "libSceAmpr.sprx"), new byte[] { 1, 2, 3, 4 });
            File.WriteAllBytes(Path.Combine(bundledFakelib, "k9.psp"), new byte[] { 5, 6, 7, 8 });

            // 1. Sync enable -> stages to fakelib/
            bool staged = ProsperoFself.SyncFakelibLive(tempDir, "libSceAmpr.sprx", true, bundledFakelib);
            Assert.True(staged);
            Assert.True(File.Exists(Path.Combine(tempDir, "fakelib", "libSceAmpr.sprx")));

            // 2. Sync disable -> removes from fakelib/
            bool removed = ProsperoFself.SyncFakelibLive(tempDir, "libSceAmpr.sprx", false, bundledFakelib);
            Assert.True(removed);
            Assert.False(File.Exists(Path.Combine(tempDir, "fakelib", "libSceAmpr.sprx")));

            // 3. Attempt to touch .psp -> blocked
            bool pspTouch = ProsperoFself.SyncFakelibLive(tempDir, "k9.psp", false, bundledFakelib);
            Assert.False(pspTouch, "SyncFakelibLive must reject touching .psp files");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            if (Directory.Exists(bundledFakelib)) Directory.Delete(bundledFakelib, recursive: true);
        }
    }

    [Fact]
    public void RecursiveMakeFself_WithExcludedFakelibs_SkipsExcludedStubs()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"fpkg-ex-{Guid.NewGuid():N}");
        string bundledFakelib = Path.Combine(Path.GetTempPath(), $"bundled-ex-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(bundledFakelib);

        try
        {
            File.WriteAllBytes(Path.Combine(bundledFakelib, "libSceAmpr.sprx"), new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(bundledFakelib, "libScePsml.sprx"), new byte[] { 4, 5, 6 });
            File.WriteAllBytes(Path.Combine(bundledFakelib, "k9.psp"), new byte[] { 7, 8, 9 });

            byte[] elfBytes = CreateDummyElf(["libSceAmpr.sprx", "libScePsml.sprx", "libkernel.sprx"]);
            File.WriteAllBytes(Path.Combine(tempDir, "eboot.bin"), elfBytes);

            // User excludes libSceAmpr.sprx
            string[] excluded = ["libSceAmpr.sprx"];
            int converted = ProsperoFself.RecursiveMakeFself(tempDir, null, default, 0x0400000000000000uL, bundledFakelib, excluded);
            Assert.Equal(1, converted);

            // libSceAmpr.sprx was excluded -> not staged
            Assert.False(File.Exists(Path.Combine(tempDir, "fakelib", "libSceAmpr.sprx")));
            // libScePsml.sprx was included -> staged
            Assert.True(File.Exists(Path.Combine(tempDir, "fakelib", "libScePsml.sprx")));
            // k9.psp is companion for libScePsml -> always staged in background
            Assert.True(File.Exists(Path.Combine(tempDir, "k9.psp")));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            if (Directory.Exists(bundledFakelib)) Directory.Delete(bundledFakelib, recursive: true);
        }
    }

    [Fact]
    public void PPSA22327_ForzaHorizon5_Dataset_DetectsAllRequiredCompatibilityModules()
    {
        string datasetDir = GetDownloadsDatasetPath("[DLPSGAME.COM]-_4xx[PPSA22327][01.685.672](@BestPig) (Sound fix by idlesauce)");
        string ebootPath = Path.Combine(datasetDir, "eboot.bin");
        if (!File.Exists(ebootPath)) return;

        byte[] ebootBytes = File.ReadAllBytes(ebootPath);
        byte[] elfBytes;
        if (ProsperoFself.TryUnfself(ebootBytes, out byte[]? unpacked) && unpacked != null)
        {
            elfBytes = unpacked;
        }
        else
        {
            elfBytes = ebootBytes;
        }

        var detected = ProsperoFself.DetectImportedCompatibilityModules(elfBytes);

        // Required modules in Forza Horizon 5 (Playground Games engine / FMOD sound)
        Assert.Contains("libSceAgc.sprx", detected);
        Assert.Contains("libSceAgcDriver.sprx", detected);
        Assert.Contains("libSceAppContent.sprx", detected);
        Assert.Contains("libSceFiber.sprx", detected);
        Assert.Contains("libScePsml.sprx", detected);
        Assert.Contains("libSceSaveData.native.sprx", detected);
        Assert.Contains("libc.prx", detected);
        Assert.Contains("right.sprx", detected);

        // Target directories
        Assert.Equal("fakelib", ProsperoFself.GetModuleTargetSubdirectory("libSceAgc.sprx"));
        Assert.Equal("fakelib", ProsperoFself.GetModuleTargetSubdirectory("libSceAgcDriver.sprx"));
        Assert.Equal("fakelib", ProsperoFself.GetModuleTargetSubdirectory("libSceAppContent.sprx"));
        Assert.Equal("fakelib", ProsperoFself.GetModuleTargetSubdirectory("libSceFiber.sprx"));
        Assert.Equal("fakelib", ProsperoFself.GetModuleTargetSubdirectory("libScePsml.sprx"));
        Assert.Equal("fakelib", ProsperoFself.GetModuleTargetSubdirectory("libSceSaveData.native.sprx"));
        Assert.Equal("sce_module", ProsperoFself.GetModuleTargetSubdirectory("libc.prx"));
        Assert.Equal("sce_sys/about", ProsperoFself.GetModuleTargetSubdirectory("right.sprx"));

        // Sound fix PRXs by idlesauce must be valid FSELF / ELF
        string[] prxNames = ["libfmod.prx", "libfmodstudio.prx", "PG.FMODPlugins_PS5.prx", "act.prx", "logiWheel.prx"];
        foreach (var prxName in prxNames)
        {
            string prxPath = Path.Combine(datasetDir, prxName);
            if (File.Exists(prxPath))
            {
                byte[] prxBytes = File.ReadAllBytes(prxPath);
                Assert.True(ProsperoFself.IsSelf(prxBytes) || ProsperoFself.IsElf(prxBytes), $"Plugin {prxName} must be valid ELF/FSELF");
            }
        }

        // Test GetDetectedFakelibStatus on this dataset
        string bundledFakelib = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../gui/Resources/fakelib"));
        if (!Directory.Exists(bundledFakelib))
        {
            bundledFakelib = Path.GetFullPath("gui/Resources/fakelib");
        }

        var statusList = ProsperoFself.GetDetectedFakelibStatus(datasetDir, 0x0400000000000000uL, bundledFakelib);
        Assert.NotEmpty(statusList);

        // Ensure key modules are present and correctly marked
        Assert.Contains(statusList, item => item.FileName == "libSceAgc.sprx");
        Assert.Contains(statusList, item => item.FileName == "libSceAgcDriver.sprx");
        Assert.Contains(statusList, item => item.FileName == "libc.prx");
        Assert.Contains(statusList, item => item.FileName == "libScePsml.sprx");
        Assert.Contains(statusList, item => item.FileName == "libSceFiber.sprx");
        Assert.Contains(statusList, item => item.FileName == "libSceSaveData.native.sprx");

        // CRITICAL Rule: .psp companion must never be returned in the UI status list
        Assert.DoesNotContain(statusList, item => item.FileName.EndsWith(".psp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SyncEbootWithFakelibs_BypassesAndRestoresModuleImports()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"fpkg-sync-{Guid.NewGuid():N}");
        string bundledFakelib = Path.Combine(Path.GetTempPath(), $"bundled-sync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(bundledFakelib);

        try
        {
            File.WriteAllBytes(Path.Combine(bundledFakelib, "libSceAmpr.sprx"), new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(bundledFakelib, "libScePsml.sprx"), new byte[] { 4, 5, 6 });

            // Create eboot.bin importing AMPR and PSML
            byte[] elfBytes = CreateDummyElf(["libSceAmpr.sprx", "libScePsml.sprx", "libkernel.sprx"]);
            File.WriteAllBytes(Path.Combine(tempDir, "eboot.bin"), elfBytes);

            // Step 1: User chooses to enable ONLY PSML (AMPR is disabled/bypassed)
            var result1 = ProsperoFself.SyncEbootWithFakelibs(tempDir, ["libScePsml.sprx"], 0x0400000000000000uL, bundledFakelib);
            Assert.True(result1.Success);
            Assert.Contains("libSceAmpr.sprx", result1.BypassedModules);
            Assert.False(File.Exists(Path.Combine(tempDir, "fakelib", "libSceAmpr.sprx")), "AMPR must not be staged");
            Assert.True(File.Exists(Path.Combine(tempDir, "fakelib", "libScePsml.sprx")), "PSML must be staged");

            // Verify eboot.bin now has AMPR bypassed
            byte[] curEboot = File.ReadAllBytes(Path.Combine(tempDir, "eboot.bin"));
            Assert.DoesNotContain(Encoding.ASCII.GetBytes("libSceAmpr.sprx"), curEboot);
            Assert.Contains(Encoding.ASCII.GetBytes("libScePsml.sprx"), curEboot);
            Assert.True(ProsperoFself.IsModuleBypassedInEboot(tempDir, "libSceAmpr.sprx"));

            // Check detected status reflects bypass
            var statusList = ProsperoFself.GetDetectedFakelibStatus(tempDir, 0x0400000000000000uL, bundledFakelib);
            var amprItem = statusList.FirstOrDefault(f => f.FileName == "libSceAmpr.sprx");
            Assert.NotNull(amprItem);
            Assert.True(amprItem.IsEbootBypassed);

            // Step 2: User re-enables AMPR
            var result2 = ProsperoFself.SyncEbootWithFakelibs(tempDir, ["libSceAmpr.sprx", "libScePsml.sprx"], 0x0400000000000000uL, bundledFakelib);
            Assert.True(result2.Success);
            Assert.Contains("libSceAmpr.sprx", result2.RestoredModules);
            Assert.True(File.Exists(Path.Combine(tempDir, "fakelib", "libSceAmpr.sprx")), "AMPR must now be staged");

            // Verify eboot.bin restored AMPR
            byte[] restoredEboot = File.ReadAllBytes(Path.Combine(tempDir, "eboot.bin"));
            Assert.Contains(Encoding.ASCII.GetBytes("libSceAmpr.sprx"), restoredEboot);
            Assert.False(ProsperoFself.IsModuleBypassedInEboot(tempDir, "libSceAmpr.sprx"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            if (Directory.Exists(bundledFakelib)) Directory.Delete(bundledFakelib, recursive: true);
        }
    }

    [Fact]
    public void GetDetectedFakelibStatus_FlagsAnomaliesWhenStubsMissing()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"fpkg-anomaly-{Guid.NewGuid():N}");
        string bundledFakelib = Path.Combine(Path.GetTempPath(), $"bundled-anom-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(bundledFakelib);

        try
        {
            File.WriteAllBytes(Path.Combine(bundledFakelib, "libSceAmpr.sprx"), new byte[] { 1, 2, 3 });

            // eboot.bin imports AMPR, but stub is NOT staged on disk
            byte[] elfBytes = CreateDummyElf(["libSceAmpr.sprx", "libkernel.sprx"]);
            File.WriteAllBytes(Path.Combine(tempDir, "eboot.bin"), elfBytes);

            var status = ProsperoFself.GetDetectedFakelibStatus(tempDir, 0x0400000000000000uL, bundledFakelib);
            var ampr = status.FirstOrDefault(f => f.FileName == "libSceAmpr.sprx");
            Assert.NotNull(ampr);
            Assert.True(ampr.IsRequired);
            Assert.False(ampr.IsStaged);
            Assert.True(ampr.HasAnomaly, "Missing required stub must be flagged as an anomaly");
            Assert.Contains("Missing stub", ampr.AnomalyReason);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            if (Directory.Exists(bundledFakelib)) Directory.Delete(bundledFakelib, recursive: true);
        }
    }

    [Fact]
    public void StageAllMissingStubs_CopiesAllMissingRequiredStubsIntoFolder()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"fpkg-stage-all-{Guid.NewGuid():N}");
        string bundledFakelib = Path.Combine(Path.GetTempPath(), $"bundled-stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(bundledFakelib);

        try
        {
            File.WriteAllBytes(Path.Combine(bundledFakelib, "libSceAmpr.sprx"), new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(bundledFakelib, "libScePsml.sprx"), new byte[] { 4, 5, 6 });

            // eboot.bin imports both AMPR and PSML, neither staged initially
            byte[] elfBytes = CreateDummyElf(["libSceAmpr.sprx", "libScePsml.sprx", "libkernel.sprx"]);
            File.WriteAllBytes(Path.Combine(tempDir, "eboot.bin"), elfBytes);

            int stagedCount = ProsperoFself.StageAllMissingStubs(tempDir, bundledFakelib, 0x0400000000000000uL);
            Assert.Equal(2, stagedCount);

            Assert.True(File.Exists(Path.Combine(tempDir, "fakelib", "libSceAmpr.sprx")));
            Assert.True(File.Exists(Path.Combine(tempDir, "fakelib", "libScePsml.sprx")));

            var status = ProsperoFself.GetDetectedFakelibStatus(tempDir, 0x0400000000000000uL, bundledFakelib);
            Assert.All(status.Where(f => f.FileName == "libSceAmpr.sprx" || f.FileName == "libScePsml.sprx"), item =>
            {
                Assert.True(item.IsStaged);
                Assert.False(item.IsMissingStub);
            });
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            if (Directory.Exists(bundledFakelib)) Directory.Delete(bundledFakelib, recursive: true);
        }
    }
}


