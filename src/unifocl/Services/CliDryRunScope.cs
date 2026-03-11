using System.Threading;

internal static class CliDryRunScope
{
    private static readonly AsyncLocal<int> DryRunDepth = new();

    public static bool IsEnabled => DryRunDepth.Value > 0;

    public static IDisposable Push(bool enabled)
    {
        if (!enabled)
        {
            return NoopDisposable.Instance;
        }

        DryRunDepth.Value = DryRunDepth.Value + 1;
        return new ScopePopDisposable();
    }

    private sealed class ScopePopDisposable : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            var current = DryRunDepth.Value;
            DryRunDepth.Value = current > 0 ? current - 1 : 0;
            _disposed = true;
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
