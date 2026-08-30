using Printonator.Core;
using Printonator.Core.Safety;
using Printonator.Spool.Printing;

namespace Printonator.Mcp;

/// <summary>
/// Trạng thái dùng chung cho các tool MCP: queue + engine + guard (an toàn AI in).
/// Instance duy nhất trong tiến trình — UI in-process (roadmap) sẽ trỏ vào cùng queue này.
/// </summary>
public static class AppServices
{
    private static readonly object Sync = new();
    private static bool _engineRegistered;

    /// <summary>Guard an toàn — cấu hình từ env/file PRINTONATOR_* (xem McpGuardConfig).</summary>
    private static readonly McpGuardConfig Cfg = McpGuardConfig.Load();
    private static readonly AuditLogger Audit = new(Cfg.AuditLogPath);
    private static readonly PrintGuard Guard = new(Cfg, Audit);

    public static PrintQueue Queue { get; } = new();

    /// <summary>Guard an toàn — cấu hình từ env PRINTONATOR_* (xem McpGuardConfig).</summary>
    public static PrintGuard GuardInstance => Guard;

    /// <summary>Cấu hình guard đang áp dụng (đọc lại 1 lần khi khởi động tiến trình).</summary>
    public static McpGuardConfig GuardConfig => Cfg;

    /// <summary>Đăng ký engine in đúng 1 lần — fail-closed nếu thiếu.</summary>
    public static void EnsureEngine()
    {
        lock (Sync)
        {
            if (_engineRegistered) return;
            // Engine ưu tiên (dynamic theo máy user — KHÔNG bundle lib):
            // 1) MS Office COM → 2) LibreOffice (soffice nếu máy có) → 3) Watermark bọc Browser render
            // (Edge/Chrome — PDF/ảnh/TXT đúng options; không watermark → delegate inner Browser) → 4) SpoolPrintEngine (fallback)
            Queue.RegisterEngine(new OfficeComPrintEngine());
            Queue.RegisterEngine(new LibreOfficePrintEngine());
            Queue.RegisterEngine(new WatermarkPrintEngine(new BrowserPrintEngine()));
            Queue.RegisterEngine(new SpoolPrintEngine());
            _engineRegistered = true;
        }
    }

    public static void Dispose()
    {
        Queue.Dispose();
    }
}