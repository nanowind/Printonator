using System;
using Printonator.Core;

namespace Printonator.Spool.Printing;

/// <summary>
/// Single source of truth for engine registration order.
/// Replaces 2 identical registration blocks in AppServices.cs + MainWindow.xaml.cs.
/// Engines must be registered in priority order: Office COM → LibreOffice → GDI → Watermark(Browser) → Spool.
/// </summary>
public static class EngineRegistry
{
    /// <summary>
    /// Register all print engines in priority order.
    /// Called by both MCP (AppServices.EnsureEngine) and UI (MainWindow.InitializeAsync).
    /// </summary>
    /// <param name="queue">The print queue to register engines into.</param>
    /// <param name="configure">Optional: configure each engine before registration (e.g., set browser resolver for tests).</param>
    public static void RegisterAll(PrintQueue queue, Action<IPrintEngine>? configure = null)
    {
        // Engine ưu tiên (dynamic theo máy user — KHÔNG bundle lib):
        // 1) MS Office COM → 2) LibreOffice (soffice nếu máy có) → 3) GDI PDF → 4) Watermark bọc Browser
        // (Edge/Chrome — PDF/ảnh/TXT đúng options) → 5) SpoolPrintEngine (fallback)
        var engines = new IPrintEngine[]
        {
            new OfficeComPrintEngine(),
            new LibreOfficePrintEngine(),
            new GdiPrintEngine(),
            new WatermarkPrintEngine(new BrowserPrintEngine()),
            new SpoolPrintEngine(),
        };

        foreach (var engine in engines)
        {
            configure?.Invoke(engine);
            queue.RegisterEngine(engine);
        }
    }
}