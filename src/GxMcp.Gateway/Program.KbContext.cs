using System;

namespace GxMcp.Gateway
{
    partial class Program
    {
        private static readonly SessionKbContextStore _sessionKbContexts =
            new SessionKbContextStore(TimeSpan.FromMinutes(10));

        internal static string? GetConfiguredDefaultKb()
        {
            string? alias = _activeConfig?.Environment?.DefaultKb;
            if (string.IsNullOrWhiteSpace(alias))
                alias = _activeConfig?.Environment?.ActiveKb;
            return string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();
        }

        internal static bool TryGetSessionSelectedKb(string sessionId, out string? alias)
        {
            return _sessionKbContexts.TryGet(sessionId, out alias);
        }

        internal static string? GetSessionSelectedKb(string sessionId)
        {
            return _sessionKbContexts.Get(sessionId);
        }

        private static void InitializeSessionKbContext(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return;
            _sessionKbContexts.Initialize(sessionId, GetConfiguredDefaultKb());
        }

        internal static void SetSessionSelectedKb(string sessionId, string alias)
        {
            _sessionKbContexts.Set(sessionId, alias);
        }

        internal static void ClearSessionSelectedKb(string sessionId)
        {
            _sessionKbContexts.Clear(sessionId);
        }
    }
}
