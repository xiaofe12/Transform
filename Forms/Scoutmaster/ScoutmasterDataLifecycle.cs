using System.Collections;
using System.Reflection;
using Photon.Pun;

namespace ImScoutmaster;

internal static class ScoutmasterDataLifecycle
{
    private static readonly FieldInfo View = typeof(CharacterData).GetField("m_photonView", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo Instances = typeof(CharacterData).GetField("m_instances", BindingFlags.Static | BindingFlags.NonPublic);

    internal static bool ShouldRunDestroy(CharacterData data)
    {
        // Isolated creature Awake never acquired a player registry entry. Native
        // bot teardown must not remove the real player's entry for a shared owner.
        var view = View?.GetValue(data) as PhotonView;
        var registry = Instances?.GetValue(null) as IDictionary;
        return view != null && view.Owner != null && registry != null
            && registry.Contains(view.Owner) && ReferenceEquals(registry[view.Owner], data);
    }
}
