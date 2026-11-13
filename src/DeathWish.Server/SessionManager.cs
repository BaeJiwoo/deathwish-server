using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using DotNetty.Transport.Channels;

namespace DeathWish.Server;

public sealed class SessionManager
{
    private readonly ConcurrentDictionary<Guid, ClientSession> _sessions = new();

    public int Count => _sessions.Count;

    /// <summary>Returns the registered session. Its connection may close after lookup.</summary>
    public bool TryGetSession(Guid sessionId, [NotNullWhen(true)] out ClientSession? session)
        => _sessions.TryGetValue(sessionId, out session);

    // Registration and removal for one connection run on that channel's event loop.
    internal ClientSession Register(IChannel channel)
    {
        var session = new ClientSession(channel);
        if (!_sessions.TryAdd(session.SessionId, session))
            throw new InvalidOperationException("Session ID collision.");

        return session;
    }

    internal bool Remove(Guid sessionId) => _sessions.TryRemove(sessionId, out _);
}
