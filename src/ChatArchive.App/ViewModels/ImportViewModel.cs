using System.Collections.ObjectModel;
using ChatArchive.Core.Importing;
using ChatArchive.Core.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;

namespace ChatArchive.App.ViewModels;

public partial class ImportViewModel : ObservableObject
{
    internal static IReadOnlyList<string> PickerExtensions { get; } = ImportDiscovery.SupportedExtensions
        .OrderBy(extension => extension, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    internal const string InitialStatusText =
        "选择包含聊天记录导出文件（JSON/JSONL/CSV/MD/TXT/SQL/XLSX）或文件夹，支持多选后一次导入。";

    internal const string HelpText =
        "支持导入以下软件与格式的导出文件或文件夹（自动递归嗅探与深度解析）：\n" +
        "• 微信 WeFlow：Standard/ArkMe/ChatLab JSON、ChatLab JSONL、WeClone CSV、Markdown、TXT、PostgreSQL SQL、Excel\n" +
        "• 微信 CipherTalk：Detailed/ChatLab JSON、ChatLab JSONL、PostgreSQL SQL、Excel\n" +
        "• QQ Chat Exporter：单文件 JSON、分块 JSONL manifest、TXT、Excel\n" +
        "• 支持多次重叠导入，应用会按内容哈希与消息原生 ID 自动去重。";

    private readonly ArchiveDatabase _database;
    private readonly DispatcherQueue _dispatcher;
    private readonly ImportRunPresentationState _presentationState = new();
    private readonly ImportRunCancellationState _cancellationState = new();

    public ObservableCollection<string> Paths { get; } = new();

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial bool IsCancellationRequested { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = InitialStatusText;

    [ObservableProperty]
    public partial double ProgressValue { get; set; }

    [ObservableProperty]
    public partial double ProgressMaximum { get; set; } = 1;

    public event Action? ImportFinished;

    public ImportViewModel(ArchiveDatabase database, DispatcherQueue dispatcher)
    {
        _database = database;
        _dispatcher = dispatcher;
    }

    public void AddPath(string path)
    {
        if (!Paths.Contains(path))
        {
            Paths.Add(path);
        }
    }

    [RelayCommand]
    private void RemovePath(string path) => Paths.Remove(path);

    [RelayCommand]
    private void ClearPaths() => Paths.Clear();

    [RelayCommand]
    private void Cancel()
    {
        if (!IsRunning || !_cancellationState.RequestCancellation())
        {
            return;
        }

        IsCancellationRequested = true;
        StatusText = "正在安全取消导入…";
    }

    [RelayCommand]
    private void Start()
    {
        if (IsRunning || Paths.Count == 0)
        {
            return;
        }

        var roots = Paths.ToList();
        var generation = _presentationState.Begin();
        var cancellationToken = _cancellationState.Begin();
        IsRunning = true;
        IsCancellationRequested = false;
        ProgressValue = 0;
        ProgressMaximum = 1;
        StatusText = "正在发现导出文件…";

        Task.Run(async () =>
        {
            string terminalText;
            try
            {
                var mediaDir = Path.Combine(Path.GetDirectoryName(_database.DatabasePath)!, "media");
                var service = new ImportService(_database, mediaDir);
                var progress = new Progress<ImportProgress>(p => _dispatcher.TryEnqueue(() =>
                {
                    if (!_presentationState.CanApplyProgress(generation)
                        || IsCancellationRequested)
                    {
                        return;
                    }

                    ProgressMaximum = Math.Max(1, p.FilesTotal);
                    ProgressValue = p.FilesDone;
                    StatusText = p.Phase switch
                    {
                        ImportPhase.Importing =>
                            $"[{p.FilesDone}/{p.FilesTotal}] {Path.GetFileName(p.CurrentFile)} — 新增 {p.Added}，重复 {p.Duplicates}，缺失媒体 {p.MissingMedia}",
                        ImportPhase.Done => $"导入完成：新增 {p.Added} 条消息",
                        ImportPhase.Failed => "导入失败",
                        _ => p.CurrentFile,
                    };
                }));

                var result = await service.RunAsync(
                    roots,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                terminalText =
                    $"完成：文件 导入{result.FilesImported}/跳过{result.FilesSkipped}/失败{result.FilesFailed}；" +
                    $"消息 新增{result.Added} 重复{result.Duplicates} 版本{result.Revised} 变体{result.Variants}；" +
                    $"附件 {result.Attachments}（缺媒体 {result.MissingMedia}）";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                terminalText = "导入已取消；已完成的文件会保留，稍后重新导入时会自动跳过重复内容。";
            }
            catch (Exception ex)
            {
                terminalText = $"导入失败：{ex.Message}";
            }

            if (!_presentationState.TryTerminate(generation))
            {
                _cancellationState.End();
                return;
            }

            if (!_dispatcher.TryEnqueue(() =>
            {
                if (!_presentationState.IsCurrentTerminal(generation))
                {
                    return;
                }

                _cancellationState.End();
                StatusText = terminalText;
                IsCancellationRequested = false;
                IsRunning = false;
                ImportFinished?.Invoke();
            }))
            {
                _cancellationState.End();
            }
        });
    }
}


