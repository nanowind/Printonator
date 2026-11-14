using System.Runtime.InteropServices;
using Printonator.Core.Models;
using Printonator.Spool.Printing;

namespace Printonator.Spool.Tests;

/// <summary>
/// Test logic DEVMODE MÀU của PrinterDialogs — thuần byte-offset, không cần máy in thật.
/// Quan trọng nhất: WriteColor chỉ được đụng dmColor@92 + cờ dmFields@72, KHÔNG được ghi nhầm
/// chỗ khác (struct sai offset sẽ ghi vào dmPrintQuality@90 hoặc dmCopies@86 = hỏng cài đặt in).
/// </summary>
public class ColorDevModeTests
{
    private const int DevModeBytes = 220;   // dmSize đo được trên máy thật
    private const int DmSizeOffset = 68;
    private const int DmFieldsOffset = 72;
    private const int DmColorOffset = 92;
    private const int DM_COLOR = 0x800;
    private const int DM_DUPLEX = 0x1000;

    [Fact]
    public void WriteColor_chi_dung_dmColor_va_dmFields_khong_doc_gia_bytes_khac()
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

            PrinterDialogs.WriteColor(p, 1);

            // Ghi đúng giá trị + bật cờ màu.
            Assert.Equal(1, PrinterDialogs.ReadColor(p));
            Assert.NotEqual(0, Marshal.ReadInt32(p, DmFieldsOffset) & DM_COLOR);
            Assert.Equal(DevModeBytes, Marshal.ReadInt16(p, DmSizeOffset));

            // Chỉ 72..75 (dmFields) và 92..93 (dmColor) được đổi — 200 byte còn lại nguyên vẹn.
            var after = Snapshot(p);
            for (var i = 0; i < DevModeBytes; i++)
            {
                var mutated = (i >= DmFieldsOffset && i < DmFieldsOffset + 4) || (i >= DmColorOffset && i < DmColorOffset + 2);
                if (mutated) continue;
                Assert.True(before[i] == after[i], $"byte {i} bị ghi nhầm (đúng chỗ chỉ gồm dmFields@72 và dmColor@92)");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }

    [Fact]
    public void WriteColor_giu_nguyen_bit_khac_trong_dmFields()
    {
        var p = Marshal.AllocHGlobal(DevModeBytes);
        try
        {
            for (var i = 0; i < DevModeBytes; i++) Marshal.WriteByte(p, i, 0);
            Marshal.WriteInt16(p, DmSizeOffset, DevModeBytes);
            Marshal.WriteInt32(p, DmFieldsOffset, DM_DUPLEX);   // cờ đã có sẵn từ lease duplex

            PrinterDialogs.WriteColor(p, 2);

            var fields = Marshal.ReadInt32(p, DmFieldsOffset);
            Assert.NotEqual(0, fields & DM_COLOR);
            Assert.NotEqual(0, fields & DM_DUPLEX);   // cờ cũ KHÔNG được bị xoá
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }

    [Theory]
    [InlineData(PrintColorMode.Grayscale, (short)1)]
    [InlineData(PrintColorMode.Color, (short)2)]
    public void TryDmColor_anh_xa_dung(PrintColorMode mode, short expected)
    {
        Assert.True(PrinterDialogs.TryDmColor(mode, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData(PrintColorMode.AsPrinter)]
    [InlineData(PrintColorMode.AsDocument)]
    [InlineData((PrintColorMode)999)]
    public void TryDmColor_khong_ep_tra_ve_false(PrintColorMode mode)
    {
        Assert.False(PrinterDialogs.TryDmColor(mode, out var value));
        Assert.Equal(0, value);
    }

    [Fact]
    public void ApplyDevMode_khong_co_gi_de_dat_tra_ve_null_khong_mo_may_in()
    {
        // Cấu hình mặc định = theo driver/tài liệu → không đụng gì, tên máy in rác vẫn phải Ok(null).
        var r = PrinterDialogs.ApplyDevMode("___no_such_printer___", new PrintConfig());

        Assert.True(r.IsSuccess);
        Assert.Null(r.Value);
        Assert.Null(r.Error);
    }

    [Fact]
    public void ApplyDevMode_ten_may_in_rong_bao_loi_ro()
    {
        var r = PrinterDialogs.ApplyDevMode("", new PrintConfig { ColorMode = PrintColorMode.Grayscale });

        Assert.False(r.IsSuccess);
        Assert.NotNull(r.Error);
        Assert.False(string.IsNullOrEmpty(r.Error!.Code));
        Assert.False(string.IsNullOrEmpty(r.Error.Message));
        Assert.False(string.IsNullOrEmpty(r.Error.Hint));
        Assert.Equal(ErrorCodes.SpoolerFailed, r.Error.Code);
        Assert.Equal(PrintErrorCategory.Printer, r.Error.Category);
    }

    private static byte[] Snapshot(IntPtr p)
    {
        var bytes = new byte[DevModeBytes];
        Marshal.Copy(p, bytes, 0, DevModeBytes);
        return bytes;
    }
}
