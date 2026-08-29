namespace ChatArchive.Core.Importing;

/// <summary>当前 WeFlow Excel 导出格式适配器。</summary>
public sealed class WeFlowExcelExportFormat : IChatExportFormat
{
    public string Platform => "wechat";

    public bool Matches(string filePath) => WeFlowExcelParser.Matches(filePath);

    public ExportFile Open(string filePath, CancellationToken cancellationToken = default)
    {
        var conversation = WeFlowExcelParser.ReadConversation(filePath, cancellationToken);
        return new ExportFile(
            conversation,
            token => WeFlowExcelParser.IterateMessages(filePath, conversation, token));
    }
}

/// <summary>当前 CipherTalk Excel 导出格式适配器。</summary>
public sealed class CipherTalkExcelExportFormat : IChatExportFormat
{
    public string Platform => "wechat";

    public bool Matches(string filePath) => CipherTalkExcelParser.Matches(filePath);

    public ExportFile Open(string filePath, CancellationToken cancellationToken = default)
    {
        var conversation = CipherTalkExcelParser.ReadConversation(filePath, cancellationToken);
        return new ExportFile(
            conversation,
            token => CipherTalkExcelParser.IterateMessages(filePath, conversation, token));
    }
}

/// <summary>当前 QQ Chat Exporter Excel 导出格式适配器。</summary>
public sealed class QqExcelExportFormat : IChatExportFormat
{
    public string Platform => "qq";

    public bool Matches(string filePath) => QqExcelParser.Matches(filePath);

    public ExportFile Open(string filePath, CancellationToken cancellationToken = default)
    {
        var conversation = QqExcelParser.ReadConversation(filePath, cancellationToken);
        return new ExportFile(
            conversation,
            token => QqExcelParser.IterateMessages(filePath, conversation, token));
    }
}
