using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

internal static class ResourcePatcher
{
    public const string OldGraph = "animation/graphs/viewmodel/viewmodel.vnmgraph";
    public const string TargetGraph = "animation/rss/graphs/viewmodel.vnmgraph";
    public const ulong OldGraphId = 0xC5111C601E968C98;
    public const ulong TargetGraphId = 0xD6AA21250E1DE440;

    private static readonly uint RerlFourCc = FourCc("RERL");
    private static readonly uint DataFourCc = FourCc("DATA");
    private static readonly HashSet<string> ProtectedBlockTypes =
    [
        "MRPH", "MVTX", "MIDX", "MDAT", "ANIM", "ASEQ", "AGRP", "PHYS",
    ];

    public static ResourceInspection Inspect(string file)
    {
        try
        {
            var bytes = File.ReadAllBytes(file);
            using var input = new MemoryStream(bytes, writable: false);
            using var resource = ReadResource(file, input);
            var root = GetDataRoot(resource);
            var graphs = GetGraphRefs(root);
            var hudRefs = graphs.Where(graph => Identifier(graph) == "hudmodel").ToArray();
            var blocks = ReadRawBlocks(bytes).Select(ToBlockReport).ToList();
            var sha = Sha256(bytes);

            if (hudRefs.Length == 0)
            {
                return Inspection(Statuses.NoHudmodel, "hudmodel 엔트리가 없습니다.", sha, bytes.Length, blocks);
            }

            if (hudRefs.Length > 1)
            {
                return Inspection(
                    Statuses.MultipleHudmodel,
                    $"hudmodel 엔트리가 {hudRefs.Length}개입니다.",
                    sha,
                    bytes.Length,
                    blocks);
            }

            if (!hudRefs[0].TryGetValue("m_hGraph", out var graphValue)
                || graphValue.ValueType != KVValueType.String)
            {
                return Inspection(
                    Statuses.ParseFailed,
                    "hudmodel.m_hGraph 문자열을 읽을 수 없습니다.",
                    sha,
                    bytes.Length,
                    blocks);
            }

            var path = (string)graphValue;
            var flag = graphValue.Flag.ToString();
            var rerl = resource.ExternalReferences;
            if (rerl == null)
            {
                return Inspection(Statuses.RerlMissing, "RERL 블록이 없습니다.", sha, bytes.Length, blocks, path, flag);
            }

            var oldEntries = rerl.ResourceRefInfoList.Where(entry => PathEquals(entry.Name, OldGraph)).ToArray();
            var targetEntries = rerl.ResourceRefInfoList.Where(entry => PathEquals(entry.Name, TargetGraph)).ToArray();

            if (PathEquals(path, TargetGraph))
            {
                if (targetEntries.Length == 1 && targetEntries[0].Id == TargetGraphId)
                {
                    return Inspection(
                        Statuses.AlreadyPatched,
                        "hudmodel DATA와 RERL이 이미 RSS 경로를 사용합니다.",
                        sha,
                        bytes.Length,
                        blocks,
                        path,
                        flag);
                }

                return Inspection(
                    Statuses.RerlMissing,
                    "DATA는 RSS 경로지만 일치하는 RERL 경로/ID가 정확히 1개가 아닙니다.",
                    sha,
                    bytes.Length,
                    blocks,
                    path,
                    flag);
            }

            if (!PathEquals(path, OldGraph))
            {
                return Inspection(
                    Statuses.CustomGraphConflict,
                    $"Valve 기본 경로가 아닌 커스텀 hudmodel입니다: {path}",
                    sha,
                    bytes.Length,
                    blocks,
                    path,
                    flag);
            }

            if (oldEntries.Length != 1 || targetEntries.Length != 0)
            {
                return Inspection(
                    Statuses.RerlMissing,
                    $"기존 RERL 대상은 정확히 1개여야 하고 RSS 대상은 없어야 합니다. old={oldEntries.Length}, target={targetEntries.Length}",
                    sha,
                    bytes.Length,
                    blocks,
                    path,
                    flag);
            }

            if (oldEntries[0].Id != OldGraphId)
            {
                return Inspection(
                    Statuses.RerlIdMismatch,
                    $"기존 RERL ID가 다릅니다: 0x{oldEntries[0].Id:X16}",
                    sha,
                    bytes.Length,
                    blocks,
                    path,
                    flag);
            }

            return Inspection(
                Statuses.Patchable,
                "hudmodel DATA와 RERL을 안전하게 패치할 수 있습니다.",
                sha,
                bytes.Length,
                blocks,
                path,
                flag);
        }
        catch (Exception ex)
        {
            return new ResourceInspection
            {
                Status = Statuses.ParseFailed,
                Reason = ex.Message,
            };
        }
    }

    public static ValidationResult PatchToStaging(
        string originalFile,
        string stagingFile,
        string source2ViewerPath)
    {
        var originalBytes = File.ReadAllBytes(originalFile);
        using var input = new MemoryStream(originalBytes, writable: false);
        using var resource = ReadResource(originalFile, input);
        var root = GetDataRoot(resource);
        var hudRefs = GetGraphRefs(root).Where(graph => Identifier(graph) == "hudmodel").ToArray();

        if (hudRefs.Length != 1
            || !hudRefs[0].TryGetValue("m_hGraph", out var oldValue)
            || oldValue.ValueType != KVValueType.String
            || !PathEquals((string)oldValue, OldGraph))
        {
            return Failed("패치 직전 DATA 대상이 정확히 1개인지 재검증하지 못했습니다.");
        }

        var rerl = resource.ExternalReferences;
        if (rerl == null)
        {
            return Failed("RERL 블록이 없습니다.");
        }

        var oldRerlEntries = rerl.ResourceRefInfoList
            .Where(entry => PathEquals(entry.Name, OldGraph) && entry.Id == OldGraphId)
            .ToArray();
        var existingTargetEntries = rerl.ResourceRefInfoList
            .Where(entry => PathEquals(entry.Name, TargetGraph))
            .ToArray();

        if (oldRerlEntries.Length != 1 || existingTargetEntries.Length != 0)
        {
            return Failed("패치 직전 RERL 대상이 정확히 1개인지 재검증하지 못했습니다.");
        }

        KVObject replacement = TargetGraph;
        replacement.Flag = oldValue.Flag;
        hudRefs[0]["m_hGraph"] = replacement;
        oldRerlEntries[0].Name = TargetGraph;
        oldRerlEntries[0].Id = TargetGraphId;

        using var serializedStream = new MemoryStream();
        resource.Serialize(serializedStream);
        var stagedBytes = RebuildWithPatchedDataBlocks(originalBytes, serializedStream.ToArray());

        Directory.CreateDirectory(Path.GetDirectoryName(stagingFile)!);
        File.WriteAllBytes(stagingFile, stagedBytes);

        var validation = Validate(originalFile, stagingFile, source2ViewerPath);
        if (!validation.Passed)
        {
            File.Delete(stagingFile);
        }

        return validation;
    }

    public static ValidationResult Validate(
        string originalFile,
        string stagedFile,
        string source2ViewerPath)
    {
        try
        {
            if (!File.Exists(originalFile))
            {
                return Failed($"원본 파일이 없습니다: {originalFile}");
            }

            if (!File.Exists(stagedFile))
            {
                return Failed($"staging 파일이 없습니다: {stagedFile}");
            }

            var originalBytes = File.ReadAllBytes(originalFile);
            var stagedBytes = File.ReadAllBytes(stagedFile);
            var originalBlocks = ReadRawBlocks(originalBytes);
            var stagedBlocks = ReadRawBlocks(stagedBytes);
            var blocksBefore = originalBlocks.Select(ToBlockReport).ToList();
            var blocksAfter = stagedBlocks.Select(ToBlockReport).ToList();

            var blockValidation = ValidateBlocks(originalBlocks, stagedBlocks, out var blockReason);
            if (!blockValidation)
            {
                return Failed(blockReason, blocksAfter: blocksAfter);
            }

            using var originalInput = new MemoryStream(originalBytes, writable: false);
            using var stagedInput = new MemoryStream(stagedBytes, writable: false);
            using var originalResource = ReadResource(originalFile, originalInput);
            using var stagedResource = ReadResource(stagedFile, stagedInput);

            var originalRoot = GetDataRoot(originalResource);
            var stagedRoot = GetDataRoot(stagedResource);
            var originalHud = GetGraphRefs(originalRoot).Where(graph => Identifier(graph) == "hudmodel").ToArray();
            var stagedHud = GetGraphRefs(stagedRoot).Where(graph => Identifier(graph) == "hudmodel").ToArray();

            if (originalHud.Length != 1 || stagedHud.Length != 1)
            {
                return Failed("원본 또는 패치본의 hudmodel 개수가 1개가 아닙니다.", blocksAfter: blocksAfter);
            }

            if (!originalHud[0].TryGetValue("m_hGraph", out var originalGraphValue)
                || !stagedHud[0].TryGetValue("m_hGraph", out var stagedGraphValue)
                || originalGraphValue.ValueType != KVValueType.String
                || stagedGraphValue.ValueType != KVValueType.String)
            {
                return Failed("hudmodel.m_hGraph를 읽지 못했습니다.", blocksAfter: blocksAfter);
            }

            if (!PathEquals((string)originalGraphValue, OldGraph)
                || !PathEquals((string)stagedGraphValue, TargetGraph)
                || originalGraphValue.Flag != stagedGraphValue.Flag)
            {
                return Failed(
                    "hudmodel 경로 또는 KV3 Resource flag 검증에 실패했습니다.",
                    blocksAfter: blocksAfter);
            }

            KVObject restoredGraphValue = OldGraph;
            restoredGraphValue.Flag = originalGraphValue.Flag;
            stagedHud[0]["m_hGraph"] = restoredGraphValue;

            var originalDataText = NormalizeSemanticText(originalRoot.ToKV3String());
            var restoredStagedDataText = NormalizeSemanticText(stagedRoot.ToKV3String());
            var dataValidation = string.Equals(
                originalDataText,
                restoredStagedDataText,
                StringComparison.Ordinal);

            if (!dataValidation)
            {
                return Failed(
                    "hudmodel 경로 외 DATA 의미 데이터가 달라졌습니다.",
                    blocksAfter: blocksAfter,
                    blocksPassed: true);
            }

            var rerlValidation = ValidateRerl(originalResource, stagedResource, out var rerlReason);
            if (!rerlValidation)
            {
                return Failed(
                    rerlReason,
                    blocksAfter: blocksAfter,
                    blocksPassed: true,
                    dataPassed: true);
            }

            var source2ViewerValidation = RunSource2Viewer(source2ViewerPath, stagedFile, out var s2vReason);
            if (!source2ViewerValidation)
            {
                return Failed(
                    s2vReason,
                    blocksAfter: blocksAfter,
                    blocksPassed: true,
                    dataPassed: true,
                    rerlPassed: true);
            }

            return new ValidationResult
            {
                Passed = true,
                Reason = $"PASS: {blockReason}; DATA/RERL 의미 비교 및 Source2Viewer 파싱 성공",
                AfterSha256 = Sha256(stagedBytes),
                AfterSize = stagedBytes.Length,
                BlocksPassed = true,
                DataPassed = true,
                RerlPassed = true,
                Source2ViewerPassed = true,
                BlocksAfter = blocksAfter,
            };
        }
        catch (Exception ex)
        {
            return Failed(ex.Message);
        }
    }

    public static bool ValidateTargetGraph(
        string assetRoot,
        string source2ViewerPath,
        out string compiledTargetPath,
        out string reason)
    {
        compiledTargetPath = Path.Combine(
            assetRoot,
            TargetGraph.Replace('/', Path.DirectorySeparatorChar) + "_c");

        if (!File.Exists(compiledTargetPath))
        {
            reason = $"RSS 루트 그래프가 에셋에 없습니다: {compiledTargetPath}";
            return false;
        }

        if (!RunSource2Viewer(source2ViewerPath, compiledTargetPath, out reason))
        {
            reason = $"RSS 루트 그래프 Source2Viewer 검증 실패: {reason}";
            return false;
        }

        reason = $"RSS 루트 그래프 확인: {compiledTargetPath}";
        return true;
    }

    public static bool RunSource2Viewer(string executable, string inputFile, out string reason)
    {
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            reason = "Source2Viewer-CLI.exe 경로가 없거나 유효하지 않습니다.";
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(inputFile);
        startInfo.ArgumentList.Add("-a");

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            reason = "Source2Viewer 검증이 120초 안에 끝나지 않았습니다.";
            return false;
        }

        Task.WaitAll(stdout, stderr);
        if (process.ExitCode != 0)
        {
            var error = stderr.Result.Trim();
            reason = $"Source2Viewer 종료 코드 {process.ExitCode}: {error}";
            return false;
        }

        reason = "Source2Viewer 전체 블록 파싱 성공";
        return true;
    }

    public static string Sha256File(string file)
        => Sha256(File.ReadAllBytes(file));

    public static void AtomicReplaceFromFile(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + $".vmdlpatch.{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(source, temporary, overwrite: false);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static ResourceInspection Inspection(
        string status,
        string reason,
        string sha,
        long size,
        List<BlockReport> blocks,
        string path = "",
        string flag = "")
        => new()
        {
            Status = status,
            Reason = reason,
            BeforeSha256 = sha,
            BeforeSize = size,
            HudGraphPath = path,
            HudGraphFlag = flag,
            Blocks = blocks,
        };

    private static ValidationResult Failed(
        string reason,
        List<BlockReport>? blocksAfter = null,
        bool blocksPassed = false,
        bool dataPassed = false,
        bool rerlPassed = false)
        => new()
        {
            Passed = false,
            Reason = reason,
            BlocksPassed = blocksPassed,
            DataPassed = dataPassed,
            RerlPassed = rerlPassed,
            Source2ViewerPassed = false,
            BlocksAfter = blocksAfter ?? [],
        };

    private static Resource ReadResource(string fileName, Stream stream)
    {
        var resource = new Resource { FileName = fileName };
        try
        {
            resource.Read(stream, verifyFileSize: true, leaveOpen: true);
            return resource;
        }
        catch
        {
            resource.Dispose();
            throw;
        }
    }

    private static KVObject GetDataRoot(Resource resource)
        => (resource.DataBlock as KeyValuesOrNTRO)?.Data
            ?? throw new InvalidDataException("DATA 블록을 KeyValues로 읽을 수 없습니다.");

    private static IReadOnlyList<KVObject> GetGraphRefs(KVObject root)
    {
        foreach (var name in new[] { "m_animGraph2Refs", "m_refAnimGraphs" })
        {
            var array = root.GetArray(name);
            if (array != null)
            {
                return array;
            }
        }

        return [];
    }

    private static string Identifier(KVObject graph)
        => graph.GetStringProperty("m_sIdentifier", "").Trim().ToLowerInvariant();

    private static bool ValidateBlocks(
        List<RawBlock> original,
        List<RawBlock> staged,
        out string reason)
    {
        if (original.Count != staged.Count)
        {
            reason = $"block 개수가 다릅니다: {original.Count} -> {staged.Count}";
            return false;
        }

        for (var index = 0; index < original.Count; index++)
        {
            var before = original[index];
            var after = staged[index];
            if (before.Type != after.Type)
            {
                reason = $"block 종류/순서가 다릅니다: index={index}";
                return false;
            }

            var typeName = FourCcName(before.Type);
            if (before.Type == RerlFourCc || before.Type == DataFourCc)
            {
                continue;
            }

            if (!before.Data.AsSpan().SequenceEqual(after.Data))
            {
                var protectedText = ProtectedBlockTypes.Contains(typeName)
                    ? "보호 대상 "
                    : "";
                reason = $"{protectedText}block payload가 달라졌습니다: {typeName}[{index}]";
                return false;
            }
        }

        reason = $"block {original.Count}개 종류/순서 유지, DATA/RERL 외 payload 동일";
        return true;
    }

    private static bool ValidateRerl(Resource original, Resource staged, out string reason)
    {
        var originalRerl = original.ExternalReferences;
        var stagedRerl = staged.ExternalReferences;
        if (originalRerl == null || stagedRerl == null)
        {
            reason = "원본 또는 패치본에 RERL이 없습니다.";
            return false;
        }

        var before = originalRerl.ResourceRefInfoList
            .Select(entry => (entry.Id, Name: NormalizePath(entry.Name)))
            .ToArray();
        var after = stagedRerl.ResourceRefInfoList
            .Select(entry => (entry.Id, Name: NormalizePath(entry.Name)))
            .ToArray();

        var oldIndexes = before
            .Select((entry, index) => (entry, index))
            .Where(item => item.entry.Name == NormalizePath(OldGraph) && item.entry.Id == OldGraphId)
            .ToArray();
        var targetIndexes = after
            .Select((entry, index) => (entry, index))
            .Where(item => item.entry.Name == NormalizePath(TargetGraph) && item.entry.Id == TargetGraphId)
            .ToArray();

        if (oldIndexes.Length != 1 || targetIndexes.Length != 1 || before.Length != after.Length)
        {
            reason = "RERL old/target 항목 개수 또는 전체 항목 개수가 예상과 다릅니다.";
            return false;
        }

        after[targetIndexes[0].index] = (OldGraphId, NormalizePath(OldGraph));
        if (!before.SequenceEqual(after))
        {
            reason = "대상 그래프 외 RERL 의미 데이터가 달라졌습니다.";
            return false;
        }

        reason = "대상 그래프 경로/ID 외 RERL 항목 동일";
        return true;
    }

    private static string NormalizeSemanticText(string value)
        => Regex.Replace(value, @"(?<![\d.])-?0(?:\.0+)?(?=[,\s\]\}])", "0");

    private static bool PathEquals(string left, string right)
        => NormalizePath(left).Equals(NormalizePath(right), StringComparison.Ordinal);

    private static string NormalizePath(string value)
        => value.Replace('\\', '/').Trim().ToLowerInvariant();

    private sealed record RawBlock(int Index, uint Type, byte[] Data);

    private static List<RawBlock> ReadRawBlocks(byte[] bytes)
    {
        if (bytes.Length < 16)
        {
            throw new InvalidDataException("Source 2 헤더가 너무 짧습니다.");
        }

        var declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
        if (declaredSize != bytes.Length)
        {
            throw new InvalidDataException($"파일 크기 헤더가 다릅니다: {declaredSize} != {bytes.Length}");
        }

        var table = checked(8 + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4)));
        var count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4)));
        var blocks = new List<RawBlock>(count);

        for (var index = 0; index < count; index++)
        {
            var entry = checked(table + index * 12);
            if (entry < 0 || entry > bytes.Length - 12)
            {
                throw new InvalidDataException("block 테이블 범위를 벗어났습니다.");
            }

            var type = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entry, 4));
            var offset = checked(entry + 4
                + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entry + 4, 4)));
            var size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entry + 8, 4)));
            if (offset < 0 || size < 0 || offset > bytes.Length - size)
            {
                throw new InvalidDataException("block 데이터 범위를 벗어났습니다.");
            }

            blocks.Add(new RawBlock(index, type, bytes.AsSpan(offset, size).ToArray()));
        }

        return blocks;
    }

    private static byte[] RebuildWithPatchedDataBlocks(byte[] original, byte[] serialized)
    {
        var originalBlocks = ReadRawBlocks(original);
        var patchedBlocks = ReadRawBlocks(serialized)
            .Where(block => block.Type == RerlFourCc || block.Type == DataFourCc)
            .ToDictionary(block => block.Type, block => block.Data);

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

        for (var index = 0; index < originalBlocks.Count; index++)
        {
            var padding = (int)((16 - stream.Position % 16) % 16);
            if (padding >= 5)
            {
                var markerStart = padding / 2 - 1;
                writer.Write(new byte[markerStart]);
                writer.Write((byte)'S');
                writer.Write((byte)'2');
                writer.Write((byte)'V');
                padding -= markerStart + 3;
            }

            writer.Write(new byte[padding]);
            var blockOffset = stream.Position;
            var block = originalBlocks[index];
            var data = patchedBlocks.TryGetValue(block.Type, out var replacement)
                ? replacement
                : block.Data;
            writer.Write(data);

            var end = stream.Position;
            var metadataOffset = 20L + index * 12L;
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

    private static BlockReport ToBlockReport(RawBlock block)
        => new()
        {
            Index = block.Index,
            Type = FourCcName(block.Type),
            Size = block.Data.Length,
            Sha256 = Sha256(block.Data),
        };

    private static string Sha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static uint FourCc(string value)
        => BinaryPrimitives.ReadUInt32LittleEndian(Encoding.ASCII.GetBytes(value));

    private static string FourCcName(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return Encoding.ASCII.GetString(bytes);
    }
}
