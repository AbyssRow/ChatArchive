# ChatArchive (聊天档案)

ChatArchive 是一款基于 WinUI 3 与 .NET 开发的现代化 Windows 聊天记录归档、离线浏览与检索工具。数据完全存储于本地 SQLite 数据库，无任何云端上传，安全保护个人隐私。

---

## 🌟 核心特性

- 🔍 **毫秒级全文检索**：内置 SQLite FTS5 (Trigram) 全文分词索引，支持按关键词、会话类型、时间范围多维过滤，支持一键直达消息上下文。
- 👥 **统一联系人与身份组**：支持跨平台（微信 + QQ）及同平台（大号 + 小号 + 工作号）任意多账号绑定，自定义统一备注与专属身份标签。
- 👤 **自定义头像与离线管理**：支持上传并设置联系人自定义头像，本地 SHA-256 内容寻址去重存储，时间线气泡接入 `PersonPicture` 头像与平台角标。
- 💬 **平滑时间线浏览**：虚拟化长列表加载海量消息，支持私聊与群聊会话切换、撤回标识展示与上下文跳转。
- 🖼️ **媒体附件管理**：支持图片（缩略图预览与大图保存）、音频、视频及文件的关联与打开。
- 📦 **流式导入与去重**：采用分块流式解析大体积导出文件，附件基于 SHA-256 内容寻址自动去重。
- 📊 **统计概览**：统计各联系人与群聊的消息总量、活跃时段等数据。

---

## 📥 丰富的导入格式支持

ChatArchive 采用来源专属适配器与自动嗅探引擎，支持通过软件界面直接选择**单文件**或包含聊天记录的**任意多级文件夹**（自动递归识别、自动跳过媒体文件夹以提速）：

| Platform | Exporter | Supported import formats |
| --- | --- | --- |
| WeChat | WeFlow | Standard/ArkMe/ChatLab JSON, ChatLab JSONL, WeClone CSV, Markdown, TXT, PostgreSQL SQL, Excel |
| WeChat | CipherTalk | Detailed/ChatLab JSON, ChatLab JSONL, PostgreSQL SQL, Excel |
| QQ | QQ Chat Exporter | Single JSON, chunked JSONL, TXT, Excel |

目录发现仅扫描 `.json .jsonl .csv .md .txt .sql .xlsx`。HTML 导出被有意排除，因为它们是浏览器展示产物，而不是稳定的数据交换格式。

---

## 🛠️ 技术栈

- **UI 框架**：Windows App SDK / WinUI 3
- **运行环境**：.NET 10 / C# 14
- **数据引擎**：SQLite (`Microsoft.Data.Sqlite`) + FTS5 全文索引

---

## 🚀 快速开始

### 环境要求
- Windows 10 (Build 17763+) 或 Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download)

### 编译与测试

```bash
# 克隆仓库
git clone https://github.com/AbyssRow/ChatArchive.git
cd ChatArchive

# 运行全套单元测试
dotnet test

# 构建项目
dotnet build
```

---

## 📄 开源许可

本项目采用 [MIT License](LICENSE) 开源。
