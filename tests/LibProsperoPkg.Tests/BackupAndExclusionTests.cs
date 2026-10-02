using System;
using System.IO;
using LibProsperoPkg;
using LibProsperoPkg.Content;
using LibProsperoPkg.PFS;
using Xunit;

namespace LibProsperoPkg.Tests;

public class BackupAndExclusionTests
{
    [Fact]
    public void DefaultExcludeSuffixes_ContainsBak()
    {
        Assert.Contains(".bak", ProsperoPfsLayoutOptions.DefaultExcludeFileSuffixes, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void SceSysQuarantine_StagesAsideBakFiles_AndRestoresOnDispose()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"fpkg-test-bak-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string regularFile = Path.Combine(tempDir, "eboot.bin");
            string backupFile = Path.Combine(tempDir, "eboot.bin.bak");
            string subDir = Path.Combine(tempDir, "sub");
            Directory.CreateDirectory(subDir);
            string subBak = Path.Combine(subDir, "module.prx.bak");

            File.WriteAllText(regularFile, "regular");
            File.WriteAllText(backupFile, "backup");
            File.WriteAllText(subBak, "sub backup");

            using (var q = SceSysQuarantine.Apply(tempDir))
            {
                // While quarantined, .bak files should be staged aside (not in source tree)
                Assert.True(File.Exists(regularFile));
                Assert.False(File.Exists(backupFile));
                Assert.False(File.Exists(subBak));
            }

            // After dispose, .bak files should be restored to their original locations
            Assert.True(File.Exists(regularFile));
            Assert.True(File.Exists(backupFile));
            Assert.True(File.Exists(subBak));
            Assert.Equal("backup", File.ReadAllText(backupFile));
            Assert.Equal("sub backup", File.ReadAllText(subBak));
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
    public void PatchElfProcParam_PatchesSdkFields()
    {
        byte[] buffer = new byte[64];
        buffer[0] = 0xBF; buffer[1] = 0xF4; buffer[2] = 0x13; buffer[3] = 0x3C;
        buffer[4] = 0x03; buffer[8] = 0x01;
        buffer[10] = 0x00; buffer[11] = 0x08;

        buffer[32] = 0x4F; buffer[33] = 0x52; buffer[34] = 0x42; buffer[35] = 0x49;
        buffer[36] = 0x05; buffer[40] = 0x01;
        buffer[42] = 0x59; buffer[43] = 0x13; buffer[44] = 0x35; buffer[45] = 0x00;

        int count = ProsperoFself.PatchElfProcParam(buffer, 0x0400000000000000uL);
        Assert.Equal(2, count);

        Assert.Equal(0x04, buffer[10]);
        Assert.Equal(0x09, buffer[11]);
        Assert.Equal(0x31, buffer[12]);
        Assert.Equal(0x00, buffer[13]);

        Assert.Equal(0x04, buffer[42]);
        Assert.Equal(0x09, buffer[43]);
        Assert.Equal(0x31, buffer[44]);
        Assert.Equal(0x00, buffer[45]);
    }

    [Fact]
    public void PatchElfAmprBypass_PatchesCallToJump()
    {
        byte[] code = new byte[64];
        byte[] pattern = new byte[]
        {
            0x48, 0x8D, 0x75, 0xE4,
            0xE8, 0x81, 0x1F, 0x6D, 0x02,
            0x85, 0xC0, 0x75, 0x08,
            0x83, 0x7D, 0xE4, 0x02,
            0xB0, 0x01, 0x74, 0x5F
        };
        Buffer.BlockCopy(pattern, 0, code, 0, pattern.Length);

        int count = ProsperoFself.PatchElfAmprBypass(code);
        Assert.Equal(1, count);

        Assert.Equal(0xE9, code[4]);
        Assert.Equal(0x0C, code[5]);
        Assert.Equal(0x00, code[6]);
        Assert.Equal(0x00, code[7]);
        Assert.Equal(0x00, code[8]);
    }

    [Fact]
    public void PatchElfSymbolVersions_PatchesAmprInitSymbol()
    {
        byte[] buffer = new byte[128];
        byte[] oldSym = System.Text.Encoding.ASCII.GetBytes("86f0wFtxn0k#u#s");
        Buffer.BlockCopy(oldSym, 0, buffer, 10, oldSym.Length);

        int count = ProsperoFself.PatchElfSymbolVersions(buffer, 0x0400000000000000uL);
        Assert.Equal(1, count);

        string patched = System.Text.Encoding.ASCII.GetString(buffer, 10, oldSym.Length);
        Assert.Equal("04AjkP0jO9U#v#G", patched);
    }

    [Fact]
    public void PatchElfExecutableBackport_PatchesPrivacyAndStatusCalls()
    {
        byte[] code = new byte[128];
        // Privacy call pattern: BE 01 00 00 00 4C 89 F7 E8 11 22 33 44 8B 3D
        byte[] priv = new byte[] { 0xBE, 0x01, 0x00, 0x00, 0x00, 0x4C, 0x89, 0xF7, 0xE8, 0x11, 0x22, 0x33, 0x44, 0x8B, 0x3D };
        Buffer.BlockCopy(priv, 0, code, 0, priv.Length);

        // Status call pattern: 4C 89 A5 78 FA FF FF E8 11 22 33 44 3D 08 00 9F 80 74
        byte[] stat = new byte[] { 0x4C, 0x89, 0xA5, 0x78, 0xFA, 0xFF, 0xFF, 0xE8, 0x11, 0x22, 0x33, 0x44, 0x3D, 0x08, 0x00, 0x9F, 0x80, 0x74 };
        Buffer.BlockCopy(stat, 0, code, 30, stat.Length);

        int count = ProsperoFself.PatchElfExecutableBackport(code, 0x0400000000000000uL);
        Assert.Equal(2, count);

        // Privacy call replaced with 5 NOPs
        for (int i = 8; i < 13; i++)
        {
            Assert.Equal(0x90, code[i]);
        }

        // Status call replaced with B8 08 00 9F 80
        Assert.Equal(0xB8, code[37]);
        Assert.Equal(0x08, code[38]);
        Assert.Equal(0x00, code[39]);
        Assert.Equal(0x9F, code[40]);
        Assert.Equal(0x80, code[41]);
    }

    [Fact]
    public void PatchElfExecutableBackport_InlinesDeviceQuerySuccess()
    {
        byte[] code = new byte[64];
        // 02 00 00 00 0F 44 F8 E8 11 22 33 44 85 C0 74 1D
        byte[] pattern = new byte[] { 0x02, 0x00, 0x00, 0x00, 0x0F, 0x44, 0xF8, 0xE8, 0x11, 0x22, 0x33, 0x44, 0x85, 0xC0, 0x74, 0x1D };
        Buffer.BlockCopy(pattern, 0, code, 0, pattern.Length);

        int count = ProsperoFself.PatchElfExecutableBackport(code, 0x0400000000000000uL);
        Assert.Equal(1, count);

        // e8 ?? ?? ?? ?? replaced with 31 c0 90 90 90
        Assert.Equal(0x31, code[7]);
        Assert.Equal(0xC0, code[8]);
        Assert.Equal(0x90, code[9]);
        Assert.Equal(0x90, code[10]);
        Assert.Equal(0x90, code[11]);
    }

    [Fact]
    public void PatchElfExecutableBackport_PatchesLibcStackSizeConstraint()
    {
        byte[] code = new byte[64];
        // BA 03 00 00 00 B9 00 80 00 00
        byte[] pattern = new byte[] { 0xBA, 0x03, 0x00, 0x00, 0x00, 0xB9, 0x00, 0x80, 0x00, 0x00 };
        Buffer.BlockCopy(pattern, 0, code, 10, pattern.Length);

        int count = ProsperoFself.PatchElfExecutableBackport(code, 0x0400000000000000uL);
        Assert.Equal(1, count);
        // B9 00 80 00 00 replaced with 31 C9 90 90 90
        Assert.Equal(0x31, code[15]);
        Assert.Equal(0xC9, code[16]);
        Assert.Equal(0x90, code[17]);
    }

    [Fact]
    public void PatchElfSymbolVersions_PatchesLibcSymbol()
    {
        byte[] code = new byte[128];
        byte[] sym = System.Text.Encoding.ASCII.GetBytes("4h6F1LLbTiw#A#B");
        Buffer.BlockCopy(sym, 0, code, 10, sym.Length);
        int count = ProsperoFself.PatchElfSymbolVersions(code, 0x0400000000000000uL);
        Assert.Equal(1, count);
    }

    [Fact]
    public void Backport_TargetApp0_MatchesReference()
    {
        string refEboot = Environment.GetEnvironmentVariable("PS5_TEST_REF_EBOOT") ?? "";
        string targetDir = Environment.GetEnvironmentVariable("PS5_TEST_WOLVERINE_DIR") ?? "";
        if (string.IsNullOrEmpty(refEboot) || string.IsNullOrEmpty(targetDir) || !File.Exists(refEboot) || !Directory.Exists(targetDir)) return;

        int converted = ProsperoFself.RecursiveMakeFself(targetDir, null, default, 0x0400000000000000uL);
        Assert.True(converted > 0);

        string targetEboot = Path.Combine(targetDir, "eboot.bin");
        Assert.Equal(new FileInfo(refEboot).Length, new FileInfo(targetEboot).Length);
    }

    [Fact]
    public void SceSysQuarantine_WhenDisabled_DoesNotMoveFiles()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"fpkg-test-quarantine-disabled-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string sceSys = Path.Combine(tempDir, "sce_sys");
            Directory.CreateDirectory(sceSys);
            string testFile = Path.Combine(sceSys, "license.dat");
            File.WriteAllText(testFile, "test-license-data");

            using (var q = SceSysQuarantine.Apply(tempDir, null, disabled: true))
            {
                Assert.True(File.Exists(testFile));
                Assert.Equal("test-license-data", File.ReadAllText(testFile));
            }

            Assert.True(File.Exists(testFile));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void Test_PPSA09826_SceModuleFiles_AreSigned()
    {
        string targetDir = Environment.GetEnvironmentVariable("PS5_TEST_PPSA09826_DIR") ?? "";
        if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir)) return;
        int count = ProsperoFself.RecursiveMakeFself(targetDir, null, default, 0x0400000000000000uL);
        Assert.True(count > 0);
    }

}
