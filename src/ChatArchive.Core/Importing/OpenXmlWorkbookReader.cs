using System.Globalization;
using System.Text;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using SdkOpenXmlReader = DocumentFormat.OpenXml.OpenXmlReader;

namespace ChatArchive.Core.Importing;

internal sealed record OpenXmlSheet(string Name, string EntryPath);

internal sealed record OpenXmlCell(int ColumnIndex, string Reference, string Value, string? Hyperlink);

internal sealed record OpenXmlRow(uint RowIndex, IReadOnlyDictionary<int, OpenXmlCell> Cells);

internal sealed class OpenXmlWorkbookReader : IDisposable
{
    private const string WorkbookEntry = "xl/workbook.xml";
    private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string OfficeRelationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
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
    private readonly XlsxPackageHandle _package;
    private readonly SpreadsheetDocument _document;
    private readonly IReadOnlyDictionary<OpenXmlSheet, WorksheetPart> _worksheetParts;
    private readonly IReadOnlyList<string> _sharedStrings;
    private readonly HashSet<OpenXmlSheet> _sheets;
    private bool _disposed;

    public IReadOnlyList<OpenXmlSheet> Sheets { get; }

    private OpenXmlWorkbookReader(
        string filePath,
        XlsxPackageHandle package,
        SpreadsheetDocument document,
        IReadOnlyList<OpenXmlSheet> sheets,
        IReadOnlyDictionary<OpenXmlSheet, WorksheetPart> worksheetParts,
        IReadOnlyList<string> sharedStrings)
    {
        _filePath = filePath;
        _package = package;
        _document = document;
        _worksheetParts = worksheetParts;
        _sharedStrings = sharedStrings;
        Sheets = sheets;
        _sheets = new HashSet<OpenXmlSheet>(sheets);
    }

    public static OpenXmlWorkbookReader Open(string filePath)
    {
        XlsxPackageHandle? package = null;
        SpreadsheetDocument? document = null;
        try
        {
            package = XlsxPackagePreflight.OpenValidated(filePath);
            ValidateWorkbookEntryContentType(package, filePath);
            document = SpreadsheetDocument.Open(package.Stream, isEditable: false);
            var workbookPart = RequireWorkbookPart(document, package, filePath);
            ValidateContainerRelationships(
                document,
                filePath,
                "_rels/.rels",
                allowExternalHyperlinks: false);
            ValidateWorkbookRelationships(workbookPart, package, filePath);
            ValidateWorkbookPartContentTypes(workbookPart, package, filePath);
            var workbookMap = ReadSheets(workbookPart, package, filePath);
            var sharedStrings = ReadSharedStrings(workbookPart, package, filePath);
            var reader = new OpenXmlWorkbookReader(
                filePath,
                package,
                document,
                workbookMap.Sheets,
                workbookMap.Parts,
                Array.AsReadOnly(sharedStrings.ToArray()));
            package = null;
            document = null;
            return reader;
        }
        catch (ImportFormatException)
        {
            throw;
        }
        catch (Exception ex) when (IsPackageFailure(ex))
        {
            throw new ImportFormatException(filePath, $"XLSX 包读取失败（{ex.Message}）");
        }
        finally
        {
            DisposeOpenResources(document, package);
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
        var worksheetPart = _worksheetParts[sheet];
        using var stream = worksheetPart.GetStream(FileMode.Open, FileAccess.Read);
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
        var worksheetPart = _worksheetParts[sheet];
        using (var stream = worksheetPart.GetStream(FileMode.Open, FileAccess.Read))
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

        var separator = sheet.EntryPath.LastIndexOf('/');
        var directory = separator < 0 ? string.Empty : sheet.EntryPath[..(separator + 1)];
        var filename = separator < 0 ? sheet.EntryPath : sheet.EntryPath[(separator + 1)..];
        var relationshipEntry = $"{directory}_rels/{filename}.rels";
        var relationships = SnapshotRelationships(
            worksheetPart,
            _filePath,
            relationshipEntry,
            token,
            MaximumHyperlinkDeclarations,
            allowExternalHyperlinks: true);
        foreach (var relationship in relationships.Values)
        {
            token.ThrowIfCancellationRequested();
            if (relationship.Type == HyperlinkRelationship && !relationship.IsExternal)
            {
                var target = ResolveInternalTargetUri(worksheetPart, relationship.Target)
                    .OriginalString
                    .TrimStart('/');
                if (!_package.EntryPaths.Contains(target))
                {
                    throw Error(
                        _filePath,
                        relationshipEntry,
                        $"找不到 XLSX 条目 {target}");
                }
            }
        }

        if (hyperlinkRelationshipIds.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        if (!_package.EntryPaths.Contains(relationshipEntry))
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

            if (relationship.IsExternal)
            {
                var target = relationship.Target.OriginalString;
                if (IsSafeExternalHyperlinkTarget(worksheetPart, target))
                {
                    result.Add(reference, target);
                }

                continue;
            }

            var resolved = ResolveInternalTargetUri(worksheetPart, relationship.Target)
                .OriginalString
                .TrimStart('/');
            if (!_package.EntryPaths.Contains(resolved))
            {
                throw Error(_filePath, relationshipEntry, $"找不到 XLSX 条目 {resolved}");
            }

            result.Add(reference, resolved);
        }

        return result;
    }

    private static void ValidateWorkbookEntryContentType(
        XlsxPackageHandle package,
        string filePath)
    {
        RequireContentType(package, WorkbookEntry, WorkbookContentType, filePath);
    }

    private static void ValidateWorkbookRelationships(
        WorkbookPart workbookPart,
        XlsxPackageHandle package,
        string filePath)
    {
        try
        {
            ValidateContainerRelationships(
                workbookPart,
                filePath,
                "xl/_rels/workbook.xml.rels",
                allowExternalHyperlinks: false);
        }
        catch (ImportFormatException error) when (
            error.Message.Contains("unexpected content type", StringComparison.OrdinalIgnoreCase)
            || error.Message.Contains("doesn't exist in the package", StringComparison.Ordinal)
            || error.Message.Contains("Specified part does not exist", StringComparison.Ordinal))
        {
            ValidateConventionalReferencedPartContentTypes(package, filePath);
            throw;
        }
    }

    private static void ValidateConventionalReferencedPartContentTypes(
        XlsxPackageHandle package,
        string filePath)
    {
        foreach (var entryPath in package.EntryPaths)
        {
            if (entryPath.StartsWith("xl/worksheets/", StringComparison.Ordinal)
                && entryPath.EndsWith(".xml", StringComparison.Ordinal))
            {
                RequireContentType(package, entryPath, WorksheetContentType, filePath);
            }
        }

        const string conventionalSharedStringsEntry = "xl/sharedStrings.xml";
        if (package.EntryPaths.Contains(conventionalSharedStringsEntry))
        {
            RequireContentType(
                package,
                conventionalSharedStringsEntry,
                SharedStringsContentType,
                filePath);
        }
    }

    private static WorkbookPart RequireWorkbookPart(
        SpreadsheetDocument document,
        XlsxPackageHandle package,
        string filePath)
    {
        var workbookParts = document.Parts
            .Select(pair => pair.OpenXmlPart)
            .OfType<WorkbookPart>()
            .ToArray();
        if (workbookParts.Length != 1)
        {
            throw Error(
                filePath,
                "_rels/.rels",
                $"工作簿部件数量必须为 1，实际为 {workbookParts.Length}");
        }

        var workbookPart = workbookParts[0];
        if (!string.Equals(workbookPart.Uri.OriginalString, "/xl/workbook.xml", StringComparison.Ordinal))
        {
            throw Error(filePath, "_rels/.rels", $"工作簿关系必须指向 {WorkbookEntry}");
        }

        if (!string.Equals(workbookPart.ContentType, WorkbookContentType, StringComparison.Ordinal))
        {
            throw Error(
                filePath,
                "_rels/.rels",
                $"工作簿部件 ContentType 必须为 {WorkbookContentType}");
        }

        RequireContentType(package, WorkbookEntry, WorkbookContentType, filePath);
        return workbookPart;
    }

    private static void ValidateWorkbookPartContentTypes(
        WorkbookPart workbookPart,
        XlsxPackageHandle package,
        string filePath)
    {
        var sharedStringParts = 0;
        foreach (var pair in workbookPart.Parts)
        {
            var expected = pair.OpenXmlPart.RelationshipType switch
            {
                WorksheetRelationship => WorksheetContentType,
                SharedStringsRelationship => SharedStringsContentType,
                _ => null,
            };
            if (expected is null)
            {
                continue;
            }

            if (pair.OpenXmlPart.RelationshipType == SharedStringsRelationship
                && ++sharedStringParts > 1)
            {
                throw Error(
                    filePath,
                    "xl/_rels/workbook.xml.rels",
                    $"关系类型重复：{SharedStringsRelationship}");
            }

            var entryPath = pair.OpenXmlPart.Uri.OriginalString.TrimStart('/');
            if (!string.Equals(pair.OpenXmlPart.ContentType, expected, StringComparison.Ordinal))
            {
                throw Error(
                    filePath,
                    "[Content_Types].xml",
                    $"条目 {entryPath} 的 ContentType 必须为 {expected}，实际为 {pair.OpenXmlPart.ContentType}");
            }

            RequireContentType(package, entryPath, expected, filePath);
        }
    }

    private static WorkbookMap ReadSheets(
        WorkbookPart workbookPart,
        XlsxPackageHandle package,
        string filePath)
    {
        return WithEntryFormatErrors(filePath, WorkbookEntry, () =>
        {
            var sheets = new List<OpenXmlSheet>();
            var parts = new Dictionary<OpenXmlSheet, WorksheetPart>();
            var relationshipIds = new HashSet<string>(StringComparer.Ordinal);
            using var reader = SdkOpenXmlReader.Create(workbookPart);
            var sawWorkbook = false;
            var sawSheets = false;
            var insideSheets = false;
            while (reader.Read())
            {
                if (reader.IsStartElement && reader.Depth == 0)
                {
                    if (reader.ElementType != typeof(Workbook))
                    {
                        throw Error(filePath, WorkbookEntry, "workbook 根元素无效");
                    }

                    sawWorkbook = true;
                    continue;
                }

                if (reader.IsStartElement && reader.ElementType == typeof(Sheets))
                {
                    if (reader.Depth != 1 || sawSheets)
                    {
                        throw Error(filePath, WorkbookEntry, "sheets 元素位置无效");
                    }

                    sawSheets = true;
                    insideSheets = true;
                    continue;
                }

                if (reader.IsEndElement
                    && reader.ElementType == typeof(Sheets)
                    && reader.Depth == 1)
                {
                    insideSheets = false;
                    continue;
                }

                if (!reader.IsStartElement
                    || reader.LocalName != "sheet"
                    || reader.NamespaceUri != SpreadsheetNamespace)
                {
                    continue;
                }

                if (!insideSheets || reader.Depth != 2)
                {
                    throw Error(filePath, WorkbookEntry, "sheet 必须是 sheets 的直接子元素");
                }

                var sheetElement = reader.LoadCurrentElement() as Sheet
                    ?? throw Error(filePath, WorkbookEntry, "sheet 元素无效");
                var sheetMarkup = workbookPart.CreateUnknownElement(sheetElement.OuterXml);
                if (sheetMarkup.Descendants().Any(element =>
                        element.LocalName == "sheet"
                        && element.NamespaceUri == SpreadsheetNamespace))
                {
                    throw Error(
                        filePath,
                        WorkbookEntry,
                        "sheet 不能嵌套，sheet 必须是 sheets 的直接子元素");
                }

                var name = sheetElement.Name?.Value;
                var relationshipId = sheetElement.Id?.Value;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(relationshipId))
                {
                    throw Error(filePath, WorkbookEntry, "工作表缺少名称或关系 ID");
                }

                if (!relationshipIds.Add(relationshipId))
                {
                    throw Error(filePath, WorkbookEntry, $"工作表关系 ID 重复：{relationshipId}");
                }

                OpenXmlPart relatedPart;
                try
                {
                    relatedPart = workbookPart.GetPartById(relationshipId);
                }
                catch (KeyNotFoundException)
                {
                    throw Error(filePath, WorkbookEntry, $"找不到工作表 {name} 的关系 {relationshipId}");
                }
                catch (ArgumentOutOfRangeException)
                {
                    throw Error(filePath, WorkbookEntry, $"找不到工作表 {name} 的关系 {relationshipId}");
                }

                if (relatedPart is not WorksheetPart worksheetPart)
                {
                    throw Error(filePath, WorkbookEntry, $"关系 {relationshipId} 不是工作表关系");
                }

                var entryPath = worksheetPart.Uri.OriginalString.TrimStart('/');
                RequireContentType(package, entryPath, WorksheetContentType, filePath);
                var sheet = new OpenXmlSheet(name, entryPath);
                if (!parts.TryAdd(sheet, worksheetPart))
                {
                    throw Error(filePath, WorkbookEntry, $"工作表声明重复：{name}");
                }

                sheets.Add(sheet);
            }

            if (!sawWorkbook)
            {
                throw Error(filePath, WorkbookEntry, "缺少 workbook 根元素");
            }

            return new WorkbookMap(
                Array.AsReadOnly(sheets.ToArray()),
                parts);
        });
    }

    private static IReadOnlyList<string> ReadSharedStrings(
        WorkbookPart workbookPart,
        XlsxPackageHandle package,
        string filePath)
    {
        var sharedStringParts = workbookPart.Parts
            .Select(pair => pair.OpenXmlPart)
            .OfType<SharedStringTablePart>()
            .ToArray();
        if (sharedStringParts.Length == 0)
        {
            return Array.Empty<string>();
        }

        if (sharedStringParts.Length != 1)
        {
            throw Error(
                filePath,
                "xl/_rels/workbook.xml.rels",
                $"关系类型重复：{SharedStringsRelationship}");
        }

        var sharedStringPart = sharedStringParts[0];
        var entryPath = sharedStringPart.Uri.OriginalString.TrimStart('/');
        RequireContentType(package, entryPath, SharedStringsContentType, filePath);
        return WithEntryFormatErrors(filePath, entryPath, () =>
        {
            var values = new List<string>();
            using var reader = SdkOpenXmlReader.Create(sharedStringPart);
            var sawSharedStrings = false;
            while (reader.Read())
            {
                if (reader.IsStartElement && reader.Depth == 0)
                {
                    if (reader.ElementType != typeof(SharedStringTable))
                    {
                        throw Error(filePath, entryPath, "sst 根元素无效");
                    }

                    sawSharedStrings = true;
                    continue;
                }

                if (!reader.IsStartElement
                    || reader.LocalName != "si"
                    || reader.NamespaceUri != SpreadsheetNamespace)
                {
                    continue;
                }

                if (reader.Depth != 1)
                {
                    throw Error(filePath, entryPath, "si 必须是 sst 的直接子元素");
                }

                var item = reader.LoadCurrentElement() as SharedStringItem
                    ?? throw Error(filePath, entryPath, "si 元素无效");
                if (item.Descendants().Any(element =>
                        element.LocalName == "si"
                        && element.NamespaceUri == SpreadsheetNamespace))
                {
                    throw Error(filePath, entryPath, "si 不能嵌套，si 必须是 sst 的直接子元素");
                }

                values.Add(string.Concat(
                    item.Descendants<DocumentFormat.OpenXml.Spreadsheet.Text>()
                        .Select(text => text.Text ?? string.Empty)));
            }

            if (!sawSharedStrings)
            {
                throw Error(filePath, entryPath, "缺少 sst 根元素");
            }

            return values;
        });
    }

    private static IReadOnlyDictionary<string, SdkRelationship> SnapshotRelationships(
        OpenXmlPartContainer owner,
        string filePath,
        string relationshipEntry,
        CancellationToken token,
        int? maximumRelationships,
        bool allowExternalHyperlinks)
    {
        try
        {
            var result = new Dictionary<string, SdkRelationship>(StringComparer.Ordinal);

            void Add(SdkRelationship relationship, bool isHyperlink)
            {
                token.ThrowIfCancellationRequested();
                if (maximumRelationships is int maximum && result.Count == maximum)
                {
                    throw Error(
                        filePath,
                        relationshipEntry,
                        $"每个工作表最多允许 {maximum} 个 Relationship 声明");
                }

                if (relationship.IsExternal && (!isHyperlink || !allowExternalHyperlinks))
                {
                    throw Error(filePath, relationshipEntry, $"关系 {relationship.Id} 是外部关系");
                }

                ValidateRelationshipPayloadPolicy(
                    owner,
                    filePath,
                    relationshipEntry,
                    relationship.Id,
                    relationship.Type,
                    relationship.Target,
                    relationship.IsExternal,
                    relationship.Part);

                if (!result.TryAdd(relationship.Id, relationship))
                {
                    throw Error(filePath, relationshipEntry, $"关系 ID 重复：{relationship.Id}");
                }
            }

            foreach (var pair in owner.Parts)
            {
                Add(new SdkRelationship(
                    pair.RelationshipId,
                    pair.OpenXmlPart.RelationshipType,
                    pair.OpenXmlPart.Uri,
                    IsExternal: false,
                    Part: pair.OpenXmlPart), isHyperlink: false);
            }

            foreach (var relationship in owner.ExternalRelationships)
            {
                Add(new SdkRelationship(
                    relationship.Id,
                    relationship.RelationshipType,
                    relationship.Uri,
                    IsExternal: true,
                    Part: null), isHyperlink: false);
            }

            foreach (var relationship in owner.HyperlinkRelationships)
            {
                Add(new SdkRelationship(
                    relationship.Id,
                    HyperlinkRelationship,
                    relationship.Uri,
                    relationship.IsExternal,
                    Part: null), isHyperlink: true);
            }

            foreach (var relationship in owner.DataPartReferenceRelationships)
            {
                Add(new SdkRelationship(
                    relationship.Id,
                    relationship.RelationshipType,
                    relationship.Uri,
                    relationship.IsExternal,
                    Part: null), isHyperlink: false);
            }

            return result;
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
            throw Error(filePath, relationshipEntry, $"关系解析失败（{ex.Message}）");
        }
    }

    private static void ValidateRelationshipPayloadPolicy(
        OpenXmlPartContainer owner,
        string filePath,
        string relationshipEntry,
        string id,
        string type,
        Uri target,
        bool isExternal,
        OpenXmlPart? part)
    {
        if (IsForbiddenRelationshipType(type))
        {
            throw Error(
                filePath,
                relationshipEntry,
                $"关系 {id} 声明了禁止的宏或二进制类型：{type}");
        }

        if (isExternal)
        {
            return;
        }

        Uri internalTarget;
        try
        {
            internalTarget = part?.Uri ?? ResolveInternalTargetUri(owner, target);
        }
        catch (ArgumentException)
        {
            throw Error(filePath, relationshipEntry, $"XLSX 关系越界：{target}");
        }

        var entryPath = internalTarget.OriginalString.TrimStart('/');
        if (XlsxPackagePreflight.IsForbiddenPayloadPath(entryPath))
        {
            throw Error(
                filePath,
                relationshipEntry,
                $"关系 {id} 指向禁止的宏或二进制负载：{entryPath}");
        }
    }

    private static Uri ResolveInternalTargetUri(OpenXmlPartContainer owner, Uri target)
    {
        if (target.OriginalString.StartsWith("/", StringComparison.Ordinal))
        {
            return target;
        }

        if (!ResolvesWithinPackageRoot(owner, target))
        {
            throw new ArgumentException("Relationship target escapes the package root.", nameof(target));
        }

        var source = owner is OpenXmlPart ownerPart
            ? ownerPart.Uri
            : new Uri("/", UriKind.Relative);
        return System.IO.Packaging.PackUriHelper.ResolvePartUri(source, target);
    }

    private static bool ResolvesWithinPackageRoot(OpenXmlPartContainer owner, Uri target)
    {
        var source = owner is OpenXmlPart ownerPart
            ? ownerPart.Uri
            : new Uri("/", UriKind.Relative);
        var sentinelSource = new Uri(
            "/__chatarchive_package_root__" + source.OriginalString,
            UriKind.Relative);
        try
        {
            var sentinelTarget = System.IO.Packaging.PackUriHelper.ResolvePartUri(
                sentinelSource,
                target);
            return sentinelTarget.OriginalString.StartsWith(
                "/__chatarchive_package_root__/",
                StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void ValidateContainerRelationships(
        OpenXmlPartContainer owner,
        string filePath,
        string relationshipEntry,
        bool allowExternalHyperlinks)
    {
        _ = SnapshotRelationships(
            owner,
            filePath,
            relationshipEntry,
            CancellationToken.None,
            maximumRelationships: null,
            allowExternalHyperlinks: allowExternalHyperlinks);
    }

    private static void RequireContentType(
        XlsxPackageHandle package,
        string entryPath,
        string expected,
        string filePath)
    {
        var actual = package.GetContentType(entryPath);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw Error(
                filePath,
                "[Content_Types].xml",
                $"条目 {entryPath} 的 ContentType 必须为 {expected}，实际为 {actual ?? "<缺失>"}");
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


    private static bool IsSafeExternalHyperlinkTarget(
        WorksheetPart worksheetPart,
        string target)
    {
        if (string.IsNullOrWhiteSpace(target)
            || target.Length != target.Trim().Length
            || target[0] == '/'
            || target.Contains('\\')
            || target.Contains('?')
            || target.Contains('#')
            || Path.IsPathRooted(target)
            || IsDrivePath(target)
            || Uri.TryCreate(target, UriKind.Absolute, out _))
        {
            return false;
        }

        foreach (var character in target)
        {
            if (char.IsControl(character))
            {
                return false;
            }
        }

        return Uri.TryCreate(target, UriKind.Relative, out var targetUri)
            && ResolvesWithinPackageRoot(worksheetPart, targetUri);
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

    private static bool IsAsciiLetter(char value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsDrivePath(string path) => path.Length >= 2 && IsAsciiLetter(path[0]) && path[1] == ':';

    private static bool IsPackageFailure(Exception exception) => exception is
        IOException
        or UnauthorizedAccessException
        or InvalidDataException
        or InvalidOperationException
        or OpenXmlPackageException
        or XmlException
        or ArgumentException
        or NotSupportedException;

    private static void DisposeOpenResources(
        SpreadsheetDocument? document,
        XlsxPackageHandle? package)
    {
        try
        {
            document?.Dispose();
        }
        finally
        {
            package?.Dispose();
        }
    }

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
        try
        {
            _document.Dispose();
        }
        finally
        {
            _package.Dispose();
        }
    }

    private sealed record WorkbookMap(
        IReadOnlyList<OpenXmlSheet> Sheets,
        IReadOnlyDictionary<OpenXmlSheet, WorksheetPart> Parts);

    private sealed record SdkRelationship(
        string Id,
        string Type,
        Uri Target,
        bool IsExternal,
        OpenXmlPart? Part);
}
