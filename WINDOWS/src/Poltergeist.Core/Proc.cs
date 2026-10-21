using System.Diagnostics;
using System.Text;

namespace Poltergeist.Core;

/// <summary>Small helpers for running command-line tools (ffmpeg, ffprobe).</summary>
public static class Proc
{
    public sealed record Result(int ExitCode, string StdOut, string StdErr)
    {
        public bool Ok => ExitCode == 0;
    }

    public static ProcessStartInfo Start(string exe, IEnumerable<string> args, bool redirectIn = false, bool redirectOut = true)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirectOut,
            RedirectStandardError = true,
            RedirectStandardInput = redirectIn,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);          // ArgumentList: no quoting mistakes with spaces
        return psi;
    }

    /// <summary>Runs a tool to completion and captures its text output.</summary>
    public static async Task<Result> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct = default)
    {
        using var p = new Process { StartInfo = Start(exe, args) };
        var so = new StringBuilder();
        var se = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) so.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) se.AppendLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        p.WaitForExit();                                         // flush the async readers
        return new Result(p.ExitCode, so.ToString(), se.ToString());
    }
}
