namespace ChatArchive.Core.Importing;

/// <summary>
/// 一个聊天记录导出格式的适配器。支持新的导出工具时，实现本接口并注册到
/// ExportFormats.Default 即可，导入流程其余部分不需要改动。
/// </summary>
public interface IChatExportFormat
{
    /// <summary>平台标识，写入 conversations.platform / messages.platform。</summary>
    string Platform { get; }

    /// <summary>判断一个文件是否属于本格式（轻量嗅探，可读文件头）。</summary>
    bool Matches(string filePath, CancellationToken cancellationToken = default);

    /// <summary>打开文件：解析文档、读取会话信息，并提供消息枚举。</summary>
    ExportFile Open(string filePath, CancellationToken cancellationToken = default);
}

/// <summary>单个已打开的导出文件。</summary>
public sealed class ExportFile : IDisposable
{
    private readonly Func<CancellationToken, IEnumerable<ParsedMessage>> _messagesFactory;

    public ExportFile(
        ParsedConversation conversation,
        Func<CancellationToken, IEnumerable<ParsedMessage>> messagesFactory)
    {
        Conversation = conversation;
        _messagesFactory = messagesFactory;
    }

    public ParsedConversation Conversation { get; }

    /// <summary>以流式方式枚举消息。</summary>
    public IEnumerable<ParsedMessage> EnumerateMessages(CancellationToken cancellationToken = default)
    {
        return _messagesFactory(cancellationToken);
    }

    public void Dispose()
    {
        // ExportFile no longer owns a whole-file JsonDocument. Kept for API compatibility.
    }
}
