using System.Text;

namespace ChatArchive.Core.Data;

/// <summary>
/// 把 SQL 脚本拆分为单条语句，正确跳过字符串字面量与 CREATE TRIGGER 的 BEGIN...END 体。
/// </summary>
public static class SqlScriptSplitter
{
    public static IReadOnlyList<string> Split(string script)
    {
        var statements = new List<string>();
        var current = new StringBuilder();
        var inTrigger = false;
        var i = 0;

        void Flush()
        {
            var text = current.ToString().Trim();
            if (text.Length > 0)
            {
                statements.Add(text);
            }

            current.Clear();
        }

        while (i < script.Length)
        {
            if (script[i] == '-' && i + 1 < script.Length && script[i + 1] == '-')
            {
                while (i < script.Length)
                {
                    current.Append(script[i]);
                    if (script[i] == '\n')
                    {
                        i++;
                        break;
                    }
                    i++;
                }
                continue;
            }

            if (script[i] == '/' && i + 1 < script.Length && script[i + 1] == '*')
            {
                current.Append("/*");
                i += 2;
                while (i < script.Length)
                {
                    if (script[i] == '*' && i + 1 < script.Length && script[i + 1] == '/')
                    {
                        current.Append("*/");
                        i += 2;
                        break;
                    }
                    current.Append(script[i]);
                    i++;
                }
                continue;
            }

            if (script[i] == '\'')
            {
                current.Append(script[i]);
                i++;
                while (i < script.Length)
                {
                    current.Append(script[i]);
                    if (script[i] == '\'')
                    {
                        if (i + 1 < script.Length && script[i + 1] == '\'')
                        {
                            current.Append('\'');
                            i += 2;
                            continue;
                        }
                        i++;
                        break;
                    }
                    i++;
                }
                continue;
            }

            if (!inTrigger && IsCreateTriggerStatement(current))
            {
                inTrigger = true;
            }

            if (script[i] == ';' && !inTrigger)
            {
                Flush();
                i++;
                continue;
            }

            if (inTrigger && IsWord(script, i, "end"))
            {
                var j = i + 3;
                while (j < script.Length && char.IsWhiteSpace(script[j]))
                {
                    j++;
                }

                if (j < script.Length && script[j] == ';')
                {
                    current.Append(script, i, j - i + 1);
                    Flush();
                    inTrigger = false;
                    i = j + 1;
                    continue;
                }
            }

            current.Append(script[i]);
            i++;
        }

        Flush();
        return statements;
    }

    private static bool IsCreateTriggerStatement(StringBuilder sb)
    {
        var idx = 0;
        var len = sb.Length;

        void SkipWhitespaceAndComments()
        {
            while (idx < len)
            {
                if (char.IsWhiteSpace(sb[idx]))
                {
                    idx++;
                    continue;
                }
                if (sb[idx] == '-' && idx + 1 < len && sb[idx + 1] == '-')
                {
                    idx += 2;
                    while (idx < len && sb[idx] != '\n')
                    {
                        idx++;
                    }
                    if (idx < len && sb[idx] == '\n')
                    {
                        idx++;
                    }
                    continue;
                }
                if (sb[idx] == '/' && idx + 1 < len && sb[idx + 1] == '*')
                {
                    idx += 2;
                    while (idx + 1 < len && !(sb[idx] == '*' && sb[idx + 1] == '/'))
                    {
                        idx++;
                    }
                    if (idx + 1 < len)
                    {
                        idx += 2;
                    }
                    else
                    {
                        idx = len;
                    }
                    continue;
                }
                break;
            }
        }

        bool MatchWord(string word)
        {
            if (idx + word.Length > len) return false;
            for (var k = 0; k < word.Length; k++)
            {
                if (char.ToUpperInvariant(sb[idx + k]) != char.ToUpperInvariant(word[k]))
                {
                    return false;
                }
            }
            var after = idx + word.Length;
            if (after < len && (char.IsLetterOrDigit(sb[after]) || sb[after] == '_'))
            {
                return false;
            }
            idx = after;
            return true;
        }

        SkipWhitespaceAndComments();
        if (!MatchWord("CREATE")) return false;

        SkipWhitespaceAndComments();
        if (MatchWord("TEMP") || MatchWord("TEMPORARY"))
        {
            SkipWhitespaceAndComments();
        }

        if (!MatchWord("TRIGGER")) return false;

        return true;
    }

    private static bool IsWord(string script, int index, string word)
    {
        if (index + word.Length > script.Length)
        {
            return false;
        }

        if (!string.Equals(script.Substring(index, word.Length), word, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var beforeOk = index == 0 || !char.IsLetterOrDigit(script[index - 1]) && script[index - 1] != '_';
        var afterIndex = index + word.Length;
        var afterOk = afterIndex >= script.Length || !char.IsLetterOrDigit(script[afterIndex]) && script[afterIndex] != '_';
        return beforeOk && afterOk;
    }
}
