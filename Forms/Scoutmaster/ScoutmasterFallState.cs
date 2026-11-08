namespace ImScoutmaster;

/// <summary>Only an explicit manual fall can suspend a controlled creature.</summary>
internal sealed class ScoutmasterFallState
{
    internal const float ManualSeconds = 1.25f;
    // Vanilla RPCA_Fall ignores negative shake, so the same RPC remains usable
    // in vanilla rooms. Modded receivers recognize this tag before zeroing it.
    internal const float ManualShakeTag = -1250f;
    private float _manualUntil;

    internal bool ManualActive(float now) => now < _manualUntil;

    internal bool AcceptManualRpc(float seconds, ref float shake, float now)
    {
        if (seconds != ManualSeconds || shake != ManualShakeTag) return false;
        shake = 0f;
        _manualUntil = now + ManualSeconds;
        return true;
    }
}
