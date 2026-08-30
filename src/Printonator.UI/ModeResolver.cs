using Microsoft.Win32;

namespace Printonator.UI;

/// <summary>
/// Chế độ giao diện Printonator: Lite (mặc định — giao diện gọn, ẩn tính năng Phase 2)
/// hoặc Full (hiện đủ tính năng). Đọc/ghi registry HKCU\Software\Printonator\Mode.
/// Quyết định được đọc TRƯỚC khi tạo MainWindow (App.OnStartup) — các cửa sổ kiểm
/// tra ModeResolver.IsFull khi Loaded để ẩn/hiện UI. Đổi chế độ cần khởi động lại app.
/// </summary>
public static class ModeResolver
{
    public const string RegistryKey = @"Software\Printonator";
    public const string RegistryValueName = "Mode";

    /// <summary>Full mode? Mặc định false (Lite). Chỉ true khi registry ghi đúng "full".</summary>
    public static bool IsFull { get; private set; }

    /// <summary>Đọc chế độ từ registry. "full" → Full; mọi giá trị khác/"lite"/thiếu/lỗi → Lite (mặc định an toàn).</summary>
    public static void Initialize()
    {
        IsFull = ReadRegistryValue();
    }

    /// <summary>Ghi chế độ mới vào registry. Lỗi ghi → bỏ qua (lần chạy hiện tại vẫn dùng chế độ cũ).</summary>
    public static void SetMode(bool full)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryKey);
            key?.SetValue(RegistryValueName, full ? "full" : "lite", RegistryValueKind.String);
        }
        catch { /* không ghi được registry — đổi chế độ không có hiệu lực, app vẫn chạy chế độ cũ */ }
    }

    private static bool ReadRegistryValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKey);
            var raw = key?.GetValue(RegistryValueName) as string;
            return !string.IsNullOrWhiteSpace(raw)
                && raw.Trim().Equals("full", System.StringComparison.OrdinalIgnoreCase);
        }
        catch (System.Security.SecurityException) { return false; }
        catch (System.IO.IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
