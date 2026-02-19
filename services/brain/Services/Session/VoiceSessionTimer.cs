namespace BrainService.Services.Session;

internal sealed class VoiceSessionTimer
{
    private CancellationTokenSource? _cts;

    public void Start(int delayMs, Func<Task> callback)
    {
        Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;

        _ = Task.Delay(delayMs, cts.Token).ContinueWith(
            t => t.IsCanceled ? Task.CompletedTask : callback(),
            TaskScheduler.Default);
    }

    public void Cancel()
    {
        var existing = Interlocked.Exchange(ref _cts, null);
        existing?.Cancel();
        existing?.Dispose();
    }
}