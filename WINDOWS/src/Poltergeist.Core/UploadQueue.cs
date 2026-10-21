using System.Collections.ObjectModel;

namespace Poltergeist.Core;

public enum JobState { Running, Done, Failed }

/// <summary>One row in the Upload list: an upload, or other work (rendering a video) shown with progress.</summary>
public sealed class UploadJob : ObservableObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    /// <summary>Set for real uploads so a failed or cancelled one can be retried.</summary>
    public string? File { get; init; }
    public bool DeleteAfter { get; init; }
    internal CancellationTokenSource? Cts;

    double _progress;
    JobState _state = JobState.Running;
    string? _message, _url;
    public double Progress { get => _progress; set { if (Set(ref _progress, value)) { OnPropertyChanged(nameof(WaitingForServer)); OnPropertyChanged(nameof(StatusText)); } } }
    public JobState State { get => _state; set { if (Set(ref _state, value)) { OnPropertyChanged(nameof(IsRunning)); OnPropertyChanged(nameof(IsDone)); OnPropertyChanged(nameof(IsFailed)); OnPropertyChanged(nameof(WaitingForServer)); OnPropertyChanged(nameof(StatusText)); } } }
    public string? Message { get => _message; set => Set(ref _message, value); }
    public string? Url { get => _url; set => Set(ref _url, value); }

    public bool IsUpload => File != null;
    public bool IsRunning => State == JobState.Running;
    public bool IsDone => State == JobState.Done;
    public bool IsFailed => State == JobState.Failed;
    /// <summary>The whole file has been sent and Haunt hasn't answered yet.</summary>
    public bool WaitingForServer => IsUpload && IsRunning && Progress >= 0.999;
    public bool CanRetry => IsFailed && IsUpload;
    public bool CanCancel => IsRunning && IsUpload;
    public string StatusText => WaitingForServer ? "Waiting for Haunt…" : $"{(int)(Progress * 100)}%";
}

/// <summary>
/// Runs uploads one by one in parallel jobs, with cancel and retry. The app listens to <see cref="Succeeded"/> / <see cref="Failed"/>
/// for the clipboard, sounds and notifications.
/// </summary>
public sealed class UploadQueue
{
    public ObservableCollection<UploadJob> Jobs { get; } = new();
    public Func<string> ApiKey { get; set; } = () => "";
    public Func<bool> StripMetadata { get; set; } = () => true;
    /// <summary>Marshals changes onto the UI thread.</summary>
    public Action<Action> Post { get; set; } = a => a();
    public HauntClient Client { get; } = new();

    public event Action<UploadJob, UploadResult>? Succeeded;
    public event Action<UploadJob, string>? Failed;
    /// <summary>Text for the tray tooltip while uploads run ("" when idle).</summary>
    public event Action<string>? StatusChanged;

    public bool HasActiveUploads => Jobs.Any(j => j.CanCancel);

    public void Enqueue(string file, bool deleteAfter = false)
    {
        Post(() =>
        {
            if (Directory.Exists(file)) { Report(Path.GetFileName(file), "Folders can't be uploaded."); return; }
            if (!File.Exists(file)) { Report(Path.GetFileName(file), "File not found."); return; }
            var job = new UploadJob { Name = Path.GetFileName(file), File = file, DeleteAfter = deleteAfter };
            Jobs.Insert(0, job);
            Launch(job);
        });
    }

    /// <summary>A red error row for something that went wrong before an upload could start.</summary>
    public void Report(string name, string message) =>
        Post(() => Jobs.Insert(0, new UploadJob { Name = name, State = JobState.Failed, Message = message }));

    // ---- rows for other work (rendering) ---------------------------------------------------------------------------

    public UploadJob BeginTask(string name)
    {
        var job = new UploadJob { Name = name };
        Post(() => { Jobs.Insert(0, job); PublishStatus(); });
        return job;
    }

    public void TaskProgress(UploadJob job, double p) => Post(() => { job.Progress = p; PublishStatus(); });

    /// <summary>Removes the row, or leaves it as an error row if <paramref name="error"/> is given.</summary>
    public void EndTask(UploadJob job, string? error = null) => Post(() =>
    {
        if (error != null) { job.State = JobState.Failed; job.Message = error; }
        else Jobs.Remove(job);
        PublishStatus();
    });

    // ---- control ---------------------------------------------------------------------------------------------------

    public void Cancel(UploadJob job)
    {
        Post(() =>
        {
            if (!job.CanCancel) return;
            job.Cts?.Cancel();
            job.State = JobState.Failed;
            job.Message = "Cancelled. If it had already finished sending, it may still show up on Haunt.";
            PublishStatus();
        });
    }

    public void CancelAll() { foreach (var j in Jobs.Where(j => j.CanCancel).ToList()) Cancel(j); }

    public void Retry(UploadJob job)
    {
        Post(() =>
        {
            if (!job.CanRetry) return;
            if (job.File == null || !File.Exists(job.File)) { job.Message = "The file is gone, so it can't be retried."; return; }
            Launch(job);
        });
    }

    public void ClearFinished()
    {
        Post(() =>
        {
            foreach (var j in Jobs.Where(j => !j.IsRunning).ToList())
            {
                if (j.IsFailed && j.DeleteAfter && j.File != null) TryDelete(j.File);     // temp files kept for a possible retry
                Jobs.Remove(j);
            }
        });
    }

    void Launch(UploadJob job)
    {
        job.Progress = 0;
        job.Message = null;
        job.State = JobState.Running;
        var cts = new CancellationTokenSource();
        job.Cts = cts;
        PublishStatus();
        _ = Task.Run(() => RunAsync(job, cts));
    }

    async Task RunAsync(UploadJob job, CancellationTokenSource cts)
    {
        string? cleaned = null;
        try
        {
            var path = job.File!;
            if (StripMetadata() && MetadataStripper.Clean(path) is { } c) { cleaned = c; path = c; }
            var progress = new Progress<double>(p => Post(() => { if (job.IsRunning) { job.Progress = p; PublishStatus(); } }));
            var result = await Client.UploadAsync(path, ApiKey(), new DirectProgress(p => Post(() => { if (job.IsRunning) { job.Progress = p; PublishStatus(); } })), cts.Token);
            if (cts.IsCancellationRequested) return;
            Post(() =>
            {
                job.Progress = 1;
                job.Url = result.Url;
                job.State = JobState.Done;
                if (job.DeleteAfter) TryDelete(job.File!);
                PublishStatus();
                Succeeded?.Invoke(job, result);
            });
        }
        catch (OperationCanceledException) { /* Cancel() already updated the row */ }
        catch (Exception e)
        {
            if (cts.IsCancellationRequested) return;
            var msg = e is HauntException ? e.Message : "Upload failed: " + e.Message;
            Post(() => { job.State = JobState.Failed; job.Message = msg; PublishStatus(); Failed?.Invoke(job, msg); });
        }
        finally
        {
            if (cleaned != null) try { Directory.Delete(Path.GetDirectoryName(cleaned)!, true); } catch { }
        }
    }

    sealed class DirectProgress : IProgress<double>
    {
        readonly Action<double> _a;
        public DirectProgress(Action<double> a) => _a = a;
        public void Report(double value) => _a(value);
    }

    void PublishStatus()
    {
        var active = Jobs.Where(j => j.IsRunning).ToList();
        if (active.Count == 0) { StatusChanged?.Invoke(""); return; }
        var avg = active.Average(j => j.Progress);
        StatusChanged?.Invoke(avg >= 0.999 ? "Waiting for Haunt…" : $"{(int)(avg * 100)}%");
    }

    static void TryDelete(string f) { try { File.Delete(f); } catch { } }
}
