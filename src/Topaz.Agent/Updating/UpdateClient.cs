using System.Net.Http.Headers;
using Topaz.Agent.Configuration;

namespace Topaz.Agent.Updating;

/// <summary>Downloads update files from the central server (station token; long timeout for the zip).</summary>
public sealed class UpdateClient(HttpClient http, AgentOptions options)
{
    private const int MaxSmallFile = 64 * 1024;

    public async Task<byte[]> GetSmallAsync(string token, string version, string rid, string file, CancellationToken ct)
    {
        using var response = await SendAsync(token, version, rid, file, ct);
        if (response.Content.Headers.ContentLength > MaxSmallFile) throw new UpdateRejectedException($"{file} is too large");
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return bytes.Length <= MaxSmallFile ? bytes : throw new UpdateRejectedException($"{file} is too large");
    }

    public async Task DownloadAsync(string token, string version, string rid, string file, string path, long expectedSize, CancellationToken ct)
    {
        using var response = await SendAsync(token, version, rid, file, ct);
        if (response.Content.Headers.ContentLength is { } len && len != expectedSize) throw new UpdateRejectedException($"server sends {len} bytes, manifest says {expectedSize}");
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using (var target = File.Create(path))
        {
            var buffer = new byte[81920];
            long total = 0;
            int n;
            while ((n = await source.ReadAsync(buffer, ct)) > 0)
            {
                total += n;
                if (total > expectedSize) throw new UpdateRejectedException("package is larger than the manifest says");
                await target.WriteAsync(buffer.AsMemory(0, n), ct);
            }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string token, string version, string rid, string file, CancellationToken ct)
    {
        var url = new Uri(new Uri(options.Server.Url.TrimEnd('/') + "/"),
            $"api/v1/agent/updates/{Uri.EscapeDataString(version)}/{Uri.EscapeDataString(rid)}/{Uri.EscapeDataString(file)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            throw new HttpRequestException($"GET {file}: HTTP {(int)response.StatusCode}");
        }
        return response;
    }
}
