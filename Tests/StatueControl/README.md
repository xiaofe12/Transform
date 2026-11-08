# Statue control regression checks

Run `dotnet run --project Tests/StatueControl/StatueControl.csproj -c Release`.

The test links the production `StatuePhysicsState.cs` and uses managed Unity API
test doubles. It checks body selection, initialization, late native collider
enables in either callback order, recovery from external body state changes,
per-body restoration and repeated entry. It does not run PhysX, Unity lifecycle
callbacks, interpolation or Photon networking.

Evidence from the installed game's decompiled code:

- `Character.WarpPlayer` runs a coroutine that calls
  `refs.ragdoll.ToggleCollision(true)` after several yields. Disabling rider
  colliders only once cannot protect a form that starts during that coroutine.
- `PetrifiedScout.RPC_SpawnPetrifiedScout` writes bodypart transforms directly.
  Entry now calls `Physics.SyncTransforms()` before enabling interpolation.
- The native `PetrifiedScout` prefab contains a live hip rigidbody and inactive
  break-fragment bodies. Only active bodies belong in the movement list.

In-game verification still needed: repeatedly enter statue form immediately
after teleporting or switching forms, then move, sprint, hop, shatter, and exit.
Check ordinary player collisions after exit and remote visibility in a room.
