using System.Media;

namespace Poltergeist;

/// <summary>The sounds that ship with Windows (C:\Windows\Media), for the success and error choices.</summary>
static class Sounds
{
    public const string None = "None";
    static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");

    public static IReadOnlyList<string> Names
    {
        get
        {
            try
            {
                return Directory.Exists(Folder)
                    ? Directory.GetFiles(Folder, "*.wav").Select(Path.GetFileNameWithoutExtension).Where(n => n != null).Select(n => n!).OrderBy(n => n).ToList()
                    : new List<string>();
            }
            catch { return new List<string>(); }
        }
    }

    public static void Play(string name)
    {
        if (string.IsNullOrEmpty(name) || name == None) return;
        try
        {
            var file = Path.Combine(Folder, name + ".wav");
            if (File.Exists(file)) new SoundPlayer(file).Play();
        }
        catch { }
    }
}
