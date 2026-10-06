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
/// Cũng chứa ApplyDuplex — đặt dmDuplex trong DEVMODE cho Office (Office không có COM duplex).
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

    [DllImport("winspool.drv", EntryPoint = "GetPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetPrinter(IntPtr hPrinter, int level, IntPtr pPrinter, int cbBuf, out int pcbNeeded);

    [DllImport("winspool.drv", EntryPoint = "SetPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetPrinter(IntPtr hPrinter, int level, IntPtr pPrinter, int command);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

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

    /// <summary>Mở "Printer Properties" (Thuộc tính máy in) — đích thị cửa sổ
    /// General/Sharing/Ports/Advanced/Color Mgmt/Security/Device Settings của máy in.
    /// Dùng run.dll printui.dll PrintUIEntry /p (đúng dialog Printer Properties), KHÔNG phải
    /// shell verb "properties" lên folder Devices-and-Printers — thứ đó mở PROPERTIES CỦA THIẾT BỊ
    /// (hardware) chung chung, KHÔNG phải thuộc tính máy in.
    /// </summary>
    public static Result<bool> OpenPrinterProperties(string printerName)
    {
        if (string.IsNullOrWhiteSpace(printerName))
            return PrinterNotSelected();

        try
        {
            // printui /p = Printer Properties chính chủ. Mở bằng Process + shell (không cần COM).
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "rundll32.exe",
                Arguments = $"printui.dll,PrintUIEntry /p /n \"{printerName}\"",
                UseShellExecute = true,
                CreateNoWindow = true,
            };
            var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
                return FailWithMessage($"Không mở được Thuộc tính máy in \"{printerName}\".", "Thử mở Cài đặt → Máy in & máy quét.");
            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return FailWithMessage($"Không mở được Thuộc tính máy in \"{printerName}\".", "Thử lại hoặc mở Cài đặt → Máy in & máy quét.", ex.Message);
        }
    }

    /// <summary>Gọi method/property COM bằng reflection (Shell.Application) — không cần package COM interop.
    /// Gộp InvokeMethod|GetProperty vì "Name" là property còn "Namespace"/"Items"/"Item"/"InvokeVerb" là method.</summary>
    private static object? Com(object target, string member, params object[] args)
        => target.GetType().InvokeMember(member,
            BindingFlags.InvokeMethod | BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance,
            null, target, args);

    // ===================== Duplex: đặt dmDuplex trong DEVMODE (Office không có COM duplex) =====================

    private const int DM_DUPLEX = 0x1000;          // cờ trong dmFields
    private const int DmSizeOffset = 68;           // dmSize  (Int16) — đo trên buffer GetPrinter thật
    private const int DmFieldsOffset = 72;         // dmFields (Int32)
    private const int DmDuplexOffset = 94;         // dmDuplex (Int16)
    private const int MinDevModeSize = 96;         // DEVMODE phải chứa tới dmDuplex
    private const int PDevModeFieldIndex = 7;      // PRINTER_INFO_2.pDevMode (x64: 7*8 = 56)

    /// <summary>Trả duplex về cấu hình ban đầu — Dispose để restore.</summary>
    public sealed class DuplexRestore : IDisposable
    {
        private readonly IntPtr _hPrinter;
        private readonly IntPtr _buffer;           // buffer PRINTER_INFO_2 tròn vẹn (SetPrinter cần đủ driver data)
        private readonly IntPtr _devMode;
        private readonly short _originalDuplex;
        private readonly object _lock = new();     // STA thread + timeout path cùng gọi Dispose → guard chống double-free
        private bool _disposed;

        internal DuplexRestore(IntPtr hPrinter, IntPtr buffer, IntPtr devMode, short originalDuplex)
        {
            _hPrinter = hPrinter;
            _buffer = buffer;
            _devMode = devMode;
            _originalDuplex = originalDuplex;
        }

        /// <summary>Restore dmDuplex cũ qua SetPrinter(2) rồi ClosePrinter + LocalFree.
        /// Idempotent (thread-safe — khoá _lock, lần gọi thứ hai là no-op, KHÔNG free lần hai);
        /// lỗi restore chỉ ghi log — KHÔNG ném ra ngoài (an toàn khi gọi trong finally).</summary>
        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                try
                {
                    WriteDuplex(_devMode, _originalDuplex);
                    if (!SetPrinter(_hPrinter, 2, _buffer, 0))
                        DuplexLog($"restore duplex: SetPrinter fail err={Marshal.GetLastWin32Error()}");
                }
                catch (Exception ex)
                {
                    DuplexLog($"restore duplex: {ex.Message}");
                }
                finally
                {
                    try
                    {
                        ClosePrinter(_hPrinter);
                        LocalFree(_buffer);
                    }
                    finally
                    {
                        if (ReferenceEquals(Volatile.Read(ref _active), this)) Volatile.Write(ref _active, null);
                    }
                }
            }
        }
    }

    /// <summary>Lease duplex đang sống (nếu có) — timeout kill STA thread thì Dispose không kịp chạy
    /// → RestoreActive() bên chờ bù. MỘT lease tại một thời điểm (mỗi job tự giữ lease của nó).</summary>
    private static DuplexRestore? _active;

    /// <summary>Dọn lease duplex đang treo khi thread STA bị abandon (timeout/cancel trong
    /// OfficeComPrintEngine) — gọi từ luồng await, KHÔNG thay Dispose của caller (luồng thường vẫn tự restore).</summary>
    public static void RestoreActive()
    {
        var lease = Volatile.Read(ref _active);
        lease?.Dispose();   // swallow-and-log như Dispose — không ném ra ngoài
    }

    /// <summary>Đặt dmDuplex cho máy in; AsPrinter → Ok(null) (không mở máy in).
    /// Trả lease để Dispose() khôi phục giá trị cũ. Không bao giờ nuốt lỗi.</summary>
    public static Result<DuplexRestore?> ApplyDuplex(string printerName, PrintDuplexMode mode)
    {
        // AsPrinter = theo driver — không đụng DEVMODE, không cần mở máy in (kể cả tên rác).
        if (mode == PrintDuplexMode.AsPrinter)
            return Result<DuplexRestore?>.Ok(null);

        if (!TryDmDuplex(mode, out var target))
            return FailDuplex($"Chế độ duplex \"{mode}\" không hợp lệ.",
                "Dùng AsPrinter / Simplex / LongEdge / ShortEdge.");

        if (string.IsNullOrWhiteSpace(printerName))
            return FailDuplex("Chưa có máy in để đặt duplex.", "Chọn máy in trước.");

        if (!OpenPrinter(printerName, out var hPrinter, IntPtr.Zero))
            return FailDuplexWin32($"Không mở được máy in \"{printerName}\" để đặt duplex.",
                "Kiểm tra tên máy in còn tồn tại và spooler đang chạy.");

        var buffer = IntPtr.Zero;
        var leaseGiven = false;   // trả lease = trao quyền sở hữu handle/buffer cho caller
        try
        {
            // Đo size trước (GetPrinter level 2 luôn trả false ở lần gọi rỗng — chỉ đọc cbNeeded).
            GetPrinter(hPrinter, 2, IntPtr.Zero, 0, out var needed);
            if (needed <= 0)
                return FailDuplexWin32($"Không lấy được cài đặt của máy in \"{printerName}\".",
                    "Mở 'Printing Preferences' một lần hoặc cài lại driver máy in.");

            buffer = Marshal.AllocHGlobal(needed);
            if (!GetPrinter(hPrinter, 2, buffer, needed, out _))
                return FailDuplexWin32($"Không đọc được bảng cài đặt (DEVMODE) của \"{printerName}\".",
                    "Mở 'Printing Preferences' một lần hoặc cài lại driver máy in.");

            // PRINTER_INFO_2 xếp tuần tự trong buffer; pDevMode trỏ vào DEVMODE CÙNG buffer này.
            var devMode = Marshal.ReadIntPtr(buffer, PDevModeFieldIndex * IntPtr.Size);
            if (devMode == IntPtr.Zero)
                return FailDuplex($"Máy in \"{printerName}\" không trả về bảng cài đặt (DEVMODE).",
                    "Mở 'Printing Preferences' để driver tạo cài đặt, rồi thử lại.");

            if (Marshal.ReadInt16(devMode, DmSizeOffset) < MinDevModeSize)
                return FailDuplex($"Bảng cài đặt của \"{printerName}\" quá ngắn (không có ô duplex).",
                    "Cập nhật driver máy in — bản cũ không hỗ trợ duplex.");

            var current = ReadDuplex(devMode);
            if (current == target)
                return Result<DuplexRestore?>.Ok(null);   // đã đúng sẵn — không giữ lease

            WriteDuplex(devMode, target);
            if (!SetPrinter(hPrinter, 2, buffer, 0))
                return FailDuplexWin32($"Không ghi được duplex vào máy in \"{printerName}\".",
                    "Đặt tay trong 'Printing Preferences' → 2 mặt in.");

            leaseGiven = true;
            var lease = new DuplexRestore(hPrinter, buffer, devMode, current);
            Volatile.Write(ref _active, lease);   // timeout path gọi RestoreActive() nếu STA bị abandon
            return Result<DuplexRestore?>.Ok(lease);
        }
        catch (Exception ex)
        {
            return FailDuplex($"Lỗi đặt duplex cho máy in \"{printerName}\".",
                "Đặt tay trong 'Printing Preferences' → 2 mặt in.", ex.Message);
        }
        finally
        {
            if (!leaseGiven)
            {
                if (buffer != IntPtr.Zero) LocalFree(buffer);
                ClosePrinter(hPrinter);
            }
        }
    }

    /// <summary>Ánh xạ PrintDuplexMode → giá trị dmDuplex (DMDUP_*). AsPrinter/rao lung → false.</summary>
    internal static bool TryDmDuplex(PrintDuplexMode mode, out short value)
    {
        value = mode switch
        {
            PrintDuplexMode.Simplex => 1,    // DMDUP_SIMPLEX
            PrintDuplexMode.LongEdge => 2,   // DMDUP_VERTICAL
            PrintDuplexMode.ShortEdge => 3,  // DMDUP_HORIZONTAL
            _ => 0,
        };
        return value != 0;
    }

    /// <summary>Đọc dmDuplex — Int16 @ offset 94 của DEVMODE (đo trên buffer thật, đừng tính lại).</summary>
    internal static short ReadDuplex(IntPtr devMode) => Marshal.ReadInt16(devMode, DmDuplexOffset);

    /// <summary>Ghi dmDuplex @94 + bật cờ DM_DUPLEX trong dmFields @72 — chỉ đụng đúng 2 chỗ đó
    /// (DEVMODE có driver data phía sau; ghi nhầm offset là hỏng dmCopies @86).</summary>
    internal static void WriteDuplex(IntPtr devMode, short value)
    {
        Marshal.WriteInt16(devMode, DmDuplexOffset, value);
        Marshal.WriteInt32(devMode, DmFieldsOffset, Marshal.ReadInt32(devMode, DmFieldsOffset) | DM_DUPLEX);
    }

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

    private static Result<DuplexRestore?> FailDuplexWin32(string message, string hint)
        => FailDuplex(message, hint, Marshal.GetLastWin32Error().ToString());

    private static Result<DuplexRestore?> FailDuplex(string message, string hint, string? detail = null)
        => Result<DuplexRestore?>.Fail(new PrintError
        {
            Code = ErrorCodes.SpoolerFailed,
            Category = PrintErrorCategory.Printer,
            Message = message,
            Hint = hint,
            Detail = detail,
        });

    /// <summary>Ghi log chẩn đoán duplex — lỗi restore trong Dispose chỉ vào đây, không ném ra ngoài.</summary>
    private static void DuplexLog(string msg)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "printonator-office.log"),
                $"{DateTimeOffset.Now:O} duplex {msg}\n");
        }
        catch { }
    }
}