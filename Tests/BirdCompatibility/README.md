# PEAK update and Raven checks

Run against the current game installation after compiling the mod:

```powershell
dotnet build Transform.csproj -c Release -v minimal
dotnet run --project Tests/BirdCompatibility/BirdCompatibility.csproj -c Release -- bin/Release/Thanks.Transform.dll D:/Steam/steamapps/common/PEAK/PEAK_Data/Managed
python -B Tools/inspect_raven_resources.py --peak-root D:/Steam/steamapps/common/PEAK
python -B Tools/verify_raven_audio.py --peak-root D:/Steam/steamapps/common/PEAK
```

The C# executable exercises the production optional-RPC resolver with legacy,
current, inherited and incompatible overloads. It checks the current game's RPC
signatures, stable form IDs and mask, native resource path, absence of an embedded
Raven model, shared player wire-pose integration, and the explicit remote ground
state. It also exercises production Raven ground/flight logic and loop timing at
30–240 FPS: no ground sliding, takeoff, airborne hovering, descending/landing,
missing support and invalid/large time deltas.

The independent UnityPy inspection checks native resource availability, all skin
bones and textures, the original Idle/FlyAway states, and finite sampled poses. It
checks the .3–.8 second flight-loop endpoints after excluding the departure
translation and scale that the production presentation pins in place.
It inventories every native component, checks the self-contained feather particle
leaf and its material/texture, the inactive-at-rest state and FlyAway activation
binding, and verifies that the prefab has no wing trail. It also checks the native
Idle random-phase parameter.

The audio verifier follows the native animation `m_Enabled` bindings to the three
`SFX_PlayOneShot` components, their `SFX_Instance` clip arrays and 3D playback
settings. It decodes each distinct installed AudioClip and verifies non-silent
PCM. It checks that Idle pulses its soft/loud components and that the repeated
flight segment keeps takeoff enabled rather than retriggering it per wing cycle.

These are headless checks. Actual terrain raycasts, foot placement, animation
evaluation in Unity, controller input, audio mixer output and Photon delivery
still require game testing. Check fresh Airport entry, takeoff/hover/landing,
restoring on the ground/in flight, Condor–Raven switching and a late-joining peer.
Updated observers should see the same perched/flight state; vanilla and older
modded observers should see the ordinary player body at the actual flight position.
