using ChatArchive.App.ViewModels;
using ChatArchive.Core.Importing;
using Xunit;

namespace ChatArchive.App.Tests;

public sealed class ImportFormatPresentationTests
{
    [Fact]
    public void InitialStatus_DoesNotAdvertiseExcludedHtmlAndIncludesExcel()
    {
        var viewModel = new ImportViewModel(null!, null!);

        Assert.DoesNotContain("HTML", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("XLSX", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PickerExtensions_ExactlyMirrorDiscoveryAndExcludeHtml()
    {
        Assert.Equal(
            ImportDiscovery.SupportedExtensions.OrderBy(extension => extension),
            ImportViewModel.PickerExtensions.OrderBy(extension => extension));
        Assert.DoesNotContain(".html", ImportViewModel.PickerExtensions, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(".htm", ImportViewModel.PickerExtensions, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void HelpText_ListsOnlySourceSpecificCurrentExporters()
    {
        Assert.Contains("WeFlow", ImportViewModel.HelpText, StringComparison.Ordinal);
        Assert.Contains("CipherTalk", ImportViewModel.HelpText, StringComparison.Ordinal);
        Assert.Contains("QQ Chat Exporter", ImportViewModel.HelpText, StringComparison.Ordinal);
        Assert.DoesNotContain("HTML", ImportViewModel.HelpText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("通用", ImportViewModel.HelpText, StringComparison.Ordinal);
    }
}
