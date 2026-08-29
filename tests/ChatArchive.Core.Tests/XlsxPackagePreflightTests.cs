using System.IO.Compression;
using ChatArchive.Core.Importing;
using Xunit;

namespace ChatArchive.Core.Tests;

public sealed class XlsxPackagePreflightTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"chatarchive-xlsx-preflight-{Guid.NewGuid():N}");

    public XlsxPackagePreflightTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void OpenValidated_RewindsStreamAndIndexesContentTypes()
    {
        var path = Path.Combine(_directory, "valid.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet(
            "聊天记录", [[new XlsxTestCell("A1", "one")]]));

        using var package = XlsxPackagePreflight.OpenValidated(path);

        Assert.Equal(0, package.Stream.Position);
        Assert.Contains("xl/workbook.xml", package.EntryPaths);
        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml",
            package.GetContentType("xl/workbook.xml"));
    }

    [Fact]
    public void OpenValidated_RejectsNonXlsxBeforeRetainingAFileHandle()
    {
        var path = Path.Combine(_directory, "renamed.xlsm");
        XlsxTestFile.Write(path, new XlsxTestSheet(
            "聊天记录", [[new XlsxTestCell("A1", "one")]]));

        var error = Assert.Throws<ImportFormatException>(
            () => XlsxPackagePreflight.OpenValidated(path));

        Assert.Contains(".xlsx", error.Message);
        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
    }

    [Fact]
    public void OpenValidated_RejectsCaseAmbiguousPackageEntries()
    {
        var path = Path.Combine(_directory, "ambiguous.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet(
            "聊天记录", [[new XlsxTestCell("A1", "one")]]));
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            archive.CreateEntry("XL/workbook.xml");
        }

        var error = Assert.Throws<ImportFormatException>(
            () => XlsxPackagePreflight.OpenValidated(path));

        Assert.Contains("重复或歧义", error.Message);
        Assert.Contains("XL/workbook.xml", error.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
