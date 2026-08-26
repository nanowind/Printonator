using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Printonator.Core.Models;

namespace Printonator.Spool.Printing;

/// <summary>
/// Mở DIALOG NATIVE của driver máy in:
/// /e (Printing Preferences) dùng Win32 DocumentProperties (DM_IN_PROMPT) — hoạt động cho MỌI
/// driver kể cả Microsoft OpenXPS Class Driver (WSD), nơi "printui.dll,PrintUIEntry /e" im lặng.
/// /p (Printer Properties) dùng shell verb "properties" trên folder Devices and Printers
/// (Shell.Application COM) — vì printui /p cũng lặng im trên máy driver class/WSD.
/// Không nuốt lỗi: lỗi mở ở pha kiểm tra (tên máy, driver) trả Result.Fail(PrintError).
/// </summary>
public static class PrinterDialogs
{
    private const string DevicesAndPrinters = "shell:::{A8A91A66-3A7D-4424-8D24-04E180695C7A}";
    private const string PrintersAndFaxes = "shell:::{26EE0668-A00A-44D7-9371-BEB064C98683}";

    // ===================== Printing Preferences: Win32 DocumentProperties =====================

    private const int DM_IN_PROMPT = 4;
    private const int DM_OUT_BUFFER = 2;

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int DocumentProperties(IntPtr hWnd, IntPtr hPrinter, string pDeviceName,
        IntPtr pDevModeOutput, IntPtr pDevModeInput, int fMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    /// <summary>Mở "Printing Preferences" của máy in (cửa sổ thật của driver — khác printui /e im lặng).</summary>
    public static Result<bool> OpenPrintingPreferences(string printerName)
    {
        if (string.IsNullOrWhiteSpace(printerName))
            return PrinterNotSelected();

        // Pha kiểm tra ngay trên luồng gọi: máy in có tồn tại + driver trả DEVMODE không.
        if (!OpenPrinter(printerName, out var hPrinter, IntPtr.Zero))
            return FailWithWin32("Không mở được máy in cho cài đặt in.", "Kiểm tra tên máy in còn tồn tại và spooler đang chạy.");
        try
        {
            var size = DocumentProperties(IntPtr.Zero, hPrinter, printerName, IntPtr.Zero, IntPtr.Zero, 0);
            if (size <= 0)
                return FailWithWin32($"Driver máy in \"{printerName}\" không cung cấp bảng cài đặt in.",
                    "Thử mở 'Printer Properties' hoặc kiểm tra driver máy in.");
        }
        finally
        {
            ClosePrinter(hPrinter);
        }

        // Hiện dialog trên thread STA riêng — không đóng băng window, dialog độc lập với app.
        var worker = new Thread(() => ShowDocumentProperties(printerName))
        {
            IsBackground = true,
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return Result<bool>.Ok(true);
    }

    /// <summary>DocumentProperties(DM_IN_PROMPT) — modal trên thread riêng, tự dọn handle sau khi đóng dialog.</summary>
    private static void ShowDocumentProperties(string printerName)
    {
        var owner = CreateWindowEx(0, "STATIC", "Printonator preferences owner", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        IntPtr hPrinter = IntPtr.Zero;
        try
        {
            if (!OpenPrinter(printerName, out hPrinter, IntPtr.Zero)) return;
            var size = DocumentProperties(owner, hPrinter, printerName, IntPtr.Zero, IntPtr.Zero, 0);
            if (size <= 0) return;
            var devMode = Marshal.AllocHGlobal(size);
            try
            {
                if (DocumentProperties(owner, hPrinter, printerName, devMode, IntPtr.Zero, DM_OUT_BUFFER) >= 0)
                    DocumentProperties(owner, hPrinter, printerName, devMode, devMode, DM_IN_PROMPT);
            }
            finally
            {
                Marshal.FreeHGlobal(devMode);
            }
        }
        catch
        {
            // Dialog đã hiện hay không — không làm crash app. Bước kiểm tra ở caller đã bắt lỗi chính.
        }
        finally
        {
            if (hPrinter != IntPtr.Zero) ClosePrinter(hPrinter);
            if (owner != IntPtr.Zero) DestroyWindow(owner);
        }
    }

    // ===================== Printer Properties: shell verb "properties" =====================

    /// <summary>Mở "Printer Properties" của máy in (một số tab yêu cầu quyền admin).</summary>
    public static Result<bool> OpenPrinterProperties(string printerName)
    {
        if (string.IsNullOrWhiteSpace(printerName))
            return PrinterNotSelected();

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
                return FailWithMessage("Không khởi động được Windows Shell để mở thuộc tính máy in.", "");
            var shell = Activator.CreateInstance(shellType);
            if (shell is null)
                return FailWithMessage("Không khởi động được Windows Shell để mở thuộc tính máy in.", "");

            foreach (var folderPath in new[] { DevicesAndPrinters, PrintersAndFaxes })
            {
                var folder = Com(shell, "Namespace", folderPath);
                if (folder is null) continue;
                var items = Com(folder, "Items");
                var count = items is null ? 0 : Convert.ToInt32(Com(items, "Count"));
                for (var i = 0; i < count; i++)
                {
                    var item = Com(items, "Item", i);
                    var name = item is null ? null : Com(item, "Name") as string;
                    if (string.Equals(name, printerName, StringComparison.OrdinalIgnoreCase))
                    {
                        Com(item!, "InvokeVerb", "properties");
                        return Result<bool>.Ok(true);
                    }
                }
            }
            return FailWithMessage($"Không tìm thấy máy in \"{printerName}\" trong Devices and Printers.",
                "Bấm Scan printers hoặc mở Cài đặt → Máy in & máy quét.");
        }
        catch (Exception ex)
        {
            // Unwrap TargetInvocationException để thấy lỗi COM gốc
            var root = ex;
            while (root is TargetInvocationException { InnerException: not null } tie) root = tie.InnerException!;
            return FailWithMessage($"Không mở được thuộc tính máy in \"{printerName}\".", "Thử lại hoặc mở Cài đặt → Máy in & máy quét.", $"{root.GetType().Name}: {root.Message}");
        }
    }

    /// <summary>Gọi method/property COM bằng reflection (Shell.Application) — không cần package COM interop.
    /// Gộp InvokeMethod|GetProperty vì "Name" là property còn "Namespace"/"Items"/"Item"/"InvokeVerb" là method.</summary>
    private static object? Com(object target, string member, params object[] args)
        => target.GetType().InvokeMember(member,
            BindingFlags.InvokeMethod | BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance,
            null, target, args);

    // ===================== Helpers =====================

    private static Result<bool> PrinterNotSelected()
        => Result<bool>.Fail(new PrintError
        {
            Code = ErrorCodes.PrinterNotFound,
            Category = PrintErrorCategory.Config,
            Message = "Chưa có máy in để mở cài đặt.",
            Hint = "Chọn máy in ở thanh công cụ trước.",
        });

    private static Result<bool> FailWithWin32(string message, string hint)
        => FailWithMessage(message, hint, Marshal.GetLastWin32Error().ToString());

    private static Result<bool> FailWithMessage(string message, string hint, string? detail = null)
        => Result<bool>.Fail(new PrintError
        {
            Code = ErrorCodes.SpoolerFailed,
            Category = PrintErrorCategory.Printer,
            Message = message,
            Hint = hint,
            Detail = detail,
        });
}