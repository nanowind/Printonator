# CI/CD — Printonator

> Pipeline hiện tại: **GitHub Actions** (Windows). Snapshot 2026-08.
> File thật: [`.github/workflows/ci.yml`](../.github/workflows/ci.yml) — là nguồn sự thật; tài liệu này giải thích.

## 1. Kiến trúc pipeline (2 lớp)

```
Push / PR
   └─▶ CI (windows-latest, headless-safe)
         checkout → dotnet restore → build Release → dotnet format --verify → Core unit tests (55)
   └─▶ E2E (self-hosted Windows có desktop, chạy thủ công workflow_dispatch)
         restore → build Debug → Core tests (55) → E2E FlaUI (5)
```

**Vì sao tách 2 lớp:** UITests (FlaUI.UIA3) phải **mở cửa sổ WPF thật** — runner GitHub hosted chạy headless
không lên desktop đáng tin → E2E gated bằng `workflow_dispatch` và cần runner **self-hosted** (hoặc chạy local).

## 2. Từng bước CI làm gì

| Bước | Lệnh | Bắt lỗi |
|---|---|---|
| Restore | `dotnet restore Printonator.sln` | NuGet, phiên bản package (MCP SDK...) |
| Build | `dotnet build Printonator.sln -c Release` | compile, XAML parse, binding lỗi resource |
| Format | `dotnet format Printonator.sln --verify-no-changes` | lệch chuẩn whitespace/style (repo chạy `dotnet format`) |
| Unit test | `dotnet test tests/Printonator.Core.Tests -c Release` | 55 test: page-range, section, queue, approve, guard, preset, engine registry |

- PR không qua build/format/unit → **đỏ**, chặn merge (branch protection nên chặn).
- E2E không block CI (chạy tuỳ lúc) vì điều kiện desktop.

## 3. Chạy local (tương đương CI)

```powershell
dotnet restore Printonator.sln
dotnet build Printonator.sln -c Release
dotnet format Printonator.sln --verify-no-changes   # lỗi → chạy `dotnet format` (áp) rồi sửa nếu cần
dotnet test tests/Printonator.Core.Tests -c Release

# E2E (máy có desktop + .NET 8):
dotnet test tests/Printonator.UITests                # 5 FlaUI tests
```

## 4. Vùng phủ / gap cần chú ý

| Lớp | Cái gì | KHÔNG phủ |
|---|---|---|
| Unit (Core) | Logic thuần: resolve trang, section, approve/cancel, guard fail-closed, preset, engine chọn | UI, COM Office, máy in |
| E2E (FlaUI) | App WPF thật: khởi động, in all không dup dòng, in selected, multi-select bulk bar, dropdown mở+chọn | Menu chuột phải (WPF ContextMenu KHÔNG lộ UIA3), COM Office, máy in thật |
| Integration (MCP smoke) | MCP server HTTP: initialize, tools/list, tools/call (xem `docs/E2E-TEST.md` §5) | — |
| Manual (hướng dẫn) | In thật: Office COM (Word/Excel/PPT need app thật), máy in, Penpot thiết kế | — |

CI không in xuống máy thật (an toàn + runner không có máy in) — in thật là bước manual/tự-host.

## 5. Phát hành / packaging (roadmap)

- Hiện chỉ `dotnet build`. Khi release:
  ```powershell
  dotnet publish src/Printonator.UI -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
  ```
- **Ký bản phát hành** (minisign/SHA-256) **đã hoạt động** — release.yml dùng minisign **0.12** (bắt buộc: secret key format mới `RWRTY0I` không đọc được bằng 0.11), zip giải nén vào `minisign-win64/x86_64/`, pass truyền qua stdin pipe. Khi đặt `MINISIGN_SECRET_KEY` + `MINISIGN_PASSPHRASE` → asset `printonator-setup.exe.minisig` xuất hiện (verify bằng public key nhúng trong `UpdateChecker.cs`).

## 6. Thêm test vào CI

- Logic → `tests/Printonator.Core.Tests` (không cần desktop): tự nhặt vào job `build-test`.
- UI → `tests/Printonator.UITests` (cần desktop): nhặt vào job `ui-e2e` (chạy khi dispatch).
- Nếu test mới cần máy in/file thật → đưa vào **manual checklist** thay vì CI (xem `E2E-TEST.md`).

## 7. Troubleshooting

- CI đỏ vì **format**: chạy `dotnet format Printonator.sln` local, commit, push lại.
- E2E đỏ vì headless: chắc chắn dùng runner có desktop session (self-hosted) hoặc chạy local.
- Build đỏ vì XAML/resources: lỗi thường là `StaticResource` thiếu key (palette nằm trong `Themes/` đã merge ở App.xaml — đừng xoá) hoặc thiếu project ref.