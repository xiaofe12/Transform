# Condor network compatibility audit

Audited against the installed PEAK 3.0.1 assemblies. The following findings come
from code inspection and payload tests, not a live multiplayer session.

| Peer | Position and appearance | Grab behavior |
| --- | --- | --- |
| Unmodded host | Receives ordinary CharacterSyncData at the real flight position; sees the player body | Original attach/detach RPCs execute without a host-mod requirement |
| Unmodded observer | Receives the same real position; no handler for the custom bird event | Sees native body/joint behavior |
| Unmodded grabbed player | Receives real carrier position; has the vanilla right-hand rig | Original grab/release works at protocol level; exact talon anchoring is unavailable |
| Modded peer | Custom event drives bird visuals and hides body renderers | Its remote hand is pinned to the talons by the mod |

## Confirmed issues repaired

- The old GetDataToWrite postfix subtracted 30 m from the hip while free flying,
  then restored it before grabbing and for a short period after releasing. Vanilla
  peers had no patch to compensate for these position jumps. Current packets always
  carry the real flight position, including on a fresh join and during grab/drop.
- The wire pose now uses the authoritative flight center, a hip offset saved at
  entry, and fixed-step velocity, rather than an animated bone's instantaneous pose.
- Remove obsolete delayed grabs that existed solely to wait for the buried body
  to return. Body visibility on modded peers is independent of wire position.
- If native StopReaching destroys a joint without clearing grabbedPlayer, release
  the orphaned state on every peer with RPCA_GrabUnattach.

## Protocol limits and manual verification

CharacterSyncData contains hip position and average velocity, but no individual
hand pose. RPCA_GrabAttach creates the joint on each client's copy of the carrier's
right hand. Only modded peers run the talon anchor correction. A vanilla victim's
hand anchor/physics can differ, especially at high speeds or latency. The victim's
local fall-timer protection is likewise only guaranteed by the mod on modded peers.
Do not interpret the native RPC's availability as proof of stable carrying on an
unmodded victim. No additional artificial position/force RPCs are sent to compensate.

Actual bird appearance/animations/trails require this mod on the viewing client.
The Condor is a scene object, not a Photon Resources prefab like Beetle; a custom
event alone cannot make an unmodded client create it. No scene Condor is taken over.

## Why native Beetle spawning cannot be reused directly

Rechecked the installed game's actual resource index and PUN implementation:

- `globalgamemanagers` contains a `ResourceManager` with 350 entries (345 unique paths).
  `0_items/beetle` resolves to a GameObject; none of the paths contains `condor`.
  `ravenvoicethief` and raven egg items are different assets.
- `CritterController.SpawnCritter` calls
  `PhotonNetwork.Instantiate("0_Items/Beetle", ...)`. PUN sends the prefab name,
  and each receiver's `DefaultPool.Instantiate` calls `Resources.Load` for that
  name on its own machine. It does not transmit mesh/material/component data.
  Registering Condor in only the modded peer's pool would leave vanilla receivers
  unable to resolve the prefab.
- The inspected `level4` Condor is a scene object with a scene PhotonView and no
  serialized observed components. Its MonoBehaviour scripts are Condor,
  PhotonView and DisableBasedOnRunSettings. Condor has no `IPunObservable` /
  `OnPhotonSerializeView` implementation; runtime auto-discovery therefore adds
  no transform serializer on this object.
- `Condor.FixedUpdate` runs flight movement on every client. `GetPosition` derives
  the route from `RunManager.Instance.TimeSinceRunStarted`, the initial scene
  position, radius, seed and loop duration. The native RPC
  `RPCA_CondorAction(characterView, action)` selects Swoop, Carry, LetGo or GiveUp;
  it does not accept an arbitrary flight position or rotation.

Consequently, the existing API does not provide Beetle-style spawning/control for
an arbitrary player-driven Condor in an unmodded Airport or map. Reusing an existing
scene Condor's action RPCs is a separate, map-dependent feature with the original
AI route/carry rules; taking ownership or moving one local copy alone does not
replace movement logic on unmodded clients. This is a limit of the inspected native
entry points, not a claim that every conceivable redesign is impossible.

Repeat the read-only asset inspection with:

```powershell
python Tools/inspect_condor_network.py --peak-root D:/Steam/steamapps/common/PEAK
```

The script's PhotonView layout parser deliberately rejects changed layouts or a
new observed-component configuration instead of silently retaining this conclusion
after a game update.

Manual multiplayer checks still needed:

- Unmodded host plus modded carrier: ascend/descend/stop/restore; verify the host
  never sees underground/30 m position transitions.
- Unmodded victim: grab, fly horizontally/vertically, drop, victim jump-to-release,
  victim death/disconnect, and restore form while holding. Check anchor differences,
  unwanted forces and fall damage, including under latency.
- Mixed room and late join: modded observers see the bird; vanilla observers see
  the player; both recover the normal body/position after the carrier exits.

## Automated check

Run from the repository root:

```powershell
dotnet run --project Tests/CondorNetwork/CondorNetwork.csproj -c Release
```

The executable links production FlightMotion and CondorNetworkPose, and references
the installed game's actual CharacterSyncData type. It checks real hip positions,
velocity, vanilla locomotion flags, preserved look/stamina data and reset on reentry.
It does not simulate Photon delivery, remote joints or game physics.
