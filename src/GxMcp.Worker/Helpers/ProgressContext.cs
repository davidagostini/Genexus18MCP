using System;
using System.Threading;

namespace GxMcp.Worker.Helpers
{
    public static class ProgressContext
    {
        private static readonly AsyncLocal<string> _token = new AsyncLocal<string>();
        private static readonly IDisposable _noop = new NoopDisposable();

        public static string CurrentToken { get { return _token.Value; } }

        public static IDisposable Use(string token)
        {
            string previous = _token.Value;
            if (string.IsNullOrEmpty(token) && string.IsNullOrEmpty(previous))
            {
                return _noop;
            }
            _token.Value = token;
            return new Scope(previous);
        }

        private sealed class Scope : IDisposable
        {
            private readonly string _previous;
            private bool _disposed;
            public Scope(string previous) { _previous = previous; }
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _token.Value = _previous;
            }
        }

        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }
}
