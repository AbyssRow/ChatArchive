using ChatArchive.App.ViewModels;
using Xunit;

namespace ChatArchive.App.Tests;

public class StatsViewModelTests
{
    [Fact]
    public void ApplyLoadResult_IgnoresStaleGeneration()
    {
        var vm = new StatsViewModel(null!, null!); // if ctor requires repo, pass a stub or make ApplyLoadResult internal and not touch repo
        vm.ApplyLoadResult(generation: 1, summary: "old", error: "");
        vm.ApplyLoadResult(generation: 2, summary: "new", error: "");
        vm.ApplyLoadResult(generation: 1, summary: "stale", error: "late fail");
        Assert.Equal("new", vm.SummaryLines);
        Assert.Equal("", vm.ErrorMessage);
    }
}
