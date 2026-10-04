namespace Jarvis.Plugin.Tests;

/// <summary>
/// Records progress reports on the thread that raises them.
/// <para>
/// <see cref="Progress{T}"/> cannot be used to assert on progress: it posts every callback to the captured
/// synchronization context, or to the thread pool when there is none, so a callback can still be queued when
/// the awaited work has already completed. Asserting straight after the await then reads a list that is
/// missing its last entry, which fails intermittently and for no reason connected to the code under test.
/// </para>
/// </summary>
internal sealed class CollectingProgress<T>(Action<T> record) : IProgress<T>
{
	private readonly Action<T> _record = record;

	public void Report(T value) => _record(value);
}