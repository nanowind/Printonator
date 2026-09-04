using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Printonator.Core;
using Printonator.Core.Models;
using Printonator.Core.Printing;

namespace Printonator.Spool.Printing;

/// <summary>
/// Shared browser render → PDF → spool pipeline.
/// Consolidates the identical 4-step sequence from BrowserPrintEngine, WatermarkPrintEngine, and MergePrintEngine:
/// 1) Resolve browser → 2) Write HTML to temp file → 3) DevTools.PrintPdfAsync → 4) Write base64 PDF to disk
/// </summary>
public static class BrowserPrintPipeline
{
    /// <summary>
    /// Render HTML to PDF via headless browser, then write to disk.
    /// Returns (ok, base64Pdf, error) — caller decides what to do with the PDF.
    /// </summary>
    public static async Task<(bool Ok, string? Base64Pdf, string? Error)> RenderToPdfAsync(
        string htmlContent,
        Dictionary<string, object?> printParams,
        string tempDir,
        CancellationToken ct)
    {
        var browser = new BrowserLocator().ResolveBrowser();
        if (browser is not { } b)
            return (false, null, "Edge/Chrome not found");

        var htmlPath = Path.Combine(tempDir, "render.html");
        await File.WriteAllTextAsync(htmlPath, htmlContent, ct);

        return await DevToolsPrintClient.PrintPdfAsync(
            b.Path,
            new Uri(htmlPath).AbsoluteUri,
            printParams,
            Path.Combine(tempDir, "profile"),
            ct);
    }

    /// <summary>
    /// Render HTML to PDF and write the PDF file to disk.
    /// Returns (ok, pdfPath, error) — the PDF file is written to tempDir/out.pdf.
    /// </summary>
    public static async Task<(bool Ok, string? PdfPath, string? Error)> RenderAndWritePdfAsync(
        string htmlContent,
        Dictionary<string, object?> printParams,
        string tempDir,
        CancellationToken ct)
    {
        var (ok, base64, err) = await RenderToPdfAsync(htmlContent, printParams, tempDir, ct);
        if (!ok || string.IsNullOrEmpty(base64))
            return (false, null, err);

        var outPdf = Path.Combine(tempDir, "out.pdf");
        await File.WriteAllBytesAsync(outPdf, Convert.FromBase64String(base64), ct);
        return (true, outPdf, null);
    }

    /// <summary>
    /// Render HTML to PDF, write to disk, then delegate to SpoolPrintEngine for actual printing.
    /// This is the common final step for WatermarkPrintEngine and MergePrintEngine.
    /// </summary>
    public static async Task<Result<bool>> RenderAndSpoolAsync(
        string htmlContent,
        Dictionary<string, object?> printParams,
        PrintJob originalJob,
        string outputLabel,
        string tempDir,
        CancellationToken ct)
    {
        var (ok, pdfPath, err) = await RenderAndWritePdfAsync(htmlContent, printParams, tempDir, ct);
        if (!ok || pdfPath is null)
            return Result<bool>.Fail(PrintErrorFactory.SpoolerFailed($"Không dựng được PDF: {err ?? "lỗi không rõ"}"));

        var spoolJob = new PrintJob
        {
            FilePath = pdfPath,
            FileName = originalJob.FileName + outputLabel,
            Format = "PDF",
            Config = new PrintConfig
            {
                PrinterName = originalJob.Config.PrinterName,
                Copies = Math.Max(originalJob.Config.Copies, 1),
            },
        };
        return await new SpoolPrintEngine().PrintAsync(spoolJob, ct);
    }
}