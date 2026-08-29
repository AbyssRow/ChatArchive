using System.IO.Compression;
using System.Text;
using ChatArchive.Core.Importing;
using Xunit;

namespace ChatArchive.Core.Tests;

public sealed class OpenXmlWorkbookReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"chatarchive-openxml-{Guid.NewGuid():N}");

    public OpenXmlWorkbookReaderTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void OpenXmlReader_ReadsCellKindsFormulaCacheAndInternalHyperlink()
    {
        var path = NewPath("cells.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录",
        [
            [
                new("A1", "shared", "s"),
                new("B1", "inline", "inlineStr"),
                new("C1", "42", "n"),
                new("D1", "1", "b"),
                new("E1", "cached", "str", Formula: "1+1"),
                new("F1", "media/one.jpg", Hyperlink: "media/one.jpg")
            ]
        ]));

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var sheet = Assert.Single(workbook.Sheets);
        var row = Assert.Single(workbook.ReadRows(sheet, CancellationToken.None));
        Assert.Equal("shared", row.Cells[1].Value);
        Assert.Equal("inline", row.Cells[2].Value);
        Assert.Equal("42", row.Cells[3].Value);
        Assert.Equal("true", row.Cells[4].Value);
        Assert.Equal("cached", row.Cells[5].Value);
        Assert.Equal("media/one.jpg", row.Cells[6].Hyperlink);
    }

    [Fact]
    public void OpenXmlReader_ReadsSparseCellsDateTextRichTextAndEmptyFormulaCache()
    {
        var path = NewPath("sparse.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("Data & 资料",
        [
            [
                new("A1", "one", "s"),
                new("C1", "2026-08-29T12:34:56Z", "d"),
                new("E1", null, "str", Formula: "A1")
            ]
        ]));
        RewriteEntry(path, "xl/sharedStrings.xml", xml => xml.Replace(
            "<si><t>one</t></si>",
            "<si><r><t>rich </t></r><r><t>text</t></r></si>",
            StringComparison.Ordinal));

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var row = Assert.Single(workbook.ReadRows(Assert.Single(workbook.Sheets), CancellationToken.None));

        Assert.Equal("Data & 资料", workbook.Sheets[0].Name);
        Assert.Equal("rich text", row.Cells[1].Value);
        Assert.False(row.Cells.ContainsKey(2));
        Assert.Equal("2026-08-29T12:34:56Z", row.Cells[3].Value);
        Assert.Equal(string.Empty, row.Cells[5].Value);
    }

    [Fact]
    public void OpenXmlReader_RejectsExternalRelationship()
    {
        var path = NewPath("external.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录",
        [[new XlsxTestCell("A1", "click", Hyperlink: "https://example.invalid", ExternalHyperlink: true)]]));

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var error = Assert.Throws<ImportFormatException>(
            () => workbook.ReadRows(workbook.Sheets[0], CancellationToken.None).ToList());
        Assert.Contains("外部关系", error.Message);
    }

    [Fact]
    public void OpenXmlReader_RejectsUnreferencedExternalRelationship()
    {
        var path = NewPath("unreferenced-external.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "one")]]));
        RewriteEntry(
            path,
            "xl/_rels/workbook.xml.rels",
            xml => xml.Replace(
                "</Relationships>",
                "<Relationship Id=\"rIdExternal\" Type=\"urn:test\" Target=\"https://example.invalid\" TargetMode=\"External\" /></Relationships>",
                StringComparison.Ordinal));

        OpenXmlWorkbookReader? opened = null;
        try
        {
            var error = Assert.Throws<ImportFormatException>(() => { opened = OpenXmlWorkbookReader.Open(path); });
            Assert.Contains("外部关系", error.Message);
            Assert.Contains("workbook.xml.rels", error.Message);
        }
        finally
        {
            opened?.Dispose();
        }
    }

    [Fact]
    public void OpenXmlReader_RejectsHyperlinkRelationshipEscapingPackageRoot()
    {
        var path = NewPath("escape.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录",
        [[new XlsxTestCell("A1", "click", Hyperlink: "media/one.jpg")]]));
        RewriteEntry(
            path,
            "xl/worksheets/_rels/sheet1.xml.rels",
            xml => xml.Replace("../../media/one.jpg", "../../../outside.jpg", StringComparison.Ordinal));

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var error = Assert.Throws<ImportFormatException>(
            () => workbook.ReadRows(workbook.Sheets[0], CancellationToken.None).ToList());
        Assert.Contains("越界", error.Message);
        Assert.Contains("sheet1.xml.rels", error.Message);
    }

    [Fact]
    public void OpenXmlReader_RejectsMissingHyperlinkTargetEntry()
    {
        var path = NewPath("missing-link-target.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录",
        [[new XlsxTestCell("A1", "click", Hyperlink: "media/one.jpg")]]));
        DeleteEntry(path, "media/one.jpg");

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var error = Assert.Throws<ImportFormatException>(
            () => workbook.ReadRows(workbook.Sheets[0], CancellationToken.None).ToList());
        Assert.Contains("media/one.jpg", error.Message);
    }

    [Fact]
    public void OpenXmlReader_RejectsInvalidCellReferenceWithCellContext()
    {
        var path = NewPath("bad-cell.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A-1", "bad")]]));

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var error = Assert.Throws<ImportFormatException>(
            () => workbook.ReadRows(workbook.Sheets[0], CancellationToken.None).ToList());
        Assert.Contains("A-1", error.Message);
        Assert.Contains("sheet1.xml", error.Message);
    }

    [Fact]
    public void OpenXmlReader_RejectsDuplicatePackageEntries()
    {
        var path = NewPath("duplicate.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "one")]]));
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var duplicate = archive.CreateEntry("xl/workbook.xml");
            using var writer = new StreamWriter(duplicate.Open(), new UTF8Encoding(false));
            writer.Write("<duplicate />");
        }

        var error = Assert.Throws<ImportFormatException>(() => OpenXmlWorkbookReader.Open(path));
        Assert.Contains("重复", error.Message);
        Assert.Contains("xl/workbook.xml", error.Message);
    }

    [Fact]
    public void OpenXmlReader_RejectsDtdWithoutResolvingIt()
    {
        var path = NewPath("dtd.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "one")]]));
        RewriteEntry(
            path,
            "xl/workbook.xml",
            xml => xml.Replace("<workbook", "<!DOCTYPE workbook SYSTEM \"https://example.invalid/workbook.dtd\"><workbook", StringComparison.Ordinal));

        var error = Assert.Throws<ImportFormatException>(() => OpenXmlWorkbookReader.Open(path));
        Assert.Contains("xl/workbook.xml", error.Message);
    }

    [Fact]
    public void OpenXmlReader_RejectsMalformedContentTypesManifest()
    {
        var path = NewPath("bad-content-types.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "one")]]));
        RewriteEntry(path, "[Content_Types].xml", _ => "<Types>");

        OpenXmlWorkbookReader? opened = null;
        try
        {
            var error = Assert.Throws<ImportFormatException>(() => { opened = OpenXmlWorkbookReader.Open(path); });
            Assert.Contains("[Content_Types].xml", error.Message);
        }
        finally
        {
            opened?.Dispose();
        }
    }

    [Fact]
    public void OpenXmlReader_ReportsCorruptZipAsImportFormatError()
    {
        var path = NewPath("broken.xlsx");
        File.WriteAllText(path, "not a zip");
        Assert.Throws<ImportFormatException>(() => OpenXmlWorkbookReader.Open(path));
    }

    [Fact]
    public void OpenXmlReader_ObservesCancellationBeforeSecondWorksheetRow()
    {
        var path = NewPath("cancel.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录",
        [
            [new XlsxTestCell("A1", "one")],
            [new XlsxTestCell("A2", "two")]
        ]));
        using var workbook = OpenXmlWorkbookReader.Open(path);
        using var cancellation = new CancellationTokenSource();
        using var rows = workbook.ReadRows(workbook.Sheets[0], cancellation.Token).GetEnumerator();

        Assert.True(rows.MoveNext());
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => rows.MoveNext());
    }

    private string NewPath(string filename) => Path.Combine(_directory, filename);

    private static void RewriteEntry(string filePath, string entryPath, Func<string, string> transform)
    {
        using var archive = ZipFile.Open(filePath, ZipArchiveMode.Update);
        var entry = Assert.Single(archive.Entries, candidate => candidate.FullName == entryPath);
        string content;
        using (var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false))
        {
            content = reader.ReadToEnd();
        }

        entry.Delete();
        var replacement = archive.CreateEntry(entryPath, CompressionLevel.NoCompression);
        using var writer = new StreamWriter(replacement.Open(), new UTF8Encoding(false));
        writer.Write(transform(content));
    }

    private static void DeleteEntry(string filePath, string entryPath)
    {
        using var archive = ZipFile.Open(filePath, ZipArchiveMode.Update);
        Assert.Single(archive.Entries, candidate => candidate.FullName == entryPath).Delete();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup for test artifacts.
        }
    }
}
