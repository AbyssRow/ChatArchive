using ChatArchive.Core.Importing;
using Xunit;

namespace ChatArchive.Core.Tests;

public class SqlScriptParserTests
{
    [Fact]
    public void EnumerateRows_FiltersNonChatTables_AndParsesMessageTables()
    {
        var sql = """
            -- Non-chat table: contacts
            INSERT INTO contacts (id, name, phone) VALUES (1, 'Alice', '123456');
            -- Non-chat table: sqlite_sequence
            INSERT INTO sqlite_sequence (name, seq) VALUES ('messages', 100);
            -- Chat table: messages
            INSERT INTO messages (id, talker, content, create_time) VALUES (1, 'user1', 'Hello from messages', 1700000000);
            -- Chat table: weixin_msg
            INSERT INTO weixin_msg (msgid, talker, msg_content, createtime, is_send) VALUES ('m2', 'user2', 'Wechat msg', 1700000010, 1);
            """;

        using var reader = new StringReader(sql);
        var rows = SqlScriptParser.EnumerateRows(reader).ToList();

        Assert.Equal(2, rows.Count);
        Assert.Equal("1", rows[0]["id"]);
        Assert.Equal("user1", rows[0]["talker"]);
        Assert.Equal("Hello from messages", rows[0]["content"]);
        Assert.Equal("1700000000", rows[0]["create_time"]);

        Assert.Equal("m2", rows[1]["msgid"]);
        Assert.Equal("user2", rows[1]["talker"]);
        Assert.Equal("Wechat msg", rows[1]["msg_content"]);
        Assert.Equal("1700000010", rows[1]["createtime"]);
    }

    [Fact]
    public void EnumerateRows_HandlesNestedParentheses_AndCommasInValues()
    {
        var sql = """
            INSERT INTO messages (id, create_time, content) VALUES
              (1, datetime('now', 'localtime'), 'Value with (parentheses, and commas) inside'),
              (2, 1700000000, substr('abcdef', 1, 3));
            """;

        using var reader = new StringReader(sql);
        var rows = SqlScriptParser.EnumerateRows(reader).ToList();

        Assert.Equal(2, rows.Count);
        Assert.Equal("1", rows[0]["id"]);
        Assert.Equal("datetime('now', 'localtime')", rows[0]["create_time"]);
        Assert.Equal("Value with (parentheses, and commas) inside", rows[0]["content"]);

        Assert.Equal("2", rows[1]["id"]);
        Assert.Equal("1700000000", rows[1]["create_time"]);
        Assert.Equal("substr('abcdef', 1, 3)", rows[1]["content"]);
    }

    [Fact]
    public void EnumerateRows_HandlesMultipleStatementsOnSameLine_AndTrailingComments()
    {
        var sql = """
            INSERT INTO messages (id, content) VALUES (1, 'first'); -- exported 2023
            INSERT INTO messages (id, content) VALUES (2, 'second'); INSERT INTO messages (id, content) VALUES (3, 'third'); -- inline comment
            """;

        using var reader = new StringReader(sql);
        var rows = SqlScriptParser.EnumerateRows(reader).ToList();

        Assert.Equal(3, rows.Count);
        Assert.Equal("1", rows[0]["id"]);
        Assert.Equal("first", rows[0]["content"]);
        Assert.Equal("2", rows[1]["id"]);
        Assert.Equal("second", rows[1]["content"]);
        Assert.Equal("3", rows[2]["id"]);
        Assert.Equal("third", rows[2]["content"]);
    }

    [Fact]
    public void EnumerateRows_MysqldumpColumnlessInserts_RecoveredFromCreateTable()
    {
        var sql = """
            DROP TABLE IF EXISTS `contacts`;
            CREATE TABLE `contacts` (
              `id` int(11) NOT NULL,
              `username` varchar(64) DEFAULT NULL,
              `nickname` varchar(64) DEFAULT NULL
            );
            INSERT INTO `contacts` VALUES (1, 'alice', 'Alice Wang');

            DROP TABLE IF EXISTS `messages`;
            CREATE TABLE `messages` (
              `id` int(11) NOT NULL AUTO_INCREMENT,
              `talker` varchar(64) DEFAULT NULL,
              `create_time` bigint(20) DEFAULT NULL,
              `is_send` int(11) DEFAULT NULL,
              `type` int(11) DEFAULT NULL,
              `content` text,
              PRIMARY KEY (`id`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

            INSERT INTO `messages` VALUES (1, 'user1', 1700000000, 1, 1, 'Hello from mysqldump'), (2, 'user2', 1700000001, 0, 1, 'Reply from mysqldump');
            """;

        using var reader = new StringReader(sql);
        var rows = SqlScriptParser.EnumerateRows(reader).ToList();

        Assert.Equal(2, rows.Count);
        Assert.Equal("1", rows[0]["id"]);
        Assert.Equal("user1", rows[0]["talker"]);
        Assert.Equal("1700000000", rows[0]["create_time"]);
        Assert.Equal("1", rows[0]["is_send"]);
        Assert.Equal("1", rows[0]["type"]);
        Assert.Equal("Hello from mysqldump", rows[0]["content"]);

        Assert.Equal("2", rows[1]["id"]);
        Assert.Equal("user2", rows[1]["talker"]);
        Assert.Equal("1700000001", rows[1]["create_time"]);
        Assert.Equal("0", rows[1]["is_send"]);
        Assert.Equal("1", rows[1]["type"]);
        Assert.Equal("Reply from mysqldump", rows[1]["content"]);
    }

    [Fact]
    public void IterateMessages_WithMysqldumpSchema_ProducesParsedMessages()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_dump_{Guid.NewGuid():N}.sql");
        var sql = """
            CREATE TABLE `messages` (
              `id` int(11) NOT NULL AUTO_INCREMENT,
              `talker` varchar(64) DEFAULT NULL,
              `create_time` bigint(20) DEFAULT NULL,
              `is_send` int(11) DEFAULT NULL,
              `type` int(11) DEFAULT NULL,
              `content` text,
              PRIMARY KEY (`id`)
            );
            INSERT INTO `messages` VALUES (1, 'alice_user', 1700000000000, 1, 1, 'Message 1'), (2, 'alice_user', 1700000001000, 0, 1, 'Message 2');
            """;

        try
        {
            File.WriteAllText(tempFile, sql);

            Assert.True(SqlScriptParser.Matches(tempFile));
            var conv = SqlScriptParser.ReadConversation(tempFile);
            Assert.Equal("alice_user", conv.NativeId);
            Assert.Equal("private", conv.Kind);

            var msgs = SqlScriptParser.IterateMessages(tempFile, conv, CancellationToken.None).ToList();
            Assert.Equal(2, msgs.Count);

            Assert.Equal("1", msgs[0].NativeId);
            Assert.Equal("Message 1", msgs[0].Content);
            Assert.Equal("outgoing", msgs[0].Direction);
            Assert.Equal(1700000000000, msgs[0].TimestampMs);

            Assert.Equal("2", msgs[1].NativeId);
            Assert.Equal("Message 2", msgs[1].Content);
            Assert.Equal("incoming", msgs[1].Direction);
            Assert.Equal(1700000001000, msgs[1].TimestampMs);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }
}
