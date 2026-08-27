using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChatArchive.Core.Importing;

namespace ChatArchive.Core.IO;

/// <summary>
/// Reads selected nested values without materializing an entire export file.
/// </summary>
internal static class ChunkedJsonReader
{
    public static JsonObject ReadObjectProperty(
        string path,
        string propertyName,
        CancellationToken cancellationToken = default,
        int bufferSize = 16 * 1024)
    {
        using var source = OpenSource(path, cancellationToken, bufferSize);
        var token = FindRootProperty(source, path, propertyName);
        if (token.Type != JsonTokenType.StartObject)
        {
            throw InvalidValue(path, propertyName, "对象");
        }

        var result = ReadObject(source, path);
        FinishRootObject(source, path);
        return result;
    }

    public static IEnumerable<JsonObject> EnumerateObjectArray(
        string path,
        string propertyName,
        CancellationToken cancellationToken = default,
        int bufferSize = 16 * 1024)
    {
        using var source = OpenSource(path, cancellationToken, bufferSize);
        var token = FindRootProperty(source, path, propertyName);
        if (token.Type != JsonTokenType.StartArray)
        {
            throw InvalidValue(path, propertyName, "数组");
        }

        while (true)
        {
            token = ReadRequired(source, path);
            if (token.Type == JsonTokenType.EndArray)
            {
                FinishRootObject(source, path);
                yield break;
            }

            if (token.Type != JsonTokenType.StartObject)
            {
                throw InvalidValue(path, $"{propertyName} 数组元素", "对象");
            }

            yield return ReadObject(source, path);
        }
    }

    /// <summary>
    /// Sniffs required root property names without materializing their values. Once all
    /// markers are found the caller is expected to perform full validation in Open.
    /// </summary>
    public static bool ContainsRootProperties(
        string path,
        IReadOnlyCollection<string> propertyNames,
        CancellationToken cancellationToken = default,
        int bufferSize = 16 * 1024)
    {
        ArgumentNullException.ThrowIfNull(propertyNames);
        if (propertyNames.Count == 0)
        {
            return true;
        }

        var required = propertyNames.ToHashSet(StringComparer.Ordinal);
        var found = new HashSet<string>(StringComparer.Ordinal);
        using var source = OpenSource(path, cancellationToken, bufferSize);
        var token = ReadRequired(source, path);
        if (token.Type != JsonTokenType.StartObject)
        {
            return false;
        }

        while (true)
        {
            token = ReadRequired(source, path);
            if (token.Type == JsonTokenType.EndObject)
            {
                EnsureEndOfDocument(source, path);
                return false;
            }

            if (token.Type != JsonTokenType.PropertyName)
            {
                throw new ImportFormatException(path, "JSON 对象属性无效");
            }

            var value = ReadRequired(source, path);
            if (token.Text is not null && required.Contains(token.Text))
            {
                found.Add(token.Text);
                if (found.Count == required.Count)
                {
                    return true;
                }
            }

            SkipValue(source, path, value);
        }
    }

    private static TokenSource OpenSource(
        string path,
        CancellationToken cancellationToken,
        int bufferSize)
    {
        try
        {
            return new TokenSource(path, cancellationToken, bufferSize);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ImportFormatException(path, $"读取失败（{ex.Message}）");
        }
    }

    private static Token FindRootProperty(TokenSource source, string path, string propertyName)
    {
        var token = ReadRequired(source, path);
        if (token.Type != JsonTokenType.StartObject)
        {
            throw new ImportFormatException(path, "JSON 根节点必须是对象");
        }

        while (true)
        {
            token = ReadRequired(source, path);
            if (token.Type == JsonTokenType.EndObject)
            {
                throw new ImportFormatException(path, $"缺少 {propertyName}");
            }

            if (token.Type != JsonTokenType.PropertyName)
            {
                throw new ImportFormatException(path, "JSON 对象属性无效");
            }

            var value = ReadRequired(source, path);
            if (string.Equals(token.Text, propertyName, StringComparison.Ordinal))
            {
                return value;
            }

            SkipValue(source, path, value);
        }
    }

    private static JsonObject ReadObject(TokenSource source, string path)
    {
        var result = new JsonObject();
        while (true)
        {
            var token = ReadRequired(source, path);
            if (token.Type == JsonTokenType.EndObject)
            {
                return result;
            }

            if (token.Type != JsonTokenType.PropertyName)
            {
                throw new ImportFormatException(path, "JSON 对象属性无效");
            }

            var value = ReadRequired(source, path);
            result[token.Text!] = ReadValue(source, path, value);
        }
    }

    private static JsonArray ReadArray(TokenSource source, string path)
    {
        var result = new JsonArray();
        while (true)
        {
            var token = ReadRequired(source, path);
            if (token.Type == JsonTokenType.EndArray)
            {
                return result;
            }

            result.Add(ReadValue(source, path, token));
        }
    }

    private static JsonNode? ReadValue(TokenSource source, string path, Token token)
    {
        return token.Type switch
        {
            JsonTokenType.StartObject => ReadObject(source, path),
            JsonTokenType.StartArray => ReadArray(source, path),
            JsonTokenType.String or JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False
                => token.Value,
            JsonTokenType.Null => null,
            _ => throw new ImportFormatException(path, "JSON 值无效"),
        };
    }

    private static void SkipValue(TokenSource source, string path, Token first)
    {
        if (first.Type is not (JsonTokenType.StartObject or JsonTokenType.StartArray))
        {
            if (first.Type is JsonTokenType.EndObject or JsonTokenType.EndArray or JsonTokenType.PropertyName)
            {
                throw new ImportFormatException(path, "JSON 值无效");
            }

            return;
        }

        var depth = 1;
        while (depth > 0)
        {
            var token = ReadRequired(source, path);
            if (token.Type is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                depth++;
            }
            else if (token.Type is JsonTokenType.EndObject or JsonTokenType.EndArray)
            {
                depth--;
            }
        }
    }

    private static void FinishRootObject(TokenSource source, string path)
    {
        while (true)
        {
            var token = ReadRequired(source, path);
            if (token.Type == JsonTokenType.EndObject)
            {
                EnsureEndOfDocument(source, path);
                return;
            }

            if (token.Type != JsonTokenType.PropertyName)
            {
                throw new ImportFormatException(path, "JSON 对象属性无效");
            }

            SkipValue(source, path, ReadRequired(source, path));
        }
    }

    private static void EnsureEndOfDocument(TokenSource source, string path)
    {
        if (source.ReadToken() is not null)
        {
            throw new ImportFormatException(path, "JSON 根对象后存在额外内容");
        }
    }

    private static Token ReadRequired(TokenSource source, string path)
    {
        return source.ReadToken()
            ?? throw new ImportFormatException(path, "JSON 意外结束");
    }

    private static ImportFormatException InvalidValue(string path, string propertyName, string expected)
    {
        return new ImportFormatException(path, $"{propertyName} 必须是 JSON {expected}");
    }

    private readonly record struct Token(JsonTokenType Type, string? Text, JsonNode? Value);

    private sealed class TokenSource : IDisposable
    {
        private static readonly UTF8Encoding StrictUtf8 = new(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

        private readonly string _path;
        private readonly CancellationToken _cancellationToken;
        private readonly FileStream _stream;
        private byte[] _buffer;
        private int _start;
        private int _length;
        private bool _isFinalBlock;
        private bool _bomProcessed;
        private JsonReaderState _state;

        public TokenSource(string path, CancellationToken cancellationToken, int bufferSize)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 4);

            _path = path;
            _cancellationToken = cancellationToken;
            _buffer = new byte[bufferSize];
            _stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: Math.Max(4096, bufferSize),
                FileOptions.SequentialScan);
        }

        public Token? ReadToken()
        {
            try
            {
                _cancellationToken.ThrowIfCancellationRequested();
                ProcessBom();
                while (true)
                {
                    var reader = new Utf8JsonReader(
                        _buffer.AsSpan(_start, _length),
                        _isFinalBlock,
                        _state);

                    if (reader.Read())
                    {
                        var token = CopyToken(reader);
                        Consume(reader);
                        return token;
                    }

                    Consume(reader);
                    if (_isFinalBlock)
                    {
                        return null;
                    }

                    EnsureWritableSpace();
                    ReadMore();
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ImportFormatException)
            {
                throw;
            }
            catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
            {
                throw new ImportFormatException(_path, $"JSON 解析失败（{ex.Message}）");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ImportFormatException(_path, $"读取失败（{ex.Message}）");
            }
        }

        private void ProcessBom()
        {
            if (_bomProcessed)
            {
                return;
            }

            while (_length < 3 && !_isFinalBlock)
            {
                EnsureWritableSpace();
                ReadMore();
            }

            if (_length >= 3 &&
                _buffer[_start] == 0xEF &&
                _buffer[_start + 1] == 0xBB &&
                _buffer[_start + 2] == 0xBF)
            {
                _start += 3;
                _length -= 3;
            }

            _bomProcessed = true;
        }

        private static Token CopyToken(Utf8JsonReader reader)
        {
            return reader.TokenType switch
            {
                JsonTokenType.PropertyName => new Token(reader.TokenType, reader.GetString(), null),
                JsonTokenType.String => new Token(reader.TokenType, null, JsonValue.Create(reader.GetString())),
                JsonTokenType.Number => new Token(
                    reader.TokenType,
                    null,
                    JsonNode.Parse(StrictUtf8.GetString(reader.ValueSpan))),
                JsonTokenType.True => new Token(reader.TokenType, null, JsonValue.Create(true)),
                JsonTokenType.False => new Token(reader.TokenType, null, JsonValue.Create(false)),
                JsonTokenType.Null => new Token(reader.TokenType, null, null),
                _ => new Token(reader.TokenType, null, null),
            };
        }

        private void Consume(Utf8JsonReader reader)
        {
            var consumed = checked((int)reader.BytesConsumed);
            _start += consumed;
            _length -= consumed;
            _state = reader.CurrentState;
        }

        private void EnsureWritableSpace()
        {
            if (_start > 0)
            {
                _buffer.AsSpan(_start, _length).CopyTo(_buffer);
                _start = 0;
            }

            if (_length < _buffer.Length)
            {
                return;
            }

            Array.Resize(ref _buffer, checked(_buffer.Length * 2));
        }

        private void ReadMore()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var read = _stream.Read(_buffer, _start + _length, _buffer.Length - _length);
            _cancellationToken.ThrowIfCancellationRequested();
            if (read == 0)
            {
                _isFinalBlock = true;
                return;
            }

            _length += read;
        }

        public void Dispose()
        {
            _stream.Dispose();
        }
    }
}
