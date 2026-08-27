using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Printonator.Spool.Printing;

/// <summary>
/// Client CHROME DEVTOOLS PROTOCOL tối giản (chỉ dùng BCL — ClientWebSocket, không NuGet):
/// bật browser headless → bắt port debug từ stderr → nối websocket → chờ trang sẵn sàng
/// → Page.printToPDF (pageRanges/scale/khổ giấy/chiều) → PDF base64.
/// LƯU Ý đã verify thực nghiệm: PDF mở ra có TOP FRAME = file://.pdf (native renderer, KHÔNG có
/// pdf.js/PDFViewerApplication kể cả headful) → pageRanges CDP bị từ chối; PDF page-SLICING do
/// WindowsPdfRasterizer (Windows.Data.Pdf) đảm nhiệm — không phải browser.
/// </summary>
public sealed class DevToolsPrintClient
{
    private const string BrowserReadyRegex = @"DevTools listening on ws://127\.0\.0\.1:(\d+)/";

    /// <summary>
    /// Render file (file:// URL — HTML/ảnh/TXT/PDF) thành PDF qua headless browser.
    /// Trả base64 PDF hoặc lỗi rõ ràng.
    /// </summary>
    public static async Task<(bool Ok, string? Base64Pdf, string? Error)> PrintPdfAsync(
        string browserPath,
        string fileUrl,
        Dictionary<string, object?> printParams,
        string profileDir,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        var session = await LaunchAndConnectAsync(browserPath, fileUrl, profileDir, ct, timeout);
        try
        {
            if (session.Error is not null) return (false, null, session.Error);
            var (_, ws, id) = (session.Proc, session.Ws!, session.Id);

            var enableResp = await SendAsync(ws, ++id, "Page.enable", null, ct);
            if (!enableResp.Contains("\"result\"", StringComparison.Ordinal))
                return (false, null, "Page.enable không được trả lời.");

            var (ready, lastId) = await WaitReadyAsync(ws, id, ct);
            if (!ready) return (false, null, "Trang chưa sẵn sàng trong thời gian cho phép.");
            id = lastId;

            var resp = await SendAsync(ws, ++id, "Page.printToPDF", printParams, ct);
            using var doc = JsonDocument.Parse(resp);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
                return (false, null, $"printToPDF lỗi: {err.GetProperty("message").GetString()}");
            if (!root.TryGetProperty("result", out var result) || !result.TryGetProperty("data", out var data))
                return (false, null, "printToPDF không trả dữ liệu PDF.");
            return (true, data.GetString(), null);
        }
        finally
        {
            Cleanup(session.Proc, session.Ws);
        }
    }

    private sealed record Session(Process? Proc, ClientWebSocket? Ws, int Id, string? Error);

    private static async Task<Session> LaunchAndConnectAsync(
        string browserPath, string fileUrl, string profileDir, CancellationToken ct, TimeSpan? timeout)
    {
        var totalTimeout = timeout ?? TimeSpan.FromSeconds(120);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(totalTimeout);

        Process? proc = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = browserPath,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"--headless=new --remote-debugging-port=0 " +
                            $"--user-data-dir=\"{profileDir}\" --no-first-run --disable-gpu " +
                            $"--disable-pdf-first-run --no-default-browser-check --mute-audio " +
                            $"--safebrowsing-disable-auto-update --disable-background-networking " +
                            $"\"{fileUrl}\"",
            };
            proc = Process.Start(psi);
            if (proc is null)
                return new Session(null, null, 0, $"Không khởi động được {Path.GetFileName(browserPath)}.");

            var (port, stderr) = await ReadDebugPortAsync(proc, cts.Token);
            if (port <= 0)
                return new Session(proc, null, 0,
                    proc.HasExited
                        ? $"{Path.GetFileName(browserPath)} thoát sớm (exit {proc.ExitCode}).\n{stderr}"
                        : "Không đọc được port DevTools — browser lạ/phiên bản cũ?");

            var wsUrl = await GetPageWebSocketUrlAsync(port, cts.Token);
            if (wsUrl is null)
                return new Session(proc, null, 0, "Không tìm thấy trang cần in trong DevTools.");

            var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(wsUrl), cts.Token);
            return new Session(proc, ws, 0, null);
        }
        catch (OperationCanceledException)
        {
            return new Session(proc, null, 0, "In qua browser quá lâu — đã dừng.");
        }
        catch (Exception ex)
        {
            return new Session(proc, null, 0, $"Lỗi kết nối DevTools: {ex.Message}");
        }
    }

    private static void Cleanup(Process? proc, ClientWebSocket? ws)
    {
        try { ws?.Dispose(); } catch { }

        // KHÔNG kill(entireProcessTree:true) — nó giết cả cây Chrome, có thể lan sang Chrome THẬT
        // của user đang mở → tab bị "Aw Snap / RESULT CODE KILLED". Headless print dùng user-data-dir
        // riêng nên chỉ cần đóng process con do MÌNH tạo, an toàn. Nếu nó chưa thoát thì sau đó
        // force-kill đúng PID (không kéo theo tree), rồi chờ.
        if (proc != null)
        {
            try
            {
                // Ưu tiên thoát nhẹ nhàng: gửi SIGTERM tương đương (CloseMainWindow chỉ hợp window) —
                // headless không có window, nên chỉ Kill đúng PID này, KHÔNG entireProcessTree.
                proc.Kill();
                proc.WaitForExit(5000);
            }
            catch { }
            try { proc.Dispose(); } catch { }
        }
    }

    private static async Task<(int Port, string Stderr)> ReadDebugPortAsync(Process proc, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var started = DateTimeOffset.Now;
        try
        {
            // Đọc từng dòng stderr — đọc TRƯỚC rồi mới check HasExited (tránh race: browser
            // in dòng DevTools xong mới thoát; line đệm vẫn đọc được).
            while (DateTimeOffset.Now - started < TimeSpan.FromSeconds(20))
            {
                var line = await proc.StandardError.ReadLineAsync(ct);
                if (line is null) break; // EOF = process đã đóng stderr
                var m = System.Text.RegularExpressions.Regex.Match(line, BrowserReadyRegex);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var p) && p > 0) return (p, sb.ToString());
                sb.AppendLine(line);
            }
        }
        catch (Exception ex) { sb.AppendLine(ex.Message); }
        return (0, sb.ToString());
    }

    private static async Task<string?> GetPageWebSocketUrlAsync(int port, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            var json = await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", ct);
            using var doc = JsonDocument.Parse(json);
            foreach (var t in doc.RootElement.EnumerateArray())
            {
                if (t.TryGetProperty("type", out var ty) && ty.GetString() == "page"
                    && t.TryGetProperty("webSocketDebuggerUrl", out var ws))
                    return ws.GetString();
            }
        }
        catch { }
        return null;
    }

    private static async Task<(bool Ready, int LastId)> WaitReadyAsync(ClientWebSocket ws, int id, CancellationToken ct)
    {
        var poll = 0;
        while (poll < 60) // ~30s
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                // readyState 'complete' là đủ cho mọi nội dung (PDF native renderer cũng báo complete)
                var resp = await SendAsync(ws, ++id, "Runtime.evaluate",
                    new Dictionary<string, object?>
                    {
                        ["expression"] = "document.readyState === 'complete'",
                        ["returnByValue"] = true,
                    }, cts.Token);

                using var doc = JsonDocument.Parse(resp);
                if (doc.RootElement.TryGetProperty("result", out var r) &&
                    r.TryGetProperty("result", out var value) &&
                    value.TryGetProperty("value", out var v) && v.GetBoolean())
                {
                    await Task.Delay(400, ct); // cho renderer kịp dựng trang đầu
                    return (true, id);
                }
            }
            catch (OperationCanceledException) { }
            poll++;
            await Task.Delay(500, ct);
        }
        return (false, id);
    }

    private static async Task<string> SendAsync(ClientWebSocket ws, int id, string method,
        Dictionary<string, object?>? parameters, CancellationToken ct)
    {
        var payload = parameters is null
            ? $"{{\"id\":{id},\"method\":\"{method}\"}}"
            : $"{{\"id\":{id},\"method\":\"{method}\",\"params\":{JsonSerializer.Serialize(parameters)}}}";

        var bytes = Encoding.UTF8.GetBytes(payload);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, ct);

        var buffer = new byte[65536];
        var sb = new StringBuilder();
        while (true)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(40));
            var segment = new ArraySegment<byte>(buffer);
            var received = await ws.ReceiveAsync(segment, cts.Token);
            if (received.MessageType == WebSocketMessageType.Close) break;
            sb.Append(Encoding.UTF8.GetString(buffer, 0, received.Count));
            if (!received.EndOfMessage) continue;

            var text = sb.ToString();
            sb.Clear();
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("id", out var rid) && rid.GetInt32() == id)
                return text;
        }
        return "{}";
    }
}