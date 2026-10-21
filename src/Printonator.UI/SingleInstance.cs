using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Threading;
using Microsoft.Win32;
using Printonator.UI.Localization;

namespace Printonator.UI;

/// <summary>
/// Single-instance + shell context menu (T2.8).
/// - Acquire(): mutex để đảm bảo chỉ 1 instance chạy; instance thứ 2 gửi args qua named pipe rồi thoát.
/// - StartServer(): nhận paths từ instance 2 → onPaths (UI thread dispatch ở caller).
/// - RegisterShellMenu()/UnregisterShellMenu(): HKCU "Software\Classes\*\shell\Printonator" — menu chuột phải
///   "In với Printonator" trên MỌI file. HKCU nên không cần admin. Command: exe --print "%1".
/// </summary>
public static class SingleInstance
{
    public const string MutexName = "Printonator_SingleInstance";
    public const string PipeName = "PrintonatorPipe";

    private static readonly CancellationTokenSource _cts = new();
    private static Mutex? _mutex;

    /// <summary>Giành quyền single-instance. instance 2 → false (đã có instance 1 chạy).</summary>
    public static bool Acquire()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
            if (!createdNew)
            {
                _mutex.Dispose();
                _mutex = null;
                return false;
            }
            return true;
        }
        catch
        {
            // Không giành được mutex (vd lỗi hạ tầng) → KHÔNG chặn app mở (user vẫn dùng được chức năng chính).
            _mutex = null;
            return true;
        }
    }

    /// <summary>Instance 2: gửi args (paths đã chọn) cho instance 1 qua named pipe. Lỗi → im lặng.</summary>
    public static void SendArgs(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName);
            client.Connect(2000);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.Write(string.Join("|", args));
        }
        catch { /* instance 1 không nghe / hết hạn — không phải lỗi đáng báo */ }
    }

    /// <summary>Vòng lặp server: nhận 1 dòng paths từ instance 2 → onPaths. Chạy trên task nền.</summary>
    public static void StartServer(Action<string> onPaths)
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName);
                    await server.WaitForConnectionAsync(_cts.Token);
                    if (_cts.IsCancellationRequested) return;
                    using var reader = new StreamReader(server);
                    var line = await reader.ReadLineAsync(_cts.Token);
                    if (!string.IsNullOrEmpty(line)) onPaths(line);
                }
                catch (OperationCanceledException) { return; }
                catch
                {
                    // pipe lỗi tạm thời (vd tiến trình cũ đang restart còn giữ tên pipe) — chờ ngắn rồi thử lại,
                    // tránh hot-spin chiếm CPU.
                    try { await Task.Delay(250, _cts.Token); }
                    catch (OperationCanceledException) { return; }
                }
            }
        });
    }

    /// <summary>Dừng server + nhả mutex (gọi khi app thoát).</summary>
    public static void StopServer()
    {
        _cts.Cancel();
        _mutex?.Dispose();
        _mutex = null;
    }

    /// <summary>Nhả CHỈ mutex NGAY để tiến trình khởi động lại giành được (app restart: gọi trước khi
    /// Process.Start exe mới; nếu không tiến trình mới thua mutex → tự thoát → không còn cửa sổ nào).
    /// KHÔNG cancel `_cts`: pipe server của tiến trình CŨ phải còn sống để nhận args nếu tiến trình mới
    /// thua mutex (instance 3 giành mất) — nếu cancel ở đây, args gửi vào hư không và cửa sổ cũ treo lại.
    /// `_cts` được cancel lúc tiến trình cũ Shutdown → StopServer() (App.xaml.cs Exit).</summary>
    public static void ReleaseForRestart()
    {
        _mutex?.Dispose();
        _mutex = null;
    }

    private static string? ExePath()
        => Environment.ProcessPath ?? Assembly.GetEntryAssembly()?.Location;

    /// <summary>Đăng ký menu chuột phải "In với Printonator" cho MỌI file (HKCU — không cần admin).
    /// Tên menu = Shell.MenuText theo ngôn ngữ đang chọn; command = exe --print "%1".</summary>
    public static void RegisterShellMenu()
    {
        try
        {
            // Installer Inno đã set sẵn registry keys → key còn tồn tại thì không ghi lại, không SHChangeNotify.
            // Lưu ý: nếu exe đổi path (update), command vẫn trỏ path cũ; khi đó cần luôn ghi lại — review giữ đơn giản.
            const string shellKey = @"Software\Classes\*\shell\Printonator";
            using var existing = Registry.CurrentUser.OpenSubKey(shellKey);
            if (existing is not null) return;

            var exe = ExePath();
            if (string.IsNullOrEmpty(exe)) return;

            var baseKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\*\shell\Printonator");
            if (baseKey is null) return;
            using (baseKey)
            {
                baseKey.SetValue("", L10n.S(Keys.Shell.MenuText));
                baseKey.SetValue("Icon", exe);
            }

            using var cmdKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\*\shell\Printonator\command");
            cmdKey?.SetValue("", $"\"{exe}\" --print \"%1\"");

            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
        catch { /* đăng ký lỗi (mất quyền HKCU hiếm) — không làm hỏng app */ }
    }

    /// <summary>Gỡ menu chuột phải (khi user tắt/tùy chọn).</summary>
    public static void UnregisterShellMenu()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\*\shell\Printonator", throwOnMissingSubKey: false);
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
        catch { /* gỡ lỗi — bỏ qua */ }
    }

    private const uint SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
