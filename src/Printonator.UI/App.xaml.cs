using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using Printonator.UI.Localization;

namespace Printonator.UI;

/// <summary>
/// Application entry point. OnStartup đọc ngôn ngữ (env → registry → vi-VN), set culture cho
/// WPF trước khi tạo window đầu tiên (bỏ StartupUri, dùng OnStartup + new MainWindow()).
/// Single-instance (T2.8): instance 2 gửi args qua pipe → instance 1 nhận và thêm file.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // === BƯỚC 1: Nạp catalog ngôn ngữ + đặt culture TRƯỚC mọi thao tác WPF ===
        // (không dùng StartupUri nữa — window được tạo THỦ CÔNG ở đây, sau khi culture đã set;
        //  MarkupExtension {l10n:Loc} resolve đúng ngôn ngữ khi XAML được parse.)
        L10n.Initialize();
        var culture = CultureResolver.Resolve();
        L10n.ApplyCulture(culture);

        // === BƯỚC 1b: Chế độ giao diện (Lite/Full) — đọc TRƯỚC khi tạo window để MainWindow
        // biết ẩn/hiện tính năng Phase 2 ngay lúc load (không cần gọi lại sau này). ===
        ModeResolver.Initialize();

        // === BƯỚC 2: Đăng ký menu chuột phải (HKCU — không cần admin) ===
        SingleInstance.RegisterShellMenu();

        // === BƯỚC 3: Single-instance gate ===
        if (!SingleInstance.Acquire())
        {
            SingleInstance.SendArgs(e.Args);
            Shutdown();
            return;
        }
        base.OnStartup(e);

        // === BƯỚC 4: Tạo window chính ===
        var mainWindow = new MainWindow();
        mainWindow.Show();

        // === BƯỚC 4b: Cold start từ shell (app CHƯA chạy — instance này giành mutex) ===
        // Không có nhánh này thì chuột phải "In với Printonator" lúc app đóng sẽ mở cửa sổ trống, mất file.
        // Dùng chung Dispatcher.BeginInvoke với pipe path để hai đường đi hành xử giống hệt nhau.
        if (e.Args.Length > 0)
            HandleIncomingPaths(string.Join("|", e.Args));

        // === BƯỚC 5: Pipe server nhận file từ instance 2 ===
        SingleInstance.StartServer(HandleIncomingPaths);

        // === BƯỚC 6: Dọn dẹp khi app thoát ===
        Exit += (_, _) => SingleInstance.StopServer();
    }

    /// <summary>Nhận paths từ shell (instance 2 qua pipe, hoặc cold start qua e.Args) → thêm vào danh sách +
    /// in ngay nếu có cờ "--print". Dispatch qua Dispatcher vì window phải sẵn sàng trước khi thêm file.</summary>
    private static void HandleIncomingPaths(string joined)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (Application.Current.MainWindow is not MainWindow main) return;
            var parts = (joined ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var files = parts.Where(p => p != "--print" && File.Exists(p)).ToList();
            if (files.Count == 0) return;
            main.AddFilesFromExternal(files, L10n.S(Keys.Shell.PrintTaskName));
            if (parts.Any(p => p == "--print"))
                main.PrintAddedFromExternal();
        });
    }
}