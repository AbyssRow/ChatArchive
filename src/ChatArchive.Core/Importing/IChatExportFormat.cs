using System.Text.Json;

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
    bool Matches(string filePath);

    /// <summary>打开文件：解析文档、读取会话信息，并提供消息枚举。</summary>
    ExportFile Open(string filePath);
}

/// <summary>单个已打开的导出文件。</summary>
public sealed class ExportFile : IDisposable
{
    private readonly JsonDocument _document;
    private readonly Func<string?, IEnumerable<ParsedMessage>> _messagesFactory;

    public ExportFile(
        JsonDocument document,
        ParsedConversation conversation,
        Func<string?, IEnumerable<ParsedMessage>> messagesFactory)
    {
        _document = document;
        Conversation = conversation;
        _messagesFactory = messagesFactory;
    }

    public ParsedConversation Conversation { get; }

    /// <summary>枚举消息；WeFlow 等格式需要会话级提示（如 selfSender）。</summary>
    public IEnumerable<ParsedMessage> EnumerateMessages(string? hint = null)
    {
        return _messagesFactory(hint);
    }

    public void Dispose()
    {
        _document.Dispose();
    }
}
