## PEAK Transform

- Special thanks to JoeDummy001 for doing a lot of testing and uncovering numerous bugs in this mod.
- To prevent the original transformation mod from ruining the gameplay experience for other players in PEAK, a host‑authoritative integrated mod **PEAK Transform** has been created.
- The original standalone mods are discontinued and will no longer be maintained. All new features, bug fixes and version updates will be released exclusively within this unified mod.

## Mod Compatibility

- peakcinema
- PeakSpectatorMode

- PeakStatsEx
- peakstamina
- PeakStats
- PlayersInfo

## About This Mod

- By default, hold the F key for 0.4 seconds to open the menu. Select a form inside the menu. You can double‑click to transform immediately, or press F once to transform.
After selecting a form, you can transform directly without opening the menu: press F once to transform, press F again to revert.
All key‑related behaviors can be modified in the configuration file.
- The mod currently includes 15 playable forms: Normal Zombie, Player Zombie, Mushroom-Man Zombie, Scoutmaster, Ghost, Tumbleweed, Tornado, Statue, Frog, Beetle, Scorpion, Coconut, Bomb, Condor, and Raven. Each form is designed with its own movement, camera behavior, interaction rules and safety handling so it can feel distinct while still working cleanly inside multiplayer sessions.
- Scoutmaster now uses independent creature control: the original scout retains the game's local-player identity and player lookup, while a dedicated controller supplies movement/look input before native movement updates and owns a native camera override. Existing movement, sprint, jump, primary-use climbing, hold-secondary grab/release-to-throw, G manual fall, and the unified F menu/restore bindings are retained, including the game's controller bindings. Throws follow the full viewing direction, including pitch, with the configured upward bias added afterward.
- Scoutmaster follows the SEE approach of moving the hidden player's ragdoll without repeatedly warping its root. Body moves capture every world pose before changing the hierarchy, and exit restores the player's saved entry geometry with the exit viewing yaw. Queued body forces and animation/history caches are reset before physics resumes, and the original interpolation state is restored. These checks also cover finite but badly stretched body poses that a NaN-only camera repair cannot detect.
- Returning from Scoutmaster preserves the player's original first-person material hiding for face, hats, body and costumes, then refreshes the native body-hiding presentation. Source restoration no longer forces those meshes fully visible inside the camera; it preserves shader opacity and renderer flags, and yields the native refresh while an external free camera is active. Scoutmaster does not alter the camera's render-layer mask.
- Scoutmaster retains the existing original-body hiding flow for unmodded rooms: publish the underground stash before freezing source synchronization. First-spawn alignment, physics synchronization and warmup remain in place. Returning to the player preserves viewing direction and hands the camera back without repeated smoothing resets; the retiring creature's physics is disabled before the source body resumes. Native spectator UI stays on the original player. Automatic collision/trigger knockdowns are suppressed while the explicit manual fall remains available, with native RPC compatibility for unmodded peers. PeakSpectatorMode/PeakCinema pause creature input and receive camera control while active; returning resets camera smoothing. Pause-menu and push-to-talk/mute input stay on the original player, and direct form entry rejects overlapping Scoutmaster control. Prefab, network, climbing and grabbing compatibility guards remain as part of this gradual migration.
- All three Zombie variants and Scoutmaster have no stamina costs or exhaustion limits while controlled, including sprinting, jumping and climbing. Their movement uses the game's unlimited-stamina behavior, with full climbing strength even at zero stored stamina. Zombie no longer copies stamina costs or shares stamina with the original player, and Scoutmaster's separate jump stamina check/debit has been removed. All forms hide the stamina HUD; the obsolete keep-stamina-bar branches and duplicate Scoutmaster bar integration are removed. Shared optional-HUD guards suppress form bars, and normal player HUD visibility returns on exit.
- Condor (秃鹫), the volcano bird, is available directly in Airport, including after a fresh game launch. Its mesh, textures and original flight animations are read from the installed game files using a small resource index; no model files or extra runtime dependencies are bundled. The loader preserves the original material settings, both animated wing-tip trails, and the native gliding/flapping audio loops with animated volume switching and game SFX mixer routing. Audio is read from installed game resources and decoded by Unity through an in-memory bundle; only the small bundle layout is indexed in the DLL. When a scene Condor becomes available, cached and active visuals switch to the original scene model. Hold jump to rise, crouch to descend, and sprint to fly faster. Right-click attacks and the controller secondary-use attack are disabled; this form cannot grab players. Other players need this mod to see the Condor visual and wing trails. Unmodded peers receive the real flight position and see the normal player body. If a game update changes the indexed asset data, the resource index needs updating for fresh-launch Airport loading.
- Raven (渡鸦) is a separate form alongside Condor, using the native `RavenVoiceThief` Resources mesh, materials and animations without embedding a model or spawning its AI/network scripts. On the ground it stays perched and plays the original Idle animation; the native asset has no walking clip. Hold jump (Space by default) to take off/rise, use movement controls while flying, crouch (Ctrl) to descend/land, and sprint (Shift) to boost, with controller bindings supported. Landing stops horizontal movement and resumes Idle; hovering in the air retains flight animation. Sustained flight loops the matching wing-motion section of the original takeoff animation while fixing its departure translation and scale. The original animation-driven `SFX_PlayOneShot` components are retained for soft/loud idle calls and takeoff sounds, using the game's sound variations, spatial playback and SFX mixer. Takeoff sound stays enabled across wing cycles, avoiding repeated takeoff cries during sustained flight. Audio remains in game resources. Its settings are independent under `Crow`, `Crow Controls` and `Crow Camera`; the legacy `Raven` sections still configure Condor. Updated modded observers receive position, velocity and perched/flight state. Unmodded or older-mod observers see the ordinary player body at the real flight position. Secondary-use attacks are disabled, as with Condor.
- Raven also preserves the original feather particle effect: its native material, texture and particle settings load from the game's inactive `Feathers` leaf, which the takeoff animation enables once. The copied emitter uses local scaling at 40% of its original scale, reducing feather size and emission spread without additionally multiplying by the small bird model's parent scale. Ground entry does not emit feathers. Each visual receives the native Idle random-phase parameter. The inspected Raven prefab has no wing-tip trails. The voice-driven mouth layer and voice-stealing/voice-redirection gameplay are not connected to this player-controlled form.
- PEAK 3.1 compatibility: the Condor index has been regenerated for the installed resources, including native flight audio. Changed resources are still verified and rejected, with throttled retries and one diagnostic instead of repeated failure logs. Shared optional patches resolve both the original signatures and the new final `PhotonMessageInfo` RPC argument. Scoutmaster sends its optional warp fallback only when the sender is allowed by the game's new MasterClient restriction; the source-pose serialization hide/restore flow is retained.
- Condor flaps at low speed and while rising, glides at high speed, and changes to a diving presentation during fast descent. Smoothed velocity and separate transition thresholds keep the animation stable near the speed boundaries. Updated peers share velocity for matching animations; older senders can still be displayed using estimated velocity.
- Tutorial-style control hints show only the controls, without a form-name heading, at the bottom right while transformed, with a transparent background, darker bold key labels and aligned action descriptions. They follow the game's active keyboard/mouse or controller bindings. The **提示：开/关** (**Hints: On/Off**) button immediately after the scale selector in the top menu header is available on both pages and defaults to enabled; the saved `Menu.ShowControlHints` configuration is preserved. Hints hide when a menu or external free camera is active.
- Failed transformations use independent cleanup steps for the spawned form, player control, physics, appearance, camera and HUD. Pending Scoutmaster entry is cancelled during rollback so a late prefab load cannot restart the failed transformation.
- Before restoring the player, the mod checks a torso-sized capsule for occupied space. A clear target stays at the existing restore position and height. An obstructed or invalid target falls back to a rechecked recent clear position, then the entry position, then nearby clear points within a bounded search. This does not snap airborne exits to the ground or verify terrain support; if every candidate is occupied, the mod logs the unresolved clearance instead of claiming a safe location.
- Multiplayer stability is the main design goal. Room-level transformation rules can be managed through the in-game menu, including whether transformations are allowed everywhere, limited to the lobby, or disabled for the room. Individual forms can also be enabled or disabled to help hosts control the experience for everyone in the lobby.
- PEAK Transform also includes safeguards for recovery, item-like forms, remote synchronization, camera transitions, stamina behavior and special-case interactions. The goal is to make transformations fun to use without breaking the normal PEAK flow or creating avoidable problems for other players.
- Zombie forms supply the original player's voice/inventory references during native GUI updates, with scoped identity restoration on normal or exceptional exit. Camera handoff validates look, ragdoll poses, smoothing and native camera caches. Background camera recovery yields to active forms and external free cameras.
- Open the transform menu in game, choose a form, and switch between different play styles without juggling multiple standalone mods.

## Menu Features

- A dark outdoor-equipment palette uses warm orange for the selected form and primary action, with compact cards showing form names, availability and an On/Off label with a sliding thumb on a rectangular track at the upper right. Relevant forms show a short caution below their names. Full ability descriptions and cautions stay in the selected-form panel.
- Transformed-form operation hints use a larger tutorial-sized font. They measure and wrap long keyboard/controller bindings, then reduce only on very short screens so the full right-bottom hint block remains visible.
- Operation hints show only form controls; the restore/menu shortcut row is omitted so it cannot widen and shift the block. Gold key labels and near-white action text both have a dark outline for bright and dark scenery, while the background stays transparent.
- Click a card, then use Transform/Switch form. The action panel displays full controls, compatibility notes and the reason when a form is unavailable; long text scrolls independently. Double-click and the configured transformation shortcut remain available. Restore player stays at the bottom of both pages.
- Room rules, panel scale and the default-enabled `Hints: On/Off` switch sit at the top. Room notices are centered between the toolbar controls, with an extra toolbar row on narrow windows. Each form card has its own switch with a green enabled state and gray disabled state; guests without permission see a dimmed read-only state. Selection is indicated by the card highlight without repeated selection labels.
- Narrow windows place details below the form collection. The settings form list and parameter area scroll independently, and the panel fits the current screen without overwriting the preferred scale. Host-controlled settings appear read-only to guests.

The images below show the previous menu design.

![1.png](https://tg-image-4ja.pages.dev/file/AgACAgUAAyEGAAMBCNy-6QADTWqKdqEaIg27ZdPzm7bEqZ1gWHHIAAJHFWsbEiZZVCpuV8vv6QdXAQADAgADdwADPQQ.png)
![2.png](https://tg-image-4ja.pages.dev/file/AgACAgUAAyEGAAMBCNy-6QADTGqKdqEtw5xv-Ey22Iq71aa-oNnWAAJGFWsbEiZZVIR6xAW5bIyZAQADAgADdwADPQQ.png)
![3.png](https://tg-image-4ja.pages.dev/file/AgACAgUAAyEGAAMBCNy-6QADT2qKd7FbxlOT_1ItgdpZXk13q1uCAAJJFWsbEiZZVGYpC5UvIodMAQADAgADdwADPQQ.png)

## In‑game transformation screenshot

![17.png](https://tg-image-4ja.pages.dev/file/AgACAgUAAyEGAAMBCNy-6QADR2qJ2T0VGC2tSxYozbq0iuu14hoMAAIpFmsbEiZRVGxkE6CNctoNAQADAgADeQADPQQ.png)
![11.png](https://tg-image-4ja.pages.dev/file/AgACAgUAAyEGAAMBCNy-6QADM2qJgPJ8tfQSoN8-agc_7PAuaFj4AAJYHGsbPOBJVL3OBOcu7BciAQADAgADeQADPQQ.png)
![12.png](https://tg-image-4ja.pages.dev/file/AgACAgUAAyEGAAMBCNy-6QADNWqJgPJxYK6OoOwOWYy5kRaG7Ny1AAJaHGsbPOBJVH8fyQuRE4XYAQADAgADeQADPQQ.png)
![1.png](https://tg-image-4ja.pages.dev/file/AgACAgUAAyEGAAMBCNy-6QADLGqJgOSs-qY7ZlkwAAEJ5IgpfzNQFwACURxrGzzgSVQePB76w9VNhwEAAwIAA3kAAz0E.png)
![2.png](https://tg-image-4ja.pages.dev/file/AgACAgUAAyEGAAMBCNy-6QADK2qJgOQPc3ynkOtJbp_oLe_qYgo3AAJQHGsbPOBJVO43kBCe6m25AQADAgADeQADPQQ.png)
![3.png](https://tg-image-4ja.pages.dev/file/AgACAgUAAyEGAAMBCNy-6QADKmqJgOTUuQI4iHAAAb_YbAhAGs-WngACTxxrGzzgSVSlEGQtgRtjaQEAAwIAA3kAAz0E.png)
![4.png](https://tg-image-4ja.pages.dev/file/AgACAgUAAyEGAAMBCNy-6QADLWqJgOdMLco4PSCeUyMUKe6q3NpvAAJSHGsbPOBJVEveo1y9Gy7GAQADAgADeQADPQQ.png)
![5.png](https://tg-image-4ja.pages.dev/file/AgACAgUAAyEGAAMBCNy-6QADLmqJgOir_WBLk9S0FrsQhYxIiHPdAAJTHGsbPOBJVJyVVw34_bBRAQADAgADeQADPQQ.png)
![6.png](https://tg-image-4ja.pages.dev/file/AgACAgUAAyEGAAMBCNy-6QADL2qJgOyiyZY-p0YiDxc5rZV2Zq77AAJUHGsbPOBJVCwCj-ZfKx1nAQADAgADeQADPQQ.png)
![7.png](https://tg-image-4ja.pages.dev/file/AgACAgUAAyEGAAMBCNy-6QADMGqJgOyO4ydtBk_p1h2QYbWwlT7sAAJVHGsbPOBJVNrZruHM9-x3AQADAgADeQADPQQ.png)
![8.png](https://tg-image-4ja.pages.dev/file/AgACAgUAAyEGAAMBCNy-6QADMWqJgOww_TUuDayxtyge7TXtHNBZAAJWHGsbPOBJVPwBVQ50ZTqwAQADAgADeQADPQQ.png)
![9.png](https://tg-image-4ja.pages.dev/file/AgACAgUAAyEGAAMBCNy-6QADMmqJgO_8I8j0Z_i6UBMY6GL_9qK5AAJXHGsbPOBJVAo-1Mc9lGYWAQADAgADeQADPQQ.png)
![10.png](https://tg-image-4ja.pages.dev/file/AgACAgUAAyEGAAMBCNy-6QADNGqJgPJrdO5M70-0S8dAyKfArtZxAAJZHGsbPOBJVAFQOZqRZp3pAQADAgADeQADPQQ.png)
![18.png](https://tg-image-4ja.pages.dev/file/AgACAgUAAyEGAAMBCNy-6QADSGqJ2T03tAJrrmQ7dehg_i2oSyhVAAIqFmsbEiZRVP3ZiMAtu-kzAQADAgADeQADPQQ.png)

## Note

The original mod has not been deleted, it has been set to unlisted status. You may still access and test it via the link below:

- I am tumbleweed:
https://thunderstore.io/c/peak/p/Thanks/I_am_tumbleweed/

- I am Zombie:
https://thunderstore.io/c/peak/p/Thanks/I_am_Zombie/

- I am BigGhost:
https://thunderstore.io/c/peak/p/Thanks/I_am_a_BigGhost/

- I am Scoutmaster:
https://thunderstore.io/c/peak/p/Thanks/IamScoutmaster/

- I am Tornado:
https://thunderstore.io/c/peak/p/Thanks/I_am_a_Tornado/
