# Product

<!-- impeccable:product-schema 1 -->

## Platform

windows

Native Windows 10/11 desktop app (WPF, C#/.NET 8). Not a web experience; no mobile variant planned. Per-product record (not the web default) because the schema's four options do not name this platform.

## Users

- **Primary:** Vietnamese office/administrative workers — thu kế toán in hóa đơn, đội kỹ thuật/hiệu chuẩn in GCN hàng loạt, văn phòng đấu thầu nén bộ hồ sơ ra giấy. Their job: get many documents onto paper correctly and reliably, per-document rules, without tediously repeating Ctrl+P dozens of times.
- **Secondary (growing):** AI assistant users — Claude Code, Hermes, or another MCP client that prints *for* the person ("in giùm") via the MCP server. Also general Windows professionals who batch-print.
- **Not targeted:** large-scale industrial printing, printer-fleet management.

## Product Purpose

Drop N files → pick a printer → configure once → print the whole batch, knowing exactly which file printed, which failed, and why. The reason it exists: commercial bulk-printers (Print Conductor, priPrinter, FinePrint) are paid per-machine and constrained; homeworkers/offices get a free, open, AI-native alternative.

## Positioning

**In hàng loạt mà lỗi không bao giờ bị nuốt — và AI in giùm được qua MCP.** The batch-printing engine is the product's clarity foundation: correct per-document settings (page range, 2-sided, paper), section-aware page mapping for DOCX, true printer state, and every failure surfaced with a code + a readable reason + a fix suggestion. On top of that — unlike any commercial competitor — AI can print on the user's behalf through MCP (`print_files`, presets, audit). The two halves share one Core.

## Operating Context

- **Bulk print workflows:** drop/Ctrl+V/add many files; select one or a group; apply per-file or per-batch config through the Bulk bar; watch the job table, progress bar, and toasts; every job reaches Done or Error(reason). Session log records printer, format, timestamps, status.
- **Section-aware page ranges (DOCX):** reads `sectPr` structure, builds a physical-page ↔ (section, in-section page) map; `All`, `2,5`, `3-4`, `1-2,7`, `S2:1-3` all supported with a live "→ Will print physical pages" preview. Prevents the classic "in trang 3 hóa ra là trang 1 section 2" mistake.
- **Printer reality:** named printers with true status (available/offline/error), paper sizes, duplex/color/tray capabilities — surfaced before printing, never wrong data about a printer.
- **Engine chain, dynamic per machine (no bundled libs):** MS Office (Word/Excel/PowerPoint COM — preserves original page setup/section) → LibreOffice (`soffice --headless`) → browser render (Chrome/Edge headless CDP — real page ranges, scale, paper size for PDF/image/TXT) → shell `printto` spurter. PDF page ranges use Windows.Data.Pdf (WindowsPdfRasterizer) to slice → HTML → printToPDF.
- **MCP server** runs in-process in the UI or standalone headless: `list_printers`, `print_files`, `print_with_preset`, `get/save_preset`, `list_jobs`, `job_status`, `cancel_job` — HTTP `:3939/mcp` (loopback only, no CORS) or `--stdio`.

## Capabilities and Constraints

- **Confirmed features (shipped):** drag-&-drop / Ctrl+V / "+ Add files" multi-select; bulk apply (copies, 2-sided, paper, color); per-file page ranges incl. section-aware; per-file or shared printer+config; native "Printing Preferences / Printer Properties" dialogs (printui.dll); search & sort; toast + notifications bell; preset save/reuse; MCP guardrail toolset.
- **AI-safety guardrails (confirmed design):** printer allowlist, approve mode (default ON — AI cannot self-print), page-quota per batch (200) + daily cap, audit log, fail-closed, loopback-only. Terms: `PRINTONATOR_REQUIRE_APPROVE`, `PRINTONATOR_ALLOWED_PRINTERS`, `MAX_PAGES_PER_BATCH`, `MAX_COPIES_PER_FILE`.
- **Local-first (binding):** 100% local command; no cloud, no telemetry; print data never leaves the machine. This is a hard constraint.
- **Error-handling philosophy (binding):** never swallow an exception. Every failure becomes a localized error (code, Vietnamese message, fix suggestion) routed to event/queue; app vs. config vs. printer errors distinguished clearly. Error codes exist today (`PRINTER_OFFLINE`, `INVALID_PAGE_RANGE`…).
- **Open decision — language parity:** product ships Vietnamese-first today (UI copy, error messages, docs in Vietnamese; English planned). The chosen direction is bilingual-from-start (see Brand Commitments); full i18n are not yet implemented. PDF page-range slicing to real rendered output is deferred to a later milestone.

## Brand Commitments

- **Name:** Printonator (existing logo asset: `printonatorLogo.png`).
- **License (stated):** MIT — free/open-source. *Note: LICENSE file is absent in the repo today; the MIT claim in README/CONCEPT is not yet recorded on disk.*
- **Voice:** concrete, no-fluff, user-first; Vietnamese error copy is direct and helpful ("gợi ý gọi lỗi"), not generic.
- **Headline identity (chosen):** batch-printing clarity is what the product is known for first; MCP/AI-prints-for-you is the differentiator that deepens it, not the leading claim.
- **Language commitment (chosen, forward):** English is to be a first-class peer, not a deferred v2 phase. This is directional — implementation (i18n) is not yet built.

## Evidence on Hand

- Real copy and feature descriptions live in `README.md`, `CONCEPT.md` (vision, personas, sections 2–3, 6–7), and `docs/`.
- Incumbent visual system recorded in `docs/DESIGN_SYSTEM.md` (Notion-style, monochrome), with a Penpot comparison in `docs/COMPARISON_PENPOT.md` and Print Conductor gap analysis in `docs/COMPARISON_PRINT_CONDUCTOR.md`. These record the current look but are not a DESIGN.md.
- Logo: `printonatorLogo.png`.
- No testimonials, customers, benchmarks, or press exists on disk — none should be fabricated.

## Product Principles

1. **Reliability over flash.** Batch printing is a packed-job task; the correctness of "which page on which printer" and honest error signaling outrank visual flourish.
2. **Errors are a product surface, not a failure mode.** Every failure gets a code + readable reason + fix suggestion, shown in the UI and returned to AI over MCP at the same time. Never swallow.
3. **Use the machine the user has.** Prefer the machine's native app (MS Office COM, LibreOffice, browser) so original document settings survive; keep the app lightweight by never bundling print libraries.
4. **Local-first is non-negotiable.** No cloud, no telemetry; when AI prints, guardrails (allowlist, approval, quota, audit) are on by default and fail closed.
5. **One Core, many front doors.** UI, MCP, and CLI share the Core engine and state machine — the AI experience is not a bolt-on but the same queue.

## Accessibility & Inclusion

- Target audience includes office workers who are not developers — the app must remain discoverable for non-technical Vietnamese users (direct language, clear labels, explicit hints under each setting).
- Full i18n (Vietnamese + English) is a stated goal though not yet implemented; bilingual-start is the confirmed direction.