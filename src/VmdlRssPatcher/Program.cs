using System.Text;
using System.Buffers.Binary;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.Serialization.KeyValues;
using ValveResourceFormat.ResourceTypes;

internal static class Program
{
    private const string ViewTarget = "animation/rss/graphs/viewmodel.vnmgraph";
    private const string WorldTarget = "animation/rss/graphs/worldmodel.vnmgraph";
    private const ulong ViewTargetId = 0xD6AA21250E1DE440;
    private const ulong WorldTargetId = 0x00E023B440DAE5CD;

    private enum ResultKind { Changed, AlreadyApplied, Skipped, Error, DryRun }
    private sealed record PatchResult(ResultKind Kind, string[] Lines);
    private sealed record GraphState(
        IReadOnlyList<KVObject> ViewRefs,
        IReadOnlyList<KVObject> WorldRefs,
        string[] ViewPaths,
        string[] WorldPaths);

    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        var dryRun = args.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase));
        var pathArgument = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        var root = Path.GetFullPath(pathArgument ?? AppContext.BaseDirectory);

        Console.WriteLine("VMDL_C RSS 그래프 패처");
        Console.WriteLine("=======================");
        Console.WriteLine($"검색 폴더 : {root}");
        Console.WriteLine($"HUD 목표  : {ViewTarget}");
        Console.WriteLine($"월드 목표 : {WorldTarget}");
        Console.WriteLine($"모드       : {(dryRun ? "미리보기(파일 변경 없음)" : "실제 적용")}");
        Console.WriteLine();

        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"[오류] 폴더가 없습니다: {root}");
            PauseIfInteractive();
            return 2;
        }

        string[] files;
        try
        {
            files = Directory.EnumerateFiles(root, "*.vmdl_c", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[오류] 하위 폴더 검색 실패: {ex.Message}");
            PauseIfInteractive();
            return 2;
        }

        Console.WriteLine($"발견한 vmdl_c: {files.Length}개");
        Console.WriteLine();

        var preview = new List<(string File, string Relative, PatchResult Result)>();
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file);
            PatchResult result;
            try { result = PatchFile(file, true); }
            catch (Exception ex) { result = new(ResultKind.Error, [$"처리 실패: {ex.Message}"]); }
            preview.Add((file, relative, result));
            PrintResult(result.Kind == ResultKind.DryRun ? "변경 대상" : Label(result.Kind), relative, result.Lines);
        }

        var targets = preview.Where(x => x.Result.Kind == ResultKind.DryRun).ToArray();
        var already = preview.Count(x => x.Result.Kind == ResultKind.AlreadyApplied);
        var skipped = preview.Count(x => x.Result.Kind == ResultKind.Skipped);
        var previewErrors = preview.Count(x => x.Result.Kind == ResultKind.Error);

        Console.WriteLine("검사 요약");
        Console.WriteLine("---------");
        Console.WriteLine($"변경 대상    : {targets.Length}");
        Console.WriteLine($"이미 적용됨  : {already}");
        Console.WriteLine($"건너뜀       : {skipped}");
        Console.WriteLine($"오류         : {previewErrors}");
        Console.WriteLine();

        if (dryRun)
        {
            Console.WriteLine("미리보기 모드라 파일을 변경하지 않았습니다.");
            PauseIfInteractive();
            return previewErrors > 0 ? 1 : 0;
        }
        if (targets.Length == 0)
        {
            Console.WriteLine("변경할 모델이 없습니다. 어떤 파일도 건드리지 않았습니다.");
            PauseIfInteractive();
            return previewErrors > 0 ? 1 : 0;
        }

        Console.Write($"위 {targets.Length}개 모델을 RSS viewmodel/worldmodel 경로로 변경하시겠습니까? (y/n): ");
        var answer = Console.ReadLine()?.Trim();
        Console.WriteLine();
        if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("사용자가 취소했습니다. 어떤 파일도 변경하지 않았습니다.");
            PauseIfInteractive();
            return 0;
        }

        var changed = 0;
        var applyErrors = 0;
        Console.WriteLine("적용 결과");
        Console.WriteLine("---------");
        foreach (var item in targets)
        {
            PatchResult result;
            try { result = PatchFile(item.File, false); }
            catch (Exception ex) { result = new(ResultKind.Error, [$"처리 실패: {ex.Message}"]); }
            if (result.Kind == ResultKind.Changed) changed++;
            if (result.Kind == ResultKind.Error) applyErrors++;
            PrintResult(Label(result.Kind), item.Relative, result.Lines);
        }

        Console.WriteLine("최종 요약");
        Console.WriteLine("---------");
        Console.WriteLine($"변경 완료    : {changed}");
        Console.WriteLine($"이미 적용됨  : {already}");
        Console.WriteLine($"건너뜀       : {skipped}");
        Console.WriteLine($"오류         : {previewErrors + applyErrors}");
        Console.WriteLine();
        Console.WriteLine("변경된 원본에는 최초 1회만 .rsspatch.bak 백업이 생성됩니다.");
        PauseIfInteractive();
        return previewErrors + applyErrors > 0 ? 1 : 0;
    }

    private static PatchResult PatchFile(string file, bool dryRun)
    {
        var original = File.ReadAllBytes(file);
        using var input = new MemoryStream(original, writable: false);
        using var resource = ReadResource(file, input);
        var state = ReadGraphState(resource);

        if (state.ViewRefs.Count == 0 && state.WorldRefs.Count == 0)
            return new(ResultKind.Skipped, ["m_animGraph2Refs에 hudmodel, worldmodel 또는 빈 기본 엔트리가 없음"]);
        if (state.ViewRefs.Count > 1)
            return new(ResultKind.Skipped, [$"모호한 hudmodel 엔트리 {state.ViewRefs.Count}개 — 자동 수정하지 않음"]);
        if (state.WorldRefs.Count(r => Identifier(r).Length == 0) > 1 || state.WorldRefs.Count(r => Identifier(r) == "worldmodel") > 1)
            return new(ResultKind.Skipped, ["모호한 worldmodel/빈 기본 엔트리 — 자동 수정하지 않음"]);
        if (state.ViewPaths.Any(string.IsNullOrWhiteSpace) || state.WorldPaths.Any(string.IsNullOrWhiteSpace))
            return new(ResultKind.Skipped, ["그래프 경로가 비어 있는 대상 엔트리 — 자동 수정하지 않음"]);

        var lines = new List<string>();
        Describe("viewmodel(hudmodel)", state.ViewPaths, ViewTarget, lines);
        Describe("worldmodel/빈 기본 엔트리", state.WorldPaths, WorldTarget, lines);

        var rerl = resource.ExternalReferences ?? throw new InvalidDataException("RERL 외부 참조 블록이 없습니다.");
        var dataApplied = state.ViewPaths.All(p => PathEquals(p, ViewTarget))
            && state.WorldPaths.All(p => PathEquals(p, WorldTarget));
        var rerlApplied = CategoryRerlApplied(rerl, state.ViewPaths, ViewTarget, ViewTargetId)
            && CategoryRerlApplied(rerl, state.WorldPaths, WorldTarget, WorldTargetId);

        if (dataApplied && rerlApplied)
            return new(ResultKind.AlreadyApplied, [.. lines, "DATA와 RERL 모두 이미 적용됨 — 파일을 건드리지 않음"]);
        if (dryRun)
            return new(ResultKind.DryRun, [.. lines, "변경 예정 — 아직 파일을 건드리지 않음"]);

        SetGraphPaths(state.ViewRefs, ViewTarget);
        SetGraphPaths(state.WorldRefs, WorldTarget);
        PatchRerlCategory(rerl, state.ViewPaths, ViewTarget, ViewTargetId);
        PatchRerlCategory(rerl, state.WorldPaths, WorldTarget, WorldTargetId);

        using var output = new MemoryStream();
        resource.Serialize(output);
        var modified = RebuildWithPatchedDataBlocks(original, output.ToArray());
        ValidateSerialized(file, modified, state.ViewRefs.Count, state.WorldRefs.Count);

        var backup = file + ".rsspatch.bak";
        if (!File.Exists(backup))
        {
            File.Copy(file, backup, overwrite: false);
            lines.Add($"백업 생성: {Path.GetFileName(backup)}");
        }
        else lines.Add($"기존 백업 유지: {Path.GetFileName(backup)}");

        var temporary = file + ".rsspatch.tmp";
        try
        {
            File.WriteAllBytes(temporary, modified);
            File.Move(temporary, file, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }

        lines.Add("DATA(Binary KV3)와 RERL을 함께 수정하고 재검증 완료");
        return new(ResultKind.Changed, [.. lines]);
    }

    private static Resource ReadResource(string file, Stream input)
    {
        var resource = new Resource { FileName = file };
        try { resource.Read(input, verifyFileSize: true, leaveOpen: true); return resource; }
        catch { resource.Dispose(); throw; }
    }

    private static GraphState ReadGraphState(Resource resource)
    {
        var root = (resource.DataBlock as KeyValuesOrNTRO)?.Data
            ?? throw new InvalidDataException("DATA 블록을 KeyValues로 읽을 수 없습니다.");
        var all = root.GetArray("m_animGraph2Refs") ?? [];
        var view = all.Where(r => Identifier(r) == "hudmodel").ToArray();
        var world = all.Where(r => Identifier(r) == "worldmodel"
            || (Identifier(r).Length == 0 && IsWorldGraphPath(r.GetStringProperty("m_hGraph", "")))).ToArray();
        return new(view, world,
            view.Select(r => r.GetStringProperty("m_hGraph", "")).ToArray(),
            world.Select(r => r.GetStringProperty("m_hGraph", "")).ToArray());
    }

    private static string Identifier(KVObject graphRef)
        => graphRef.GetStringProperty("m_sIdentifier", "").Trim().ToLowerInvariant();

    private static void SetGraphPaths(IEnumerable<KVObject> refs, string target)
    {
        foreach (var graphRef in refs)
        {
            KVObject value = target;
            value.Flag = KVFlag.Resource;
            graphRef["m_hGraph"] = value;
        }
    }

    private static bool CategoryRerlApplied(ResourceExtRefList rerl, string[] paths, string target, ulong targetId)
    {
        if (paths.Length == 0) return true;
        return paths.All(p => PathEquals(p, target))
            && rerl.ResourceRefInfoList.Any(e => PathEquals(e.Name, target) && e.Id == targetId);
    }

    private static void PatchRerlCategory(ResourceExtRefList rerl, string[] oldPaths, string target, ulong targetId)
    {
        if (oldPaths.Length == 0) return;
        var list = rerl.ResourceRefInfoList;
        var targetEntry = list.FirstOrDefault(e => PathEquals(e.Name, target));
        var oldEntries = list.Where(e => oldPaths.Any(p => PathEquals(e.Name, p))).ToArray();

        if (targetEntry == null)
        {
            targetEntry = oldEntries.FirstOrDefault();
            if (targetEntry == null)
            {
                targetEntry = new ResourceExtRefList.ResourceReferenceInfo { Id = targetId, Name = target };
                list.Add(targetEntry);
            }
            targetEntry.Name = target;
        }
        targetEntry.Id = targetId;
        foreach (var duplicate in oldEntries.Where(e => !ReferenceEquals(e, targetEntry)).ToArray()) list.Remove(duplicate);
    }

    private sealed record RawBlock(uint Type, byte[] Data);

    private static byte[] RebuildWithPatchedDataBlocks(byte[] original, byte[] serialized)
    {
        var originalBlocks = ReadRawBlocks(original);
        var patchedBlocks = ReadRawBlocks(serialized)
            .Where(b => b.Type == FourCc("RERL") || b.Type == FourCc("DATA"))
            .ToDictionary(b => b.Type, b => b.Data);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(0xDEADBEEFu);
        writer.Write(BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(4, 2)));
        writer.Write(BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(6, 2)));
        writer.Write(8u);
        writer.Write(originalBlocks.Count);
        foreach (var block in originalBlocks)
        {
            writer.Write(block.Type);
            writer.Write(0xDEADBEEFu);
            writer.Write(0xDEADBEEFu);
        }

        for (var i = 0; i < originalBlocks.Count; i++)
        {
            var padding = (int)((16 - stream.Position % 16) % 16);
            if (padding >= 5)
            {
                var s2vStart = padding / 2 - 1;
                writer.Write(new byte[s2vStart]);
                writer.Write((byte)'S'); writer.Write((byte)'2'); writer.Write((byte)'V');
                padding -= s2vStart + 3;
            }
            writer.Write(new byte[padding]);

            var blockOffset = stream.Position;
            var block = originalBlocks[i];
            var data = patchedBlocks.TryGetValue(block.Type, out var replacement) ? replacement : block.Data;
            writer.Write(data);
            var end = stream.Position;
            var metadataOffset = 20L + i * 12L;
            stream.Position = metadataOffset;
            writer.Write(checked((uint)(blockOffset - metadataOffset)));
            writer.Write(checked((uint)data.Length));
            stream.Position = end;
        }

        stream.Position = 0;
        writer.Write(checked((uint)stream.Length));
        writer.Flush();
        return stream.ToArray();
    }

    private static List<RawBlock> ReadRawBlocks(byte[] bytes)
    {
        if (bytes.Length < 16) throw new InvalidDataException("Source 2 헤더가 너무 짧습니다.");
        var table = checked(8 + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4)));
        var count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4)));
        var blocks = new List<RawBlock>(count);
        for (var i = 0; i < count; i++)
        {
            var entry = checked(table + i * 12);
            if (entry < 0 || entry > bytes.Length - 12) throw new InvalidDataException("블록 테이블 범위 오류");
            var type = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entry, 4));
            var offset = checked(entry + 4 + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entry + 4, 4)));
            var size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entry + 8, 4)));
            if (offset < 0 || size < 0 || offset > bytes.Length - size) throw new InvalidDataException("블록 범위 오류");
            blocks.Add(new(type, bytes.AsSpan(offset, size).ToArray()));
        }
        return blocks;
    }

    private static uint FourCc(string value)
        => BinaryPrimitives.ReadUInt32LittleEndian(Encoding.ASCII.GetBytes(value));
    private static void ValidateSerialized(string file, byte[] bytes, int expectedViews, int expectedWorlds)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var check = ReadResource(file, stream);
        var state = ReadGraphState(check);
        var rerl = check.ExternalReferences ?? throw new InvalidDataException("재검증 중 RERL 블록이 없습니다.");
        if (state.ViewRefs.Count != expectedViews || state.WorldRefs.Count != expectedWorlds
            || state.ViewPaths.Any(p => !PathEquals(p, ViewTarget))
            || state.WorldPaths.Any(p => !PathEquals(p, WorldTarget))
            || (expectedViews > 0 && !rerl.ResourceRefInfoList.Any(e => PathEquals(e.Name, ViewTarget) && e.Id == ViewTargetId))
            || (expectedWorlds > 0 && !rerl.ResourceRefInfoList.Any(e => PathEquals(e.Name, WorldTarget) && e.Id == WorldTargetId)))
            throw new InvalidDataException("수정 후 DATA/RERL 자체 검증에 실패했습니다.");
    }

    private static void Describe(string label, string[] paths, string target, List<string> lines)
    {
        if (paths.Length == 0) { lines.Add($"{label}: 엔트리 없음 (임의 추가하지 않음)"); return; }
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (PathEquals(path, target)) lines.Add($"{label}: 이미 적용됨 — {target}");
            else { lines.Add($"{label}: {path}"); lines.Add($"         → {target}"); }
        }
    }

    private static bool IsWorldGraphPath(string path)
        => Normalize(path).EndsWith("/worldmodel.vnmgraph", StringComparison.Ordinal);

    private static bool PathEquals(string left, string right)
        => Normalize(left).Equals(Normalize(right), StringComparison.Ordinal);
    private static string Normalize(string value)
        => value.Replace('\\', '/').Trim().ToLowerInvariant();

    private static string Label(ResultKind kind) => kind switch
    {
        ResultKind.Changed => "변경", ResultKind.AlreadyApplied => "이미 적용됨",
        ResultKind.DryRun => "미리보기", ResultKind.Skipped => "건너뜀",
        ResultKind.Error => "오류", _ => kind.ToString(),
    };

    private static void PrintResult(string label, string relative, IEnumerable<string> lines)
    {
        Console.WriteLine($"[{label}] {relative}");
        foreach (var line in lines) Console.WriteLine($"  {line}");
        Console.WriteLine();
    }

    private static void PauseIfInteractive()
    {
        if (!Console.IsInputRedirected)
        {
            Console.WriteLine("종료하려면 Enter 키를 누르세요.");
            Console.ReadLine();
        }
    }
}