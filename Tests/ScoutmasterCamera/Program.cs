using System;
using System.Reflection;
using ImScoutmaster;
using UnityEngine;

internal static class Program
{
    private static int _checks;
    private static void Check(bool ok, string message) { _checks++; if (!ok) throw new Exception(message); }
    private static void Main()
    {
        var owner = new object();
        var playerData = new CharacterData(owner, true);
        var isolatedData = new CharacterData();
        var botData = new CharacterData(owner);
        var unrelatedData = new CharacterData(new object());
        Check(ScoutmasterDataLifecycle.ShouldRunDestroy(playerData), "Real player can unregister its own data");
        Check(!ScoutmasterDataLifecycle.ShouldRunDestroy(isolatedData), "Skipped isolated Awake does not run native player teardown");
        Check(!ScoutmasterDataLifecycle.ShouldRunDestroy(botData), "A bot sharing its owner's identity cannot unregister real player data");
        Check(!ScoutmasterDataLifecycle.ShouldRunDestroy(unrelatedData), "Unregistered owners require no teardown");
        var replacementData = new CharacterData(owner, true);
        Check(!ScoutmasterDataLifecycle.ShouldRunDestroy(playerData) && ScoutmasterDataLifecycle.ShouldRunDestroy(replacementData),
            "Late destruction of an old player cannot unregister a replacement");
        var missing = new System.Collections.Generic.List<string>();
        ScoutmasterCameraRig.AppendMissingReflectionMembers(missing);
        Check(missing.Count == 0, "Camera reflection diagnostics resolve both accessors and ownership field");
        var source = new Character();
        var spectatorTarget = new Character();
        MainCamera.instance = new GameObject().AddComponent<MainCamera>();
        var camera = MainCamera.instance.gameObject.AddComponent<Camera>();
        camera.fieldOfView = 81f;
        MainCameraMovement.specCharacter = source;
        var previous = new GameObject().AddComponent<CameraOverride>();
        MainCamera.instance.SetCameraOverride(previous);
        var rig = new GameObject().AddComponent<ScoutmasterCameraRig>();
        typeof(ScoutmasterCameraRig).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(rig, null);
        var pose = new Vector3(30, 40, 50);
        rig.Publish(source, pose, new Quaternion { w = 1 });
        var owned = MainCamera.instance.camOverride;
        Check(ScoutmasterCameraRig.HasCameraOverride, "Restore assist can defer while a native/third-party override is active");
        Check(owned != null && owned != previous, "Claim a dedicated override");
        Check(owned.fov == 81f && owned.transform.position.y == 40, "Publish original FOV and desired pose");
        Check(MainCameraMovement.specCharacter == source, "Creature camera pose keeps native observation on the real player");
        rig.Release();
        Check(MainCamera.instance.camOverride == previous && MainCameraMovement.specCharacter == source, "Yield restores previous camera and observed target");
        rig.Release();
        Check(MainCamera.instance.camOverride == previous, "Repeated release is idempotent");
        camera.fieldOfView = 20f;
        rig.Publish(source, pose, new Quaternion { w = 1 });
        Check(MainCamera.instance.camOverride == owned && owned.fov == 81f, "Resume reuses override and session FOV after external zoom");
        var external = new GameObject().AddComponent<CameraOverride>();
        MainCamera.instance.SetCameraOverride(external);
        MainCameraMovement.specCharacter = spectatorTarget;
        rig.Release();
        Check(MainCamera.instance.camOverride == external && MainCameraMovement.specCharacter == spectatorTarget, "Never clear another camera's override or spectator target");
        // The external camera normally removes its override on deactivation.
        MainCamera.instance.SetCameraOverride(null);
        Check(!ScoutmasterCameraRig.HasCameraOverride, "Native camera becomes available when the external override ends");
        rig.Publish(source, pose, new Quaternion { w = 1 });
        rig.Release();
        Check(MainCamera.instance.camOverride == null && MainCameraMovement.specCharacter == spectatorTarget, "Exit restores the camera state captured on resumption");
        MainCamera.instance = null;
        rig.Publish(source, pose, new Quaternion { w = 1 });
        rig.Release();
        Check(MainCameraMovement.specCharacter == spectatorTarget, "Scene unload without a camera is harmless");
        MainCamera.instance = new GameObject().AddComponent<MainCamera>();
        rig.Publish(source, pose, new Quaternion { w = 1 });
        Check(MainCamera.instance.camOverride == owned, "A new scene's camera can be acquired");
        rig.Release();
        MainCameraMovement.specCharacter = source;
        rig.Publish(source, pose, new Quaternion { w = 1 });
        MainCamera.instance.FailNextSet = true;
        bool failed = false;
        try { rig.Release(); } catch (InvalidOperationException) { failed = true; }
        Check(failed && MainCameraMovement.specCharacter == source, "Override teardown failure still restores observed target");
        var foreign = new GameObject().AddComponent<CameraOverride>();
        MainCamera.instance.SetCameraOverride(foreign);
        rig.Release();
        Check(MainCamera.instance.camOverride == foreign, "Failed release clears ownership and cannot steal a later camera");
        rig.Publish(source, pose, new Quaternion { w = 1 });
        rig.Release();
        Check(MainCamera.instance.camOverride == foreign && MainCameraMovement.specCharacter == source, "A session can reacquire after a failed release");
        MainCamera.instance = new GameObject().AddComponent<MainCamera>();
        var corruptCamera = MainCamera.instance.gameObject.AddComponent<Camera>();
        corruptCamera.fieldOfView = float.NaN;
        var guardedRig = new GameObject().AddComponent<ScoutmasterCameraRig>();
        typeof(ScoutmasterCameraRig).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(guardedRig, null);
        guardedRig.Publish(source, pose, new Quaternion { w = 1 });
        Check(MainCamera.instance.camOverride.fov == 70f, "A corrupt captured FOV cannot poison the native override");
        guardedRig.Release();
        Console.WriteLine("PASS: " + _checks + " production camera ownership checks: claim, pose/FOV, yield/resume, foreign override protection, idempotent exit and scene replacement.");
    }
}
