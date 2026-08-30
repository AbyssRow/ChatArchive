# Current export compatibility fixtures

These files are the smallest textual structures extracted from or validated against the external current writers at the audited commits below. They are source-attributed regression inputs: they are not vendored repositories, and they are not invented generic dialects.

| Fixture | Upstream commit | Writer source |
| --- | --- | --- |
| `weflow-standard.json` / `weflow-arkme.json` | `6f8e7e89f9b1` | `electron/services/export/formatters/JsonFormatter.ts` |
| `chatlab-current.jsonl` | `6f8e7e89f9b1` | `electron/services/export/formatters/ChatLabFormatter.ts` |
| `weflow-current.csv` | `6f8e7e89f9b1` | `electron/services/export/formatters/WeCloneFormatter.ts` |
| `weflow-current.md` | `6f8e7e89f9b1` | `electron/services/export/formatters/MarkdownFormatter.ts` |
| `weflow-current.txt` | `6f8e7e89f9b1` | `electron/services/export/formatters/TxtFormatter.ts` |
| `weflow-current.sql` | `6f8e7e89f9b1` | `electron/services/export/formatters/SqlFormatter.ts` |
| `ciphertalk-detailed.json` / `ciphertalk-chatlab.json` | `6b886e682472` | `electron/services/exportService.ts` |
| `ciphertalk-current.sql` | `6b886e682472` | `electron/services/exportService.ts` |
| `qq-single.json` / `qq-chunked/**` | `888b51fab652` | `qq-chat-export-core/src/json_exporter.rs`, `chunked_jsonl_writer.rs` |
| `qq-current.txt` | `888b51fab652` | `qq-chat-export-core/src/text_exporter.rs` |

XLSX compatibility tests use `XlsxTestFile` to reproduce the exact writer sheet and row layouts instead of committing opaque binary fixtures:

| Exporter | Upstream commit | Writer source |
| --- | --- | --- |
| WeFlow | `6f8e7e89f9b1` | `electron/services/export/formatters/ExcelFormatter.ts` and the streaming equivalent in `electron/services/export/core/ExportContext.ts` |
| CipherTalk | `6b886e682472` | `electron/services/exportService.ts` |
| QQ Chat Exporter | `888b51fab652` | `qq-chat-export-core/src/excel_exporter.rs` |
