using System.Collections.Concurrent;
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
/// Cũng chứa ApplyDevMode — đặt duplex / màu / khổ giấy / chiều trong DEVMODE cho Office
/// (Office không có COM duplex/color), gói trong MỘT lease để restore đúng một lần.
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

    // ===================== DEVMODE: duplex + màu + khổ giấy + chiều (PowerPoint/Office thiếu COM) =====================

    private const int DmSizeOffset = 68;           // dmSize  (Int16) — đo trên buffer GetPrinter thật
    private const int DmFieldsOffset = 72;         // dmFields (Int32)
    private const int DmOrientationOffset = 76;    // dmOrientation (Int16)
    private const int DmPaperSizeOffset = 78;      // dmPaperSize (Int16)
    private const int DmPaperLengthOffset = 80;    // dmPaperLength (Int16), đơn vị 1/10 mm  ← TRƯỚC width
    private const int DmPaperWidthOffset = 82;     // dmPaperWidth  (Int16), đơn vị 1/10 mm
    private const int DmPrintQualityOffset = 90;   // dmPrintQuality (Int16): DMRES_* âm, DƯƠNG = DPI
    private const int DmColorOffset = 92;          // dmColor (Int16): 1 = đen trắng, 2 = màu
    private const int DmDuplexOffset = 94;         // dmDuplex (Int16)
    private const int MinDevModeSize = 96;         // DEVMODE phải chứa tới dmDuplex (trường xa nhất ta đụng)
    private const int PDevModeFieldIndex = 7;      // PRINTER_INFO_2.pDevMode (x64: 7*8 = 56)

    // Vùng liền mạch dmFields(72) .. dmDuplex(94) — chụp/restore một khối duy nhất.
    private const int DevModeWindowOffset = DmFieldsOffset;   // 72
    private const int DevModeWindowLength = 24;               // 72..95

    private const int DM_ORIENTATION = 0x00000001; // dmFields
    private const int DM_PAPERSIZE = 0x00000002;   // dmFields
    // Nguồn sự thật: wingdi.h (Windows SDK 10.0.19041.0), dòng 2439-2440 —
    //   #define DM_PAPERLENGTH 0x00000004L
    //   #define DM_PAPERWIDTH  0x00000008L
    // ĐỪNG "sửa lại cho đối xứng": LENGTH là 0x4, WIDTH là 0x8. Đảo hai bit này làm driver
    // nhận cờ sai → SetPrinter fail → cả job rơi về shell, mất luôn màu + duplex + khổ giấy.
    private const int DM_PAPERLENGTH = 0x00000004; // dmFields — wingdi.h:2439
    private const int DM_PAPERWIDTH = 0x00000008;  // dmFields — wingdi.h:2440
    private const int DM_PRINTQUALITY = 0x00000400; // dmFields
    private const int DM_COLOR = 0x00000800;       // dmFields
    private const int DM_DUPLEX = 0x00001000;      // dmFields

    private const short DmOrientPortrait = 1;
    private const short DmOrientLandscape = 2;
    private const short DmColorMonochrome = 1;     // DMCOLOR_MONOCHROME
    private const short DmColorColor = 2;          // DMCOLOR_COLOR
    private const short DmResHigh = -4;            // DMRES_HIGH
    private const short DmResMedium = -3;          // DMRES_MEDIUM
    private const short DmResLow = -2;             // DMRES_LOW
    private const short DmResDraft = -1;           // DMRES_DRAFT

    /// <summary>Trả DEVMODE về cấu hình ban đầu — Dispose để restore.
    /// MỘT lease giữ MỘT handle + MỘT buffer PRINTER_INFO_2 và restore CẢ vùng 72..95 (dmFields..dmDuplex)
    /// trong MỘT SetPrinter: hai lease song song thì lease restore sau cùng sẽ ghi đè cài đặt của lease kia
    /// bằng snapshot cũ hơn (đó là lý do gộp duplex/màu/khổ giấy về một lớp).
    /// ponytail: nếu tiến trình bị kill cứng giữa lúc lease còn sống, cài đặt máy in ở lại giá trị đã đặt;
    /// đường nâng cấp = reconcile lúc khởi động (đã ghi ở docs/BACKLOG.md:49).
    /// </summary>
    public sealed class DevModeLease : IDisposable
    {
        private readonly IntPtr _hPrinter;
        private readonly IntPtr _buffer;           // buffer PRINTER_INFO_2 tròn vẹn (SetPrinter cần đủ driver data)
        private readonly IntPtr _devMode;
        private readonly byte[] _original;         // bản chụp 24 byte vùng 72..95
        private readonly object _lock = new();     // STA thread + timeout path cùng gọi Dispose → guard chống double-free
        private bool _disposed;

        internal DevModeLease(string printerName, IntPtr hPrinter, IntPtr buffer, IntPtr devMode, byte[] original)
        {
            PrinterName = printerName;
            _hPrinter = hPrinter;
            _buffer = buffer;
            _devMode = devMode;
            _original = original;
        }

        /// <summary>Máy in mà lease này đã ghi — khoá của slot trong _activeLeases (không phải khoá cấp phát).</summary>
        public string PrinterName { get; }

        /// <summary>Restore nguyên vùng 72..95 qua MỘT SetPrinter(2) rồi ClosePrinter + LocalFree.
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
                    Marshal.Copy(_original, 0, _devMode + DevModeWindowOffset, DevModeWindowLength);
                    if (!SetPrinter(_hPrinter, 2, _buffer, 0))
                        DevModeLog($"restore devmode: SetPrinter fail err={Marshal.GetLastWin32Error()}");
                }
                catch (Exception ex)
                {
                    DevModeLog($"restore devmode: {ex.Message}");
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
                        // Chỉ bỏ entry NẾU nó VẪN LÀ lease này. TryRemove(key) là remove-theo-khoá →
                        // lease khác đã ghi đè slot cùng máy in (_activeLeases[printerName] = lease) thì
                        // nó xoá entry của lease ĐÓ, lease còn sống mất dấu, timeout không tìm ra để restore
                        // → DEVMODE kẹt cho mọi app. ICollection<KeyValuePair>.Remove trên
                        // ConcurrentDictionary là atomic VÀ so sánh VALUE = remove-if-value-matches.
                        if (!((ICollection<KeyValuePair<string, DevModeLease>>)_activeLeases)
                                .Remove(new(PrinterName, this)))
                            DevModeLog($"lease \"{PrinterName}\" không còn là entry active (lease khác đã ghi đè) — không xoá");
                    }
                }
            }
        }
    }

    /// <summary>Lease DEVMODE đang sống, KHOÁ THEO TÊN MÁY IN — timeout kill STA thread thì Dispose không kịp chạy
    /// → RestoreActive() bên chờ bù. Phải khoá theo máy in: MCP PrintQueue, PrintQueue của UI và WatchFolderService
    /// enqueue độc lập trong CÙNG tiến trình, hai job khác máy in hoàn toàn có thể chồng nhau; restore nhầm lease
    /// của máy in khác đang in giữa PrintOut là hỏng job đó.</summary>
    private static readonly ConcurrentDictionary<string, DevModeLease> _activeLeases =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Dọn lease DEVMODE đang treo khi thread STA bị abandon (timeout/cancel trong
    /// OfficeComPrintEngine) — gọi từ luồng await, KHÔNG thay Dispose của caller (luồng thường vẫn tự restore).
    /// printerName = null → dọn TẤT CẢ (giữ nguyên hành vi cho call site cũ); có tên → chỉ lease của máy in đó.</summary>
    public static void RestoreActive(string? printerName = null)
    {
        if (printerName is not null)
        {
            if (_activeLeases.TryRemove(printerName, out var lease)) lease.Dispose();
            return;
        }

        foreach (var key in _activeLeases.Keys)
            if (_activeLeases.TryRemove(key, out var lease))
                lease.Dispose();   // swallow-and-log như Dispose — không ném ra ngoài
    }

    /// <summary>Cài đặt MẶC ĐỊNH hiện tại của máy in (đọc từ DEVMODE) — dùng cho nút "Lấy theo máy in".
    /// Chỉ là ảnh chụp để điền form; KHÔNG ghi gì và KHÔNG được lưu vào preset.</summary>
    public sealed record PrinterDefaults(
        PrintColorMode ColorMode,
        PrintDuplexMode DuplexMode,
        PrintOrientation Orientation,
        string? PaperName);

    /// <summary>
    /// Đặt DUPLEX + MÀU + KHỔ GIẤY + CHIỀU của một lần in vào DEVMODE driver — Office/PowerPoint không
    /// có COM cho duplex/color/page setup nên phải qua driver. Không có gì phải đặt → Ok(null), KHÔNG mở máy in.
    /// Trả lease khôi phục khi Dispose. Không bao giờ nuốt lỗi: lỗi trả Result.Fail(PrintError).
    /// </summary>
    public static Result<DevModeLease?> ApplyDevMode(string printerName, PrintConfig cfg)
    {
        var wantDuplex = cfg.DuplexMode != PrintDuplexMode.AsPrinter;
        var wantColor = cfg.ColorMode is PrintColorMode.Grayscale or PrintColorMode.Color;
        var wantPaper = !string.IsNullOrWhiteSpace(cfg.PaperSize)
                        && !cfg.PaperSize.Equals(PaperCatalog.AsDocument, StringComparison.OrdinalIgnoreCase)
                        && PaperCatalog.Dimensions(cfg.PaperSize) is not null;
        var wantOrient = cfg.Orientation is PrintOrientation.Portrait or PrintOrientation.Landscape;
        var wantQuality = TryDmPrintQuality(cfg.Quality, out var targetQuality);

        // Không có gì để đặt → không đụng máy in (tên rác vẫn phải Ok(null)).
        if (!wantDuplex && !wantColor && !wantPaper && !wantOrient && !wantQuality)
            return Result<DevModeLease?>.Ok(null);

        if (string.IsNullOrWhiteSpace(printerName))
            return FailDevMode("Chưa có máy in để đặt cấu hình in.", "Chọn máy in trước.");

        short targetDuplex = 0;
        if (wantDuplex && !TryDmDuplex(cfg.DuplexMode, out targetDuplex))
            return FailDevMode($"Chế độ in hai mặt \"{cfg.DuplexMode}\" không hợp lệ.",
                "Dùng AsPrinter / Simplex / LongEdge / ShortEdge.");

        short targetColor = 0;
        if (wantColor && !TryDmColor(cfg.ColorMode, out targetColor))
            return FailDevMode($"Chế độ màu \"{cfg.ColorMode}\" không hợp lệ.",
                "Dùng AsPrinter / AsDocument / Color / Grayscale.");

        if (!OpenPrinter(printerName, out var hPrinter, IntPtr.Zero))
            return FailDevModeWin32($"Không mở được máy in \"{printerName}\" để đặt cấu hình in.",
                "Kiểm tra tên máy in còn tồn tại và spooler đang chạy.");

        var buffer = IntPtr.Zero;
        var leaseGiven = false;   // trả lease = trao quyền sở hữu handle/buffer cho caller
        // SetPrinter đã ghi xong → máy in đang mang DEVMODE đã sửa cho MỌI app; snapshot/devMode phải
        // sống ngoài try để catch còn hoàn lại được (khai báo ở đây vì catch không thấy biến trong try).
        var wrote = false;
        var devMode = IntPtr.Zero;
        byte[]? snapshot = null;
        try
        {
            // Đo size trước (GetPrinter level 2 luôn trả false ở lần gọi rỗng — chỉ đọc cbNeeded).
            GetPrinter(hPrinter, 2, IntPtr.Zero, 0, out var needed);
            if (needed <= 0)
                return FailDevModeWin32($"Không lấy được cài đặt của máy in \"{printerName}\".",
                    "Mở 'Printing Preferences' một lần hoặc cài lại driver máy in.");

            buffer = Marshal.AllocHGlobal(needed);
            if (!GetPrinter(hPrinter, 2, buffer, needed, out _))
                return FailDevModeWin32($"Không đọc được bảng cài đặt (DEVMODE) của \"{printerName}\".",
                    "Mở 'Printing Preferences' một lần hoặc cài lại driver máy in.");

            // PRINTER_INFO_2 xếp tuần tự trong buffer; pDevMode trỏ vào DEVMODE CÙNG buffer này.
            devMode = Marshal.ReadIntPtr(buffer, PDevModeFieldIndex * IntPtr.Size);
            if (devMode == IntPtr.Zero)
                return FailDevMode($"Máy in \"{printerName}\" không trả về bảng cài đặt (DEVMODE).",
                    "Mở 'Printing Preferences' để driver tạo cài đặt, rồi thử lại.");

            if (Marshal.ReadInt16(devMode, DmSizeOffset) < MinDevModeSize)
                return FailDevMode($"Bảng cài đặt của \"{printerName}\" quá ngắn (không đủ ô cài đặt in).",
                    "Cập nhật driver máy in — bản cũ không hỗ trợ các cài đặt này.");

            // Chụp vùng 72..95 NGAY (trước mọi lần ghi) để restore chính xác cả 4 nhóm cài đặt.
            snapshot = new byte[DevModeWindowLength];
            Marshal.Copy(devMode + DevModeWindowOffset, snapshot, 0, DevModeWindowLength);

            var wroteColor = false;

            // Mỗi nhóm tự OR cờ của nó vào dmFields@72 trong buffer ngay tại chỗ ghi — MỘT cơ chế
            // sở hữu dmFields (không có lần ghi gộp cuối), và test byte-isolation chạy đúng đường sản xuất.
            if (wantDuplex && ReadDuplex(devMode) != targetDuplex)
                WriteDuplex(devMode, targetDuplex);

            if (wantOrient)
            {
                Marshal.WriteInt16(devMode, DmOrientationOffset,
                    cfg.Orientation == PrintOrientation.Landscape ? DmOrientLandscape : DmOrientPortrait);
                Marshal.WriteInt32(devMode, DmFieldsOffset, Marshal.ReadInt32(devMode, DmFieldsOffset) | DM_ORIENTATION);
            }

            // Khổ giấy/chiều ghi MỖI KHI user chọn (khác duplex/màu — hai cái đó so sánh trước rồi mới ghi),
            // nên job có khổ giấy luôn lấy lease → luôn SetPrinter ở đây và ở Dispose.
            if (wantPaper)
            {
                var dims = PaperCatalog.Dimensions(cfg.PaperSize)!.Value;
                // dmPaperLength/Width tính bằng 1/10 mm. Luôn mô tả tờ theo chiều DỌC
                // (dmOrientation lo việc xoay) — giống cách GDI in PDF tránh driver clamp.
                Marshal.WriteInt16(devMode, DmPaperLengthOffset, (short)(dims.H * 10));
                Marshal.WriteInt16(devMode, DmPaperWidthOffset, (short)(dims.W * 10));
                // Xoá dmPaperSize để driver dùng width/length thay vì mã chuẩn.
                Marshal.WriteInt16(devMode, DmPaperSizeOffset, 0);
                var fields = Marshal.ReadInt32(devMode, DmFieldsOffset) & ~DM_PAPERSIZE;
                Marshal.WriteInt32(devMode, DmFieldsOffset, fields | DM_PAPERWIDTH | DM_PAPERLENGTH);
            }

            if (wantColor)
            {
                WriteColor(devMode, targetColor);
                wroteColor = true;
            }

            // CỔNG CHẶN (khác color): theo MS DeviceCapabilities(DC_FIELDS), dmFields là lời khai của driver
            // về các thành viên DEVMODE nó hỗ trợ; driver không khai DM_PRINTQUALITY thì ghi vào là vô nghĩa
            // (và .NET cũng không ghi). Bỏ qua + ghi log, KHÔNG làm hỏng job — chất lượng in là tối ưu.
            // CỔNG CHẶN 2: dmPrintQuality hai nghĩa — ÂM là DMRES_* (enum), DƯƠNG là DPI thật (1200, 600...).
            // Driver đang giữ DPI/thang thật thì ghi -3 vào là âm thầm hạ độ phân giải của user, và vì
            // SetPrinter ảnh hưởng MỌI app trong lúc lease sống nên cả máy in bị hạ theo. Chỉ ghi khi giá trị
            // ĐANG LƯU đã là enum (âm); dương/0 = driver tự quản thang chất lượng → bỏ qua + log.
            // ponytail: vẫn chỉ thô 4 nấc DMRES_* và nhiều driver giữ thang chất lượng thật ở chỗ khác
            // (driver data riêng / ICM), nên trên vài máy đây là no-op — dòng log là chẩn đoán.
            // Cái TRẦN mới: driver lưu DPI thì ta KHÔNG đặt được chất lượng nữa (bỏ qua, giữ nguyên driver).
            // Đường nâng cấp: đọc DeviceCapabilities(DC_ENUMRESOLUTIONS), chọn DPI thật gần nhất rồi mới ghi.
            if (wantQuality)
            {
                var storedQuality = Marshal.ReadInt16(devMode, DmPrintQualityOffset);
                if ((Marshal.ReadInt32(devMode, DmFieldsOffset) & DM_PRINTQUALITY) == 0)
                    DevModeLog($"print quality: printer=\"{printerName}\" driver không khai DM_PRINTQUALITY — bỏ qua (giữ chất lượng của driver)");
                else if (storedQuality < 0)
                    WritePrintQuality(devMode, targetQuality);
                else
                    DevModeLog($"print quality: printer=\"{printerName}\" dmPrintQuality={storedQuality} (DPI/thang thật, không phải DMRES_*) — bỏ qua (giữ chất lượng của driver)");
            }

            if (!SetPrinter(hPrinter, 2, buffer, 0))
                return FailDevModeWin32($"Không ghi được cấu hình in vào máy in \"{printerName}\".",
                    "Đặt tay trong 'Printing Preferences' rồi in lại.");

            wrote = true;

            // CHẨN ĐOÁN, không phải cổng chặn: DEVMODE lưu của máy in KHÔNG phải cái Word/Excel/PPT/
            // LibreOffice/shell thật sự in — mỗi app tự dựng DEVMODE tài liệu từ driver. Driver có thể
            // hợp lệ giữ nguyên giá trị lưu mà vẫn tôn trọng giá trị của job (hoặc spooler chuẩn hoá lúc đọc),
            // nên mismatch chỉ trả lời "mặc định lưu có ăn không", KHÔNG trả lời "trang in có ra đen trắng không".
            // Bảo đảm thật nằm ở pixel của các đường render; ở đây chỉ ghi log rồi trao lease như thường.
            if (wroteColor)
            {
                var back = ReadBackColor(hPrinter);
                if (back != targetColor)
                    DevModeLog($"color readback: printer=\"{printerName}\" wrote={targetColor} " +
                               $"read={(back is null ? "unreadable" : back.Value.ToString())}");
            }

            leaseGiven = true;
            var lease = new DevModeLease(printerName, hPrinter, buffer, devMode, snapshot);
            _activeLeases[printerName] = lease;   // timeout path gọi RestoreActive(printerName) nếu STA bị abandon
            return Result<DevModeLease?>.Ok(lease);
        }
        catch (Exception ex)
        {
            // SetPrinter đã thành công mà có gì ném sau đó (đăng ký lease vào _activeLeases) thì máy in
            // vẫn đang mang DEVMODE đã sửa cho MỌI app, và lease chưa nằm trong từ điển nên
            // RestoreActive(printerName) cũng không cứu được → tự hoàn lại ở đây, y hệt DevModeLease.Dispose
            // (cùng 2 dòng, cố ý không tách hàm để lease giữ nguyên). Lỗi hoàn lại chỉ ghi log, không che lỗi gốc.
            if (wrote && snapshot is not null)
            {
                try
                {
                    Marshal.Copy(snapshot, 0, devMode + DevModeWindowOffset, DevModeWindowLength);
                    if (!SetPrinter(hPrinter, 2, buffer, 0))
                        DevModeLog($"restore devmode (apply failed): SetPrinter fail err={Marshal.GetLastWin32Error()}");
                }
                catch (Exception restoreEx)
                {
                    DevModeLog($"restore devmode (apply failed): {restoreEx.Message}");
                }
            }

            return FailDevMode($"Lỗi đặt cấu hình in cho máy in \"{printerName}\".",
                "Đặt tay trong 'Printing Preferences' rồi in lại.", ex.Message);
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

    /// <summary>
    /// Đọc cài đặt MẶC ĐỊNH đang lưu trong driver máy in — CHỈ ĐỌC: không lease, không SetPrinter.
    /// Dùng cho nút "Lấy theo máy in" trong form cài đặt (điền sẵn giá trị rồi user sửa).
    /// Giá trị đọc được là cài đặt ĐANG LƯU trong driver, không phải bảo đảm máy in sẽ in đúng như vậy:
    /// nhiều driver bỏ qua dmColor/dmDuplex khi in thật (mỗi app tự dựng DEVMODE tài liệu của nó).
    /// Khổ giấy đọc qua <see cref="PaperNameFromDevMode"/>: ưu tiên dmPaperWidth/dmPaperLength
    /// (kích thước thật, chính xác nhất), rơi về mã dmPaperSize chuẩn khi driver không set/không khớp.
    /// </summary>
    public static Result<PrinterDefaults?> ReadPrinterDefaults(string printerName)
    {
        if (string.IsNullOrWhiteSpace(printerName))
            return FailPrinterDefaults("Chưa có máy in để đọc cài đặt in.", "Chọn máy in trước.");

        if (!OpenPrinter(printerName, out var hPrinter, IntPtr.Zero))
            return FailPrinterDefaultsWin32($"Không mở được máy in \"{printerName}\" để đọc cài đặt in.",
                "Kiểm tra tên máy in còn tồn tại và spooler đang chạy.");

        var buffer = IntPtr.Zero;
        try
        {
            // Đo size trước (GetPrinter level 2 luôn trả false ở lần gọi rỗng — chỉ đọc cbNeeded).
            GetPrinter(hPrinter, 2, IntPtr.Zero, 0, out var needed);
            if (needed <= 0)
                return FailPrinterDefaultsWin32($"Không lấy được cài đặt của máy in \"{printerName}\".",
                    "Mở 'Printing Preferences' một lần hoặc cài lại driver máy in.");

            buffer = Marshal.AllocHGlobal(needed);
            if (!GetPrinter(hPrinter, 2, buffer, needed, out _))
                return FailPrinterDefaultsWin32($"Không đọc được bảng cài đặt (DEVMODE) của \"{printerName}\".",
                    "Mở 'Printing Preferences' một lần hoặc cài lại driver máy in.");

            var devMode = Marshal.ReadIntPtr(buffer, PDevModeFieldIndex * IntPtr.Size);
            if (devMode == IntPtr.Zero)
                return FailPrinterDefaults($"Máy in \"{printerName}\" không trả về bảng cài đặt (DEVMODE).",
                    "Mở 'Printing Preferences' để driver tạo cài đặt, rồi thử lại.");

            if (Marshal.ReadInt16(devMode, DmSizeOffset) < MinDevModeSize)
                return FailPrinterDefaults($"Bảng cài đặt của \"{printerName}\" quá ngắn (không đủ ô cài đặt in).",
                    "Cập nhật driver máy in — bản cũ không hỗ trợ các cài đặt này.");

            // dmFields = trường driver KHAI là có hiệu lực — mirror của ApplyDevMode (driver không khai
            // DM_COLOR thì dmColor còn lại chỉ là rác, đọc lên sẽ hiện "Màu" cho khả năng driver phủ nhận).
            // dmFields == 0 KHÔNG phải "driver tin hết mọi trường" mà là "driver không khai gì": dmColor/
            // dmPaperWidth/dmPaperLength khi đó là byte chưa khởi tạo, đọc lên có thể ra "A4" hay "Letter"
            // từ rác. Không đọc được trường nào → trả cài đặt trung tính (theo máy in / theo tài liệu).
            var fields = Marshal.ReadInt32(devMode, DmFieldsOffset);
            if (fields == 0)
                return Result<PrinterDefaults?>.Ok(new PrinterDefaults(
                    PrintColorMode.AsPrinter, PrintDuplexMode.AsPrinter,
                    PrintOrientation.AsDocument, null));

            bool Honoured(int mask) => (fields & mask) != 0;

            return Result<PrinterDefaults?>.Ok(new PrinterDefaults(
                ColorFromDm(Honoured(DM_COLOR) ? ReadColor(devMode) : (short)0),
                DuplexFromDm(Honoured(DM_DUPLEX) ? ReadDuplex(devMode) : (short)0),
                OrientationFromDm(Honoured(DM_ORIENTATION) ? Marshal.ReadInt16(devMode, DmOrientationOffset) : (short)0),
                Honoured(DM_PAPERWIDTH | DM_PAPERLENGTH) || Honoured(DM_PAPERSIZE)
                    ? PaperNameFromDevMode(devMode) : null));
        }
        catch (Exception ex)
        {
            return FailPrinterDefaults($"Không đọc được cài đặt của máy in \"{printerName}\".",
                "Mở 'Printing Preferences' một lần rồi thử lại; nếu vẫn lỗi thì cập nhật driver máy in.",
                ex.Message);
        }
        finally
        {
            if (buffer != IntPtr.Zero) LocalFree(buffer);
            ClosePrinter(hPrinter);
        }
    }

    /// <summary>dmColor → PrintColorMode (nghịch đảo TryDmColor); giá trị lạ = theo máy in.</summary>
    private static PrintColorMode ColorFromDm(short dmColor) => dmColor switch
    {
        DmColorMonochrome => PrintColorMode.Grayscale,
        DmColorColor => PrintColorMode.Color,
        _ => PrintColorMode.AsPrinter,
    };

    /// <summary>dmDuplex → PrintDuplexMode (nghịch đảo TryDmDuplex: 1 = DMDUP_SIMPLEX,
    /// 2 = DMDUP_VERTICAL, 3 = DMDUP_HORIZONTAL); giá trị lạ = theo máy in.</summary>
    private static PrintDuplexMode DuplexFromDm(short dmDuplex) => dmDuplex switch
    {
        1 => PrintDuplexMode.Simplex,
        2 => PrintDuplexMode.LongEdge,
        3 => PrintDuplexMode.ShortEdge,
        _ => PrintDuplexMode.AsPrinter,
    };

    /// <summary>dmOrientation → PrintOrientation; giá trị lạ = theo tài liệu.</summary>
    private static PrintOrientation OrientationFromDm(short dmOrientation) => dmOrientation switch
    {
        DmOrientLandscape => PrintOrientation.Landscape,
        DmOrientPortrait => PrintOrientation.Portrait,
        _ => PrintOrientation.AsDocument,
    };

    /// <summary>Tên khổ trong PaperCatalog khớp dmPaperWidth@82 (cạnh ngắn) + dmPaperLength@80 (cạnh dài),
    /// đơn vị 1/10 mm → mm, sai số ±2 mm, so CẢ HAI chiều vì cặp đã lưu có thể bị xoay. Không khớp → null.</summary>
    private static string? PaperNameFromDm(IntPtr devMode)
    {
        var widthMm = (Marshal.ReadInt16(devMode, DmPaperWidthOffset) + 5) / 10;
        var lengthMm = (Marshal.ReadInt16(devMode, DmPaperLengthOffset) + 5) / 10;
        if (widthMm <= 0 || lengthMm <= 0) return null;

        foreach (var display in PaperCatalog.StandardSizes())
        {
            var name = PaperCatalog.SizeName(display);
            if (PaperCatalog.Dimensions(name) is not { } d) continue;
            if ((Math.Abs(widthMm - d.W) <= 2 && Math.Abs(lengthMm - d.H) <= 2)
                || (Math.Abs(widthMm - d.H) <= 2 && Math.Abs(lengthMm - d.W) <= 2))
                return name;
        }
        return null;
    }

    /// <summary>
    /// Tên khổ đọc từ DEVMODE. Ưu tiên kích thước thật (dmPaperWidth/Length, ±2 mm); không khớp khổ
    /// nào HOẶC driver để 0 thì mới rơi về mã dmPaperSize. Driver rất hay set cờ kích thước mà để giá
    /// trị 0 — nếu chỉ xét cờ thì đúng những driver đó vẫn ra null và nút "Lấy theo máy in" lại im lặng.
    /// Cả hai đường đều không đọc được → null (UI giữ "theo máy in").
    /// </summary>
    internal static string? PaperNameFromDevMode(IntPtr devMode)
    {
        var fields = Marshal.ReadInt32(devMode, DmFieldsOffset);
        var byDimension = (fields & (DM_PAPERWIDTH | DM_PAPERLENGTH)) != 0 ? PaperNameFromDm(devMode) : null;
        if (byDimension is not null) return byDimension;
        return (fields & DM_PAPERSIZE) != 0
            ? DmPaperSizeName(Marshal.ReadInt16(devMode, DmPaperSizeOffset))
            : null;
    }

    /// <summary>
    /// Mã dmPaperSize chuẩn → tên khổ trong PaperCatalog. Nguồn: wingdi.h (SDK 10.0.19041.0) 2482-2612.
    /// CHỈ map khổ có trong PaperCatalog VÀ đúng kích thước — sai kích thước còn tệ hơn không biết,
    /// vì giá trị này được ghi thẳng vào cfg.PaperSize rồi đem đi in.
    /// DMPAPER_B5 (13) = B5 JIS 182×257, khớp PaperCatalog["B5"] (đã sửa từ ISO 176×250 → JIS
    /// 182×257 cho đúng những gì Windows/Office dùng).
    /// DMPAPER_B4 (12, JIS 250×354) và DMPAPER_ISO_B4 (42, ISO 250×353) cùng map "B4" (catalog
    /// 250×353): chênh 1 mm, nằm trong dung sai ±2 mm của đường width/length.
    /// Mã lạ (khổ driver riêng, phong bì hiếm) → null để UI giữ "theo máy in", không đoán bừa.
    /// </summary>
    private static readonly IReadOnlyDictionary<short, string> DmPaperSizeNames = new Dictionary<short, string>
    {
        [1] = "Letter",      // DMPAPER_LETTER
        // DMPAPER_TABLOID (3) và DMPAPER_LEDGER (4) cùng là tờ 279×432, catalog không có "Tabloid".
        [3] = "Ledger",
        [4] = "Ledger",
        [5] = "Legal",       // DMPAPER_LEGAL
        [7] = "Executive",   // DMPAPER_EXECUTIVE
        [8] = "A3",          // DMPAPER_A3
        [9] = "A4",          // DMPAPER_A4
        [11] = "A5",         // DMPAPER_A5
        [12] = "B4",         // DMPAPER_B4 (JIS, chênh 1 mm)
        [13] = "B5",         // DMPAPER_B5 (JIS 182×257 — khớp catalog)
        [27] = "DL",         // DMPAPER_ENV_DL
        [28] = "C5",         // DMPAPER_ENV_C5
        [29] = "C3Envelope", // DMPAPER_ENV_C3
        [30] = "C4Envelope", // DMPAPER_ENV_C4
        [31] = "C6",         // DMPAPER_ENV_C6
        [42] = "B4",         // DMPAPER_ISO_B4 (khớp chính xác catalog "B4")
        [66] = "A2",         // DMPAPER_A2
        [70] = "A6",         // DMPAPER_A6
    };

    /// <summary>dmPaperSize → tên khổ PaperCatalog; mã chưa map → null (UI giữ "theo máy in").</summary>
    internal static string? DmPaperSizeName(short dmPaperSize)
        => DmPaperSizeNames.TryGetValue(dmPaperSize, out var name) ? name : null;

    private static Result<PrinterDefaults?> FailPrinterDefaultsWin32(string message, string hint)
        => FailPrinterDefaults(message, hint, Marshal.GetLastWin32Error().ToString());

    private static Result<PrinterDefaults?> FailPrinterDefaults(string message, string hint, string? detail = null)
        => Result<PrinterDefaults?>.Fail(new PrintError
        {
            Code = ErrorCodes.SpoolerFailed,
            Category = PrintErrorCategory.Printer,
            Message = message,
            Hint = hint,
            Detail = detail,
        });

    /// <summary>Đọc lại dmColor từ driver (buffer MỚI) — CHỈ để ghi log chẩn đoán, không phải cổng chặn.
    /// null = không đọc được (buffer tạm luôn được free trước khi trả về).</summary>
    private static short? ReadBackColor(IntPtr hPrinter)
    {
        GetPrinter(hPrinter, 2, IntPtr.Zero, 0, out var needed);
        if (needed <= 0) return null;

        var fresh = Marshal.AllocHGlobal(needed);
        try
        {
            if (!GetPrinter(hPrinter, 2, fresh, needed, out _)) return null;
            var devMode = Marshal.ReadIntPtr(fresh, PDevModeFieldIndex * IntPtr.Size);
            if (devMode == IntPtr.Zero) return null;
            if (Marshal.ReadInt16(devMode, DmSizeOffset) < MinDevModeSize) return null;
            return ReadColor(devMode);
        }
        finally
        {
            Marshal.FreeHGlobal(fresh);
        }
    }

    private static Result<DevModeLease?> FailDevModeWin32(string message, string hint)
        => FailDevMode(message, hint, Marshal.GetLastWin32Error().ToString());

    private static Result<DevModeLease?> FailDevMode(string message, string hint, string? detail = null)
        => Result<DevModeLease?>.Fail(new PrintError
        {
            Code = ErrorCodes.SpoolerFailed,
            Category = PrintErrorCategory.Printer,
            Message = message,
            Hint = hint,
            Detail = detail,
        });

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

    /// <summary>Ánh xạ PrintColorMode → giá trị dmColor (DMCOLOR_*). AsPrinter/AsDocument/rao lung → false.</summary>
    internal static bool TryDmColor(PrintColorMode mode, out short value)
    {
        value = mode switch
        {
            PrintColorMode.Grayscale => DmColorMonochrome,   // DMCOLOR_MONOCHROME
            PrintColorMode.Color => DmColorColor,            // DMCOLOR_COLOR
            _ => 0,
        };
        return value != 0;
    }

    /// <summary>Đọc dmColor — Int16 @ offset 92 của DEVMODE (ngay TRƯỚC dmDuplex@94).</summary>
    internal static short ReadColor(IntPtr devMode) => Marshal.ReadInt16(devMode, DmColorOffset);

    /// <summary>Ghi dmColor @92 + bật cờ DM_COLOR trong dmFields @72 — mirror của WriteDuplex,
    /// chỉ đụng đúng 2 chỗ đó (ghi nhầm offset là hỏng dmPrintQuality @90).</summary>
    internal static void WriteColor(IntPtr devMode, short value)
    {
        Marshal.WriteInt16(devMode, DmColorOffset, value);
        Marshal.WriteInt32(devMode, DmFieldsOffset, Marshal.ReadInt32(devMode, DmFieldsOffset) | DM_COLOR);
    }

    /// <summary>Ánh xạ PrintQuality → giá trị dmPrintQuality (DMRES_*, LUÔN âm: số dương = DPI, mà DPI
    /// driver không có sẽ làm driver từ chối CẢ DEVMODE). AsPrinter/rao lung → false.</summary>
    internal static bool TryDmPrintQuality(PrintQuality q, out short value)
    {
        value = q switch
        {
            PrintQuality.High => DmResHigh,       // DMRES_HIGH
            PrintQuality.Medium => DmResMedium,   // DMRES_MEDIUM
            PrintQuality.Low => DmResLow,         // DMRES_LOW
            PrintQuality.Draft => DmResDraft,     // DMRES_DRAFT
            _ => 0,
        };
        return value != 0;
    }

    /// <summary>Ghi dmPrintQuality @90 + bật cờ DM_PRINTQUALITY trong dmFields @72 — mirror của WriteColor.
    /// Gọi SAU khi đã kiểm tra driver khai DM_PRINTQUALITY (xem ApplyDevMode).</summary>
    internal static void WritePrintQuality(IntPtr devMode, short value)
    {
        Marshal.WriteInt16(devMode, DmPrintQualityOffset, value);
        Marshal.WriteInt32(devMode, DmFieldsOffset, Marshal.ReadInt32(devMode, DmFieldsOffset) | DM_PRINTQUALITY);
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

    /// <summary>Ghi log chẩn đoán DEVMODE — lỗi restore trong Dispose chỉ vào đây, không ném ra ngoài.</summary>
    private static void DevModeLog(string msg)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "printonator-office.log"),
                $"{DateTimeOffset.Now:O} devmode {msg}\n");
        }
        catch { }
    }
}