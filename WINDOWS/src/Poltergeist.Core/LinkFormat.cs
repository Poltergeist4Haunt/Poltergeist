namespace Poltergeist.Core;

public enum LinkFormat { Url, Direct, Markdown, BBCode }

public static class LinkFormatExtensions
{
    public static string Title(this LinkFormat f) => f switch
    {
        LinkFormat.Url => "URL",
        LinkFormat.Direct => "Direct URL (with extension)",
        LinkFormat.Markdown => "Markdown",
        LinkFormat.BBCode => "BBCode",
        _ => f.ToString(),
    };

    public static string Format(this LinkFormat f, string url, string name)
    {
        var direct = WithExtension(url, name);
        return f switch
        {
            LinkFormat.Direct => direct,
            LinkFormat.Markdown => $"![{name}]({direct})",
            LinkFormat.BBCode => $"[img]{direct}[/img]",
            _ => url,
        };
    }

    /// <summary>Haunt can hide the extension; add it back from the file name when the URL has none.</summary>
    static string WithExtension(string url, string name)
    {
        var ext = Path.GetExtension(name);
        if (string.IsNullOrEmpty(ext) || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;
        return string.IsNullOrEmpty(Path.GetExtension(u.AbsolutePath)) ? url + ext : url;
    }
}

/// <summary>{file.name}-style variables in embed templates.</summary>
public static class Template
{
    public static readonly Dictionary<string, string> Sample = new()
    {
        ["user.id"] = "1", ["user.name"] = "haunt", ["user.uploads"] = "128", ["user.uploads.today"] = "3", ["user.storage"] = "412 MB",
        ["file.id"] = "a1b2c3d4", ["file.name"] = "a1b2c3d4.png", ["file.originalName"] = "Screenshot.png", ["file.size"] = "793 KB",
        ["file.uploadAt"] = "08/20/2026, 06:00 AM", ["file.url"] = "https://i.haunt.gg/a1b2c3d4", ["file.rawUrl"] = "https://i.haunt.gg/a1b2c3d4.png",
    };

    public static string Render(string s, IReadOnlyDictionary<string, string>? vars = null)
    {
        vars ??= Sample;
        foreach (var kv in vars.OrderByDescending(k => k.Key.Length))       // longest first so {user.uploads.today} beats {user.uploads}
            s = s.Replace("{" + kv.Key + "}", kv.Value);
        return s;
    }
}
