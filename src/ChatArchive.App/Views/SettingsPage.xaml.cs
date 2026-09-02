using System.Diagnostics;
using ChatArchive.App.Navigation;
using ChatArchive.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace ChatArchive.App.Views;

public sealed partial class SettingsPage : Page, IShellPage
{
    private IAppShell? _shell;
    private bool _attached;

    public SettingsPage()
    {
        InitializeComponent();
    }

    void IShellPage.Attach(IAppShell shell) => Attach(shell);

    internal void Attach(IAppShell shell)
    {
        if (_attached)
        {
            return;
        }

        _shell = shell;
        _attached = true;
    }

    public void OnShown()
    {
        SettingsChangeDirButton.IsEnabled = _shell?.IsPickerReady == true;
        RefreshSettingsView();
    }

    private async void RefreshSettingsView()
    {
        try
        {
            var currentDir = AppServices.Instance.Settings.GetValidDataDirectory();
            SettingsDataPathText.Text = currentDir;
            SettingsTotalSizeText.Text = "计算中…";
            SettingsDbSizeText.Text = "计算中…";
            SettingsMediaSizeText.Text = "计算中…";
            SettingsAvatarSizeText.Text = "计算中…";

            var usage = await Task.Run(() => AppSettings.GetStorageUsage(currentDir));
            SettingsTotalSizeText.Text = usage.FormattedTotalSize;
            SettingsDbSizeText.Text = usage.FormattedDatabaseSize;
            SettingsMediaSizeText.Text = usage.FormattedMediaSize;
            SettingsAvatarSizeText.Text = usage.FormattedAvatarSize;
        }
        catch (Exception ex)
        {
            _shell!.ShowError($"读取设置信息失败: {ex.Message}");
        }
    }

    private void OnSettingsOpenDirClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var currentDir = AppServices.Instance.Settings.GetValidDataDirectory();
            if (Directory.Exists(currentDir))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = currentDir,
                    UseShellExecute = true
                });
            }
            else
            {
                _shell!.ShowError($"目录不存在: {currentDir}");
            }
        }
        catch (Exception ex)
        {
            _shell!.ShowError($"无法打开目录: {ex.Message}");
        }
    }

    private async void OnSettingsChangeDirClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker,
                PickerInterop.RequireHandle(_shell!.WindowHandle));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                return;
            }

            var targetPath = folder.Path;
            var currentDir = AppServices.Instance.Settings.GetValidDataDirectory();
            if (string.Equals(targetPath, currentDir, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "更改数据存储目录",
                PrimaryButtonText = "保存并迁移数据",
                SecondaryButtonText = "仅保存新路径",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock { Text = $"您选择的新存储目录为：\n{targetPath}", TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = "提示：\n• 「保存并迁移数据」会将当前数据库、媒体附件与头像复制到新目录。\n• 「仅保存新路径」仅修改配置文件，在新目录创建全新空白数据库。\n• 保存后请重启应用以完全切换至新存储目录。", FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap }
                    }
                }
            };

            var result = await dialog.ShowSafeAsync();
            if (result == ContentDialogResult.None)
            {
                return;
            }

            if (result == ContentDialogResult.Primary)
            {
                await Task.Run(() => AppSettings.CopyDataDirectory(currentDir, targetPath, overwrite: false));
            }

            var settings = AppServices.Instance.Settings;
            settings.DataDirectory = targetPath;
            settings.Save();

            RefreshSettingsView();

            var successDlg = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "存储目录已更改",
                Content = "数据存储目录已成功更改为新路径！\n请重启 ChatArchive 应用程序以加载新目录的数据。",
                CloseButtonText = "知道了"
            };
            await successDlg.ShowSafeAsync();
        }
        catch (Exception ex)
        {
            _shell!.ShowError(PickerInterop.FormatFailure("更改存储目录", ex));
        }
    }

    private async void OnSettingsResetDirClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var defaultDir = AppSettings.DefaultDataDirectory;
            var currentDir = AppServices.Instance.Settings.GetValidDataDirectory();
            if (string.Equals(defaultDir, currentDir, StringComparison.OrdinalIgnoreCase))
            {
                _shell!.ShowError("当前已经是默认存储目录。");
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "恢复默认存储目录",
                Content = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock { Text = $"确定要将存储目录恢复为默认路径吗？\n{defaultDir}", TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = "保存后需重启应用以完全切换生效。", FontSize = 12, Opacity = 0.75 }
                    }
                },
                PrimaryButtonText = "恢复默认并保存",
                CloseButtonText = "取消"
            };

            var result = await dialog.ShowSafeAsync();
            if (result == ContentDialogResult.Primary)
            {
                var settings = AppServices.Instance.Settings;
                settings.DataDirectory = defaultDir;
                settings.Save();

                RefreshSettingsView();

                var successDlg = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "已恢复默认目录",
                    Content = "已成功恢复为默认数据目录，请重启应用生效。",
                    CloseButtonText = "知道了"
                };
                await successDlg.ShowSafeAsync();
            }
        }
        catch (Exception ex)
        {
            _shell!.ShowError($"恢复默认目录失败: {ex.Message}");
        }
    }
}
