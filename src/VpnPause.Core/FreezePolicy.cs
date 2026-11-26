namespace VpnPause.Core;

public enum FreezeAction
{
    None,
    Freeze,
    Release
}

/// <summary>
/// Decides when to freeze and release. Freezing starts only after the VPN has been down for <c>delay</c>
/// and stops after <c>max</c> more; after that the apps stay released (network is still blocked by filters)
/// until the VPN comes back.
/// </summary>
public sealed class FreezePolicy
{
    DateTime? downSince;

    public bool Expired { get; private set; }

    public TimeSpan DownFor(DateTime now) => downSince is { } d ? now - d : TimeSpan.Zero;

    public FreezeAction Tick(bool active, bool vpnUp, DateTime now, TimeSpan delay, TimeSpan max)
    {
        if (!active || vpnUp)
        {
            downSince = null;
            Expired = false;
            return FreezeAction.Release;
        }
        downSince ??= now;
        var elapsed = now - downSince.Value;
        if (Expired) return FreezeAction.None;
        if (elapsed < delay) return FreezeAction.None;
        if (elapsed >= delay + max)
        {
            Expired = true;
            return FreezeAction.Release;
        }
        return FreezeAction.Freeze;
    }
}
