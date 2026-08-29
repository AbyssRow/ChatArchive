using ChatArchive.Core.Importing;
using Xunit;

namespace ChatArchive.Core.Tests;

public class SqlScriptParserTests
{
    [Fact]
    public void Enumerate_PreservesTableIdentityAndPostgreSqlScalarValues()
    {
        var sql = """
            -- PostgreSQL writer output
            /* DDL and comments are ignored. */
            CREATE TABLE weflow_messages (
              session_id TEXT, content TEXT, media_path TEXT, is_send BOOLEAN, local_type INTEGER
            );
            INSERT INTO weflow_messages (session_id, content, media_path, is_send, local_type) VALUES
              ('group@chatroom', 'It''s (one, two); still text', NULL, FALSE, 3),
              ('group@chatroom', 'second', 'images/(one);two.jpg', TRUE, 49);
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
    public void Enumerate_RecoversColumnlessInsertColumnsFromCreateTable()
    {
        var sql = """
            CREATE TABLE "messages" (
              "id" SERIAL PRIMARY KEY,
              "content" TEXT,
              "created_at" BIGINT,
              CONSTRAINT positive_id CHECK (id > 0)
            );
            INSERT INTO "messages" VALUES (1, 'hello', 1700000000), (2, 'goodbye', 1700000001);
            """;

        using var reader = new StringReader(sql);
        var rows = SqlInsertReader.Enumerate(reader).ToList();

        Assert.Equal(2, rows.Count);
        Assert.Equal("1", rows[0].Values["id"]);
        Assert.Equal("hello", rows[0].Values["content"]);
        Assert.Equal("1700000001", rows[1].Values["created_at"]);
    }

    [Fact]
    public void Enumerate_PreservesNestedScalarExpressionsWithoutExecutingThem()
    {
        const string sql = "INSERT INTO messages (id, value) VALUES (1, json_build_object('a', 1, 'b', '(x,y)'));";

        using var reader = new StringReader(sql);
        var row = Assert.Single(SqlInsertReader.Enumerate(reader));

        Assert.Equal("json_build_object('a', 1, 'b', '(x,y)')", row.Values["value"]);
    }

    [Fact]
    public void Enumerate_IgnoresInsertSelectStatements()
    {
        const string sql = "INSERT INTO messages (id, content) SELECT id, content FROM other_messages;";

        using var reader = new StringReader(sql);

        Assert.Empty(SqlInsertReader.Enumerate(reader));
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
}
