# Printonator — Design System (Notion-style)

> Dành cho AI khác (hoặc dev mới) làm UI tiếp. Snapshot 2026-08-25.
> Đọc kèm `docs/COMPARISON_PENPOT.md` (element gap vs Penpot) + `MainWindow.xaml` (nguồn sự thật).

## 1. Nguyên tắc (design principles)

1. **Notion-style**: nền trắng, chữ đen/grey, border mỏng #E5E5E5, ít màu — màu chỉ dành cho trạng thái (success/error/warning) và accent đen.
2. **Không gradient, không shadow nặng, không bo góc tròn quá** — CornerRadius 4-8.
3. **Grid column discipline**: header row và item template phải KHỚP ColumnDefinitions (skill dotnet-desktop) — nếu lệch là lỗi "header không fit cột".
4. **Mọi dialog phải tự có RoundedBtn/GhostBtn trong Window.Resources** — StaticResource không xuyên qua Window khác (đã vướng XamlParseException).
5. **Trạng thái luôn có màu ngữ nghĩa**: xanh lá = done/ready, đỏ = error, vàng = warning, xám = idle.
6. Font: **Segoe UI** (Windows default) — size 11-16, SemiBold cho header, Muted cho phụ.

## 2. Color tokens (định nghĩa trong MainWindow.xaml Window.Resources)

| Token | Hex | Dùng cho |
|---|---|---|
| `BgBrush` | `#FFFFFF` | Nền window |
| `BorderBrush` | `#E5E5E5` | Border mỏng mọi nơi |
| `TextBrush` | `#1F1F1F` | Chữ chính |
| `MutedBrush` | `#6B6B6B` | Chữ phụ (label, hint) |
| `FaintBrush` | `#8A8A8A` | Chữ mờ (footer hint, badge mờ) |
| `RowAltBrush` | `#FAFAFA` | Row xen kẽ |
| `HeaderBgBrush` | `#F5F5F5` | Header bảng + footer |
| `ReadyBrush` / `ReadyBgBrush` | `#16A34A` / `#F0FDF4` | Status "Ready/Done" (xanh lá) |
| `ErrorBrush` / `ErrorBgBrush` | `#DC2626` / `#FEF2F2` | Lỗi (đỏ) |
| `WarnBrush` / `WarnBgBrush` | `#CA8A04` / `#FFF9ED` | Cảnh báo (vàng) |
| `AccentBrush` | `#1F1F1F` | Logo + nút chính (đen) |

## 3. Typography

| Vai trò | FontSize | FontWeight | Màu |
|---|---|---|---|
| Tiêu đề app (logo) | 16 | SemiBold | TextBrush |
| Column header bảng | 12 | SemiBold | MutedBrush |
| Tên file (row) | 13 | Regular | TextBrush |
| Format badge | 9 | SemiBold | FaintBrush |
| Settings cell | 12 | Regular | TextBrush |
| Status pill | 11 | SemiBold | Ready/ErrorBrush |
| Footer stats | 12 | Regular | MutedBrush |
| Toast / hint | 11-12.5 | Regular | — |
| Dialog title | 13-15 | SemiBold | TextBrush |

## 4. Component styles (MainWindow.xaml Resources — nguồn sự thật)

### RoundedBtn (primary, đen)
- Background `#1F1F1F`, Foreground White, FontWeight Medium, Cursor Hand
- ControlTemplate: `Border CornerRadius=6` + ContentPresenter (WPF Button KHÔNG có CornerRadius — bắt buộc template)
- Hover `#3a3a3a`, Pressed `#0f0f0f`, Disabled Opacity 0.5

### GhostBtn (secondary, outline)
- BasedOn RoundedBtn; Background White, Foreground `#6B6B6B`
- Template: Border `BorderBrush #D4D4D4` BorderThickness 1; hover Background `#FAFAFA`

### ContextMenu / MenuItem / Separator (Notion)
- ContextMenu: White, Border `#E5E5E5` 1px, FontSize 12.5, Padding 4
- MenuItem: Padding `14,7`, Template Border `CornerRadius=5` Margin 2, hover `#F3F3F3`, isHighlighted `#F3F3F3`, disabled Opacity 0.45
- Separator: `Border Height=1 Background #EEEEEE Margin 8,4`

### Status pill
- Border CornerRadius 10 Padding `6,1`; Background ReadyBgBrush → DataTrigger State=Error → ErrorBgBrush
- Text: Semibold 11; ReadyBrush → Error → ErrorBrush
- ✅ Đã có: icon "✓ Done" + badge "↻ Reloaded" (WasReloaded → hiển thị)

### Job table colums (header + item KHỚP):
```
16 | 52 | 14 | 296 | 16 | 116 | 16 | 270 | 16 | 90 | 16 | 280
    fmt  sp  name    sp pages   sp settings sp status sp error
```

### Bulk bar
- Border `#F6F6F4` / `#D4D4D4` CornerRadius 8, hiện khi selection > 0 (Visibility)
- `BulkCountText` = "N files selected" (UIA test dùng cái này)
- Copies TextBox + Duplex CheckBox ("2-sided") + Paper ComboBox + Apply to selection (RoundedBtn)

## 5. Layout (MainWindow)

```
Grid 7 rows:
 0: Header 56px — logo + title + bell(popover) phải
 1: Toolbar 52px — +Add files | PrinterCombo ● status | r-phải: Print selected · Paper setup · Print all
 2: Auto — ErrorBanner (vàng, có Retry)
 3: Auto — (trống)
 4: *    — Job table (header + ListBox)
 5: Auto — BulkBar (collapsed mặc định)
 6: 44px — Footer (stats trái, hint phải)
```

## 6. Toast / Notification (Penpot gap — ✅ đã làm)

- **Toast success** "Added 3 files to queue": góc dưới phải, nền trắng border mỏng, auto ẩn 4-6s. ✅ Đã làm (add file / in xong / reload / preset...).
- **Notifications popover** (bell): ĐÃ CÓ — NotifPopup (Update/Security items giống Penpot).
- **Progress bar**: ĐÃ CÓ — `ProgressBar` footer + taskbar progress tính `done/total`, kèm `ProgressText %`.

## 7. Dialog patterns

| Dialog | Width | Content | Buttons |
|---|---|---|---|
| PageRangeDialog | 460 | FileName, TextBox range, hint format, SectionInfo, ErrorText | Hủy (Ghost) / OK (Rounded) |
| PaperSetupDialog | 420 | Khổ giấy combo, Chế độ in combo, Màu combo | Hủy (Ghost) / Áp dụng (Rounded) |
| PrintSettingsWindow | ~720 | Bảng cấu hình in đầy đủ 2 cột (Trang & bản / Giấy & màu): page range, parity, copies, collation, duplex, paper, paper source, color, orientation, scale, per-sheet, quality, profile + nút native driver | Hủy (Ghost) / Áp dụng (Rounded) |
| PresetManagerWindow | ~520 | Danh sách preset + đổi tên / xóa / áp dụng / xuất / nhập (`.printonator`) | Đóng (Rounded) |
| WatchFolderWindow | ~460 | Danh sách thư mục theo dõi + Thêm/Xóa + auto-print | Đóng (Rounded) |
| PrintConfirmWindow | ~480 | Xác nhận in trước khi chạy: số file, ước lượng trang, máy in | Hủy (Ghost) / In (Rounded) |
| PrintDoneWindow | ~520 | "✓ Đã in xong": thống kê file in được, nút xóa khỏi queue, rate request | Đóng (Rounded) |

**Pitfall:** mỗi dialog tự copy 2 style button (RoundedBtn/GhostBtn) — không dùng chung được qua StaticResource.

## 8. i18n & Mode (đã làm)

- **i18n 5 ngôn ngữ**: vi (nguồn chuẩn) / en / zh / ru / ja — catalog `Localization/Strings.json` + `Localization/Keys.cs` (const tránh typo).
  Chọn ngôn ngữ trong AboutWindow (`About.LanguageLabel` combo) → ghi registry → nhắc restart.
  Gate script `tools/check_i18n.ps1` (parity 5 ngôn ngữ, placeholder {n}, flag "---" chưa dịch, flag tiếng Việt lọt sang en).
- **Lite / Full mode**: `ModeResolver` đọc/ghi registry; combo trong AboutWindow (`Mode.ModeLabel`); Lite ẩn các tính năng nâng cao (ví dụ nút theo dõi thư mục), Full bật đủ.

## 9. TODO UI từ Penpot (chi tiết: docs/COMPARISON_PENPOT.md)

1. ✅ Progress bar (footer + taskbar)
2. ✅ Toast/badge success khi add file
3. ✅ "Print all (N)" count
4. ✅ Icon search ⌕ header
5. ✅ ✓ done + ↻ Reloaded badge
6. ✅ PageRangeDialog preview physical pages
7. ✅ Màn Printer Config riêng (danh sách + trạng thái + paper + capabilities + scan)
8. ✅ Default paper theo file type
9. ✅ Màn Preset (PresetManagerWindow) + nút duyệt job AwaitingApproval; màn MCP/Safety UI đầy đủ chưa làm
10. ⚠️ Security Warning — có dòng "chưa xác thực chữ ký số" trong AboutWindow; **minisign ký installer đã hoạt động** (release.yml, asset `.minisig`); màn đầy đủ theo Penpot chưa làm