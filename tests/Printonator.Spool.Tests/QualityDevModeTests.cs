using System.Runtime.InteropServices;
using Printonator.Core.Models;
using Printonator.Spool.Printing;

namespace Printonator.Spool.Tests;

/// <summary>
/// Test logic DEVMODE CHẤT LƯỢNG IN của PrinterDialogs — thuần byte-offset, không cần máy in thật.
/// Quan trọng nhất: WritePrintQuality chỉ được đụng dmPrintQuality@90 + cờ dmFields@72, KHÔNG được ghi
/// nhầm chỗ khác (struct sai offset sẽ ghi vào dmColor@92 hoặc hỏng dmCopies@86), và giá trị ghi vào
/// LUÔN âm (DMRES_*) — số dương là DPI, DPI driver không có làm driver từ chối cả DEVMODE.
/// </summary>
public class QualityDevModeTests
{
    private const int DevModeBytes = 220;   // dmSize đo được trên máy thật
    private const int DmSizeOffset = 68;
    private const int DmFieldsOffset = 72;
    private const int DmPrintQualityOffset = 90;
    private const int DM_PRINTQUALITY = 0x400;
    private const int DM_COLOR = 0x800;
    private const int DM_DUPLEX = 0x1000;

    [Fact]
    public void WritePrintQuality_chi_dung_dmPrintQuality_va_dmFields_khong_doc_gia_bytes_khac()
    {
        var p = Marshal.AllocHGlobal(DevModeBytes);
        try
        {
            // Đổ pattern dễ nhận ra vào toàn bộ buffer, rồi ghi header hợp lệ, rồi mới snapshot.
            var before = new byte[DevModeBytes];
            for (var i = 0; i < DevModeBytes; i++) before[i] = (byte)((i * 7 + 3) % 251);
            Marshal.Copy(before, 0, p, DevModeBytes);
            Marshal.WriteInt16(p, DmSizeOffset, DevModeBytes);
            Marshal.WriteInt32(p, DmFieldsOffset, 0);
            before = Snapshot(p);

            PrinterDialogs.WritePrintQuality(p, -1);   // DMRES_DRAFT

            // Ghi đúng giá trị + bật cờ chất lượng.
            Assert.Equal(-1, Marshal.ReadInt16(p, DmPrintQualityOffset));
            Assert.NotEqual(0, Marshal.ReadInt32(p, DmFieldsOffset) & DM_PRINTQUALITY);
            Assert.Equal(DevModeBytes, Marshal.ReadInt16(p, DmSizeOffset));

            // Chỉ 72..75 (dmFields) và 90..91 (dmPrintQuality) được đổi — 214 byte còn lại nguyên vẹn.
            var after = Snapshot(p);
            for (var i = 0; i < DevModeBytes; i++)
            {
                var mutated = (i >= DmFieldsOffset && i < DmFieldsOffset + 4)
                              || (i >= DmPrintQualityOffset && i < DmPrintQualityOffset + 2);
                if (mutated) continue;
                Assert.True(before[i] == after[i], $"byte {i} bị ghi nhầm (đúng chỗ chỉ gồm dmFields@72 và dmPrintQuality@90)");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }

    [Theory]
    [InlineData(PrintQuality.High, (short)-4)]
    [InlineData(PrintQuality.Medium, (short)-3)]
    [InlineData(PrintQuality.Low, (short)-2)]
    [InlineData(PrintQuality.Draft, (short)-1)]
    public void TryDmPrintQuality_anh_xa_dung(PrintQuality q, short expected)
    {
        Assert.True(PrinterDialogs.TryDmPrintQuality(q, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData(PrintQuality.AsPrinter)]
    [InlineData((PrintQuality)999)]
    public void TryDmPrintQuality_khong_ep_tra_ve_false(PrintQuality q)
    {
        Assert.False(PrinterDialogs.TryDmPrintQuality(q, out var value));
        Assert.Equal(0, value);
    }

    [Fact]
    public void TryDmPrintQuality_khong_bao_gio_duong()
    {
        // Số dương = DPI; DPI driver không có → driver từ chối CẢ DEVMODE (mất luôn khổ giấy/màu/duplex).
        foreach (var q in new[] { PrintQuality.High, PrintQuality.Medium, PrintQuality.Low, PrintQuality.Draft })
        {
            Assert.True(PrinterDialogs.TryDmPrintQuality(q, out var value));
            Assert.True(value < 0, $"{q} ánh xạ ra {value} — dmPrintQuality phải là DMRES_* âm");
        }
    }

    [Fact]
    public void ApplyDevMode_Quality_AsPrinter_khong_mo_may_in()
    {
        // Tên máy in rác vẫn phải Ok(null): chất lượng AsPrinter không được mở máy in.
        var r = PrinterDialogs.ApplyDevMode("___no_such_printer___",
            new PrintConfig { Quality = PrintQuality.AsPrinter });

        Assert.True(r.IsSuccess);
        Assert.Null(r.Value);
        Assert.Null(r.Error);
    }

    [Fact]
    public void ApplyDevMode_Quality_khong_lam_vo_hieu_hoa_cac_bit_khac()
    {
        var p = Marshal.AllocHGlobal(DevModeBytes);
        try
        {
            for (var i = 0; i < DevModeBytes; i++) Marshal.WriteByte(p, i, 0);
            Marshal.WriteInt16(p, DmSizeOffset, DevModeBytes);
            Marshal.WriteInt32(p, DmFieldsOffset, DM_COLOR | DM_DUPLEX);   // cờ đã có từ lease màu/duplex

            PrinterDialogs.WritePrintQuality(p, -3);   // DMRES_MEDIUM

            var fields = Marshal.ReadInt32(p, DmFieldsOffset);
            Assert.NotEqual(0, fields & DM_PRINTQUALITY);
            Assert.NotEqual(0, fields & DM_COLOR);     // cờ cũ KHÔNG được bị xoá
            Assert.NotEqual(0, fields & DM_DUPLEX);
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }

    private static byte[] Snapshot(IntPtr p)
    {
        var bytes = new byte[DevModeBytes];
        Marshal.Copy(p, bytes, 0, DevModeBytes);
        return bytes;
    }
}
