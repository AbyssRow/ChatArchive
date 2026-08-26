# SDD ledger — plan: docs/superpowers/plans/2026-08-26-custom-contacts-and-avatars.md

## Pre-flight Conflict Scan
| Task Pair / Self | Interface / Scope | Finding / Ruling |
| :--- | :--- | :--- |
| Task 1 (Schema) | `ArchiveDatabase.EnsureSchema()` | v1->v2 incremental migration placed in `ArchiveDatabase.EnsureSchema()` to ensure backward compatibility. |
| Task 2 (Models) | `ContactModels` & `SenderProfile` | Use `QQNumber` naming consistency across models. |
| Task 3 (AvatarStorage) | Content-addressed storage | Use SHA-256 storage without eager physical deletions to protect shared references. |
| Task 4 (ContactRepo) | SQLite CRUD & Bindings | Supports both same-platform (QQ+QQ / WeChat+WeChat) and cross-platform (QQ+WeChat) bindings. |
| Task 5 (Projection) | `MessageEntry` & `ConversationRepository` | Hydrates Contact details in batch to prevent N+1 queries during timeline paging. |
| Task 6 (ViewModels) | `ContactsViewModel` | Uses CommunityToolkit.Mvvm for bindings and actions. |
| Task 7 (UI & Converters) | WinUI 3 XAML | Uses `PathToImageSourceConverter` for `PersonPicture` and sets `XamlRoot` for dialogs. |
| Task 8 (Verification) | Full test suite | Verifies 100% test pass on Release & Debug. |

## Progress Ledger
- [x] Task 1: 数据库 Schema 迁移升级 (Schema v1 -> v2)
- [x] Task 2: 核心数据模型 (Models)
- [x] Task 3: 本地头像存储服务 (AvatarStorageService)
- [x] Task 4: 联系人仓储与绑定业务 (ContactRepository)
- [ ] Task 5: 批量装配与时间线投影升级
- [x] Task 6: 通讯录与联系人 ViewModel
- [ ] Task 7: UI 界面整合 (WinUI 3)
- [ ] Task 8: 全套集成验证与回归测试
