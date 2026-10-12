using WindowsPilot.Core;

namespace WindowsPilot.Engine.Governance;

public enum ArmScope { read, act, drive }

/// <summary>
/// Arm state machine (design §8.1).
///   DISARMED ──arm(act)──▶ ARMED(act) ──ttl──▶ DISARMED
///   ARMED(drive) is mutually exclusive with act (human owns input).
///   panic ⇒ immediate DISARMED from any state (see KillSwitch).
/// Mutations are impossible unless IsActArmed — every mutation path in
/// PilotEngine calls Guard.Check(scope) BEFORE touching the seam.
/// </summary>
public sealed class ArmState(PilotSettings cfg, EventHub events)
{
    readonly object _gate = new();
    ArmScope _scope = ArmScope.read;
    DateTime? _expires;
    string _why = "";
    long _generation;   // bumps on any transition — cheap staleness signal

    public event Action? Changed;
    public long Generation => Interlocked.Read(ref _generation);

    public bool IsActArmed { get { lock (_gate) return _scope == ArmScope.act && (_expires is null || _expires > DateTime.UtcNow); } }
    public bool IsDriveArmed { get { lock (_gate) return _scope == ArmScope.drive && (_expires is null || _expires > DateTime.UtcNow); } }

    public ArmInfo Info
    {
        get
        {
            lock (_gate)
            {
                var alive = _expires is null || _expires > DateTime.UtcNow;
                return new ArmInfo
                {
                    Armed = alive ? _scope.ToString() : "read",
                    Expires = _expires is null ? null : new DateTimeOffset(_expires.Value.ToUniversalTime()).ToUnixTimeSeconds(),
                    Why = _why,
                };
            }
        }
    }

    public ArmInfo Arm(ArmScope scope, int? ttlMin, string why, bool allowMcpArm)
    {
        // NOTE: caller-side gating — the MCP tool checks cfg.Governance.AllowMcpArm
        // itself; the web console may arm drive. This method is host-agnostic.

        lock (_gate)
        {
            if (scope == ArmScope.act && _scope == ArmScope.drive)
                throw PilotException.DriveLocked();

            _scope = scope;
            var ttl = ttlMin ?? cfg.Governance.ArmTtlMin;
            // ttl <= 0 means "no expiry" (rig auto_arm); null-safe either way
            _expires = scope == ArmScope.read || ttl <= 0 ? null : DateTime.UtcNow.AddMinutes(ttl);
            _why = why;
            Interlocked.Increment(ref _generation);
        }
        events.Push("armed_changed", new { armed = scope.ToString(), why, ttl_min = ttlMin ?? cfg.Governance.ArmTtlMin });
        Changed?.Invoke();
        return Info;
    }

    public ArmInfo Disarm(string why = "")
    {
        lock (_gate)
        {
            _scope = ArmScope.read;
            _expires = null;
            _why = why;
            Interlocked.Increment(ref _generation);
        }
        events.Push("armed_changed", new { armed = "read", why });
        Changed?.Invoke();
        return Info;
    }

    /// <summary>Gate every mutation. Throws NotArmed/DriveLocked with the right hint.</summary>
    public void Check(ArmScope need)
    {
        if (need == ArmScope.read) return;
        if (need == ArmScope.drive)
        {
            if (!IsDriveArmed) throw PilotException.NotArmed("drive");
            return;
        }
        if (!IsActArmed)
        {
            if (Info.Armed == "drive") throw PilotException.DriveLocked();
            throw PilotException.NotArmed("act");
        }
    }

    /// <summary>auto_arm=startup support (dev rigs). Called once after engine init.</summary>
    public void MaybeAutoArm()
    {
        switch (cfg.Governance.AutoArm)
        {
            case PilotSettings.AutoArm.startup:
            case PilotSettings.AutoArm.always:
                if (cfg.Governance.AllowMcpArm || cfg.Rig.Profile)
                    Arm(ArmScope.act, ttlMin: 0, "auto_arm", allowMcpArm: true);  // 0 = no expiry
                break;
        }
    }
}
