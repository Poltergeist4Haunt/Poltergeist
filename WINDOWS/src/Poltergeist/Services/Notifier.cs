namespace Poltergeist;

/// <summary>Messages go through Poltergeist's own corner popup; Windows notifications are not used.</summary>
static class Notifier
{
    public static void Init(Action<string, string?> onAction) { }

    public static void Success(string name, string url, byte[]? thumb) { }      // the upload popup already covers this

    public static void Error(string title, string message) =>
        Application.Current.Dispatcher.BeginInvoke(() => PreviewToast.ShowMessage(title, message, error: true));

    public static void Info(string title, string message) =>
        Application.Current.Dispatcher.BeginInvoke(() => PreviewToast.ShowMessage(title, message));

    public static void Shutdown() { }
}
