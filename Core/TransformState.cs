// Global-namespace state on purpose: controller modules live in namespaces (ImZombie, ImGhost,
// ImTumbleweed, ImTornado, ImScoutmaster, Transform.Statue) whose files have "using UnityEngine;",
// where a qualified "Transform.Core.…" would mis-resolve "Transform" to the UnityEngine.Transform
// TYPE. A global type sidesteps that collision for every module.

/// <summary>
/// Shared runtime state of the unified Transform mod, readable from every module namespace.
///
/// <see cref="MenuOpen"/> is set by the TransformMenu while it is open so form controllers freeze
/// their input-driven behaviour (movement, jumps, attacks, mouse-look) — menu clicks must never
/// leak into the active form; camera and maintenance code keep running so page-2 parameter tuning
/// stays live.
///
/// <see cref="AnyFormActive"/> is mirrored from FormRegistry by TransformPlugin every frame and
/// drives the game-wide guards: the reticle suppression and the item-bar/backpack-wheel block.
/// </summary>
internal static class TransformState
{
    /// <summary>The unified menu is open and form input must be suppressed.</summary>
    internal static bool MenuOpen;

    /// <summary>The local player is currently transformed into any form.</summary>
    internal static bool AnyFormActive;
}
