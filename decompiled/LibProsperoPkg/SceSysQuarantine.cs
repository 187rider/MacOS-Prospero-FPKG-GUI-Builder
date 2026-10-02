using System;
using System.Collections.Generic;
using System.IO;

namespace LibProsperoPkg;

/// <summary>
/// Safely sets aside stale or conflicting retail dump artifacts (playgo-*, retail license.*,
/// origin/target deltas, backup files) so LibProsperoPkg generates matching fresh PlayGo tables
/// and a valid debug license. All quarantined files are restored in Dispose().
/// </summary>
public sealed class SceSysQuarantine : IDisposable
{
    private static readonly string[] SceSysStaleNames =
    [
        "license.dat", "license.info",
        "playgo-chunk.dat", "playgo-hash-table.dat", "playgo-ficm.dat",
        "playgo-scenario.json", "playgo-manifest.xml", "playgo-chunk.crc",
        "origin-param.json", "target-param.json",
        "origin-deltainfo.dat", "target-deltainfo.dat",
        "origin-relocinfo.dat", "target-relocinfo.dat",
        "disc_info.dat", "ext_info.dat", "imagedigs.dat",
        "pfsimage.xml", "pfs-region-hints.json", "param.sfo", "param_cp_values.json"
    ];

    private readonly string _sourceDir;
    private readonly string _backupDir;
    private readonly List<(string SourcePath, string BackupPath)> _movedFiles = new();
    private bool _disposed;

    private SceSysQuarantine(string sourceDir, string backupDir)
    {
        _sourceDir = sourceDir;
        _backupDir = backupDir;
    }

    public static SceSysQuarantine Apply(string sourceDir, Action<string>? logger = null, bool disabled = false)
    {
        if (disabled)
        {
            return new SceSysQuarantine(sourceDir, "");
        }

        string backupDir = Path.Combine(Path.GetTempPath(), $"fpkg-quarantine-{Guid.NewGuid():N}");
        Directory.CreateDirectory(backupDir);

        var q = new SceSysQuarantine(sourceDir, backupDir);
        string sceSysDir = Path.Combine(sourceDir, "sce_sys");

        if (Directory.Exists(sceSysDir))
        {
            foreach (var name in SceSysStaleNames)
            {
                string path = Path.Combine(sceSysDir, name);
                if (File.Exists(path))
                {
                    string dest = Path.Combine(backupDir, "sce_sys_" + name);
                    try
                    {
                        File.Move(path, dest, overwrite: true);
                        q._movedFiles.Add((path, dest));
                        logger?.Invoke($"[quarantine] Staged aside conflicting retail artifact: sce_sys/{name}");
                    }
                    catch (Exception ex)
                    {
                        logger?.Invoke($"[quarantine] Warning: could not move sce_sys/{name}: {ex.Message}");
                    }
                }
            }

            // Check and quarantine any corrupted PNG files (e.g. invalid header, truncated data)
            foreach (var pngFile in Directory.EnumerateFiles(sceSysDir, "*.png", SearchOption.TopDirectoryOnly))
            {
                bool isCorrupt = false;
                try
                {
                    byte[] sig = new byte[8];
                    using var fs = new FileStream(pngFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    if (fs.Length < 16 || fs.Read(sig, 0, 8) != 8 ||
                        sig[0] != 0x89 || sig[1] != 0x50 || sig[2] != 0x4E || sig[3] != 0x47 ||
                        sig[4] != 0x0D || sig[5] != 0x0A || sig[6] != 0x1A || sig[7] != 0x0A)
                    {
                        isCorrupt = true;
                    }
                }
                catch { isCorrupt = true; }

                if (isCorrupt)
                {
                    string fname = Path.GetFileName(pngFile);
                    string dest = Path.Combine(backupDir, "corrupted_" + fname);
                    try
                    {
                        File.Move(pngFile, dest, overwrite: true);
                        q._movedFiles.Add((pngFile, dest));
                        logger?.Invoke($"[quarantine] Staged aside corrupted image file: sce_sys/{fname}");
                    }
                    catch (Exception ex)
                    {
                        logger?.Invoke($"[quarantine] Warning: could not move corrupted sce_sys/{fname}: {ex.Message}");
                    }
                }
            }
        }

        // Also quarantine tree-wide backup files (.bak, .esbak, .gp4, .gp5) that leak into inner image
        try
        {
            var rootDirInfo = new DirectoryInfo(sourceDir);
            foreach (var fi in rootDirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (fi.FullName.StartsWith(backupDir, StringComparison.OrdinalIgnoreCase)) continue;
                string ext = fi.Extension.ToLowerInvariant();
                if (ext is ".esbak" or ".gp4" or ".gp5" or ".bak" || fi.Name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                {
                    string rel = Path.GetRelativePath(sourceDir, fi.FullName);
                    string dest = Path.Combine(backupDir, "treewide_" + Guid.NewGuid().ToString("N") + "_" + fi.Name);
                    try
                    {
                        File.Move(fi.FullName, dest, overwrite: true);
                        q._movedFiles.Add((fi.FullName, dest));
                        logger?.Invoke($"[quarantine] Staged aside loose dump/editor file: {rel}");
                    }
                    catch { }
                }
            }
        }
        catch { }

        return q;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var (sourcePath, backupPath) in _movedFiles)
        {
            try
            {
                if (File.Exists(backupPath))
                {
                    string? parent = Path.GetDirectoryName(sourcePath);
                    if (parent != null) Directory.CreateDirectory(parent);
                    File.Move(backupPath, sourcePath, overwrite: true);
                }
            }
            catch
            {
            }
        }

        try
        {
            if (Directory.Exists(_backupDir))
            {
                Directory.Delete(_backupDir, recursive: true);
            }
        }
        catch
        {
        }
    }
}
