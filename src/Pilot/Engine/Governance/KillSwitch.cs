namespace WindowsPilot.Engine.Governance;

/// <summary>
/// The deterministic stop (design §8.2). Three independent trigger paths —
/// web STOP button, MCP pilot_panic, in-session hotkey Ctrl+Alt+Pause —
/// any one suffices. Effect, asserted by rig test T-09 at &lt;100 ms:
///   1. disarm-all (ArmState)
///   2. poison the InputMutex: queued injections are DROPPED not executed
///   3. cancel the running action loop — checked between EVERY SendInput event
///      via ShouldContinue(), not just between tool calls.
/// </summary>
public sealed class KillSwitch(EventHub events)
{
    long _trips;
    CancellationTokenSource _cts = new();

    public event Action? Tripped;

    public long Trips => Interlocked.Read(ref _trips);
    public int DroppedInputs;   // counter for verification bundles + T-09 assert

    /// <summary>Caller token — thread into every action loop.</summary>
    public CancellationToken Token { get { lock (_cts) return _cts.Token; } }

    /// <summary>
    /// The ONLY way the injector decides to keep going. Checked between every
    /// individual SendInput event (drag steps, key repeats), never cached.
    /// </summary>
    public bool ShouldContinue() => !Token.IsCancellationRequested;

    public void DropInput() => Interlocked.Increment(ref DroppedInputs);

    public void Trip(string via)
    {
        Interlocked.Increment(ref _trips);
        CancellationTokenSource? old;
        lock (_cts)
        {
            old = _cts;
            _cts = new CancellationTokenSource();   // fresh token for post-panic actions
            try { old.Cancel(); } catch { }
        }
        events.Push("panic", new { via });
        Tripped?.Invoke();
    }
}
