using Printonator.Spool.Printing;

namespace Printonator.Spool.Tests;

/// <summary>
/// Test parser đếm trang PDF raw (/Type + /Page) — thuần đọc file, không máy in, không browser.
/// Bug thật (Word 2016 + iTextSharp): PDF ghi "/Type/Page" KHÔNG space → parser cũ đếm 0 trang
/// → page range "1" rỗng → GDI bỏ in → job fail. Parser chạy internal qua InternalsVisibleTo.
/// </summary>
public class PdfPageCountTests
{
    [Fact]
    public async Task Dem_dung_1_trang_khi_PDF_ghi_Type_Page_khong_space()
    {
        var path = Path.Combine(Path.GetTempPath(), $"printonator_typepage_{Guid.NewGuid():N}.pdf");
        try
        {
            // Giả lập object tree: 1 /Pages (không được đếm) + 1 /Page (phải đếm).
            // Cả hai kiểu khoảng trắng đều có mặt: "/Type/Page" và "/Type /Pages".
            var content = "%PDF-1.4\n1 0 obj\n<< /Type/Pages /Count 1 /Kids [2 0 R] >>\nendobj\n"
                        + "2 0 obj\n<< /Type/Page /Parent 1 0 R >>\nendobj\n%%EOF\n";
            await File.WriteAllTextAsync(path, content);

            var count = await WindowsPdfRasterizer.CountPagesViaParserAsync(path, CancellationToken.None);

            Assert.Equal(1, count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Chap_nhan_khoang_trang_va_loai_PageLabels()
    {
        var path = Path.Combine(Path.GetTempPath(), $"printonator_typepage_{Guid.NewGuid():N}.pdf");
        try
        {
            var content = "%PDF-1.4\n"
                        + "1 0 obj\n<< /Type\n/Pages /Count 2 >>\nendobj\n"
                        + "2 0 obj\n<< /Type\t/Page >>\nendobj\n"
                        + "3 0 obj\n<< /Type\r\n/Page >>\nendobj\n"
                        + "4 0 obj\n<< /Type/PageLabels >>\nendobj\n"
                        + "%%EOF\n";
            await File.WriteAllTextAsync(path, content);

            var count = await WindowsPdfRasterizer.CountPagesViaParserAsync(path, CancellationToken.None);

            Assert.Equal(2, count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Dem_du_trang_khi_token_vat_qua_ranh_gioi_chunk()
    {
        // Chunk = 256 KB. Đặt "/Type /Page" đúng vào ranh giới để token bị cắt đôi giữa 2 lần đọc.
        var path = Path.Combine(Path.GetTempPath(), $"printonator_chunk_{Guid.NewGuid():N}.pdf");
        try
        {
            const int chunk = 256 * 1024;
            var sb = new System.Text.StringBuilder(chunk * 2);
            sb.Append("%PDF-1.4\n");
            // Đệm để "/Type /Page" bắt đầu lệch 4 byte so với mốc chunk → chữ "Page" rơi sang chunk sau.
            sb.Append(new string(' ', chunk - sb.Length - 4));
            sb.Append("/Type /Page >>\n");
            sb.Append(new string(' ', chunk));            // đảm bảo vượt qua chunk thứ hai
            sb.Append("2 0 obj\n<< /Type /Page >>\nendobj\n%%EOF\n");
            await File.WriteAllTextAsync(path, sb.ToString());

            var count = await WindowsPdfRasterizer.CountPagesViaParserAsync(path, CancellationToken.None);

            Assert.Equal(2, count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
