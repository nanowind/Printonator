using System.Globalization;
using System.IO;
using System.Text;
using Printonator.Core;
using Printonator.Core.IO;
using Printonator.Core.Models;
using Printonator.Core.Printing;

namespace Printonator.Spool.Printing;

/// <summary>
/// Nhãn hiển thị trên trang bìa — UI điền theo ngôn ngữ app (Spool KHÔNG reference UI);
/// null → mặc định tiếng Việt (fallback cho test/MCP). Field giữ nguyên placeholder {0}.
/// Giá trị nhãn KHÔNG được Esc khi ghép HTML: chúng là chuỗi catalog do mình viết (như bản
/// hardcode cũ) — Esc sẽ biến "—" và chữ có dấu thành numeric entity, đổi byte output.
/// </summary>
public sealed class CoverLabels
{
    public string Heading = "DANH SÁCH FILE IN";
    /// <summary>Tiền tố ghép TRƯỚC tiêu đề lô (tiêu đề do user đặt nằm sau, được Esc riêng).</summary>
    public string BatchPrefix = "Lô in — ";
    public string MarkTitle = "TRANG BÌA";
    public string BrandSub = "Phần mềm in hàng loạt";
    /// <summary>Dòng phần mềm trong khối: {0} = "Printonator &lt;version&gt;".</summary>
    public string SoftwareLine = "{0} — phần mềm in hàng loạt cho Windows";
    public string SoftwareBlock = "PHẦN MỀM";
    public string SoftwareMuted = "github.com/nanowind/Printonator &nbsp;·&nbsp; Giấy in được tạo tự động, không cần kiểm tra tay";
    public string MachineBlock = "MÁY YÊU CẦU IN";
    /// <summary>Environment.MachineName rỗng (hiếm) → nhãn này.</summary>
    public string MachineUnknown = "không rõ";
    /// <summary>Tên nhãn TRƯỚC dấu hai chấm — renderer tự thêm ":" (catalog giữ nhãn trần).</summary>
    public string MachineLabel = "Máy tính";
    public string PrinterLabel = "Máy in đích";
    public string TimeLabel = "Thời điểm in";
    public string SummaryBlock = "TỔNG HỢP";
    public string FilesLabel = "Số file";
    public string TotalPagesLabel = "Tổng trang";
    /// <summary>{0} = số file chưa rõ số trang.</summary>
    public string UnknownPagesFormat = "(+{0} file chưa rõ số trang)";
    public string SheetsLabel = "Tổng tờ (ước tính, đã nhân số bản)";
    public string PaperLabel = "Khổ giấy";
    public string ColIndex = "STT";
    public string ColFile = "Tên file";
    public string ColPages = "Trang";
    public string ColConfig = "Cấu hình in";
    public string ColPrinter = "Máy in riêng";
    /// <summary>{0} = tổng số file.</summary>
    public string TotalRowFormat = "TỔNG CỘNG — {0} file";
    /// <summary>{0} = tổng số tờ ước tính.</summary>
    public string SheetsTotalFormat = "{0} tờ (ước tính)";
    public string Note = "Số trang ghi trên đây là số trang của từng file; số tờ thực tế phụ thuộc cấu hình in và máy in.";
    public string PrinterDefault = "Máy in mặc định";
    /// <summary>Tiền tố khi lô trộn nhiều máy in — ghép tên cột "Máy in riêng" (MixedPrintersListFormat ghép danh sách tên máy).</summary>
    public string MixedPrinters = "nhiều máy in (xem cột Máy in riêng)";
    /// <summary>{0} = danh sách tên máy in (lô trộn máy nhưng KHÔNG có máy in riêng per-file).</summary>
    public string MixedPrintersListFormat = "nhiều máy in ({0})";
    /// <summary>{0} = số file không hiển thị được trong bảng.</summary>
    public string MoreFilesFormat = "… và {0} file nữa (xem danh sách đầy đủ trong phần mềm)";
    public string NoFiles = "(không có file)";
    /// <summary>Khổ giấy "theo tài liệu" (PaperCatalog.AsDocument).</summary>
    public string PaperAsDocument = "theo file";
    /// <summary>Dịch Config.SummaryText cho cột "Cấu hình in" (UI điền; null → token VN gốc).</summary>
    public Func<PrintConfig, string>? ConfigText;
}

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
        string? printerName,
        CoverLabels? labels = null)
    {
        labels ??= new CoverLabels();
        var dateText = now.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        var title = ResolveTitle(jobs, batchTitle, dateText);
        var machine = string.IsNullOrWhiteSpace(Environment.MachineName) ? labels.MachineUnknown : Environment.MachineName;
        var appLine = string.IsNullOrWhiteSpace(appVersion) ? "Printonator" : $"Printonator {appVersion.Trim()}";

        // Số trang SẮP IN của từng job (đã resolve page range + parity) — không phải PageCount toàn file.
        // null = chưa biết (PageCount chưa probe / range lỗi) → không cộng vào tổng, ô trang ghi "?".
        var perJobPages = jobs.Select(PagesToPrint).ToList();
        var totalPages = perJobPages.Sum(p => p ?? 0);
        var unknownPages = perJobPages.Count(p => p is null);
        var totalSheets = jobs.Sum(EstimatedSheets);
        var paper = ResolvePaper(jobs, labels);
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
        sb.Append("<div class=\"brand-sub\">").Append(labels.BrandSub).Append("</div>");
        sb.Append("</td></tr></table></td>");
        sb.Append("<td style=\"width:38%\"><div class=\"mark\"><b>").Append(labels.MarkTitle).Append("</b><span>")
          .Append(Esc(dateText)).Append("</span></div></td>");
        sb.Append("</tr></table>");

        sb.Append("<h1>").Append(labels.Heading).Append("</h1>");
        sb.Append("<p class=\"sub\">").Append(labels.BatchPrefix).Append(Esc(title)).Append("</p>");

        // ---- PHAN MEM ----
        sb.Append("<div class=\"block\"><h2>").Append(labels.SoftwareBlock).Append("</h2>");
        sb.Append("<p>").Append(string.Format(CultureInfo.InvariantCulture, labels.SoftwareLine, Esc(appLine))).Append("</p>");
        sb.Append("<p class=\"muted\">").Append(labels.SoftwareMuted).Append("</p>");
        sb.Append("</div>");

        // ---- MAY YEU CAU IN (khong in ten nguoi dung Windows — thong tin ca nhan roi khoi may) ----
        sb.Append("<div class=\"block\"><h2>").Append(labels.MachineBlock).Append("</h2>");
        sb.Append("<p><b>").Append(labels.MachineLabel).Append(":</b> ").Append(Esc(machine)).Append("</p>");
        sb.Append("<p><b>").Append(labels.PrinterLabel).Append(":</b> ").Append(Esc(ResolvePrinter(jobs, printerName, labels))).Append("</p>");
        sb.Append("<p><b>").Append(labels.TimeLabel).Append(":</b> ").Append(Esc(dateText)).Append("</p>");
        sb.Append("</div>");

        // ---- TONG HOP ----
        sb.Append("<div class=\"block\"><h2>").Append(labels.SummaryBlock).Append("</h2><p>");
        sb.Append("<b>").Append(labels.FilesLabel).Append(":</b> ").Append(jobs.Count).Append(" &nbsp;·&nbsp; ");
        sb.Append("<b>").Append(labels.TotalPagesLabel).Append(":</b> ").Append(totalPages);
        if (unknownPages > 0)
            sb.Append(' ').Append(string.Format(CultureInfo.InvariantCulture, labels.UnknownPagesFormat, unknownPages));
        sb.Append(" &nbsp;·&nbsp; ");
        sb.Append("<b>").Append(labels.SheetsLabel).Append(":</b> ").Append(totalSheets).Append(" &nbsp;·&nbsp; ");
        sb.Append("<b>").Append(labels.PaperLabel).Append(":</b> ").Append(Esc(paper));
        sb.Append("</p></div>");

        // ---- BANG FILE ----
        sb.Append("<table class=\"list\"><thead><tr>");
        sb.Append("<th class=\"c\" style=\"width:6%\">").Append(labels.ColIndex).Append("</th>");
        sb.Append("<th style=\"width:").Append(showPrinterCol ? 40 : 46).Append("%\">").Append(labels.ColFile).Append("</th>");
        sb.Append("<th class=\"c\" style=\"width:8%\">").Append(labels.ColPages).Append("</th>");
        sb.Append("<th style=\"width:").Append(showPrinterCol ? 34 : 40).Append("%\">").Append(labels.ColConfig).Append("</th>");
        if (showPrinterCol) sb.Append("<th style=\"width:12%\">").Append(labels.ColPrinter).Append("</th>");
        sb.Append("</tr></thead><tbody>");

        if (jobs.Count == 0)
        {
            sb.Append("<tr><td colspan=\"").Append(cols).Append("\">").Append(labels.NoFiles).Append("</td></tr>");
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
                var pages = perJobPages[i];
                sb.Append("<td class=\"c\">").Append(pages?.ToString(CultureInfo.InvariantCulture) ?? "?").Append("</td>");
                sb.Append("<td>").Append(Esc(labels.ConfigText?.Invoke(j.Config) ?? j.Config.SummaryText)).Append("</td>");
                if (showPrinterCol) sb.Append("<td>").Append(Esc(j.Config.PrinterName ?? "")).Append("</td>");
                sb.Append("</tr>");
            }

            if (jobs.Count > MaxRows)
                sb.Append("<tr><td colspan=\"").Append(cols).Append("\">")
                  .Append(string.Format(CultureInfo.InvariantCulture, labels.MoreFilesFormat, jobs.Count - MaxRows))
                  .Append("</tr>");
        }

        // Dong TONG CONG phai GOP O — de trong cot STT 6% thi chu vo dong.
        sb.Append("</tbody><tfoot><tr>");
        sb.Append("<td colspan=\"2\">")
          .Append(string.Format(CultureInfo.InvariantCulture, labels.TotalRowFormat, jobs.Count)).Append("</td>");
        sb.Append("<td class=\"c\">").Append(totalPages).Append("</td>");
        sb.Append("<td colspan=\"").Append(showPrinterCol ? 2 : 1).Append("\">")
          .Append(string.Format(CultureInfo.InvariantCulture, labels.SheetsTotalFormat, totalSheets)).Append("</td>");
        sb.Append("</tr></tfoot></table>");

        sb.Append("<p class=\"note\">").Append(labels.Note).Append("</p>");
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
    private static string ResolvePrinter(IReadOnlyList<PrintJob> jobs, string? printerName, CoverLabels labels)
    {
        var names = jobs.Select(j => j.Config.PrinterName ?? "")
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count > 1)
            return jobs.Any(j => j.HasPerFilePrinter)
                ? labels.MixedPrinters
                : string.Format(CultureInfo.InvariantCulture, labels.MixedPrintersListFormat, string.Join(", ", names));
        return string.IsNullOrWhiteSpace(printerName) ? labels.PrinterDefault : printerName.Trim();
    }

    /// <summary>Khổ giấy tổng: các khổ khác nhau trong lô, "theo file" cho AsDocument.</summary>
    private static string ResolvePaper(IReadOnlyList<PrintJob> jobs, CoverLabels labels)
    {
        var names = jobs.Select(j => j.Config.PaperSize)
            .Select(p => string.IsNullOrWhiteSpace(p) || p.Equals(PaperCatalog.AsDocument, StringComparison.OrdinalIgnoreCase)
                ? labels.PaperAsDocument
                : p)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return names.Count == 0 ? "—" : string.Join(", ", names);
    }

    /// <summary>Số trang SẮP IN của 1 job: đã resolve Config.PageRange + parity qua
    /// <see cref="CdpPrintParams.ResolveSelectedPages"/> (null = in toàn bộ → PageCount).
    /// PageCount chưa probe (≤0) hoặc resolve lỗi → null (bìa ghi "?", không cộng vào tổng).
    /// BuildHtml CHỈ tính — không mở file (probe đã làm ở tầng orchestrator).</summary>
    private static int? PagesToPrint(PrintJob j)
    {
        if (j.PageCount <= 0) return null;   // chưa probe được (TXT...) → "?", kể cả khi có range
        try
        {
            var pages = CdpPrintParams.ResolveSelectedPages(j);   // null = in tất cả (All + không lọc lẻ/chẵn / range lỗi)
            return pages is not null ? pages.Length : j.PageCount;
        }
        catch { return null; }
    }

    /// <summary>Tổng tờ ƯỚC TÍNH của 1 job = số trang SẮP IN × số bản ÷ số trang/tờ (làm tròn lên).
    /// Chưa biết số trang thực in → 0 (không đoán bừa).</summary>
    private static int EstimatedSheets(PrintJob j)
    {
        var pages = PagesToPrint(j);
        if (pages is not > 0) return 0;
        var copies = Math.Max(j.Config.Copies, 1);
        var perSheet = Math.Max(j.Config.PagesPerSheet, 1);
        return (int)Math.Ceiling(pages.Value * (double)copies / perSheet);
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
