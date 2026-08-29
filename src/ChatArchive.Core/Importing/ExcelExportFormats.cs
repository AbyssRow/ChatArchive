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
