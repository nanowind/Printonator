using System.Globalization;
using System.Windows;
using Printonator.UI.Localization;

namespace Printonator.UI;

/// <summary>
/// Application entry point. OnStartup đọc ngôn ngữ (env → registry → vi-VN), set culture cho
/// WPF trước khi tạo window đầu tiên (bỏ StartupUri, dùng OnStartup + new MainWindow()).
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

        // === BƯỚC 2: Tạo window chính ===
        var mainWindow = new MainWindow();
        mainWindow.Show();
    }
}