# Printonator MCP Plugin

Plugin Claude Code tối giản để đăng ký MCP server Printonator + kèm skill `printonator-mcp`.

> **Dev-only**: MCP server không nằm trong bộ cài đặt (installer) — plugin trỏ tới `Printonator.Mcp.exe` đã build.

## Cách dùng

1. **Build MCP server**:
   ```powershell
   dotnet build Printonator.sln
   ```
   File exe nằm tại:
   `src\Printonator.Mcp\bin\Debug\net8.0-windows10.0.19041.0\Printonator.Mcp.exe`
   (hoặc `bin\Release\...` nếu build Release).

2. **Trỏ `command` trong `plugin.json`** đến đúng đường dẫn exe của bạn (mặc định `Printonator.Mcp.exe` — cần nằm trong `PATH`), hoặc sửa thành đường dẫn tuyệt đối.

3. **Đặt allowlist thật** trong `plugin.json` → `env.PRINTONATOR_ALLOWED_PRINTERS` (tên máy in thực tế của bạn, phân cách dấu phẩy). Mặc định là placeholder `Microsoft Print to PDF`.

4. **Bật plugin trong Claude Code**: cài plugin (vd đặt vào `~/.claude/plugins/` hoặc marketplace), rồi kiểm tra `printonator` MCP server + skill `printonator-mcp` đã nạp.

## Cấu hình an toàn

| Env | Ý nghĩa |
|---|---|
| `PRINTONATOR_REQUIRE_APPROVE` | `false` = AI tự in (không cần duyệt); `true` = job chờ `approve_job`. Mặc định plugin để `false` cho flow auto-print. |
| `PRINTONATOR_ALLOWED_PRINTERS` | Chỉ cho AI in vào các máy này (danh sách cách dấu phẩy). Rỗng + không duyệt = cấm tự in (fail-closed). |
| `PRINTONATOR_MAX_*` | Giới hạn trang/file/bản (xem SKILL §3). |

## Lưu ý

- KHÔNG commit đường dẫn tuyệt đối vào `.mcp.json` của repo.
- MCP server là dev-only: trên máy người dùng cuối chưa có exe — cần build hoặc gói riêng.
- Chi tiết tools + error + workflow: đọc `.claude/skills/printonator-mcp/SKILL.md`.
