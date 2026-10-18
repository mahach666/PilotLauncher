namespace PilotLauncher.Core.Install;

/// <summary>
/// Reports on the calling thread. <see cref="Progress{T}"/> queues every report to the thread pool when it is created
/// without a synchronization context, so a report can arrive after the operation that made it has finished; this adapter
/// forwards one progress type to another without that hop. The window's own <see cref="Progress{T}"/> still moves the
/// final report to the UI thread.
/// </summary>
public sealed class InlineProgress<T> : IProgress<T>
{
    private readonly Action<T> _report;

    public InlineProgress(Action<T> report) => _report = report;

    public void Report(T value) => _report(value);
}
