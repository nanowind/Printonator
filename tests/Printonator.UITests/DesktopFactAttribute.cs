using Xunit;

namespace Printonator.UITests;

/// <summary>
/// Test bật app WPF THẬT qua FlaUI — chiếm focus/chuột/clipboard, và Kill() mọi instance
/// Printonator.UI đang chạy (mất hàng đợi đang in dở của người dùng).
/// Mặc định BỎ QUA để `dotnet test` không quấy rầy máy người dùng.
/// Chạy khi cần: đặt biến môi trường PRINTONATOR_UI_TESTS=1.
/// </summary>
public sealed class DesktopFactAttribute : FactAttribute
{
    public DesktopFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PRINTONATOR_UI_TESTS") != "1")
            Skip = "UI test bật app thật — đặt PRINTONATOR_UI_TESTS=1 để chạy.";
    }
}
