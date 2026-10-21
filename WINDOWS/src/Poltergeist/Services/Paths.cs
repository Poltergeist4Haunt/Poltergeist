namespace Poltergeist;

static class Paths
{
    public static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Poltergeist");
    public static string Settings => Path.Combine(Data, "settings.json");
    public static string History => Path.Combine(Data, "history.json");
    public static string ApiKey => Path.Combine(Data, "apikey.bin");
    public static void Log(string text)
    {
        try { Directory.CreateDirectory(Data); File.AppendAllText(Path.Combine(Data, "log.txt"), $"[{DateTime.Now:s}] {text}\n"); } catch { }
    }
    public static string Tools => Path.Combine(Data, "tools");
    public static string Temp
    {
        get
        {
            var d = Path.Combine(Path.GetTempPath(), "Poltergeist");
            Directory.CreateDirectory(d);
            return d;
        }
    }
}
