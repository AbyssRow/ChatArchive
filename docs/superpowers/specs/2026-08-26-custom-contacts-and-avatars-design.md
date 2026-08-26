# 统一联系人与自定义头像备注设计规范 (Custom Contacts, Avatars & Identity Groups Design)

## 1. 概述与设计目标 (Overview & Goals)

ChatArchive 目前底层数据模型按平台将用户隔离为 `senders`（例如 `wechat:wxid_xxx` 和 `qq:12345`）。在实际场景中，同一个好友可能拥有多个微信号、多个 QQ 号，或跨平台同时存在。

本项目旨在引入**「统一联系人（Contact / 身份组）」**模型，实现：
1. **自定义头像与统一备注**：支持为联系人设置自定义头像（本地 `avatars/` 安全存储）与全局展示备注，时间线与会话列表无缝呈现。
2. **自由账号绑定（多平台与同平台多号）**：支持将任意数量的微信账号、QQ 账号绑定到同一个联系人主体下（如：QQ 大号 + QQ 小号 + 微信工作号）。
3. **账号身份标签 (Account Labels)**：为绑定的子账号设置身份标签（如“工作号”、“大号”、“小号”），在时间线和联系人卡片中清晰区分具体发信账号。
4. **双向管理入口**：
   - 导航栏新增「通讯录」管理页，支持集中管理、搜索、创建身份组和关联账号；
   - 聊天时间线中点击头像/名称可随时弹出资料卡进行快速修改与绑定。
5. **纯离线与隐私保护**：所有头像与联系人配置完全存放在本地，绝无云端上传。

---

## 2. 数据库结构设计与版本迁移 (Database Schema & Migration)

应用数据库当前 `schema_version` 为 `1`。本次设计引入版本 `2` 迁移。

### 2.1 数据表定义

```sql
-- 统一联系人主体表
CREATE TABLE IF NOT EXISTS contacts (
    id INTEGER PRIMARY KEY,
    display_name TEXT NOT NULL,
    custom_avatar_path TEXT,
    note TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS ix_contacts_display_name ON contacts(display_name);

-- 联系人与账号绑定关系表
CREATE TABLE IF NOT EXISTS contact_senders (
    contact_id INTEGER NOT NULL REFERENCES contacts(id) ON DELETE CASCADE,
    sender_id INTEGER NOT NULL REFERENCES senders(id) ON DELETE CASCADE,
    account_label TEXT,
    is_primary INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (contact_id, sender_id)
);

-- 一个 Sender 最多绑定到一个统一联系人
CREATE UNIQUE INDEX IF NOT EXISTS ux_contact_senders_sender ON contact_senders(sender_id);
CREATE INDEX IF NOT EXISTS ix_contact_senders_contact ON contact_senders(contact_id);
```

### 2.2 迁移策略 (Migration v1 -> v2)
- 在 `MigrationRunner` 中增加 `MigrateToV2(connection)` 脚本。
- 执行表创建与索引建立，更新 `app_metadata` 中的 `schema_version` 为 `2`。
- 保证向后兼容：未绑定的 `senders` 继续按原始机制解析展示，不破坏任何既有数据。

---

## 3. 核心子系统架构 (Core Subsystems)

### 3.1 本地头像存储服务 (`AvatarStorageService`)
- **文件定位**：位于应用数据根目录下的 `avatars/` 文件夹（如数据库同级 `ChatArchive/avatars/`）。
- **文件命名与去重**：读取图片流计算 SHA-256 哈希，以 `<sha256>.<ext>` 命名保存。相同图片自动去重。
- **格式规范**：支持常用格式（PNG, JPG, WebP, BMP），提供正方形自动裁剪/缩放辅助方法，确保加载流畅。

### 3.2 联系人仓储与业务接口 (`ContactRepository`)
提供核心数据操作：
- `GetContact(long contactId)`: 获取联系人详情及其所有绑定的子账号（带平台、账号、昵称、身份标签与各账号消息量统计）。
- `FindContactBySenderId(long senderId)`: 根据平台 Sender 查询绑定的 Contact。
- `CreateContact(string displayName, string? customAvatarPath, string? note, IEnumerable<(long SenderId, string? Label, bool IsPrimary)> bindings)`: 创建新联系人并建立绑定。
- `UpdateContact(long contactId, string displayName, string? customAvatarPath, string? note)`: 更新联系人基础信息。
- `BindSender(long contactId, long senderId, string? accountLabel, bool isPrimary = false)`: 绑定账号。
- `UnbindSender(long contactId, long senderId)`: 解除绑定。
- `DeleteContact(long contactId)`: 删除联系人（级联删除关系，`senders` 保持完好）。
- `ListContacts(string? searchKeyword = null)`: 通讯录列表分页或全量加载，包含绑定账号概览与总消息数。
- `GetUnboundSenders(string? searchKeyword = null)`: 查询尚未绑定任何联系人的 Sender 列表（用于快速绑定）。

### 3.3 名称与头像解析策略 (`ContactDisplayNameResolver`)
时间线与会话列表展示时的优先级：
- **头像解析**：
  1. `Contact.CustomAvatarPath`（用户设置的自定义本地头像）
  2. `Sender.ImportedAvatarPath`（若未来导入包包含头像）
  3. `PersonPicture` 首字占位（自动根据联系人姓名/昵称首字符渲染并分配主题色）。
- **名称解析**：
  1. `Contact.DisplayName`（统一自定义备注，如“张三”）
  2. `SenderDisplayName.Resolve`（群名片 / 别名 / 快照名）
- **身份标识行 (Identity Line)**：
  - 若已绑定联系人：显示 `[平台图标] [账号标签] (账号ID)`，如 `🐧 工作号 (10001)` 或 `💬 生活微信 (wxid_abc)`。
  - 若未绑定联系人：显示默认的平台与账号 ID。

---

## 4. UI 与交互设计 (WinUI 3 Presentation)

### 4.1 导航栏新增「通讯录」页 (`ContactsView`)
1. **左侧联系人列表**：
   - 顶部提供搜索过滤框（支持按备注、昵称、账号 ID 快速过滤）与“+ 新建联系人”按钮。
   - 列表项包含：圆形头像（`PersonPicture`）、主备注名、绑定的平台徽标小集合（如 `💬 🐧`）、总互动消息数。
2. **右侧联系人详情面板**：
   - **头部区域**：大尺寸头像（点击可选择本地图片更换或移除）、姓名编辑输入框、备注笔记编辑框、保存按钮。
   - **已绑定账号组 (Identity Group)**：
     - 列表展示该联系人名下所有绑定的 Sender。
     - 每条记录包含：平台图标、账号 ID、原始昵称、**可直接编辑的「账号标签」**（如“大号”、“工作号”）、该账号消息总数、以及「设为主账号」和「解除绑定」按钮。
     - 底部提供「+ 绑定新账号」按钮，点击弹出选择器，支持搜索并一键关联其他未绑定账号。
   - **关联会话与统计**：
     - 列出该联系人所有涉及的聊天会话列表（点击直接跳转至该会话）。

### 4.2 时间线消息气泡与资料弹窗升级 (`TimelineView` & `ContactDialog`)
1. **时间线气泡**：
   - 消息气泡左侧（发送方）与右侧（自己）渲染 `PersonPicture` 头像。
   - 头像右下角叠加 14px 微型平台角标（微信 / QQ）。
   - 消息发送者名称行：展示 `统一备注` + `账号标签 (如 🐧 工作号)`。
2. **资料卡弹窗 (`ContactProfileDialog`)**：
   - 点击消息头像或姓名时打开。
   - 若已绑定：直接展示联系人详情，支持快速修改头像、备注名及子账号标签。
   - 若未绑定：支持“一键为此人创建联系人”或“绑定到已有联系人”。

---

## 5. 测试与验证策略 (Testing & Verification Strategy)

1. **Schema 迁移测试 (`MigrationTests.cs`)**：
   - 验证从 v1 迁移到 v2 的无损性、表与索引结构正确性。
2. **联系人仓储测试 (`ContactRepositoryTests.cs`)**：
   - 验证联系人 CRUD、跨平台绑定（QQ+微信）、同平台多号绑定（QQ+QQ / 微信+微信）、解绑、主账号切换等行为。
   - 验证级联删除行为（删除 Contact 后 `senders` 不受影响）。
3. **头像存储测试 (`AvatarStorageTests.cs`)**：
   - 验证头像保存、SHA-256 去重、文件存在性检查与清理。
4. **UI 视图模型测试 (`ContactsViewModelTests.cs` & `TimelineProjectionTests.cs`)**：
   - 验证时间线气泡对统一备注、自定义头像路径及账号身份标签的投影计算正确性。
5. **回归测试**：
   - 确保全套原有 114 项单元测试全部保持 100% 通过。
