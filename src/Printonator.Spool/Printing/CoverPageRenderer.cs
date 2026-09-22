using System.Globalization;
using System.IO;
using System.Text;
using Printonator.Core;
using Printonator.Core.IO;
using Printonator.Core.Models;
using Printonator.Core.Printing;

namespace Printonator.Spool.Printing;

/// <summary>
/// Trang bìa in trước lô (CoverPage): dựng HTML 1 trang → headless browser (CDP printToPDF)
/// → PDF tạm → in qua GDI engine tới máy đã chọn. Không nuốt lỗi — render/in hỏng
/// trả PrintError rõ ràng để queue dừng-đúng-chỗ (không đốt giấy phần sau lô).
/// </summary>
public static class CoverPageRenderer
{
    /// <summary>Tên resource logo (khớp LogicalName trong Printonator.Spool.csproj).</summary>
    private const string LogoResource = "Printonator.Spool.Assets.logo.png";

    /// <summary>Cap số dòng bảng file — bìa dài quá chỉ tốn giấy, danh sách đầy đủ đã có trong phần mềm.</summary>
    private const int MaxRows = 100;

    /// <summary>Tên thiết bị dành riêng của Windows (CON/PRN/NUL/COM1...) → không tạo được file, phải đổi tên.</summary>
    private static readonly string[] ReservedNames =
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
         "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    /// <summary>Dựng HTML trang bìa: tab nhận diện góc trên (logo + khối TRANG BÌA tô đậm) → tiêu đề lô
    /// → khối phần mềm / máy yêu cầu in / tổng hợp → bảng file (lặp header khi sang trang) → tổng cộng.
    /// Không flexbox căn giữa: flex + nội dung dài hơn 1 trang làm Chromium cắt/nhảy trang sai.</summary>
    public static string BuildHtml(
        IReadOnlyList<PrintJob> jobs,
        string? batchTitle,
        DateTime now,
        string? appVersion,
        string? printerName)
    {
        var dateText = now.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        var title = ResolveTitle(jobs, batchTitle, dateText);
        var machine = string.IsNullOrWhiteSpace(Environment.MachineName) ? "không rõ" : Environment.MachineName;
        var appLine = string.IsNullOrWhiteSpace(appVersion) ? "Printonator" : $"Printonator {appVersion.Trim()}";

        var totalPages = jobs.Sum(j => j.PageCount > 0 ? j.PageCount : 0);
        var unknownPages = jobs.Count(j => j.PageCount <= 0);
        var totalSheets = jobs.Sum(EstimatedSheets);
        var paper = ResolvePaper(jobs);
        var showPrinterCol = jobs.Any(j => j.HasPerFilePrinter);
        var cols = showPrinterCol ? 5 : 4;
        var dupNames = jobs.GroupBy(j => j.FileName, StringComparer.OrdinalIgnoreCase)
                           .Where(g => g.Count() > 1)
                           .Select(g => g.Key)
                           .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var logo = LogoDataUri();

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>");
        sb.Append(Css);
        sb.Append("</style></head><body><div class=\"sheet\">");

        // ---- TAB NHAN DIEN GOC TREN-PHAI: logo/tên phần mềm | khối TRANG BÌA tô đậm ----
        sb.Append("<table class=\"tab\"><tr><td style=\"width:62%\"><table class=\"brandtab\"><tr>");
        if (logo.Length > 0)
            sb.Append("<td><img src=\"").Append(logo).Append("\" alt=\"Printonator\"></td>");
        sb.Append("<td style=\"padding-left:9px\">");
        sb.Append("<div class=\"brand\">Printonator</div>");
        sb.Append("<div class=\"brand-sub\">Phần mềm in hàng loạt</div>");
        sb.Append("</td></tr></table></td>");
        sb.Append("<td style=\"width:38%\"><div class=\"mark\"><b>TRANG BÌA</b><span>")
          .Append(Esc(dateText)).Append("</span></div></td>");
        sb.Append("</tr></table>");

        sb.Append("<h1>DANH SÁCH FILE IN</h1>");
        sb.Append("<p class=\"sub\">Lô in — ").Append(Esc(title)).Append("</p>");

        // ---- PHAN MEM ----
        sb.Append("<div class=\"block\"><h2>PHẦN MỀM</h2>");
        sb.Append("<p>").Append(Esc(appLine)).Append(" — phần mềm in hàng loạt cho Windows</p>");
        sb.Append("<p class=\"muted\">github.com/nanowind/Printonator &nbsp;·&nbsp; Giấy in được tạo tự động, không cần kiểm tra tay</p>");
        sb.Append("</div>");

        // ---- MAY YEU CAU IN (khong in ten nguoi dung Windows — thong tin ca nhan roi khoi may) ----
        sb.Append("<div class=\"block\"><h2>MÁY YÊU CẦU IN</h2>");
        sb.Append("<p><b>Máy tính:</b> ").Append(Esc(machine)).Append("</p>");
        sb.Append("<p><b>Máy in đích:</b> ").Append(Esc(ResolvePrinter(jobs, printerName))).Append("</p>");
        sb.Append("<p><b>Thời điểm in:</b> ").Append(Esc(dateText)).Append("</p>");
        sb.Append("</div>");

        // ---- TONG HOP ----
        sb.Append("<div class=\"block\"><h2>TỔNG HỢP</h2><p>");
        sb.Append("<b>Số file:</b> ").Append(jobs.Count).Append(" &nbsp;·&nbsp; ");
        sb.Append("<b>Tổng trang:</b> ").Append(totalPages);
        if (unknownPages > 0)
            sb.Append(" (+").Append(unknownPages).Append(" file chưa rõ số trang)");
        sb.Append(" &nbsp;·&nbsp; ");
        sb.Append("<b>Tổng tờ (ước tính, đã nhân số bản):</b> ").Append(totalSheets).Append(" &nbsp;·&nbsp; ");
        sb.Append("<b>Khổ giấy:</b> ").Append(Esc(paper));
        sb.Append("</p></div>");

        // ---- BANG FILE ----
        sb.Append("<table class=\"list\"><thead><tr>");
        sb.Append("<th class=\"c\" style=\"width:6%\">STT</th>");
        sb.Append("<th style=\"width:").Append(showPrinterCol ? 40 : 46).Append("%\">Tên file</th>");
        sb.Append("<th class=\"c\" style=\"width:8%\">Trang</th>");
        sb.Append("<th style=\"width:").Append(showPrinterCol ? 34 : 40).Append("%\">Cấu hình in</th>");
        if (showPrinterCol) sb.Append("<th style=\"width:12%\">Máy in riêng</th>");
        sb.Append("</tr></thead><tbody>");

        if (jobs.Count == 0)
        {
            sb.Append("<tr><td colspan=\"").Append(cols).Append("\">(không có file)</td></tr>");
        }
        else
        {
            var shown = Math.Min(jobs.Count, MaxRows);
            for (var i = 0; i < shown; i++)
            {
                var j = jobs[i];
                sb.Append("<tr><td class=\"c\">").Append(i + 1).Append("</td><td>");
                sb.Append(Esc(j.FileName));
                // Tên file trùng (khác thư mục) → thêm dòng phụ để phân biệt
                if (dupNames.Contains(j.FileName))
                    sb.Append("<div class=\"sub-file\">").Append(Esc(j.FolderLabel)).Append("</div>");
                sb.Append("</td>");
                sb.Append("<td class=\"c\">").Append(j.PageCount > 0 ? j.PageCount.ToString(CultureInfo.InvariantCulture) : "?").Append("</td>");
                sb.Append("<td>").Append(Esc(j.Config.SummaryText)).Append("</td>");
                if (showPrinterCol) sb.Append("<td>").Append(Esc(j.Config.PrinterName ?? "")).Append("</td>");
                sb.Append("</tr>");
            }

            if (jobs.Count > MaxRows)
                sb.Append("<tr><td colspan=\"").Append(cols).Append("\">… và ")
                  .Append(jobs.Count - MaxRows).Append(" file nữa (xem danh sách đầy đủ trong phần mềm)</td></tr>");
        }

        // Dong TONG CONG phai GOP O — de trong cot STT 6% thi chu vo dong.
        sb.Append("</tbody><tfoot><tr>");
        sb.Append("<td colspan=\"2\">TỔNG CỘNG — ").Append(jobs.Count).Append(" file</td>");
        sb.Append("<td class=\"c\">").Append(totalPages).Append("</td>");
        sb.Append("<td colspan=\"").Append(showPrinterCol ? 2 : 1).Append("\">")
          .Append(totalSheets).Append(" tờ (ước tính)</td>");
        sb.Append("</tr></tfoot></table>");

        sb.Append("<p class=\"note\">Số trang ghi trên đây là số trang của từng file; số tờ thực tế phụ thuộc cấu hình in và máy in.</p>");
        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    /// <summary>Render HTML trang bìa thành PDF base64 qua headless browser (dọn tempdir sau).
    /// cfg phải là config RIÊNG của bìa do caller truyền (caller ép A4/dọc/simplex).</summary>
    public static async Task<(bool Ok, string? Base64Pdf)> RenderCoverAsync(
        string html, PrintConfig cfg, CancellationToken ct)
    {
        var browser = new BrowserLocator().ResolveBrowser();
        if (browser is not { } b)
            return (false, null);

        using var temp = TempDir.Create("printonator-cover");
        var tempDir = temp.FullPath;
        {
            var htmlPath = Path.Combine(tempDir, "cover.html");
            await File.WriteAllTextAsync(htmlPath, html, ct);
            var (ok, base64, _) = await DevToolsPrintClient.PrintPdfAsync(
                b.Path,
                new Uri(htmlPath).AbsoluteUri,
                CdpPrintParams.Build(cfg, null),
                Path.Combine(tempDir, "profile"),
                ct);
            return (ok, base64);
        }
    }

    /// <summary>Ghi PDF trang bìa + in qua GDI engine (ảnh GDI thẳng máy in — KHÔNG cần print handler
    /// cho .pdf). <paramref name="printerName"/> có thể là sentinel "mặc định"/rỗng → resolve sang tên
    /// máy Windows TRƯỚC khi phân loại ảo/vật lý (sentinel không tự nhận ra máy ảo → mất bìa im lặng).
    /// Máy VẬT LÝ: ghi vào temp rồi dọn. Máy ẢO (PDF/XPS): engine ghi PDF xuất cạnh file nguồn → phải
    /// nằm trong <paramref name="coverOutputDir"/> BỀN, nếu để trong temp thì tempdir bị xóa đệ quy
    /// ngay sau khi in → MẤT BÌA không lỗi (bug đã xác minh). Tên file bìa có hậu tố "_trangbia" +
    /// chống trùng (không bao giờ ghi đè file có sẵn của người dùng).</summary>
    public static async Task<Result<bool>> PrintCoverAsync(
        string base64Pdf, string printerName, string? coverOutputDir, string coverTitle, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(base64Pdf))
            return Result<bool>.Fail(PrintErrorFactory.SpoolerFailed("Trang bìa rỗng — không in được."));

        // Sentinel "mặc định" phải resolve TRƯỚC khi phân loại ảo/vật lý — IsVirtualPrinter("mặc định")
        // = false (heuristic tên) nên bìa sẽ ghi vào temp rồi bị xóa khi máy default là PDF/XPS (bug đã xác minh).
        if (DefaultPrinter.IsDefault(printerName))
            printerName = DefaultPrinter.GetWindowsDefaultPrinterName() ?? printerName;

        TempDir? temp = null;
        try
        {
            string outPdf;
            // Phòng thủ 2 lớp: resolve xong vẫn là sentinel/rỗng mà có coverOutputDir → ưu tiên thư mục
            // bền (mất bìa tệ hơn ghi nhầm chỗ).
            var useOutputDir = !string.IsNullOrWhiteSpace(coverOutputDir)
                               && (PrinterService.IsVirtualPrinter(printerName)
                                   || DefaultPrinter.IsDefault(printerName)
                                   || string.IsNullOrWhiteSpace(printerName));
            if (useOutputDir)
            {
                // Ten file phai KHAC ten file nguon (engine them "_printonator" cho .pdf → khong de output cua file dau lo).
                Directory.CreateDirectory(coverOutputDir!);
                var baseName = SanitizeFileName(coverTitle) + "_trangbia";
                outPdf = Path.Combine(coverOutputDir!, baseName + ".pdf");
                // Phòng trùng: file đã tồn tại → thêm _1, _2... (không bao giờ ghi đè file có sẵn).
                var n = 1;
                while (File.Exists(outPdf))
                    outPdf = Path.Combine(coverOutputDir!, $"{baseName}_{n++}.pdf");
            }
            else
            {
                temp = TempDir.Create("printonator-cover-out");
                outPdf = Path.Combine(temp.FullPath, "out.pdf");
            }

            await File.WriteAllBytesAsync(outPdf, Convert.FromBase64String(base64Pdf), ct);

            var coverJob = new PrintJob
            {
                FilePath = outPdf,
                FileName = Path.GetFileName(outPdf),
                Format = "PDF",
                // Bia LUON 1 mat kho A4 doc: de mac dinh AsPrinter thi may 2 mat gop bia chung to voi trang 1.
                Config = new PrintConfig
                {
                    PrinterName = printerName,
                    Copies = 1,
                    DuplexMode = PrintDuplexMode.Simplex,
                    PaperSize = "A4",
                    Orientation = PrintOrientation.Portrait,
                },
            };
            // GdiPrintEngine: in ảnh trực tiếp, không phụ thuộc app/handler PDF trên máy.
            // Trả NGUYÊN Result (không nuốt) để caller báo được lỗi bìa.
            return await new GdiPrintEngine().PrintAsync(coverJob, ct);
        }
        finally
        {
            temp?.Dispose();
        }
    }

    /// <summary>Tên file an toàn từ tiêu đề bìa: bỏ ký tự Windows cấm, cắt 60 ký tự, rỗng → "Trang bia",
    /// trùng tên thiết bị dành riêng (CON/PRN/NUL/COM1...) → thêm tiền tố "_".</summary>
    internal static string SanitizeFileName(string? title)
    {
        var name = title ?? "";
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim().Trim('.');
        if (name.Length > 60) name = name[..60].TrimEnd();
        if (string.IsNullOrWhiteSpace(name)) return "Trang bia";
        return ReservedNames.Contains(name, StringComparer.OrdinalIgnoreCase) ? "_" + name : name;
    }

    /// <summary>Tiêu đề lô: user đặt → tên thư mục chung → ngày giờ (không bao giờ rỗng).</summary>
    private static string ResolveTitle(IReadOnlyList<PrintJob> jobs, string? batchTitle, string dateText)
    {
        if (!string.IsNullOrWhiteSpace(batchTitle)) return batchTitle.Trim();

        var dirs = jobs.Select(j => Path.GetDirectoryName(j.FilePath) ?? "")
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .ToList();
        if (dirs.Count == 1 && !string.IsNullOrWhiteSpace(dirs[0]))
        {
            var leaf = Path.GetFileName(dirs[0].TrimEnd('\\', '/'));
            if (!string.IsNullOrWhiteSpace(leaf)) return leaf;
        }
        return dateText;
    }

    /// <summary>Máy in đích: rỗng → mặc định; lô trộn nhiều máy → trỏ sang cột "Máy in riêng"
    /// (cột này chỉ có khi lô có máy in riêng — không có thì liệt kê tên máy).</summary>
    private static string ResolvePrinter(IReadOnlyList<PrintJob> jobs, string? printerName)
    {
        var names = jobs.Select(j => j.Config.PrinterName ?? "")
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count > 1)
            return jobs.Any(j => j.HasPerFilePrinter)
                ? "nhiều máy in (xem cột Máy in riêng)"
                : "nhiều máy in (" + string.Join(", ", names) + ")";
        return string.IsNullOrWhiteSpace(printerName) ? "Máy in mặc định" : printerName.Trim();
    }

    /// <summary>Khổ giấy tổng: các khổ khác nhau trong lô, "theo file" cho AsDocument.</summary>
    private static string ResolvePaper(IReadOnlyList<PrintJob> jobs)
    {
        var names = jobs.Select(j => j.Config.PaperSize)
            .Select(p => string.IsNullOrWhiteSpace(p) || p.Equals(PaperCatalog.AsDocument, StringComparison.OrdinalIgnoreCase)
                ? "theo file"
                : p)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return names.Count == 0 ? "—" : string.Join(", ", names);
    }

    /// <summary>Tổng tờ ƯỚC TÍNH của 1 job = số trang vật lý × số bản ÷ số trang/tờ (làm tròn lên).
    /// File chưa đếm được trang → 0 (không đoán bừa).</summary>
    private static int EstimatedSheets(PrintJob j)
    {
        if (j.PageCount <= 0) return 0;
        var resolved = j.ResolvePhysicalPages();
        var pages = resolved.IsSuccess && resolved.Value is { Length: > 0 } v ? v.Length : j.PageCount;
        var copies = Math.Max(j.Config.Copies, 1);
        var perSheet = Math.Max(j.Config.PagesPerSheet, 1);
        return (int)Math.Ceiling(pages * (double)copies / perSheet);
    }

    /// <summary>Logo nhúng thẳng dạng data URI — HTML ghi vào tempdir rồi render bằng headless browser
    /// nên đường dẫn tương đối không resolve được. Lỗi/thiếu logo → "" (bìa KHÔNG được chết vì logo).</summary>
    private static string LogoDataUri()
    {
        try
        {
            using var stream = typeof(CoverPageRenderer).Assembly.GetManifestResourceStream(LogoResource);
            if (stream is null) return "";
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch { return ""; }
    }

    private static string Esc(string? s) => System.Net.WebUtility.HtmlEncode(s) ?? "";

    /// <summary>CSS bám sát preview đã duyệt (tools/preview_trang_bia.html).
    /// KHÔNG đặt @page{margin} — CDP đã set margin 0.4in và lề 10mm > vùng không in được của máy laser.
    /// Bỏ khung .sheet cố định 794×1123px + nền xám (chỉ để xem trước trên màn hình): giữ tỷ lệ thật
    /// để Chromium tự phân trang, nếu không bảng dài sẽ bị cắt.</summary>
    private const string Css =
        "html,body{margin:0;padding:0;font-family:'Segoe UI',Arial,sans-serif;}" +
        ".sheet{background:#fff;color:#000;font-size:14px;line-height:1.35;padding:5mm 6mm;box-sizing:border-box;}" +
        ".tab{width:100%;border-collapse:collapse;}" +
        ".tab td{vertical-align:top;border:none;padding:0;}" +
        ".brand{font-size:17px;font-weight:700;color:#1B4F72;}" +
        ".brand-sub{font-size:12px;color:#444;}" +
        ".brandtab{border-collapse:collapse;}" +
        ".brandtab td{border:none;padding:0;vertical-align:middle;}" +
        ".brandtab img{display:block;width:36px;height:36px;}" +
        ".mark{background:#1B4F72;color:#fff;text-align:center;padding:5px 4px;border-radius:3px;}" +
        ".mark b{font-size:17px;display:block;letter-spacing:.5px;}" +
        ".mark span{font-size:12px;display:block;margin-top:2px;}" +
        "h1{font-size:23px;margin:14px 0 2px;}" +
        ".sub{font-size:13px;color:#444;margin:0 0 14px;}" +
        ".block{border:1px solid #000;padding:9px 11px;margin:0 0 12px;break-inside:avoid;page-break-inside:avoid;}" +
        ".block h2{font-size:12.5px;margin:0 0 5px;color:#1B4F72;letter-spacing:.4px;}" +
        ".block p{margin:0 0 2px;font-size:13px;}" +
        ".muted{font-size:12px;color:#444;}" +
        ".sub-file{font-size:9pt;color:#444;}" +
        "table.list{width:100%;border-collapse:collapse;font-size:13px;margin-top:2px;}" +
        "table.list th,table.list td{border:1px solid #000;padding:4px 6px;text-align:left;vertical-align:top;overflow-wrap:anywhere;}" +
        "table.list th{background:#E8E8E8;font-size:12.5px;}" +
        "table.list thead{display:table-header-group;}" +
        "table.list tr{break-inside:avoid;page-break-inside:avoid;}" +
        "table.list tfoot td{font-weight:700;background:#F2F2F2;}" +
        ".c{text-align:center;}" +
        ".note{font-size:11.5px;color:#444;margin-top:6px;}";
}
