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
    public void IsModuleMissingOnTargetSdk_CorrectlyEvaluatesFirmwarePresence(string modFile, ulong targetSdk, bool expectedMissing)
    {
        bool isMissing = ProsperoFself.IsModuleMissingOnTargetSdk(modFile, targetSdk);
        Assert.Equal(expectedMissing, isMissing);
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

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr objc_getClass(string name);

    [Fact]
    public void Test_Mac_ObjectiveC_NSString_PInvoke()
    {
        if (!OperatingSystem.IsMacOS()) return;

        IntPtr cls_NSString = objc_getClass("NSString");
        Assert.NotEqual(IntPtr.Zero, cls_NSString);
    }
}

