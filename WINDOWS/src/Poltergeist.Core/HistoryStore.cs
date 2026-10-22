using System.Collections.ObjectModel;
using System.Text.Json;

namespace Poltergeist.Core;

public sealed record HistoryItem(string Url, string Name, DateTime Date);

/// <summary>The most recent uploads, kept in %APPDATA%\Poltergeist\history.json.</summary>
public sealed class HistoryStore
{
    readonly string _file;
    readonly int _capacity;
    public ObservableCollection<HistoryItem> Items { get; } = new();
    public Action<Action> Post { get; set; } = a => a();

    public HistoryStore(string file, int capacity = 20)
    {
        _file = file;
        _capacity = capacity;
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<List<HistoryItem>>(File.ReadAllText(file)) is { } list)
                foreach (var i in list) Items.Add(i);
        }
        catch { }
    }

    public void Add(string url, string name)
    {
        Post(() =>
        {
            Items.Insert(0, new HistoryItem(url, name, DateTime.Now));
            while (Items.Count > _capacity) Items.RemoveAt(Items.Count - 1);
            Save();
        });
    }

    public void Clear() => Post(() => { Items.Clear(); Save(); });

    /// <summary>Applies the "clear automatically" setting ("day" / "week" drop old entries; "quit" clears everything).</summary>
    public void Prune(string policy, bool quitting = false)
    {
        Post(() =>
        {
            if (policy == "quit" && quitting) { Items.Clear(); Save(); return; }
            var days = policy switch { "day" => 1, "week" => 7, _ => 0 };
            if (days == 0) return;
            var cutoff = DateTime.Now.AddDays(-days);
            for (int i = Items.Count - 1; i >= 0; i--) if (Items[i].Date < cutoff) Items.RemoveAt(i);
            Save();
        });
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(Items.ToList()));
        }
        catch { }
    }
}
