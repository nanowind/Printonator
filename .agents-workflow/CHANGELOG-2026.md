# Printonator — CHANGELOG archive 2026 (rotated 2026-11-14, text moved intact)

> Phần đã đóng của `.agents-workflow/CHANGELOG.md` khi file vượt 64KB. Nội dung KHÔNG bị sửa; git history vẫn giữ nguyên. Bản hiện hành: `CHANGELOG.md`.

## 2026-08-30 (session 20260830) — 15 mục plan v0.2 + Lite/Full + release v0.2.0
- Mode: FULL 6-phase mỗi phase (Phase 0-3, mỗi phase gate: build + test_full + check_i18n + commit)
- Subsystems: toàn repo (UI/Spool/Core/Mcp/Setup/docs)
- Completed:
  - **Phase 0** (10d8f71): T0.1 tách MainWindow (PrintBatchOrchestrator + FooterController ~300 dòng); T0.2 copies cap MaxLength=3; T0.3 real CancellationToken (CTS per-job + 4 engine rethrow OCE → job Cancelled, not Error/retry; 3 test mới Core 81); T0.4 watcher 8s cancellable (_lifeCts); T0.5 i18n/glossary.md; T0.6 layout hardening RU/CJK (6 XAML). Mcp cancel_job fix description/hint.
  - **Phase 1** (7678cf2): T1.1 PresetManagerWindow (liệt kê/đổi tên/xóa/áp dụng + nút toolbar); T1.2 ApprovalBar (ApproveAll/RejectAll/✕, test hook PRINTONATOR_TEST_APPROVAL); T1.3 QueueStore persist + restore (Mcp→AddForApproval, User→AddOnly, HasPerFilePrinter); T1.4 Hủy lô (CancelPending + RejectJob + CancelJob đang in); T1.5 PresetExporter .printonator JSON + 2 nút PrintSettings. QueueStoreTests 6 + PresetExporterTests 4 (Core 91).
  - **Phase 2** (b753869): T2.1 CoverPageRenderer (HTML→CDP→in trước lô); T2.2 macro last/lastN (PageRangeTests); T2.3 per-file printer (HasPerFilePrinter flag + cột ComboBox 14 cột + persist); T2.4 MergePrintEngine (rasterize→HTML→1 PDF→in, PrintQueue.MarkDone); T2.5 WatermarkPrintEngine (decorator Browser, đăng ký 2 nơi); T2.6 WatchFolderService + Window + nút toolbar + watch.json; T2.7 HistoryStore (1000 entries, hook OnJobStateChanged, 5 tests); T2.8 SingleInstance (Mutex+NamedPipe+args) + App.OnStartup + installer [Registry] ShellMenu. ErrorCodes.EngineFailed + ErrorReference. Core 102.
  - **Lite/Full mode** (9c31797): ModeResolver (HKCU Mode, mặc định Lite); Lite ẩn per-file printer cột + Watch nút + Cover/Merge/Watermark options; AboutWindow combo chọn Lite/Full (restart). 4 key Mode.*
  - **Phase 3** (06b93e3): T3.1 CLI tools/printonator.ps1 (7 command MCP wrapper); T3.2 SecurityNote AboutWindow + README smart screen; T3.3 Docs sync (README/ARCHITECTURE/COMPARISON×2/MCP/DESIGN_SYSTEM, test count 139).
  - **Release v0.2.0** (6bba618): bump version + release notes tay; E2E GATE PASS (build 0/0, Core 102/102, Spool 4/4, Mcp 6/6, UI 24/27 pre-existing, check_i18n 392 keys PASS, smoke Lite ảnh i18n/smoke_lite_default.png); push master→main + tag v0.2.0 → CI build installer.
- Baseline UI fail cũ (env, không regression): NeedsRender_PdfSubsetRange, App_Launches_Empty, MultiSelect, PrintSelected_DoesNot_Duplicate (4 fail pre-existing do SendInput env). Test tổng ~139.
- Remaining: CI không chạy Spool/Mcp tests (chỉ Core + UI manual) — nên thêm vào ci.yml; 2 CS8629 pre-existing; user gắn MINISIGN secret trên GitHub để release ký số.

## 2026-08-25 (session 6-phase full run) — Printonator
- Mode: FULL (6-phase run: test đầy đủ + clean code review)
- Subsystems: Core queue engine, UI (MainWindow), page-range logic, engines, tests
- Completed:
  - Init git repo + .gitignore (loại six-phase-workflow/.qoder/.claude/hooks/.env khỏi commit)
  - Initial commit (58 files): app + setup + tests
  - Xóa Class1.cs template stub
  - FIX: PrintAll_Click chỉ lọc Queued + thông báo rõ (đồng bộ với PrintJobs)
  - FIX: CtxRemove_Click dùng RemoveJob (lock an toàn, tránh race)
  - FIX: _engine static → instance field
  - FIX: WrapError dùng chung (bỏ DRY duplication trong DrainAsync/DrainOnceAsync)
  - FIX: indentation vỡ — dotnet format toàn bộ
  - Tests: Core.Tests (xUnit, 24 tests: page-range + section-aware + queue) + UITests (FlaUI, 4 tests: launch, print-no-dup, print-all-no-dup, multiselect) — ALL 28 PASS
- Remaining:
  - PDF in thật qua PDFium (engine hiện dùng ShellExecute printto — fail với file không handler)
  - Sort theo cột, Printer/Page setup/Settings trang riêng như Penpot
  - Bell popover đã có nhưng chưa align hoàn hảo (đã thêm HorizontalAlignment=Right)
- Blocked: — (git rm --cached bị block bởi terminal guard; dùng .gitignore thay thế — an toàn tương đương)
## 2026-08-25 (session 2 — FULL: docs-gap + MCP + Penpot) — Printonator
- Mode: FULL 6-phase (P1 explore web/API Penpot + P2 thinkers/critic + P3 implem + P4 review/basher 2 vòng + P5 fix loop)
- Subsystems: Core (approve/guard/preset), Spool (mới), Mcp (mới), UI quick-wins, docs/skill/README
- Completed:
  - Core: JobState.AwaitingApproval + JobSource, AddForApproval/ApproveJob/RejectJob/CancelJob/CountPendingPages, ProcessExisting chống in kép (claim trong lock), Preset/PresetStore (JSON+backup), PrintGuard fail-closed (allowlist, quota 200 trang/lô, copies ≤100, approve mặc định, audit whitelist), ErrorCodes.ApprovalRequired.
  - Spool (mới): PrinterService (GetPrintQueues+GetPrintCapabilities thật) + SpoolPrintEngine (printto, giữ PageCount đã probe).
  - Mcp (mới, ModelContextProtocol.AspNetCore 2.2.0): HTTP 127.0.0.1:3939/mcp + --stdio; 9 tools (list_printers, print_files, presets, list/job/cancel, penpot_print_file); AdmitGate chống TOCTOU quota; InternalsVisibleTo Core→Mcp.
  - UI: search ⌕, sort cột, Print all (N), toast success, progress+taskbar, ✓/↻ badge, PrinterCombo→PrinterInfo, PrinterConfigWindow mới, PageRange preview, DefaultPaperFor (DWG→A3/TXT→A5), xóa ShellPrintEngine.
  - Tests: 52 Core (mới 28) + 4 UI — ALL PASS; dotnet format xanh.
  - Docs/skill: docs/MCP.md (mới, có config client), COMPARISON_PENPOT + COMPARISON_PRINT_CONDUCTOR cập nhật trạng thái, README.md (mới), .kilo/skills/printonator-mcp/SKILL.md.
- Remaining: engine thật PDFium+LibreOffice/WIC (P0); màn Preset UI + MCP/Safety UI + approve nút trên MainWindow (in-process host); async Penpot export-frames; quote-da-ngay; CLI.
- Blocked: —

## 2026-08-25 (session 3 — FULL: engine app gốc + bỏ Penpot-print) — Printonator
- Mode: FULL 6-phase (reuse context session 2 + research Word COM + Phase4 gate)
- Subsystems: Spool (engine), Mcp (bỏ penpot tool), Core (registry), docs/skill
- Completed:
  - PrintQueue: engine REGISTRY dạng list, pick theo CanHandle(format) (ưu tiên đăng ký), giữ demo-mode khi không engine. Engines nhiều không giới hạn.
  - Spool: InstalledApps (ProgID Word/Excel/PPT), OfficeComPrintEngine (STA thread + timeout 60s, Word/Excel/PPT PrintOut, ActivePrinter qua QueuePort, page range, copies, Word duplex + ComputeStatistics set PageCount thật; fallback shell).
  - UI + Mcp: register OfficeCom trước rồi Spool* fallback.
  - Bỏ penpot_print_file + PenpotClient (Penpot = design source, ko phải đối tượng in).
  - Fix Word COM bug 0x800A11FD: mở doc KHÔNG Visible:false + doc.Activate() trước PrintOut.
  - MCP còn 8 tools (đã bỏ penpot_print_file). README/MCP.md/COMPARISON* /ARCHITECTURE/skill cập nhật.
  - Tests: 55 Core (mới EngineRegistryTests) + 4 UI = 59 ALL PASS; build 0 warn (fix CS0067 RelayCommand).
  - Verified: smoke MCP print_files DOCX -> Done qua Word COM tới máy Canon thật.
- Remaining: PDFium cho PDF + LibreOffice khi ko có MS Office (P0); màn Preset UI + MCP/Safety UI + approve in-process; CLI.
- Blocked: —

## 2026-08-26 (session 4 — FULL: Print Settings UI theo Print Conductor) — Printonator
- Mode: FULL 6-phase (explore web how-to pages + plan + implem + review/test 67/67)
- Subsystems: Core model (PrintConfig), UI (PrintSettingsWindow/PrinterConfigWindow/MainWindow), Spool (PrinterDialogs/Trays), MCP compat, docs
- Completed:
  - PrintConfig mở rộng: ColorMode (AsPrinter/AsDocument/Color/Grayscale), PaperSource, ScaleMode+ScalePercent, PagesPerSheet+Booklet, Collation, ProfileName; PrintOrientation +AsDocument/AsPrinter; Color giữ shim bool; SummaryText/CopyInto.
  - Preset thành printer profile template đầy đủ (lưu mọi option); MCP print_files nhận colorMode/paperSource/scaleMode/pagesPerSheet.
  - PrinterService.Trays[] (tên khay VN; LƯU Ý InputBin .NET Core chỉ 6 giá trị) + PrinterDialogs.cs mở native driver dialog (printui.dll /e /p).
  - PrintSettingsWindow MỚI (2 cột): profile combo + Lưu/Xóa, page range All/Ranges + preview, màu 4 mức, khay, duplex 4 mức, collation, khổ giấy theo máy, chiều 4 mức, scale 6 mức + zoom%, N-up 1..16 + booklet, nút Printing Preferences/Printer Properties; áp selection hoặc đặt default.
  - MainWindow: BulkBar tinh gọn (count+summary+nút Cấu hình in), toolbar Print settings, Item settings context menu, cột Settings = SummaryText, _defaultConfig cho file mới; xóa PaperSetupDialog + bulk cũ.
  - PrinterConfigWindow: nút native dialog cho từng máy.
  - Tests: PrintConfigTests (6 mới Core) + UITests UseWPF + smoke ctor PrintSettingsWindow — 61 Core + 6 UI = 67 ALL PASS; build 0 warn; dotnet format xanh.
  - Docs: COMPARISON_PRINT_CONDUCTOR.md viết lại đầy đủ (bảng cấu hình in + mục UI/UX gaps đã xử lý + roadmap mới).
- Remaining: PDFium + LibreOffice để option mới (scale/N-up/khay) có hiệu lực trên PDF/ảnh/TXT; export/import preset .ini + CLI; màn MCP/Safety UI + approve; per-file printer cột; macro page range "last".
- Blocked: FlaUI SendInput "Access is denied" flaky trên máy (đã né bằng Invoke pattern cho test bấm nút)

## 2026-08-26 (session 4b — REUSE SKIP P1+P2: engine dynamic, KHÔNG bundle) — Printonator
- Mode: REUSE (context session 4 đã map subsystem) — quyết định user: "PDFium + LibreOffice sao phụ thuộc app làm gì — dynamic theo máy user, app nhẹ, ko cần lib riêng"
- Subsystems: Spool (LibreOfficeLocator + LibreOfficePrintEngine), docs
- Completed:
  - LibreOfficeLocator: dò soffice trên máy (env PRINTONATOR_LIBREOFFICE → registry HKLM/HKCU Software\LibreOffice\LibreOffice InstallPath → ProgramFiles\LibreOffice\program); ưu tiên soffice.com (headless console chờ được exit) rồi soffice.exe; chỉ nhận file tồn tại.
  - LibreOfficePrintEngine: `soffice --headless --norestore --nologo --pt "máy" "file"` / `-p` máy mặc định (cú pháp VERIFIED từ help.libreoffice.org); timeout 120s + Kill(entireProcessTree); CanHandle chỉ nhận office format khi máy có soffice.
  - Đăng ký ở cả MainWindow.InitializeAsync + AppServices.EnsureEngine: OfficeCom → LibreOffice → Spool (registry chọn đầu tiên CanHandle).
  - Tests: LibreOfficeEngineTests 5 test deterministic (override path) — 61 Core + 11 UI = 72 ALL PASS; build 0 warn; format xanh.
  - Docs: AGENTS.md (module thật + pipeline dynamic + bỏ PDFium URL), ARCHITECTURE.md (bảng engine dynamic, bỏ PDFium bundle), README.md (engine dynamic + test counts), COMPARISON (LibreOffice ✅ dynamic, PDF = shell + probe không lib, P0 = render dynamic), context.md.
- Remaining: engine render dynamic cho PDF/ảnh/TXT để scale/N-up/khay áp thật (dò lib có sẵn, KHÔNG bundle); export/import preset .ini + CLI; màn MCP/Safety UI + approve; per-file printer cột; macro "last".
- Blocked: —

## 2026-08-26 (session 4c — REUSE: BrowserPrintEngine, render bằng Chrome/Edge có sẵn) — Printonator
- Mode: REUSE (context đã map) — user: "mấy cái còn lại tận dụng browser engine được ko — máy ai cũng có, xem pdf bằng chrome in cũng đc"
- Quyết định: KHÔNG bundle PDFium/LibreOffice — **dùng Chrome/Edge headless CDP làm engine render** (máy ai cũng có).
- Completed:
  - BrowserLocator (Chrome trước — verify thực nghiệm Chrome headless OK, Edge 151 trên máy exit 0 im lặng; Edge vẫn fallback), env PRINTONATOR_BROWSER.
  - CdpPrintParams (thuần): PrintConfig → CDP `Page.printToPDF` params (pageRanges "1-5, 8, 11-13", scale 0.1–1.5, inch từ PaperCatalog mm, landscape, margin Fill=0, printBackground, header/footer off).
  - DevToolsPrintClient (chỉ BCL/ClientWebSocket): --headless=new --remote-debugging-port=0 --user-data-dir → stderr port → /json/list → ws → Page.enable → poll ready (PDF cần numPages>0 — readyState complete quá sớm gây "Page range exceeds page count") → printToPDF → base64; kill tree mọi nhánh.
  - BrowserPrintEngine: CanHandle PDF/PNG/JPG/.../TXT/CSV; **FAST-PATH** config-as-default → shell thẳng (không render; đã fix treo UI suite do demo jobs render 12 Chrome); render khi range(non-PDF)/scale/landscape/paper≠A4; render fail → rớt mềm shell; PDF tạm → printto N bản.
  - VERIFY CDP E2E thật (probe tạm): render ra PDF thật; HTML 3 trang + pageRanges "2-2" → 1 trang ✓; **PDF viewer TỪ CHỐI ranges** → engine không gửi pageRanges cho PDF (PDF slicing = P0).
  - Đăng ký 4 engine: OfficeCom → LibreOffice → Browser → Spool (UI + MCP). Docs README/ARCHITECTURE/COMPARISON/AGENTS cập nhật.
- Tests: BrowserEngineTests 10 (locator/ranges/params/scale/CanHandle) — 61 Core + 21 UI = 82 ALL PASS; build 0 warn.
- Remaining: PDF slicing thật (P0); export/import preset .ini + CLI; màn MCP/Safety UI + approve; per-file printer; macro "last".
- Blocked: —

## 2026-08-26 (session 4d — REUSE: hoàn thiện option theo feedback PC) — Printonator
- Mode: REUSE (context đã map) — user: "tài liệu compare chưa đầy đủ, thiếu chỉ dẫn + option cơ bản (parity, res, khổ gốc...); đừng ảnh hưởng browser user"
- Completed:
  - Core: PageParityFilter (All/Odd/Even), PrintQuality (AsPrinter→Draft), PaperCatalog.AsDocument; PrintConfig/Preset/CopyInto/SummaryText đầy đủ.
  - Spool: ResolveSelectedPages + parity filter; Build() preferCSSPageSize khi khổ "theo tài liệu"; DpiFor quality; WindowsPdfRasterizer nhận renderDpi.
  - UI PrintSettingsWindow: nhóm option + HINT MỖI option; ParityCombo/QualityCombo/Paper "khổ gốc". MCP print_files thêm parity/quality; print_with_preset map đủ.
  - Fix UI: badge "↻ Reloaded" (ẩn BORDER khi chưa reload — hết đốm vàng); nút "Retry connection" chỉ khi lỗi SpoolerFailed/PrinterNotFound.
  - DOCS: COMPARISON_PRINT_CONDUCTOR.md viết LẠI đầy đủ (~90 mục: thêm odd/even, skip-repeated, page-size-based, resolution, khay per range, last-macro, FLIST, context menu Explorer, history, watermark/pre-print/post-process chi tiết, time delay, job name, single-job, load balancing, keep/discard properties...). README/ARCHITECTURE/AGENTS cập nhật test counts + engine.
- Tests: 62 Core + 27 UI = 89 ALL PASS (pattern: build riêng → test --no-build; `dotnet test` trực tiếp treo pha build trên máy).
- Lesson: KHÔNG BAO GIỜ Kill chrome/msedge của user khi dọn process test.
- Remaining: Cover/Report (P0); MCP/Safety UI + approve; export/import preset .ini + CLI; per-file printer; macro "last"; single-job merge.
- Blocked: —

## 2026-08-26 (session 5 — REUSE NARROW: fix in nhầm máy + dialog driver hỏng + 2 tab Cơ bản/Nâng cao) — Printonator
- Mode: REUSE (context đã map subsystem UI/Spool) — bugfix 3 mục theo feedback user
- Subsystems: UI (MainWindow/PrintSettingsWindow), Spool (PrinterDialogs), tools/
- Completed:
  - FIX in nhầm máy (báo "chọn LBP242/243 vẫn in vào PDF"): PrintJobs + CtxPrint_Click giờ ÉP SelectedPrinter lên MỌI job qua ApplySelectedPrinter — trước đây `??=` giữ nguyên PrinterName cũ ghi lúc thêm file nên đổi combo không có tác dụng.
  - FIX nút Printing Preferences/Properties "không tác dụng": verify thực nghiệm printui.dll PrintUIEntry IM LẶNG trên máy này (cả /e /p, mọi biến thể quoting, UseShellExecute true/false — rundll32 exit 0 không cửa sổ). Viết lại PrinterDialogs: Preferences = Win32 DocumentProperties(DM_IN_PROMPT) trên thread STA riêng + owner window ẩn (validate OpenPrinter + DEVMODE size đồng bộ để trả lỗi rõ); Properties = shell verb "properties" trong Devices and Printers (Shell.Application COM reflection — PITFALL: "Name" là PROPERTY cần BindingFlags GetProperty|InvokeMethod, không phải method).
  - VERIFY production harness (temp console tham chiếu Spool + EnumWindows diff + WM_CLOSE tự đóng): dialog mở THẬT cho "Printonator Test" (ảo), "Canon LBP151 (222)" (UFRII), "LBP242/243" (prefs, class driver OpenXPS/WSD).
  - PrintSettingsWindow bố trí LẠI thành 2 TAB Cơ bản (copies/range/khổ/duplex/màu + cài đặt driver) / Nâng cao (parity/collation/khay/chiều/scale/N-up/quality); TabControl segmented style inline; GIỮ NGUYÊN mọi x:Name → code-behind không đổi.
  - tools/test_printui.ps1 + tools/test_shell_props.ps1 — script test có log (tools/test_printui.log), tự dọn process nó spawn (KHÔNG chạy inline nữa sau khi bị treo).
  - Máy in ảo tạm "Printonator Test" (driver Microsoft Print To PDF, PORTPROMPT:) để test ở nhà — gỡ: Remove-Printer -Name "Printonator Test".
- Tests: 62 Core + smoke PrintSettingsWindow_Constructor_Loads PASS; build 0 error.
- Bổ sung cùng session (feedback UI):
  - FIX tab "Cơ bản" chớp nháy khi rê chuột: trigger IsMouseOver đặt SAU IsSelected trong TabItem template — trước đây hover đè lên selected làm tab đang chọn xám↔trắng theo từng pixel di chuột.
  - Cột "Pages to print": thay ô TextBox bằng cell bấm được → popup chọn "Tất cả các trang" / "Khoảng trang" + ô nhập (radio bật/tắt ô); Áp dụng theo quy tắc selection (row thuộc nhóm chọn → áp cả nhóm). Cell hiển thị "Tất cả" khi range trống/All.
  - FIX radio trong popup Pages KHÔNG loại nhau: 2 radio nằm ở 2 StackPanel khác nhau → không cùng nhóm mặc định; gán GroupName="{Binding FilePath}" (duy nhất theo dòng) — GroupName là cách duy nhất gom radio khác cha logic.
  - Header cột checkbox: thêm checkbox chọn-tất-cả — dùng event Click (không phải Checked/Unchecked) để click lúc indeterminate luôn = CHỌN HẾT; sync ✓/■/trống qua SyncSelectAllState() gọi từ UpdateFooter + OnSelectionChanged.
  - Tab Cơ bản bố trí lại 2 CỘT (trái: copies+page range, phải: khổ/duplex/màu, combo 330px) + khối "Cài đặt máy in (driver)" full-width cuối tab cùng kiểu khối Profile — hết trống bên phải.
- Remaining: verify tay trên LBP242/243 tại cơ quan (in thật + 2 nút dialog); export/import preset .ini + CLI; màn MCP/Safety UI + approve; per-file printer.
- Blocked: —

## 2026-08-26 19:15 (session audit — kiểm tra rò rỉ workflow/AI + squash history) — Printonator
- Mode: BOUNDED (audit read-only + git surgery theo lựa chọn user "Squash lại history")
- Subsystems: git history, .gitignore, README.md, docs/ARCHITECTURE.md
- Completed:
  - AUDIT tracking + TOÀN BỘ history: đã track/vào git từ trước — AGENTS.md (nguyên văn spec 6-phase), CLAUDE.md (@AGENTS.md), SOUL.md, .claude/agents/* (10) + .claude/settings.local.json, .kilo/agents/* (10), .agents-workflow/CHANGELOG.md (log nội bộ + quote quyết định user), .mcp.json; lẫn trong docs: README row AGENTS.md, ARCHITECTURE nhắc six-phase + hash `ada93e3` + note AGENTS.md; commit message cũng lộ ("six-phase-workflow", "context for 6-phase session").
  - Sạch từ trước: .agents-workflow/context.md, .qoder/, .agents-workflow.env chưa từng commit.
  - Repo KHÔNG có remote → chưa lộ ra ngoài trước khi sửa.
  - Backup bundle (verify OK): C:\Users\scraw\AppData\Local\Temp\kilo\printonator-master-backup-20260826.bundle (chứa history cũ 80dd6b4).
  - SQUASH: orphan branch → index sạch (kiểm tra trước khi commit) → xoá master cũ → rename → reflog expire --expire=now --all + gc --prune=now --aggressive.
  - History mới: ĐÚNG 1 commit `d51b4ec` (88 files, 9636 dòng), message sạch. Verify: cat-file các commit cũ FAIL ("Not a valid object name"), count-objects loose=0, git ls-files CLEAN, git grep HEAD toàn bộ từ khóa CLEAN (khớp duy nhất = rule trong .gitignore, chủ đích).
  - .gitignore bổ sung: SOUL.md, .mcp.json, .agents-workflow/ (cả thư mục), .codex/, .opencode/, .github/instructions/.
  - Docs: README bỏ row AGENTS.md; ARCHITECTURE bỏ tham chiếu six-phase/hash cũ/note AGENTS.md.
  - Thay đổi pending PrinterDialogs.cs (session 5) được đưa vào commit gốc — tree khớp working dir.
  - Memory .github/instructions/memory.instruction.md (đã ignore) ghi ràng buộc: không bao giờ commit AI/workflow docs + đường dẫn backup.
- Remaining: cân nhắc bỏ binary setup/app/*.dll|*.exe|*.pdb + tools/test_printui.log khỏi git (đang track, làm repo nặng vô ích).
- Blocked: —

## 2026-08-26 21:15 (session 6 — FULL: UI/UX Review + cài skill design tham khảo) — Printonator
- Mode: FULL (P1 narrow: audit tĩnh + map surface + research + skill-finder; P2 thinker×2 + critic; P4 code-reviewer fact-check + basher 89/89; review-only — KHÔNG sửa code)
- Subsystems: UI (MainWindow/PrintSettingsWindow/PrinterConfigWindow/PageRangeDialog/Themes), docs; skills `.agents/skills/`
- Completed:
  - Cài 2 skill design tham khảo (như user yêu cầu "huashu"): `alchaincyf/huashu-design` + `vercel-labs/agent-skills@web-design-guidelines` → `.agents\skills\` (junction .claude/.qoder; khuyến nghị thêm `.agents/` vào .gitignore nếu không muốn commit — CHƯA làm).
  - Audit heuristic Nielsen + form-design + WCAG + Win32 UX Guide: 25 findings ban đầu, sau fact-check còn lại:
    - P0: (1) **Drag&drop file KHÔNG TỒN TẠI** trong code (grep AllowDrop/Drop/DragEnter = 0; comment MainWindow.xaml.cs:224 còn chữ "drag-drop"; README/ARCHITECTURE/COMPARISON/AGENTS đều ghi ✅) — luồng chính quảng bá mà không có; alternative có thật: nút Add files (multiselect) + Ctrl+V (FileDrop/text-path, quét folder).
    - P1: demo jobs `AddDemoJobs` nạp 6 file giả %TEMP% MỖI lần mở (đã xác minh KHÔNG auto-in; in ra loạt lỗi chứ không ra giấy rác; file không bao giờ dọn); **approval flow là DEAD CODE** (AddForApproval/ApproveJob/RejectJob không call site nào; print_files trả APPROVAL_REQUIRED; AwaitingApproval unreachable — nặng hơn claim "chỉ MCP dùng"); popover Update+Security hardcode XAML literal (PrintGuard KHÔNG wire vào UI); banner lỗi không nút đóng + OnJobStateChanged showBanner per-error đè nhau + chỉ Retry với lỗi retryable (`HideBanner` chỉ gọi :556); duplex ShortEdge mất khi save→reload (model bool); EN/VI trộn; 3 đường in quy tắc khác nhau (PrintSelected lọc Queued/Không in lại Done-Error; comment+hint tự mâu thuẫn code); không empty state; dark mode quảng bá mà code light-only (nhiều #FFFFFF hardcode); **copies UI không chặn trên** (9999 bản in thẳng, MCP clamp 100 nhưng UI không); **xóa job đang in không dừng bản in thật** (RemoveJob chỉ gỡ khỏi list; engine CancellationToken.None); AddFiles không whitelist extension (.exe/.zip → shell printto); watcher "tự nạp bản mới" chỉ sample 1 lần sau 8s.
    - P2/P3: pill 1 màu; #8A8A8A ≈3.1:1 fail AA (dùng cho hint 10-11px); sort header/PagesCell không keyboard + FocusVisualStyle x:Null (MainWindow.xaml:304, PrinterConfigWindow.xaml:45); AutomationProperties hạ xuống P2 (chỉ 6/27 test FlaUI; WPF tự expose x:Name→AutomationId); ép giá trị im lặng (copies→1, zoom clamp, empty range→"All" in TOÀN BỘ; OK PrintSettings KHÔNG chặn range sai — chỉ PageRangeDialog chặn); PrinterConfigWindow không chọn máy; title/hint sai mô hình; token violation; search không placeholder; emoji icon.
    - Điểm mạnh giữ nguyên: hint dưới mỗi option; page-range live preview (PageRangeDialog) chặn OK; lỗi 3 lớp code+TV+hint; label đa chọn ngữ cảnh; toast/progress+taskbar/badge.
  - Skill áp dụng (web-design-guidelines 12 rule CONFIRM/REFINE + 4 finding mới: AutomationProperties.LiveSetting, native-control dark-mode readiness, confirm/undo cho destructive, typography tabular/ellipsis; huashu-design B1-B6: anti-slop, "placeholder trung thực > hiện thực giả", token màu giữ 1 nguồn, typography floor ≥12px + Faint#8A8A8A→Muted#6B6B6B, signature detail = drop-zone).
  - Roadmap đề nghị 3 đợt (P1/P2 hoàn thiện): ĐỢT 1 "nói thật" (bỏ demo jobs, DnD dùng lại paste handler + empty state + drop overlay, sửa duplex, banner 3 variant + đóng được, popover nối event thật hoặc xóa, copies cap, whitelist ext, watch range chặn ở OK); ĐỢT 2 "một luồng" (gộp 3 đường in thành 1 hàm quyết định, nối approval strip + batch approve + TTL (Core method có sẵn — chỉ dead code), persist queue qua restart, pre-flight gate (tổng tờ/offline/file mất), UI Stop/Cancel lô, Việt-first); ĐỢT 3 "tin cậy" (a11y: focus + keyboard + LiveSetting + Name; token cleanup + AA; docs đồng bộ). CẮT (over-engineering): i18n resx, dark mode full, AAA, drag-reorder queue.
  - Đã xác minh "ĐÃ CÓ": bulk retarget máy in (ApplySelectedPrinter ép lên mọi job); CancelPending/CancelJob/MCP cancel_job (server có, UI chưa); PrintGuard pre-flight chỉ ở MCP path (UI in thẳng).
- Baseline: build 0 warn/0 err; test 89/89 PASS (Core 62 + UI 27).
- NOTE: visual review bằng ảnh chụp CHƯA làm được — 2 task chạy app+chụp (basher) bị abort môi trường; audit dựa trên source-verified + 89/89 tests. Chụp ảnh thủ công khi user chạy app hoặc khi làm đợt 1.
- Remaining (theo roadmap): user chọn mục nào để bắt đầu (mặc định khuyến nghị ĐỢT 1 — quick-wins, bỏ dữ liệu giả).
- Blocked: —

## 2026-08-27 (release v0.1.3) — printer-scan robustness + installer 61.4→5.6MB
- Mode: REUSE — NARROW P1 (printer delta) + FULL plan (installer redesign) qua 2 thinker
- Subsystems: PrinterService scan, MainWindow printer dropdown, PrinterConfigWindow, installer Inno, release.yml, UI test harness
- Completed:
  - Printer list fix (root cause thật: catch timeout RỖNG → dropdown rỗng câm không banner/retry; scan treo vô hạn; PrinterConfigWindow sync treo UI). Bounded scan per-printer 4s + budget 15s; máy treo → placeholder "Không phản hồi"; mọi kết cục dispatch; generation counter; backstop 20s; PrinterConfigWindow async.
  - Installer 61.4→5.6MB: bỏ gói runtime; DownloadTemporaryFile on-demand khi máy thiếu .NET (SHA verify); RollForward Major; skip khi /VERYSILENT; release.yml step verify SHA.
  - UI test harness: AppPath sửa net8.0-windows (exe STALE) → net8.0-windows10.0.19041.0.
- Test: build 0/0 (Release+Debug), Core 72/72, format sạch, UI 25/27 — 2 fail (App_Launches_Empty, MultiSelect) pre-existing (A/B chứng minh KHÔNG phải regression).
- Remaining: user test trên máy Avast + máy thiếu .NET → publish draft release CI.
- Blocked: —

## 2026-08-28 14:10 (session v0.1.8) — in tuần tự + dừng lô + thứ tự hiển thị
- Mode: REUSE SKIP P1 (subsystem đã map) → BOUNDED từng bước
- Subsystems: PrintQueue (drain), SpoolPrintEngine (wait job xong), MainWindow (batch flow, display order)
- Completed:
  - In TUẦN TỰ từng file (như Print Conductor): bỏ ProcessExisting chạy DrainOnceAsync song song (gốc rễ "cả lô Converting đồng loạt") → 1 vòng drain duy nhất (KickDrain + _drainRunning guard) đi chung cho Enqueue (MCP) + ProcessBatch (UI).
  - STOP-ON-ERROR: 1 file lỗi → queue tự Pause, các file sau giữ Queued chờ Resume (không đốt giấy); UI báo OnBatchStopped thay vì popup "in xong"; Resume xóa cờ _stoppedByError.
  - Pause có nghĩa thật (dừng giữa file); nút 3 trạng thái Print/Pause-đỏ/Resume-xanh giữ nguyên.
  - Print All + "In file này" (context menu) + in file chọn → chung ProcessBatch (cùng logic).
  - Thứ tự in bám theo thứ tự HIỂN THỊ (sort + group thư mục): OrderByDisplay() quét đệ quy view.Groups — trước đây dùng thứ tự chèn Jobs → "file dưới cùng in trước".
  - SpoolPrintEngine.WaitForPrintCompletion (poll spooler) — mỗi file chờ job in thật XONG mới tới lượt file kế.
  - Quảng cáo labm.io.vn đã nằm sẵn README + AboutWindow (ship kèm).
- Test: Core 76/76 (thêm 4 regression: tuần tự / dừng-lô / resume / pause giữa file), Spool 4/4, build 0/0, format sạch. UITests 3 fail = pre-existing env (A/B đã chứng minh trên v0.1.7).
- Remaining: user đã test OK → commit + push + release v0.1.8 (CI tự build + changelog từ git log).
- Blocked: —

## 2026-08-29 (session i18n) — Đa ngôn ngữ 5 thứ tiếng (VI/EN/ZH/RU/JA) — FULL 6-phase
- Mode: FULL (Phase 1: 6 agents explore; Phase 2: 3 thinker + critic; Phase 3: hạ tầng + 7 editor agents port; Phase 4: 2 basher verify; Phase 5 fix loop; Phase 6 này)
- Subsystems: UI i18n (toàn bộ 7 window), Inno Setup đa ngôn ngữ, tools/check_i18n.ps1, UITest
- Completed:
  - **Kiến trúc**: JSON catalog 1 file (`Strings.json` 327 key × 5 ngôn ngữ) embedded — KHÔNG satellite/resx (đơn giản hơn, key-set 5 ngôn ngữ luôn khớp). L10n (lookup lang→vi→key, format theo culture), CultureResolver (env PRINTONATOR_LANGUAGE > registry > vi-VN; nhận cả 2-chữ ISO), LocExtension `{l10n:Loc}` XAML, App.OnStartup bỏ StartupUri set culture trước render. Keys.cs nested auto-gen chống typo.
  - **Dịch chuẩn không AI-slop**: vi = nguồn sự thật (Việt-first sweep 37 key trộn EN → VN); en dịch xong + round-trip QA (agent bắt 7 lỗi: số nhiều, "default", thuật ngữ collation vs N-up, ngữ pháp); zh/ru/ja dịch theo glossary in ấn + placeholder parity verify từng ngôn ngữ. Kết quả: 327 key × 5 ngôn ngữ parity 100%.
  - **Port**: 7 window XAML 240 `{l10n:Loc}` + code-behind ~125 `L10n.S/F`. ComboBox Tag (enum) giữ nguyên, chỉ dịch Content. Sentinels giữ literal ("mặc định", "Không phản hồi").
  - **Inno Setup**: `[Languages]` 5 (vi/en/zh-CN/ru-RU/ja-JP qua GetLangTag map), ghi `HKCU\Software\Printonator\Language`, `[CustomMessages]` 5 ngôn ngữ cho MsgBox .NET runtime, copy 3 .isl (ChineseSimplified/Russian/Japanese). ISCC build PASS.
  - **AboutWindow**: thêm language combo + restart (đường lui cài nhầm ngôn ngữ).
  - **Gate**: `tools/check_i18n.ps1` (key parity, placeholder parity, phát hiện "---" chưa dịch, chống diacritics VN lọt EN). PASS toàn bộ.
- Test: Core **78/78**, Spool **4/4**, UI **24/27** (3 fail pre-existing env — A/B chứng minh KHÔNG regression i18n trên baseline clean). Smoke 5 ngôn ngữ: app mở cả 5, không crash, render CJK/Cyrillic OK (ảnh i18n/smoke_*.png + final_*.png). Build sln 0 warn/0 err.
- **HOÀN TẤT (2026-08-29)**: Verify cuối qua basher — build 0 error, dotnet format SẠCH, smoke lại 5 ngôn ngữ sau bump OK, ISCC compile PASS (.iss v0.1.9), gate check_i18n PASS, đúng 3 UI fail pre-existing baseline (không regression mới). Layout hardening RU (ui-ux-specialist: FooterHint/FooterStats/BulkFullText +TextTrimming, PrintConfirm warning +Wrap; 7 điểm khác kiểm không cần sửa). Version bump 0.1.8→**0.1.9** (csproj + iss) + release notes viết tay v0.1.9 (template). **COMMIT `ded89e3`** (53 files, +5145/-418) — working tree sạch. Dọn ảnh smoke khỏi git `8059044` (user xóa local).
- **RELEASE (2026-08-29)**: Push `06ae6b0..87c7618` lên origin/main + tag **v0.1.9** (annotation, từ origin/main) → CI Release workflow đang chạy (build installer + SHA256 + minisign + GitHub Release draft). Bên trong commit `ded89e3` có kèm cải tiến MCP/error-routing: `ExtractPrintError` giữ lỗi engine cụ thể (không re-wrap SPOOLER_FAILED), tool `approve_job`/`reject_job` + `list_jobs` lọc awaitingapproval + `save_preset` + `get_guard_config`, luồng approval MCP thật (AddForApproval call site), mã lỗi mới (PRESET_NOT_FOUND/INVALID_PRESET/JOB_NOT_FOUND/JOB_NOT_APPROVABLE), docs/MCP.md.
- Blocked: —

## 2026-08-30 10:40 (SE Asia Standard Time) (session 20260830)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-08-30 10:42 (SE Asia Standard Time) (session 20260830)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-08-30 10:43 (SE Asia Standard Time) (session 20260830)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-08-30 10:43 (SE Asia Standard Time) (session 20260830)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-08-30 10:45 (SE Asia Standard Time) (session 20260830)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-08-30 10:45 (SE Asia Standard Time) (session 20260830)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-08-30 10:47 (SE Asia Standard Time) (session 20260830)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-08-30 10:49 (SE Asia Standard Time) (session 20260830)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-08-30 10:52 (SE Asia Standard Time) (session 20260830)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-08-30 10:55 (SE Asia Standard Time) (session 20260830)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-08-30 10:57 (SE Asia Standard Time) (session 20260830)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-08-30 11:03 (SE Asia Standard Time) (session 20260830)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-08-30 11:09 (SE Asia Standard Time) (session 20260830)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-08-30 11:10 (SE Asia Standard Time) (session 20260830)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-08-30 11:13 (SE Asia Standard Time) (session 20260830)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-08-30 11:22 (SE Asia Standard Time) (session 20260830)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-08-30 11:26 (SE Asia Standard Time) (session 20260830)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-08-30 11:34 (SE Asia Standard Time) (session 20260830)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-08-30 11:35 (SE Asia Standard Time) (session 20260830)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-08-30 11:40 (SE Asia Standard Time) (session 20260830)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-08-30 11:43 (SE Asia Standard Time) (session 20260830)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-08-30 11:44 (SE Asia Standard Time) (session 20260830)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-09-04 15:48 (SE Asia Standard Time) (session 20260904)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-09-04 16:11 (SE Asia Standard Time) (session 20260904)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-09-04 16:13 (SE Asia Standard Time) (session 20260904)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-09-04 (session docs-screenshots) — sửa ảnh minh họa lỗi trong README + USER_GUIDE
- Mode: FULL (P1 explore + verify vision; P4 review bằng script verify ad-hoc; Phase 6 kết thúc)
- Subsystems: docs (README.md, docs/USER_GUIDE.md, docs/screenshots/*)
- Completed:
  - Nguyên nhân: main_full.png (4.5KB, đen) + main_full_highlight.png (7KB, overlay đỏ trên nền đen — phần app mất) — ảnh chụp hỏng từ trước, không hiển thị gì.
  - Chụp lại ảnh THẬT: chạy app Debug (registry tạm Full+vi-VN, backup + khôi phục sau) + instance 2 truyền file demo qua named pipe; queue.json tạm rời để ảnh không lộ file thật của user ("BIÊN NHẬN PHIẾU KẾT QUẢ - NHÔM LÂM ĐỒNG...") — verify bằng vision OCR vùng queue.
  - main_full.png: 131KB, giao diện thật, queue chỉ 3 file demo (hop-dong-mau.txt / bien-ban-nghiem-thu.png / bao-cao-thang.txt).
  - main_full_highlight.png: toạ độ 5 nút từ UIAutomation (PrinterCombo x=498, PrintSettingsBtn x=1401, WatchFoldersBtn x=1701, PrintMainBtn x=1966, Thêm file x=325 — window L=285 T=106) → vẽ ô đỏ + badge số bằng Python PIL (font Segoe UI Bold 26); verify vision: từng ô khớp đúng nút (1=Thêm file, 2=vùng máy in, 3=Cài đặt in, 4=Theo dõi, 5=In tất cả (3)).
  - Bảng số USER_GUIDE sửa theo thứ tự trái→phải (trước để 2=Cài đặt in, 3=Máy in — sai vị trí).
  - README trỏ ảnh sạch main_full.png (không highlight); xóa 4 ảnh/tmp lỗi cũ (_crop_buttons, _crop_top, _test_mapping, _gray_cols.txt).
  - PITFALL: PowerShell 5.1 đọc .ps1 UTF-8 không BOM theo ANSI → chuỗi "Thêm file" vỡ encoding; lần sau script phải ASCII-only hoặc BOM. UIAutomation FindAll + bounding box dùng được nhưng ComboBox bị miss khi filter theo TopRel — lấy trực tiếp AutomationId.
  - Verify: script ad-hoc (temp hermes-verify-*) — mọi image ref tồn tại, main_full không đen (brightness 239.6), highlight có đủ red pixels (2253), temp artifacts sạch. Build Debug 0 lỗi trước đó.
  - Commit 4863e36 + push origin/main (kéo theo luôn 2 commit release đang chờ: 3017002 v0.2.3 + a878e39 v0.2.4 — CHƯA tag nên CI chưa tạo release).
- Remaining: tag v0.2.4 nếu muốn phát hành (release notes tay trước theo .github/release_notes.template.md).
- Blocked: —

## 2026-09-04 16:15 (SE Asia Standard Time) (session 20260904)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-09-04 16:16 (SE Asia Standard Time) (session 20260904)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-09-04 16:42 (SE Asia Standard Time) (session 20260904)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-09-04 16:44 (SE Asia Standard Time) (session 20260904)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-09-07 09:41 (SE Asia Standard Time) (session 20260907)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-09-07 09:43 (SE Asia Standard Time) (session 20260907)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-09-07 09:51 (SE Asia Standard Time) (session 20260907)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-09-07 09:51 (SE Asia Standard Time) (session 20260907)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-09-07 09:52 (SE Asia Standard Time) (session 20260907)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-09-07 09:52 (SE Asia Standard Time) (session 20260907)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-09-07 10:27 (SE Asia Standard Time) (session 20260907)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-09-07 11:47 (SE Asia Standard Time) (session 20260907)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-09-07 11:50 (SE Asia Standard Time) (session 20260907)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-09-07 (session race-fix — spam Pause/Resume kẹt "Converting mãi")
- Mode: BOUNDED (audit race + fix theo plan user duyệt) — không commit workflow files
- Subsystems: Core (PrintQueue drain loop), UI (MainWindow.OnJobStateChanged), Spool (GdiPrintEngine)
- Completed:
  - Root cause: (1) JobStateChanged fire sync từ drain thread + UI handler Dispatcher.Invoke (block) → UI bận (modal PrintDoneWindow/MessageBox/spam click) → drain treo vô hạn, job Converting mãi; subscriber ném exception → _drainRunning kẹt true → queue chết vĩnh viễn. (2) GDI pd.Print() blocking không nhận token → driver máy in treo = Converting mãi.
  - Fix: DrainLoopAsync bọc try/catch/finally (finally luôn nhả _drainRunning + MarkAllUnfinishedAsError đưa job dở về Error); FireJobStateChanged nuốt lỗi subscriber (4 call site); OnJobStateChanged Invoke→BeginInvoke; pause-wait Task.Delay(200, _cts.Token); GDI WaitAsync(120s)→EngineTimeout.
  - +3 test regression (spam pause/resume, subscriber ném, remove converting job).
- Verify: build 0 lỗi; Core 106/106; Spool 4/4; Mcp 6/6; UI 25/27 (2 fail pre-existing env SendInput); format sạch.
- Commit cfb271d → pushed origin/main (4863e36..cfb271d).
- Remaining: 3 file release đang dở local (release_notes.template.md, printonator.iss, Printonator.UI.csproj) — chờ release v0.2.5.

## 2026-09-07 12:11 (SE Asia Standard Time) (session 20260907)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-09-07 12:13 (SE Asia Standard Time) (session 20260907)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-09-10 (release v0.2.7) — fix in PDF ra PDF in đúng (không copy, không browser) + Excel/Word/PPT ra PDF tự lưu
- Mode: BOUNDED (fix loop theo feedback user) + REUSE (release prep)
- Subsystems: Spool (GdiPrintEngine/PdfImageWriter/PrinterService/OfficeComPrintEngine), UI (MainWindow), tests, release
- Completed:
  - PDF → máy ảo (Microsoft Print to PDF): **browserless render** — `ExportPdfDirectAsync` (GdiPrintEngine) rasterize qua Windows.Data.Pdf (300 DPI) → `PdfImageWriter` (mới) ghi file PDF MỚI (JPEG DCTDecode, khổ trang theo DIPs/96*72). KHÔNG copy file gốc (copy giữ nguyên chữ ký số = lỗ hổng bảo mật — user chốt), KHÔNG spawn Chrome/Edge (không UI browser — verify 3 cách trên máy user: Chrome 152 headless 0 cửa sổ).
  - OfficeComPrintEngine.PdfOutputArgs: resolve sentinel "mặc định" → tên máy THẬT trước khi kiểm tra máy ảo → Excel/Word/PPT ra PDF tự lưu cạnh file gốc (ExportAsFixedFormat), hết hộp "Save As" lọt ra (bug user báo "hiện ô nhập vị trí lưu").
  - PrinterService: `PdfOutputPath(printerName, filePath)` overload; MainWindow.DefaultPaperFor PDF → AsDocument (giữ khổ gốc, không ép A4).
  - Verify: E2E harness temp (PDF có /Sig giả → output SẠCH /Sig//ByteRange//AcroForm, content giữ, 1 trang, không phải copy); Spool **11/11** (thêm PdfImageWriter ×2 + PdfOutputPath resolve), Core **106/106**, BrowserEngine **17/17**, build 0 err.
  - Release v0.2.7: bump (csproj+iss) + notes tay; commit `7a2b6de`; push origin master; tag v0.2.7 → CI run 34479373797 SUCCESS (installer 6.17MB + SHA256 95c485... + minisig) → publish Latest (gh release edit --draft=false; đã xóa nhánh copy bị user bác).
- Remaining: — (test trên máy in vật lý lần cuối nếu chưa)
- Blocked: —

## 2026-09-10 (release v0.2.6) — fix in PDF đúng chiều + in ra PDF đúng cấu hình
- Mode: BOUNDED (fix loop nhiều vòng theo feedback user) + REUSE (release prep)
- Subsystems: Spool (GdiPrintEngine/BrowserPrintEngine/CdpPrintParams/WindowsPdfRasterizer), tests, release
- Completed:
  - GDI in PDF: Landscape explicit dùng cờ DEVMODE (`DefaultPageSettings.Landscape = true`) thay vì swap paper (driver clamp custom paper → FAIL); vẽ theo `e.Graphics.VisibleClipBounds` thay `PrintableArea` (PrintableArea vẫn trả khổ dọc khi Landscape bật → co nhỏ + lệch góc dưới phải). Portrait explicit auto-rotate ảnh landscape 90° vừa tờ dọc.
  - Máy in ảo + PDF nguồn: bỏ copy thẳng file → BrowserPrintEngine render đúng cấu hình (page range/chiều/scale); probe PageCount trước slicing; `FitToPaper` thu nhỏ vừa khổ tờ user chọn, giữ chiều đọc (không xoay 90°); bọc try/catch `File.Copy` khi file xuất bị lock → lỗi mềm.
  - Verify: probe in thật ra "Microsoft Print to PDF" (Portrait→trang dọc shrink đúng chiều, Landscape→ngang, AsDocument→giữ khổ gốc); Spool 8/8, Core 106/106, BrowserEngine 17/17, build 0 warn/0 err.
  - Release: bump 0.2.5→0.2.6 (csproj+iss) + release notes tay; commit 52f7761; push master→main; tag v0.2.6 → CI run 34443787874 SUCCESS (installer + SHA256 1ac592f8... + minisig) → publish Latest (không còn draft).
- Remaining: verify lại trên máy in vật lý lần cuối.
- Blocked: —

## 2026-09-07 (release v0.2.5) — push CI + draft release
- Mode: REUSE (quy trình release đã map) — BOUNDED
- Subsystems: release (.github/release_notes.template.md, setup/printonator.iss, Printonator.UI.csproj), CI
- Completed:
  - Viết release notes tay (5 Đã sửa + 1 Cải tiến, tiếng Việt cho người dùng) — gộp nội dung 47a1e1c (PDF 1 trang/duplex/Xóa file) + a878e39 (engine fallback) + race-fix v0.2.5 (spam Pause/Resume kẹt Converting, máy in treo 2 phút timeout).
  - Bump version 0.2.4 → 0.2.5 (iss + csproj, anh đã sửa sẵn).
  - Commit f063e16 → push master→main (cfb271d..f063e16) + tag v0.2.5 (annotated) → CI Release chạy.
- Verify: workflow 34091578556 SUCCESS (build 3m3s, Core+Spool gate xanh); draft release v0.2.5 tạo với body đúng + SHA256 thật fb4ecde0... + asset printonator-setup.exe + .minisig (ký minisign).
- Remaining: anh duyệt draft v0.2.5 trên GitHub → Publish → Latest (chưa tự publish).

## 2026-09-07 13:40 (SE Asia Standard Time) (session 20260907)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-09-07 15:09 (SE Asia Standard Time) (session 20260907)
- Mode: [auto-logged — see context.md for details]
- NOTE: This entry was auto-generated by the six-phase plugin.
  Fill in the specific subsystems and mode after the session.

## 2026-10-06 — fix duplex DOCX (mất trang) + collation + i18n `&amp;`
- Mode: FULL
- Subsystems: Spool (OfficeComPrintEngine, PrinterDialogs, GdiPrintEngine), UI (PrintSettingsWindow), Localization (Strings.json), tools/check_i18n.ps1
- Completed:
  - **A (lỗi nặng, mất trang)**: in DOCX ở chế độ 2 mặt custom (lật cạnh dài/ngắn) chỉ ra 1 tờ 1 mặt. Nguyên nhân: `OfficeComPrintEngine` truyền `ManualDuplexPrint: job.Config.Duplex` vào `doc.PrintOut` — đây là tính năng in 2 mặt THỦ CÔNG 2 lượt của Word cho máy KHÔNG có duplex kit, không phải cờ hướng lật; trên máy đã bật duplex thì Word chỉ in trang đầu (Microsoft KB xác nhận nhóm lỗi này). Đã xoá tham số ở cả 2 call site. Hướng lật cạnh giờ đặt qua DEVMODE driver (`dmDuplex` offset 94, `DM_DUPLEX=0x1000`, Simplex=1/LongEdge=2/ShortEdge=3 — đo thực trên máy in thật) qua `PrinterDialogs.ApplyDuplex` trả lease `IDisposable` khôi phục giá trị cũ; áp cho Word/Excel/PPT, bỏ qua nhánh PDF ảo. Thêm `RestoreActive()` chống rò lease khi timeout 60s.
  - **B**: `PrintCollation` trước đây là option chết (không engine nào đọc) → nay Office truyền `Collate:` ở 6 chỗ, GDI chọn ảnh theo ByPages, combo bỏ item "AsPrinter" giả còn 2 lựa chọn thật.
  - **C**: 20 giá trị trong `Strings.json` (vi/zh/ru/ja) chứa literal `&amp;` hiện nguyên văn trên UI → thay bằng `&`; thêm luật chống rò entity vào `check_i18n.ps1` (cả khối `vi`).
  - Test: `dotnet build` 0 lỗi; 178/180 test pass (2 fail là FlaUI UITests CÓ SẴN — đã A/B worktree trên HEAD sạch xác nhận fail 4/4, không phải regression); `DuplexDevModeTests` 8/8.
- Remaining / next: xem mục mới trong `docs/BACKLOG.md` (audit toàn cục phát hiện nhiều option chết/nửa vời; bug i18n bulk bar).
- Blocked / residual: chưa kiểm chứng trên máy in thật rằng (1) Word có đọc lại DEVMODE driver sau khi set ActivePrinter không; (2) `SetPrinter` level-2 ghi được với user thường không. User sẽ tự in thử. Nếu (1) sai → duplex custom không tác dụng cho DOCX nhưng KHÔNG còn mất trang (nhờ đã xoá ManualDuplexPrint).

## 2026-10-29 — Rà soát toàn app + tối ưu (FULL)
- Mode: FULL (P1: file-picker + pipeline trace + 3 audit code-reviewer + research; P2: chọn plan theo mức độ; P3: sửa song song; P4: build+test 7 vòng qua basher)
- Subsystems: Core (PrintQueue), Spool (Office/GDI/Spool/LibreOffice/Watermark), Mcp (Program/PrintTools), UI (MainWindow/PrintSettingsWindow), docs, CI, setup
- Completed:
  - **Security**: Bearer token HTTP MCP (`PRINTONATOR_MCP_TOKEN`); chặn command injection tên máy in (`ValidPrinterName` + 13 test); LibreOffice dùng `ArgumentList` thay ghép chuỗi.
  - **Stability**: STA thread Office không còn bị bỏ rơi (Join 3s sau kill → finally dọn COM + restore DEVMODE); `_drainRunning` chỉ nhả ở finally ngoài (hết race 2 drain); `AllJobsCompleted` invoke ngoài lock (hết deadlock); snapshot `Jobs` khi enumerate dưới lock; `WrapError` map đúng FileNotFound/IOException/UnauthorizedAccess; `SpoolPrintEngine` kill+wait+dispose process an toàn.
  - **UI**: `SelectedPrinter` → DependencyProperty + bind TwoWay; unsubscribe event handler khi đóng; `EmptyState` có AutomationId trên TextBlock (FlaUI tìm được); UI test hermetic qua env `PRINTONATOR_LANGUAGE=vi`.
  - **Option chết đã wire**: PaperSource → GDI `DefaultPageSettings.PaperSource`; ScaleMode/Zoom → `GdiPrintEngine.ZoomFactor`; WatermarkPosition → combo 5 vị trí + 6 key i18n × 5 ngôn ngữ; Watermark tôn trọng page range/parity; Booklet bỏ khỏi UI (chưa engine nào imposition thật).
  - **PowerPoint khổ giấy/chiều**: `PrinterDialogs.ApplyPaperOrientation` đặt dmOrientation/dmPaperWidth/dmPaperLength qua DEVMODE (không đổi SlideWidth/Height) + `RestoreActiveDevMode` cho timeout/cancel; `PaperDevModeTests` 4 test.
  - **Config/docs**: version đồng bộ 0.2.12 (Mcp/plugin/WiX); installer thêm Printonator.Mcp (4 file) + stage CI/build-setup; CI chạy Spool+Mcp+i18n; .NET 10→8; ARCHITECTURE/CONCEPT bỏ module giả (PDFium, Formats.*, Cli).
  - **Test**: build Release + Debug 0 error / 0 warning; **200/200 pass** (Core 106, Spool 60, Mcp 6, UI 28); i18n gate PASS 452 key × 5 ngôn ngữ.
- Remaining: xem `docs/BACKLOG.md` mục "Từ rà soát 2026-10-29" — Watermark/Merge vẫn bỏ Copies/N-up/Quality, PPT không áp PaperSize/Orientation, i18n sót chuỗi cứng.
- Commit `2d97d83` (bảo mật/ổn định/option chết/config) + `4857793` (MCP trả APPROVAL_REQUIRED) + `169bfe5` (PowerPoint khổ giấy/chiều qua DEVMODE) — đã push origin/main.
- Blocked / phát hiện lớn: **UI và MCP là 2 tiến trình, 2 `PrintQueue` riêng** — job AI `AwaitingApproval` chỉ sống trong `AppServices.Queue`, UI không thấy (trừ khi UI tự restore từ queue.json). Cần quyết: IPC/queue.json chung, host MCP in-process, hoặc tắt RequireApprove khi standalone.

## 2026-11-14 — Fix "chọn in grayscale nhưng máy in vẫn in màu" (FULL)
- Mode: FULL. Subsystem: print settings → driver (`PrintConfig.ColorMode` → 6 engine in).
- Completed: `ColorMode` vốn đã được giữ đúng từ UI/MCP vào job rồi bị 5/6 engine bỏ rơi (chỉ GDI có đọc, mà `.NET` chỉ ghi `dmColor` khi driver khai báo `DM_COLOR` nên vẫn là no-op im lặng). Đã gộp `DevModeRestore` + `DuplexRestore` thành một `DevModeLease` duy nhất và một `ApplyDevMode` đặt duplex/khổ/chiều/màu trong một lượt (trả `Ok(null)` không mở máy in khi `AsPrinter`), gọi trước `SetPrinter`/`ActivePrinter` cho Word/Excel/PowerPoint (PowerPoint thêm `PrintOptions.PrintColorType`), giữ lease quanh LibreOffice + shell printto (fail rõ thay vì in sai màu im lặng), chuyển xám ngay lúc VẼ trong GDI (ImageAttributes + ColorMatrix — bảo đảm không phụ thuộc driver cho PDF), chèn CSS `filter:grayscale(1)` khi render browser/watermark/merge/slice, và sửa MCP `color` mặc định (trước đây mọi lệnh MCP không truyền màu đều bị ép Grayscale). Readback `dmColor` hạ xuống chẩn đoán-only. Build 0 warning/0 error, 216 test xanh (Core 106, Spool 75, Mcp 7, UITests 28).
- Hai gotcha ghi lại cho người sau: GDI+ nhân pixel như vector HÀNG (`[R G B A 1] × M`) nên trọng số luma phải hằng theo CỘT (bản đầu đặt theo hàng → đỏ thuần ra `(76,150,29)`); `System.Drawing.Common` .NET 8 KHÔNG có overload `DrawImage(Image, RectangleF, …, ImageAttributes)` — dùng dest `PointF[]`.
- Remains / next: `docs/BACKLOG.md:60-65` — nhánh full-PDF browser render chưa có HTML để chèn CSS xám; `dmColor` chưa verify trên máy in thật (cần 1 tờ có thanh đỏ 100%); `SetPrinter` level 2 dưới tài khoản non-admin chưa verify; CSS grayscale trong Chromium print pipeline chưa verify.

## 2026-11-14 đợt 2 — Rà soát option bị engine bỏ qua im lặng (FULL)
- Mode: FULL. Subsystem: print options → engines. Commit `d53b732` (17 file, +676/−27).
- Hoàn thành: audit `PrintConfig` × 6 engine theo đúng lớp bug của fix grayscale cùng ngày ("option lưu đúng nhưng engine im lặng bỏ qua"). Parity lẻ/chẵn giờ lọc THẬT ở Word (`Range:4` + danh sách trang)/Excel/PowerPoint qua `PrintJob.ApplyParity` + `ResolvePrintPages` (cố ý giữ nguyên `ResolvePhysicalPages` vì nó nuôi quota/progress và `BrowserPrintEngine.ResolveCount` GHI `job.PageCount`); engine không áp được (LibreOffice, shell printto, nhánh export-PDF của Office, Merge khi chưa biết số trang) TỪ CHỐI bằng mã lỗi mới `UNSUPPORTED_OPTION` thay vì in sai; config PDF tạm reset `Parity = All` để job render không fail ở bước spool của chính nó. Quality ghi `DMRES_*` âm và gate theo `DM_PRINTQUALITY` (không bao giờ ghi DPI dương — driver có thể từ chối cả DEVMODE ở `CreateDC`); xuất PDF grayscale sang máy in ảo chuyển xám lúc render, lỗi thì fail `EngineFailed`; browser render thất bại chỉ hard-fail khi config có option shell không làm được (default `AsDocument` vẫn fallback shell); Merge chỉ render trang được chọn.
- Test: build 0 warning / 0 error; **235 pass / 5 skip** (skip = `[DesktopFact]` UI có sẵn), 2 lần chạy giống nhau; `dotnet format --verify-no-changes` sạch.
- Remains / next: `docs/BACKLOG.md:67` (đợt 2) — tray (lưu tên thân thiện ≠ mã `DC_BINS`), scale (double-scale + 3/5 mode không biểu diễn được), N-up (`dmNup` private data), watermark trên file Office (Word COM không có API + `OfficeCom` thắng registry), Quality có thể no-op trên vài driver, `Copies` bị LibreOffice bỏ qua, export máy in ảo qua browser vẫn ra PDF màu.
- Nhắc lại luật nhà: **KHÔNG chạy `dotnet test` trần trên máy này** — luôn `dotnet build` rồi `dotnet test --no-build` (vstest treo).

## 2026-11-14 đợt 3 — Ẩn dòng "Màu"/"Hai mặt" khi máy in chắc chắn không làm được (FULL)
- Mode: FULL. Subsystem: printer capability → settings UI. Commit `e862e34` (6 file, +196/−13).
- Hoàn thành: `PrinterInfo.SupportsColor`/`SupportsDuplex` (bool, `false` gộp 4 tình huống khác nhau — driver báo không, probe capability 4s timeout, probe throw, record tổng hợp) đổi sang tri-state `CapabilitySupport { Unknown, NotSupported, Supported }` với `Unknown` làm mặc định (`Unknown = 0` nên `default` cũng Unknown). Panel Cài đặt in ẩn dòng Color/Duplex CHỈ khi capability chắc chắn `NotSupported` VÀ combo đang ở giá trị trung tính, và chỉ ở Lite mode; preset/job mang `Color`/`LongEdge` thì giữ dòng để user còn thấy và tắt được. Hai gotcha mapping được review bắt: collection capability RỖNG = "driver không báo" (Unknown, không phải không hỗ trợ), và enum member `OutputColor.Unknown`/`Duplexing.Unknown` là giá trị THẬT của driver dùng option riêng → phải check trước nhánh `NotSupported`. `SupportsColor/SupportsDuplex` thành computed `== Supported` nên `PickBestPrinter` giữ nguyên ngữ nghĩa cũ và payload MCP không đổi (không cần release note).
- Test: build 0 warning / 0 error; **246 pass / 5 skip** (skip = `[DesktopFact]` UI có sẵn); `dotnet format --verify-no-changes` sạch; chạy lại isolated ra cùng số. Test mới `tests/Printonator.Core.Tests/PrintCapabilityTests.cs` khoá default-Unknown.
- Remains / next: park vào `.agents-workflow/backlog.md` → cần chuyển sang `docs/BACKLOG.md` mục "Từ ẩn dòng Màu/Hai mặt theo khả năng máy in (2026-11-14, đợt 3)": tiêu đề nhóm vẫn là "Giấy & màu" khi dòng màu bị ẩn (`Strings.json:296`), `Settings.HintParity` còn nhắc "in 2 mặt trước" (`:346`), MCP vẫn export `color:false`/`duplex:false` cho `Unknown` (`PrintTools.cs:45-46,255-256`), mapping trong `PrinterService.Describe` chưa có unit test (cần seam `PrintQueue`).
- Cố ý từ chối (đừng "sửa lại"): auto-sync profile từ cấu hình máy in (readback `dmColor` không tin được → hazard giá trị cũ; nếu muốn thì làm nút one-shot "Lấy theo máy in" không ghi vào `PresetStore`), clamp config lúc Apply/in, và ẩn ở Full mode.

## 2026-11-14 — Chốt session: 4 đợt liên tiếp về "cài đặt in không có tác dụng" (FULL)
- Mode: FULL (4 task, cùng ngày). Subsystems: print settings → driver DEVMODE; option → 6 engine; printer capability → settings UI; printer defaults → settings form.
- Commits: `2013770` (màu in ép được xuống máy in — gộp lease DEVMODE, xám lúc vẽ ở GDI, CSS grayscale cho browser/watermark/merge/slice, MCP hết tự ép Grayscale) · `8e05a41` (màu ép cả với ảnh/TXT; MCP không tự ép 1 mặt, không kẹt DEVMODE) · `703d811` (docs: MCP ghi rõ bỏ trống duplex/color = theo máy in) · `4551e57` (docs: release notes bản kế) · `d53b732` (option bị engine bỏ qua im lặng — parity lẻ/chẵn lọc thật ở Word/Excel/PPT, quality `DMRES_*` gate theo `DM_PRINTQUALITY`, engine không áp được thì từ chối bằng `UNSUPPORTED_OPTION` thay vì in sai) · `e862e34` (capability tri-state `CapabilitySupport` — hết gộp 4 tình huống vào bool `false`; ẩn dòng Màu/Hai mặt chỉ ở Lite và chỉ khi chắc chắn NotSupported + combo trung tính) · `dc56404` (nút "Lấy theo máy in" đọc mặc định đang lưu của driver vào 4 combo) · `84088a9` (docs: release notes phủ 3 commit sau).
- Hoàn thành: cả 4 đợt đóng đúng một lớp bug — option được lưu đúng nhưng bị bỏ qua im lặng ở đâu đó trên đường ra máy in. Mọi chỗ không áp được giờ fail rõ (mã lỗi + gợi ý) thay vì in sai; panel cài đặt chỉ ẩn thứ máy in chắc chắn không làm được; user có một hành động tường minh để lấy snapshot cài đặt driver thay vì link ngầm bị đóng băng.
- Test (cuối session): build 0 warning / 0 error; **246 pass / 5 skip** (skip = `[DesktopFact]` có sẵn); `tools/check_i18n.ps1` PASS 455 key × 5 ngôn ngữ; `dotnet format --verify-no-changes` sạch.
- Remains / next: `.agents-workflow/backlog.md` (cần orchestrator chuyển sang `docs/BACKLOG.md`) — 4 dòng đợt 3 (nhãn nhóm "Giấy & màu" và hint parity khi dòng bị ẩn, MCP export `color:false`/`duplex:false` cho `Unknown`, mapping `Describe` chưa có test) + 3 dòng đợt 4 (`ReadPrinterDefaults` chưa có test, `PaperNameFromDm` null khi driver chỉ set `dmPaperSize`, nút thứ 3 clip ở 720px min width). `docs/BACKLOG.md` đã có sẵn các mục lớn hơn: tray ≠ mã `DC_BINS`, scale double-scale, N-up private data, watermark trên Office, `dmColor`/quality chưa verify trên máy thật, CSS grayscale trong Chromium chưa verify, `SetPrinter` trên tài khoản non-admin chưa verify.
- Luật nhà nhắc lại: **không chạy `dotnet test` trần** trên máy này (vstest treo) — luôn `dotnet build` rồi `dotnet test --no-build`. Cố ý từ chối (đừng "sửa lại"): auto-sync profile từ cấu hình máy in, clamp/rewrite `PrintConfig` lúc Apply/in, ẩn capability ở Full mode.

## 2026-11-14 đợt 5 — Xem trước tờ giấy sẽ in, cập nhật theo từng option (FULL, commit `8f52cbb`)
- Mode: FULL. Subsystem: UI print preview (mới) + `PrintJob` overload nhận config nháp.
- Hoàn thành: panel xem trước ở cột phải cửa sổ Cài đặt in (`PrintSettingsWindow.xaml:405`, rộng 248px, cột Auto nên ẩn thì co về 0) vẽ lại mỗi lần đổi option — hình tờ giấy theo chiều/khổ, lưới N-up, tint xám khi in grayscale, lật cạnh khi in hai mặt, số trang bị parity bỏ qua, và cảnh báo khi chưa biết số trang. Toàn bộ phép tính nằm trong model thuần `src/Printonator.UI/PrintPreviewModel.cs` (`Build(PrintJob?, PrintConfig)`, không WPF/IO) nên test được không cần UI thread — 16 case ở `tests/Printonator.UITests/PreviewModelTests.cs`. Hai luật dễ hiểu sai đã ghim: N-up chỉ được vẽ khi format PDF (`PagesPerSheetHonoured` — Word/Excel/PPT do COM engine tự phân trang), và `PageCount <= 0` thoát sớm sang "chưa biết số trang" thay vì gọi resolver. Cửa sổ nới lên `Width=1000 MinWidth=900` (trước 840/720).
- Test: 16 test model xanh; panel và chuỗi i18n xác nhận qua accessibility tree sống. **Chưa verify thị giác** — chụp ảnh màn hình trong session trả về trắng cho mọi cửa sổ WPF kể cả bản baseline `a88f663`, nên là giới hạn môi trường chụp chứ không phải hồi quy.
- Remains / next: cần một lượt xem thật trên màn hình (hình tờ giấy, lưới N-up, tint xám, 3 animation 180/150/140ms) — đã park một dòng vào `.agents-workflow/backlog.md`.

## 2026-11-14 đợt 6 — Sửa 2 lỗi thị giác phát hiện bằng ảnh chụp cửa sổ thật (BOUNDED)
- Mode: BOUNDED (commit `9a7fe90` — 4 file; docs `0860969`). Subsystem: UI print preview + hàng nút profile.
- Hoàn thành: (1) hàng 4 nút profile đổi `StackPanel Horizontal` → `WrapPanel` (`PrintSettingsWindow.xaml:114`), nút "Nhập" (`:127`, trước bị cắt hoàn toàn — chữ không vẽ, chỉ ~6px viền) giờ xuống dòng; `HorizontalScrollBarVisibility="Disabled"` trên ScrollViewer (`:99`) nay TƯỜNG MINH + comment vì nó là load-bearing (`Auto` cho bề rộng vô hạn → WrapPanel dồn 1 dòng → nút biến mất lại). (2) số trang chưa biết nay vẫn vẽ ĐỦ `perSheet` ô (`PrintPreviewModel.cs:89` + helper `Thumbs` `:113`) thay vì 1 ô — 4-up trước trông như "1 trang nhỏ giữa tờ trắng"; ternary `PagesPerSheetHonoured ? perSheet : 1` hoist thành `var chunk` `:80`, dùng lại ở `:106`. Test mới `Chua_biet_so_trang_van_ve_du_o_cua_to` (2/4/9). `.gitignore:7` thêm `*_wpftmp.*` (build WPF ngắt để lại csproj tạm ngoài bin/obj).
- Test: build 8/8 project, 0 warning 0 error; `PreviewModelTests` 19/19; `Core.Tests` 126/126; code review APPROVED (không Critical/Major).
- Mở khoá môi trường: chụp ảnh cửa sổ WPF trên máy này ĐÃ CHẠY — `SetProcessDpiAwarenessContext(-4)` + tạm đặt `HKCU\Software\Microsoft\Avalon.Graphics\DisableHWAcceleration=1` + `PrintWindow(hwnd,hdc,2)` (`PW_RENDERFULLCONTENT`), tìm cửa sổ bằng `EnumWindows` theo pid chứ không `MainWindowHandle` (dialog modal là top-level riêng). Recipe đầy đủ ghi ở `context.md` đợt 6.
- Remains / next: `backlog.md` — đóng dòng "nút GhostBtn thứ 3 bị clip" (hết điều kiện: `MinWidth` đã 720→900 từ đợt 5; hàng máy in `:240` vẫn là `StackPanel`, chưa wrap); dòng verify preview thu hẹp còn "3 animation + 2 fix đợt 6 chưa chụp lại"; thêm 1 dòng mới `GridFor` map `perSheet` 10..15 về lưới 3×3 (vẽ thiếu 1 ô, chưa chạm từ UI). Cố ý không sửa: caption "chỉ cho biết khổ giấy" ở 4-up nay hơi understated.
- Luật nhà nhắc lại: **không chạy `dotnet test` trần** — luôn `dotnet build` rồi `dotnet test --no-build`.

## 2026-11-14 đợt 7 — "Lấy theo máy in" im lặng khi driver chỉ set `dmPaperSize` (BOUNDED, commit `e166765`)
- Mode: BOUNDED — commit `e166765` (2 file: `PrinterDialogs.cs`, `PaperDevModeTests.cs`) + docs `14705ce` + `021b0f5`. Subsystem: DEVMODE paper-size read path (`PrinterDialogs` ↔ `PaperCatalog`). `main` vượt `origin/main` 17 commit, chưa push.
- Hoàn thành: nút "Lấy theo máy in" trước đây bỏ qua khổ giấy khi driver chỉ điền mã `dmPaperSize` (DMPAPER_*) mà để `dmPaperWidth`/`dmPaperLength` = 0 — combo giấy đứng ở "theo máy in" không giải thích. Thêm bảng mã chuẩn → tên khổ `PaperCatalog` (16 mã: Letter, Ledger, Legal, Executive, A3/A4/A5/A2/A6, B4, DL, C5, C3/C4/C6 envelope) và gói toàn bộ logic đọc vào một hàm test được `PaperNameFromDevMode`: kích thước thật (±2 mm) thắng, rơi về mã khi kích thước vắng / bằng 0 / không khớp — kiểm tra cờ đơn thuần TÁI TẠO đúng lỗi gốc vì nhiều driver bật cờ mà để giá trị 0. `DMPAPER_B5` = 13 là B5 JIS 182×257 còn catalog "B5" là ISO 176×250 (lệch 7 mm) nên CỐ Ý không map — ghi sai khổ vào cấu hình rồi in sai tệ hơn "không biết".
- Test: build 8/8 project 0 warning 0 error; `PaperDevModeTests` 41/41; Spool 128/128; Core 126/126; code review vòng 1 CHANGES REQUESTED (3 major) đã sửa hết.
- Remains / next: `ReadPrinterDefaults` (khung OpenPrinter/GetPrinter) vẫn chưa có test — cần seam `PrintQueue` hoặc máy in thật. Park 1 dòng backlog: lệch B5 JIS-vs-ISO giữa `OfficeComPrintEngine` (`WordPaperCode`/`ExcelPaperCode` map "B5"→13) và `PaperCatalog` (ISO) — cùng app in ra 2 khổ B5 khác nhau tùy engine; deferred vì sẽ đổi kết quả in của Office, cần verify máy in thật. Đóng dòng backlog "`PaperNameFromDm` null khi driver chỉ set `dmPaperSize`".
- Luật nhà nhắc lại: không chạy `dotnet test` trần (vstest treo) — luôn `dotnet build` rồi `dotnet test --no-build`.

