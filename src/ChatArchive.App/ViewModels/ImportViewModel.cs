using System.Collections.ObjectModel;
using ChatArchive.Core.Importing;
using ChatArchive.Core.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;

namespace ChatArchive.App.ViewModels;

public partial class ImportViewModel : ObservableObject
{
    private readonly ArchiveDatabase _database;
    private readonly DispatcherQueue _dispatcher;

    public ObservableCollection<string> Paths { get; } = new();

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "选择包含导出 JSON 的文件夹，可以多选后一次导入。";

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
    private void Start()
    {
        if (IsRunning || Paths.Count == 0)
        {
            return;
        }

        IsRunning = true;
        ProgressValue = 0;
        ProgressMaximum = 1;
        StatusText = "正在发现导出文件…";

        var roots = Paths.ToList();
        var mediaDir = Path.Combine(Path.GetDirectoryName(_database.DatabasePath)!, "media");
        var service = new ImportService(_database, mediaDir);

        Task.Run(async () =>
        {
            var progress = new Progress<ImportProgress>(p => _dispatcher.TryEnqueue(() =>
            {
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

            try
            {
                var result = await service.RunAsync(roots, progress).ConfigureAwait(false);
                _dispatcher.TryEnqueue(() =>
                {
                    StatusText =
                        $"完成：文件 导入{result.FilesImported}/跳过{result.FilesSkipped}/失败{result.FilesFailed}；" +
                        $"消息 新增{result.Added} 重复{result.Duplicates} 版本{result.Revised} 变体{result.Variants}；" +
                        $"附件 {result.Attachments}（缺媒体 {result.MissingMedia}）";
                });
            }
            catch (Exception ex)
            {
                _dispatcher.TryEnqueue(() => StatusText = $"导入失败：{ex.Message}");
            }
            finally
            {
                _dispatcher.TryEnqueue(() =>
                {
                    IsRunning = false;
                    ImportFinished?.Invoke();
                });
            }
        });
    }
}


