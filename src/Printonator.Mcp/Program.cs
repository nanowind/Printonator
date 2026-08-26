using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

namespace Printonator.Mcp;

/// <summary>
/// MCP server Printonator — "AI in giùm".
/// Chạy:  Printonator.Mcp            → HTTP trên http://127.0.0.1:3939/mcp
///        Printonator.Mcp --stdio    → stdio transport (IDE/AI client spawn trực tiếp)
/// An toàn: chỉ bind loopback, KHÔNG CORS, mọi tool đều qua PrintGuard (allowlist/quota/approve).
/// </summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        // Khởi tạo engine in trước khi phục vụ bất kỳ tool nào — không bao giờ "in thành công" mà không có engine
        AppServices.EnsureEngine();

        if (args.Contains("--stdio", StringComparer.OrdinalIgnoreCase))
            await RunStdioAsync();
        else
            await RunHttpAsync();

        AppServices.Dispose();
    }

    /// <summary>stdio transport — dành cho client spawn local (Claude Code/Desktop, IDE...).</summary>
    private static async Task RunStdioAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.AddConsole(opt => opt.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();
        await builder.Build().RunAsync();
    }

    /// <summary>HTTP transport — endpoint http://127.0.0.1:3939/mcp, stateless, loopback-only.</summary>
    private static async Task RunHttpAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions());
        builder.Logging.AddConsole();
        builder.Services
            .AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithToolsFromAssembly();

        var app = builder.Build();
        app.MapMcp("/mcp");

        // Chỉ loopback (127.0.0.1), không mở CORS, không "*" — chặn máy khác trong LAN ra lệnh in
        await app.RunAsync("http://127.0.0.1:3939");
    }
}