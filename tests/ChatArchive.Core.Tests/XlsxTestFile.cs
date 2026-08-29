using System.IO.Compression;
using System.Text;
using System.Xml;

namespace ChatArchive.Core.Tests;

internal sealed record XlsxTestCell(
    string Reference,
    string? Value,
    string Type = "inlineStr",
    string? Formula = null,
    string? Hyperlink = null,
    bool ExternalHyperlink = false);

internal sealed record XlsxTestSheet(
    string Name,
    IReadOnlyList<IReadOnlyList<XlsxTestCell>> Rows);

internal static class XlsxTestFile
{
    private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string OfficeRelationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelationshipNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly DateTimeOffset EntryTimestamp = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    internal static void Write(string filePath, params XlsxTestSheet[] sheets)
    {
        var sharedStrings = sheets
            .SelectMany(sheet => sheet.Rows)
            .SelectMany(row => row)
            .Where(cell => cell.Type == "s" && cell.Value is not null)
            .Select(cell => cell.Value!)
            .ToArray();
        var sharedStringIndexes = new Queue<int>(Enumerable.Range(0, sharedStrings.Length));
        var internalTargets = sheets
            .SelectMany(sheet => sheet.Rows)
            .SelectMany(row => row)
            .Where(cell => cell.Hyperlink is not null && !cell.ExternalHyperlink)
            .Select(cell => cell.Hyperlink!)
            .ToHashSet(StringComparer.Ordinal);

        using var archive = ZipFile.Open(filePath, ZipArchiveMode.Create);
        WriteXml(
            archive,
            "[Content_Types].xml",
            writer => WriteContentTypes(writer, sheets.Length, sharedStrings.Length > 0, internalTargets));
        WriteXml(archive, "_rels/.rels", WriteRootRelationships);
        WriteXml(archive, "xl/workbook.xml", writer => WriteWorkbook(writer, sheets));
        WriteXml(
            archive,
            "xl/_rels/workbook.xml.rels",
            writer => WriteWorkbookRelationships(writer, sheets.Length, sharedStrings.Length > 0));

        for (var index = 0; index < sheets.Length; index++)
        {
            var sheet = sheets[index];
            var sheetNumber = index + 1;
            WriteXml(
                archive,
                $"xl/worksheets/sheet{sheetNumber}.xml",
                writer => WriteWorksheet(writer, sheet, sharedStringIndexes));

            var hyperlinks = sheet.Rows
                .SelectMany(row => row)
                .Where(cell => cell.Hyperlink is not null)
                .ToArray();
            if (hyperlinks.Length > 0)
            {
                WriteXml(
                    archive,
                    $"xl/worksheets/_rels/sheet{sheetNumber}.xml.rels",
                    writer => WriteWorksheetRelationships(writer, hyperlinks));
            }
        }

        if (sharedStrings.Length > 0)
        {
            WriteXml(archive, "xl/sharedStrings.xml", writer => WriteSharedStrings(writer, sharedStrings));
        }

        foreach (var target in internalTargets.Order(StringComparer.Ordinal))
        {
            var entry = archive.CreateEntry(target, CompressionLevel.NoCompression);
            entry.LastWriteTime = EntryTimestamp;
        }
    }

    private static void WriteContentTypes(
        XmlWriter writer,
        int sheetCount,
        bool hasSharedStrings,
        IReadOnlySet<string> internalTargets)
    {
        writer.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");
        writer.WriteStartElement("Default");
        writer.WriteAttributeString("Extension", "rels");
        writer.WriteAttributeString("ContentType", "application/vnd.openxmlformats-package.relationships+xml");
        writer.WriteEndElement();
        writer.WriteStartElement("Default");
        writer.WriteAttributeString("Extension", "xml");
        writer.WriteAttributeString("ContentType", "application/xml");
        writer.WriteEndElement();
        writer.WriteStartElement("Override");
        writer.WriteAttributeString("PartName", "/xl/workbook.xml");
        writer.WriteAttributeString("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
        writer.WriteEndElement();
        for (var index = 1; index <= sheetCount; index++)
        {
            writer.WriteStartElement("Override");
            writer.WriteAttributeString("PartName", $"/xl/worksheets/sheet{index}.xml");
            writer.WriteAttributeString("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
            writer.WriteEndElement();
        }

        if (hasSharedStrings)
        {
            writer.WriteStartElement("Override");
            writer.WriteAttributeString("PartName", "/xl/sharedStrings.xml");
            writer.WriteAttributeString("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml");
            writer.WriteEndElement();
        }

        foreach (var target in internalTargets.Order(StringComparer.Ordinal))
        {
            writer.WriteStartElement("Override");
            writer.WriteAttributeString("PartName", $"/{target}");
            writer.WriteAttributeString("ContentType", "application/octet-stream");
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteRootRelationships(XmlWriter writer)
    {
        writer.WriteStartElement("Relationships", PackageRelationshipNamespace);
        WriteRelationship(
            writer,
            "rIdWorkbook",
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument",
            "xl/workbook.xml");
        writer.WriteEndElement();
    }

    private static void WriteWorkbook(XmlWriter writer, IReadOnlyList<XlsxTestSheet> sheets)
    {
        writer.WriteStartElement("workbook", SpreadsheetNamespace);
        writer.WriteAttributeString("xmlns", "r", null, OfficeRelationshipNamespace);
        writer.WriteStartElement("sheets", SpreadsheetNamespace);
        for (var index = 0; index < sheets.Count; index++)
        {
            writer.WriteStartElement("sheet", SpreadsheetNamespace);
            writer.WriteAttributeString("name", sheets[index].Name);
            writer.WriteAttributeString("sheetId", (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteAttributeString("r", "id", OfficeRelationshipNamespace, $"rIdSheet{index + 1}");
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteWorkbookRelationships(XmlWriter writer, int sheetCount, bool hasSharedStrings)
    {
        writer.WriteStartElement("Relationships", PackageRelationshipNamespace);
        for (var index = 1; index <= sheetCount; index++)
        {
            WriteRelationship(
                writer,
                $"rIdSheet{index}",
                "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet",
                $"worksheets/sheet{index}.xml");
        }

        if (hasSharedStrings)
        {
            WriteRelationship(
                writer,
                "rIdSharedStrings",
                "http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings",
                "sharedStrings.xml");
        }

        writer.WriteEndElement();
    }

    private static void WriteWorksheet(XmlWriter writer, XlsxTestSheet sheet, Queue<int> sharedStringIndexes)
    {
        writer.WriteStartElement("worksheet", SpreadsheetNamespace);
        writer.WriteAttributeString("xmlns", "r", null, OfficeRelationshipNamespace);
        writer.WriteStartElement("sheetData", SpreadsheetNamespace);
        for (var rowIndex = 0; rowIndex < sheet.Rows.Count; rowIndex++)
        {
            writer.WriteStartElement("row", SpreadsheetNamespace);
            writer.WriteAttributeString("r", (rowIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var cell in sheet.Rows[rowIndex])
            {
                writer.WriteStartElement("c", SpreadsheetNamespace);
                writer.WriteAttributeString("r", cell.Reference);
                writer.WriteAttributeString("t", cell.Type);
                if (cell.Formula is not null)
                {
                    writer.WriteElementString("f", SpreadsheetNamespace, cell.Formula);
                }

                if (cell.Value is not null)
                {
                    if (cell.Type == "inlineStr" && cell.Formula is null)
                    {
                        writer.WriteStartElement("is", SpreadsheetNamespace);
                        writer.WriteElementString("t", SpreadsheetNamespace, cell.Value);
                        writer.WriteEndElement();
                    }
                    else
                    {
                        var value = cell.Type == "s"
                            ? sharedStringIndexes.Dequeue().ToString(System.Globalization.CultureInfo.InvariantCulture)
                            : cell.Value;
                        writer.WriteElementString("v", SpreadsheetNamespace, value);
                    }
                }

                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        writer.WriteEndElement();

        var hyperlinks = sheet.Rows
            .SelectMany(row => row)
            .Where(cell => cell.Hyperlink is not null)
            .ToArray();
        if (hyperlinks.Length > 0)
        {
            writer.WriteStartElement("hyperlinks", SpreadsheetNamespace);
            for (var index = 0; index < hyperlinks.Length; index++)
            {
                writer.WriteStartElement("hyperlink", SpreadsheetNamespace);
                writer.WriteAttributeString("ref", hyperlinks[index].Reference);
                writer.WriteAttributeString("r", "id", OfficeRelationshipNamespace, $"rIdHyperlink{index + 1}");
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteWorksheetRelationships(XmlWriter writer, IReadOnlyList<XlsxTestCell> hyperlinks)
    {
        writer.WriteStartElement("Relationships", PackageRelationshipNamespace);
        for (var index = 0; index < hyperlinks.Count; index++)
        {
            var hyperlink = hyperlinks[index];
            var target = hyperlink.ExternalHyperlink
                ? hyperlink.Hyperlink!
                : $"../../{hyperlink.Hyperlink}";
            WriteRelationship(
                writer,
                $"rIdHyperlink{index + 1}",
                "http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink",
                target,
                hyperlink.ExternalHyperlink);
        }

        writer.WriteEndElement();
    }

    private static void WriteSharedStrings(XmlWriter writer, IReadOnlyList<string> sharedStrings)
    {
        writer.WriteStartElement("sst", SpreadsheetNamespace);
        writer.WriteAttributeString("count", sharedStrings.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteAttributeString("uniqueCount", sharedStrings.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var value in sharedStrings)
        {
            writer.WriteStartElement("si", SpreadsheetNamespace);
            writer.WriteElementString("t", SpreadsheetNamespace, value);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteRelationship(
        XmlWriter writer,
        string id,
        string type,
        string target,
        bool external = false)
    {
        writer.WriteStartElement("Relationship", PackageRelationshipNamespace);
        writer.WriteAttributeString("Id", id);
        writer.WriteAttributeString("Type", type);
        writer.WriteAttributeString("Target", target);
        if (external)
        {
            writer.WriteAttributeString("TargetMode", "External");
        }

        writer.WriteEndElement();
    }

    private static void WriteXml(ZipArchive archive, string entryPath, Action<XmlWriter> write)
    {
        var entry = archive.CreateEntry(entryPath, CompressionLevel.NoCompression);
        entry.LastWriteTime = EntryTimestamp;
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = false,
            CloseOutput = false,
        });
        write(writer);
    }
}
