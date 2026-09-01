using ChatArchive.Core.Importing;
using ChatArchive.Core.Models;
using Xunit;

namespace ChatArchive.Core.Tests;

public class ExportFormatsTests
{
    private sealed class DummyFormat(string platform) : IChatExportFormat
    {
        public string Platform => platform;
        public bool Matches(string filePath) => false;
        public ExportFile Open(string filePath, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class LegacyCancellingFormat(CancellationTokenSource cancellation)
        : IChatExportFormat
    {
        public string Platform => "legacy";

        public bool LegacyMatchesCalled { get; private set; }

        public bool Matches(string filePath)
        {
            LegacyMatchesCalled = true;
            cancellation.Cancel();
            return false;
        }

        public ExportFile Open(string filePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    [Fact]
    public void CancellationAwareMatches_DefaultImplementationSupportsLegacyFormatsAndChecksAfterMatch()
    {
        using var cancellation = new CancellationTokenSource();
        var format = new LegacyCancellingFormat(cancellation);

        Assert.Throws<OperationCanceledException>(() =>
            ((IChatExportFormat)format).Matches("candidate.json", cancellation.Token));
        Assert.True(format.LegacyMatchesCalled);
    }

    [Fact]
    public void LegacyPublicSniffOverloadsRemainAvailable()
    {
        Type[] formatTypes =
        [
            typeof(QqExportFormat),
            typeof(QqChunkedExportFormat),
            typeof(WeFlowExportFormat),
            typeof(CipherTalkDetailedJsonFormat),
            typeof(ChatLabJsonExportFormat),
            typeof(ChatLabJsonlExportFormat),
            typeof(WeFlowCsvExportFormat),
            typeof(WeFlowMarkdownExportFormat),
            typeof(QqTextExportFormat),
            typeof(WeFlowTextExportFormat),
            typeof(WeFlowExcelExportFormat),
            typeof(CipherTalkExcelExportFormat),
            typeof(QqExcelExportFormat),
            typeof(WeFlowSqlExportFormat),
            typeof(CipherTalkSqlExportFormat),
        ];

        foreach (var type in formatTypes)
        {
            AssertPublicOverload(type, nameof(IChatExportFormat.Matches), typeof(string));
            var cancellable = AssertPublicOverload(
                type,
                nameof(IChatExportFormat.Matches),
                typeof(string),
                typeof(CancellationToken));
            Assert.False(cancellable.GetParameters()[1].IsOptional);
        }

        AssertPublicOverload(typeof(ImportText), nameof(ImportText.ParseDocument), typeof(string));
        AssertPublicOverload(typeof(Rfc4180CsvReader), nameof(Rfc4180CsvReader.ReadRecords), typeof(TextReader));
        AssertPublicOverload(typeof(QqTextParser), nameof(QqTextParser.Matches), typeof(string));
        AssertPublicOverload(
            typeof(QqTextParser),
            nameof(QqTextParser.ReadConversation),
            typeof(string),
            typeof(CancellationToken));
        AssertLegacyParserOverloads(typeof(WeFlowCsvParser));
        AssertLegacyParserOverloads(typeof(WeFlowMarkdownParser));
        AssertLegacyParserOverloads(typeof(WeFlowTextParser));
        AssertPublicOverload(typeof(OpenXmlWorkbookReader), nameof(OpenXmlWorkbookReader.Open), typeof(string));
    }

    private static void AssertLegacyParserOverloads(Type type)
    {
        AssertPublicOverload(type, "Matches", typeof(string));
        AssertPublicOverload(type, "ReadConversation", typeof(string));
    }

    private static System.Reflection.MethodInfo AssertPublicOverload(
        Type type,
        string methodName,
        params Type[] parameterTypes)
    {
        var method = type.GetMethod(methodName, parameterTypes);
        return Assert.IsAssignableFrom<System.Reflection.MethodInfo>(method);
    }

    [Fact]
    public void Default_ContainsStandardFormats()
    {
        var formats = ExportFormats.Default;
        Assert.NotNull(formats);
        Assert.Contains(formats, f => f is QqExportFormat);
        Assert.Contains(formats, f => f is QqChunkedExportFormat);
        Assert.Contains(formats, f => f is WeFlowExportFormat);
    }

    [Fact]
    public void Default_DoesNotRegisterHtmlImporter()
    {
        Assert.DoesNotContain(
            ExportFormats.Default,
            format => format.GetType().Name.Contains("Html", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Register_And_Enumerate_IsThreadSafe()
    {
        var readTasks = new List<Task>();
        var writeTasks = new List<Task>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        // Start multiple reader tasks iterating Default
        for (var i = 0; i < 5; i++)
        {
            readTasks.Add(Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var formats = ExportFormats.Default;
                    var count = 0;
                    foreach (var f in formats)
                    {
                        count++;
                        _ = f.Platform;
                    }
                    Assert.True(count > 0);
                }
            }));
        }

        // Start multiple writer tasks registering formats
        for (var i = 0; i < 5; i++)
        {
            var index = i;
            writeTasks.Add(Task.Run(() =>
            {
                var registered = 0;
                while (!cts.Token.IsCancellationRequested && registered < 20)
                {
                    ExportFormats.Register(new DummyFormat($"custom_{index}_{registered++}"));
                    Thread.Sleep(5);
                }
            }));
        }

        await Task.WhenAll(readTasks.Concat(writeTasks));
    }
}
