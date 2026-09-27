using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using LibProsperoPkg;
using LibProsperoPkg.PFS;
using LibProsperoPkg.PKG;
using Photino.NET;

namespace LibProsperoPkgGui;

public sealed class AppLogic
{
    private readonly PhotinoWindow _window;
    private static readonly Regex ContentIdPattern = new Regex(@"^[A-Z]{2}[0-9]{4}-[A-Z]{4}[0-9]{5}_[0-9]{2}-[A-Za-z0-9]{16}$", RegexOptions.Compiled);
    private static readonly Regex VersionPattern = new Regex(@"^[0-9]{2}\.[0-9]{2}$", RegexOptions.Compiled);
    private CancellationTokenSource? _buildCts;
    private int _currentStage = 1;

    public AppLogic(PhotinoWindow window)
    {
        _window = window;
    }

    public void CleanupTempFiles()
    {
        try
        {
            _buildCts?.Cancel();
        }
        catch
        {
        }
        LibProsperoPkg.Util.BuildCleaner.CleanupAll();
    }

    public void HandleMessage(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            string action = root.GetProperty("action").GetString() ?? "";

            switch (action)
            {
                case "browseFolder":
                    HandleBrowseFolder(root);
                    break;
                case "browseFile":
                    HandleBrowseFile(root);
                    break;
                case "scanMetadata":
                    HandleScanMetadata(root);
                    break;
                case "startBuild":
                    {
                        var cloned = root.Clone();
                        _ = Task.Run(() => HandleStartBuild(cloned));
                    }
                    break;
                case "inspectPkg":
                    {
                        var cloned = root.Clone();
                        _ = Task.Run(() => HandleInspectPkg(cloned));
                    }
                    break;
                case "verifyPkg":
                    {
                        var cloned = root.Clone();
                        _ = Task.Run(() => HandleVerifyPkg(cloned));
                    }
                    break;
                case "extractPkg":
                    {
                        var cloned = root.Clone();
                        _ = Task.Run(() => HandleExtractPkg(cloned));
                    }
                    break;
                case "loadPackageInfo":
                    {
                        var cloned = root.Clone();
                        _ = Task.Run(() => HandleLoadPackageInfo(cloned));
                    }
                    break;
                case "runQuickVerify":
                    {
                        var cloned = root.Clone();
                        _ = Task.Run(() => HandleRunQuickVerify(cloned));
                    }
                    break;
                case "unpackPkg":
                    {
                        var cloned = root.Clone();
                        _ = Task.Run(() => HandleUnpackPkg(cloned));
                    }
                    break;
                case "openFolder":
                    HandleOpenFolder(root);
                    break;
                case "cancelBuild":
                    HandleCancelBuild();
                    break;
                default:
                    SendResponse("error", new { message = $"Unknown action: {action}" });
                    break;
            }
        }
        catch (Exception ex)
        {
            SendResponse("error", new { message = ex.Message });
        }
    }

    private void SendResponse(string type, object payload)
    {
        string json = JsonSerializer.Serialize(new { type, payload });
        _window.SendWebMessage(json);
    }

    private void HandleBrowseFolder(JsonElement root)
    {
        string target = root.TryGetProperty("target", out var t) ? t.GetString() ?? "source" : "source";
        string title = target switch
        {
            "source" => "Select PS5 Source Folder",
            "unpackDir" => "Select Output Directory for Unpacking",
            _ => "Select Output Folder"
        };

        string[] results = _window.ShowOpenFolder(title);
        if (results != null && results.Length > 0 && !string.IsNullOrWhiteSpace(results[0]))
        {
            string path = Path.GetFullPath(results[0]);
            SendResponse("folderSelected", new { target, path });
            if (target == "source")
            {
                ScanAndSendMetadata(path);
            }
        }
    }

    private void HandleBrowseFile(JsonElement root)
    {
        string target = root.TryGetProperty("target", out var t) ? t.GetString() ?? "pkg" : "pkg";
        string title = target == "unpackPkg" ? "Select PS5 Package File (.pkg)" : "Select PS5 Package File";
        var filters = new (string Name, string[] Extensions)[]
        {
            ("PS5 Package (*.pkg)", new[] { "pkg" }),
            ("All Files", new[] { "*" })
        };

        string[] results = _window.ShowOpenFile(title, filters: filters);
        if (results != null && results.Length > 0 && !string.IsNullOrWhiteSpace(results[0]))
        {
            string path = Path.GetFullPath(results[0]);
            SendResponse("fileSelected", new { target, path });
        }
    }

    private void HandleScanMetadata(JsonElement root)
    {
        string path = root.GetProperty("path").GetString() ?? "";
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            ScanAndSendMetadata(Path.GetFullPath(path));
        }
    }

    private void ScanAndSendMetadata(string sourceDir)
    {
        string paramJsonPath = Path.Combine(sourceDir, "sce_sys", "param.json");
        if (!File.Exists(paramJsonPath) && File.Exists(Path.Combine(sourceDir, "param.json")))
        {
            paramJsonPath = Path.Combine(sourceDir, "param.json");
        }

        string? contentId = null;
        string? title = null;
        string? version = null;
        bool hasParamJson = File.Exists(paramJsonPath);

        if (hasParamJson)
        {
            try
            {
                using var jsonDoc = JsonDocument.Parse(File.ReadAllText(paramJsonPath, Encoding.UTF8));
                var root = jsonDoc.RootElement;
                if (root.TryGetProperty("contentId", out var cVal))
                {
                    contentId = cVal.GetString();
                }
                if (root.TryGetProperty("contentVersion", out var vVal))
                {
                    version = NormalizeVersion(vVal.GetString());
                }
                title = ReadLocalizedTitle(root);
            }
            catch
            {
                // Ignore parse errors, fall back to defaults
            }
        }

        string[] playgoFiles = { "playgo-chunk.dat", "playgo-hash-table.dat", "playgo-ficm.dat" };
        int existingPlaygo = playgoFiles.Count(f => File.Exists(Path.Combine(sourceDir, "sce_sys", f)));

        SendResponse("metadataScanned", new
        {
            hasParamJson,
            contentId = contentId ?? "UP0000-PPSA00000_00-0000000000000000",
            title = title ?? Path.GetFileName(sourceDir.TrimEnd('/', '\\')),
            version = version ?? "01.00",
            titleId = (contentId != null && contentId.Length >= 16) ? contentId.Substring(7, 9) : "PPSA00000",
            playgoStatus = existingPlaygo == 3 ? "All 3 PlayGo files detected" : (existingPlaygo > 0 ? "Partial PlayGo files detected" : "Automatic 1-chunk PlayGo generation")
        });
    }

    private void HandleStartBuild(JsonElement root)
    {
        try
        {
            string source = root.GetProperty("source").GetString() ?? "";
            string output = root.GetProperty("output").GetString() ?? "";
            string contentId = root.GetProperty("contentId").GetString() ?? "";
            string title = root.GetProperty("title").GetString() ?? "";
            string version = root.GetProperty("version").GetString() ?? "01.00";
            string passcode = root.GetProperty("passcode").GetString() ?? "00000000000000000000000000000000";
            string modeStr = root.GetProperty("mode").GetString() ?? "app";
            string imageModeStr = root.GetProperty("imageMode").GetString() ?? "plaintext";
            string compressionStr = root.GetProperty("compression").GetString() ?? "none";
            int chunks = root.TryGetProperty("chunks", out var ch) ? ch.GetInt32() : 1;
            bool deterministic = !root.TryGetProperty("deterministic", out var det) || det.GetBoolean();
            bool verify = !root.TryGetProperty("verify", out var ver) || ver.GetBoolean();
            bool autoFself = !root.TryGetProperty("autoFself", out var af) || af.GetBoolean();
            bool autoBackport = !root.TryGetProperty("autoBackport", out var ab) || ab.GetBoolean();

            if (!Directory.Exists(source))
            {
                SendResponse("buildError", new { message = "Source folder does not exist." });
                return;
            }

            source = Path.GetFullPath(source);
            output = Path.GetFullPath(output);
            Directory.CreateDirectory(output);

            if (output.Equals(source, StringComparison.OrdinalIgnoreCase) ||
                output.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                SendResponse("buildError", new { message = "Output directory cannot be inside the source directory." });
                return;
            }

            if (!ContentIdPattern.IsMatch(contentId))
            {
                SendResponse("buildError", new { message = "Invalid Content ID format. Must match UP0000-PPSA00000_00-XXXXXXXXXXXXXXXX (36 characters)." });
                return;
            }

            // If files were extracted to root without an sce_sys folder, automatically ensure sce_sys/ has them
            string sceSysDir = Path.Combine(source, "sce_sys");
            Directory.CreateDirectory(sceSysDir);

            string[] sysFileNames = { "param.json", "icon0.png", "icon0.dds", "pic0.png", "pic0.dds" };
            foreach (var sfn in sysFileNames)
            {
                string rootFile = Path.Combine(source, sfn);
                string targetFile = Path.Combine(sceSysDir, sfn);
                if (File.Exists(rootFile) && !File.Exists(targetFile))
                {
                    File.Copy(rootFile, targetFile, overwrite: true);
                }
            }

            // Automatically patch param.json for PS5 compatibility
            string targetParamJson = Path.Combine(sceSysDir, "param.json");
            if (File.Exists(targetParamJson))
            {
                try
                {
                    string jsonContent = File.ReadAllText(targetParamJson);
                    var jsonNode = System.Text.Json.Nodes.JsonNode.Parse(jsonContent);
                    if (jsonNode is System.Text.Json.Nodes.JsonObject jObj)
                    {
                        bool modified = false;

                        // When building an Application package, remove patch-only delta fields that cause PS5 launch failure
                        if (modeStr is "app" or "application")
                        {
                            if (jObj.ContainsKey("originContentVersion")) { jObj.Remove("originContentVersion"); modified = true; }
                            if (jObj.ContainsKey("targetContentVersion")) { jObj.Remove("targetContentVersion"); modified = true; }
                            if (jObj.TryGetPropertyValue("downloadDataSize", out var dds) && (dds?.GetValue<long>() ?? 0) > 0)
                            {
                                jObj["downloadDataSize"] = 0;
                                modified = true;
                            }
                        }

                        if (!jObj.TryGetPropertyValue("attribute", out var attrNode) || (attrNode?.GetValue<long>() ?? 0) == 0)
                        {
                            jObj["attribute"] = 536870912;
                            modified = true;
                        }

                        if (autoBackport)
                        {
                            if (!jObj.TryGetPropertyValue("requiredSystemSoftwareVersion", out var fwNode) ||
                                !string.Equals(fwNode?.ToString(), "0x0100000000000000", StringComparison.OrdinalIgnoreCase))
                            {
                                jObj["requiredSystemSoftwareVersion"] = "0x0100000000000000";
                                modified = true;
                            }
                        }

                        if (modified)
                        {
                            File.WriteAllText(targetParamJson, jObj.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                            SendResponse("buildLog", new { log = "[stage 0/5] [param.json] Optimized param.json for PS5 package installation.", timestamp = DateTime.Now.ToString("HH:mm:ss") });
                        }
                    }
                }
                catch (Exception ex)
                {
                    SendResponse("buildLog", new { log = $"[stage 0/5] [param.json] Note: could not patch param.json: {ex.Message}", timestamp = DateTime.Now.ToString("HH:mm:ss") });
                }
            }

            version = NormalizeVersion(version) ?? "01.00";
            string titleId = contentId.Length >= 16 ? contentId.Substring(7, 9) : "PPSA00000";

            var mode = modeStr switch
            {
                "homebrew" or "hb" => ProsperoPackageMode.Homebrew,
                "dlc" or "additionalcontent" => ProsperoPackageMode.AdditionalContentData,
                _ => ProsperoPackageMode.Application
            };

            string cpuModeStr = root.TryGetProperty("cpuMode", out var cm) ? cm.GetString() ?? "cool" : "cool";

            // Set background/utility QoS on Darwin to keep system cool and prevent thermal throttling
            if (cpuModeStr == "cool" || cpuModeStr == "single")
            {
                Thread.CurrentThread.Priority = ThreadPriority.Lowest;
            }
            else if (cpuModeStr == "balanced")
            {
                Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            }

            int maxThreads = cpuModeStr switch
            {
                "single" => 1,
                "cool" => Math.Min(4, Environment.ProcessorCount),
                "balanced" => Math.Max(2, Environment.ProcessorCount / 2),
                "max" or "performance" => Environment.ProcessorCount,
                _ => Math.Min(4, Environment.ProcessorCount)
            };

            // Modern LibProsperoPkg v2.6.0 ProsperoBuildOptions
            var options = new ProsperoBuildOptions
            {
                SourceFolder = source,
                OutputFolder = output,
                ContentId = contentId,
                TitleId = titleId,
                Title = title,
                Version = version,
                Passcode = passcode,
                Mode = mode,
                OutputFormat = ProsperoOutputFormat.DebugImage,
                GenerateParamJsonIfMissing = true,
                FakeSignSelfModules = autoFself,
                LicenseFree = true
            };

            SendResponse("buildStarted", new { contentId, version });

            if (autoFself)
            {
                _currentStage = 0;
                SendResponse("buildProgress", new { stage = "Stage 0/5", desc = "Pre-processing game dump (make_fself)...", percent = 0 });
                RecursiveMakeFself(source, msg =>
                {
                    SendResponse("buildLog", new { log = msg, timestamp = DateTime.Now.ToString("HH:mm:ss") });
                });
            }
            else
            {
                SanitizeTruncatedElfHeaders(source, msg =>
                {
                    SendResponse("buildLog", new { log = msg, timestamp = DateTime.Now.ToString("HH:mm:ss") });
                });
            }

            _currentStage = 1;
            SendResponse("buildProgress", new { stage = "Stage 1/5", desc = "Starting package build...", percent = 0 });

            var sw = Stopwatch.StartNew();
            var buildResult = ProsperoPackageBuilder.Build(options, log =>
            {
                SendResponse("buildLog", new { log, timestamp = DateTime.Now.ToString("HH:mm:ss") });
                ParseAndSendProgress(log);
            });
            sw.Stop();

            string pkgPath = Path.GetFullPath(buildResult.OutputPath);
            long pkgSize = new FileInfo(pkgPath).Length;

            object? verifyResult = null;
            if (verify)
            {
                try
                {
                    SendResponse("buildLog", new { log = "Verifying package checksum (SHA-256)...", timestamp = DateTime.Now.ToString("HH:mm:ss") });
                    var v = VerifyPackageDirect(pkgPath);
                    verifyResult = new
                    {
                        passed = true,
                        containerType = v.ContainerType,
                        size = v.Length,
                        signedByte = $"0x{v.SignedByte:X2}",
                        outerMode = $"0x{v.OuterMode:X4}",
                        seedMarker = v.SeedMarker,
                        sha256 = v.Sha256
                    };
                }
                catch (Exception vex)
                {
                    verifyResult = new { passed = false, error = vex.Message };
                }
            }

            SendResponse("buildCompleted", new
            {
                packagePath = pkgPath,
                fileSize = pkgSize,
                durationMs = sw.ElapsedMilliseconds,
                warnings = buildResult.Warnings,
                verification = verifyResult
            });
        }
        catch (OperationCanceledException)
        {
            LibProsperoPkg.Util.BuildCleaner.CleanupAll();
            SendResponse("buildCancelled", new { message = "Build was cancelled by the user." });
        }
        catch (Exception ex)
        {
            LibProsperoPkg.Util.BuildCleaner.CleanupAll();
            SendResponse("buildLog", new { log = $"[ERROR] Build Exception: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}", timestamp = DateTime.Now.ToString("HH:mm:ss") });
            SendResponse("buildError", new { message = ex.Message });
        }
        finally
        {
            _buildCts?.Dispose();
            _buildCts = null;
            LibProsperoPkg.Util.BuildCleaner.CleanupAll();
        }
    }

    private void HandleCancelBuild()
    {
        try
        {
            if (_buildCts != null && !_buildCts.IsCancellationRequested)
            {
                SendResponse("buildLog", new { log = "[CANCEL] Cancellation requested. Stopping build and removing temp files...", timestamp = DateTime.Now.ToString("HH:mm:ss") });
                _buildCts.Cancel();
            }
        }
        catch
        {
        }
    }

    private void ParseAndSendProgress(string log)
    {
        if (string.IsNullOrWhiteSpace(log)) return;

        // Check for stage 1 inner data file progress
        // Example: [+00:00:27.809] [inner] data 6% (230/599): /Data/Ps5/configurations/defaultinstallpackage/cas_101.cas -> ...
        if (log.Contains("data ") && log.Contains("%"))
        {
            _currentStage = 1;
            int idx = log.IndexOf("data ");
            if (idx >= 0)
            {
                string sub = log.Substring(idx + 5).TrimStart();
                int pctIdx = sub.IndexOf('%');
                if (pctIdx > 0 && int.TryParse(sub.Substring(0, pctIdx).Trim(), out int innerPct))
                {
                    string fileDesc = "";
                    int colonIdx = sub.IndexOf(':');
                    if (colonIdx > 0)
                    {
                        int arrowIdx = sub.IndexOf("->", colonIdx);
                        if (arrowIdx > colonIdx)
                        {
                            fileDesc = sub.Substring(colonIdx + 1, arrowIdx - colonIdx - 1).Trim();
                        }
                        else
                        {
                            fileDesc = sub.Substring(colonIdx + 1).Trim();
                        }
                    }

                    string displayDesc = fileDesc;
                    if (!string.IsNullOrEmpty(fileDesc))
                    {
                        int slash = fileDesc.LastIndexOfAny(new[] { '/', '\\' });
                        if (slash >= 0 && slash + 1 < fileDesc.Length)
                        {
                            displayDesc = fileDesc.Substring(slash + 1);
                        }
                    }

                    // Stage 1 (inner filesystem image) represents ~75% of the total build workload.
                    // Map innerPct (0-100%) to 0-75% total progress.
                    int totalPct = Math.Max(1, Math.Min(75, (int)Math.Round(innerPct * 0.75)));

                    SendResponse("buildProgress", new
                    {
                        stage = "Stage 1/5",
                        desc = string.IsNullOrEmpty(displayDesc) ? $"Packing inner filesystem data ({innerPct}%)" : $"{displayDesc} (Stage 1: {innerPct}%)",
                        percent = totalPct
                    });
                    return;
                }
            }
        }

        if (log.Contains("[stage 0/5]"))
        {
            _currentStage = 0;
            SendResponse("buildProgress", new { stage = "Stage 0/5", desc = "Pre-processing dump (make_fself)...", percent = 0 });
        }
        else if (log.Contains("[stage 1/5]"))
        {
            _currentStage = 1;
            SendResponse("buildProgress", new { stage = "Stage 1/5", desc = "Building inner image...", percent = 0 });
        }
        else if (log.Contains("[stage 2/5]"))
        {
            _currentStage = 2;
            SendResponse("buildProgress", new { stage = "Stage 2/5", desc = "Generating NAPS layout and integrity tables...", percent = 80 });
        }
        else if (log.Contains("[stage 3/5]"))
        {
            _currentStage = 3;
            int stagePercent = 80;
            var match = System.Text.RegularExpressions.Regex.Match(log, @"Hashing outer PFS:\s*(\d+)%");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int subPercent))
            {
                stagePercent = 80 + (int)(subPercent * 0.08);
            }
            else if (log.Contains("Outer PFS complete"))
            {
                stagePercent = 88;
            }
            SendResponse("buildProgress", new { stage = "Stage 3/5", desc = "Building publisher outer PFS image...", percent = stagePercent });
        }
        else if (log.Contains("[stage 4/5]"))
        {
            _currentStage = 4;
            SendResponse("buildProgress", new { stage = "Stage 4/5", desc = "Writing package container bodies...", percent = 93 });
        }
        else if (log.Contains("[stage 5/5]"))
        {
            _currentStage = 5;
            SendResponse("buildProgress", new { stage = "Stage 5/5", desc = "Finalizing debug/retail mount image...", percent = 97 });
        }
        else if (log.Contains("Validated output container:") || log.Contains("Done (debug FIH)"))
        {
            SendResponse("buildProgress", new { stage = "Finalizing", desc = "Package finalized successfully", percent = 99 });
        }
    }

    private void HandleInspectPkg(JsonElement root)
    {
        try
        {
            string path = root.GetProperty("path").GetString() ?? "";
            if (!File.Exists(path))
            {
                SendResponse("inspectError", new { message = "Package file not found." });
                return;
            }

            path = Path.GetFullPath(path);
            var fileInfo = new FileInfo(path);
            ProsperoPkg pkg = ProsperoPkgReader.Read(path);

            var entriesList = pkg.Entries.Select((e, idx) => new
            {
                index = idx,
                name = e.Name ?? "(unnamed)",
                id = e.Id != ProsperoEntryId.Unknown ? e.Id.ToString() : $"0x{e.RawId:X8}",
                rawId = $"0x{e.RawId:X8}",
                offset = $"0x{e.DataOffset:X8}",
                size = e.DataSize,
                encrypted = e.Encrypted,
                keyIndex = e.KeyIndex
            }).ToList();

            SendResponse("inspectResult", new
            {
                path,
                size = fileInfo.Length,
                type = pkg.Type.ToString(),
                fih = pkg.Fih != null ? new
                {
                    signedByte = $"0x{pkg.Fih.SignedByte:X2}",
                    pfsOffset = $"0x{pkg.Fih.PfsImageOffset:X16}",
                    pfsSize = pkg.Fih.PfsImageSize,
                    embeddedCntOffset = $"0x{pkg.Fih.EmbeddedCntOffset:X16}",
                    formatVersion = pkg.Fih.FormatVersion,
                    pfsSizeFormatted = FormatSize((long)pkg.Fih.PfsImageSize)
                } : null,
                header = pkg.Header != null ? new
                {
                    contentId = pkg.Header.ContentId,
                    flags = $"0x{pkg.Header.Flags:X8}",
                    drmType = $"0x{pkg.Header.DrmType:X8}",
                    contentType = $"0x{pkg.Header.ContentType:X8}",
                    entryCount = pkg.Header.EntryCount,
                    scEntryCount = pkg.Header.ScEntryCount,
                    bodyOffset = $"0x{pkg.Header.BodyOffset:X16}",
                    bodySize = pkg.Header.BodySize
                } : null,
                entries = entriesList
            });
        }
        catch (Exception ex)
        {
            SendResponse("inspectError", new { message = ex.Message });
        }
    }

    private void HandleVerifyPkg(JsonElement root)
    {
        try
        {
            string path = root.GetProperty("path").GetString() ?? "";
            string imageMode = root.TryGetProperty("imageMode", out var im) ? im.GetString() ?? "plaintext" : "plaintext";

            if (!File.Exists(path))
            {
                SendResponse("verifyResult", new { success = false, message = "File does not exist." });
                return;
            }

            path = Path.GetFullPath(path);
            var v = VerifyPackageDirect(path, imageMode);

            SendResponse("verifyResult", new
            {
                success = true,
                path,
                containerType = v.ContainerType,
                size = v.Length,
                signedByte = $"0x{v.SignedByte:X2}",
                outerMode = $"0x{v.OuterMode:X4}",
                seedMarker = v.SeedMarker,
                sha256 = v.Sha256
            });
        }
        catch (Exception ex)
        {
            SendResponse("verifyResult", new { success = false, message = ex.Message });
        }
    }

    private void HandleExtractPkg(JsonElement root)
    {
        try
        {
            string pkgPath = root.GetProperty("path").GetString() ?? "";
            string outDir = root.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "";

            if (!File.Exists(pkgPath))
            {
                SendResponse("extractError", new { message = "Package file does not exist." });
                return;
            }

            pkgPath = Path.GetFullPath(pkgPath);
            if (string.IsNullOrWhiteSpace(outDir))
            {
                outDir = Path.Combine(Path.GetDirectoryName(pkgPath) ?? "", Path.GetFileNameWithoutExtension(pkgPath) + "_extracted");
            }
            outDir = Path.GetFullPath(outDir);
            Directory.CreateDirectory(outDir);

            ProsperoPkg pkg = ProsperoPkgReader.Read(pkgPath);
            long baseOffset = pkg.Fih != null ? (long)pkg.Fih.EmbeddedCntOffset : 0L;

            using var fs = File.OpenRead(pkgPath);
            var extractedFiles = new List<object>();

            foreach (var entry in pkg.Entries)
            {
                if (entry.DataSize == 0) continue;

                string filename = !string.IsNullOrWhiteSpace(entry.Name)
                    ? entry.Name
                    : (entry.Id != ProsperoEntryId.Unknown ? $"{entry.Id}.bin" : $"entry_0x{entry.RawId:X8}.bin");

                // If entry belongs in sce_sys (param.json, icon0, playgo, etc.), ensure it's in sce_sys/
                if (!filename.StartsWith("sce_sys/", StringComparison.OrdinalIgnoreCase) &&
                    !filename.StartsWith("sce_sys\\", StringComparison.OrdinalIgnoreCase))
                {
                    if (entry.Id is ProsperoEntryId.ParamJson or ProsperoEntryId.ParamSfo or
                        ProsperoEntryId.Icon0Png or ProsperoEntryId.Icon0Dds or
                        ProsperoEntryId.Pic0Png or ProsperoEntryId.Pic0Dds or
                        ProsperoEntryId.PlaygoChunkDat or ProsperoEntryId.PlaygoChunkSha or
                        ProsperoEntryId.PlaygoManifestXml or
                        ProsperoEntryId.LicenseDat or ProsperoEntryId.LicenseInfo ||
                        filename.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) ||
                        filename.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                        filename.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ||
                        filename.StartsWith("icon", StringComparison.OrdinalIgnoreCase) ||
                        filename.StartsWith("pic", StringComparison.OrdinalIgnoreCase))
                    {
                        if (entry.Id == ProsperoEntryId.ParamJson && !filename.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        {
                            filename = "sce_sys/param.json";
                        }
                        else if (entry.Id == ProsperoEntryId.LicenseDat && !filename.Equals("license.dat", StringComparison.OrdinalIgnoreCase))
                        {
                            filename = "sce_sys/license.dat";
                        }
                        else if (entry.Id == ProsperoEntryId.LicenseInfo && !filename.Equals("license.info", StringComparison.OrdinalIgnoreCase))
                        {
                            filename = "sce_sys/license.info";
                        }
                        else
                        {
                            filename = "sce_sys/" + filename;
                        }
                    }
                }

                filename = filename.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                string targetPath = Path.Combine(outDir, filename);
                string? dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                long offset = baseOffset + entry.DataOffset;
                if (offset + entry.DataSize > fs.Length) continue;

                fs.Position = offset;
                byte[] buffer = new byte[entry.DataSize];
                fs.ReadExactly(buffer);
                File.WriteAllBytes(targetPath, buffer);

                extractedFiles.Add(new { name = filename, size = entry.DataSize, encrypted = entry.Encrypted });
            }

            SendResponse("extractResult", new
            {
                outputDirectory = outDir,
                files = extractedFiles
            });
        }
        catch (Exception ex)
        {
            SendResponse("extractError", new { message = ex.Message });
        }
    }

    private void SendUnpackLog(string message)
    {
        string time = DateTime.Now.ToString("HH:mm:ss");
        SendResponse("unpackVerifyLog", new { line = $"[{time}] {message}" });
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024 * 1024)
            return $"{(double)bytes / (1024.0 * 1024 * 1024 * 1024):0.00} TiB";
        if (bytes >= 1024L * 1024 * 1024)
            return $"{(double)bytes / (1024.0 * 1024 * 1024):0.00} GiB";
        if (bytes >= 1024L * 1024)
            return $"{(double)bytes / (1024.0 * 1024 * 1024):0.00} MiB";
        if (bytes >= 1024L)
            return $"{(double)bytes / 1024.0:0.00} KiB";
        return $"{bytes} bytes";
    }

    private static byte[] ReadPkgEntryBytes(FileStream fs, long cntBaseOffset, ProsperoPkgEntry entry)
    {
        long size = entry.DataSize;
        fs.Position = cntBaseOffset + entry.DataOffset;
        byte[] buffer = new byte[size];
        fs.ReadExactly(buffer);
        return buffer;
    }

    private void HandleLoadPackageInfo(JsonElement root)
    {
        string pkgPath = root.GetProperty("path").GetString() ?? "";
        string passcode = root.TryGetProperty("passcode", out var pc) ? pc.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(passcode)) passcode = "00000000000000000000000000000000";

        try
        {
            if (!File.Exists(pkgPath))
            {
                SendResponse("packageInfoError", new { message = "Package file does not exist." });
                return;
            }

            pkgPath = Path.GetFullPath(pkgPath);
            SendUnpackLog($"Reading package: {pkgPath}");

            var ru = new CultureInfo("ru-RU");
            var fi = new FileInfo(pkgPath);
            long fileLength = fi.Length;

            using var fs = File.OpenRead(pkgPath);
            var pkg = ProsperoPkgReader.Read(fs);
            long baseOffset = pkg.Fih != null ? (long)pkg.Fih.EmbeddedCntOffset : 0L;
            long outerPfsSize = pkg.Fih != null ? (long)pkg.Fih.PfsImageSize : 0L;
            long cntSize = pkg.Header != null ? (long)pkg.Header.BodySize : 0L;
            string contentId = pkg.Header?.ContentId ?? "—";

            // param.json
            string title = "—";
            string version = "—";
            string sdk = "0x0000000000000000";
            string sysReq = "0x0000000000000000";
            string availableLanguages = "—";
            string drm = (pkg.Header != null && pkg.Header.DrmType == 0) ? "None" : "Standard";
            string container = pkg.Type.ToString();
            string contentType = (pkg.Header?.ContentType) switch
            {
                1 => "PS5GD (game data)",
                2 => "PS5GP (game patch)",
                3 => "PS5AC (additional content)",
                4 => "PS5AL (additional content license)",
                5 => "PS5LA (license authorization)",
                _ => "PS5GD (game data)"
            };

            var paramEntry = pkg.Entries.FirstOrDefault(e => e.Id == ProsperoEntryId.ParamJson || e.Name == "param.json");
            if (paramEntry != null)
            {
                byte[] paramBytes = ReadPkgEntryBytes(fs, baseOffset, paramEntry);
                try
                {
                    using var pdoc = JsonDocument.Parse(paramBytes);
                    var proot = pdoc.RootElement;
                    title = ReadLocalizedTitle(proot) ?? (proot.TryGetProperty("titleName", out var tn) ? tn.GetString() ?? "—" : "—");
                    if (proot.TryGetProperty("contentId", out var cid) && !string.IsNullOrWhiteSpace(cid.GetString()))
                        contentId = cid.GetString()!;
                    if (proot.TryGetProperty("contentVersion", out var cv))
                        version = cv.GetString() ?? "—";
                    if (proot.TryGetProperty("sdkVersion", out var sv))
                        sdk = sv.ValueKind == JsonValueKind.Number ? $"0x{sv.GetUInt64():X16}" : sv.GetString() ?? sdk;
                    if (proot.TryGetProperty("requiredSystemSoftwareVersion", out var rv))
                        sysReq = rv.ValueKind == JsonValueKind.Number ? $"0x{rv.GetUInt64():X16}" : rv.GetString() ?? sysReq;
                    if (proot.TryGetProperty("localizedParameters", out var lp) && lp.ValueKind == JsonValueKind.Object)
                    {
                        var langs = lp.EnumerateObject().Select(p => p.Name).Where(n => n != "defaultLanguage").ToList();
                        if (langs.Count > 0) availableLanguages = string.Join(", ", langs);
                    }
                    if (proot.TryGetProperty("applicationDrmType", out var adt))
                    {
                        drm = adt.ValueKind == JsonValueKind.Number ? (adt.GetInt32() == 0 ? "None" : "Standard") : adt.GetString() ?? drm;
                    }
                }
                catch { }
            }

            // PlayGo
            string playgoLanguages = "—";
            string playgoSummary = "1 chunks / 1 scenarios";
            int chunks = 1;
            int scenarios = 1;

            var playgoEntry = pkg.Entries.FirstOrDefault(e => e.Id == ProsperoEntryId.PlaygoManifestXml || e.Name == "playgo-manifest.xml");
            if (playgoEntry != null)
            {
                byte[] xmlBytes = ReadPkgEntryBytes(fs, baseOffset, playgoEntry);
                try
                {
                    var doc = XDocument.Parse(Encoding.UTF8.GetString(xmlBytes));
                    var chunksElem = doc.Descendants("chunks").FirstOrDefault();
                    if (chunksElem != null && int.TryParse(chunksElem.Attribute("count")?.Value, out int c)) chunks = c;
                    else chunks = doc.Descendants("chunk").Count();
                    if (chunks == 0) chunks = 1;

                    var scElem = doc.Descendants("scenarios").FirstOrDefault();
                    if (scElem != null && int.TryParse(scElem.Attribute("count")?.Value, out int s)) scenarios = s;
                    else scenarios = doc.Descendants("scenario").Count();
                    if (scenarios == 0) scenarios = 1;

                    var langElem = doc.Descendants("languages").FirstOrDefault();
                    if (langElem != null && !string.IsNullOrWhiteSpace(langElem.Value))
                    {
                        var lList = langElem.Value.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        playgoLanguages = string.Join(", ", lList);
                    }
                }
                catch { }
            }
            else
            {
                var chunkDatEntry = pkg.Entries.FirstOrDefault(e => e.Id == ProsperoEntryId.PlaygoChunkDat || e.Name == "playgo-chunk.dat");
                if (chunkDatEntry != null)
                {
                    byte[] cBytes = ReadPkgEntryBytes(fs, baseOffset, chunkDatEntry);
                    if (cBytes.Length >= 12 && cBytes[0] == (byte)'p' && cBytes[1] == (byte)'l' && cBytes[2] == (byte)'g')
                    {
                        scenarios = BinaryPrimitives.ReadUInt16LittleEndian(cBytes.AsSpan(8, 2));
                        chunks = BinaryPrimitives.ReadUInt16LittleEndian(cBytes.AsSpan(10, 2));
                        if (chunks == 0) chunks = 1;
                        if (scenarios == 0) scenarios = 1;
                    }
                }

                var scenarioJsonEntry = pkg.Entries.FirstOrDefault(e => e.Name == "playgo-scenario.json" || e.RawId == 0x00003000);
                if (scenarioJsonEntry != null)
                {
                    byte[] sBytes = ReadPkgEntryBytes(fs, baseOffset, scenarioJsonEntry);
                    try
                    {
                        using var sdoc = JsonDocument.Parse(sBytes);
                        var sroot = sdoc.RootElement;
                        if (sroot.TryGetProperty("scenarioCount", out var sc)) scenarios = sc.GetInt32();
                        if (sroot.TryGetProperty("chunkSupportedLanguages", out var csl) && csl.ValueKind == JsonValueKind.Array)
                        {
                            var sLangs = csl.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrEmpty(x)).ToList();
                            if (sLangs.Count > 0) playgoLanguages = string.Join(", ", sLangs);
                        }
                    }
                    catch { }
                }
            }

            if (playgoLanguages == "—" && availableLanguages != "—")
            {
                playgoLanguages = availableLanguages;
            }

            playgoSummary = $"{chunks} chunks / {scenarios} scenarios";

            // Segments
            string segments = $"FIH 64.00 KiB (65,536 bytes); outer PFS {FormatSize(outerPfsSize)} ({outerPfsSize:N0} bytes); CNT {FormatSize(cntSize)} ({cntSize:N0} bytes)";
            string pkgSize = $"{FormatSize(fileLength)} ({fileLength:N0} bytes)";

            // Artwork: Check pic0.png or icon0.png
            string? coverImageBase64 = null;
            var pic0 = pkg.Entries.FirstOrDefault(e => e.Id == ProsperoEntryId.Pic0Png || e.Name == "pic0.png");
            var icon0 = pkg.Entries.FirstOrDefault(e => e.Id == ProsperoEntryId.Icon0Png || e.Name == "icon0.png");
            var coverEntry = pic0 ?? icon0;
            if (coverEntry != null)
            {
                try
                {
                    byte[] imgBytes = ReadPkgEntryBytes(fs, baseOffset, coverEntry);
                    if (imgBytes.Length > 8 && imgBytes[0] == 0x89 && imgBytes[1] == 0x50 && imgBytes[2] == 0x4E && imgBytes[3] == 0x47)
                    {
                        coverImageBase64 = "data:image/png;base64," + Convert.ToBase64String(imgBytes);
                    }
                }
                catch { }
            }

            SendUnpackLog("Package information loaded.");

            SendResponse("packageInfoResult", new
            {
                path = pkgPath,
                title,
                contentId,
                version,
                sdk,
                sysReq,
                availableLanguages,
                playgoLanguages,
                playgoSummary,
                drm,
                container,
                contentType,
                segments,
                pkgSize,
                coverImage = coverImageBase64
            });
        }
        catch (Exception ex)
        {
            SendUnpackLog($"ERROR: {ex.Message}");
            SendResponse("packageInfoError", new { message = ex.Message });
        }
    }

    private void HandleRunQuickVerify(JsonElement root)
    {
        string pkgPath = root.GetProperty("path").GetString() ?? "";
        string passcode = root.TryGetProperty("passcode", out var pc) ? pc.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(passcode)) passcode = "00000000000000000000000000000000";

        var sw = Stopwatch.StartNew();
        try
        {
            if (!File.Exists(pkgPath))
            {
                SendUnpackLog($"ERROR: Package file does not exist: {pkgPath}");
                SendResponse("quickVerifyResult", new { success = false, message = "Package file does not exist." });
                return;
            }

            pkgPath = Path.GetFullPath(pkgPath);
            SendUnpackLog($"Running acceptance-gate verification: {pkgPath}");

            var report = ProsperoPkgValidator.Validate(pkgPath);
            int verifiedChecks = report.Checks.Count;
            foreach (var check in report.Checks)
            {
                string statusIcon = check.Status switch
                {
                    ProsperoCheckStatus.Pass => "[PASS]",
                    ProsperoCheckStatus.Warning => "[WARN]",
                    _ => "[FAIL]"
                };
                SendUnpackLog($"{statusIcon} {check.Name}: {check.Detail}");
            }

            sw.Stop();
            SendUnpackLog("Verification Summary:");
            SendUnpackLog($"Total checks evaluated: {verifiedChecks}");
            SendUnpackLog($"Elapsed time: {sw.Elapsed}");
            if (report.Accepted)
            {
                SendUnpackLog("RESULT: No critical errors found. Package passes acceptance gate.");
            }
            else
            {
                SendUnpackLog("RESULT: Package failed acceptance gate verification.");
            }

            SendResponse("quickVerifyResult", new
            {
                success = report.Accepted,
                message = report.Accepted ? "Quick verification passed" : "Quick verification failed",
                elapsed = sw.Elapsed.ToString(),
                entries = verifiedChecks
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            SendUnpackLog($"ERROR: {ex.Message}");
            SendUnpackLog("RESULT: Errors detected.");
            SendResponse("quickVerifyResult", new
            {
                success = false,
                message = $"Verification failed: {ex.Message}"
            });
        }
    }

    private void HandleUnpackPkg(JsonElement root)
    {
        string pkgPath = root.GetProperty("path").GetString() ?? "";
        string outDir = root.TryGetProperty("output", out var od) ? od.GetString() ?? "" : "";
        string passcode = root.TryGetProperty("passcode", out var pc) ? pc.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(passcode)) passcode = "00000000000000000000000000000000";

        var sw = Stopwatch.StartNew();
        try
        {
            if (!File.Exists(pkgPath))
            {
                SendUnpackLog($"ERROR: Package file does not exist: {pkgPath}");
                SendResponse("unpackResult", new { success = false, message = "Package file does not exist." });
                return;
            }

            pkgPath = Path.GetFullPath(pkgPath);
            if (string.IsNullOrWhiteSpace(outDir))
            {
                outDir = Path.Combine(Path.GetDirectoryName(pkgPath)!, Path.GetFileNameWithoutExtension(pkgPath) + "-unpacked");
            }
            outDir = Path.GetFullPath(outDir);
            Directory.CreateDirectory(outDir);

            SendUnpackLog($"Starting package unpacking: {pkgPath}");
            SendUnpackLog($"Output directory: {outDir}");

            // 1. CNT entries (sce_sys metadata)
            SendUnpackLog("Extracting CNT entries (sce_sys metadata)...");
            string sceSysDir = Path.Combine(outDir, "sce_sys");
            Directory.CreateDirectory(sceSysDir);
            int cntCount = 0;
            try
            {
                using var fs = File.OpenRead(pkgPath);
                var pkg = ProsperoPkgReader.Read(fs);
                if (pkg.Fih != null && pkg.Entries.Count > 0)
                {
                    long baseOffset = (long)pkg.Fih.EmbeddedCntOffset;
                    foreach (var entry in pkg.Entries)
                    {
                        if (entry.DataSize <= 0) continue;
                        string filename = !string.IsNullOrEmpty(entry.Name) ? entry.Name : $"entry_0x{entry.RawId:X8}.bin";
                        string destFile = Path.Combine(sceSysDir, filename);
                        byte[] entryBytes = ReadPkgEntryBytes(fs, baseOffset, entry);
                        File.WriteAllBytes(destFile, entryBytes);
                        cntCount++;
                    }
                }
            }
            catch (Exception ex)
            {
                SendUnpackLog($"Notice extracting CNT metadata entries: {ex.Message}");
            }
            SendUnpackLog($"Extracted CNT metadata entries: {cntCount}");

            // 2. Application filesystem via modern ProsperoPackageExtractor
            SendUnpackLog("Extracting application filesystem...");
            var options = new ProsperoExtractionOptions
            {
                ExtractOuterMetadata = true,
                OuterMetadataSubdirectory = "_outer"
            };
            var key = ProsperoExtractionKey.FromPasscode(passcode);
            var manifest = ProsperoPackageExtractor.Extract(pkgPath, outDir, key, options, log => SendUnpackLog(log));
            SendUnpackLog($"Extracted {manifest.ExtractedFileCount} file(s) total ({manifest.Entries.Count} application files).");

            sw.Stop();
            SendUnpackLog($"Unpacking completed successfully in {sw.Elapsed}.");
            SendResponse("unpackResult", new
            {
                success = true,
                message = "Unpacking complete",
                outputDirectory = outDir,
                elapsed = sw.Elapsed.ToString()
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            SendUnpackLog($"ERROR: {ex.Message}");
            SendResponse("unpackResult", new
            {
                success = false,
                message = $"Unpacking error: {ex.Message}"
            });
        }
    }

    private void HandleOpenFolder(JsonElement root)
    {
        string path = root.GetProperty("path").GetString() ?? "";
        if (!string.IsNullOrWhiteSpace(path))
        {
            string dir = Directory.Exists(path) ? path : (Path.GetDirectoryName(path) ?? "");
            if (Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    Arguments = $"\"{dir}\"",
                    UseShellExecute = true
                });
            }
        }
    }

    private sealed record PackageVerification(
        string ContainerType,
        long Length,
        byte SignedByte,
        ushort OuterMode,
        string? SeedMarker,
        string Sha256);

    private static PackageVerification VerifyPackageDirect(string packagePath, string? expectedMode = null)
    {
        ProsperoPkgType? type = ProsperoPkgReader.DetectType(packagePath);
        using FileStream stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4 * 1024 * 1024, FileOptions.SequentialScan);

        if (stream.Length < 4096)
        {
            throw new InvalidDataException("The package file is too small to contain an FIH.");
        }

        Span<byte> buffer = stackalloc byte[48];
        stream.ReadExactly(buffer);

        if (!buffer.Slice(0, 4).SequenceEqual("\u007fFIH"u8))
        {
            throw new InvalidDataException("The package file does not contain a valid FIH header.");
        }

        byte signedByte = buffer[5];
        if (signedByte != 0)
        {
            throw new InvalidDataException($"Expected debug FIH signed byte 0x00, got 0x{signedByte:X2}.");
        }

        long outerOffset = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(32, 8)));
        if (outerOffset < 0 || outerOffset + 896 > stream.Length)
        {
            throw new InvalidDataException("FIH contains an invalid outer superblock offset.");
        }

        stream.Position = outerOffset + 28;
        Span<byte> modeSpan = stackalloc byte[2];
        stream.ReadExactly(modeSpan);
        ushort outerMode = BinaryPrimitives.ReadUInt16LittleEndian(modeSpan);
        if (outerMode != 13)
        {
            throw new InvalidDataException($"Expected publisher outer PFS mode 0x000D, got 0x{outerMode:X4}.");
        }

        stream.Position = outerOffset + 880;
        byte[] seedBytes = new byte[16];
        stream.ReadExactly(seedBytes);
        string? seedMarker = null;

        if (string.Equals(expectedMode, "plaintext", StringComparison.OrdinalIgnoreCase))
        {
            seedMarker = Encoding.ASCII.GetString(seedBytes);
            if (!string.Equals(seedMarker, "PPRPLAIN-NOAUTH!", StringComparison.Ordinal))
            {
                throw new InvalidDataException("The outer PFS does not contain the PLAINTEXT_NOAUTH marker.");
            }
        }

        stream.Position = 0;
        string sha = Convert.ToHexString(SHA256.HashData(stream));

        return new PackageVerification(type?.ToString() ?? "Unknown", stream.Length, signedByte, outerMode, seedMarker, sha);
    }

    private static string? NormalizeVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();
        if (VersionPattern.IsMatch(raw)) return raw;
        string[] parts = raw.Split('.');
        if (parts.Length >= 2 && int.TryParse(parts[0], out int major) && int.TryParse(parts[1], out int minor))
        {
            return $"{Math.Clamp(major, 0, 99):00}.{Math.Clamp(minor, 0, 99):00}";
        }
        return null;
    }

    private static string? ReadLocalizedTitle(JsonElement root)
    {
        if (!root.TryGetProperty("localizedParameters", out var val) || val.ValueKind != JsonValueKind.Object)
        {
            if (root.TryGetProperty("titleName", out var tn)) return tn.GetString();
            return null;
        }

        string? defLang = val.TryGetProperty("defaultLanguage", out var dl) ? dl.GetString() : null;
        if (defLang != null && val.TryGetProperty(defLang, out var langObj) && langObj.TryGetProperty("titleName", out var tName))
        {
            return tName.GetString();
        }

        if (val.TryGetProperty("en-US", out var enUs) && enUs.TryGetProperty("titleName", out var t1)) return t1.GetString();
        if (val.TryGetProperty("default", out var def) && def.TryGetProperty("titleName", out var t2)) return t2.GetString();
        if (val.TryGetProperty("en", out var en) && en.TryGetProperty("titleName", out var t3)) return t3.GetString();

        foreach (var prop in val.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Object && prop.Value.TryGetProperty("titleName", out var tVal))
            {
                string? s = tVal.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }

        return null;
    }

    public static int RecursiveMakeFself(string sourceDir, Action<string>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDir, nameof(sourceDir));
        if (!Directory.Exists(sourceDir)) return 0;

        logger?.Invoke("[stage 0/5] Pre-processing game dump (recursive make_fself)...");

        byte[]? applicationSceVersion = null;
        string ebootPath = Path.Combine(sourceDir, "eboot.bin");
        if (File.Exists(ebootPath))
        {
            try
            {
                byte[] ebootBytes = File.ReadAllBytes(ebootPath);
                if (LibProsperoPkg.Content.ProsperoFself.TryGetSceVersionRecord(ebootBytes, out byte[] rec) && rec.Length == 8)
                {
                    applicationSceVersion = rec;
                }
            }
            catch { }
        }

        int convertedCount = 0;
        int sanitizedCount = 0;
        string[] extensions = { ".bin", ".elf", ".prx", ".sprx" };

        try
        {
            foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (!extensions.Contains(ext)) continue;

                try
                {
                    var fi = new FileInfo(file);
                    if (fi.Length < 64) continue;

                    byte[] header = new byte[64];
                    using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        if (fs.Read(header, 0, 64) != 64) continue;
                    }

                    uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
                    string relPath = Path.GetRelativePath(sourceDir, file);
                    byte[] elfBytes;

                    // If already a native PS5 SELF/FSELF (0xEEF51454 / 54 14 f5 ee), nothing to do
                    if (magic == 4009038932u)
                    {
                        continue;
                    }
                    else if (magic == 490542415u) // Legacy PS4/Orbis FSELF (0x1D3D154F / 4f 15 3d 1d)
                    {
                        byte[] selfBytes = File.ReadAllBytes(file);
                        if (!TryUnfself(selfBytes, out byte[]? extractedElf) || extractedElf == null)
                        {
                            logger?.Invoke($"[stage 0/5] Warning: could not unpack Orbis FSELF '{relPath}'");
                            continue;
                        }
                        elfBytes = extractedElf;
                        logger?.Invoke($"[stage 0/5] Unpacked Orbis FSELF to clean ELF: {relPath} ({selfBytes.Length:N0} -> {elfBytes.Length:N0} bytes)");
                    }
                    else if (LibProsperoPkg.Content.ProsperoFself.IsElf(header))
                    {
                        elfBytes = File.ReadAllBytes(file);
                    }
                    else
                    {
                        continue;
                    }

                    // Check and sanitize truncated section header tables in the ELF
                    if (elfBytes.Length >= 64)
                    {
                        ulong e_shoff = BinaryPrimitives.ReadUInt64LittleEndian(elfBytes.AsSpan(40, 8));
                        ushort e_shentsize = BinaryPrimitives.ReadUInt16LittleEndian(elfBytes.AsSpan(58, 2));
                        ushort e_shnum = BinaryPrimitives.ReadUInt16LittleEndian(elfBytes.AsSpan(60, 2));

                        if (e_shoff > 0 || e_shnum > 0)
                        {
                            long tableEnd = (long)e_shoff + ((long)e_shnum * (long)e_shentsize);
                            if (tableEnd > elfBytes.Length || (long)e_shoff >= elfBytes.Length)
                            {
                                elfBytes.AsSpan(40, 8).Clear();
                                elfBytes.AsSpan(58, 6).Clear();
                                sanitizedCount++;
                            }
                        }
                    }

                    LibProsperoPkg.Content.FselfOptions? fselfOpts = null;
                    string fileName = Path.GetFileName(file);
                    if (!fileName.Equals("eboot.bin", StringComparison.OrdinalIgnoreCase) &&
                        applicationSceVersion != null &&
                        !LibProsperoPkg.Content.ProsperoFself.TryGetSceVersionRecord(elfBytes, out _))
                    {
                        fselfOpts = new LibProsperoPkg.Content.FselfOptions
                        {
                            SceVersionName = Path.GetFileNameWithoutExtension(fileName),
                            SceVersionRecord = applicationSceVersion
                        };
                    }

                    byte[] fself = LibProsperoPkg.Content.ProsperoFself.MakeFself(elfBytes, fselfOpts);
                    File.WriteAllBytes(file, fself);
                    convertedCount++;

                    logger?.Invoke($"[stage 0/5] Fake-signed native PS5 FSELF (0xEEF51454): {relPath} ({fself.Length:N0} bytes)");
                }
                catch (Exception ex)
                {
                    string relPath = Path.GetRelativePath(sourceDir, file);
                    logger?.Invoke($"[stage 0/5] Warning: could not fake-sign '{relPath}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            logger?.Invoke($"[stage 0/5] Scan warning: {ex.Message}");
        }

        logger?.Invoke($"[stage 0/5] Pre-processing complete: {convertedCount} decrypted ELF(s) converted to FSELF{(sanitizedCount > 0 ? $", {sanitizedCount} section table(s) sanitized" : "")}.");
        return convertedCount;
    }

    private static bool TryUnfself(byte[] selfBytes, out byte[]? elf) =>
        LibProsperoPkg.Content.ProsperoFself.TryUnfself(selfBytes, out elf);

    public static int SanitizeTruncatedElfHeaders(string sourceDir, Action<string>? logger = null)
    {
        int sanitizedCount = 0;
        try
        {
            var files = Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories);
            byte[] headerBuffer = new byte[64];
            Span<byte> header = headerBuffer.AsSpan();

            foreach (var file in files)
            {
                try
                {
                    var fi = new FileInfo(file);
                    if (fi.Length < 64) continue;

                    using var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                    if (stream.Read(header) != 64) continue;

                    if (header[0] != 0x7F || header[1] != (byte)'E' || header[2] != (byte)'L' || header[3] != (byte)'F')
                        continue;

                    ulong e_shoff = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(40, 8));
                    ushort e_shentsize = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(58, 2));
                    ushort e_shnum = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(60, 2));

                    if (e_shoff > 0 || e_shnum > 0)
                    {
                        long tableEnd = (long)e_shoff + ((long)e_shnum * (long)e_shentsize);
                        if (tableEnd > fi.Length || (long)e_shoff >= fi.Length)
                        {
                            header.Slice(40, 8).Clear();
                            header.Slice(58, 6).Clear();

                            stream.Seek(40, SeekOrigin.Begin);
                            stream.Write(header.Slice(40, 8));
                            stream.Seek(58, SeekOrigin.Begin);
                            stream.Write(header.Slice(58, 6));
                            stream.Flush();

                            sanitizedCount++;
                            string rel = Path.GetRelativePath(sourceDir, file);
                            logger?.Invoke($"[INFO] Cleared truncated section header table in '{rel}' (offset 0x{e_shoff:X} exceeded file size {fi.Length:N0} bytes).");
                        }
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
        return sanitizedCount;
    }
}

