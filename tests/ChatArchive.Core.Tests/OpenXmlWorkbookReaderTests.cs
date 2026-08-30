using System.IO.Compression;
using System.Text;
using ChatArchive.Core.Importing;
using Xunit;

namespace ChatArchive.Core.Tests;

public sealed class OpenXmlWorkbookReaderTests : IDisposable
{
    private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string PackageRelationshipNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string HyperlinkRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink";
    private const string WorkbookContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml";
    private const string WorksheetContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml";
    private const string SharedStringsContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml";

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
    public void OpenXmlReader_ReadsExcelJsExternalRelativeMediaHyperlinkWithoutPackageEntry()
    {
        var path = NewPath("exceljs-relative-media.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录",
        [[new XlsxTestCell("A1", "[image]", Hyperlink: "../images/one.jpg", ExternalHyperlink: true)]]));

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var row = Assert.Single(workbook.ReadRows(workbook.Sheets[0], CancellationToken.None));

        Assert.Equal("[image]", row.Cells[1].Value);
        Assert.Equal("../images/one.jpg", row.Cells[1].Hyperlink);
        using var archive = ZipFile.OpenRead(path);
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName == "images/one.jpg");
    }

    [Theory]
    [InlineData("https://example.invalid/card")]
    [InlineData("/images/one.jpg")]
    [InlineData("C:/images/one.jpg")]
    [InlineData("\\\\server\\share\\one.jpg")]
    [InlineData("images\\one.jpg")]
    [InlineData("../images/one.jpg?download=1")]
    [InlineData("../images/one.jpg#preview")]
    [InlineData("../images/\none.jpg")]
    [InlineData("../../../outside.jpg")]
    [InlineData("../../../__chatarchive_package_root__/outside.jpg")]
    public void OpenXmlReader_IgnoresUnsafeExternalHyperlinkButReadsCellText(string target)
    {
        var path = NewPath("unsafe-external-link.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录",
        [[new XlsxTestCell("A1", "link card", Hyperlink: target, ExternalHyperlink: true)]]));

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var row = Assert.Single(workbook.ReadRows(workbook.Sheets[0], CancellationToken.None));

        Assert.Equal("link card", row.Cells[1].Value);
        Assert.Null(row.Cells[1].Hyperlink);
    }

    [Fact]
    public void OpenXmlReader_IgnoresUnreferencedExternalHyperlinkWithoutPackageTarget()
    {
        var path = NewPath("unreferenced-external-hyperlink.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "one")]]));
        AddTextEntry(path, "xl/worksheets/_rels/sheet1.xml.rels", $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <Relationships xmlns="{{PackageRelationshipNamespace}}">
              <Relationship Id="rIdUnused" Type="{{HyperlinkRelationship}}" Target="../images/missing.jpg" TargetMode="External" />
            </Relationships>
            """);

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var row = Assert.Single(workbook.ReadRows(workbook.Sheets[0], CancellationToken.None));

        Assert.Equal("one", row.Cells[1].Value);
        Assert.Null(row.Cells[1].Hyperlink);
    }

    [Fact]
    public void OpenXmlReader_RejectsExactExternalHyperlinkRelationshipInPackageRoot()
    {
        var path = NewPath("root-external-hyperlink.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "one")]]));
        RewriteEntry(
            path,
            "_rels/.rels",
            xml => xml.Replace(
                "</Relationships>",
                $"<Relationship Id=\"rIdExternalHyperlink\" Type=\"{HyperlinkRelationship}\" Target=\"images/missing.jpg\" TargetMode=\"External\" /></Relationships>",
                StringComparison.Ordinal));

        OpenXmlWorkbookReader? opened = null;
        try
        {
            var error = Assert.Throws<ImportFormatException>(() => { opened = OpenXmlWorkbookReader.Open(path); });
            Assert.Contains("外部关系", error.Message);
            Assert.Contains("_rels/.rels", error.Message);
        }
        finally
        {
            opened?.Dispose();
        }
    }

    [Fact]
    public void OpenXmlReader_RejectsExactExternalHyperlinkRelationshipInWorkbookPart()
    {
        var path = NewPath("workbook-external-hyperlink.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "one")]]));
        RewriteEntry(
            path,
            "xl/_rels/workbook.xml.rels",
            xml => xml.Replace(
                "</Relationships>",
                $"<Relationship Id=\"rIdExternalHyperlink\" Type=\"{HyperlinkRelationship}\" Target=\"../images/missing.jpg\" TargetMode=\"External\" /></Relationships>",
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
    public void OpenXmlReader_RejectsExternalRelationshipWhoseTypeOnlyCaseMatchesHyperlink()
    {
        var path = NewPath("non-exact-external-hyperlink-type.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录",
        [[new XlsxTestCell("A1", "one", Hyperlink: "../images/one.jpg", ExternalHyperlink: true)]]));
        RewriteEntry(
            path,
            "xl/worksheets/_rels/sheet1.xml.rels",
            xml => ReplaceRequired(xml, HyperlinkRelationship, $"{HyperlinkRelationship[..^9]}Hyperlink"));

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

    [Theory]
    [InlineData(null)]
    [InlineData("application/xml")]
    public void OpenXmlReader_RejectsMissingOrMismatchedWorkbookContentType(string? replacementContentType)
    {
        var path = NewPath($"workbook-content-type-{(replacementContentType is null ? "missing" : "wrong")}.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "one")]]));
        RewriteEntry(
            path,
            "[Content_Types].xml",
            xml => RewriteContentTypeOverride(xml, "/xl/workbook.xml", WorkbookContentType, replacementContentType));

        var error = Assert.Throws<ImportFormatException>(() => OpenXmlWorkbookReader.Open(path));
        Assert.Contains("xl/workbook.xml", error.Message);
        Assert.Contains("ContentType", error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("application/xml")]
    public void OpenXmlReader_RejectsMissingOrMismatchedWorksheetContentType(string? replacementContentType)
    {
        var path = NewPath($"worksheet-content-type-{(replacementContentType is null ? "missing" : "wrong")}.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "one")]]));
        RewriteEntry(
            path,
            "[Content_Types].xml",
            xml => RewriteContentTypeOverride(xml, "/xl/worksheets/sheet1.xml", WorksheetContentType, replacementContentType));

        var error = Assert.Throws<ImportFormatException>(() => OpenXmlWorkbookReader.Open(path));
        Assert.Contains("sheet1.xml", error.Message);
        Assert.Contains("ContentType", error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("application/xml")]
    public void OpenXmlReader_RejectsMissingOrMismatchedSharedStringsContentType(string? replacementContentType)
    {
        var path = NewPath($"shared-content-type-{(replacementContentType is null ? "missing" : "wrong")}.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "one", "s")]]));
        RewriteEntry(
            path,
            "[Content_Types].xml",
            xml => RewriteContentTypeOverride(xml, "/xl/sharedStrings.xml", SharedStringsContentType, replacementContentType));

        var error = Assert.Throws<ImportFormatException>(() => OpenXmlWorkbookReader.Open(path));
        Assert.Contains("sharedStrings.xml", error.Message);
        Assert.Contains("ContentType", error.Message);
    }

    [Theory]
    [InlineData("xl/worksheets/unrelated.xml")]
    [InlineData("xl/sharedStrings.xml")]
    public void OpenXmlReader_IgnoresUnreferencedNonSpreadsheetPartWithSpreadsheetLikePath(
        string entryPath)
    {
        var path = NewPath($"unreferenced-{Path.GetFileName(entryPath)}.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet(
            "聊天记录", [[new XlsxTestCell("A1", "one")]]));
        AddTextEntry(path, entryPath, "<unrelated />");
        RewriteEntry(path, "[Content_Types].xml", xml => InsertBeforeRequired(
            xml,
            "</Types>",
            $"<Override PartName=\"/{entryPath}\" ContentType=\"application/xml\" />"));

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var row = Assert.Single(workbook.ReadRows(workbook.Sheets[0], CancellationToken.None));

        Assert.Equal("one", row.Cells[1].Value);
    }

    [Fact]
    public void OpenXmlReader_RejectsForbiddenMacroPayloadDeclarationAnywhere()
    {
        var path = NewPath("macro-payload.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "one")]]));
        RewriteEntry(path, "[Content_Types].xml", xml => InsertBeforeRequired(
            xml,
            "</Types>",
            "<Override PartName=\"/xl/vbaProject.bin\" ContentType=\"application/vnd.ms-office.vbaProject\" />"));

        var error = Assert.Throws<ImportFormatException>(() => OpenXmlWorkbookReader.Open(path));
        Assert.Contains("vbaProject", error.Message);
        Assert.Contains("[Content_Types].xml", error.Message);
    }

    [Theory]
    [InlineData("xl/vbaProject.bin")]
    [InlineData("xl/activeX/activeX1.bin")]
    [InlineData("xl/embeddings/oleObject1.bin")]
    public void OpenXmlReader_RejectsUndeclaredForbiddenPayloadEntry(string entryPath)
    {
        var path = NewPath($"undeclared-{Path.GetFileName(entryPath)}.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "one")]]));
        AddTextEntry(path, entryPath, "not opened");

        var error = Assert.Throws<ImportFormatException>(() => OpenXmlWorkbookReader.Open(path));
        Assert.Contains(entryPath, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("禁止", error.Message);
    }

    [Theory]
    [InlineData("xl/vbaProject.bin")]
    [InlineData("xl/activeX/activeX1.bin")]
    [InlineData("xl/embeddings/oleObject1.bin")]
    public void OpenXmlReader_RejectsForbiddenPayloadEntryMislabeledAsOctetStream(string entryPath)
    {
        var path = NewPath($"mislabeled-{Path.GetFileName(entryPath)}.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "one")]]));
        AddTextEntry(path, entryPath, "not opened");
        RewriteEntry(path, "[Content_Types].xml", xml => InsertBeforeRequired(
            xml,
            "</Types>",
            $"<Override PartName=\"/{entryPath}\" ContentType=\"application/octet-stream\" />"));

        var error = Assert.Throws<ImportFormatException>(() => OpenXmlWorkbookReader.Open(path));
        Assert.Contains(entryPath, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("禁止", error.Message);
    }

    [Fact]
    public void OpenXmlReader_RejectsForbiddenInternalRelationshipType()
    {
        var path = NewPath("forbidden-relationship-type.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "one")]]));
        AddTextEntry(path, "xl/custom/forbidden.bin", "unused");
        RewriteEntry(path, "[Content_Types].xml", xml => InsertBeforeRequired(
            xml,
            "</Types>",
            "<Override PartName=\"/xl/custom/forbidden.bin\" ContentType=\"application/octet-stream\" />"));
        RewriteEntry(path, "xl/_rels/workbook.xml.rels", xml => InsertBeforeRequired(
            xml,
            "</Relationships>",
            "<Relationship Id=\"rIdForbidden\" Type=\"http://schemas.microsoft.com/office/2006/relationships/vbaProject\" Target=\"custom/forbidden.bin\" />"));

        var error = Assert.Throws<ImportFormatException>(() => OpenXmlWorkbookReader.Open(path));
        Assert.Contains("vbaProject", error.Message);
        Assert.Contains("workbook.xml.rels", error.Message);
    }

    [Fact]
    public void OpenXmlReader_RejectsInternalRelationshipTargetingForbiddenPayloadPath()
    {
        var path = NewPath("forbidden-relationship-target.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "one")]]));
        RewriteEntry(path, "xl/_rels/workbook.xml.rels", xml => InsertBeforeRequired(
            xml,
            "</Relationships>",
            "<Relationship Id=\"rIdForbidden\" Type=\"urn:test\" Target=\"activeX/activeX1.bin\" />"));

        var error = Assert.Throws<ImportFormatException>(() => OpenXmlWorkbookReader.Open(path));
        Assert.Contains("xl/activeX/activeX1.bin", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("workbook.xml.rels", error.Message);
    }

    [Fact]
    public void OpenXmlReader_RejectsBinaryWorkbookContentType()
    {
        var path = NewPath("binary-workbook.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "one")]]));
        RewriteEntry(
            path,
            "[Content_Types].xml",
            xml => RewriteContentTypeOverride(
                xml,
                "/xl/workbook.xml",
                WorkbookContentType,
                "application/vnd.ms-excel.sheet.binary.macroEnabled.main"));

        var error = Assert.Throws<ImportFormatException>(() => OpenXmlWorkbookReader.Open(path));
        Assert.Contains("ContentType", error.Message);
    }

    [Fact]
    public void OpenXmlReader_RejectsNestedContentTypeOverride()
    {
        var path = NewPath("nested-content-type.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "one")]]));
        RewriteEntry(path, "[Content_Types].xml", xml => WrapRequired(
            xml,
            $"<Override PartName=\"/xl/workbook.xml\" ContentType=\"{WorkbookContentType}\" />",
            "wrapper"));

        var error = Assert.Throws<ImportFormatException>(() => OpenXmlWorkbookReader.Open(path));
        Assert.Contains("[Content_Types].xml", error.Message);
    }

    [Fact]
    public void OpenXmlReader_RejectsContentTypesNestedBelowWrongRoot()
    {
        var path = NewPath("nested-content-types-root.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "one")]]));
        RewriteEntry(path, "[Content_Types].xml", xml => ReplaceRequired(
            ReplaceRequired(
                xml,
                "<Types",
                "<outer xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Types",
                replaceFirstOnly: true),
            "</Types>",
            "</Types></outer>"));

        var error = Assert.Throws<ImportFormatException>(() => OpenXmlWorkbookReader.Open(path));
        Assert.Contains("[Content_Types].xml", error.Message);
    }

    [Fact]
    public void OpenXmlReader_RejectsNestedRelationshipElement()
    {
        var path = NewPath("nested-relationship.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "one")]]));
        RewriteEntry(
            path,
            "xl/_rels/workbook.xml.rels",
            xml => WrapRequired(xml, "<Relationship Id=\"rIdSheet1\"", "wrapper", matchIsPrefix: true));

        var error = Assert.Throws<ImportFormatException>(() => OpenXmlWorkbookReader.Open(path));
        Assert.Contains("workbook.xml.rels", error.Message);
    }

    [Fact]
    public void OpenXmlReader_RejectsWorkbookNestedBelowWrongRoot()
    {
        var path = NewPath("nested-workbook-root.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "one")]]));
        RewriteEntry(path, "xl/workbook.xml", xml => ReplaceRequired(
            ReplaceRequired(
                xml,
                "<workbook",
                $"<outer xmlns=\"{SpreadsheetNamespace}\"><workbook",
                replaceFirstOnly: true),
            "</workbook>",
            "</workbook></outer>"));

        var error = Assert.Throws<ImportFormatException>(() => OpenXmlWorkbookReader.Open(path));
        Assert.Contains("xl/workbook.xml", error.Message);
    }

    [Fact]
    public void OpenXmlReader_RejectsSheetOutsideDirectSheetsParent()
    {
        var path = NewPath("nested-sheet.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "one")]]));
        RewriteEntry(path, "xl/workbook.xml", xml => ReplaceRequired(
            ReplaceRequired(xml, "<sheets>", "<sheets><wrapper>"),
            "</sheets>",
            "</wrapper></sheets>"));

        var error = Assert.Throws<ImportFormatException>(() => OpenXmlWorkbookReader.Open(path));
        Assert.Contains("xl/workbook.xml", error.Message);
    }

    [Fact]
    public void OpenXmlReader_RejectsSheetNestedInsideDirectSheet()
    {
        var path = NewPath("sheet-nested-inside-sheet.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet(
            "聊天记录", [[new XlsxTestCell("A1", "one")]]));
        RewriteEntry(path, "xl/workbook.xml", xml =>
        {
            const string marker = "<sheet name=\"聊天记录\"";
            Assert.Contains(marker, xml);
            var start = xml.IndexOf(marker, StringComparison.Ordinal);
            var end = xml.IndexOf("/>", start, StringComparison.Ordinal);
            Assert.True(end >= 0);
            var directSheet = xml.Substring(start, end + 2 - start);
            var replacement = string.Concat(
                directSheet.AsSpan(0, directSheet.Length - 2),
                ">",
                "<sheet name=\"Nested\" sheetId=\"2\" r:id=\"rIdSheet1\" />",
                "</sheet>");
            return xml.Remove(start, directSheet.Length).Insert(start, replacement);
        });

        var error = Assert.Throws<ImportFormatException>(() => OpenXmlWorkbookReader.Open(path));
        Assert.Contains("sheet", error.Message);
        Assert.Contains("xl/workbook.xml", error.Message);
    }

    [Fact]
    public void OpenXmlReader_RejectsSharedStringsNestedBelowWrongRoot()
    {
        var path = NewPath("nested-shared-root.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "one", "s")]]));
        RewriteEntry(path, "xl/sharedStrings.xml", xml => ReplaceRequired(
            ReplaceRequired(
                xml,
                "<sst",
                $"<outer xmlns=\"{SpreadsheetNamespace}\"><sst",
                replaceFirstOnly: true),
            "</sst>",
            "</sst></outer>"));

        var error = Assert.Throws<ImportFormatException>(() => OpenXmlWorkbookReader.Open(path));
        Assert.Contains("xl/sharedStrings.xml", error.Message);
    }

    [Fact]
    public void OpenXmlReader_RejectsSharedStringItemOutsideDirectSstParent()
    {
        var path = NewPath("nested-shared-item.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "one", "s")]]));
        RewriteEntry(path, "xl/sharedStrings.xml", xml => WrapRequired(xml, "<si><t>one</t></si>", "wrapper"));

        var error = Assert.Throws<ImportFormatException>(() => OpenXmlWorkbookReader.Open(path));
        Assert.Contains("xl/sharedStrings.xml", error.Message);
    }

    [Fact]
    public void OpenXmlReader_RejectsSharedStringItemNestedInsideDirectItem()
    {
        var path = NewPath("nested-shared-item-inside-item.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "one", "s")]]));
        RewriteEntry(
            path,
            "xl/sharedStrings.xml",
            xml => ReplaceRequired(xml, "<si><t>one</t></si>", "<si><si><t>nested</t></si></si>"));

        var error = Assert.Throws<ImportFormatException>(() => OpenXmlWorkbookReader.Open(path));
        Assert.Contains("si", error.Message);
        Assert.Contains("xl/sharedStrings.xml", error.Message);
    }

    [Fact]
    public void OpenXmlReader_OnlyConcatenatesRichTextInsideDirectSharedStringItem()
    {
        var path = NewPath("scoped-rich-text.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "one", "s")]]));
        RewriteEntry(path, "xl/sharedStrings.xml", xml => ReplaceRequired(
            xml,
            "<si><t>one</t></si>",
            "<t>fake</t><si><r><t>rich</t></r><r><t> text</t></r></si>"));

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var row = Assert.Single(workbook.ReadRows(workbook.Sheets[0], CancellationToken.None));
        Assert.Equal("rich text", row.Cells[1].Value);
    }

    [Fact]
    public void OpenXmlReader_ReportsCorruptZipAsImportFormatError()
    {
        var path = NewPath("broken.xlsx");
        File.WriteAllText(path, "not a zip");
        Assert.Throws<ImportFormatException>(() => OpenXmlWorkbookReader.Open(path));
    }

    [Fact]
    public void OpenXmlReader_RejectsNonXlsxExtensionThroughFacade()
    {
        var path = NewPath("renamed.xlsm");
        XlsxTestFile.Write(path, new XlsxTestSheet(
            "聊天记录", [[new XlsxTestCell("A1", "one")]]));
        OpenXmlWorkbookReader? opened = null;

        try
        {
            var error = Assert.Throws<ImportFormatException>(
                () => opened = OpenXmlWorkbookReader.Open(path));
            Assert.Contains(".xlsx", error.Message);
        }
        finally
        {
            opened?.Dispose();
        }
    }

    [Fact]
    public void OpenXmlReader_FailedSdkOpenReleasesWorkbookFile()
    {
        var path = NewPath("failed-sdk-open-release.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet(
            "聊天记录", [[new XlsxTestCell("A1", "one")]]));
        RewriteEntry(path, "xl/workbook.xml", _ => "<broken>");

        var error = Assert.Throws<ImportFormatException>(() => OpenXmlWorkbookReader.Open(path));
        Assert.Equal(path, error.FilePath);
        Assert.Contains("xl/workbook.xml", error.Message);

        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
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

    [Fact]
    public void OpenXmlReader_RejectsExternalWorksheetRelationshipWithoutHyperlinkDeclaration()
    {
        var path = NewPath("unreferenced-sheet-external.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "one")]]));
        AddTextEntry(path, "xl/worksheets/_rels/sheet1.xml.rels", $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <Relationships xmlns="{{PackageRelationshipNamespace}}">
              <Relationship Id="rIdExternal" Type="urn:test" Target="https://example.invalid" TargetMode="External" />
            </Relationships>
            """);

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var error = Assert.Throws<ImportFormatException>(
            () => workbook.ReadRows(workbook.Sheets[0], CancellationToken.None).ToList());
        Assert.Contains("外部关系", error.Message);
        Assert.Contains("sheet1.xml.rels", error.Message);
    }

    [Fact]
    public void OpenXmlReader_AllowsExactlyTenThousandHyperlinkDeclarations()
    {
        var path = NewPath("ten-thousand-links.xlsx");
        WriteWorkbookWithHyperlinkDeclarations(path, 10_000);

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var row = Assert.Single(workbook.ReadRows(workbook.Sheets[0], CancellationToken.None));
        Assert.Equal("media/one.jpg", row.Cells[1].Hyperlink);
    }

    [Fact]
    public void OpenXmlReader_AllowsExactlyTenThousandWorksheetRelationshipsWithoutHyperlinks()
    {
        var path = NewPath("ten-thousand-sheet-relationships.xlsx");
        WriteWorkbookWithWorksheetRelationships(path, 10_000);

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var row = Assert.Single(workbook.ReadRows(workbook.Sheets[0], CancellationToken.None));
        Assert.Equal("one", row.Cells[1].Value);
    }

    [Fact]
    public void OpenXmlReader_RejectsTenThousandAndFirstWorksheetRelationshipBeforeRetainingIt()
    {
        var path = NewPath("too-many-sheet-relationships.xlsx");
        WriteWorkbookWithWorksheetRelationships(path, 10_001);

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var error = Assert.Throws<ImportFormatException>(
            () => workbook.ReadRows(workbook.Sheets[0], CancellationToken.None).ToList());
        Assert.Contains("10000", error.Message);
        Assert.Contains("sheet1.xml.rels", error.Message);
    }

    [Fact]
    public void OpenXmlReader_RejectsTenThousandAndFirstHyperlinkBeforeRetainingIt()
    {
        var path = NewPath("too-many-links.xlsx");
        WriteWorkbookWithHyperlinkDeclarations(path, 10_001);

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var error = Assert.Throws<ImportFormatException>(
            () => workbook.ReadRows(workbook.Sheets[0], CancellationToken.None).ToList());
        Assert.Contains("10000", error.Message);
        Assert.Contains("sheet1.xml", error.Message);
    }

    [Fact]
    public void OpenXmlReader_RejectsDuplicateRowIndex()
    {
        var path = NewPath("duplicate-row.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录",
        [
            [new XlsxTestCell("A1", "one")],
            [new XlsxTestCell("A2", "two")]
        ]));
        RewriteEntry(path, "xl/worksheets/sheet1.xml", xml => ReplaceRequired(
            xml,
            "<row r=\"2\"><c r=\"A2\"",
            "<row r=\"1\"><c r=\"A1\""));

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var error = Assert.Throws<ImportFormatException>(
            () => workbook.ReadRows(workbook.Sheets[0], CancellationToken.None).ToList());
        Assert.Contains("递增", error.Message);
        Assert.Contains("1", error.Message);
    }

    [Fact]
    public void OpenXmlReader_RejectsOutOfOrderRowIndex()
    {
        var path = NewPath("out-of-order-row.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录",
        [
            [new XlsxTestCell("A1", "one")],
            [new XlsxTestCell("A2", "two")]
        ]));
        RewriteEntry(path, "xl/worksheets/sheet1.xml", xml =>
        {
            var changed = ReplaceRequired(
                xml,
                "<row r=\"1\"><c r=\"A1\"",
                "<row r=\"__first__\"><c r=\"A__first__\"");
            changed = ReplaceRequired(
                changed,
                "<row r=\"2\"><c r=\"A2\"",
                "<row r=\"1\"><c r=\"A1\"");
            return ReplaceRequired(
                changed,
                "<row r=\"__first__\"><c r=\"A__first__\"",
                "<row r=\"2\"><c r=\"A2\"");
        });

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var error = Assert.Throws<ImportFormatException>(
            () => workbook.ReadRows(workbook.Sheets[0], CancellationToken.None).ToList());
        Assert.Contains("递增", error.Message);
        Assert.Contains("1", error.Message);
    }

    [Fact]
    public void OpenXmlReader_RejectsCellReferenceRowDisagreement()
    {
        var path = NewPath("cell-row-disagreement.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A2", "one")]]));

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var error = Assert.Throws<ImportFormatException>(
            () => workbook.ReadRows(workbook.Sheets[0], CancellationToken.None).ToList());
        Assert.Contains("A2", error.Message);
        Assert.Contains("所在行 1", error.Message);
    }

    [Fact]
    public void OpenXmlReader_AcceptsMaximumExcelRowAndColumn()
    {
        var path = NewPath("maximum-cell.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "edge")]]));
        RewriteEntry(path, "xl/worksheets/sheet1.xml", xml => ReplaceRequired(
            ReplaceRequired(xml, "<row r=\"1\">", "<row r=\"1048576\">"),
            "r=\"A1\"",
            "r=\"XFD1048576\""));

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var row = Assert.Single(workbook.ReadRows(workbook.Sheets[0], CancellationToken.None));
        Assert.Equal(1_048_576u, row.RowIndex);
        Assert.Equal("edge", row.Cells[16_384].Value);
    }

    [Theory]
    [InlineData("XFE1", "1")]
    [InlineData("A1048577", "1048577")]
    public void OpenXmlReader_RejectsCellReferenceOutsideExcelBounds(string cellReference, string rowReference)
    {
        var path = NewPath($"outside-cell-bounds-{cellReference}.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "edge")]]));
        RewriteEntry(path, "xl/worksheets/sheet1.xml", xml => ReplaceRequired(
            ReplaceRequired(xml, "<row r=\"1\">", $"<row r=\"{rowReference}\">"),
            "r=\"A1\"",
            $"r=\"{cellReference}\""));

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var error = Assert.Throws<ImportFormatException>(
            () => workbook.ReadRows(workbook.Sheets[0], CancellationToken.None).ToList());
        Assert.Contains(cellReference == "XFE1" ? "XFE1" : "1048577", error.Message);
    }

    [Fact]
    public void OpenXmlReader_RejectsInvalidSharedStringIndex()
    {
        var path = NewPath("invalid-shared-index.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "one", "s")]]));
        RewriteEntry(
            path,
            "xl/worksheets/sheet1.xml",
            xml => ReplaceRequired(xml, "<v>0</v>", "<v>99</v>"));

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var error = Assert.Throws<ImportFormatException>(
            () => workbook.ReadRows(workbook.Sheets[0], CancellationToken.None).ToList());
        Assert.Contains("共享字符串索引", error.Message);
        Assert.Contains("A1", error.Message);
    }

    [Fact]
    public void OpenXmlReader_RejectsInvalidBooleanCache()
    {
        var path = NewPath("invalid-boolean.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "2", "b")]]));

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var error = Assert.Throws<ImportFormatException>(
            () => workbook.ReadRows(workbook.Sheets[0], CancellationToken.None).ToList());
        Assert.Contains("布尔缓存值", error.Message);
        Assert.Contains("A1", error.Message);
    }

    [Fact]
    public void OpenXmlReader_RejectsElementContentInsideDirectHyperlinkDeclaration()
    {
        var path = NewPath("hyperlink-with-element-content.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录",
        [[new XlsxTestCell("A1", "one", Hyperlink: "media/one.jpg")]]));
        RewriteEntry(path, "xl/worksheets/sheet1.xml", xml => ReplaceRequired(
            xml,
            "<hyperlink ref=\"A1\" r:id=\"rIdHyperlink1\" />",
            "<hyperlink ref=\"A1\" r:id=\"rIdHyperlink1\"><c r=\"B1\" /></hyperlink>"));

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var error = Assert.Throws<ImportFormatException>(
            () => workbook.ReadRows(workbook.Sheets[0], CancellationToken.None).ToList());
        Assert.Contains("hyperlink", error.Message);
        Assert.Contains("sheet1.xml", error.Message);
    }

    [Theory]
    [InlineData("str", "1+1", "cached", "<f>1+1</f>", "<f><c r=\"B1\" /></f>")]
    [InlineData("str", "1+1", "cached", "<v>cached</v>", "<v><c r=\"B1\" /></v>")]
    [InlineData("inlineStr", null, "one", "<t>one</t>", "<t><c r=\"B1\" /></t>")]
    [InlineData("str", "1+1", "cached", "<f>1+1</f>", "<f>1<!--bad-->+1</f>")]
    [InlineData("str", "1+1", "cached", "<v>cached</v>", "<v>a<?bad data?>b</v>")]
    [InlineData("inlineStr", null, "one", "<t>one</t>", "<t>a<!--bad-->b</t>")]
    public void OpenXmlReader_RejectsElementMarkupInsideCellLeaf(
        string type,
        string? formula,
        string value,
        string original,
        string replacement)
    {
        var path = NewPath($"cell-leaf-{type}-{original[1]}.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet(
            "聊天记录",
            [[new XlsxTestCell("A1", value, type, Formula: formula)]]));
        RewriteEntry(path, "xl/worksheets/sheet1.xml", xml => ReplaceRequired(
            xml,
            original,
            replacement));

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var error = Assert.Throws<ImportFormatException>(
            () => workbook.ReadRows(workbook.Sheets[0], CancellationToken.None).ToList());
        Assert.Contains("A1", error.Message);
        Assert.Contains("sheet1.xml", error.Message);
    }

    [Fact]
    public void OpenXmlReader_RejectsInlineTextInsideRunProperties()
    {
        var path = NewPath("inline-text-inside-run-properties.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet(
            "聊天记录",
            [[new XlsxTestCell("A1", "one")]]));
        RewriteEntry(path, "xl/worksheets/sheet1.xml", xml => ReplaceRequired(
            xml,
            "<is><t>one</t></is>",
            "<is><r><rPr><t>bad</t></rPr><t>good</t></r></is>"));

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var error = Assert.Throws<ImportFormatException>(
            () => workbook.ReadRows(workbook.Sheets[0], CancellationToken.None).ToList());
        Assert.Contains("A1", error.Message);
        Assert.Contains("t", error.Message);
    }

    [Fact]
    public void OpenXmlReader_ReadsInlineTextFromPermittedPathsAndXmlTextEncodings()
    {
        var path = NewPath("inline-text-permitted-paths.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet(
            "聊天记录",
            [[new XlsxTestCell("A1", "one")]]));
        RewriteEntry(path, "xl/worksheets/sheet1.xml", xml => ReplaceRequired(
            xml,
            "<is><t>one</t></is>",
            "<is><t>A &amp; </t><r><t><![CDATA[B ]]></t></r><rPh sb=\"0\" eb=\"1\"><t>C</t></rPh></is>"));

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var row = Assert.Single(workbook.ReadRows(workbook.Sheets[0], CancellationToken.None));
        Assert.Equal("A & B C", row.Cells[1].Value);
    }

    [Fact]
    public void OpenXmlReader_ConcatenatesMixedInlineCharacterDataSegments()
    {
        var path = NewPath("inline-text-mixed-character-data.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet(
            "聊天记录",
            [[new XlsxTestCell("A1", "one")]]));
        RewriteEntry(path, "xl/worksheets/sheet1.xml", xml => ReplaceRequired(
            xml,
            "<is><t>one</t></is>",
            "<is><t>a&amp;<![CDATA[b]]>c</t></is>"));

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var row = Assert.Single(workbook.ReadRows(workbook.Sheets[0], CancellationToken.None));
        Assert.Equal("a&bc", row.Cells[1].Value);
    }

    [Fact]
    public void OpenXmlReader_ReadsCanonicalEscapedAndNumericCharacterReferences()
    {
        var path = NewPath("inline-text-character-references.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet(
            "聊天记录",
            [[new XlsxTestCell("A1", "one")]]));
        RewriteEntry(path, "xl/worksheets/sheet1.xml", xml => ReplaceRequired(
            xml,
            "<is><t>one</t></is>",
            "<is><t>&lt;&amp;&#65;&#x42;</t></is>"));

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var row = Assert.Single(workbook.ReadRows(workbook.Sheets[0], CancellationToken.None));
        Assert.Equal("<&AB", row.Cells[1].Value);
    }

    [Fact]
    public void OpenXmlReader_RejectsAmbiguousDecodedMarkupInsideMixedText()
    {
        var path = NewPath("inline-text-ambiguous-decoded-markup.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet(
            "聊天记录",
            [[new XlsxTestCell("A1", "one")]]));
        RewriteEntry(path, "xl/worksheets/sheet1.xml", xml => ReplaceRequired(
            xml,
            "<is><t>one</t></is>",
            "<is><t><![CDATA[a]]>&lt;fake/&gt;</t></is>"));

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var error = Assert.Throws<ImportFormatException>(
            () => workbook.ReadRows(workbook.Sheets[0], CancellationToken.None).ToList());
        Assert.Contains("A1", error.Message);
        Assert.Contains("t", error.Message);
    }

    [Fact]
    public void OpenXmlReader_FragmentClassificationFailureIncludesCellAndLeafContext()
    {
        var path = NewPath("inline-text-malformed-decoded-less-than.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet(
            "聊天记录",
            [[new XlsxTestCell("A1", "one")]]));
        RewriteEntry(path, "xl/worksheets/sheet1.xml", xml => ReplaceRequired(
            xml,
            "<is><t>one</t></is>",
            "<is><t><![CDATA[a]]>&lt;</t></is>"));

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var error = Record.Exception(
            () => workbook.ReadRows(workbook.Sheets[0], CancellationToken.None).ToList());

        Assert.NotNull(error);
        Assert.Contains(
            "XLSX 条目 xl/worksheets/sheet1.xml：单元格 A1：t 包含无效的子元素或标记",
            error.Message);
        Assert.IsType<ImportFormatException>(error);
    }

    [Fact]
    public void OpenXmlReader_SentinelExhaustionIncludesCellAndLeafContext()
    {
        var path = NewPath("inline-text-sentinel-exhaustion.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet(
            "聊天记录",
            [[new XlsxTestCell("A1", "one")]]));
        var privateUseRange = string.Concat(
            Enumerable.Range(0xE000, 0x1900).Select(char.ConvertFromUtf32));
        RewriteEntry(path, "xl/worksheets/sheet1.xml", xml => ReplaceRequired(
            xml,
            "<is><t>one</t></is>",
            $"<is><t><![CDATA[{privateUseRange}]]>&amp;</t></is>"));

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var error = Record.Exception(
            () => workbook.ReadRows(workbook.Sheets[0], CancellationToken.None).ToList());

        Assert.NotNull(error);
        Assert.Contains(
            "XLSX 条目 xl/worksheets/sheet1.xml：单元格 A1：t 包含无效的子元素或标记",
            error.Message);
        Assert.IsType<ImportFormatException>(error);
    }

    [Fact]
    public void OpenXmlReader_PreservesMixedCharacterDataSegmentOrder()
    {
        var path = NewPath("inline-text-mixed-character-data-order.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet(
            "聊天记录",
            [[
                new XlsxTestCell("A1", "one"),
                new XlsxTestCell("B1", "two"),
                new XlsxTestCell("C1", "three"),
            ]]));
        RewriteEntry(path, "xl/worksheets/sheet1.xml", xml => ReplaceRequired(
            ReplaceRequired(
                ReplaceRequired(
                    xml,
                    "<is><t>one</t></is>",
                    "<is><t><![CDATA[\uE000a]]>b&amp;</t></is>"),
                "<is><t>two</t></is>",
                "<is><t><![CDATA[a]]><![CDATA[b]]></t></is>"),
            "<is><t>three</t></is>",
            "<is><t>a&#38;<![CDATA[b]]></t></is>"));

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var row = Assert.Single(workbook.ReadRows(workbook.Sheets[0], CancellationToken.None));
        Assert.Equal("\uE000ab&", row.Cells[1].Value);
        Assert.Equal("ab", row.Cells[2].Value);
        Assert.Equal("a&b", row.Cells[3].Value);
    }

    [Fact]
    public void OpenXmlReader_ConcatenatesMixedCachedValueCharacterDataSegments()
    {
        var path = NewPath("cached-value-mixed-character-data.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet(
            "聊天记录",
            [[new XlsxTestCell("A1", "cached", "str")]]));
        RewriteEntry(path, "xl/worksheets/sheet1.xml", xml => ReplaceRequired(
            xml,
            "<v>cached</v>",
            "<v>a<![CDATA[b]]>c</v>"));

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var row = Assert.Single(workbook.ReadRows(workbook.Sheets[0], CancellationToken.None));
        Assert.Equal("abc", row.Cells[1].Value);
    }

    [Fact]
    public void OpenXmlReader_AcceptsMixedFormulaCharacterDataAndReturnsCachedValue()
    {
        var path = NewPath("formula-mixed-character-data.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet(
            "聊天记录",
            [[new XlsxTestCell("A1", "cached", "str", Formula: "1+1")]]));
        RewriteEntry(path, "xl/worksheets/sheet1.xml", xml => ReplaceRequired(
            xml,
            "<f>1+1</f>",
            "<f>1<![CDATA[+]]>1</f>"));

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var row = Assert.Single(workbook.ReadRows(workbook.Sheets[0], CancellationToken.None));
        Assert.Equal("cached", row.Cells[1].Value);
    }

    [Fact]
    public void OpenXmlReader_RejectsWorksheetQNameNestedInsideWorksheet()
    {
        var path = NewPath("worksheet-inside-worksheet.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "one")]]));
        RewriteEntry(path, "xl/worksheets/sheet1.xml", xml => ReplaceRequired(
            xml,
            "</worksheet>",
            "<wrapper><worksheet /></wrapper></worksheet>"));

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var error = Assert.Throws<ImportFormatException>(
            () => workbook.ReadRows(workbook.Sheets[0], CancellationToken.None).ToList());
        Assert.Contains("worksheet", error.Message);
        Assert.Contains("sheet1.xml", error.Message);
    }

    [Fact]
    public void OpenXmlReader_RejectsWorksheetNestedBelowWrongRoot()
    {
        var path = NewPath("nested-worksheet-root.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "one")]]));
        RewriteEntry(path, "xl/worksheets/sheet1.xml", xml => ReplaceRequired(
            ReplaceRequired(
                xml,
                "<worksheet",
                $"<outer xmlns=\"{SpreadsheetNamespace}\"><worksheet",
                replaceFirstOnly: true),
            "</worksheet>",
            "</worksheet></outer>"));

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var error = Assert.Throws<ImportFormatException>(
            () => workbook.ReadRows(workbook.Sheets[0], CancellationToken.None).ToList());
        Assert.Contains("sheet1.xml", error.Message);
    }

    [Fact]
    public void OpenXmlReader_RejectsSheetDataOutsideDirectWorksheetParent()
    {
        var path = NewPath("nested-sheet-data.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "one")]]));
        RewriteEntry(path, "xl/worksheets/sheet1.xml", xml => ReplaceRequired(
            ReplaceRequired(xml, "<sheetData>", "<wrapper><sheetData>"),
            "</sheetData>",
            "</sheetData></wrapper>"));

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var error = Assert.Throws<ImportFormatException>(
            () => workbook.ReadRows(workbook.Sheets[0], CancellationToken.None).ToList());
        Assert.Contains("sheetData", error.Message);
    }

    [Fact]
    public void OpenXmlReader_RejectsRowOutsideDirectSheetDataParent()
    {
        var path = NewPath("nested-row.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "one")]]));
        RewriteEntry(path, "xl/worksheets/sheet1.xml", xml => WrapRequired(
            xml,
            "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>one</t></is></c></row>",
            "wrapper"));

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var error = Assert.Throws<ImportFormatException>(
            () => workbook.ReadRows(workbook.Sheets[0], CancellationToken.None).ToList());
        Assert.Contains("row", error.Message);
        Assert.Contains("sheetData", error.Message);
    }

    [Fact]
    public void OpenXmlReader_RejectsRowNestedInsideDirectRow()
    {
        var path = NewPath("row-inside-row.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "one")]]));
        RewriteEntry(path, "xl/worksheets/sheet1.xml", xml => ReplaceRequired(
            xml,
            "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>one</t></is></c></row>",
            "<row r=\"1\"><row r=\"2\" /></row>"));

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var error = Assert.Throws<ImportFormatException>(
            () => workbook.ReadRows(workbook.Sheets[0], CancellationToken.None).ToList());
        Assert.Contains("row", error.Message);
        Assert.Contains("sheetData", error.Message);
    }

    [Fact]
    public void OpenXmlReader_RejectsCellOutsideDirectRowParent()
    {
        var path = NewPath("nested-cell.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "one")]]));
        RewriteEntry(path, "xl/worksheets/sheet1.xml", xml => WrapRequired(
            xml,
            "<c r=\"A1\" t=\"inlineStr\"><is><t>one</t></is></c>",
            "wrapper"));

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var error = Assert.Throws<ImportFormatException>(
            () => workbook.ReadRows(workbook.Sheets[0], CancellationToken.None).ToList());
        Assert.Contains("c", error.Message);
        Assert.Contains("row", error.Message);
    }

    [Fact]
    public void OpenXmlReader_RejectsCellPlacedDirectlyUnderSheetData()
    {
        var path = NewPath("cell-under-sheet-data.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "one")]]));
        RewriteEntry(path, "xl/worksheets/sheet1.xml", xml => ReplaceRequired(
            ReplaceRequired(xml, "<row r=\"1\">", string.Empty),
            "</row>",
            string.Empty));

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var error = Assert.Throws<ImportFormatException>(
            () => workbook.ReadRows(workbook.Sheets[0], CancellationToken.None).ToList());
        Assert.Contains("c", error.Message);
        Assert.Contains("row", error.Message);
    }

    [Fact]
    public void OpenXmlReader_RejectsHyperlinkOutsideDirectHyperlinksParent()
    {
        var path = NewPath("nested-hyperlink.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录",
        [[new XlsxTestCell("A1", "one", Hyperlink: "media/one.jpg")]]));
        RewriteEntry(path, "xl/worksheets/sheet1.xml", xml => WrapRequired(
            xml,
            "<hyperlink ref=\"A1\" r:id=\"rIdHyperlink1\" />",
            "wrapper"));

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var error = Assert.Throws<ImportFormatException>(
            () => workbook.ReadRows(workbook.Sheets[0], CancellationToken.None).ToList());
        Assert.Contains("hyperlink", error.Message);
        Assert.Contains("hyperlinks", error.Message);
    }

    [Fact]
    public void OpenXmlReader_RejectsWorksheetRelationshipsNestedBelowWrongRoot()
    {
        var path = NewPath("nested-sheet-relationships.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "one")]]));
        AddTextEntry(path, "xl/worksheets/_rels/sheet1.xml.rels", $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <outer xmlns="{{PackageRelationshipNamespace}}"><Relationships /></outer>
            """);

        using var workbook = OpenXmlWorkbookReader.Open(path);
        var error = Assert.Throws<ImportFormatException>(
            () => workbook.ReadRows(workbook.Sheets[0], CancellationToken.None).ToList());
        Assert.Contains("sheet1.xml.rels", error.Message);
    }

    [Fact]
    public void OpenXmlReader_EarlyIteratorDisposalReleasesWorkbookFile()
    {
        var path = NewPath("early-dispose.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录",
        [
            [new XlsxTestCell("A1", "one")],
            [new XlsxTestCell("A2", "two")]
        ]));

        using (var workbook = OpenXmlWorkbookReader.Open(path))
        {
            using var rows = workbook.ReadRows(workbook.Sheets[0], CancellationToken.None).GetEnumerator();
            Assert.True(rows.MoveNext());
        }

        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
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

    private static void AddTextEntry(string filePath, string entryPath, string content)
    {
        using var archive = ZipFile.Open(filePath, ZipArchiveMode.Update);
        var entry = archive.CreateEntry(entryPath, CompressionLevel.NoCompression);
        entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void WriteWorkbookWithHyperlinkDeclarations(string filePath, int declarationCount)
    {
        XlsxTestFile.Write(filePath, new XlsxTestSheet("聊天记录",
        [[new XlsxTestCell("A1", "one", Hyperlink: "media/one.jpg")]]));
        var hyperlinks = new StringBuilder("<hyperlinks>");
        for (var index = 1; index <= declarationCount; index++)
        {
            hyperlinks.Append("<hyperlink ref=\"")
                .Append(ColumnReference(index))
                .Append("1\" r:id=\"rIdHyperlink1\" />");
        }

        hyperlinks.Append("</hyperlinks>");
        RewriteEntry(filePath, "xl/worksheets/sheet1.xml", xml => ReplaceRequired(
            xml,
            "<hyperlinks><hyperlink ref=\"A1\" r:id=\"rIdHyperlink1\" /></hyperlinks>",
            hyperlinks.ToString()));
    }

    private static void WriteWorkbookWithWorksheetRelationships(string filePath, int relationshipCount)
    {
        XlsxTestFile.Write(filePath, new XlsxTestSheet("聊天记录", [[new XlsxTestCell("A1", "one")]]));
        var relationships = new StringBuilder()
            .Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>")
            .Append("<Relationships xmlns=\"")
            .Append(PackageRelationshipNamespace)
            .Append("\">");
        for (var index = 1; index <= relationshipCount; index++)
        {
            relationships.Append("<Relationship Id=\"rId")
                .Append(index)
                .Append("\" Type=\"urn:test\" Target=\"../../unused.bin\" />");
        }

        relationships.Append("</Relationships>");
        AddTextEntry(
            filePath,
            "xl/worksheets/_rels/sheet1.xml.rels",
            relationships.ToString());
        AddTextEntry(filePath, "unused.bin", "unused");
        RewriteEntry(filePath, "[Content_Types].xml", xml => InsertBeforeRequired(
            xml,
            "</Types>",
            "<Override PartName=\"/unused.bin\" ContentType=\"application/octet-stream\" />"));
    }

    private static string ColumnReference(int columnIndex)
    {
        var result = new StringBuilder();
        while (columnIndex > 0)
        {
            columnIndex--;
            result.Insert(0, (char)('A' + columnIndex % 26));
            columnIndex /= 26;
        }

        return result.ToString();
    }

    private static string RewriteContentTypeOverride(
        string xml,
        string partName,
        string currentContentType,
        string? replacementContentType)
    {
        var current = $"<Override PartName=\"{partName}\" ContentType=\"{currentContentType}\" />";
        var replacement = replacementContentType is null
            ? string.Empty
            : $"<Override PartName=\"{partName}\" ContentType=\"{replacementContentType}\" />";
        return ReplaceRequired(xml, current, replacement);
    }

    private static string InsertBeforeRequired(string value, string marker, string insertion)
    {
        Assert.Contains(marker, value);
        var index = value.IndexOf(marker, StringComparison.Ordinal);
        return value.Insert(index, insertion);
    }

    private static string WrapRequired(
        string value,
        string element,
        string wrapper,
        bool matchIsPrefix = false)
    {
        Assert.Contains(element, value);
        if (!matchIsPrefix)
        {
            return ReplaceRequired(value, element, $"<{wrapper}>{element}</{wrapper}>", replaceFirstOnly: true);
        }

        var start = value.IndexOf(element, StringComparison.Ordinal);
        var end = value.IndexOf("/>", start, StringComparison.Ordinal);
        Assert.True(end >= 0);
        var length = end + 2 - start;
        var completeElement = value.Substring(start, length);
        return value.Remove(start, length).Insert(start, $"<{wrapper}>{completeElement}</{wrapper}>");
    }

    private static string ReplaceRequired(
        string value,
        string oldValue,
        string newValue,
        bool replaceFirstOnly = false)
    {
        Assert.Contains(oldValue, value);
        if (!replaceFirstOnly)
        {
            return value.Replace(oldValue, newValue, StringComparison.Ordinal);
        }

        var index = value.IndexOf(oldValue, StringComparison.Ordinal);
        return string.Concat(value.AsSpan(0, index), newValue, value.AsSpan(index + oldValue.Length));
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
