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
            ValidateContentTypes(entries, filePath);

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

            var workbookRelationships = ReadRelationships(
                entries,
                WorkbookRelationshipsEntry,
                WorkbookEntry,
                filePath,
                CancellationToken.None);
            var sheets = ReadSheets(entries, workbookRelationships, filePath);
            var sharedStrings = ReadSharedStrings(entries, workbookRelationships, filePath, CancellationToken.None);
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
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element
                && reader.LocalName == "worksheet"
                && reader.NamespaceURI == SpreadsheetNamespace)
            {
                sawWorksheet = true;
            }

            if (reader.NodeType != XmlNodeType.Element
                || reader.LocalName != "row"
                || reader.NamespaceURI != SpreadsheetNamespace)
            {
                continue;
            }

            token.ThrowIfCancellationRequested();
            var rowText = reader.GetAttribute("r");
            if (!uint.TryParse(rowText, NumberStyles.None, CultureInfo.InvariantCulture, out var rowIndex)
                || rowIndex is 0 or > MaximumRowIndex)
            {
                throw Error(_filePath, sheet.EntryPath, $"行号无效：{rowText ?? "<缺失>"}");
            }

            var cells = new Dictionary<int, OpenXmlCell>();
            using (var rowReader = reader.ReadSubtree())
            {
                rowReader.Read();
                while (rowReader.Read())
                {
                    if (rowReader.NodeType != XmlNodeType.Element
                        || rowReader.LocalName != "c"
                        || rowReader.NamespaceURI != SpreadsheetNamespace)
                    {
                        continue;
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
        var entry = RequireEntry(_entries, sheet.EntryPath, _filePath, sheet.EntryPath);
        using (var stream = entry.Open())
        using (var reader = CreateXmlReader(stream))
        {
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element
                    || reader.LocalName != "hyperlink"
                    || reader.NamespaceURI != SpreadsheetNamespace)
                {
                    continue;
                }

                token.ThrowIfCancellationRequested();
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
        }

        if (hyperlinkRelationshipIds.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var relationshipEntry = RelationshipEntryFor(sheet.EntryPath);
        var relationships = ReadRelationships(_entries, relationshipEntry, sheet.EntryPath, _filePath, token);
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
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element
                && reader.LocalName == "workbook"
                && reader.NamespaceURI == SpreadsheetNamespace)
            {
                sawWorkbook = true;
            }

            if (reader.NodeType != XmlNodeType.Element
                || reader.LocalName != "sheet"
                || reader.NamespaceURI != SpreadsheetNamespace)
            {
                continue;
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
            if (reader.NodeType == XmlNodeType.Element
                && reader.LocalName == "sst"
                && reader.NamespaceURI == SpreadsheetNamespace)
            {
                sawSharedStrings = true;
            }

            if (reader.NodeType != XmlNodeType.Element
                || reader.LocalName != "si"
                || reader.NamespaceURI != SpreadsheetNamespace)
            {
                continue;
            }

            token.ThrowIfCancellationRequested();
            var value = new StringBuilder();
            using (var itemReader = reader.ReadSubtree())
            {
                itemReader.Read();
                while (itemReader.Read())
                {
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
        CancellationToken token)
    {
        return WithEntryFormatErrors(
            filePath,
            relationshipEntry,
            () => ReadRelationshipsCore(entries, relationshipEntry, ownerEntry, filePath, token));
    }

    private static IReadOnlyDictionary<string, PackageRelationship> ReadRelationshipsCore(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        string relationshipEntry,
        string ownerEntry,
        string filePath,
        CancellationToken token)
    {
        var entry = RequireEntry(entries, relationshipEntry, filePath, relationshipEntry);
        var relationships = new Dictionary<string, PackageRelationship>(StringComparer.Ordinal);
        using var stream = entry.Open();
        using var reader = CreateXmlReader(stream);
        var sawRelationships = false;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element
                && reader.LocalName == "Relationships"
                && reader.NamespaceURI == PackageRelationshipNamespace)
            {
                sawRelationships = true;
            }

            if (reader.NodeType != XmlNodeType.Element
                || reader.LocalName != "Relationship"
                || reader.NamespaceURI != PackageRelationshipNamespace)
            {
                continue;
            }

            token.ThrowIfCancellationRequested();
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

            if (!relationships.TryAdd(id, new PackageRelationship(id, type, target, isExternal)))
            {
                throw Error(filePath, relationshipEntry, $"关系 ID 重复：{id}");
            }
        }

        if (!sawRelationships)
        {
            throw Error(filePath, relationshipEntry, "缺少 Relationships 根元素");
        }

        _ = ownerEntry;
        return relationships;
    }

    private static void ValidateContentTypes(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        string filePath)
    {
        WithEntryFormatErrors(filePath, ContentTypesEntry, () =>
        {
            var entry = RequireEntry(entries, ContentTypesEntry, filePath, ContentTypesEntry);
            var partNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var workbookDeclarations = 0;
            var sawTypes = false;
            using var stream = entry.Open();
            using var reader = CreateXmlReader(stream);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element
                    && reader.LocalName == "Types"
                    && reader.NamespaceURI == ContentTypesNamespace)
                {
                    sawTypes = true;
                }

                if (reader.NodeType != XmlNodeType.Element
                    || reader.LocalName != "Override"
                    || reader.NamespaceURI != ContentTypesNamespace)
                {
                    continue;
                }

                CancellationToken.None.ThrowIfCancellationRequested();
                var declaredPart = reader.GetAttribute("PartName");
                var contentType = reader.GetAttribute("ContentType");
                if (string.IsNullOrWhiteSpace(declaredPart)
                    || declaredPart[0] != '/'
                    || string.IsNullOrWhiteSpace(contentType))
                {
                    throw Error(filePath, ContentTypesEntry, "Override 缺少有效 PartName 或 ContentType");
                }

                var partName = declaredPart[1..];
                ValidateEntryPath(partName, filePath);
                if (!partNames.Add(partName))
                {
                    throw Error(filePath, ContentTypesEntry, $"PartName 重复或歧义：{declaredPart}");
                }

                if (partName == WorkbookEntry)
                {
                    workbookDeclarations++;
                    if (contentType != WorkbookContentType)
                    {
                        throw Error(filePath, ContentTypesEntry, $"工作簿 ContentType 无效：{contentType}");
                    }
                }
            }

            if (!sawTypes)
            {
                throw Error(filePath, ContentTypesEntry, "缺少 Types 根元素");
            }

            if (workbookDeclarations != 1)
            {
                throw Error(filePath, ContentTypesEntry, $"工作簿 ContentType 声明数量无效：{workbookDeclarations}");
            }

            return true;
        });
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

    private sealed record PackageRelationship(string Id, string Type, string Target, bool IsExternal);
}
