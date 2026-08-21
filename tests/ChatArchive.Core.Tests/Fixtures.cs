namespace ChatArchive.Core.Tests;

/// <summary>解析与导入测试共用的最小导出样例。</summary>
internal static class Fixtures
{
    public const string QqExport = """
        {
          "QQChatExporter": {"version": 4},
          "chatInfo": {"selfUin": "10001", "selfUid": "uSELF", "peerUid": "uPEER", "peerUin": "12345", "name": "老张", "type": "private"},
          "messages": [
            {
              "id": "m1", "timestamp": 1700000000000, "type": "text", "seq": 5,
              "sender": {"uid": "uPEER", "uin": "12345", "groupCard": "小李", "nickname": "Li"},
              "content": {"text": "你好",
                          "elements": [{"type": "reply", "data": {"referencedMessageId": "m0"}}],
                          "summary": "你好摘要"},
              "recalled": false
            },
            {
              "id": "m2", "timestamp": 1700000005000, "type": "image",
              "sender": {"uid": "uSELF", "uin": "10001", "nickname": "我"},
              "content": {"text": "",
                          "resources": [{"type": "image", "localPath": "resources/images/pic.jpg",
                                          "width": 800, "height": 600, "md5": "abc"}]}
            }
          ]
        }
        """;

    public const string WeFlowExport = """
        {
          "weflow": true,
          "session": {"wxid": "wxid_zhang", "type": "私聊", "remark": "张三"},
          "messages": [
            {"localId": 1, "createTime": 1700000000, "isSend": true,
             "senderUsername": "wxid_me", "senderDisplayName": "",
             "type": "文本消息", "localType": 1, "content": "在吗"},
            {"localId": 2, "createTime": 1700000060, "isSend": false,
             "senderUsername": "wxid_zhang", "senderDisplayName": "张三",
             "type": "图片消息", "content": "MSG/images/cat.jpg"}
          ]
        }
        """;
}
