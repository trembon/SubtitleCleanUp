namespace SubtitleCleanUp.Web.Services;

public sealed class OperationGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    public bool IsBusy { get; private set; }

    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        IsBusy = true;
        return new Releaser(this);
    }

    private sealed class Releaser(OperationGate owner) : IDisposable
    {
        public void Dispose()
        {
            owner.IsBusy = false;
            owner._semaphore.Release();
        }
    }
}
