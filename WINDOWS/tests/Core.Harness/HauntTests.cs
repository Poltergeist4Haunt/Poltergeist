using System.Net;
using System.Text;
using Poltergeist.Core;
using SkiaSharp;

static class HauntTests
{
    sealed class Server : IDisposable
    {
        readonly HttpListener _l = new();
        public string Base { get; }
        public string? LastKey, LastBodyHead;
        public long LastLength;
        readonly CancellationTokenSource _stop = new();

        public Server(int port)
        {
            Base = $"http://127.0.0.1:{port}";
            _l.Prefixes.Add(Base + "/");
            _l.Start();
            _ = Task.Run(Loop);
        }

        async Task Loop()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _l.GetContextAsync(); } catch { return; }
                _ = Task.Run(() => Handle(ctx));
            }
        }

        async Task Handle(HttpListenerContext ctx)
        {
            try
            {
                LastKey = ctx.Request.Headers["x-api-key"];
                using var ms = new MemoryStream();
                await ctx.Request.InputStream.CopyToAsync(ms);
                LastLength = ms.Length;
                LastBodyHead = Encoding.UTF8.GetString(ms.ToArray(), 0, (int)Math.Min(300, ms.Length));
                var path = ctx.Request.Url!.AbsolutePath;
                if (path == "/hang") { try { await Task.Delay(60000, _stop.Token); } catch { } return; }
                if (path == "/401") { await Reply(ctx, 401, "{\"error\":\"Invalid API key.\",\"code\":\"INVALID_API_KEY\"}"); return; }
                if (path == "/413") { await Reply(ctx, 413, "{\"message\":\"File too large for your plan.\"}"); return; }
                if (path == "/junk") { await Reply(ctx, 200, "<html>hello</html>"); return; }
                await Reply(ctx, 200, "{\"url\":\"https://i.haunt.gg/abc123\",\"id\":\"abc123\",\"fileName\":\"abc123\"}");
            }
            catch { }
        }

        static async Task Reply(HttpListenerContext ctx, int status, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }

        public void Dispose() { _stop.Cancel(); try { _l.Stop(); } catch { } }
    }

    public static async Task RunAsync()
    {
        Console.WriteLine("\nUpload client, queue, settings");
        var work = Path.Combine(Path.GetTempPath(), "polt-tests-up");
        Directory.CreateDirectory(work);
        var big = Path.Combine(work, "video.mp4");
        File.WriteAllBytes(big, new byte[6_000_000]);

        using var server = new Server(8766);
        var client = new HauntClient { ReplyTimeout = TimeSpan.FromSeconds(1.5) };

        // ---- the client
        HauntClient.UploadUri = new Uri(server.Base + "/ok");
        double peak = 0;
        var r = await client.UploadAsync(big, "key-123", new Progress0(p => peak = Math.Max(peak, p)), CancellationToken.None);
        T.Check("upload returns the link Haunt answered with", r.Url == "https://i.haunt.gg/abc123" && r.Id == "abc123");
        T.Check("sends the API key header", server.LastKey == "key-123");
        T.Check("sends a multipart body with field 'file' and the file's name", server.LastBodyHead!.Contains("name=\"file\"") && server.LastBodyHead.Contains("filename=\"video.mp4\"") && server.LastBodyHead.Contains("video/mp4"));
        T.Check("the whole file was sent", server.LastLength > 6_000_000);
        T.Check("progress reaches 100%", peak >= 0.999);

        HauntClient.UploadUri = new Uri(server.Base + "/401");
        try { await client.UploadAsync(big, "bad", null, CancellationToken.None); T.Check("401 raises an error", false); }
        catch (HauntException e) { T.Check("Haunt's own error message is shown", e.Message == "Invalid API key.", e.Message); }

        HauntClient.UploadUri = new Uri(server.Base + "/413");
        try { await client.UploadAsync(big, "k", null, CancellationToken.None); T.Check("413 raises an error", false); }
        catch (HauntException e) { T.Check("a 'message' field is used when there's no 'error'", e.Message.Contains("too large"), e.Message); }

        HauntClient.UploadUri = new Uri(server.Base + "/junk");
        try { await client.UploadAsync(big, "k", null, CancellationToken.None); T.Check("non-JSON answer raises an error", false); }
        catch (HauntException e) { T.Check("an unexpected reply is reported clearly", e.Message.Contains("unexpected"), e.Message); }

        try { await client.UploadAsync(big, "  ", null, CancellationToken.None); T.Check("empty key refused", false); }
        catch (HauntException e) { T.Check("no API key: tells you where to set it", e.Message.Contains("Settings")); }

        HauntClient.UploadUri = new Uri(server.Base + "/hang");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { await client.UploadAsync(big, "k", null, CancellationToken.None); T.Check("hanging server times out", false); }
        catch (HauntException e) { T.Check("a server that never answers times out with a clear message", e.Message.Contains("didn't answer") && sw.Elapsed.TotalSeconds is > 1.2 and < 6, $"after {sw.Elapsed.TotalSeconds:0.0}s"); }

        using (var cts = new CancellationTokenSource())
        {
            var t = client.UploadAsync(big, "k", null, cts.Token);
            await Task.Delay(800);
            sw.Restart(); cts.Cancel();
            try { await t; T.Check("cancel stops a hanging upload", false); }
            catch (OperationCanceledException) { T.Check("cancel stops a hanging upload at once", sw.Elapsed.TotalSeconds < 1, $"{sw.Elapsed.TotalMilliseconds:0} ms"); }
        }

        // ---- the queue
        HauntClient.UploadUri = new Uri(server.Base + "/ok");
        var q = new UploadQueue { ApiKey = () => "key-123", StripMetadata = () => false };
        var succeeded = new List<string>();
        q.Succeeded += (j, res) => succeeded.Add(res.Url);
        var png = Path.Combine(work, "shot.png");
        using (var bmp = new SKBitmap(200, 100)) { bmp.Erase(SKColors.HotPink); File.WriteAllBytes(png, SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100).ToArray()); }
        q.Enqueue(png);
        await WaitFor(() => q.Jobs.Count == 1 && q.Jobs[0].IsDone);
        T.Check("queue: an upload finishes and reports its link", q.Jobs[0].IsDone && succeeded.Count == 1 && q.Jobs[0].Url == "https://i.haunt.gg/abc123");

        HauntClient.UploadUri = new Uri(server.Base + "/hang");
        q.Enqueue(big);
        await WaitFor(() => q.Jobs.Count == 2 && q.Jobs[0].WaitingForServer);
        T.Check("queue: at 100% a hanging upload says it is waiting for Haunt", q.Jobs[0].WaitingForServer && q.Jobs[0].StatusText == "Waiting for Haunt…");
        var hanging = q.Jobs[0];
        q.Cancel(hanging);
        T.Check("queue: cancel marks it failed and retryable", hanging.IsFailed && hanging.CanRetry && hanging.Message!.StartsWith("Cancelled"));
        HauntClient.UploadUri = new Uri(server.Base + "/ok");
        q.Retry(hanging);
        await WaitFor(() => hanging.IsDone);
        T.Check("queue: retry succeeds once the server answers", hanging.IsDone && succeeded.Count == 2);

        var temp = Path.Combine(work, "temp.mp4"); File.WriteAllBytes(temp, new byte[1000]);
        HauntClient.UploadUri = new Uri(server.Base + "/401");
        q.Enqueue(temp, deleteAfter: true);
        await WaitFor(() => q.Jobs[0].IsFailed);
        T.Check("queue: a failed upload keeps its temp file for a retry", q.Jobs[0].IsFailed && File.Exists(temp) && q.Jobs[0].Message == "Invalid API key.");
        q.ClearFinished();
        T.Check("queue: Clear finished removes rows and the leftover temp file", q.Jobs.Count == 0 && !File.Exists(temp));
        q.Enqueue(Path.Combine(work, "nope.png"));
        T.Check("queue: a missing file gets an error row", q.Jobs.Count == 1 && q.Jobs[0].IsFailed && q.Jobs[0].Message == "File not found.");

        var task = q.BeginTask("Rendering clip.mp4");
        q.TaskProgress(task, 0.4);
        T.Check("queue: render rows show progress", task.IsRunning && Math.Abs(task.Progress - 0.4) < 1e-9 && !task.IsUpload);
        q.EndTask(task);
        T.Check("queue: a finished render row disappears", !q.Jobs.Contains(task));

        // ---- metadata stripping
        var jpg = Path.Combine(work, "photo.jpg");
        using (var bmp = new SKBitmap(300, 200)) { bmp.Erase(SKColors.Orange); File.WriteAllBytes(jpg, SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Jpeg, 90).ToArray()); }
        var cleaned = MetadataStripper.Clean(jpg);
        using var cleanedBmp = cleaned != null ? SKBitmap.Decode(cleaned) : null;
        T.Check("strip metadata: a JPEG is re-saved with the same size", cleaned != null && cleanedBmp is { Width: 300, Height: 200 } && Path.GetFileName(cleaned) == "photo.jpg");
        T.Check("strip metadata: videos are left alone", MetadataStripper.Clean(big) == null);

        // ---- settings, history, links
        var sf = Path.Combine(work, "settings.json");
        var s = new AppSettings { LinkFormat = LinkFormat.Markdown, Bitrate = 12, SaveScreenshots = true };
        s.SetBinding(HotKeyAction.Area, new HotKeyBinding(0x41, HotKeyBinding.Ctrl | HotKeyBinding.Alt, "A"));
        s.Embed.AuthorName = "me";
        s.Save(sf);
        var back = AppSettings.Load(sf);
        T.Check("settings survive a save and load", back.LinkFormat == LinkFormat.Markdown && back.Bitrate == 12 && back.SaveScreenshots && back.Embed.AuthorName == "me");
        T.Check("a changed hotkey is remembered, others keep their defaults", back.Binding(HotKeyAction.Area).Display == "Ctrl + Alt + A" && back.Binding(HotKeyAction.Fullscreen).Display == "Ctrl + Shift + 3");
        T.Check("missing settings file gives defaults", AppSettings.Load(Path.Combine(work, "none.json")).CopyLink);

        var hf = Path.Combine(work, "history.json"); File.Delete(hf);
        var h = new HistoryStore(hf);
        for (int i = 0; i < 25; i++) h.Add("https://i.haunt.gg/" + i, $"f{i}.png");
        T.Check("history keeps the last 20", h.Items.Count == 20 && h.Items[0].Name == "f24.png");
        T.Check("history is reloaded from disk", new HistoryStore(hf).Items.Count == 20);
        h.Prune("quit", quitting: true);
        T.Check("history 'clear on quit'", h.Items.Count == 0);

        T.Check("link formats", LinkFormat.Markdown.Format("https://i.haunt.gg/abc", "pic.png") == "![pic.png](https://i.haunt.gg/abc.png)"
                && LinkFormat.BBCode.Format("https://i.haunt.gg/abc.png", "pic.png") == "[img]https://i.haunt.gg/abc.png[/img]"
                && LinkFormat.Direct.Format("https://i.haunt.gg/abc", "x.mp4") == "https://i.haunt.gg/abc.mp4"
                && LinkFormat.Url.Format("https://i.haunt.gg/abc", "x.png") == "https://i.haunt.gg/abc");
        T.Check("template variables (longest first)", Template.Render("{user.uploads.today} / {user.uploads}") == "3 / 128");
    }

    static async Task WaitFor(Func<bool> cond, int ms = 8000)
    {
        var t0 = DateTime.UtcNow;
        while (!cond() && (DateTime.UtcNow - t0).TotalMilliseconds < ms) await Task.Delay(30);
    }

    sealed class Progress0 : IProgress<double>
    {
        readonly Action<double> _a;
        public Progress0(Action<double> a) => _a = a;
        public void Report(double v) => _a(v);
    }
}
