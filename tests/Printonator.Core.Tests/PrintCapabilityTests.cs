using Printonator.Core.Models;
using Xunit;

namespace Printonator.Core.Tests;

/// <summary>
/// Test mô hình khả năng máy in 3 trạng thái (Unknown / NotSupported / Supported).
/// Vì sao quan trọng: "SupportsColor == false" từng gộp 3 nghĩa khác nhau — driver báo không có,
/// probe 4 giây hết giờ, hoặc bản ghi tổng hợp (toolbar "Theo máy thanh công cụ").
/// UI ẩn hàng Màu/2 mặt khi NotSupported; nếu Unknown bị coi là "không hỗ trợ" thì máy in màu
/// đang offline sẽ mất option màu trong im lặng.
/// </summary>
public class PrintCapabilityTests
{
    [Fact]
    public void Mac_dinh_la_Khong_biet_va_khong_bao_gio_la_khong_ho_tro()
    {
        // Nơi tạo PrinterInfo nào quên set cờ → phải rơi về Unknown, KHÔNG BAO GIỜ thành NotSupported.
        var p = new PrinterInfo { Name = "x" };

        Assert.Equal(CapabilitySupport.Unknown, p.ColorSupport);
        Assert.Equal(CapabilitySupport.Unknown, p.DuplexSupport);
        Assert.NotEqual(CapabilitySupport.NotSupported, p.ColorSupport);
        Assert.NotEqual(CapabilitySupport.NotSupported, p.DuplexSupport);
        // Unknown không tính là "có" — chỉ là chưa biết.
        Assert.False(p.SupportsColor);
        Assert.False(p.SupportsDuplex);
    }

    [Theory]
    [InlineData(CapabilitySupport.Unknown, false)]
    [InlineData(CapabilitySupport.NotSupported, false)]
    [InlineData(CapabilitySupport.Supported, true)]
    public void SupportsColor_chi_true_khi_doc_duoc_va_co_ho_tro(CapabilitySupport state, bool expected)
    {
        var p = new PrinterInfo { Name = "Máy in màu", ColorSupport = state };
        Assert.Equal(expected, p.SupportsColor);
    }

    [Theory]
    [InlineData(CapabilitySupport.Unknown, false)]
    [InlineData(CapabilitySupport.NotSupported, false)]
    [InlineData(CapabilitySupport.Supported, true)]
    public void SupportsDuplex_chi_true_khi_doc_duoc_va_co_ho_tro(CapabilitySupport state, bool expected)
    {
        var p = new PrinterInfo { Name = "Máy in 2 mặt", DuplexSupport = state };
        Assert.Equal(expected, p.SupportsDuplex);
    }

    [Fact]
    public void May_in_den_trang_mot_mat_bao_khong_ho_tro()
    {
        // Ca UI dựa vào để ẩn hàng Màu/2 mặt — nghĩa của nó phải tường minh.
        var p = new PrinterInfo
        {
            Name = "Máy in văn phòng",
            ColorSupport = CapabilitySupport.NotSupported,
            DuplexSupport = CapabilitySupport.NotSupported,
        };

        Assert.Equal(CapabilitySupport.NotSupported, p.ColorSupport);
        Assert.Equal(CapabilitySupport.NotSupported, p.DuplexSupport);
        Assert.False(p.SupportsColor);
        Assert.False(p.SupportsDuplex);
    }

    [Fact]
    public void May_in_mau_hai_mat_bao_co_ho_tro()
    {
        var p = new PrinterInfo
        {
            Name = "Máy in màu 2 mặt",
            ColorSupport = CapabilitySupport.Supported,
            DuplexSupport = CapabilitySupport.Supported,
        };

        Assert.True(p.SupportsColor);
        Assert.True(p.SupportsDuplex);
    }

    [Fact]
    public void CapabilitiesSummary_khong_noi_KHONG_khi_chua_biet()
    {
        // Chưa đọc được khả năng (Unknown) không được hiển thị y như "đã đọc và không có" (NotSupported),
        // nếu không người dùng đọc nhầm thành "máy này không in màu được".
        var unknown = new PrinterInfo
        {
            Name = "Máy vừa cài (chưa dò)",
            ColorSupport = CapabilitySupport.Unknown,
            DuplexSupport = CapabilitySupport.Unknown,
        };
        var notSupported = new PrinterInfo
        {
            Name = "Máy đen trắng 1 mặt",
            ColorSupport = CapabilitySupport.NotSupported,
            DuplexSupport = CapabilitySupport.NotSupported,
        };
        var supported = new PrinterInfo
        {
            Name = "Máy màu 2 mặt",
            ColorSupport = CapabilitySupport.Supported,
            DuplexSupport = CapabilitySupport.Supported,
        };

        // So sánh bất đẳng thức thay vì ghim dấu câu — không phụ thuộc chuỗi chính xác.
        Assert.NotEqual(unknown.CapabilitiesSummary, notSupported.CapabilitiesSummary);
        // Supported và NotSupported vẫn phải khác nhau (mốc cơ sở).
        Assert.NotEqual(supported.CapabilitiesSummary, notSupported.CapabilitiesSummary);
        // Unknown cũng không được đọc giống hệt "có hỗ trợ".
        Assert.NotEqual(unknown.CapabilitiesSummary, supported.CapabilitiesSummary);
    }

    [Fact]
    public void Unknown_khong_duoc_dung_de_loc_may_in()
    {
        var p = new PrinterInfo { Name = "Máy in màu đang offline", ColorSupport = CapabilitySupport.Unknown };

        // Hai vế phải cùng đúng: "không ưu tiên máy này" (SupportsColor false)
        // KHÁC với "máy này không in được màu" (NotSupported). PickBestPrinter dựa vế đầu;
        // vế sau là điều UI ẩn option — chỉ được phép khi đã đọc được khả năng.
        Assert.False(p.SupportsColor);
        Assert.NotEqual(CapabilitySupport.NotSupported, p.ColorSupport);
    }
}
