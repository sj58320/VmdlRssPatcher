internal static class BatchCommands
{
    public static int Scan(CliOptions options)
    {
        var run = RunStore.Create(options, "scan");
        var candidates = RunStore.ResolveCandidates(run, options);

        PrintHeader("SCAN / DRY RUN", run, candidates.Count);
        run.Files = InspectCandidates(candidates);
        run.RunStatus = "SCANNED";
        RunStore.Save(run, "scan");
        PrintSummary(run);
        PrintRunLocation(run);
        return run.Files.Any(file => file.Status == Statuses.ParseFailed) ? 1 : 0;
    }

    public static int Patch(CliOptions options)
    {
        EnsureFixedTarget(options);
        var run = RunStore.Create(options, "patch");
        EnsureSource2Viewer(run.Source2ViewerPath);

        if (!ResourcePatcher.ValidateTargetGraph(
                run.AssetRoot,
                run.Source2ViewerPath,
                out _,
                out var targetReason))
        {
            throw new InvalidOperationException(targetReason);
        }

        var candidates = RunStore.ResolveCandidates(run, options);
        PrintHeader("PATCH TO STAGING", run, candidates.Count);
        Console.WriteLine(targetReason);
        Console.WriteLine();

        run.Files = InspectCandidates(candidates);
        run.RunStatus = "AWAITING_CONFIRMATION";
        RunStore.Save(run, "patch-preview");
        PrintSummary(run);

        var patchable = run.Files.Where(file => file.Status == Statuses.Patchable).ToArray();
        if (patchable.Length == 0)
        {
            run.RunStatus = "NO_PATCHABLE_FILES";
            RunStore.Save(run, "patch");
            PrintRunLocation(run);
            return 0;
        }

        if (!Confirm(
                options,
                $"위 {patchable.Length}개 hudmodel을 staging에 패치하고 별도 backup을 생성하시겠습니까?"))
        {
            run.RunStatus = "CANCELLED";
            RunStore.Save(run, "patch-cancelled");
            Console.WriteLine("취소했습니다. 에셋, staging, backup을 변경하지 않았습니다.");
            PrintRunLocation(run);
            return 0;
        }

        Directory.CreateDirectory(run.StagingDirectory);
        Directory.CreateDirectory(run.BackupDirectory);

        foreach (var report in patchable)
        {
            var assetFile = RunStore.ResolveAssetFile(run, report.RelativePath);
            var backupFile = RunStore.ResolveRunFile(run, run.BackupDirectory, report.RelativePath);
            var stagingFile = RunStore.ResolveRunFile(run, run.StagingDirectory, report.RelativePath);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
                File.Copy(assetFile, backupFile, overwrite: false);

                if (!string.Equals(
                        ResourcePatcher.Sha256File(backupFile),
                        report.BeforeSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("backup SHA-256이 원본 기록과 다릅니다.");
                }

                var validation = ResourcePatcher.PatchToStaging(
                    backupFile,
                    stagingFile,
                    run.Source2ViewerPath);
                ApplyValidation(report, validation);

                if (validation.Passed)
                {
                    report.Status = Statuses.Patched;
                    report.Reason = validation.Reason;
                    report.OldHudGraph = ResourcePatcher.OldGraph;
                    report.NewHudGraph = ResourcePatcher.TargetGraph;
                    report.DataPatchCount = 1;
                    report.RerlPatchCount = 1;
                    Console.WriteLine($"[PATCHED] {report.RelativePath}");
                }
                else
                {
                    report.Status = Statuses.ValidationFailed;
                    report.Reason = validation.Reason;
                    Console.WriteLine($"[VALIDATION_FAILED] {report.RelativePath}: {report.Reason}");
                }
            }
            catch (Exception ex)
            {
                report.Status = Statuses.ValidationFailed;
                report.Reason = ex.Message;
                Console.WriteLine($"[VALIDATION_FAILED] {report.RelativePath}: {ex.Message}");
            }
        }

        run.RunStatus = "STAGED";
        RunStore.Save(run, "patch");
        PrintSummary(run);
        PrintRunLocation(run);
        Console.WriteLine();
        Console.WriteLine("다음 단계:");
        Console.WriteLine(
            $"  VmdlHudGraphBatchPatcher.exe verify --run-id {run.RunId} --run-root \"{run.RunRoot}\" --source2viewer \"{run.Source2ViewerPath}\"");
        return run.Files.Any(file => file.Status == Statuses.ValidationFailed) ? 1 : 0;
    }

    public static int Verify(CliOptions options)
    {
        var run = RunStore.Load(options);
        var source2Viewer = ResolveStoredSource2Viewer(run, options);
        EnsureSource2Viewer(source2Viewer);
        run.Source2ViewerPath = source2Viewer;

        if (!ResourcePatcher.ValidateTargetGraph(
                run.AssetRoot,
                source2Viewer,
                out _,
                out var targetReason))
        {
            throw new InvalidOperationException(targetReason);
        }

        Console.WriteLine($"VERIFY RUN {run.RunId}");
        Console.WriteLine(targetReason);
        Console.WriteLine();

        foreach (var report in run.Files.Where(file =>
                     file.Status is Statuses.Patched or Statuses.Verified))
        {
            var backupFile = RunStore.ResolveRunFile(run, run.BackupDirectory, report.RelativePath);
            var stagingFile = RunStore.ResolveRunFile(run, run.StagingDirectory, report.RelativePath);

            if (!File.Exists(backupFile))
            {
                report.Status = Statuses.BackupMissing;
                report.Reason = $"backup이 없습니다: {backupFile}";
                Console.WriteLine($"[BACKUP_MISSING] {report.RelativePath}");
                continue;
            }

            if (!File.Exists(stagingFile))
            {
                report.Status = Statuses.StagingMissing;
                report.Reason = $"staging이 없습니다: {stagingFile}";
                Console.WriteLine($"[STAGING_MISSING] {report.RelativePath}");
                continue;
            }

            if (!string.Equals(
                    ResourcePatcher.Sha256File(backupFile),
                    report.BeforeSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                report.Status = Statuses.ValidationFailed;
                report.Reason = "backup SHA-256이 최초 원본 기록과 다릅니다.";
                Console.WriteLine($"[VALIDATION_FAILED] {report.RelativePath}: {report.Reason}");
                continue;
            }

            var validation = ResourcePatcher.Validate(backupFile, stagingFile, source2Viewer);
            ApplyValidation(report, validation);
            if (validation.Passed
                && (string.IsNullOrWhiteSpace(report.AfterSha256)
                    || string.Equals(
                        report.AfterSha256,
                        validation.AfterSha256,
                        StringComparison.OrdinalIgnoreCase)))
            {
                report.Status = Statuses.Verified;
                report.Reason = validation.Reason;
                Console.WriteLine($"[VERIFIED] {report.RelativePath}");
            }
            else
            {
                report.Status = Statuses.ValidationFailed;
                report.Reason = validation.Passed
                    ? "staging SHA-256이 patch 실행 기록과 다릅니다."
                    : validation.Reason;
                Console.WriteLine($"[VALIDATION_FAILED] {report.RelativePath}: {report.Reason}");
            }
        }

        run.RunStatus = run.Files.Any(file => file.Status == Statuses.Verified)
            ? "VERIFIED"
            : "VERIFICATION_FAILED";
        RunStore.Save(run, "verify");
        PrintSummary(run);
        PrintRunLocation(run);

        if (run.RunStatus == "VERIFIED")
        {
            Console.WriteLine();
            Console.WriteLine("검증된 파일만 반영하려면:");
            Console.WriteLine(
                $"  VmdlHudGraphBatchPatcher.exe apply --run-id {run.RunId} --run-root \"{run.RunRoot}\"");
        }

        return run.RunStatus == "VERIFIED" ? 0 : 1;
    }

    public static int Apply(CliOptions options)
    {
        var run = RunStore.Load(options);
        var verified = run.Files.Where(file => file.Status == Statuses.Verified).ToArray();
        if (verified.Length == 0)
        {
            throw new InvalidOperationException("apply할 VERIFIED 파일이 없습니다. 먼저 verify를 실행하세요.");
        }

        Console.WriteLine($"APPLY RUN {run.RunId}");
        Console.WriteLine($"검증된 파일: {verified.Length}개");
        Console.WriteLine($"에셋 루트: {run.AssetRoot}");
        Console.WriteLine();

        if (!Confirm(options, "검증된 staging 파일을 실제 에셋에 반영하시겠습니까?"))
        {
            Console.WriteLine("취소했습니다. 실제 에셋을 변경하지 않았습니다.");
            return 0;
        }

        foreach (var report in verified)
        {
            var assetFile = RunStore.ResolveAssetFile(run, report.RelativePath);
            var backupFile = RunStore.ResolveRunFile(run, run.BackupDirectory, report.RelativePath);
            var stagingFile = RunStore.ResolveRunFile(run, run.StagingDirectory, report.RelativePath);

            try
            {
                if (!File.Exists(assetFile)
                    || !string.Equals(
                        ResourcePatcher.Sha256File(assetFile),
                        report.BeforeSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    report.Status = Statuses.SourceChanged;
                    report.Reason = "patch 이후 실제 에셋 원본이 변경되어 덮어쓰지 않았습니다.";
                    Console.WriteLine($"[SOURCE_CHANGED] {report.RelativePath}");
                    continue;
                }

                if (!File.Exists(backupFile)
                    || !string.Equals(
                        ResourcePatcher.Sha256File(backupFile),
                        report.BeforeSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    report.Status = Statuses.BackupMissing;
                    report.Reason = "유효한 backup이 없어 적용하지 않았습니다.";
                    Console.WriteLine($"[BACKUP_MISSING] {report.RelativePath}");
                    continue;
                }

                if (!File.Exists(stagingFile)
                    || !string.Equals(
                        ResourcePatcher.Sha256File(stagingFile),
                        report.AfterSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    report.Status = Statuses.StagingMissing;
                    report.Reason = "검증된 staging 파일이 없거나 SHA-256이 달라 적용하지 않았습니다.";
                    Console.WriteLine($"[STAGING_MISSING] {report.RelativePath}");
                    continue;
                }

                ResourcePatcher.AtomicReplaceFromFile(stagingFile, assetFile);
                if (!string.Equals(
                        ResourcePatcher.Sha256File(assetFile),
                        report.AfterSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("원자적 교체 후 SHA-256 검증에 실패했습니다.");
                }

                report.Status = Statuses.Applied;
                report.Reason = "검증된 staging 파일을 실제 에셋에 원자적으로 반영했습니다.";
                report.AppliedAt = DateTimeOffset.Now;
                Console.WriteLine($"[APPLIED] {report.RelativePath}");
            }
            catch (Exception ex)
            {
                report.Status = Statuses.ApplyFailed;
                report.Reason = ex.Message;
                Console.WriteLine($"[APPLY_FAILED] {report.RelativePath}: {ex.Message}");
            }
        }

        run.RunStatus = run.Files.Any(file => file.Status == Statuses.Applied)
            ? "APPLIED"
            : "APPLY_FAILED";
        RunStore.Save(run, "apply");
        PrintSummary(run);
        PrintRunLocation(run);
        return run.RunStatus == "APPLIED" ? 0 : 1;
    }

    public static int Restore(CliOptions options)
    {
        var run = RunStore.Load(options);
        var applied = run.Files.Where(file => file.Status == Statuses.Applied).ToArray();
        if (applied.Length == 0)
        {
            throw new InvalidOperationException("복원할 APPLIED 파일이 없습니다.");
        }

        Console.WriteLine($"RESTORE RUN {run.RunId}");
        Console.WriteLine($"복원 대상: {applied.Length}개");
        Console.WriteLine();

        if (!Confirm(options, "backup을 사용해 이 실행 이전 상태로 복원하시겠습니까?"))
        {
            Console.WriteLine("취소했습니다. 실제 에셋을 변경하지 않았습니다.");
            return 0;
        }

        var restoreId = DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss_fff");
        var snapshotRoot = Path.Combine(run.RunDirectory, "restore_snapshots", restoreId);
        run.RestoreSnapshotDirectory = snapshotRoot;

        foreach (var report in applied)
        {
            var assetFile = RunStore.ResolveAssetFile(run, report.RelativePath);
            var backupFile = RunStore.ResolveRunFile(run, run.BackupDirectory, report.RelativePath);
            var snapshotFile = RunStore.ResolveRunFile(run, snapshotRoot, report.RelativePath);

            try
            {
                if (!File.Exists(backupFile)
                    || !string.Equals(
                        ResourcePatcher.Sha256File(backupFile),
                        report.BeforeSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    report.Status = Statuses.BackupMissing;
                    report.Reason = "최초 원본 SHA-256과 일치하는 backup이 없습니다.";
                    Console.WriteLine($"[BACKUP_MISSING] {report.RelativePath}");
                    continue;
                }

                if (!File.Exists(assetFile))
                {
                    throw new FileNotFoundException($"현재 에셋 파일이 없습니다: {assetFile}");
                }

                report.CurrentBeforeRestoreSha256 = ResourcePatcher.Sha256File(assetFile);
                Directory.CreateDirectory(Path.GetDirectoryName(snapshotFile)!);
                File.Copy(assetFile, snapshotFile, overwrite: false);
                ResourcePatcher.AtomicReplaceFromFile(backupFile, assetFile);

                if (!string.Equals(
                        ResourcePatcher.Sha256File(assetFile),
                        report.BeforeSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("복원 후 SHA-256이 최초 원본과 다릅니다.");
                }

                report.Status = Statuses.Restored;
                report.Reason = "현재 파일을 restore snapshot에 보관하고 최초 backup으로 복원했습니다.";
                report.RestoredAt = DateTimeOffset.Now;
                Console.WriteLine($"[RESTORED] {report.RelativePath}");
            }
            catch (Exception ex)
            {
                report.Status = Statuses.RestoreFailed;
                report.Reason = ex.Message;
                Console.WriteLine($"[RESTORE_FAILED] {report.RelativePath}: {ex.Message}");
            }
        }

        run.RunStatus = run.Files.Any(file => file.Status == Statuses.Restored)
            ? "RESTORED"
            : "RESTORE_FAILED";
        RunStore.Save(run, "restore");
        PrintSummary(run);
        PrintRunLocation(run);
        return run.RunStatus == "RESTORED" ? 0 : 1;
    }

    private static List<FileReport> InspectCandidates(IReadOnlyList<ModelCandidate> candidates)
    {
        var reports = new List<FileReport>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (!candidate.Exists)
            {
                var missing = new FileReport
                {
                    RelativePath = candidate.RelativePath,
                    Status = Statuses.FileNotFound,
                    Reason = "manifest에 등록된 파일이 없습니다.",
                };
                reports.Add(missing);
                Console.WriteLine($"[FILE_NOT_FOUND] {candidate.RelativePath}");
                continue;
            }

            var inspection = ResourcePatcher.Inspect(candidate.FullPath);
            var report = new FileReport
            {
                RelativePath = candidate.RelativePath,
                Status = inspection.Status,
                Reason = inspection.Reason,
                BeforeSha256 = inspection.BeforeSha256,
                BeforeSize = inspection.BeforeSize,
                OldHudGraph = inspection.HudGraphPath,
                NewHudGraph = inspection.Status == Statuses.Patchable
                    ? ResourcePatcher.TargetGraph
                    : inspection.HudGraphPath,
                OriginalKvFlag = inspection.HudGraphFlag,
                BlocksBefore = inspection.Blocks,
            };
            reports.Add(report);
            Console.WriteLine($"[{report.Status}] {report.RelativePath}");
            Console.WriteLine($"  {report.Reason}");
            if (!string.IsNullOrWhiteSpace(report.OldHudGraph))
            {
                Console.WriteLine($"  hudmodel: {report.OldHudGraph}");
                if (report.Status == Statuses.Patchable)
                {
                    Console.WriteLine($"          -> {ResourcePatcher.TargetGraph}");
                }
            }
        }

        Console.WriteLine();
        return reports;
    }

    private static void ApplyValidation(FileReport report, ValidationResult validation)
    {
        report.AfterSha256 = validation.AfterSha256;
        report.AfterSize = validation.AfterSize;
        report.BlockValidationPassed = validation.BlocksPassed;
        report.DataValidationPassed = validation.DataPassed;
        report.RerlValidationPassed = validation.RerlPassed;
        report.Source2ViewerValidationPassed = validation.Source2ViewerPassed;
        report.BlockValidationResult = validation.Reason;
        report.BlocksAfter = validation.BlocksAfter;
    }

    private static void EnsureFixedTarget(CliOptions options)
    {
        if (!string.Equals(
                options.TargetGraph.Replace('\\', '/').Trim(),
                ResourcePatcher.TargetGraph,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"안전상 검증된 고정 target만 지원합니다: {ResourcePatcher.TargetGraph}");
        }
    }

    private static string ResolveStoredSource2Viewer(BatchRun run, CliOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Source2ViewerPath))
        {
            return RunStore.ResolveSource2Viewer(options.Source2ViewerPath);
        }

        if (!string.IsNullOrWhiteSpace(run.Source2ViewerPath)
            && File.Exists(run.Source2ViewerPath))
        {
            return run.Source2ViewerPath;
        }

        return RunStore.ResolveSource2Viewer("");
    }

    private static void EnsureSource2Viewer(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException(
                "엄격 검증에 Source2Viewer-CLI.exe가 필요합니다. "
                + "--source2viewer <경로>로 지정하거나 EXE 옆에 두세요.");
        }
    }

    private static bool Confirm(CliOptions options, string question)
    {
        if (options.Yes)
        {
            return true;
        }

        if (Console.IsInputRedirected)
        {
            throw new InvalidOperationException(
                "입력이 리다이렉트된 환경에서는 명시적으로 --yes를 지정해야 합니다.");
        }

        Console.Write($"{question} (y/n): ");
        return string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase);
    }

    private static void PrintHeader(
        string title,
        BatchRun run,
        int candidateCount)
    {
        Console.WriteLine($"VMDL HUD GRAPH BATCH PATCHER - {title}");
        Console.WriteLine(new string('=', 48));
        Console.WriteLine($"실행 ID    : {run.RunId}");
        Console.WriteLine($"에셋 루트  : {run.AssetRoot}");
        Console.WriteLine($"대상 수    : {candidateCount}");
        Console.WriteLine($"기존 HUD   : {ResourcePatcher.OldGraph}");
        Console.WriteLine($"RSS HUD    : {ResourcePatcher.TargetGraph}");
        Console.WriteLine("worldmodel : 변경하지 않음");
        Console.WriteLine();
    }

    private static void PrintSummary(BatchRun run)
    {
        Console.WriteLine();
        Console.WriteLine("요약");
        Console.WriteLine("----");
        foreach (var group in run.Files
                     .GroupBy(file => file.Status)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"{group.Key,-24} {group.Count(),5}");
        }
    }

    private static void PrintRunLocation(BatchRun run)
    {
        Console.WriteLine();
        Console.WriteLine($"보고서 JSON: {Path.Combine(run.RunDirectory, "report.json")}");
        Console.WriteLine($"보고서 CSV : {Path.Combine(run.RunDirectory, "report.csv")}");
        Console.WriteLine($"run ID     : {run.RunId}");
    }
}
