using Printonator.Core.Models;
using Printonator.Spool.Printing;

namespace Printonator.Spool.Tests;

/// <summary>
/// Test cho HTML trang bìa (CoverPageRenderer.BuildHtml) — hàm THUẦN, không I/O, không máy in,
/// không browser. Assert trên CHUỖI HTML trả về (không parse DOM) để chạy nhanh trên CI windows-latest.
/// </summary>
public class CoverPageTests
{
    private static readonly DateTime Now = new(2026, 9, 22, 14, 30, 0);

    /// <summary>Job giả gọn — mặc định 1 bản, 1 trang/tờ, chung thư mục C:\lo\in.</summary>
    private static PrintJob Job(
        string name,
        int pageCount = 0,
        int copies = 1,
        int perSheet = 1,
        bool perFilePrinter = false,
        string dir = @"C:\lo\in",
        string? printer = null,
        string paper = PaperCatalog.AsDocument) => new()
        {
            FilePath = Path.Combine(dir, name),
            FileName = name,
            Format = "PDF",
            Config = new PrintConfig
            {
                Copies = copies,
                PagesPerSheet = perSheet,
                PaperSize = paper,
                PrinterName = printer,
            },
            PageCount = pageCount,
            HasPerFilePrinter = perFilePrinter,
        };

    private static string Build(IReadOnlyList<PrintJob> jobs, string? title = "Lô tháng 9",
        string? version = "1.2.3", string? printer = null) =>
        CoverPageRenderer.BuildHtml(jobs, title, Now, version, printer);

    /// <summary>Phần giữa &lt;tbody&gt; và &lt;/tbody&gt; — tránh đếm nhầm &lt;tr&gt; của thead/tfoot.</summary>
    private static string Tbody(string html)
    {
        var start = html.IndexOf("<tbody>", StringComparison.Ordinal);
        var end = html.IndexOf("</tbody>", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "HTML thiếu <tbody>");
        return html[(start + "<tbody>".Length)..end];
    }

    private static int Count(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            n++;
        return n;
    }

    [Fact]
    public void BuildHtml_NoiDungCoBan_CoDuTenFileVaTieuDe()
    {
        var html = Build([Job("bao-cao.pdf"), Job("bang-luong.xlsx"), Job("hop-dong.docx")]);

        Assert.Contains("DANH SÁCH FILE IN", html);
        Assert.Contains("TRANG BÌA", html);
        Assert.Contains("bao-cao.pdf", html);
        Assert.Contains("bang-luong.xlsx", html);
        Assert.Contains("hop-dong.docx", html);
        Assert.Contains("<b>Số file:</b> 3", html);
        Assert.Contains("TỔNG CỘNG — 3 file", html);
        // Tiêu đề lô do user đặt — chữ có dấu bị HtmlEncode thành numeric entity
        Assert.Contains("Lô in — L&#244; th&#225;ng 9", html);
    }

    [Fact]
    public void BuildHtml_TenFileCoKyTuDacBiet_DuocEscape()
    {
        var html = Build([Job("a&b<c>\"d\".pdf")]);

        // Esc dùng WebUtility.HtmlEncode → ASCII thành entity
        Assert.Contains("a&amp;b&lt;c&gt;&quot;d&quot;.pdf", html);
        Assert.DoesNotContain("<c>", html);   // không được để thẻ thô lọt vào HTML
    }

    [Fact]
    public void BuildHtml_150Job_ChiHien100DongVaBaoConLai()
    {
        var jobs = Enumerable.Range(1, 150).Select(i => Job($"file-{i:D3}.pdf")).ToList();
        var html = Build(jobs);
        var body = Tbody(html);

        // 100 dòng dữ liệu (mỗi dòng bắt đầu bằng <tr><td class="c">STT)
        Assert.Equal(100, Count(body, "<tr><td class=\"c\">"));
        Assert.Equal(101, Count(body, "<tr>"));   // + 1 dòng báo còn lại
        Assert.Contains("… và 50 file nữa", html);
        Assert.DoesNotContain("file-101.pdf", html);
        // Tổng cộng vẫn tính theo TOÀN BỘ lô, không theo số dòng hiển thị
        Assert.Contains("TỔNG CỘNG — 150 file", html);
    }

    [Fact]
    public void BuildHtml_CotMayInRieng_AnKhiKhongCo_HienKhiCo()
    {
        var an = Build([Job("a.pdf"), Job("b.pdf")]);
        Assert.DoesNotContain("Máy in riêng", an);
        Assert.Contains(">Tên file</th>", an);

        var hien = Build([Job("a.pdf"), Job("b.pdf", perFilePrinter: true)]);
        Assert.Contains("<th style=\"width:12%\">Máy in riêng</th>", hien);
    }

    [Fact]
    public void BuildHtml_PageCountChuaBiet_HienDauHoiVaKhongCongVaoTong()
    {
        var html = Build([Job("biet.pdf", pageCount: 5), Job("chua-biet.pdf", pageCount: 0)]);

        Assert.Contains("<td class=\"c\">5</td>", html);
        Assert.Contains("<td class=\"c\">?</td>", html);          // KHÔNG được hiện "1"
        Assert.Contains("<b>Tổng trang:</b> 5 (+1 file chưa rõ số trang)", html);
        // File chưa biết trang KHÔNG được cộng vào tổng tờ (0 trang → 0 tờ)
        Assert.Contains("<b>Tổng tờ (ước tính, đã nhân số bản):</b> 5", html);
    }

    [Fact]
    public void BuildHtml_TongTo_NhanSoBanVaChiaSoTrangTrenTo()
    {
        // 10 trang × 2 bản ÷ 1 trang/tờ = 20 tờ
        var mot = Build([Job("a.pdf", pageCount: 10, copies: 2)]);
        Assert.Contains("<b>Tổng tờ (ước tính, đã nhân số bản):</b> 20", mot);

        // 10 trang × 2 bản ÷ 2 trang/tờ = 10 tờ (làm tròn lên)
        var hai = Build([Job("a.pdf", pageCount: 10, copies: 2, perSheet: 2)]);
        Assert.Contains("<b>Tổng tờ (ước tính, đã nhân số bản):</b> 10", hai);

        // 3 trang × 1 bản ÷ 2 trang/tờ = 1.5 → ceil = 2 tờ
        var le = Build([Job("a.pdf", pageCount: 3, perSheet: 2)]);
        Assert.Contains("<b>Tổng tờ (ước tính, đã nhân số bản):</b> 2", le);
    }

    [Fact]
    public void BuildHtml_NhungLogoDangDataUri()
    {
        var html = Build([Job("a.pdf")]);
        Assert.Contains("data:image/png;base64,", html);
    }

    [Fact]
    public void BuildHtml_TieuDeRong_DungTenThuMucChung()
    {
        var html = CoverPageRenderer.BuildHtml(
            [Job("a.pdf", dir: @"C:\du-an\bao-cao-thang-9")], null, Now, null, null);

        Assert.Contains("Lô in — bao-cao-thang-9", html);
        Assert.Contains("Printonator</div>", html);   // không có version → vẫn có dòng phần mềm
    }

    [Fact]
    public void BuildHtml_KhongCoFile_KhongThrow()
    {
        var html = CoverPageRenderer.BuildHtml([], null, Now, null, null);

        Assert.Contains("(không có file)", html);
        Assert.Contains("TỔNG CỘNG — 0 file", html);
        Assert.Contains("22/09/2026 14:30", html);   // title fallback = ngày giờ
    }

    [Fact]
    public void BuildHtml_KhongDungFlexbox()
    {
        // Hồi quy: flex căn giữa làm Chromium cắt/nhảy trang khi nội dung dài hơn 1 trang
        var html = Build([Job("a.pdf")]);
        Assert.DoesNotContain("display:flex", html);
        Assert.DoesNotContain("display: flex", html);
    }

    [Fact]
    public void BuildHtml_CssChongNgatTrang()
    {
        var html = Build([Job("a.pdf")]);
        Assert.Contains("display:table-header-group", html);   // header bảng lặp khi sang trang
        Assert.Contains("break-inside:avoid", html);
    }

    [Fact]
    public void SanitizeFileName_LocKyTuCam_CatDai_RongThiMacDinh()
    {
        var s = CoverPageRenderer.SanitizeFileName("a:b\\c/d*e?f\"g<h>i|j");
        Assert.Equal("a_b_c_d_e_f_g_h_i_j", s);
        // Ký tự Windows cấm (bỏ ký tự điều khiển — chuỗi test không chứa chúng)
        foreach (var c in Path.GetInvalidFileNameChars().Where(c => !char.IsControl(c)))
            Assert.DoesNotContain(c.ToString(), s);
        Assert.NotEmpty(s);

        var dai = CoverPageRenderer.SanitizeFileName(new string('a', 80));
        Assert.Equal(60, dai.Length);

        Assert.Equal("Trang bia", CoverPageRenderer.SanitizeFileName(null));
        Assert.Equal("Trang bia", CoverPageRenderer.SanitizeFileName(""));
        Assert.Equal("Trang bia", CoverPageRenderer.SanitizeFileName("   "));
        Assert.Equal("Trang bia", CoverPageRenderer.SanitizeFileName("..."));
    }

    [Fact]
    public void BuildHtml_TenFileTrungKhacThuMuc_HienThemThuMuc()
    {
        var html = Build([
            Job("bao-cao.pdf", dir: @"C:\du-an\A"),
            Job("bao-cao.pdf", dir: @"C:\du-an\B"),
        ]);

        Assert.Contains("<div class=\"sub-file\">A</div>", html);
        Assert.Contains("<div class=\"sub-file\">B</div>", html);
    }

    // ===== PageCountProber — đếm số trang TRƯỚC khi in (trang bìa dựng trước lô) =====

    [Fact]
    public async Task ProbeAsync_Pdf2Trang_DemDuocTruocKhiIn()
    {
        var dir = Path.Combine(Path.GetTempPath(), "printonator-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // 2 trang PNG giả → PdfImageWriter (pattern E2ePrintTests)
            byte[] Png(int r)
            {
                using var bmp = new System.Drawing.Bitmap(94, 150);
                using (var g = System.Drawing.Graphics.FromImage(bmp)) { g.Clear(System.Drawing.Color.FromArgb(r, 200, 200)); }
                using var ms = new MemoryStream();
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                return ms.ToArray();
            }
            var pdf = Path.Combine(dir, "2-trang.pdf");
            PdfImageWriter.Write(pdf, [new(Png(200), 94, 150), new(Png(80), 94, 150)]);

            var job = Job("2-trang.pdf", dir: dir);   // Format=PDF, PageCount=0, file THẬT tồn tại
            await PageCountProber.ProbeAsync([job], default);

            Assert.Equal(2, job.PageCount);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ProbeAsync_AnhLaLuon1Trang_KhongCanMoFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "printonator-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Ảnh probe = 1 không mở file → nội dung bytes không quan trọng, chỉ cần File.Exists
            var png = Path.Combine(dir, "an.png");
            await File.WriteAllBytesAsync(png, [1, 2, 3]);

            var job = new PrintJob
            {
                FilePath = png,
                FileName = "an.png",
                Format = "PNG",
                Config = new PrintConfig(),
                PageCount = 0,
            };
            await PageCountProber.ProbeAsync([job], default);

            Assert.Equal(1, job.PageCount);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ProbeAsync_KhongCoGiCanDem_TraVeNhanh_KhongThrow()
    {
        // Mọi job đã có PageCount → không rơi vào nhánh nào, return ngay, không mở app
        var daBiet = Job("a.pdf", pageCount: 5);
        await PageCountProber.ProbeAsync([daBiet], default);
        Assert.Equal(5, daBiet.PageCount);

        await PageCountProber.ProbeAsync([], default);   // rỗng — không throw
    }
}
