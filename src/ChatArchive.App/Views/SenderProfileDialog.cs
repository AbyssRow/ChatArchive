using ChatArchive.App.Navigation;
using ChatArchive.App.Services;
using ChatArchive.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ChatArchive.App.Views;

internal static class SenderProfileDialog
{
    internal static async Task ShowAsync(
        XamlRoot xamlRoot,
        long senderId,
        IAppShell shell,
        Action onConversationsChanged)
    {
        var contact = new ContactViewModel(
            AppServices.Instance.Senders,
            AppServices.Instance.Contacts,
            AppServices.Instance.AvatarStorage);
        try
        {
            if (!await contact.LoadAsync(senderId))
            {
                shell.ShowError("未找到联系人资料");
                return;
            }
        }
        catch (Exception ex)
        {
            shell.ShowError($"加载联系人失败：{ex.Message}");
            return;
        }

        var panel = new StackPanel { Spacing = 12, MinWidth = 440 };

        var headerGrid = new Grid { ColumnSpacing = 12 };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var pic = new PersonPicture
        {
            Width = 48,
            Height = 48,
            DisplayName = contact.DisplayName,
            Initials = string.IsNullOrWhiteSpace(contact.DisplayName) ? "?" : System.Globalization.StringInfo.GetNextTextElement(contact.DisplayName.Trim()),
        };
        if (!string.IsNullOrEmpty(contact.CustomAvatarPath))
        {
            var resolved = AppServices.Instance.AvatarStorage.ResolveAvatarFullPath(contact.CustomAvatarPath);
            if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved))
            {
                pic.ProfilePicture = new BitmapImage(new Uri(resolved));
            }
        }
        Grid.SetColumn(pic, 0);
        headerGrid.Children.Add(pic);

        var headerTextStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        headerTextStack.Children.Add(new TextBlock { Text = contact.DisplayName, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        headerTextStack.Children.Add(new TextBlock { Text = contact.IdentityLine, FontSize = 12, Opacity = 0.7 });
        Grid.SetColumn(headerTextStack, 1);
        headerGrid.Children.Add(headerTextStack);
        panel.Children.Add(headerGrid);

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = contact.DisplayName,
            CloseButtonText = "关闭",
            Content = panel,
        };

        if (contact.IsBound && contact.BoundContact is not null)
        {
            var boundInfoStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            boundInfoStack.Children.Add(new TextBlock { Text = $"已关联联系人: {contact.BoundContact.DisplayName}", FontSize = 12, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center });

            var unbindBtn = new Button { Content = "解除关联", FontSize = 11 };
            unbindBtn.Click += async (_, _) =>
            {
                try
                {
                    await contact.QuickUnbindContactAsync();
                    onConversationsChanged();
                    dialog.Hide();
                }
                catch (Exception ex)
                {
                    shell.ShowError($"解除关联失败: {ex.Message}");
                }
            };
            boundInfoStack.Children.Add(unbindBtn);
            panel.Children.Add(boundInfoStack);
        }
        else
        {
            var bindRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var createContactBtn = new Button { Content = "新建并绑定联系人", FontSize = 12 };

            var inlineCreatePanel = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
            var nameBox = new TextBox { Header = "联系人姓名", Text = contact.OriginalName };
            var labelBox = new TextBox { Header = "身份标签(可选)", PlaceholderText = "如: 工作号" };
            var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var confirmBtn = new Button { Content = "确认创建", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            var cancelBtn = new Button { Content = "取消" };
            actionRow.Children.Add(confirmBtn);
            actionRow.Children.Add(cancelBtn);
            inlineCreatePanel.Children.Add(nameBox);
            inlineCreatePanel.Children.Add(labelBox);
            inlineCreatePanel.Children.Add(actionRow);

            createContactBtn.Click += (_, _) =>
            {
                bindRow.Visibility = Visibility.Collapsed;
                inlineCreatePanel.Visibility = Visibility.Visible;
            };

            cancelBtn.Click += (_, _) =>
            {
                inlineCreatePanel.Visibility = Visibility.Collapsed;
                bindRow.Visibility = Visibility.Visible;
            };

            confirmBtn.Click += async (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(nameBox.Text))
                {
                    try
                    {
                        await contact.QuickCreateAndBindContactAsync(nameBox.Text.Trim(), labelBox.Text?.Trim());
                        onConversationsChanged();
                        dialog.Hide();
                    }
                    catch (Exception ex)
                    {
                        shell.ShowError($"创建联系人失败: {ex.Message}");
                    }
                }
            };

            bindRow.Children.Add(createContactBtn);
            panel.Children.Add(bindRow);
            panel.Children.Add(inlineCreatePanel);
        }

        panel.Children.Add(new TextBlock { Text = "名称记录", FontSize = 12, Opacity = 0.6, Margin = new Thickness(0, 4, 0, 0) });
        var aliasList = new ListView { MaxHeight = 120, SelectionMode = ListViewSelectionMode.None };
        aliasList.Items.Add(contact.IdentityLine.Length == 0 ? "-" : contact.DisplayName);
        foreach (var alias in contact.Aliases.Take(30))
        {
            var seen = alias.LastSeenAt is long ts
                ? DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(ts, 0, 253402300799000L)).LocalDateTime.ToString("yyyy-MM-dd")
                : string.Empty;
            aliasList.Items.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = alias.Alias, FontSize = 13 },
                    new TextBlock { Text = seen, FontSize = 11, Opacity = 0.5 },
                },
            });
        }

        panel.Children.Add(aliasList);
        panel.Children.Add(new TextBlock { Text = "出现过的会话（点击跳转）", FontSize = 12, Opacity = 0.6, Margin = new Thickness(0, 4, 0, 0) });
        var conversationList = new ListView { MaxHeight = 160, IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.None };
        foreach (var conversation in contact.Conversations)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = conversation.Title, FontSize = 13 },
                    new TextBlock { Text = $"{conversation.MessageCount:N0} 条", FontSize = 11, Opacity = 0.5 },
                },
            };
            var wrap = new ListViewItem
            {
                Content = row,
                Tag = conversation.ConversationId,
            };
            conversationList.Items.Add(wrap);
        }
        conversationList.ItemClick += (_, args) =>
        {
            if (args.ClickedItem is ListViewItem { Tag: long conversationId })
            {
                contact.ActivateConversation(conversationId);
            }
        };

        panel.Children.Add(conversationList);
        contact.ConversationActivated += id =>
        {
            dialog.Hide();
            shell.OpenConversation(id);
        };
        await dialog.ShowSafeAsync();
    }
}
