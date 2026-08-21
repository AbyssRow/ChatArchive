using ChatArchive.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ChatArchive.App.Views;

/// <summary>按条目类型选择气泡模板。</summary>
public sealed class TimelineTemplateSelector : DataTemplateSelector
{
    public DataTemplate Separator { get; set; } = null!;
    public DataTemplate Incoming { get; set; } = null!;
    public DataTemplate Outgoing { get; set; } = null!;
    public DataTemplate System { get; set; } = null!;

    protected override DataTemplate SelectTemplateCore(object item)
    {
        return Select(item);
    }

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
    {
        return Select(item);
    }

    private DataTemplate Select(object item)
    {
        return item switch
        {
            DateSeparatorEntry => Separator,
            MessageEntry m when m.Message.IsSystem || m.Message.Direction == "system" => System,
            MessageEntry m when m.Message.Direction == "outgoing" => Outgoing,
            _ => Incoming,
        };
    }
}
