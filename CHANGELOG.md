# Changelog

## Unreleased

- Reduce the unified menu hold threshold from 1 second to 0.4 seconds; retain short-press transform/restore and suppress the tap action after a menu hold. Update configuration help and startup guidance.

- Add short card cautions for pickup duplication with an unmodded host, Condor visibility, statue shattering and Tornado player displacement. Keep full explanations in the selected-form panel. Restore the On/Off label and sliding thumb, changing only the old oval track to a rectangular track at the upper right; keep title, caution and availability rows separate.
- Remove the restore/menu shortcut row from transformation hints and exclude it from size measurements, keeping the remaining controls anchored to the bottom right. Use gold key labels and near-white actions with a dark outline around both columns for contrast over bright terrain and dark scenery; retain the transparent background and resolution fitting.
- Center room-policy/host notices in the top toolbar and remove the old full-width content banner. Move the message to a centered second toolbar row when space is limited. Shorten the English Mushroom-Man form name to leave room for the card control.

- Keep form cards free of ability descriptions and restore the selected-form panel's full controls, compatibility notes and pickup warnings in a scrollable information area. Keep unavailable-state explanations and the separate in-game operation hints.
- Remove repeated Select/Selected labels from form cards and the selected-form heading. Restore an independent On/Off control to every form card at the upper right with green/gray state and On/Off text. Guests see a read-only switch. Keep its click area separate from card selection and double-click transformation.
- Increase transformed-form control hint text to a tutorial-sized, responsive label. Font size now follows both screen height and width, long controller bindings wrap within the measured right-bottom block, and the font is reduced only as needed to keep every hint row inside the safe screen area.
- Restyle the menu as a dark outdoor-equipment panel with warm orange selections/actions, compact form cards with individual room permissions, and a selected-form panel containing availability reasons, Transform/Restore and settings. Keep double-click transformation and existing shortcut behavior.
- Keep room rules, scale and the “提示：开/关” switch across the top, with Restore player in the footer on both pages. Stack details below the collection on narrow windows, scroll settings lists/parameters independently, and fit saved scale preferences to the current screen without rewriting them. Disable reset as well as numeric editing when settings belong to the host.

- Fix the player's face, hat and body covering the view after leaving Scoutmaster. Capture and restore the source renderers' original shader visibility/opacity values instead of forcing full creature visibility; refresh native HideTheBody on return while yielding to external free cameras. Repeated masking and material/cosmetic replacements preserve the original values. No camera render-layer mask is changed.
- Stop the look-state copy from rotating the restored player's parent transform a second time; body placement owns pose changes, while look copying updates view data only.

- Fix Scoutmaster's parent-before-child body warp: capture all world poses before writing any transform and restore parts in hierarchy order. Finite but stretched head/body poses no longer pass unnoticed through the old warp path.
- Follow SEE's rig-only player-body following while keeping the native player root intact. Preserve a separate entry-pose snapshot for exit, clear queued forces and reseed body animation/history before physics resumes, and suspend/restore the source's interpolation with its physics state. Keep the existing source-stash broadcast/freeze order, controls, view-based throws, spawn warmup and external-camera handoff.
- Validate Scoutmaster's captured camera FOV, recover an invalid native FOV smoothing cache, and log camera/head/torso/clip/matrix details if scoped restoration still finds a bad camera. Remove the speculative global mirror-projection fallback; the latest log starts its persistent camera errors directly after Scoutmaster exit.

- Fix native GUI player-component reads during Zombie forms by temporarily supplying the original player for the GUI update and restoring creature identity even on exceptions. Preserve pause consumption and foreign spectator ownership, including a form exit inside the update.
- Harden Zombie ragdoll teleport/copy, look directions, camera smoothing and exit blends against nonfinite values and unusable rotations. Seed native camera caches on return, yield an exit blend when an external camera activates, and suspend only the native camera movement driver while transformed.
- Extend camera recovery to invalid projection/view/culling matrices and prevent Scoutmaster's background camera patrol from resetting another active form's view.

- Move the control-hints switch immediately after the scale selector in the title bar, labeled “提示：开/关” (Hints: On/Off). Remove its standalone toolbar row; at narrow widths keep the scale/hints pair together in an extra header row to prevent overlap.

- Remove form names and their reserved heading space from transformation control hints.
- Fix Scoutmaster exit camera handoff: restore view angles and directions from the same gaze, align the restored body to that yaw, seed the native head offset once instead of repeatedly resetting smoothing, and defer restoration while another camera owns the view. Disable the retiring creature's physics before restoring the overlapping source body.
- Keep native observation on the original player while the independent Scoutmaster camera follows the creature, avoiding native spectator UI reads of incomplete creature player data. Guard CharacterData.OnDestroy by actual ownership of the player-data registry entry; isolated creature teardown no longer dereferences an uninitialized view or removes the source player's entry.
- Prevent collision/trigger-driven Scoutmaster falls and pass-outs from trapping creature control. Recognize explicit manual falls separately, retain the native fall RPC for vanilla peers, and clear unintended incapacitation outside the bounded manual-fall window. Preserve the manual-fall key and other form controls.

- Fix the control-hint heading drifting away from its rows: measure the key/action columns instead of anchoring a fixed-width empty panel, align the heading to the same text block, and explicitly set action text alignment. Add a subtle shadow to light labels for visibility against bright scenery.
- Restore missing Condor flight audio in the installed-resource loader: reconstruct both original spatial loop sources, route them through the game SFX mixer, and retain the original animation volume curves for gliding/flapping. Read the original FSB bytes from checked game-file ranges and let Unity decode a transient in-memory AudioClip bundle; no audio payload or extra runtime library is bundled. Unload owned audio with the visual resources.
- Move transformation control hints to the bottom right, remove the background box, and use darker bold keys with aligned light action labels. Move the persistent, default-enabled control-hints switch to a shared toolbar at the top of the menu so both pages expose it.

- Remove stamina costs/exhaustion limits from all Zombie variants and Scoutmaster using scoped native unlimited-stamina getters; retain full climbing strength at zero stored stamina. Leave the original player's stamina and other forms' rules unchanged, with no persistent player/pool flags.
- Delete Zombie stamina-rate copying and source-player stamina synchronization, Scoutmaster's jump stamina cost/check/debit and manual stamina resets, and the obsolete keep-status-bar HUD branches. Hide stamina HUD during transformation and restore it on exit, including external-camera use. Remove the duplicate Scoutmaster-specific PeakStats patch/retry/prune/cleanup code and use the shared optional-HUD suppression guard.
- Migrate Scoutmaster input and camera to independent creature control. Keep Character.localCharacter and the local Player.character lookup on the original scout; remove Scoutmaster's local-identity getter overrides and player-lookup rewrites. Supply explicit input before native CharacterMovement.Update, sample once per frame, and gate control until body initialization succeeds.
- Use a dedicated native CameraOverride for Scoutmaster while keeping the native observed UI target on the original player. Yield to PeakSpectatorMode/PeakCinema without clearing their overrides or spectator target; reset camera smoothing on return and restore owned camera state on exit. Restore the observed target and clear ownership even if override teardown fails. Preserve pause-menu and push-to-talk/mute input on the original player.
- Preserve Scoutmaster's existing controls, original-room source hiding/serialization order, first-spawn alignment and physics warmup, and independent failure cleanup. Keep climbing/grabbing/network guards during the migration, reject overlapping direct form entry, and retain vertical viewing angle when throwing rather than flattening throws to the ground plane.
- Animate Condor flight from smoothed movement velocity: flap while slow/rising, glide at speed, and dive during fast descent. Separate enter/exit thresholds prevent flicker. Modded peers receive optional velocity data, with position-based estimation for older senders.
- Roll back failed transformations in independent steps so a failed model, network or presentation cleanup does not skip the remaining physics, renderer, camera and HUD restoration. Cancel pending Scoutmaster entry on rollback and catch nested setup iterator errors. Retain partially spawned objects for cleanup and reject missing required visuals/components.
- Show a compact control panel while transformed, with labels for the current keyboard/mouse or controller bindings. Expose the persistent, default-enabled control-hints toggle at the top of the menu; hide the panel while menus or external cameras are active.
- Check restore-space clearance before re-enabling player physics. Preserve clear requested positions, otherwise recheck the most recent clear point and transform entry, then search a bounded neighborhood around both. Ignore the form's own colliders, clear history between sessions/scenes, and retain existing restore-height behavior without ground snapping.
- Fixed a statue control race with the original player's overlapping ragdoll: isolate the rider at the rigidbody level for the whole transformation, including when a vanilla warp re-enables its colliders, and restore each body's collision setting on exit. Synchronize the spawn RPC pose before enabling interpolation and drive only active statue bodies, excluding break fragments. Gate movement before reading raw input while the transform menu is open.
- Replaced Raven form with the volcano Condor model and renamed it to Condor (秃鹫).
- Disabled Condor right-click attacks and controller secondary-use attacks: remove the local grab/attach path, grab key/range settings and grab instructions. Release stale grab state through the original detach RPC; preserve legacy remote carry compatibility.
- Replicate Condor visuals between modded clients without spawning the bird's AI or duplicating its scene PhotonView.
- Fixed Condor being unavailable in Airport after a fresh launch: read the model, native textures and original flight animation curves from installed game files, with only a small hierarchy/read-layout index in the DLL and no extra runtime dependency or prior map visit required. This replaces the embedded model payload. Checked asset ranges reject missing or changed game data; a loaded scene Condor remains the preferred source.
- Fixed incomplete Condor rendering in the game-file loader: preserve original shader keywords, face-card render queue, instancing and texture filtering/wrapping. Restore both original wing-tip trails, their width/color/fade settings and animated lifetime (1 s gliding, 0.2 s flapping). Use the original shaders rather than substituting a generic lit material.
- Upgrade cached Airport Condor visuals to the original scene model when a volcano Condor becomes available, including existing local/remote visuals. Clear trail history on spawn and remote position jumps to avoid long streaks across the map.
- Fixed Condor's vanilla position sync in rooms with unmodded peers: always broadcast the authoritative real flight position/velocity with a stable hip offset, instead of switching the body 30 m underground between grabs. Body hiding is handled by modded renderers only; remove the obsolete grab visibility delay and clear orphaned grab state through the original detach RPC. Vanilla peers see a flying player body, not the Condor, and cannot reproduce the custom talon hand anchor.
- Added the Condor card warning to the transform menu: unmodded players see the normal player body, while the Condor model and wing trails require the mod.
- Fixed camera shaking during ascent/descent in Condor and big Ghost forms: interpolate one authoritative flight position for both the model and camera, smooth orbit offsets instead of lagging behind the vertical movement, and synchronize velocity on physics steps. Camera/FOV state is updated once per render frame and resets when returning from an external camera.

## 0.9.11

### Changes

- Rebuilt against PEAK 3.0.1 (Unity 6.3) game assemblies, so the mod keeps working after the game update.
- Game assembly references now resolve straight from the installed game directory instead of the stale local snapshots.

### Fixes

- Statue form: the driven statue rigidbodies kept interpolation off, so the render frame only saw 50 Hz step motion and the statue felt stuttery. Interpolation is now enabled while the form is active, and the collider bounds used for ground checks are cached instead of allocating every physics step.
- Beetle / scorpion movement: replaced the per-step `MovePosition` teleport with horizontal velocity driving, so the physics solver motion is interpolated and no longer looks choppy.
- Critter form: the player root was only following the critter inside `FixedUpdate` (50 Hz). It now follows once per render frame in `LateUpdate` by accumulating the target delta.
- Scoutmaster form: control used to run in both `Update` and `LateUpdate`, deduplicated with `Time.frameCount` guards. `Update` always won, so the `LateUpdate` phase was dead and `StabilizeControlledHead` ran twice per frame (double head/view smoothing). Control now runs exactly once, from `LateUpdate`.

## 0.9.10  Reporter:JoeDummy001

### Changes

- Add transformation protection feature in the mod UI menu.
- No longer recalculate height from ground raycast.

### Fixed

- Fixed Scout > Zombie > Scoutmaster > Any Form = BUG , this later results in issues such as invisibility, missing collision, falling through the ground, and movement lock.

## 0.9.9  Reporter:JoeDummy001

### Changes

- Add compatibility between this mod and both PeakSpectatorMode.dll & com.github.megalon.peakcinema.dll.
- Disable canEmote during transformation and force close / exit the emote wheel.
- Increase the fall resistance of the petrified statue.

### Fixed

- Fixed penetration and thaw‑launch occurring the moment rigidbodies restore.
- Optimize stuttering caused by scene transitions.
- Fixed an issue where the player ragdoll’s collision could remain disabled.
- Fixed an issue where the source player's render state was not restored when the stone statue shattered.

## 0.9.8

### Changes

- The mod's UI panel is now disabled while the player is in game menu.
- The selected form state is now cleared when the player returns to the menu.
- Players can no longer carry others on their back while transformed.
- Disabled the Cactus Ball form.

### Fixed

- Resolved console spam caused by call errors.
- Fix frog form dragging other players to extreme heights


## 0.9.7

### Changes

- When the Statue form is destroyed, the player is restored at the transformation position.

## 0.9.6

### Changes

- Coconut or Bomb is destroyed, the player is restored at the transformation position.
- Optimize performance and reduce stuttering.

## 0.9.5

### Changes

- Added controller support.

## 0.9.4

### Changes

- Rework the UI panel.

## V 0.9.2

### Changes

- Added two new playable forms: **Mushroom-Man Zombie** and **Cactus Ball**.
