using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Poltergeist.Core;

public sealed record UploadResult(string Url, string? Id, string? FileName);

/// <summary>An upload problem with a message that is fine to show the user.</summary>
public sealed class HauntException : Exception
{
    public HauntException(string message) : base(message) { }
}

/// <summary>The multipart body ShareX's haunt.gg config sends: field "file", streamed from disk with progress.</summary>
sealed class MultipartFileContent : HttpContent
{
    readonly string _file;
    readonly byte[] _head;
    readonly byte[] _tail;
    readonly long _length;
    readonly IProgress<double>? _progress;
    public event Action? BodySent;

    static readonly Dictionary<string, string> Mime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".webp"] = "image/webp",
        [".avif"] = "image/avif", [".bmp"] = "image/bmp", [".mp4"] = "video/mp4", [".webm"] = "video/webm", [".mov"] = "video/quicktime",
        [".m4v"] = "video/mp4", [".mkv"] = "video/x-matroska",
    };

    public MultipartFileContent(string file, IProgress<double>? progress)
    {
        _file = file;
        _progress = progress;
        var boundary = "Poltergeist-" + Guid.NewGuid().ToString("N");
        var name = Path.GetFileName(file).Replace("\"", "_");
        var mime = Mime.TryGetValue(Path.GetExtension(file), out var m) ? m : "application/octet-stream";
        _head = Encoding.UTF8.GetBytes($"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{name}\"\r\nContent-Type: {mime}\r\n\r\n");
        _tail = Encoding.UTF8.GetBytes($"\r\n--{boundary}--\r\n");
        _length = _head.Length + new FileInfo(file).Length + _tail.Length;
        Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=" + boundary);
    }

    protected override bool TryComputeLength(out long length) { length = _length; return true; }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        await stream.WriteAsync(_head);
        long sent = _head.Length;
        await using (var fs = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, true))
        {
            var buf = new byte[1 << 16];
            int n;
            while ((n = await fs.ReadAsync(buf)) > 0)
            {
                await stream.WriteAsync(buf.AsMemory(0, n));
                sent += n;
                _progress?.Report(Math.Min(1.0, sent / (double)_length));
            }
        }
        await stream.WriteAsync(_tail);
        _progress?.Report(1.0);
        BodySent?.Invoke();                                      // from here on we're only waiting for Haunt's reply
    }
}

public sealed class HauntClient
{
    /// <summary>The same endpoint as ShareX's haunt.gg.sxcu. (Settable so tests can point it at a local server.)</summary>
    public static Uri UploadUri { get; set; } = new("https://haunt.gg/api/imagehost/upload");

    /// <summary>How long to wait for Haunt's reply once the whole file has been sent.</summary>
    public TimeSpan ReplyTimeout { get; set; } = TimeSpan.FromSeconds(240);

    static readonly HttpClient Http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) })
    {
        Timeout = Timeout.InfiniteTimeSpan,                      // we run our own timeouts so a big upload isn't cut off
    };

    public async Task<UploadResult> UploadAsync(string file, string apiKey, IProgress<double>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new HauntException("No API key set. Open Settings → Account.");
        using var content = new MultipartFileContent(file, progress);
        using var req = new HttpRequestMessage(HttpMethod.Post, UploadUri) { Content = content };
        req.Headers.Add("x-api-key", apiKey.Trim());

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bool timedOut = false;
        content.BodySent += () => _ = Task.Run(async () =>
        {
            try { await Task.Delay(ReplyTimeout, cts.Token); timedOut = true; cts.Cancel(); } catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { }
        });

        try
        {
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
            var text = await resp.Content.ReadAsStringAsync(cts.Token);
            JsonElement root = default;
            bool isJson = false;
            try { root = JsonDocument.Parse(text).RootElement; isJson = root.ValueKind == JsonValueKind.Object; } catch { }

            if (!resp.IsSuccessStatusCode)
            {
                string? msg = null;
                if (isJson)
                {
                    if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String) msg = e.GetString();
                    else if (root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) msg = m.GetString();
                }
                throw new HauntException(msg ?? $"Upload failed (HTTP {(int)resp.StatusCode}).");
            }
            if (!isJson || !root.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String)
                throw new HauntException("Haunt returned something unexpected.");
            return new UploadResult(url.GetString()!,
                root.TryGetProperty("id", out var id) ? id.GetString() : null,
                root.TryGetProperty("fileName", out var fn) ? fn.GetString() : null);
        }
        catch (OperationCanceledException) when (timedOut)
        {
            throw new HauntException("Haunt didn't answer in time. The file may still have gone through, so check haunt.gg before retrying.");
        }
        catch (HttpRequestException e) when (e.InnerException is IOException or System.Net.Sockets.SocketException || e.StatusCode == null)
        {
            throw new HauntException("The connection dropped. Check your internet and retry.");
        }
    }
}
