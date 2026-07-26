namespace SubtitleCleanUp.Web.Services;

public sealed class OperationGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private int _isBusy;

    public event Action? StateChanged;

    public bool IsBusy => Volatile.Read(ref _isBusy) == 1;

    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        Volatile.Write(ref _isBusy, 1);
        StateChanged?.Invoke();
        return new Releaser(this);
    }

    private sealed class Releaser(OperationGate owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Volatile.Write(ref owner._isBusy, 0);
            owner._semaphore.Release();
            owner.StateChanged?.Invoke();
        }
    }
}
