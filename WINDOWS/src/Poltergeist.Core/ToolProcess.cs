using System.Diagnostics;
using System.Text;

namespace Poltergeist.Core;

/// <summary>A running ffmpeg / ffprobe whose binary output (and optionally input) we stream.</summary>
public sealed class ToolProcess : IDisposable
{
    public Process P { get; }
    readonly StringBuilder _err = new();

    ToolProcess(Process p) { P = p; }

    public Stream Output => P.StandardOutput.BaseStream;
    public Stream Input => P.StandardInput.BaseStream;

    /// <summary>The last of what the tool printed on stderr, for error messages.</summary>
    public string ErrorText
    {
        get { lock (_err) { var s = _err.ToString(); return s.Length > 2000 ? s[^2000..] : s; } }
    }

    public static ToolProcess Start(string exe, IEnumerable<string> args, bool stdin = false)
    {
        var psi = Proc.Start(exe, args, redirectIn: stdin, redirectOut: true);
        var p = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start " + exe);
        var tp = new ToolProcess(p);
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (tp._err) tp._err.AppendLine(e.Data); };
        p.BeginErrorReadLine();                                    // always drain stderr or the tool can block when it fills
        return tp;
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes unless the stream ends first; returns how many were read.</summary>
    public async Task<int> ReadFullAsync(byte[] buffer, int count, CancellationToken ct = default)
    {
        int got = 0;
        while (got < count)
        {
            int n = await Output.ReadAsync(buffer.AsMemory(got, count - got), ct);
            if (n == 0) break;
            got += n;
        }
        return got;
    }

    public async Task WaitAsync(CancellationToken ct = default)
    {
        await P.WaitForExitAsync(ct);
        P.WaitForExit();                                           // let the stderr reader finish
    }

    public void Dispose()
    {
        try { if (!P.HasExited) P.Kill(true); } catch { }
        P.Dispose();
    }
}
