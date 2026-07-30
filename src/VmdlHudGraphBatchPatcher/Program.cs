using System.Text;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);

        try
        {
            var options = CliOptions.Parse(args);
            if (options.ShowHelp)
            {
                PrintHelp();
                return 0;
            }

            return options.Command switch
            {
                "scan" => BatchCommands.Scan(options),
                "patch" => BatchCommands.Patch(options),
                "verify" => BatchCommands.Verify(options),
                "apply" => BatchCommands.Apply(options),
                "restore" => BatchCommands.Restore(options),
                _ => throw new ArgumentException($"지원하지 않는 명령입니다: {options.Command}"),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[오류] {ex.Message}");
            return 2;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            VmdlHudGraphBatchPatcher
            ========================

            기본 실행은 파일을 수정하지 않는 scan입니다.

            사용법:
              VmdlHudGraphBatchPatcher.exe scan --asset-root <폴더> [--manifest <파일>]

              VmdlHudGraphBatchPatcher.exe patch --asset-root <폴더>
                [--manifest <파일>] [--source2viewer <Source2Viewer-CLI.exe>]
                [--staging <폴더>] [--backup <폴더>] [--run-root <폴더>] [--yes]

              VmdlHudGraphBatchPatcher.exe verify --run-id <실행ID>
                [--run-root <폴더>] [--source2viewer <Source2Viewer-CLI.exe>]

              VmdlHudGraphBatchPatcher.exe apply --run-id <실행ID>
                [--run-root <폴더>] [--yes]

              VmdlHudGraphBatchPatcher.exe restore --run-id <실행ID>
                [--run-root <폴더>] [--yes]

            고정 변경:
              DATA hudmodel:
                animation/graphs/viewmodel/viewmodel.vnmgraph
                -> animation/rss/graphs/viewmodel.vnmgraph

              RERL:
                ID C5111C601E968C98 -> D6AA21250E1DE440

            기본 graph, uimodel, worldmodel은 변경하지 않습니다.
            JSON/CSV 보고서는 <run-root>\<실행ID>에 생성됩니다.
            """);
    }
}
