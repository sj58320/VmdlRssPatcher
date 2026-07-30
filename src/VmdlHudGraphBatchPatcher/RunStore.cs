using System.Diagnostics;
using System.Text;
using System.Text.Json;

internal sealed record ModelCandidate(string RelativePath, string FullPath, bool Exists);

internal static class RunStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static BatchRun Create(CliOptions options, string command)
    {
        var now = DateTimeOffset.Now;
        var runId = now.ToString("yyyyMMdd_HHmmss_fff");
        var runDirectory = Path.Combine(options.RunRoot, runId);
        return new BatchRun
        {
            RunId = runId,
            Command = command,
            RunStatus = command == "scan" ? "SCANNING" : "PREPARING",
            CreatedAt = now,
            UpdatedAt = now,
            AssetRoot = options.AssetRoot,
            ManifestPath = options.ManifestPath,
            TargetGraph = ResourcePatcher.TargetGraph,
            TargetGraphResourceId = $"0x{ResourcePatcher.TargetGraphId:X16}",
            Source2ViewerPath = ResolveSource2Viewer(options.Source2ViewerPath),
            RunRoot = options.RunRoot,
            RunDirectory = runDirectory,
            StagingDirectory = Path.Combine(options.StagingRoot, $"vmdl_hudmodel_{runId}"),
            BackupDirectory = Path.Combine(options.BackupRoot, $"vmdl_hudmodel_{runId}"),
        };
    }

    public static BatchRun Load(CliOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.RunId))
        {
            throw new ArgumentException("--run-id가 필요합니다.");
        }

        var runFile = Path.Combine(options.RunRoot, options.RunId, "run.json");
        if (!File.Exists(runFile))
        {
            throw new FileNotFoundException($"run.json이 없습니다: {runFile}");
        }

        var run = JsonSerializer.Deserialize<BatchRun>(
            File.ReadAllText(runFile, Encoding.UTF8),
            JsonOptions);
        return run ?? throw new InvalidDataException($"run.json을 읽을 수 없습니다: {runFile}");
    }

    public static void Save(BatchRun run, string command)
    {
        run.Command = command;
        run.UpdatedAt = DateTimeOffset.Now;
        Directory.CreateDirectory(run.RunDirectory);
        Directory.CreateDirectory(Path.Combine(run.RunDirectory, "history"));

        var json = JsonSerializer.Serialize(run, JsonOptions);
        var csv = BuildCsv(run);
        WriteAtomic(Path.Combine(run.RunDirectory, "run.json"), json);
        WriteAtomic(Path.Combine(run.RunDirectory, "report.json"), json);
        WriteAtomic(Path.Combine(run.RunDirectory, "report.csv"), csv);

        var historyName = $"{run.UpdatedAt:yyyyMMdd_HHmmss_fff}_{command}";
        WriteAtomic(Path.Combine(run.RunDirectory, "history", historyName + ".json"), json);
        WriteAtomic(Path.Combine(run.RunDirectory, "history", historyName + ".csv"), csv);
    }

    public static IReadOnlyList<ModelCandidate> ResolveCandidates(
        BatchRun run,
        CliOptions options)
    {
        if (!Directory.Exists(run.AssetRoot))
        {
            throw new DirectoryNotFoundException($"에셋 루트가 없습니다: {run.AssetRoot}");
        }

        if (!string.IsNullOrWhiteSpace(options.ManifestPath))
        {
            if (!File.Exists(options.ManifestPath))
            {
                throw new FileNotFoundException($"manifest가 없습니다: {options.ManifestPath}");
            }

            var candidates = new List<ModelCandidate>();
            foreach (var rawLine in File.ReadLines(options.ManifestPath, Encoding.UTF8))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                var full = Path.IsPathRooted(line)
                    ? Path.GetFullPath(line)
                    : Path.GetFullPath(Path.Combine(run.AssetRoot, line));
                EnsureInside(run.AssetRoot, full, "manifest 모델");
                var relative = NormalizeRelative(Path.GetRelativePath(run.AssetRoot, full));
                candidates.Add(new ModelCandidate(relative, full, File.Exists(full)));
            }

            return candidates
                .DistinctBy(candidate => candidate.RelativePath, StringComparer.OrdinalIgnoreCase)
                .OrderBy(candidate => candidate.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var excludedRoots = new[]
        {
            options.StagingRoot,
            options.BackupRoot,
            options.RunRoot,
        };

        return Directory
            .EnumerateFiles(run.AssetRoot, "*.vmdl_c", SearchOption.AllDirectories)
            .Where(file => !excludedRoots.Any(root => IsInside(root, file)))
            .Select(file => new ModelCandidate(
                NormalizeRelative(Path.GetRelativePath(run.AssetRoot, file)),
                file,
                true))
            .OrderBy(candidate => candidate.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string ResolveRunFile(BatchRun run, string root, string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        EnsureInside(root, full, "run 파일");
        return full;
    }

    public static string ResolveAssetFile(BatchRun run, string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(run.AssetRoot, relativePath));
        EnsureInside(run.AssetRoot, full, "에셋 파일");
        return full;
    }

    public static string ResolveSource2Viewer(string requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var explicitPath = Path.GetFullPath(requested);
            return File.Exists(explicitPath) ? explicitPath : "";
        }

        var besideExe = Path.Combine(AppContext.BaseDirectory, "Source2Viewer-CLI.exe");
        if (File.Exists(besideExe))
        {
            return besideExe;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "where.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("Source2Viewer-CLI.exe");
            using var process = Process.Start(startInfo);
            if (process != null)
            {
                var first = process.StandardOutput.ReadLine();
                process.WaitForExit(5_000);
                if (process.ExitCode == 0 && first != null && File.Exists(first.Trim()))
                {
                    return Path.GetFullPath(first.Trim());
                }
            }
        }
        catch
        {
            // The caller reports the missing validation dependency.
        }

        return "";
    }

    private static string BuildCsv(BatchRun run)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(',', new[]
        {
            "run_id", "run_time", "asset_root", "relative_path", "status", "reason",
            "before_sha256", "after_sha256", "before_size", "after_size",
            "old_hud_graph", "new_hud_graph", "original_kv_flag",
            "data_patch_count", "rerl_patch_count", "block_validation",
            "data_validation", "rerl_validation", "source2viewer_validation",
            "block_validation_result",
        }));

        foreach (var file in run.Files)
        {
            builder.AppendLine(string.Join(',', new[]
            {
                Csv(run.RunId),
                Csv(run.UpdatedAt.ToString("O")),
                Csv(run.AssetRoot),
                Csv(file.RelativePath),
                Csv(file.Status),
                Csv(file.Reason),
                Csv(file.BeforeSha256),
                Csv(file.AfterSha256),
                file.BeforeSize.ToString(),
                file.AfterSize.ToString(),
                Csv(file.OldHudGraph),
                Csv(file.NewHudGraph),
                Csv(file.OriginalKvFlag),
                file.DataPatchCount.ToString(),
                file.RerlPatchCount.ToString(),
                file.BlockValidationPassed.ToString(),
                file.DataValidationPassed.ToString(),
                file.RerlValidationPassed.ToString(),
                file.Source2ViewerValidationPassed.ToString(),
                Csv(file.BlockValidationResult),
            }));
        }

        return builder.ToString();
    }

    private static string Csv(string value)
        => '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

    private static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void EnsureInside(string root, string path, string label)
    {
        if (!IsInside(root, path))
        {
            throw new InvalidOperationException($"{label} 경로가 허용된 루트를 벗어났습니다: {path}");
        }
    }

    private static bool IsInside(string root, string path)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))
            + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRelative(string value)
        => value.Replace('\\', '/');
}
