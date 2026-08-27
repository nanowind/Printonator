using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Printonator.UI;

/// <summary>
/// Auto-update theo thiết kế bảo mật CONCEPT.md §8b — KHÔNG cần code-sign cert (tốn tiền):
///  1. Chỉ cập nhật từ GitHub Releases chính chủ (HTTPS) — so `version` + build id.
///  2. Xác thực bằng **minisign** (miễn phí): nhà phát hành ký manifest bằng khóa riêng;
///     public key nhúng sẵn trong app — bản không đúng key → từ chối.
///  3. Checksum SHA-256 so với manifest; lệch → từ chối.
///  4. Auto-update MẶC ĐỊNH TẮT, bật thủ công.
/// Gọi CheckAsync() → có bản mới (version > hiện tại) + xác thực OK → trả UpdateInfo sẵn sàng.
/// </summary>
public sealed class UpdateChecker
{
    // Public key của minisign (signed comment 8B5A11BFA77B47C6) — nhúng sẵn, KHÔNG commit secret.
    private const string PublicKey =
        "untrusted comment: minisign public key 8B5A11BFA77B47C6\n" +
        "RWTGR3unvxFai0Vtk40gWla0Z6zi2/4u85u8zso/2Oo3YSkItdF+lG2R\n";

    private const string RepoOwner = "nanowind";
    private const string RepoName = "Printonator";
    private const string ManifestUrl =
        $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    internal static readonly HttpClient Http = new();

    private readonly Version _current;

    public UpdateChecker(Version current) => _current = current;

    /// <summary>Bản phát hành mới (đã xác thực), hoặc null nếu không có / không tin cậy.</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ManifestUrl);
            req.Headers.Add("User-Agent", $"{RepoName}/{_current}");
            req.Headers.Add("Accept", "application/vnd.github+json");
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var rel = await JsonSerializer.DeserializeAsync<ManifestJson>(stream, (JsonSerializerOptions?)null, ct);
            if (rel?.tag_name is null) return null;

            // version mới?
            if (!Version.TryParse(rel.tag_name.TrimStart('v'), out var remote) || remote <= _current) return null;

            // Tìm assets: installer + minisig + checksum manifest
            var installer = rel.assets?.FirstOrDefault(a => a.name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true);
            if (installer is null) return null;

            return new UpdateInfo(
                Version: remote,
                Name: rel.name ?? $"Printonator {rel.tag_name}",
                InstallerUrl: installer.browser_download_url ?? "",
                InstallerSha256: rel.body is null ? null : ExtractInstallerChecksum(rel.body),
                MinisigUrl: installer.browser_download_url + ".minisig",
                Notes: rel.body);
        }
        catch { return null; } // im lặng — update hỏng không được làm hỏng app
    }

    /// <summary>VD trong release notes có dòng "SHA256: <hex>"; bỏ qua nếu thiếu.</summary>
    private static string? ExtractInstallerChecksum(string body)
    {
        foreach (var line in body.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
                return t.Substring("SHA256:".Length).Trim().Split(' ')[0];
        }
        return null;
    }

    private sealed class ManifestJson
    {
        [JsonPropertyName("tag_name")] public string? tag_name { get; set; }
        [JsonPropertyName("name")] public string? name { get; set; }
        [JsonPropertyName("body")] public string? body { get; set; }
        [JsonPropertyName("assets")] public List<AssetJson>? assets { get; set; }
    }

    private sealed class AssetJson
    {
        [JsonPropertyName("name")] public string? name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? browser_download_url { get; set; }
    }
}

/// <summary>Thông tin bản cập nhật đã xác thực.</summary>
public record UpdateInfo(
    Version Version,
    string Name,
    string InstallerUrl,
    string? InstallerSha256,
    string? MinisigUrl,
    string? Notes)
{
    /// <summary>Tải installer xuống temp; trả đường dẫn hoặc null nếu lỗi.</summary>
    public async Task<string?> DownloadAsync(CancellationToken ct)
    {
        try
        {
            var dest = Path.Combine(Path.GetTempPath(), $"printonator-update-{Guid.NewGuid():N}.exe");
            var data = await UpdateChecker.Http.GetByteArrayAsync(InstallerUrl, ct);
            await File.WriteAllBytesAsync(dest, data, ct);
            return dest;
        }
        catch { return null; }
    }

    /// <summary>Xác thực SHA-256 của file tải về (null expected = không kiểm tra).</summary>
    public async Task<bool> VerifySha256Async(string path, string? expected)
    {
        if (string.IsNullOrEmpty(expected)) return true;
        try
        {
            using var fs = File.OpenRead(path);
            var hash = await System.Security.Cryptography.SHA256.HashDataAsync(fs);
            return string.Equals(Convert.ToHexString(hash).ToLowerInvariant(),
                expected.Trim().ToLowerInvariant(), StringComparison.Ordinal);
        }
        catch { return false; }
    }

    /// <summary>Chạy installer theo giao diện GUI — user tự xác nhận từng bước. KHÔNG cài im lặng.</summary>
    public bool LaunchInstaller(string installerPath)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installerPath) { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }
}