using System.Reflection;

namespace ImZombie;

// The zombie owns movement/render identity, but has no player voice/inventory lifecycle.
// Native GUIManager and its nested calls must use the parked real player's UI references.
internal sealed class ZombieGuiContext
{
    private static readonly PropertyInfo SpectatorProperty = typeof(MainCameraMovement).GetProperty("specCharacter", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    private readonly Character _zombie;
    private readonly Character _player;
    private readonly Character _spectator;
    private readonly bool _changedSpectator;

    private ZombieGuiContext(Character zombie, Character player)
    {
        _zombie = zombie;
        _player = player;
        _spectator = MainCameraMovement.specCharacter;
        _changedSpectator = _spectator == zombie;
        if (_changedSpectator) SpectatorProperty?.SetValue(null, player, null);
        if (player.input != null && zombie.input != null)
            player.input.pauseWasPressed = zombie.input.pauseWasPressed;
        Character.localCharacter = player;
    }

    internal static ZombieGuiContext Begin()
    {
        Character zombie = ZombieController.ActiveZombieCharacter;
        Character player = ZombieController.ParkedPlayerCharacter;
        return zombie != null && player != null && Character.localCharacter == zombie
            ? new ZombieGuiContext(zombie, player) : null;
    }

    internal void End()
    {
        // A menu callback may restore the player or enter another form during LateUpdate.
        // Never put a retired zombie or a previous spectator back into that new session.
        bool stillOwned = _zombie != null && _player != null
            && ReferenceEquals(ZombieController.ActiveZombieCharacter, _zombie)
            && ReferenceEquals(ZombieController.ParkedPlayerCharacter, _player);
        bool restoredIdentity = false;
        if (stillOwned && Character.localCharacter == _player)
        {
            if (_player.input != null && _zombie.input != null)
                _zombie.input.pauseWasPressed = _player.input.pauseWasPressed;
            Character.localCharacter = _zombie;
            restoredIdentity = true;
        }
        // If the form ended, leave the safe player target for the next camera update.
        // A new independent form may already own that very same observed player.
        if (_changedSpectator && restoredIdentity && MainCameraMovement.specCharacter == _player)
            SpectatorProperty?.SetValue(null, _spectator, null);
    }
}
