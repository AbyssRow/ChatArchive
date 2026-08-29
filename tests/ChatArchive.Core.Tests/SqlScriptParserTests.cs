using ChatArchive.Core.Importing;
using Xunit;

namespace ChatArchive.Core.Tests;

public class SqlScriptParserTests
{
    private const string ExactWeFlowCreate = """
        CREATE TABLE IF NOT EXISTS weflow_messages (
          session_id TEXT NOT NULL, local_id TEXT, message_id TEXT,
          create_time BIGINT NOT NULL, sender TEXT, is_send BOOLEAN NOT NULL,
          local_type INTEGER, media_type TEXT, content TEXT, media_path TEXT
        );
        """;

    private const string ExactWeFlowInsert = """
        INSERT INTO weflow_messages
          (session_id, local_id, message_id, create_time, sender, is_send, local_type, media_type, content, media_path)
        VALUES ('session-a', '1', '101', 0, 'alice', FALSE, 1, NULL, 'hello', NULL);
        """;

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
    [InlineData("create_time BIGINT NOT NULL", "create_time TEXT NOT NULL")]
    [InlineData("is_send BOOLEAN NOT NULL", "is_send BOOLEAN")]
    [InlineData("content TEXT", "content TEXT CHECK (content IS NOT NULL)")]
    public void Enumerate_RejectsAlteredWeFlowDeclarationsBeforeValidRow(
        string currentDeclaration,
        string alteredDeclaration)
    {
        var sql = ExactWeFlowCreate.Replace(
            currentDeclaration,
            alteredDeclaration,
            StringComparison.Ordinal) + ExactWeFlowInsert;

        using var reader = new StringReader(sql);

        Assert.Throws<FormatException>(() => SqlInsertReader.Enumerate(reader).ToList());
    }

    [Fact]
    public void Enumerate_RejectsAlteredCipherTalkSessionDefaultBeforeValidRow()
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS sessions (
              wxid TEXT PRIMARY KEY, display_name TEXT NOT NULL, session_type TEXT NOT NULL,
              owner_id TEXT, message_count INTEGER, first_message_time BIGINT,
              last_message_time BIGINT, exported_at BIGINT
            );
            INSERT INTO sessions
              (wxid, display_name, session_type, owner_id, message_count, first_message_time, last_message_time, exported_at)
            VALUES ('session-a', 'A', 'private', NULL, 1, 0, 0, 0);
            """;

        using var reader = new StringReader(sql);

        Assert.Throws<FormatException>(() => SqlInsertReader.Enumerate(reader).ToList());
    }

    [Fact]
    public void Enumerate_RejectsAlteredCipherTalkMessageReferenceBeforeValidRow()
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS messages (
              id SERIAL PRIMARY KEY, session_wxid TEXT NOT NULL,
              local_id INTEGER, create_time BIGINT NOT NULL, formatted_time TEXT,
              msg_type TEXT, content TEXT, is_send SMALLINT DEFAULT 0,
              sender_username TEXT, sender_display_name TEXT, group_nickname TEXT,
              reply_to_message_id TEXT
            );
            INSERT INTO messages
              (session_wxid, local_id, create_time, formatted_time, msg_type, content, is_send, sender_username, sender_display_name, group_nickname, reply_to_message_id)
            VALUES ('session-a', 1, 0, NULL, '文本消息', 'hello', 0, NULL, 'Alice', NULL, NULL);
            """;

        using var reader = new StringReader(sql);

        Assert.Throws<FormatException>(() => SqlInsertReader.Enumerate(reader).ToList());
    }

    [Fact]
    public void Enumerate_AcceptsQuotedIdentifiersInExactCurrentDeclarations()
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS "weflow_messages" (
              "session_id" TEXT NOT NULL, "local_id" TEXT, "message_id" TEXT,
              "create_time" BIGINT NOT NULL, "sender" TEXT, "is_send" BOOLEAN NOT NULL,
              "local_type" INTEGER, "media_type" TEXT, "content" TEXT, "media_path" TEXT
            );
            INSERT INTO weflow_messages
              (session_id, local_id, message_id, create_time, sender, is_send, local_type, media_type, content, media_path)
            VALUES ('session-a', '1', '101', 0, 'alice', FALSE, 1, NULL, 'hello', NULL);
            """;

        using var reader = new StringReader(sql);
        var row = Assert.Single(SqlInsertReader.Enumerate(reader));

        Assert.Equal("hello", row.Values["content"]);
    }

    [Fact]
    public void Enumerate_AcceptsExactLowercaseQuotedInsertIdentifiers()
    {
        const string sql = """
            INSERT INTO "weflow_messages"
              ("session_id", "local_id", "message_id", "create_time", "sender", "is_send", "local_type", "media_type", "content", "media_path")
            VALUES ('session-a', '1', '101', 0, 'alice', FALSE, 1, NULL, 'hello', NULL);
            """;

        using var reader = new StringReader(sql);
        var row = Assert.Single(SqlInsertReader.Enumerate(reader));

        Assert.Equal("weflow_messages", row.Table);
        Assert.Equal(
            [
                "session_id", "local_id", "message_id", "create_time", "sender",
                "is_send", "local_type", "media_type", "content", "media_path"
            ],
            row.Values.Keys);
    }

    [Fact]
    public void Enumerate_FoldsUnquotedUppercaseInsertIdentifiers()
    {
        const string sql = """
            INSERT INTO WEFLOW_MESSAGES
              (SESSION_ID, LOCAL_ID, MESSAGE_ID, CREATE_TIME, SENDER, IS_SEND, LOCAL_TYPE, MEDIA_TYPE, CONTENT, MEDIA_PATH)
            VALUES ('session-a', '1', '101', 0, 'alice', FALSE, 1, NULL, 'hello', NULL);
            """;

        using var reader = new StringReader(sql);
        var row = Assert.Single(SqlInsertReader.Enumerate(reader));

        Assert.Equal("weflow_messages", row.Table);
        Assert.Equal(
            [
                "session_id", "local_id", "message_id", "create_time", "sender",
                "is_send", "local_type", "media_type", "content", "media_path"
            ],
            row.Values.Keys);
    }

    [Theory]
    [InlineData("WEFLOW_MESSAGES")]
    [InlineData("Weflow_Messages")]
    public void Enumerate_RejectsCaseChangedQuotedInsertIdentifierTables(string table)
    {
        var sql = $"INSERT INTO \"{table}\" (session_id) VALUES ('session-a');";

        using var reader = new StringReader(sql);

        Assert.Throws<FormatException>(() => SqlInsertReader.Enumerate(reader).ToList());
    }

    [Theory]
    [InlineData("SESSION_ID")]
    [InlineData("Session_Id")]
    public void Enumerate_PreservesCaseChangedQuotedInsertIdentifierColumns(string column)
    {
        var sql = $"INSERT INTO weflow_messages (\"{column}\") VALUES ('session-a');";

        using var reader = new StringReader(sql);
        var row = Assert.Single(SqlInsertReader.Enumerate(reader));

        Assert.Equal(column, Assert.Single(row.Values).Key);
        Assert.DoesNotContain("session_id", row.Values.Keys);
    }

    [Fact]
    public void Enumerate_RejectsCaseChangedQuotedTableIdentifier()
    {
        var sql = ExactWeFlowCreate.Replace(
            "weflow_messages",
            "\"WEFLOW_MESSAGES\"",
            StringComparison.Ordinal) + ExactWeFlowInsert;

        using var reader = new StringReader(sql);

        Assert.Throws<FormatException>(() => SqlInsertReader.Enumerate(reader).ToList());
    }

    [Fact]
    public void Enumerate_RejectsCaseChangedQuotedColumnIdentifier()
    {
        var sql = ExactWeFlowCreate.Replace(
            "session_id TEXT NOT NULL",
            "\"Session_Id\" TEXT NOT NULL",
            StringComparison.Ordinal) + ExactWeFlowInsert;

        using var reader = new StringReader(sql);

        Assert.Throws<FormatException>(() => SqlInsertReader.Enumerate(reader).ToList());
    }

    [Theory]
    [InlineData("REFERENCES \"SESSIONS\"(wxid)")]
    [InlineData("REFERENCES sessions(\"WxId\")")]
    public void Enumerate_RejectsCaseChangedQuotedReferenceIdentifiers(string reference)
    {
        var sql = $$"""
            CREATE TABLE IF NOT EXISTS messages (
              id SERIAL PRIMARY KEY, session_wxid TEXT NOT NULL {{reference}},
              local_id INTEGER, create_time BIGINT NOT NULL, formatted_time TEXT,
              msg_type TEXT, content TEXT, is_send SMALLINT DEFAULT 0,
              sender_username TEXT, sender_display_name TEXT, group_nickname TEXT,
              reply_to_message_id TEXT
            );
            INSERT INTO messages
              (session_wxid, local_id, create_time, formatted_time, msg_type, content, is_send, sender_username, sender_display_name, group_nickname, reply_to_message_id)
            VALUES ('session-a', 1, 0, NULL, '文本消息', 'hello', 0, NULL, 'Alice', NULL, NULL);
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
