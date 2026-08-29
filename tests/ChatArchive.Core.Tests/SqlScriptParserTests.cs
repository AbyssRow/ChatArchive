using ChatArchive.Core.Importing;
using Xunit;

namespace ChatArchive.Core.Tests;

public class SqlScriptParserTests
{
    [Fact]
    public void Enumerate_AcceptsCurrentFramingAndPostgreSqlScalars()
    {
        var sql = """
            -- PostgreSQL writer output; comments are inert.
            /* block; comment */
            BEGIN;
            CREATE TABLE IF NOT EXISTS weflow_messages (
              session_id TEXT NOT NULL, local_id TEXT, message_id TEXT,
              create_time BIGINT NOT NULL, sender TEXT, is_send BOOLEAN NOT NULL,
              local_type INTEGER, media_type TEXT, content TEXT, media_path TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_weflow_messages_session_time ON weflow_messages (session_id, create_time);
            INSERT INTO weflow_messages (session_id, local_id, message_id, create_time, sender, is_send, local_type, media_type, content, media_path) VALUES
              ('group@chatroom', '1', '101', 1, 'alice', FALSE, 3, 'image', 'It''s (one, two); still text', NULL),
              ('group@chatroom', '2', '102', 2, 'bob', TRUE, 49, 'file', 'second', 'images/(one);two.jpg');
            COMMIT;
            """;

        using var reader = new StringReader(sql);
        var rows = SqlInsertReader.Enumerate(reader).ToList();

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("weflow_messages", row.Table));
        Assert.Equal("It's (one, two); still text", rows[0].Values["content"]);
        Assert.Null(rows[0].Values["media_path"]);
        Assert.Equal("FALSE", rows[0].Values["is_send"]);
        Assert.Equal("3", rows[0].Values["local_type"]);
        Assert.Equal("images/(one);two.jpg", rows[1].Values["media_path"]);
        Assert.Equal("TRUE", rows[1].Values["is_send"]);
    }

    [Fact]
    public void Enumerate_RejectsColumnlessInsertEvenWithCreateTable()
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS messages (
              id SERIAL PRIMARY KEY, session_wxid TEXT NOT NULL REFERENCES sessions(wxid),
              local_id INTEGER, create_time BIGINT NOT NULL, formatted_time TEXT,
              msg_type TEXT, content TEXT, is_send SMALLINT DEFAULT 0,
              sender_username TEXT, sender_display_name TEXT, group_nickname TEXT,
              reply_to_message_id TEXT
            );
            INSERT INTO messages VALUES (1, 'session-a', 1, 0, NULL, '文本消息', 'hello', 0, NULL, 'Alice', NULL, NULL);
            """;

        using var reader = new StringReader(sql);

        Assert.Throws<FormatException>(() => SqlInsertReader.Enumerate(reader).ToList());
    }

    [Theory]
    [InlineData("")]
    [InlineData("bare_identifier")]
    [InlineData("now()")]
    [InlineData("json_build_object('a', 1)")]
    [InlineData("'text'::text")]
    [InlineData("1 + 2")]
    [InlineData("(1)")]
    public void Enumerate_RejectsNonScalarValueExpressions(string value)
    {
        var sql = $"INSERT INTO messages (id, value) VALUES (1, {value});";

        using var reader = new StringReader(sql);

        Assert.Throws<FormatException>(() => SqlInsertReader.Enumerate(reader).ToList());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1.25")]
    [InlineData(".5")]
    [InlineData("5.")]
    [InlineData("1e3")]
    [InlineData("-1.2E-3")]
    public void Enumerate_AcceptsInvariantNumericLiterals(string value)
    {
        var sql = $"INSERT INTO messages (value) VALUES ({value});";

        using var reader = new StringReader(sql);
        var row = Assert.Single(SqlInsertReader.Enumerate(reader));

        Assert.Equal(value, row.Values["value"]);
    }

    [Theory]
    [InlineData("INSERT INTO messages (id, content) SELECT id, content FROM other_messages;")]
    [InlineData("INSERT INTO messages (id, content) VALUES;")]
    [InlineData("SELECT 1;")]
    [InlineData("DROP TABLE messages;")]
    [InlineData("UPDATE messages SET content = 'x';")]
    [InlineData("DELETE FROM messages;")]
    [InlineData("CREATE INDEX IF NOT EXISTS idx_messages_content ON messages(content);")]
    public void Enumerate_FailsClosedOnUnsupportedOrMalformedStatements(string sql)
    {
        using var reader = new StringReader(sql);

        Assert.Throws<FormatException>(() => SqlInsertReader.Enumerate(reader).ToList());
    }

    [Fact]
    public void Enumerate_UnsupportedStatementCannotHideBehindAValidRow()
    {
        const string sql = "INSERT INTO messages (id) VALUES (1); SELECT pg_read_file('/secret');";

        using var reader = new StringReader(sql);

        Assert.Throws<FormatException>(() => SqlInsertReader.Enumerate(reader).ToList());
    }

    [Fact]
    public void Enumerate_YieldsFirstTupleBeforeReadingLargeRemainder()
    {
        var sql = "INSERT INTO messages (id, content) VALUES (1, 'first'), (2, '"
            + new string('x', 100_000)
            + "');";
        using var reader = new CountingTextReader(sql);
        using var rows = SqlInsertReader.Enumerate(reader).GetEnumerator();

        Assert.True(rows.MoveNext());
        Assert.Equal("first", rows.Current.Values["content"]);
        Assert.True(reader.CharactersRead < 200, $"reader consumed {reader.CharactersRead} characters");
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    [InlineData("\r")]
    public void Enumerate_PreservesExactNewlinesInsideQuotedContent(string newline)
    {
        var content = $"first{newline}second";
        var sql = $"INSERT INTO messages (id, content) VALUES (1, 'first{newline}second');";

        using var reader = new StringReader(sql);
        var row = Assert.Single(SqlInsertReader.Enumerate(reader));

        Assert.Equal(content, row.Values["content"]);
    }

    [Fact]
    public void Enumerate_RejectsMalformedTupleArity()
    {
        const string sql = "INSERT INTO messages (id, content) VALUES (1);";

        using var reader = new StringReader(sql);

        Assert.Throws<FormatException>(() => SqlInsertReader.Enumerate(reader).ToList());
    }

    [Fact]
    public void Enumerate_ObservesCancellation()
    {
        using var reader = new StringReader("INSERT INTO messages (id) VALUES (1);");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            SqlInsertReader.Enumerate(reader, cancellation.Token).ToList());
    }

    private sealed class CountingTextReader(string text) : TextReader
    {
        private readonly StringReader _inner = new(text);

        internal int CharactersRead { get; private set; }

        public override int Read()
        {
            var value = _inner.Read();
            if (value >= 0)
            {
                CharactersRead++;
            }
            return value;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
