using ChatArchive.Core.Migration;

namespace ChatArchive.Migrate;

public static class Program
{
    public static int Main(string[] args)
    {
        string? from = null;
        string? to = "E:\\ChatArchive";

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--from" when i + 1 < args.Length:
                    from = args[++i];
                    break;
                case "--to" when i + 1 < args.Length:
                    to = args[++i];
                    break;
                default:
                    Console.Error.WriteLine($"未知参数: {args[i]}");
                    PrintUsage();
                    return 2;
            }
        }

        if (string.IsNullOrEmpty(from))
        {
            PrintUsage();
            return 2;
        }

        try
        {
            var runner = new MigrationRunner(from, to);
            var report = runner.Run(message => Console.WriteLine(message));
            Console.WriteLine();
            Console.WriteLine("迁移完成：");
            Console.WriteLine($"  会话 {report.Conversations} | 消息 {report.Messages} | 附件 {report.Attachments} | 媒体 {report.MediaObjects}");
            Console.WriteLine($"  媒体文件 新增 {report.MediaFilesCopied}/跳过 {report.MediaFilesSkipped}，路径改写 {report.ManagedPathsRewritten}");
            Console.WriteLine($"  数据库 {report.TargetDb}");
            Console.WriteLine($"  校验 {(report.Verified ? "通过" : "失败")}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"迁移失败: {ex.Message}");
            return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("用法: ChatArchive.Migrate --from <旧数据目录> [--to E:\\ChatArchive]");
        Console.WriteLine("  --from 指向包含 chat_archive.db 与 media\\ 的目录（只读）");
    }
}
