using System.Runtime.InteropServices;
using Printonator.Core.Models;
using Printonator.Spool.Printing;

namespace Printonator.Spool.Tests;

/// <summary>
/// Test logic DEVMODE của PrinterDialogs.ApplyDuplex — thuần byte-offset, không cần máy in thật.
/// Quan trọng nhất: WriteDuplex chỉ được đụng dmDuplex@94 + cờ dmFields@72, KHÔNG được ghi nhầm
/// chỗ khác (struct sai offset sẽ ghi vào dmCopies@86 = hỏng số bản in).
/// </summary>
public class DuplexDevModeTests
{
    private const int DevModeBytes = 220;   // dmSize đo được trên máy thật
    private const int DmSizeOffset = 68;
    private const int DmFieldsOffset = 72;
    private const int DmDuplexOffset = 94;
    private const int DM_DUPLEX = 0x1000;

    [Fact]
    public void WriteDuplex_chi_dung_duplex_va_dmFields_khong_doc_gia_bytes_khac()
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

            PrinterDialogs.WriteDuplex(p, 2);

            // Ghi đúng giá trị + bật cờ duplex.
            Assert.Equal(2, PrinterDialogs.ReadDuplex(p));
            Assert.NotEqual(0, Marshal.ReadInt32(p, DmFieldsOffset) & DM_DUPLEX);
            Assert.Equal(DevModeBytes, Marshal.ReadInt16(p, DmSizeOffset));

            // Chỉ 72..75 (dmFields) và 94..95 (dmDuplex) được đổi — 200 byte còn lại nguyên vẹn.
            var after = Snapshot(p);
            for (var i = 0; i < DevModeBytes; i++)
            {
                var mutated = (i >= DmFieldsOffset && i < DmFieldsOffset + 4) || (i >= DmDuplexOffset && i < DmDuplexOffset + 2);
                if (mutated) continue;
                Assert.True(before[i] == after[i], $"byte {i} bị ghi nhầm (đúng chỗ chỉ gồm dmFields@72 và dmDuplex@94)");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }

    [Theory]
    [InlineData(PrintDuplexMode.Simplex, (short)1)]
    [InlineData(PrintDuplexMode.LongEdge, (short)2)]
    [InlineData(PrintDuplexMode.ShortEdge, (short)3)]
    public void TryDmDuplex_anh_xa_dung_gia_tri_DMDUP(PrintDuplexMode mode, short expected)
    {
        Assert.True(PrinterDialogs.TryDmDuplex(mode, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData(PrintDuplexMode.AsPrinter)]
    [InlineData((PrintDuplexMode)999)]
    public void TryDmDuplex_gia_tri_loi_tra_ve_false(PrintDuplexMode mode)
    {
        Assert.False(PrinterDialogs.TryDmDuplex(mode, out var value));
        Assert.Equal(0, value);
    }

    [Fact]
    public void ApplyDuplex_AsPrinter_khong_can_may_in_tra_ve_null()
    {
        // Tên rác vẫn phải Ok(null): AsPrinter không được mở máy in.
        var r = PrinterDialogs.ApplyDuplex("___no_such_printer___", PrintDuplexMode.AsPrinter);
        Assert.True(r.IsSuccess);
        Assert.Null(r.Value);
        Assert.Null(r.Error);
    }

    [Fact]
    public void ApplyDuplex_may_in_khong_ton_tai_tra_ve_loi_day_du()
    {
        var r = PrinterDialogs.ApplyDuplex("___no_such_printer___", PrintDuplexMode.LongEdge);
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
