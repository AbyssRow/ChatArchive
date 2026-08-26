using ChatArchive.App.Services;
using ChatArchive.App.ViewModels;
using ChatArchive.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ChatArchive.App.Views;

public sealed class PathToImageSourceConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, string language)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            if (value is Uri uri)
            {
                return new BitmapImage(uri);
            }

            if (value is string path && !string.IsNullOrWhiteSpace(path))
            {
                string? resolved = null;
                if (File.Exists(path))
                {
                    resolved = Path.GetFullPath(path);
                }
                else
                {
                    try
                    {
                        resolved = AppServices.Instance.AvatarStorage.ResolveAvatarFullPath(path);
                    }
                    catch
                    {
                        // Fallback or ignore if AppServices is unavailable
                    }

                    if (string.IsNullOrEmpty(resolved) || !File.Exists(resolved))
                    {
                        var appPath = Path.Combine(AppContext.BaseDirectory, path);
                        if (File.Exists(appPath))
                        {
                            resolved = Path.GetFullPath(appPath);
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved))
                {
                    return new BitmapImage(new Uri(resolved));
                }
            }
        }
        catch
        {
            // Suppress any image conversion failure to avoid UI crash
        }

        return null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}

public sealed class MsToDateTimeConverter : IValueConverter
{
    public string Format { get; set; } = "yyyy-MM-dd HH:mm";

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is long ms && ms > 0)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString(Format);
        }

        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}

public sealed class CountTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is long count ? $"{count:N0} 条" : string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}

public sealed class PlatformLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is string p ? p switch
        {
            "qq" => "QQ",
            "wechat" => "微信",
            _ => string.Empty,
        } : string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}

public sealed class KindGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is string k && k == "group" ? "\uE902" : "\uE77B"; // 群组/联系人 字形
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is bool b && !b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is true ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}

public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is null || (value as string) == string.Empty
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}
