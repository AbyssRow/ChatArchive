using System.Globalization;
using System.Text;
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

    public static OpenXmlWorkbookReader Open(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        XlsxPackageHandle? package = null;
        SpreadsheetDocument? document = null;
        try
        {
            package = XlsxPackagePreflight.OpenValidated(filePath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateWorkbookEntryContentType(package, filePath);
            document = SpreadsheetDocument.Open(package.Stream, isEditable: false);
            cancellationToken.ThrowIfCancellationRequested();
            var workbookPart = RequireWorkbookPart(document, package, filePath, cancellationToken);
            ValidateContainerRelationships(
                document,
                package.EntryPaths,
                filePath,
                "_rels/.rels",
                cancellationToken,
                allowExternalHyperlinks: false);
            ValidateWorkbookRelationships(workbookPart, package, filePath, cancellationToken);
            ValidateWorkbookPartContentTypes(workbookPart, package, filePath, cancellationToken);
            var workbookMap = ReadSheets(workbookPart, package, filePath, cancellationToken);
            var sharedStrings = ReadSharedStrings(workbookPart, package, filePath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
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
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (Exception ex) when (IsPackageFailure(ex))
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                token.ThrowIfCancellationRequested();
                throw;
            }
            catch (Exception ex) when (IsPackageFailure(ex))
            {
                token.ThrowIfCancellationRequested();
                throw Error(_filePath, sheet.EntryPath, $"工作表解析失败（{ex.Message}）");
            }

            yield return current;
        }
    }

    private IEnumerable<OpenXmlRow> ReadRowsCore(OpenXmlSheet sheet, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var worksheetPart = _worksheetParts[sheet];
        var hyperlinks = ReadHyperlinks(sheet, worksheetPart, token);
        using var reader = SdkOpenXmlReader.Create(worksheetPart);
        var sawWorksheet = false;
        var sawSheetData = false;
        var insideSheetData = false;
        uint previousRowIndex = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var hasElement = reader.Read();
            token.ThrowIfCancellationRequested();
            if (!hasElement)
            {
                break;
            }

            if (reader.IsStartElement && IsSpreadsheetElement(reader, "worksheet"))
            {
                if (reader.Depth != 0 || sawWorksheet)
                {
                    throw Error(_filePath, sheet.EntryPath, "worksheet 必须是唯一根元素");
                }

                sawWorksheet = true;
                continue;
            }

            if (reader.IsStartElement && reader.Depth == 0)
            {
                throw Error(_filePath, sheet.EntryPath, "worksheet 根元素无效");
            }

            if (reader.IsStartElement && IsSpreadsheetElement(reader, "sheetData"))
            {
                if (reader.Depth != 1 || sawSheetData)
                {
                    throw Error(_filePath, sheet.EntryPath, "sheetData 必须是 worksheet 的唯一直接子元素");
                }

                sawSheetData = true;
                insideSheetData = true;
                continue;
            }

            if (reader.IsEndElement
                && reader.Depth == 1
                && IsSpreadsheetElement(reader, "sheetData"))
            {
                insideSheetData = false;
                continue;
            }

            if (reader.IsStartElement && IsSpreadsheetElement(reader, "c"))
            {
                throw Error(_filePath, sheet.EntryPath, "c 必须是 row 的直接子元素");
            }

            if (!reader.IsStartElement || !IsSpreadsheetElement(reader, "row"))
            {
                continue;
            }

            if (!insideSheetData || reader.Depth != 2)
            {
                throw Error(_filePath, sheet.EntryPath, "row 必须是 sheetData 的直接子元素");
            }

            token.ThrowIfCancellationRequested();
            var rowText = GetAttribute(reader, "r", string.Empty, token);
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
            yield return ReadRow(reader, sheet.EntryPath, rowIndex, hyperlinks, token);
        }

        if (!sawWorksheet)
        {
            throw Error(_filePath, sheet.EntryPath, "缺少 worksheet 根元素");
        }
    }

    private OpenXmlRow ReadRow(
        SdkOpenXmlReader reader,
        string entryPath,
        uint rowIndex,
        IReadOnlyDictionary<string, string> hyperlinks,
        CancellationToken token)
    {
        var rowDepth = reader.Depth;
        var cells = new Dictionary<int, OpenXmlCell>();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var hasElement = reader.Read();
            token.ThrowIfCancellationRequested();
            if (!hasElement)
            {
                break;
            }

            if (reader.IsEndElement
                && reader.Depth == rowDepth
                && IsSpreadsheetElement(reader, "row"))
            {
                return new OpenXmlRow(rowIndex, cells);
            }

            if (!reader.IsStartElement)
            {
                continue;
            }

            if (IsSpreadsheetElement(reader, "row"))
            {
                throw Error(
                    _filePath,
                    entryPath,
                    $"行 {rowIndex} 中 row 不能嵌套，row 必须是 sheetData 的直接子元素");
            }

            if (IsSpreadsheetElement(reader, "worksheet"))
            {
                throw Error(_filePath, entryPath, "worksheet 必须是唯一根元素");
            }

            if (!IsSpreadsheetElement(reader, "c"))
            {
                if (IsSpreadsheetElement(reader, "v")
                    || IsSpreadsheetElement(reader, "f")
                    || IsSpreadsheetElement(reader, "is")
                    || IsSpreadsheetElement(reader, "t"))
                {
                    throw Error(
                        _filePath,
                        entryPath,
                        $"行 {rowIndex} 中 {reader.LocalName} 必须位于 c 元素内");
                }

                continue;
            }

            if (reader.Depth != rowDepth + 1)
            {
                throw Error(
                    _filePath,
                    entryPath,
                    $"行 {rowIndex} 中 c 必须是 row 的直接子元素");
            }

            token.ThrowIfCancellationRequested();
            var element = reader.LoadCurrentElement() as Cell
                ?? throw Error(_filePath, entryPath, $"行 {rowIndex} 中 c 元素无效");
            token.ThrowIfCancellationRequested();
            var cell = ReadCell(element, entryPath, rowIndex, hyperlinks, token);
            if (!cells.TryAdd(cell.ColumnIndex, cell))
            {
                throw CellError(
                    _filePath,
                    entryPath,
                    cell.Reference,
                    $"第 {cell.ColumnIndex} 列重复");
            }
        }

        throw Error(_filePath, entryPath, $"行 {rowIndex} 未正常结束");
    }

    private OpenXmlCell ReadCell(
        Cell cell,
        string entryPath,
        uint rowIndex,
        IReadOnlyDictionary<string, string> hyperlinks,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var reference = cell.GetAttribute("r", string.Empty).Value ?? string.Empty;
        var (columnIndex, referencedRow, normalizedReference) = ParseCellReference(reference, entryPath, token);
        if (referencedRow != rowIndex)
        {
            throw CellError(_filePath, entryPath, reference, $"引用行 {referencedRow} 与所在行 {rowIndex} 不一致");
        }

        var type = cell.GetAttribute("t", string.Empty).Value;
        var hasFormula = false;
        var hasCachedValue = false;
        var cachedValue = string.Empty;
        var inlineText = new StringBuilder();
        foreach (var element in cell.Descendants())
        {
            token.ThrowIfCancellationRequested();
            if (IsSpreadsheetElement(element, "c"))
            {
                throw CellError(_filePath, entryPath, reference, "c 不能嵌套");
            }

            if (IsSpreadsheetElement(element, "worksheet")
                || IsSpreadsheetElement(element, "row")
                || IsSpreadsheetElement(element, "sheetData")
                || IsSpreadsheetElement(element, "hyperlink")
                || IsSpreadsheetElement(element, "hyperlinks"))
            {
                throw CellError(
                    _filePath,
                    entryPath,
                    reference,
                    $"包含位置无效的 {element.LocalName} 元素");
            }

            if (IsSpreadsheetElement(element, "f"))
            {
                if (!ReferenceEquals(element.Parent, cell))
                {
                    throw CellError(_filePath, entryPath, reference, "f 必须是 c 的直接文本子元素");
                }

                _ = ReadValidatedLeafText(element, entryPath, reference, "f", token);
                hasFormula = true;
                continue;
            }

            if (IsSpreadsheetElement(element, "v"))
            {
                if (!ReferenceEquals(element.Parent, cell))
                {
                    throw CellError(_filePath, entryPath, reference, "v 必须是 c 的直接文本子元素");
                }

                if (hasCachedValue)
                {
                    throw CellError(_filePath, entryPath, reference, "包含重复缓存值");
                }

                hasCachedValue = true;
                cachedValue = ReadValidatedLeafText(element, entryPath, reference, "v", token);
                continue;
            }

            if (IsSpreadsheetElement(element, "is")
                && !ReferenceEquals(element.Parent, cell))
            {
                throw CellError(_filePath, entryPath, reference, "is 必须是 c 的直接子元素");
            }

            if (!IsSpreadsheetElement(element, "t"))
            {
                continue;
            }

            if (!IsPermittedInlineTextPath(element, cell))
            {
                throw CellError(
                    _filePath,
                    entryPath,
                    reference,
                    "t 只允许位于 is/t、is/r/t 或 is/rPh/t");
            }

            var text = ReadValidatedLeafText(element, entryPath, reference, "t", token);
            if (type == "inlineStr")
            {
                inlineText.Append(text);
            }
        }

        var value = hasFormula && !hasCachedValue
            ? string.Empty
            : ResolveCellValue(
                type,
                hasCachedValue,
                cachedValue,
                inlineText.ToString(),
                entryPath,
                reference);
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

    private IReadOnlyDictionary<string, string> ReadHyperlinks(
        OpenXmlSheet sheet,
        WorksheetPart worksheetPart,
        CancellationToken token)
    {
        var hyperlinkRelationshipIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hyperlinkDeclarations = 0;
        using (var reader = SdkOpenXmlReader.Create(worksheetPart))
        {
            var sawWorksheet = false;
            var sawHyperlinks = false;
            var insideHyperlinks = false;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var hasElement = reader.Read();
                token.ThrowIfCancellationRequested();
                if (!hasElement)
                {
                    break;
                }

                if (reader.IsStartElement && IsSpreadsheetElement(reader, "worksheet"))
                {
                    if (reader.Depth != 0 || sawWorksheet)
                    {
                        throw Error(_filePath, sheet.EntryPath, "worksheet 必须是唯一根元素");
                    }

                    sawWorksheet = true;
                    continue;
                }

                if (reader.IsStartElement && reader.Depth == 0)
                {
                    throw Error(_filePath, sheet.EntryPath, "worksheet 根元素无效");
                }

                if (reader.IsStartElement
                    && IsSpreadsheetElement(reader, "sheetData")
                    && reader.Depth != 1)
                {
                    throw Error(_filePath, sheet.EntryPath, "sheetData 必须是 worksheet 的直接子元素");
                }

                if (reader.IsStartElement && IsSpreadsheetElement(reader, "hyperlinks"))
                {
                    if (reader.Depth != 1 || sawHyperlinks)
                    {
                        throw Error(_filePath, sheet.EntryPath, "hyperlinks 必须是 worksheet 的唯一直接子元素");
                    }

                    sawHyperlinks = true;
                    insideHyperlinks = true;
                    continue;
                }

                if (reader.IsEndElement
                    && reader.Depth == 1
                    && IsSpreadsheetElement(reader, "hyperlinks"))
                {
                    insideHyperlinks = false;
                    continue;
                }

                if (!reader.IsStartElement || !IsSpreadsheetElement(reader, "hyperlink"))
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
                var reference = GetAttribute(reader, "ref", string.Empty, token) ?? string.Empty;
                var (_, _, normalizedReference) = ParseCellReference(reference, sheet.EntryPath, token);
                var relationshipId = GetAttribute(reader, "id", OfficeRelationshipNamespace, token);
                if (string.IsNullOrWhiteSpace(relationshipId))
                {
                    throw CellError(_filePath, sheet.EntryPath, reference, "超链接缺少关系 ID");
                }

                var hyperlink = reader.LoadCurrentElement() as Hyperlink
                    ?? throw Error(_filePath, sheet.EntryPath, "hyperlink 元素无效");
                token.ThrowIfCancellationRequested();
                if (!string.IsNullOrEmpty(hyperlink.InnerXml))
                {
                    throw CellError(
                        _filePath,
                        sheet.EntryPath,
                        reference,
                        "hyperlink 必须为空，不能包含内容");
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
                if (IsSafeExternalHyperlinkTarget(worksheetPart, target, token))
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
        string filePath,
        CancellationToken cancellationToken)
    {
        try
        {
            ValidateContainerRelationships(
                workbookPart,
                package.EntryPaths,
                filePath,
                "xl/_rels/workbook.xml.rels",
                cancellationToken,
                allowExternalHyperlinks: false);
        }
        catch (ImportFormatException error) when (
            error.Message.Contains("unexpected content type", StringComparison.OrdinalIgnoreCase)
            || error.Message.Contains("doesn't exist in the package", StringComparison.Ordinal)
            || error.Message.Contains("Specified part does not exist", StringComparison.Ordinal))
        {
            ValidateConventionalReferencedPartContentTypes(package, filePath, cancellationToken);
            throw;
        }
    }

    private static void ValidateConventionalReferencedPartContentTypes(
        XlsxPackageHandle package,
        string filePath,
        CancellationToken cancellationToken)
    {
        foreach (var entryPath in package.EntryPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
        string filePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var workbookParts = new List<WorkbookPart>();
        foreach (var pair in document.Parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pair.OpenXmlPart is WorkbookPart candidateWorkbookPart)
            {
                workbookParts.Add(candidateWorkbookPart);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (workbookParts.Count != 1)
        {
            throw Error(
                filePath,
                "_rels/.rels",
                $"工作簿部件数量必须为 1，实际为 {workbookParts.Count}");
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
        string filePath,
        CancellationToken cancellationToken)
    {
        var sharedStringParts = 0;
        foreach (var pair in workbookPart.Parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
        string filePath,
        CancellationToken cancellationToken)
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
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hasElement = reader.Read();
                cancellationToken.ThrowIfCancellationRequested();
                if (!hasElement)
                {
                    break;
                }

                if (reader.IsStartElement && IsSpreadsheetElement(reader, "workbook"))
                {
                    if (reader.Depth != 0 || sawWorkbook)
                    {
                        throw Error(filePath, WorkbookEntry, "workbook 必须是唯一根元素");
                    }

                    sawWorkbook = true;
                    continue;
                }

                if (reader.IsStartElement && reader.Depth == 0)
                {
                    throw Error(filePath, WorkbookEntry, "workbook 根元素无效");
                }

                if (reader.IsStartElement && IsSpreadsheetElement(reader, "sheets"))
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
                    && IsSpreadsheetElement(reader, "sheets")
                    && reader.Depth == 1)
                {
                    insideSheets = false;
                    continue;
                }

                if (reader.IsStartElement && IsSpreadsheetElement(reader, "sheet"))
                {
                    if (!insideSheets || reader.Depth != 2)
                    {
                        throw Error(filePath, WorkbookEntry, "sheet 必须是 sheets 的直接子元素");
                    }

                    var sheetElement = reader.LoadCurrentElement() as Sheet
                        ?? throw Error(filePath, WorkbookEntry, "sheet 元素无效");
                    if (!string.IsNullOrEmpty(sheetElement.InnerXml))
                    {
                        throw Error(
                            filePath,
                            WorkbookEntry,
                            "sheet 必须为空，不能包含子元素或文本内容");
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
                    continue;
                }

                if (!reader.IsStartElement
                    || !typeof(OpenXmlLeafElement).IsAssignableFrom(reader.ElementType))
                {
                    continue;
                }

                var leafLocalName = reader.LocalName;
                var leaf = reader.LoadCurrentElement() as OpenXmlLeafElement
                    ?? throw Error(filePath, WorkbookEntry, $"{leafLocalName} 叶元素无效");
                _ = ReadOrValidateLeafContent(
                    leaf,
                    leafLocalName,
                    allowText: leaf is OpenXmlLeafTextElement,
                    message => Error(filePath, WorkbookEntry, message),
                    cancellationToken);
            }

            if (!sawWorkbook)
            {
                throw Error(filePath, WorkbookEntry, "缺少 workbook 根元素");
            }

            return new WorkbookMap(
                Array.AsReadOnly(sheets.ToArray()),
                parts);
        }, cancellationToken);
    }

    private static IReadOnlyList<string> ReadSharedStrings(
        WorkbookPart workbookPart,
        XlsxPackageHandle package,
        string filePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sharedStringParts = new List<SharedStringTablePart>();
        foreach (var pair in workbookPart.Parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pair.OpenXmlPart is SharedStringTablePart candidateSharedStringPart)
            {
                sharedStringParts.Add(candidateSharedStringPart);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (sharedStringParts.Count == 0)
        {
            return Array.Empty<string>();
        }

        if (sharedStringParts.Count != 1)
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
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hasElement = reader.Read();
                cancellationToken.ThrowIfCancellationRequested();
                if (!hasElement)
                {
                    break;
                }

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
                var position = values.Count + 1;
                var value = new StringBuilder();
                foreach (var element in item.Descendants())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsSpreadsheetElement(element, "si"))
                    {
                        throw SharedStringError(
                            filePath,
                            entryPath,
                            position,
                            "si 不能嵌套，si 必须是 sst 的直接子元素");
                    }

                    if (!IsSpreadsheetElement(element, "t"))
                    {
                        if (element is OpenXmlLeafElement leaf)
                        {
                            _ = ReadOrValidateLeafContent(
                                leaf,
                                element.LocalName,
                                allowText: false,
                                message => SharedStringError(
                                    filePath,
                                    entryPath,
                                    position,
                                    message),
                                cancellationToken);
                        }

                        continue;
                    }

                    if (!IsPermittedSharedStringTextPath(element, item))
                    {
                        throw SharedStringError(
                            filePath,
                            entryPath,
                            position,
                            "t 只允许位于 si/t 或 si/r/t");
                    }

                    value.Append(ReadOrValidateLeafContent(
                        element,
                        "t",
                        allowText: true,
                        message => SharedStringError(
                            filePath,
                            entryPath,
                            position,
                            message),
                        cancellationToken));
                }

                values.Add(value.ToString());
            }

            if (!sawSharedStrings)
            {
                throw Error(filePath, entryPath, "缺少 sst 根元素");
            }

            return values;
        }, cancellationToken);
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

            token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ImportFormatException)
        {
            token.ThrowIfCancellationRequested();
            throw;
        }
        catch (Exception ex) when (IsPackageFailure(ex))
        {
            token.ThrowIfCancellationRequested();
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
        const string SentinelPrefix = "/__chatarchive_package_root__";
        var source = owner is OpenXmlPart ownerPart
            ? ownerPart.Uri
            : new Uri("/", UriKind.Relative);
        var sentinelSource = new Uri(
            SentinelPrefix + source.OriginalString,
            UriKind.Relative);
        try
        {
            var resolvedTarget = System.IO.Packaging.PackUriHelper.ResolvePartUri(
                source,
                target);
            var sentinelTarget = System.IO.Packaging.PackUriHelper.ResolvePartUri(
                sentinelSource,
                target);
            return sentinelTarget.OriginalString.Equals(
                SentinelPrefix + resolvedTarget.OriginalString,
                StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void ValidateContainerRelationships(
        OpenXmlPartContainer owner,
        IReadOnlySet<string> entryPaths,
        string filePath,
        string relationshipEntry,
        CancellationToken cancellationToken,
        bool allowExternalHyperlinks)
    {
        var relationships = SnapshotRelationships(
            owner,
            filePath,
            relationshipEntry,
            cancellationToken,
            maximumRelationships: null,
            allowExternalHyperlinks: allowExternalHyperlinks);
        foreach (var relationship in relationships.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (relationship.IsExternal || relationship.Type != HyperlinkRelationship)
            {
                continue;
            }

            var target = (relationship.Part?.Uri
                    ?? ResolveInternalTargetUri(owner, relationship.Target))
                .OriginalString
                .TrimStart('/');
            if (!entryPaths.Contains(target))
            {
                throw Error(
                    filePath,
                    relationshipEntry,
                    $"关系 {relationship.Id} 指向不存在的 XLSX 条目 {target}");
            }
        }
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
        string target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(target)
            || char.IsWhiteSpace(target[0])
            || char.IsWhiteSpace(target[^1])
            || target[0] == '/')
        {
            return false;
        }

        foreach (var character in target)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (char.IsControl(character) || character is '\\' or '?' or '#')
            {
                return false;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (Path.IsPathRooted(target)
            || IsDrivePath(target)
            || Uri.TryCreate(target, UriKind.Absolute, out _))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }

        var isRelative = Uri.TryCreate(target, UriKind.Relative, out var targetUri);
        cancellationToken.ThrowIfCancellationRequested();
        return isRelative && ResolvesWithinPackageRoot(worksheetPart, targetUri!);
    }

    private (int ColumnIndex, uint RowIndex, string NormalizedReference) ParseCellReference(
        string reference,
        string entryPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(reference))
        {
            throw CellError(_filePath, entryPath, "<缺失>", "引用无效");
        }
        if (reference.Length > 10)
        {
            throw CellError(_filePath, entryPath, reference, "引用无效");
        }

        var position = 0;
        var columnIndex = 0;
        try
        {
            while (position < reference.Length && IsAsciiLetter(reference[position]))
            {
                cancellationToken.ThrowIfCancellationRequested();
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
            cancellationToken.ThrowIfCancellationRequested();
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
        cancellationToken.ThrowIfCancellationRequested();
        return (columnIndex, rowIndex, normalized);
    }

    private string ReadValidatedLeafText(
        OpenXmlElement element,
        string entryPath,
        string reference,
        string localName,
        CancellationToken cancellationToken)
    {
        return ReadValidatedLeafText(
            element,
            localName,
            message => CellError(_filePath, entryPath, reference, message),
            cancellationToken);
    }

    private static string ReadOrValidateLeafContent(
        OpenXmlElement element,
        string localName,
        bool allowText,
        Func<string, ImportFormatException> invalid,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (allowText)
        {
            return ReadValidatedLeafText(element, localName, invalid, cancellationToken);
        }

        if (element is not OpenXmlLeafElement leaf)
        {
            throw invalid($"{localName} 叶元素无效");
        }

        var innerXml = leaf.InnerXml;
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrEmpty(innerXml))
        {
            throw invalid($"{localName} 必须为空，不能包含子元素或文本内容");
        }

        return string.Empty;
    }

    private static string ReadValidatedLeafText(
        OpenXmlElement element,
        string localName,
        Func<string, ImportFormatException> invalid,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (element is not OpenXmlLeafTextElement leaf)
        {
            throw invalid($"{localName} 文本元素无效");
        }

        var normalized = (OpenXmlLeafTextElement)leaf.CloneNode(deep: false);
        normalized.Text = leaf.Text ?? string.Empty;
        cancellationToken.ThrowIfCancellationRequested();
        var leafInnerXml = leaf.InnerXml;
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedInnerXml = normalized.InnerXml;
        cancellationToken.ThrowIfCancellationRequested();
        if (string.Equals(leafInnerXml, normalizedInnerXml, StringComparison.Ordinal))
        {
            return leaf.Text ?? string.Empty;
        }

        return ReadCharacterDataOnlyLeafText(
            leaf,
            localName,
            invalid,
            cancellationToken);
    }

    private static string ReadCharacterDataOnlyLeafText(
        OpenXmlLeafTextElement leaf,
        string localName,
        Func<string, ImportFormatException> invalid,
        CancellationToken cancellationToken)
    {
        try
        {
            // SDK leaf shadow content exposes decoded ampersands; reserve a legal XML
            // character so the SDK fragment parser can still classify every node in order.
            cancellationToken.ThrowIfCancellationRequested();
            var innerXml = leaf.InnerXml;
            cancellationToken.ThrowIfCancellationRequested();
            var sentinel = FindUnusedXmlTextSentinel(innerXml, cancellationToken);
            var fragmentInnerXml = sentinel is null
                ? innerXml
                : innerXml.Replace('&', sentinel.Value);
            cancellationToken.ThrowIfCancellationRequested();
            var fragment = new OpenXmlUnknownElement("fragment")
            {
                InnerXml = fragmentInnerXml,
            };
            cancellationToken.ThrowIfCancellationRequested();
            var text = new StringBuilder();
            foreach (var child in fragment.ChildElements)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (child is not OpenXmlMiscNode node)
                {
                    throw InvalidLeafMarkup(localName, invalid);
                }

                switch (node.XmlNodeType)
                {
                    case System.Xml.XmlNodeType.Text:
                    case System.Xml.XmlNodeType.Whitespace:
                    case System.Xml.XmlNodeType.SignificantWhitespace:
                        text.Append(RestoreXmlTextSentinel(node.OuterXml, sentinel, cancellationToken));
                        break;
                    case System.Xml.XmlNodeType.CDATA:
                        const string CDataStart = "<![CDATA[";
                        const string CDataEnd = "]]>";
                        var outerXml = RestoreXmlTextSentinel(node.OuterXml, sentinel, cancellationToken);
                        if (!outerXml.StartsWith(CDataStart, StringComparison.Ordinal)
                            || !outerXml.EndsWith(CDataEnd, StringComparison.Ordinal))
                        {
                            throw InvalidLeafMarkup(localName, invalid);
                        }

                        text.Append(outerXml.AsSpan(CDataStart.Length, outerXml.Length - CDataStart.Length - CDataEnd.Length));
                        break;
                    default:
                        throw InvalidLeafMarkup(localName, invalid);
                }
            }

            return text.ToString();
        }
        catch (ImportFormatException)
        {
            throw;
        }
        catch (Exception ex) when (IsPackageFailure(ex))
        {
            throw InvalidLeafMarkup(localName, invalid);
        }
    }

    private static char? FindUnusedXmlTextSentinel(
        string innerXml,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const char FirstPrivateUseCharacter = '\uE000';
        const char LastPrivateUseCharacter = '\uF8FF';
        var used = new bool[LastPrivateUseCharacter - FirstPrivateUseCharacter + 1];
        var hasAmpersand = false;
        foreach (var character in innerXml)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hasAmpersand |= character == '&';
            if (character >= FirstPrivateUseCharacter && character <= LastPrivateUseCharacter)
            {
                used[character - FirstPrivateUseCharacter] = true;
            }
        }

        if (!hasAmpersand)
        {
            return null;
        }

        for (var offset = 0; offset < used.Length; offset++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!used[offset])
            {
                return (char)(FirstPrivateUseCharacter + offset);
            }
        }

        throw new InvalidOperationException("Unable to reserve an XML text sentinel.");
    }

    private static string RestoreXmlTextSentinel(
        string value,
        char? sentinel,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var restored = sentinel is null
            ? value
            : value.Replace(sentinel.Value, '&');
        cancellationToken.ThrowIfCancellationRequested();
        return restored;
    }

    private static ImportFormatException InvalidLeafMarkup(
        string localName,
        Func<string, ImportFormatException> invalid)
    {
        return invalid($"{localName} 包含无效的子元素或标记");
    }

    private static bool IsPermittedSharedStringTextPath(
        OpenXmlElement text,
        SharedStringItem item)
    {
        var parent = text.Parent;
        if (parent is null)
        {
            return false;
        }

        if (ReferenceEquals(parent, item))
        {
            return true;
        }

        return IsSpreadsheetElement(parent, "r")
            && ReferenceEquals(parent.Parent, item);
    }

    private static bool IsPermittedInlineTextPath(OpenXmlElement text, Cell cell)
    {
        var parent = text.Parent;
        if (parent is null)
        {
            return false;
        }

        if (IsSpreadsheetElement(parent, "is"))
        {
            return ReferenceEquals(parent.Parent, cell);
        }

        if (!IsSpreadsheetElement(parent, "r")
            && !IsSpreadsheetElement(parent, "rPh"))
        {
            return false;
        }

        var inlineString = parent.Parent;
        return inlineString is not null
            && IsSpreadsheetElement(inlineString, "is")
            && ReferenceEquals(inlineString.Parent, cell);
    }

    private static bool IsSpreadsheetElement(SdkOpenXmlReader reader, string localName) =>
        reader.LocalName == localName && reader.NamespaceUri == SpreadsheetNamespace;

    private static bool IsSpreadsheetElement(OpenXmlElement element, string localName) =>
        element.LocalName == localName && element.NamespaceUri == SpreadsheetNamespace;

    private static string? GetAttribute(
        SdkOpenXmlReader reader,
        string localName,
        string namespaceUri,
        CancellationToken cancellationToken)
    {
        foreach (var attribute in reader.Attributes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attribute.LocalName == localName && attribute.NamespaceUri == namespaceUri)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return attribute.Value;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return null;
    }

    private static T WithEntryFormatErrors<T>(
        string filePath,
        string entryPath,
        Func<T> read,
        CancellationToken cancellationToken)
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
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (Exception ex) when (IsPackageFailure(ex))
        {
            cancellationToken.ThrowIfCancellationRequested();
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
        or System.Xml.XmlException
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

    private static ImportFormatException SharedStringError(
        string filePath,
        string entryPath,
        int position,
        string message)
    {
        return Error(filePath, entryPath, $"共享字符串 {position}：{message}");
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
