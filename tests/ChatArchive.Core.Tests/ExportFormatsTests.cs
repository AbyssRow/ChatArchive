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
