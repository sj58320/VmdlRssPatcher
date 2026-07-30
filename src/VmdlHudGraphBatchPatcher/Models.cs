using System.Text.Json.Serialization;

internal static class Statuses
{
    public const string Patchable = "PATCHABLE";
    public const string Patched = "PATCHED";
    public const string Verified = "VERIFIED";
    public const string Applied = "APPLIED";
    public const string Restored = "RESTORED";
    public const string AlreadyPatched = "ALREADY_PATCHED";
    public const string NoHudmodel = "NO_HUDMODEL";
    public const string MultipleHudmodel = "MULTIPLE_HUDMODEL";
    public const string CustomGraphConflict = "CUSTOM_GRAPH_CONFLICT";
    public const string RerlMissing = "RERL_MISSING";
    public const string RerlIdMismatch = "RERL_ID_MISMATCH";
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string ParseFailed = "PARSE_FAILED";
    public const string FileNotFound = "FILE_NOT_FOUND";
    public const string SourceChanged = "SOURCE_CHANGED";
    public const string BackupMissing = "BACKUP_MISSING";
    public const string StagingMissing = "STAGING_MISSING";
    public const string ApplyFailed = "APPLY_FAILED";
    public const string RestoreFailed = "RESTORE_FAILED";
}

internal sealed class FileReport
{
    public string RelativePath { get; set; } = "";
    public string Status { get; set; } = "";
    public string Reason { get; set; } = "";
    public string BeforeSha256 { get; set; } = "";
    public string AfterSha256 { get; set; } = "";
    public long BeforeSize { get; set; }
    public long AfterSize { get; set; }
    public string OldHudGraph { get; set; } = "";
    public string NewHudGraph { get; set; } = "";
    public string OriginalKvFlag { get; set; } = "";
    public int DataPatchCount { get; set; }
    public int RerlPatchCount { get; set; }
    public bool BlockValidationPassed { get; set; }
    public bool DataValidationPassed { get; set; }
    public bool RerlValidationPassed { get; set; }
    public bool Source2ViewerValidationPassed { get; set; }
    public string BlockValidationResult { get; set; } = "";
    public List<BlockReport> BlocksBefore { get; set; } = [];
    public List<BlockReport> BlocksAfter { get; set; } = [];
    public string CurrentBeforeRestoreSha256 { get; set; } = "";
    public DateTimeOffset? AppliedAt { get; set; }
    public DateTimeOffset? RestoredAt { get; set; }
}

internal sealed class BlockReport
{
    public int Index { get; set; }
    public string Type { get; set; } = "";
    public int Size { get; set; }
    public string Sha256 { get; set; } = "";
}

internal sealed class BatchRun
{
    public int SchemaVersion { get; set; } = 1;
    public string RunId { get; set; } = "";
    public string Command { get; set; } = "";
    public string RunStatus { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string AssetRoot { get; set; } = "";
    public string ManifestPath { get; set; } = "";
    public string TargetGraph { get; set; } = ResourcePatcher.TargetGraph;
    public string TargetGraphResourceId { get; set; } = $"0x{ResourcePatcher.TargetGraphId:X16}";
    public string Source2ViewerPath { get; set; } = "";
    public string RunRoot { get; set; } = "";
    public string RunDirectory { get; set; } = "";
    public string StagingDirectory { get; set; } = "";
    public string BackupDirectory { get; set; } = "";
    public string RestoreSnapshotDirectory { get; set; } = "";
    public List<FileReport> Files { get; set; } = [];
}

internal sealed class ResourceInspection
{
    public string Status { get; init; } = "";
    public string Reason { get; init; } = "";
    public string BeforeSha256 { get; init; } = "";
    public long BeforeSize { get; init; }
    public string HudGraphPath { get; init; } = "";
    public string HudGraphFlag { get; init; } = "";
    public List<BlockReport> Blocks { get; init; } = [];
}

internal sealed class ValidationResult
{
    public bool Passed { get; init; }
    public string Reason { get; init; } = "";
    public string AfterSha256 { get; init; } = "";
    public long AfterSize { get; init; }
    public bool BlocksPassed { get; init; }
    public bool DataPassed { get; init; }
    public bool RerlPassed { get; init; }
    public bool Source2ViewerPassed { get; init; }
    public List<BlockReport> BlocksAfter { get; init; } = [];
}

internal sealed class CliOptions
{
    public string Command { get; private set; } = "scan";
    public string AssetRoot { get; private set; } = "";
    public string ManifestPath { get; private set; } = "";
    public string TargetGraph { get; private set; } = ResourcePatcher.TargetGraph;
    public string StagingRoot { get; private set; } = "";
    public string BackupRoot { get; private set; } = "";
    public string RunRoot { get; private set; } = "";
    public string RunId { get; private set; } = "";
    public string Source2ViewerPath { get; private set; } = "";
    public bool Yes { get; private set; }
    public bool DryRun { get; private set; } = true;
    public bool ShowHelp { get; private set; }

    public static CliOptions Parse(string[] args)
    {
        var result = new CliOptions();
        var index = 0;

        if (args.Length > 0 && !args[0].StartsWith("-", StringComparison.Ordinal))
        {
            var command = args[0].Trim().ToLowerInvariant();
            if (command is not ("scan" or "patch" or "verify" or "apply" or "restore"))
            {
                throw new ArgumentException($"알 수 없는 명령입니다: {args[0]}");
            }

            result.Command = command;
            index++;
        }

        while (index < args.Length)
        {
            var option = args[index++];
            switch (option.ToLowerInvariant())
            {
                case "-h":
                case "--help":
                    result.ShowHelp = true;
                    break;
                case "--yes":
                case "-y":
                    result.Yes = true;
                    break;
                case "--dry-run":
                    result.DryRun = true;
                    break;
                case "--asset-root":
                    result.AssetRoot = NextValue(args, ref index, option);
                    break;
                case "--manifest":
                    result.ManifestPath = NextValue(args, ref index, option);
                    break;
                case "--target-graph":
                    result.TargetGraph = NextValue(args, ref index, option);
                    break;
                case "--staging":
                    result.StagingRoot = NextValue(args, ref index, option);
                    break;
                case "--backup":
                    result.BackupRoot = NextValue(args, ref index, option);
                    break;
                case "--run-root":
                    result.RunRoot = NextValue(args, ref index, option);
                    break;
                case "--run-id":
                    result.RunId = NextValue(args, ref index, option);
                    break;
                case "--source2viewer":
                    result.Source2ViewerPath = NextValue(args, ref index, option);
                    break;
                default:
                    throw new ArgumentException($"알 수 없는 옵션입니다: {option}");
            }
        }

        if (string.IsNullOrWhiteSpace(result.AssetRoot))
        {
            result.AssetRoot = AppContext.BaseDirectory;
        }

        result.AssetRoot = Path.GetFullPath(result.AssetRoot);
        result.RunRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(result.RunRoot)
            ? Path.Combine(AppContext.BaseDirectory, "runs")
            : result.RunRoot);
        result.StagingRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(result.StagingRoot)
            ? Path.Combine(AppContext.BaseDirectory, "staging")
            : result.StagingRoot);
        result.BackupRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(result.BackupRoot)
            ? Path.Combine(AppContext.BaseDirectory, "backups")
            : result.BackupRoot);

        if (!string.IsNullOrWhiteSpace(result.ManifestPath))
        {
            result.ManifestPath = Path.GetFullPath(result.ManifestPath);
        }

        if (!string.IsNullOrWhiteSpace(result.Source2ViewerPath))
        {
            result.Source2ViewerPath = Path.GetFullPath(result.Source2ViewerPath);
        }

        return result;
    }

    private static string NextValue(string[] args, ref int index, string option)
    {
        if (index >= args.Length)
        {
            throw new ArgumentException($"{option} 뒤에 값이 필요합니다.");
        }

        return args[index++];
    }
}
