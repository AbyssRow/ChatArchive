using System.ComponentModel;
using ChatArchive.App.Navigation;
using ChatArchive.App.Services;
using ChatArchive.App.ViewModels;
using ChatArchive.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;

namespace ChatArchive.App.Views;

public sealed partial class ContactsPage : Page, IShellPage
{
    private IAppShell? _shell;
    private ContactsViewModel? _contacts;
    private bool _attached;
    private CancellationTokenSource? _contactsQueryDebounce;
    private readonly LatestRequestGate _contactSelectionGate = new();
    private bool _isAddingBoundAccount;
    private bool _isRefreshingContacts;

    public ContactsPage()
    {
        InitializeComponent();
    }

    void IShellPage.Attach(IAppShell shell)
    {
        _ = shell;
    }

    internal void Attach(IAppShell shell, ContactsViewModel contacts)
    {
        if (_attached)
        {
            return;
        }

        _shell = shell;
        _contacts = contacts;
        ContactsListView.ItemsSource = contacts.Contacts;
        contacts.PropertyChanged += ContactsOnPropertyChanged;
        _attached = true;
    }

    public void OnShown()
    {
        _ = ReloadContactsAsync(_contacts?.SelectedContact?.Id);
    }

    private void ContactsOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_contacts is null)
        {
            return;
        }

        if (e.PropertyName == nameof(ContactsViewModel.IsLoading))
        {
            ContactsProgressBar.Visibility = _contacts.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        }
        else if (e.PropertyName == nameof(ContactsViewModel.ErrorMessage)
                 && _contacts.ErrorMessage.Length > 0)
        {
            _shell!.ShowError(_contacts.ErrorMessage);
        }
    }

    private async Task ReloadContactsAsync(long? preserveContactId = null)
    {
        if (_contacts is null)
        {
            return;
        }

        _isRefreshingContacts = true;
        try
        {
            var targetId = preserveContactId ?? _contacts.SelectedContact?.Id ?? (_contacts.SelectedDetail?.ContactId);
            await _contacts.LoadAsync(preferredSelectedContactId: targetId);
            ContactsListView.SelectedItem = _contacts.SelectedContact;
            UpdateContactDetailView();
        }
        catch (Exception ex)
        {
            _shell!.ShowError($"刷新联系人列表失败: {ex.Message}");
        }
        finally
        {
            _isRefreshingContacts = false;
        }
    }

    private void ContactsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _contactsQueryDebounce?.Cancel();
        _contactsQueryDebounce = new CancellationTokenSource();
        var token = _contactsQueryDebounce.Token;
        _ = Task.Delay(300, token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    _contacts!.SearchKeyword = ContactsSearchBox.Text;
                    _ = ReloadContactsAsync(_contacts.SelectedContact?.Id);
                });
            }
        });
    }

    private async void ContactsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _contactSelectionGate.Next();

        if (_isRefreshingContacts)
        {
            return;
        }

        try
        {
            if (ContactsListView.SelectedItem is ContactInfo contact)
            {
                await _contacts!.SelectContactAsync(contact);
                UpdateContactDetailView();
            }
            else
            {
                await _contacts!.SelectContactAsync(null);
                UpdateContactDetailView();
            }
        }
        catch (Exception ex)
        {
            _shell!.ShowError($"选择联系人失败: {ex.Message}");
        }
    }

    private void UpdateContactDetailView()
    {
        var detail = _contacts!.SelectedDetail;
        if (detail is null)
        {
            NoContactSelectedPrompt.Visibility = Visibility.Visible;
            ContactDetailPane.Visibility = Visibility.Collapsed;
            return;
        }

        NoContactSelectedPrompt.Visibility = Visibility.Collapsed;
        ContactDetailPane.Visibility = Visibility.Visible;

        DetailDisplayNameBox.Text = detail.DisplayName;
        DetailNoteBox.Text = detail.Note ?? string.Empty;
        DetailTotalMessagesText.Text = $"总消息数: {detail.TotalMessageCount:N0} 条";

        DetailAvatarPicture.DisplayName = detail.DisplayName;
        DetailAvatarPicture.Initials = string.IsNullOrWhiteSpace(detail.DisplayName) ? "?" : System.Globalization.StringInfo.GetNextTextElement(detail.DisplayName.Trim());
        if (!string.IsNullOrEmpty(detail.CustomAvatarPath))
        {
            var resolved = AppServices.Instance.AvatarStorage.ResolveAvatarFullPath(detail.CustomAvatarPath);
            if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved))
            {
                DetailAvatarPicture.ProfilePicture = new BitmapImage(new Uri(resolved));
            }
            else
            {
                DetailAvatarPicture.ProfilePicture = null;
            }
        }
        else
        {
            DetailAvatarPicture.ProfilePicture = null;
        }

        BoundSendersListView.ItemsSource = detail.BoundSenders;
        ContactConversationsListView.ItemsSource = detail.Conversations;
    }

    private async void OnNewContactClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var nameBox = new TextBox { Header = "姓名", PlaceholderText = "输入联系人姓名" };
            var noteBox = new TextBox { Header = "备注（可选）", PlaceholderText = "输入备注信息", AcceptsReturn = true };
            var panel = new StackPanel { Spacing = 10, Children = { nameBox, noteBox } };

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "新建联系人",
                PrimaryButtonText = "创建",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                Content = panel,
            };

            if (await dialog.ShowSafeAsync() == ContentDialogResult.Primary)
            {
                var name = nameBox.Text?.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    _shell!.ShowError("联系人姓名不能为空");
                    return;
                }

                var newDetail = await _contacts!.CreateNewContactAsync(name, noteBox.Text);
                await ReloadContactsAsync(newDetail.ContactId);
            }
        }
        catch (Exception ex)
        {
            _shell!.ShowError($"创建联系人失败: {ex.Message}");
        }
    }

    private async void OnChangeAvatarClick(object sender, RoutedEventArgs e)
    {
        var detail = _contacts!.SelectedDetail;
        if (detail is null)
        {
            return;
        }

        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                ViewMode = PickerViewMode.Thumbnail,
            };
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".webp");
            picker.FileTypeFilter.Add(".bmp");

            WinRT.Interop.InitializeWithWindow.Initialize(picker, _shell!.WindowHandle);

            var file = await picker.PickSingleFileAsync();
            if (file is not null)
            {
                var currentId = detail.ContactId;
                await detail.SaveAvatarFromFileAsync(file.Path);
                await ReloadContactsAsync(currentId);
            }
        }
        catch (Exception ex)
        {
            _shell!.ShowError($"更换头像失败: {ex.Message}");
        }
    }

    private async void OnSaveContactClick(object sender, RoutedEventArgs e)
    {
        var detail = _contacts!.SelectedDetail;
        if (detail is null)
        {
            return;
        }

        var newName = DetailDisplayNameBox.Text?.Trim();
        if (string.IsNullOrEmpty(newName))
        {
            _shell!.ShowError("姓名不能为空");
            return;
        }

        try
        {
            var currentId = detail.ContactId;
            await detail.SaveBasicInfoAsync(newName, DetailNoteBox.Text);
            await ReloadContactsAsync(currentId);
        }
        catch (Exception ex)
        {
            _shell!.ShowError($"保存失败: {ex.Message}");
        }
    }

    private async void OnDeleteContactClick(object sender, RoutedEventArgs e)
    {
        var detail = _contacts!.SelectedDetail;
        if (detail is null)
        {
            return;
        }

        try
        {
            var confirmDialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "删除联系人",
                Content = $"确定要删除联系人【{detail.DisplayName}】吗？\n已绑定的账号不会被删除，仅解除关联关系。",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };

            if (await confirmDialog.ShowSafeAsync() == ContentDialogResult.Primary)
            {
                await _contacts.DeleteContactAsync(detail.ContactId);
                await ReloadContactsAsync(null);
            }
        }
        catch (Exception ex)
        {
            _shell!.ShowError($"删除失败: {ex.Message}");
        }
    }

    private async void OnAccountLabelLostFocus(object sender, RoutedEventArgs e)
    {
        try
        {
            var detail = _contacts!.SelectedDetail;
            if (sender is TextBox tb && tb.DataContext is BoundSenderInfo info && detail is not null)
            {
                var newLabel = tb.Text?.Trim();
                if (newLabel != info.AccountLabel)
                {
                    await detail.UpdateAccountLabelAsync(info.SenderId, string.IsNullOrWhiteSpace(newLabel) ? null : newLabel);
                    if (_contacts.SelectedDetail?.ContactId == detail.ContactId)
                    {
                        UpdateContactDetailView();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _shell!.ShowError($"更新身份标签失败: {ex.Message}");
        }
    }

    private async void OnSetPrimarySenderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var detail = _contacts!.SelectedDetail;
            if (sender is Button { Tag: long senderId } && detail is not null)
            {
                await detail.SetPrimarySenderAsync(senderId);
                UpdateContactDetailView();
            }
        }
        catch (Exception ex)
        {
            _shell!.ShowError($"设置主账号失败: {ex.Message}");
        }
    }

    private async void OnUnbindSenderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var detail = _contacts!.SelectedDetail;
            if (sender is Button { Tag: long senderId } && detail is not null)
            {
                var currentId = detail.ContactId;
                await detail.UnbindSenderAsync(senderId);
                await ReloadContactsAsync(currentId);
            }
        }
        catch (Exception ex)
        {
            _shell!.ShowError($"解绑失败: {ex.Message}");
        }
    }

    private async void OnAddBoundAccountClick(object sender, RoutedEventArgs e)
    {
        var detail = _contacts!.SelectedDetail;
        if (detail is null)
        {
            return;
        }

        if (_isAddingBoundAccount)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(detail.IdentityToken))
        {
            _shell!.ShowError("无法确认目标联系人身份，请刷新联系人后重试");
            return;
        }

        var target = new ContactTargetSnapshot(
            detail.ContactId,
            detail.IdentityToken,
            detail.DisplayName);
        var targetSelectionVersion = _contactSelectionGate.Next();
        bool EnsureTargetIsCurrent()
        {
            if (_contactSelectionGate.IsCurrent(targetSelectionVersion)
                && target.IsCurrent(
                    _contacts.SelectedContact?.Id,
                    _contacts.SelectedContact?.IdentityToken,
                    _contacts.SelectedDetail?.ContactId,
                    _contacts.SelectedDetail?.IdentityToken))
            {
                return true;
            }

            _shell!.ShowError("当前联系人已更改，请在目标联系人上重新打开“绑定账号”");
            return false;
        }

        _isAddingBoundAccount = true;
        AddBoundAccountButton.IsEnabled = false;

        try
        {
            var searchBox = new TextBox { PlaceholderText = "搜索未绑定发送者 (姓名/平台ID/QQ号)..." };
            var list = new ListView { MaxHeight = 220, SelectionMode = ListViewSelectionMode.Single };
            var labelBox = new TextBox { Header = "身份标签（可选，如：工作号、大号）", PlaceholderText = "输入身份标签" };
            var primaryCheck = new CheckBox { Content = "设为主账号", IsChecked = detail.BoundSenders.Count == 0 };

            var availableSenders = new List<BoundSenderInfo>();
            var isSenderPickerOpen = true;
            async Task<bool> RefreshAvailable(string? kw, CancellationToken cancellationToken = default)
            {
                try
                {
                    if (cancellationToken.IsCancellationRequested || !isSenderPickerOpen)
                    {
                        return false;
                    }

                    var items = await detail.LoadAvailableSendersAsync(kw);
                    if (cancellationToken.IsCancellationRequested || !isSenderPickerOpen)
                    {
                        return false;
                    }

                    availableSenders.Clear();
                    availableSenders.AddRange(items);
                    list.Items.Clear();
                    foreach (var item in availableSenders)
                    {
                        var plat = item.Platform == "qq" ? "QQ" : "微信";
                        var idStr = item.Platform == "qq" ? (item.QQNumber ?? item.NativeId) : item.NativeId;
                        var status = !string.IsNullOrEmpty(item.BoundContactName)
                            ? $" [当前归属: {item.BoundContactName} (合并转移)]"
                            : " [未绑定]";
                        list.Items.Add(new ListViewItem
                        {
                            Content = $"{plat}: {item.OriginalName} ({idStr}) - {item.MessageCount:N0}条{status}",
                            Tag = item,
                        });
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    if (!cancellationToken.IsCancellationRequested && isSenderPickerOpen)
                    {
                        _shell!.ShowError($"加载发送者失败: {ex.Message}");
                    }

                    return false;
                }
            }

            if (!await RefreshAvailable(null) || !EnsureTargetIsCurrent())
            {
                return;
            }

            CancellationTokenSource? searchCts = null;
            searchBox.TextChanged += (_, _) =>
            {
                searchCts?.Cancel();
                searchCts = new CancellationTokenSource();
                var token = searchCts.Token;
                var query = searchBox.Text;
                _ = Task.Delay(250, token).ContinueWith(_ =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        DispatcherQueue.TryEnqueue(async () =>
                        {
                            if (!token.IsCancellationRequested && isSenderPickerOpen)
                            {
                                await RefreshAvailable(query, token);
                            }
                        });
                    }
                });
            };

            var panel = new StackPanel
            {
                Spacing = 10,
                MinWidth = 460,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"目标联系人：{target.DisplayName}",
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.8,
                    },
                    searchBox,
                    list,
                    labelBox,
                    primaryCheck,
                },
            };

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"绑定/合并账号到“{target.DisplayName}”",
                PrimaryButtonText = "确认绑定",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = false,
                Content = panel,
            };
            list.SelectionChanged += (_, _) =>
            {
                dialog.IsPrimaryButtonEnabled = list.SelectedItem is ListViewItem
                {
                    Tag: BoundSenderInfo,
                };
            };

            BoundSenderInfo selectedSender;
            string? selectedLabel;
            bool selectedPrimary;
            try
            {
                if (await dialog.ShowSafeAsync() != ContentDialogResult.Primary)
                {
                    return;
                }

                if (list.SelectedItem is not ListViewItem { Tag: BoundSenderInfo item })
                {
                    _shell!.ShowError("未选择要绑定的账号");
                    return;
                }

                selectedSender = item;
                selectedLabel = string.IsNullOrWhiteSpace(labelBox.Text) ? null : labelBox.Text.Trim();
                selectedPrimary = primaryCheck.IsChecked == true;
            }
            finally
            {
                isSenderPickerOpen = false;
                searchCts?.Cancel();
                searchCts?.Dispose();
            }

            if (!EnsureTargetIsCurrent())
            {
                return;
            }

            long? expectedSourceContactId = null;
            string? expectedSourceIdentityToken = null;
            var hasBoundContactName = !string.IsNullOrWhiteSpace(selectedSender.BoundContactName);
            var hasBoundContactIdentityToken =
                !string.IsNullOrWhiteSpace(selectedSender.BoundContactIdentityToken);
            if (selectedSender.BoundContactId.HasValue
                || hasBoundContactName
                || hasBoundContactIdentityToken)
            {
                if (!selectedSender.BoundContactId.HasValue
                    || !hasBoundContactName
                    || !hasBoundContactIdentityToken)
                {
                    _shell!.ShowError("账号归属信息已发生变化，请重新选择账号后重试");
                    return;
                }

                expectedSourceContactId = selectedSender.BoundContactId.Value;
                expectedSourceIdentityToken = selectedSender.BoundContactIdentityToken;
                var oldContactName = selectedSender.BoundContactName!.Trim();
                var confirm = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "确认转移账号",
                    PrimaryButtonText = "确认转移",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                    Content = $"账号“{selectedSender.OriginalName}”当前属于“{oldContactName}”。\n"
                              + $"确认将其转移到“{target.DisplayName}”吗？\n\n"
                              + "旧联系人如果没有其他账号、备注或自定义头像，可能会被自动清理。",
                };

                if (await confirm.ShowSafeAsync() != ContentDialogResult.Primary)
                {
                    return;
                }
            }

            if (!EnsureTargetIsCurrent())
            {
                return;
            }

            var currentId = target.ContactId;
            if (expectedSourceContactId.HasValue)
            {
                await detail.TransferSenderFromExpectedContactAsync(
                    selectedSender.SenderId,
                    target.IdentityToken,
                    expectedSourceContactId.Value,
                    expectedSourceIdentityToken!,
                    selectedLabel,
                    selectedPrimary);
            }
            else
            {
                await detail.BindUnboundSenderToExpectedContactAsync(
                    selectedSender.SenderId,
                    target.IdentityToken,
                    selectedLabel,
                    selectedPrimary);
            }
            await ReloadContactsAsync(currentId);
        }
        catch (Exception ex)
        {
            _shell!.ShowError($"绑定账号失败: {ex.Message}");
        }
        finally
        {
            _isAddingBoundAccount = false;
            AddBoundAccountButton.IsEnabled = true;
        }
    }

    private void ContactConversation_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SenderConversationInfo conv)
        {
            _shell!.OpenConversation(conv.ConversationId);
        }
    }
}
