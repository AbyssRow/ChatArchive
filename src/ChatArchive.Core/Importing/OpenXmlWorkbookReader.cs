using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace ChatArchive.Core.Importing;

internal sealed record OpenXmlSheet(string Name, string EntryPath);

internal sealed record OpenXmlCell(int ColumnIndex, string Reference, string Value, string? Hyperlink);

internal sealed record OpenXmlRow(uint RowIndex, IReadOnlyDictionary<int, OpenXmlCell> Cells);

internal sealed class OpenXmlWorkbookReader : IDisposable
{
    private const string ContentTypesEntry = "[Content_Types].xml";
    private const string RootRelationshipsEntry = "_rels/.rels";
    private const string WorkbookEntry = "xl/workbook.xml";
    private const string WorkbookRelationshipsEntry = "xl/_rels/workbook.xml.rels";
    private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string ContentTypesNamespace = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string OfficeRelationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelationshipNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string OfficeDocumentRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
    private const string WorksheetRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet";
    private const string SharedStringsRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings";
    private const string HyperlinkRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink";
    private const string WorkbookContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml";
    private const string WorksheetContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml";
    private const string SharedStringsContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml";
    private const int MaximumHyperlinkDeclarations = 10_000;
    private const int MaximumColumnIndex = 16_384;
    private const uint MaximumRowIndex = 1_048_576;

    private readonly string _filePath;
    private readonly ZipArchive _archive;
    private readonly IReadOnlyDictionary<string, ZipArchiveEntry> _entries;
    private readonly IReadOnlyList<string> _sharedStrings;
    private readonly HashSet<OpenXmlSheet> _sheets;
    private bool _disposed;

    public IReadOnlyList<OpenXmlSheet> Sheets { get; }

    private OpenXmlWorkbookReader(
        string filePath,
        ZipArchive archive,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        IReadOnlyList<OpenXmlSheet> sheets,
        IReadOnlyList<string> sharedStrings)
    {
        _filePath = filePath;
        _archive = archive;
        _entries = entries;
        _sharedStrings = sharedStrings;
        Sheets = sheets;
        _sheets = new HashSet<OpenXmlSheet>(sheets);
    }

    public static OpenXmlWorkbookReader Open(string filePath)
    {
        ZipArchive? archive = null;
        try
        {
            archive = ZipFile.OpenRead(filePath);
            var entries = IndexEntries(archive, filePath);
            RequireEntry(entries, ContentTypesEntry, filePath, ContentTypesEntry);
            RequireEntry(entries, RootRelationshipsEntry, filePath, RootRelationshipsEntry);
            RequireEntry(entries, WorkbookEntry, filePath, WorkbookEntry);
            RequireEntry(entries, WorkbookRelationshipsEntry, filePath, WorkbookRelationshipsEntry);
            var contentTypes = ReadContentTypes(entries, filePath);

            var rootRelationships = ReadRelationships(
                entries,
                RootRelationshipsEntry,
                ownerEntry: string.Empty,
                filePath,
                CancellationToken.None);
            var workbookRelationship = SingleRelationshipOfType(
                rootRelationships,
                OfficeDocumentRelationship,
                filePath,
                RootRelationshipsEntry,
                required: true)!;
            var resolvedWorkbook = ResolveRelationship(
                workbookRelationship,
                ownerEntry: string.Empty,
                entries,
                filePath,
                RootRelationshipsEntry);
            if (!string.Equals(resolvedWorkbook, WorkbookEntry, StringComparison.Ordinal))
            {
                throw Error(filePath, RootRelationshipsEntry, $"工作簿关系必须指向 {WorkbookEntry}");
            }

            RequireContentType(contentTypes, resolvedWorkbook, WorkbookContentType, filePath);

            var workbookRelationships = ReadRelationships(
                entries,
                WorkbookRelationshipsEntry,
                WorkbookEntry,
                filePath,
                CancellationToken.None);
            var sheets = ReadSheets(entries, workbookRelationships, filePath);
            foreach (var sheet in sheets)
            {
                RequireContentType(contentTypes, sheet.EntryPath, WorksheetContentType, filePath);
            }

            var sharedStrings = ReadSharedStrings(
                entries,
                workbookRelationships,
                contentTypes,
                filePath,
                CancellationToken.None);
            var reader = new OpenXmlWorkbookReader(
                filePath,
                archive,
                entries,
                Array.AsReadOnly(sheets.ToArray()),
                Array.AsReadOnly(sharedStrings.ToArray()));
            archive = null;
            return reader;
        }
        catch (ImportFormatException)
        {
            archive?.Dispose();
            throw;
        }
        catch (Exception ex) when (IsPackageFailure(ex))
        {
            archive?.Dispose();
            throw new ImportFormatException(filePath, $"XLSX 包读取失败（{ex.Message}）");
        }
    }

    public IEnumerable<OpenXmlRow> ReadRows(OpenXmlSheet sheet, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_sheets.Contains(sheet))
        {
            throw new ImportFormatException(_filePath, $"XLSX 工作表不属于当前工作簿：{sheet.Name}");
        }

        return ReadRowsWithFormatErrors(sheet, token);
    }

    private IEnumerable<OpenXmlRow> ReadRowsWithFormatErrors(OpenXmlSheet sheet, CancellationToken token)
    {
        using var rows = ReadRowsCore(sheet, token).GetEnumerator();
        while (true)
        {
            OpenXmlRow current;
            try
            {
                if (!rows.MoveNext())
                {
                    yield break;
                }

                current = rows.Current;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ImportFormatException)
            {
                throw;
            }
            catch (Exception ex) when (IsPackageFailure(ex))
            {
                throw Error(_filePath, sheet.EntryPath, $"工作表解析失败（{ex.Message}）");
            }

            yield return current;
        }
    }

    private IEnumerable<OpenXmlRow> ReadRowsCore(OpenXmlSheet sheet, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var hyperlinks = ReadHyperlinks(sheet, token);
        var entry = RequireEntry(_entries, sheet.EntryPath, _filePath, sheet.EntryPath);
        using var stream = entry.Open();
        using var reader = CreateXmlReader(stream);
        var sawWorksheet = false;
        var sawSheetData = false;
        var insideSheetData = false;
        uint previousRowIndex = 0;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == 0)
            {
                if (reader.LocalName != "worksheet" || reader.NamespaceURI != SpreadsheetNamespace)
                {
                    throw Error(_filePath, sheet.EntryPath, "worksheet 根元素无效");
                }

                sawWorksheet = true;
                continue;
            }

            if (reader.NodeType == XmlNodeType.Element
                && reader.LocalName == "sheetData"
                && reader.NamespaceURI == SpreadsheetNamespace)
            {
                if (reader.Depth != 1 || sawSheetData)
                {
                    throw Error(_filePath, sheet.EntryPath, "sheetData 必须是 worksheet 的唯一直接子元素");
                }

                sawSheetData = true;
                insideSheetData = !reader.IsEmptyElement;
                continue;
            }

            if (reader.NodeType == XmlNodeType.EndElement
                && reader.Depth == 1
                && reader.LocalName == "sheetData"
                && reader.NamespaceURI == SpreadsheetNamespace)
            {
                insideSheetData = false;
                continue;
            }

            if (reader.NodeType == XmlNodeType.Element
                && reader.LocalName == "c"
                && reader.NamespaceURI == SpreadsheetNamespace)
            {
                throw Error(_filePath, sheet.EntryPath, "c 必须是 row 的直接子元素");
            }

            if (reader.NodeType != XmlNodeType.Element
                || reader.LocalName != "row"
                || reader.NamespaceURI != SpreadsheetNamespace)
            {
                continue;
            }

            if (!insideSheetData || reader.Depth != 2)
            {
                throw Error(_filePath, sheet.EntryPath, "row 必须是 sheetData 的直接子元素");
            }

            token.ThrowIfCancellationRequested();
            var rowText = reader.GetAttribute("r");
            if (!uint.TryParse(rowText, NumberStyles.None, CultureInfo.InvariantCulture, out var rowIndex)
                || rowIndex is 0 or > MaximumRowIndex)
            {
                throw Error(_filePath, sheet.EntryPath, $"行号无效：{rowText ?? "<缺失>"}");
            }

            if (rowIndex <= previousRowIndex)
            {
                throw Error(
                    _filePath,
                    sheet.EntryPath,
                    $"行 {rowIndex} 必须严格递增（上一行 {previousRowIndex}）");
            }

            previousRowIndex = rowIndex;

            var cells = new Dictionary<int, OpenXmlCell>();
            using (var rowReader = reader.ReadSubtree())
            {
                rowReader.Read();
                while (rowReader.Read())
                {
                    if (rowReader.NodeType == XmlNodeType.Element
                        && rowReader.LocalName == "row"
                        && rowReader.NamespaceURI == SpreadsheetNamespace)
                    {
                        throw Error(
                            _filePath,
                            sheet.EntryPath,
                            $"行 {rowIndex} 中 row 不能嵌套，row 必须是 sheetData 的直接子元素");
                    }

                    if (rowReader.NodeType != XmlNodeType.Element
                        || rowReader.LocalName != "c"
                        || rowReader.NamespaceURI != SpreadsheetNamespace)
                    {
                        continue;
                    }

                    if (rowReader.Depth != 1)
                    {
                        throw Error(
                            _filePath,
                            sheet.EntryPath,
                            $"行 {rowIndex} 中 c 必须是 row 的直接子元素");
                    }

                    token.ThrowIfCancellationRequested();
                    var cell = ReadCell(rowReader, sheet.EntryPath, rowIndex, hyperlinks, token);
                    if (!cells.TryAdd(cell.ColumnIndex, cell))
                    {
                        throw CellError(
                            _filePath,
                            sheet.EntryPath,
                            cell.Reference,
                            $"第 {cell.ColumnIndex} 列重复");
                    }
                }
            }

            yield return new OpenXmlRow(rowIndex, cells);
        }

        if (!sawWorksheet)
        {
            throw Error(_filePath, sheet.EntryPath, "缺少 worksheet 根元素");
        }
    }

    private OpenXmlCell ReadCell(
        XmlReader cellReader,
        string entryPath,
        uint rowIndex,
        IReadOnlyDictionary<string, string> hyperlinks,
        CancellationToken token)
    {
        var reference = cellReader.GetAttribute("r") ?? string.Empty;
        var (columnIndex, referencedRow, normalizedReference) = ParseCellReference(reference, entryPath);
        if (referencedRow != rowIndex)
        {
            throw CellError(_filePath, entryPath, reference, $"引用行 {referencedRow} 与所在行 {rowIndex} 不一致");
        }

        var type = cellReader.GetAttribute("t");
        var hasFormula = false;
        var hasCachedValue = false;
        var cachedValue = string.Empty;
        var inlineText = new StringBuilder();
        using (var contents = cellReader.ReadSubtree())
        {
            contents.Read();
            while (contents.Read())
            {
                token.ThrowIfCancellationRequested();
                if (contents.NodeType != XmlNodeType.Element || contents.NamespaceURI != SpreadsheetNamespace)
                {
                    continue;
                }

                switch (contents.LocalName)
                {
                    case "f":
                        hasFormula = true;
                        break;
                    case "v":
                        if (hasCachedValue)
                        {
                            throw CellError(_filePath, entryPath, reference, "包含重复缓存值");
                        }

                        hasCachedValue = true;
                        cachedValue = contents.ReadElementContentAsString();
                        break;
                    case "t" when type == "inlineStr":
                        inlineText.Append(contents.ReadElementContentAsString());
                        break;
                }
            }
        }

        var value = hasFormula && !hasCachedValue
            ? string.Empty
            : ResolveCellValue(type, hasCachedValue, cachedValue, inlineText.ToString(), entryPath, reference);
        hyperlinks.TryGetValue(normalizedReference, out var hyperlink);
        return new OpenXmlCell(columnIndex, reference, value, hyperlink);
    }

    private string ResolveCellValue(
        string? type,
        bool hasCachedValue,
        string cachedValue,
        string inlineText,
        string entryPath,
        string reference)
    {
        if (type == "inlineStr")
        {
            return inlineText;
        }

        if (!hasCachedValue)
        {
            return string.Empty;
        }

        return type switch
        {
            null or "" or "n" or "str" or "d" => cachedValue,
            "b" when cachedValue == "1" => "true",
            "b" when cachedValue == "0" => "false",
            "b" => throw CellError(_filePath, entryPath, reference, $"布尔缓存值无效：{cachedValue}"),
            "s" => ResolveSharedString(cachedValue, entryPath, reference),
            _ => throw CellError(_filePath, entryPath, reference, $"不支持的单元格类型：{type}"),
        };
    }

    private string ResolveSharedString(string value, string entryPath, string reference)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            || index < 0
            || index >= _sharedStrings.Count)
        {
            throw CellError(_filePath, entryPath, reference, $"共享字符串索引无效：{value}");
        }

        return _sharedStrings[index];
    }

    private IReadOnlyDictionary<string, string> ReadHyperlinks(OpenXmlSheet sheet, CancellationToken token)
    {
        var hyperlinkRelationshipIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hyperlinkDeclarations = 0;
        var entry = RequireEntry(_entries, sheet.EntryPath, _filePath, sheet.EntryPath);
        using (var stream = entry.Open())
        using (var reader = CreateXmlReader(stream))
        {
            var sawWorksheet = false;
            var sawHyperlinks = false;
            var insideHyperlinks = false;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Depth == 0)
                {
                    if (reader.LocalName != "worksheet" || reader.NamespaceURI != SpreadsheetNamespace)
                    {
                        throw Error(_filePath, sheet.EntryPath, "worksheet 根元素无效");
                    }

                    sawWorksheet = true;
                    continue;
                }

                if (reader.NodeType == XmlNodeType.Element
                    && reader.LocalName == "sheetData"
                    && reader.NamespaceURI == SpreadsheetNamespace
                    && reader.Depth != 1)
                {
                    throw Error(_filePath, sheet.EntryPath, "sheetData 必须是 worksheet 的直接子元素");
                }

                if (reader.NodeType == XmlNodeType.Element
                    && reader.LocalName == "hyperlinks"
                    && reader.NamespaceURI == SpreadsheetNamespace)
                {
                    if (reader.Depth != 1 || sawHyperlinks)
                    {
                        throw Error(_filePath, sheet.EntryPath, "hyperlinks 必须是 worksheet 的唯一直接子元素");
                    }

                    sawHyperlinks = true;
                    insideHyperlinks = !reader.IsEmptyElement;
                    continue;
                }

                if (reader.NodeType == XmlNodeType.EndElement
                    && reader.Depth == 1
                    && reader.LocalName == "hyperlinks"
                    && reader.NamespaceURI == SpreadsheetNamespace)
                {
                    insideHyperlinks = false;
                    continue;
                }

                if (reader.NodeType != XmlNodeType.Element
                    || reader.LocalName != "hyperlink"
                    || reader.NamespaceURI != SpreadsheetNamespace)
                {
                    continue;
                }

                if (!insideHyperlinks || reader.Depth != 2)
                {
                    throw Error(_filePath, sheet.EntryPath, "hyperlink 必须是 hyperlinks 的直接子元素");
                }

                token.ThrowIfCancellationRequested();
                if (hyperlinkDeclarations == MaximumHyperlinkDeclarations)
                {
                    throw Error(
                        _filePath,
                        sheet.EntryPath,
                        $"每个工作表最多允许 {MaximumHyperlinkDeclarations} 个 hyperlink 声明");
                }

                hyperlinkDeclarations++;
                var reference = reader.GetAttribute("ref") ?? string.Empty;
                var (_, _, normalizedReference) = ParseCellReference(reference, sheet.EntryPath);
                var relationshipId = reader.GetAttribute("id", OfficeRelationshipNamespace);
                if (string.IsNullOrWhiteSpace(relationshipId))
                {
                    throw CellError(_filePath, sheet.EntryPath, reference, "超链接缺少关系 ID");
                }

                if (!hyperlinkRelationshipIds.TryAdd(normalizedReference, relationshipId))
                {
                    throw CellError(_filePath, sheet.EntryPath, reference, "超链接重复");
                }
            }

            if (!sawWorksheet)
            {
                throw Error(_filePath, sheet.EntryPath, "缺少 worksheet 根元素");
            }
        }

        var relationshipEntry = RelationshipEntryFor(sheet.EntryPath);
        IReadOnlyDictionary<string, PackageRelationship>? relationships = null;
        if (_entries.ContainsKey(relationshipEntry))
        {
            relationships = ReadRelationships(
                _entries,
                relationshipEntry,
                sheet.EntryPath,
                _filePath,
                token,
                MaximumHyperlinkDeclarations);
            foreach (var relationship in relationships.Values)
            {
                token.ThrowIfCancellationRequested();
                if (relationship.Type == HyperlinkRelationship)
                {
                    _ = ResolveRelationship(
                        relationship,
                        sheet.EntryPath,
                        _entries,
                        _filePath,
                        relationshipEntry);
                }
            }
        }

        if (hyperlinkRelationshipIds.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        if (relationships is null)
        {
            throw Error(_filePath, sheet.EntryPath, $"找不到超链接关系条目 {relationshipEntry}");
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (reference, relationshipId) in hyperlinkRelationshipIds)
        {
            token.ThrowIfCancellationRequested();
            if (!relationships.TryGetValue(relationshipId, out var relationship))
            {
                throw CellError(_filePath, sheet.EntryPath, reference, $"找不到超链接关系 {relationshipId}");
            }

            if (relationship.Type != HyperlinkRelationship)
            {
                throw CellError(_filePath, sheet.EntryPath, reference, $"关系 {relationshipId} 不是超链接关系");
            }

            result.Add(
                reference,
                ResolveRelationship(relationship, sheet.EntryPath, _entries, _filePath, relationshipEntry));
        }

        return result;
    }

    private static IReadOnlyList<OpenXmlSheet> ReadSheets(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        IReadOnlyDictionary<string, PackageRelationship> relationships,
        string filePath)
    {
        return WithEntryFormatErrors(
            filePath,
            WorkbookEntry,
            () => ReadSheetsCore(entries, relationships, filePath));
    }

    private static IReadOnlyList<OpenXmlSheet> ReadSheetsCore(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        IReadOnlyDictionary<string, PackageRelationship> relationships,
        string filePath)
    {
        var sheets = new List<OpenXmlSheet>();
        var relationshipIds = new HashSet<string>(StringComparer.Ordinal);
        var workbook = RequireEntry(entries, WorkbookEntry, filePath, WorkbookEntry);
        using var stream = workbook.Open();
        using var reader = CreateXmlReader(stream);
        var sawWorkbook = false;
        var sawSheets = false;
        var insideSheets = false;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == 0)
            {
                if (reader.LocalName != "workbook" || reader.NamespaceURI != SpreadsheetNamespace)
                {
                    throw Error(filePath, WorkbookEntry, "workbook 根元素无效");
                }

                sawWorkbook = true;
                continue;
            }

            if (reader.NodeType == XmlNodeType.Element
                && reader.LocalName == "sheets"
                && reader.NamespaceURI == SpreadsheetNamespace)
            {
                if (reader.Depth != 1 || sawSheets)
                {
                    throw Error(filePath, WorkbookEntry, "sheets 元素位置无效");
                }

                sawSheets = true;
                insideSheets = !reader.IsEmptyElement;
                continue;
            }

            if (reader.NodeType == XmlNodeType.EndElement
                && reader.Depth == 1
                && reader.LocalName == "sheets"
                && reader.NamespaceURI == SpreadsheetNamespace)
            {
                insideSheets = false;
                continue;
            }

            if (reader.NodeType != XmlNodeType.Element
                || reader.LocalName != "sheet"
                || reader.NamespaceURI != SpreadsheetNamespace)
            {
                continue;
            }

            if (!insideSheets || reader.Depth != 2)
            {
                throw Error(filePath, WorkbookEntry, "sheet 必须是 sheets 的直接子元素");
            }

            CancellationToken.None.ThrowIfCancellationRequested();
            var name = reader.GetAttribute("name");
            var relationshipId = reader.GetAttribute("id", OfficeRelationshipNamespace);
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(relationshipId))
            {
                throw Error(filePath, WorkbookEntry, "工作表缺少名称或关系 ID");
            }

            if (!relationshipIds.Add(relationshipId))
            {
                throw Error(filePath, WorkbookEntry, $"工作表关系 ID 重复：{relationshipId}");
            }

            if (!relationships.TryGetValue(relationshipId, out var relationship))
            {
                throw Error(filePath, WorkbookEntry, $"找不到工作表 {name} 的关系 {relationshipId}");
            }

            if (relationship.Type != WorksheetRelationship)
            {
                throw Error(filePath, WorkbookEntry, $"关系 {relationshipId} 不是工作表关系");
            }

            var entryPath = ResolveRelationship(
                relationship,
                WorkbookEntry,
                entries,
                filePath,
                WorkbookRelationshipsEntry);
            sheets.Add(new OpenXmlSheet(name, entryPath));
        }

        if (!sawWorkbook)
        {
            throw Error(filePath, WorkbookEntry, "缺少 workbook 根元素");
        }

        return sheets;
    }

    private static IReadOnlyList<string> ReadSharedStrings(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        IReadOnlyDictionary<string, PackageRelationship> relationships,
        PackageContentTypes contentTypes,
        string filePath,
        CancellationToken token)
    {
        var relationship = SingleRelationshipOfType(
            relationships,
            SharedStringsRelationship,
            filePath,
            WorkbookRelationshipsEntry,
            required: false);
        if (relationship is null)
        {
            return Array.Empty<string>();
        }

        var entryPath = ResolveRelationship(
            relationship,
            WorkbookEntry,
            entries,
            filePath,
            WorkbookRelationshipsEntry);
        RequireContentType(contentTypes, entryPath, SharedStringsContentType, filePath);
        return WithEntryFormatErrors(
            filePath,
            entryPath,
            () => ReadSharedStringEntry(entries, entryPath, filePath, token));
    }

    private static IReadOnlyList<string> ReadSharedStringEntry(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        string entryPath,
        string filePath,
        CancellationToken token)
    {
        var entry = RequireEntry(entries, entryPath, filePath, entryPath);
        var values = new List<string>();
        using var stream = entry.Open();
        using var reader = CreateXmlReader(stream);
        var sawSharedStrings = false;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == 0)
            {
                if (reader.LocalName != "sst" || reader.NamespaceURI != SpreadsheetNamespace)
                {
                    throw Error(filePath, entryPath, "sst 根元素无效");
                }

                sawSharedStrings = true;
                continue;
            }

            if (reader.NodeType != XmlNodeType.Element
                || reader.LocalName != "si"
                || reader.NamespaceURI != SpreadsheetNamespace)
            {
                continue;
            }

            if (reader.Depth != 1)
            {
                throw Error(filePath, entryPath, "si 必须是 sst 的直接子元素");
            }

            token.ThrowIfCancellationRequested();
            var value = new StringBuilder();
            using (var itemReader = reader.ReadSubtree())
            {
                itemReader.Read();
                while (itemReader.Read())
                {
                    if (itemReader.NodeType == XmlNodeType.Element
                        && itemReader.LocalName == "si"
                        && itemReader.NamespaceURI == SpreadsheetNamespace)
                    {
                        throw Error(filePath, entryPath, "si 不能嵌套，si 必须是 sst 的直接子元素");
                    }

                    if (itemReader.NodeType == XmlNodeType.Element
                        && itemReader.LocalName == "t"
                        && itemReader.NamespaceURI == SpreadsheetNamespace)
                    {
                        token.ThrowIfCancellationRequested();
                        value.Append(itemReader.ReadElementContentAsString());
                    }
                }
            }

            values.Add(value.ToString());
        }

        if (!sawSharedStrings)
        {
            throw Error(filePath, entryPath, "缺少 sst 根元素");
        }

        return values;
    }

    private static IReadOnlyDictionary<string, PackageRelationship> ReadRelationships(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        string relationshipEntry,
        string ownerEntry,
        string filePath,
        CancellationToken token,
        int? maximumRelationships = null)
    {
        return WithEntryFormatErrors(
            filePath,
            relationshipEntry,
            () => ReadRelationshipsCore(
                entries,
                relationshipEntry,
                ownerEntry,
                filePath,
                token,
                maximumRelationships));
    }

    private static IReadOnlyDictionary<string, PackageRelationship> ReadRelationshipsCore(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        string relationshipEntry,
        string ownerEntry,
        string filePath,
        CancellationToken token,
        int? maximumRelationships)
    {
        var entry = RequireEntry(entries, relationshipEntry, filePath, relationshipEntry);
        var relationships = new Dictionary<string, PackageRelationship>(StringComparer.Ordinal);
        var relationshipDeclarations = 0;
        using var stream = entry.Open();
        using var reader = CreateXmlReader(stream);
        var sawRelationships = false;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == 0)
            {
                if (reader.LocalName != "Relationships" || reader.NamespaceURI != PackageRelationshipNamespace)
                {
                    throw Error(filePath, relationshipEntry, "Relationships 根元素无效");
                }

                sawRelationships = true;
                continue;
            }

            if (reader.NodeType != XmlNodeType.Element
                || reader.LocalName != "Relationship"
                || reader.NamespaceURI != PackageRelationshipNamespace)
            {
                continue;
            }

            if (reader.Depth != 1)
            {
                throw Error(filePath, relationshipEntry, "Relationship 必须是 Relationships 的直接子元素");
            }

            token.ThrowIfCancellationRequested();
            if (maximumRelationships is int maximum
                && relationshipDeclarations == maximum)
            {
                throw Error(
                    filePath,
                    relationshipEntry,
                    $"每个工作表最多允许 {maximum} 个 Relationship 声明");
            }

            relationshipDeclarations++;
            var id = reader.GetAttribute("Id");
            var type = reader.GetAttribute("Type");
            var target = reader.GetAttribute("Target");
            var targetMode = reader.GetAttribute("TargetMode");
            if (string.IsNullOrWhiteSpace(id)
                || string.IsNullOrWhiteSpace(type)
                || string.IsNullOrWhiteSpace(target))
            {
                throw Error(filePath, relationshipEntry, "关系缺少 Id、Type 或 Target");
            }

            var isExternal = string.Equals(targetMode, "External", StringComparison.OrdinalIgnoreCase);
            if (targetMode is not null
                && !isExternal
                && !string.Equals(targetMode, "Internal", StringComparison.OrdinalIgnoreCase))
            {
                throw Error(filePath, relationshipEntry, $"关系 {id} 的 TargetMode 无效：{targetMode}");
            }

            if (isExternal)
            {
                throw Error(filePath, relationshipEntry, $"关系 {id} 是外部关系");
            }

            if (IsForbiddenRelationshipType(type))
            {
                throw Error(filePath, relationshipEntry, $"关系 {id} 声明了禁止的宏或二进制类型：{type}");
            }

            var resolvedTarget = ResolvePackageTarget(ownerEntry, target, filePath, relationshipEntry);
            if (IsForbiddenPayloadPath(resolvedTarget))
            {
                throw Error(filePath, relationshipEntry, $"关系 {id} 指向禁止的宏或二进制负载：{resolvedTarget}");
            }

            if (!relationships.TryAdd(id, new PackageRelationship(id, type, target, isExternal)))
            {
                throw Error(filePath, relationshipEntry, $"关系 ID 重复：{id}");
            }
        }

        if (!sawRelationships)
        {
            throw Error(filePath, relationshipEntry, "缺少 Relationships 根元素");
        }

        return relationships;
    }

    private static PackageContentTypes ReadContentTypes(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        string filePath)
    {
        return WithEntryFormatErrors(filePath, ContentTypesEntry, () =>
        {
            var entry = RequireEntry(entries, ContentTypesEntry, filePath, ContentTypesEntry);
            var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var sawTypes = false;
            using var stream = entry.Open();
            using var reader = CreateXmlReader(stream);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Depth == 0)
                {
                    if (reader.LocalName != "Types" || reader.NamespaceURI != ContentTypesNamespace)
                    {
                        throw Error(filePath, ContentTypesEntry, "Types 根元素无效");
                    }

                    sawTypes = true;
                    continue;
                }

                if (reader.NodeType != XmlNodeType.Element || reader.NamespaceURI != ContentTypesNamespace)
                {
                    continue;
                }

                CancellationToken.None.ThrowIfCancellationRequested();
                var contentType = reader.GetAttribute("ContentType");
                if (reader.Depth != 1 || reader.LocalName is not ("Default" or "Override"))
                {
                    throw Error(filePath, ContentTypesEntry, $"{reader.LocalName} 元素位置或类型无效");
                }

                if (string.IsNullOrWhiteSpace(contentType))
                {
                    throw Error(filePath, ContentTypesEntry, $"{reader.LocalName} 缺少 ContentType");
                }

                RejectForbiddenContentType(contentType, filePath);
                if (reader.LocalName == "Default")
                {
                    var extension = reader.GetAttribute("Extension");
                    if (string.IsNullOrWhiteSpace(extension)
                        || extension.Contains('/')
                        || extension.Contains('\\')
                        || extension[0] == '.')
                    {
                        throw Error(filePath, ContentTypesEntry, "Default 缺少有效 Extension");
                    }

                    if (!defaults.TryAdd(extension, contentType))
                    {
                        throw Error(filePath, ContentTypesEntry, $"Extension 重复或歧义：{extension}");
                    }

                    continue;
                }

                var declaredPart = reader.GetAttribute("PartName");
                if (string.IsNullOrWhiteSpace(declaredPart) || declaredPart[0] != '/')
                {
                    throw Error(filePath, ContentTypesEntry, "Override 缺少有效 PartName");
                }

                var partName = declaredPart[1..];
                try
                {
                    ValidateEntryPath(partName, filePath);
                }
                catch (ImportFormatException)
                {
                    throw Error(filePath, ContentTypesEntry, $"PartName 无效：{declaredPart}");
                }

                if (!overrides.TryAdd(partName, contentType))
                {
                    throw Error(filePath, ContentTypesEntry, $"PartName 重复或歧义：{declaredPart}");
                }
            }

            if (!sawTypes)
            {
                throw Error(filePath, ContentTypesEntry, "缺少 Types 根元素");
            }

            return new PackageContentTypes(overrides, defaults);
        });
    }

    private static void RequireContentType(
        PackageContentTypes contentTypes,
        string entryPath,
        string expected,
        string filePath)
    {
        var actual = contentTypes.Get(entryPath);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw Error(
                filePath,
                ContentTypesEntry,
                $"条目 {entryPath} 的 ContentType 必须为 {expected}，实际为 {actual ?? "<缺失>"}");
        }
    }

    private static void RejectForbiddenContentType(string contentType, string filePath)
    {
        if (contentType.Contains("macroEnabled", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("vbaProject", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("activeX", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("macrosheet", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("intlmacrosheet", StringComparison.OrdinalIgnoreCase)
            || contentType.Equals(
                "application/vnd.openxmlformats-officedocument.oleObject",
                StringComparison.OrdinalIgnoreCase))
        {
            throw Error(filePath, ContentTypesEntry, $"禁止的宏或二进制 ContentType：{contentType}");
        }
    }

    private static bool IsForbiddenRelationshipType(string type)
    {
        var separator = type.LastIndexOf('/');
        var name = separator >= 0 ? type[(separator + 1)..] : type;
        return name.Equals("vbaProject", StringComparison.OrdinalIgnoreCase)
            || name.Equals("vbaProjectSignature", StringComparison.OrdinalIgnoreCase)
            || name.Equals("activeXControl", StringComparison.OrdinalIgnoreCase)
            || name.Equals("activeXControlBinary", StringComparison.OrdinalIgnoreCase)
            || name.Equals("oleObject", StringComparison.OrdinalIgnoreCase)
            || name.Equals("package", StringComparison.OrdinalIgnoreCase)
            || name.Equals("control", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsForbiddenPayloadPath(string path)
    {
        var fileName = path[(path.LastIndexOf('/') + 1)..];
        return fileName.Equals("vbaProject.bin", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("vbaProjectSignature.bin", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("xl/activeX/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("xl/embeddings/", StringComparison.OrdinalIgnoreCase);
    }

    private static PackageRelationship? SingleRelationshipOfType(
        IReadOnlyDictionary<string, PackageRelationship> relationships,
        string type,
        string filePath,
        string relationshipEntry,
        bool required)
    {
        PackageRelationship? result = null;
        foreach (var relationship in relationships.Values)
        {
            CancellationToken.None.ThrowIfCancellationRequested();
            if (relationship.Type != type)
            {
                continue;
            }

            if (result is not null)
            {
                throw Error(filePath, relationshipEntry, $"关系类型重复：{type}");
            }

            result = relationship;
        }

        if (required && result is null)
        {
            throw Error(filePath, relationshipEntry, $"缺少关系类型：{type}");
        }

        return result;
    }

    private static string ResolveRelationship(
        PackageRelationship relationship,
        string ownerEntry,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        string filePath,
        string relationshipEntry)
    {
        if (relationship.IsExternal)
        {
            throw Error(filePath, relationshipEntry, $"关系 {relationship.Id} 是外部关系");
        }

        var target = ResolvePackageTarget(ownerEntry, relationship.Target, filePath, relationshipEntry);
        RequireEntry(entries, target, filePath, relationshipEntry);
        return target;
    }

    private static string ResolvePackageTarget(
        string ownerEntry,
        string target,
        string filePath,
        string relationshipEntry)
    {
        if (string.IsNullOrWhiteSpace(target)
            || Path.IsPathRooted(target)
            || target.Contains('\\')
            || target[0] == '/'
            || target.Contains('?')
            || target.Contains('#')
            || IsDrivePath(target)
            || Uri.TryCreate(target, UriKind.Absolute, out _))
        {
            throw Error(filePath, relationshipEntry, $"XLSX 包含非法关系路径：{target}");
        }

        var ownerSeparator = ownerEntry.LastIndexOf('/');
        var ownerDirectory = ownerSeparator < 0 ? string.Empty : ownerEntry[..(ownerSeparator + 1)];
        var stack = new List<string>();
        foreach (var segment in (ownerDirectory + target).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (stack.Count == 0)
                {
                    throw Error(filePath, relationshipEntry, $"XLSX 关系越界：{target}");
                }

                stack.RemoveAt(stack.Count - 1);
            }
            else
            {
                stack.Add(segment);
            }
        }

        if (stack.Count == 0)
        {
            throw Error(filePath, relationshipEntry, $"XLSX 包含非法关系路径：{target}");
        }

        return string.Join('/', stack);
    }

    private (int ColumnIndex, uint RowIndex, string NormalizedReference) ParseCellReference(
        string reference,
        string entryPath)
    {
        if (string.IsNullOrEmpty(reference))
        {
            throw CellError(_filePath, entryPath, "<缺失>", "引用无效");
        }

        var position = 0;
        var columnIndex = 0;
        try
        {
            while (position < reference.Length && IsAsciiLetter(reference[position]))
            {
                columnIndex = checked(
                    columnIndex * 26
                    + char.ToUpperInvariant(reference[position]) - 'A'
                    + 1);
                position++;
            }
        }
        catch (OverflowException)
        {
            throw CellError(_filePath, entryPath, reference, "列索引溢出");
        }

        if (position == 0
            || columnIndex > MaximumColumnIndex
            || position == reference.Length
            || reference[position] == '0')
        {
            throw CellError(_filePath, entryPath, reference, "引用无效");
        }

        var rowStart = position;
        while (position < reference.Length && reference[position] is >= '0' and <= '9')
        {
            position++;
        }

        if (position != reference.Length
            || !uint.TryParse(reference.AsSpan(rowStart), NumberStyles.None, CultureInfo.InvariantCulture, out var rowIndex)
            || rowIndex is 0 or > MaximumRowIndex)
        {
            throw CellError(_filePath, entryPath, reference, "引用无效");
        }

        var normalized = string.Concat(
            reference.AsSpan(0, rowStart).ToString().ToUpperInvariant(),
            rowIndex.ToString(CultureInfo.InvariantCulture));
        return (columnIndex, rowIndex, normalized);
    }

    private static IReadOnlyDictionary<string, ZipArchiveEntry> IndexEntries(ZipArchive archive, string filePath)
    {
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        var entryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            CancellationToken.None.ThrowIfCancellationRequested();
            var path = entry.FullName;
            ValidateEntryPath(path, filePath);
            var identity = path.EndsWith("/", StringComparison.Ordinal) ? path[..^1] : path;
            if (IsForbiddenPayloadPath(identity))
            {
                throw new ImportFormatException(
                    filePath,
                    $"XLSX 包含禁止的宏或二进制负载条目：{identity}");
            }

            if (!entryNames.Add(identity))
            {
                throw new ImportFormatException(filePath, $"XLSX 包含重复或歧义条目：{path}");
            }

            if (!path.EndsWith("/", StringComparison.Ordinal))
            {
                entries.Add(path, entry);
            }
        }

        return entries;
    }

    private static void ValidateEntryPath(string path, string filePath)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path[0] == '/'
            || path.Contains('\\')
            || Path.IsPathRooted(path)
            || IsDrivePath(path)
            || Uri.TryCreate(path, UriKind.Absolute, out _))
        {
            throw new ImportFormatException(filePath, $"XLSX 包含非法条目路径：{path}");
        }

        var isDirectory = path.EndsWith("/", StringComparison.Ordinal);
        var segments = path.Split('/');
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            if (isDirectory && index == segments.Length - 1 && segment.Length == 0)
            {
                continue;
            }

            if (segment.Length == 0 || segment is "." or "..")
            {
                throw new ImportFormatException(filePath, $"XLSX 包含非法条目路径：{path}");
            }
        }
    }

    private static ZipArchiveEntry RequireEntry(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        string entryPath,
        string filePath,
        string contextEntry)
    {
        if (!entries.TryGetValue(entryPath, out var entry))
        {
            throw Error(filePath, contextEntry, $"找不到 XLSX 条目 {entryPath}");
        }

        return entry;
    }

    private static XmlReader CreateXmlReader(Stream stream)
    {
        return XmlReader.Create(stream, new XmlReaderSettings
        {
            CloseInput = false,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        });
    }

    private static T WithEntryFormatErrors<T>(string filePath, string entryPath, Func<T> read)
    {
        try
        {
            return read();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ImportFormatException)
        {
            throw;
        }
        catch (Exception ex) when (IsPackageFailure(ex))
        {
            throw Error(filePath, entryPath, $"解析失败（{ex.Message}）");
        }
    }

    private static string RelationshipEntryFor(string ownerEntry)
    {
        var separator = ownerEntry.LastIndexOf('/');
        var directory = separator < 0 ? string.Empty : ownerEntry[..(separator + 1)];
        var filename = separator < 0 ? ownerEntry : ownerEntry[(separator + 1)..];
        return $"{directory}_rels/{filename}.rels";
    }

    private static bool IsAsciiLetter(char value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsDrivePath(string path) => path.Length >= 2 && IsAsciiLetter(path[0]) && path[1] == ':';

    private static bool IsPackageFailure(Exception exception) => exception is
        IOException
        or UnauthorizedAccessException
        or InvalidDataException
        or XmlException
        or ArgumentException
        or NotSupportedException;

    private static ImportFormatException Error(string filePath, string entryPath, string message)
    {
        return new ImportFormatException(filePath, $"XLSX 条目 {entryPath}：{message}");
    }

    private static ImportFormatException CellError(
        string filePath,
        string entryPath,
        string reference,
        string message)
    {
        return Error(filePath, entryPath, $"单元格 {reference}：{message}");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _archive.Dispose();
    }

    private sealed record PackageContentTypes(
        IReadOnlyDictionary<string, string> Overrides,
        IReadOnlyDictionary<string, string> Defaults)
    {
        internal string? Get(string entryPath)
        {
            if (Overrides.TryGetValue(entryPath, out var contentType))
            {
                return contentType;
            }

            var separator = entryPath.LastIndexOf('/');
            var dot = entryPath.LastIndexOf('.');
            if (dot <= separator || dot == entryPath.Length - 1)
            {
                return null;
            }

            return Defaults.TryGetValue(entryPath[(dot + 1)..], out contentType)
                ? contentType
                : null;
        }
    }

    private sealed record PackageRelationship(string Id, string Type, string Target, bool IsExternal);
}
