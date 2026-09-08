namespace BetterComputerUse;

internal sealed class DisconnectedSessionRecovery(int parentSession, Func<int?> childSession,
    Func<int, int> connectionState, Func<IDisposable> acquireLease, Action<int> logoff)
{
    internal static DisconnectedSessionRecovery Windows() => new(Native.CurrentSession, Native.ChildSession,
        Native.SessionConnectionState, () => DesktopLease.Acquire(), session => Native.Check(Native.WTSLogoffSession(0, (uint)session, false)));

    internal object Inspect()
    {
        var session = childSession();
        if (session is null) return new { sessionId = (int?)null, canLogoff = false, reason = "No existing child session." };
        try
        {
            using var lease = acquireLease();
            Validate(session.Value);
            return new { sessionId = session, connectionState = "disconnected", canLogoff = true,
                reason = "No task owns this disconnected child session. session_logoff can close it before starting a fresh session; its applications will close." };
        }
        catch (Exception ex) { return new { sessionId = session, canLogoff = false, reason = ex.Message }; }
    }

    private void Validate(int expected)
    {
        if (expected <= 0 || expected == parentSession || childSession() != expected)
            throw new InvalidOperationException("The requested session is not the current child session. Read session_status again.");
        if (connectionState(expected) != 4)
            throw new InvalidOperationException("The child session is not disconnected; refusing recovery logoff.");
    }

    internal async Task<object> LogoffAsync(int expected, CancellationToken cancellation)
    {
        using var lease = acquireLease(); // Competing managers cannot acquire or log off this desktop concurrently.
        Validate(expected);
        cancellation.ThrowIfCancellationRequested();
        logoff(expected);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (childSession() == expected) await Task.Delay(100, timeout.Token);
        return new { loggedOff = true, sessionId = expected, nextAction = "session_start" };
    }
}
