using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace ChatArchive.Core.Importing;

/// <summary>
/// 与旧版 Python json.dumps(sort_keys=True, separators=(",", ":"), ensure_ascii=False)
/// 字节级兼容的规范化序列化，保证 payload_hash 跨实现一致。
/// </summary>
public static class CanonicalJson
{
    public static string Serialize(JsonNode? node)
    {
        var builder = new StringBuilder(256);
        WriteNode(builder, node);
        return builder.ToString();
    }

    public static string HashHex(JsonNode? node)
    {
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(node))))
            .ToLowerInvariant();
    }

    private static void WriteNode(StringBuilder builder, JsonNode? node)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                break;
            case JsonObject obj:
                WriteObject(builder, obj);
                break;
            case JsonArray array:
                builder.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    WriteNode(builder, array[i]);
                }

                builder.Append(']');
                break;
            case JsonValue value:
                WriteValue(builder, value);
                break;
        }
    }

    private static void WriteObject(StringBuilder builder, JsonObject obj)
    {
        builder.Append('{');
        var first = true;
        foreach (var property in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            WriteString(builder, property.Key);
            builder.Append(':');
            WriteNode(builder, property.Value);
        }

        builder.Append('}');
    }

    private static void WriteValue(StringBuilder builder, JsonValue value)
    {
        if (value.TryGetValue<bool>(out var b))
        {
            builder.Append(b ? "true" : "false");
            return;
        }

        if (value.TryGetValue<string>(out var s))
        {
            WriteString(builder, s);
            return;
        }

        if (value.GetValue<object>() is decimal decVal)
        {
            builder.Append(decVal.ToString(CultureInfo.InvariantCulture));
            return;
        }

        if (value.TryGetValue<long>(out var l))
        {
            builder.Append(l.ToString(CultureInfo.InvariantCulture));
            return;
        }

        if (value.TryGetValue<int>(out var i))
        {
            builder.Append(i.ToString(CultureInfo.InvariantCulture));
            return;
        }

        if (value.TryGetValue<ulong>(out var ul))
        {
            builder.Append(ul.ToString(CultureInfo.InvariantCulture));
            return;
        }

        if (value.TryGetValue<double>(out var d))
        {
            builder.Append(FormatDouble(d));
            return;
        }

        if (value.TryGetValue<float>(out var f))
        {
            builder.Append(FormatDouble(f));
            return;
        }

        if (value.TryGetValue<decimal>(out var dec))
        {
            builder.Append(dec.ToString(CultureInfo.InvariantCulture));
            return;
        }

        var raw = value.ToJsonString();
        if (raw.IndexOfAny(new[] { '.', 'e', 'E' }) < 0
            && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
        {
            builder.Append(intValue.ToString(CultureInfo.InvariantCulture));
            return;
        }

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
        {
            builder.Append(FormatDouble(doubleValue));
            return;
        }

        builder.Append(raw);
    }

    /// <summary>复刻 Python repr(float) 的关键行为：整数值补 ".0"，指数用小写 e。</summary>
    public static string FormatDouble(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return value.Equals(double.NaN) ? "NaN" : value > 0 ? "Infinity" : "-Infinity";
        }

        if (double.IsNegative(value) && value == 0.0)
        {
            return "-0.0";
        }

        if (value == Math.Floor(value) && Math.Abs(value) < 1e16)
        {
            return ((long)value).ToString(CultureInfo.InvariantCulture) + ".0";
        }

        var text = value.ToString("R", CultureInfo.InvariantCulture);
        text = text.Replace('E', 'e');
        if (text.EndsWith(".0", StringComparison.Ordinal))
        {
            return text;
        }

        return text;
    }

    private static void WriteString(StringBuilder builder, string text)
    {
        builder.Append('"');
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (ch < ' ')
                    {
                        builder.Append("\\u");
                        builder.Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(ch);
                    }

                    break;
            }
        }

        builder.Append('"');
    }
}
