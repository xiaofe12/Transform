using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using ImScoutmaster;
using Mono.Cecil;
using Mono.Cecil.Cil;
using UnityEngine;

internal static class Program
{
    private static int _checks;
    private static void Check(bool ok, string message) { _checks++; if (!ok) throw new Exception(message); }
    private static IEnumerable<TypeDefinition> All(TypeDefinition t) => new[] { t }.Concat(t.NestedTypes.SelectMany(All));
    private static IEnumerable<Instruction> Instructions(MethodDefinition m) => m.HasBody ? m.Body.Instructions : Enumerable.Empty<Instruction>();
    private static bool Calls(MethodDefinition m, string owner, string name) => Instructions(m).Any(i => i.Operand is MethodReference r && r.DeclaringType.FullName == owner && r.Name == name);
    private static void ControlFrames()
    {
        var state = new ScoutmasterControlState();
        for (int session = 0; session < 100; session++)
        {
            state.Begin();
            for (int frame = 0; frame < 20; frame++) Check(!state.TrySample(frame, false), "Spawn warmup must not accept input");
            state.Activate();
            for (int frame = 20; frame < 100; frame++)
            {
                Check(!state.TrySample(frame, true), "Menu, incapacitation and external camera block input");
                Check(state.TrySample(frame, false), "Resume works without another character swap");
                Check(!state.TrySample(frame, false), "One movement/look sample per frame");
            }
            state.End();
            Check(!state.Ready && !state.TrySample(101, false), "Exit clears control ownership");
        }
        for (int yaw = -180; yaw <= 180; yaw += 10)
        for (int pitch = -85; pitch <= 85; pitch += 5)
        {
            float y = yaw * MathF.PI / 180f, p = pitch * MathF.PI / 180f;
            Vector3 sight = new Vector3(MathF.Sin(y) * MathF.Cos(p), -MathF.Sin(p), MathF.Cos(y) * MathF.Cos(p));
            Vector2 restoredLook = ScoutmasterControlState.LookValues(sight, Vector3.back);
            float restoredYaw = restoredLook.x * MathF.PI / 180f, restoredPitch = restoredLook.y * MathF.PI / 180f;
            Vector3 recalculated = new Vector3(MathF.Sin(restoredYaw) * MathF.Cos(restoredPitch),
                MathF.Sin(restoredPitch), MathF.Cos(restoredYaw) * MathF.Cos(restoredPitch));
            Check(Vector3.Dot(recalculated, sight) > 0.99999f,
                "Restored look matches native angle recalculation even when the physical body faces elsewhere");
            Vector3 force = ScoutmasterControlState.ThrowDirection(sight, Vector3.right, 0);
            Check(Vector3.Dot(force.normalized, sight) > 0.99999f, "Throw follows full sight yaw and pitch");
            Vector3 biased = ScoutmasterControlState.ThrowDirection(sight, Vector3.right, 0.1f);
            Check((biased - force - Vector3.up * 0.1f).sqrMagnitude < 0.000001f, "Upward bias is additive");
        }
        Check(ScoutmasterControlState.ThrowDirection(Vector3.zero, Vector3.right, 0) == Vector3.right, "Degenerate sight uses body facing");
        Check(ScoutmasterControlState.ThrowDirection(new Vector3(float.NaN, 0, 0), Vector3.zero, float.NaN) == Vector3.forward, "Invalid sight and config are finite");
        Vector2 fallbackLook = ScoutmasterControlState.LookValues(new Vector3(float.NaN, 0, 0), Vector3.right);
        Check(MathF.Abs(fallbackLook.x - 90) < 0.001f && MathF.Abs(fallbackLook.y) < 0.001f, "Invalid gaze restores finite body-facing angles");
        Check(ScoutmasterControlState.LookValues(Vector3.up, Vector3.forward).y == 85f, "Restore respects native pitch limits");
    }

    private static void Falls()
    {
        var state = new ScoutmasterFallState();
        for (int session = 1; session <= 100; session++)
        {
            float now = session * 10f;
            Check(!state.ManualActive(now), "New session has no inherited manual fall");
            foreach (float duration in new[] { 0.1f, 0.5f, 1f, 1.25f, 2f, 8f, float.NaN })
            foreach (float shake in new[] { 0f, 1f, 15f, float.NaN })
            {
                float tag = shake;
                Check(!state.AcceptManualRpc(duration, ref tag, now), "Ordinary collision/trigger RPC cannot authorize a manual fall");
            }
            float manualTag = ScoutmasterFallState.ManualShakeTag;
            Check(state.AcceptManualRpc(ScoutmasterFallState.ManualSeconds, ref manualTag, now), "Explicit manual fall is accepted");
            Check(manualTag == 0f && state.ManualActive(now), "Native fall receives zero shake and timed permission");
            Check(state.ManualActive(now + 1.24f) && !state.ManualActive(now + 1.25f), "Manual fall expires instead of trapping controls");
            float collisionShake = 0f;
            Check(!state.AcceptManualRpc(100f, ref collisionShake, now + 1), "Collision during manual fall cannot extend its window");
            Check(!state.ManualActive(now + 1.3f), "Repeated environmental hits cannot keep a creature down");
        }
    }

    private static void Architecture(string dll, string gamePath)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(dll);
        var types = assembly.MainModule.Types.SelectMany(All).Where(t => t.Namespace == "ImScoutmaster" || t.FullName.StartsWith("ImScoutmaster.")).ToArray();
        foreach (var type in types)
        foreach (var method in type.Methods)
        foreach (var i in Instructions(method))
            Check(!(i.OpCode == OpCodes.Stsfld && i.Operand is FieldReference field && field.DeclaringType.FullName == "Character" && field.Name == "localCharacter"),
                "Scoutmaster must never replace Character.localCharacter: " + method.FullName);
        var patches = types.Single(t => t.Name == "ScoutmasterHarmonyPatches");
        foreach (var method in patches.Methods)
        foreach (var attribute in method.CustomAttributes.Where(a => a.AttributeType.Name == "HarmonyPatch"))
        {
            if (attribute.ConstructorArguments.Count < 2) continue;
            string owner = attribute.ConstructorArguments[0].Value?.ToString();
            string name = attribute.ConstructorArguments[1].Value?.ToString();
            Check(!(owner == "Character" && new[] { "get_IsLocal", "get_IsPlayerControlled", "get_IsRegisteredToPlayer" }.Contains(name)), "Do not impersonate a player through creature flags");
            Check(!(owner == "Player" && name == "get_character"), "Player.character remains native");
        }
        var plugin = types.Single(t => t.FullName == "ImScoutmaster.Plugin");
        Check(!plugin.Fields.Any(f => f.Name == "PlayerHandlerCharacterLookupField"), "No reflective writes to player lookup");
        var movementPrefix = patches.Methods.Single(m => m.Name == "CharacterMovementUpdatePrefix");
        Check(Calls(movementPrefix, "ImScoutmaster.Plugin", "PrepareCharacterMovement"), "Input is prepared before native movement Update");
        var controller = types.Single(t => t.Name == "PlayerScoutmasterController");
        var prepare = controller.Methods.Single(m => m.Name == "PrepareMovementFrame");
        var calls = Instructions(prepare).Where(i => i.Operand is MethodReference).Select(i => ((MethodReference)i.Operand).Name).ToList();
        Check(calls.IndexOf("ResetOriginalScoutmasterInputSafely") < calls.IndexOf("DriveOriginalScoutmasterControl"), "Reset precedes movement/jump sample");
        Check(calls.Contains("DriveScoutmasterLookInput"), "Independent look uses native sensitivity/inversion path");
        Check(!Calls(controller.Methods.Single(m => m.Name == "ControlTick"), controller.FullName, "ResetOriginalScoutmasterInputSafely"), "LateUpdate must not erase sampled input");
        Check(Calls(controller.Methods.Single(m => m.Name == "ThrowHeldPlayer"), "ImScoutmaster.ScoutmasterControlState", "ThrowDirection"), "Production throw uses tested sight math");
        Check(Calls(types.Single(t => t.Name == "ScoutmasterCameraRig").Methods.Single(m => m.Name == "Publish"), "MainCamera", "SetCameraOverride"), "Use native camera override API");
        var session = types.Single(t => t.Name == "ActiveScoutmasterSession");
        var iteratorRef = (TypeReference)session.Methods.Single(m => m.Name == "Enter").CustomAttributes.Single(a => a.AttributeType.Name == "IteratorStateMachineAttribute").ConstructorArguments[0].Value;
        var iterator = types.Single(t => t.FullName == iteratorRef.FullName).Methods.Single(m => m.Name == "MoveNext");
        var entryCalls = Instructions(iterator).Where(i => i.Operand is MethodReference).Select(i => ((MethodReference)i.Operand).Name).ToList();
        Check(entryCalls.IndexOf("AlignScoutmasterBodyToSource") >= 0 && entryCalls.IndexOf("ActivateDynamicRagdollControl") > entryCalls.IndexOf("AlignScoutmasterBodyToSource"), "Align and warm up the new body before accepting input");
        Check(Instructions(iterator).Any(i => i.OpCode == OpCodes.Ldc_R4 && i.Operand is float wait && Math.Abs(wait - 0.25f) < 0.001f), "First-spawn warmup wait is retained");
        Check(entryCalls.IndexOf("HideSourceCharacter") >= 0 && entryCalls.IndexOf("FreezeSourceNetworkSync") > entryCalls.IndexOf("HideSourceCharacter"), "Broadcast stash before freezing source sync");
        Check(Instructions(iterator).Any(i => i.OpCode == OpCodes.Ldc_R4 && i.Operand is float wait && Math.Abs(wait - 0.1f) < 0.001f), "Wait for source stash serialization remains intact");
        Check(Calls(session.Methods.Single(m => m.Name == "HideSourceCharacter"), session.FullName, "SendSourceCharacterOutOfPlay"), "Retain vanilla-room hide RPC fallback");
        Check(Calls(session.Methods.Single(m => m.Name == "RestoreSourceCharacter"), "Transform.Core.CleanupSequence", "Step"), "Independent source cleanup stages are retained");
        var follow = session.Methods.Single(m => m.Name == "FollowSourceCharacterToControlledBodySafely");
        Check(Calls(follow, "ImScoutmaster.ScoutmasterBodyPose", "Capture")
            && Calls(follow, "ImScoutmaster.ScoutmasterBodyPose", "TranslateTo")
            && !Calls(follow, plugin.FullName, "SetCharacterPositionImmediate"),
            "Source follows the creature through rig-only world-pose translation, without per-frame root warps");
        Check(Calls(plugin.Methods.Single(m => m.Name == "SetCharacterPositionImmediate"), "ImScoutmaster.ScoutmasterBodyPose", "Apply"),
            "Entry/spawn warps use the tested hierarchy-safe transaction");
        Check(session.Fields.Any(f => f.Name == "_sourceEntryPose" && f.IsInitOnly), "Source keeps a separate entry geometry snapshot for restoration");
        Check(Calls(plugin.Methods.Single(m => m.Name == "RefreshControlledScoutmasterCamera"), "Transform.Core.ThirdPartyCameras", "get_ExternalCameraActive"), "Camera acquisition is gated by external free-camera state");
        var validation = assembly.MainModule.Types.Single(t => t.FullName == "Transform.Core.FormValidation");
        Check(Calls(validation.Methods.Single(m => m.Name == "ValidateTransformable"), validation.FullName, "get_HasIndependentScoutmasterControl"), "Other forms reject overlapping independent control");
        var zombie = assembly.MainModule.Types.Single(t => t.FullName == "ImZombie.ZombieController");
        Check(Calls(zombie.Methods.Single(m => m.Name == "IsOtherTransformModActive"), validation.FullName, "get_HasIndependentScoutmasterControl"), "Zombie direct entry respects independent control too");
        var sourceInput = Instructions(plugin.Methods.Single(m => m.Name == "PrepareCharacterMovement")).ToArray();
        Check(sourceInput.Any(i => i.OpCode == OpCodes.Stfld && i.Operand is FieldReference f && f.Name == "pauseWasPressed"), "Source still samples pause for GUIManager");
        Check(sourceInput.Any(i => i.OpCode == OpCodes.Stfld && i.Operand is FieldReference f && f.Name == "pushToTalkPressed")
            && sourceInput.Any(i => i.OpCode == OpCodes.Ldsfld && i.Operand is FieldReference f && f.Name == "push_to_talk"), "Source voice input follows current push-to-talk/mute action");

        using var game = AssemblyDefinition.ReadAssembly(Path.Combine(gamePath, "Assembly-CSharp.dll"));
        var movement = game.MainModule.Types.Single(t => t.Name == "CharacterMovement");
        Check(Calls(movement.Methods.Single(m => m.Name == "Update"), "CharacterMovement", "CameraLook"), "Game still calculates gaze from provided look input");
        var mainCamera = game.MainModule.Types.Single(t => t.Name == "MainCamera");
        Check(mainCamera.Fields.Any(f => f.Name == "camOverride" && f.FieldType.Name == "CameraOverride"), "Camera ownership field matches installed game");
        var movementCamera = game.MainModule.Types.Single(t => t.Name == "MainCameraMovement");
        Check(movementCamera.Fields.Any(f => f.Name == "currentFov" && f.FieldType.FullName == "System.Single"), "Native FOV smoothing cache matches the installed game");
        var bodypart = game.MainModule.Types.Single(t => t.Name == "Bodypart");
        var hideBody = game.MainModule.Types.Single(t => t.Name == "HideTheBody");
        Check(Calls(hideBody.Methods.Single(m => m.Name == "Refresh"), "HideTheBody", "Toggle")
            && Calls(hideBody.Methods.Single(m => m.Name == "SetShowing"), "UnityEngine.Material", "SetFloat"),
            "Installed native body hiding refresh reapplies shader visibility without a camera-layer change");
        foreach (var field in new[] { "prevPos", "prevRot", "targetRotation", "lastTargetRotation", "targetForward", "targetUp", "targetOffsetRelativeToHip", "forcesToAdd" })
            Check(bodypart.Fields.Any(f => f.Name == field), "Native restored-body history field exists: " + field);
        Check(movementCamera.Properties.Any(p => p.Name == "specCharacter" && p.SetMethod != null), "Observed target setter exists in installed game");
        var character = game.MainModule.Types.Single(t => t.Name == "Character");
        var rpc = character.Methods.Single(m => m.Name == "RPCA_Fall");
        Check(rpc.Parameters.Take(2).Select(p => p.Name).SequenceEqual(new[] { "seconds", "shake" }), "Fall prefix parameter names match installed RPC");
        Check(Instructions(rpc).Any(i => i.OpCode == OpCodes.Ble_Un || i.OpCode == OpCodes.Ble_Un_S),
            "Native fall gates screen shake to positive values, preserving vanilla negative-tag compatibility");
        Check(Calls(patches.Methods.Single(m => m.Name == "CharacterRpcFallPrefix"), plugin.FullName, "AcceptControlledScoutmasterManualFallRpc"), "Native RPC guard preserves explicit manual falls");
        Check(Calls(patches.Methods.Single(m => m.Name == "CharacterUpdatePrefix"), plugin.FullName, "RecoverControlledScoutmaster"), "Unexpected incapacitation is recovered before native life update");
        var data = game.MainModule.Types.Single(t => t.Name == "CharacterData");
        Check(data.Fields.Any(f => f.Name == "m_photonView") && data.Fields.Any(f => f.Name == "m_instances"), "Player data teardown fields match installed game");
        Check(Calls(patches.Methods.Single(m => m.Name == "CharacterDataOnDestroyPrefix"), "ImScoutmaster.ScoutmasterDataLifecycle", "ShouldRunDestroy"), "Isolated creature teardown checks registry ownership");
        var refreshCamera = plugin.Methods.Single(m => m.Name == "RefreshControlledScoutmasterCamera");
        Check(Instructions(refreshCamera).Any(i => i.OpCode == OpCodes.Ldsfld && i.Operand is FieldReference f
            && f.DeclaringType.Name == "Character" && f.Name == "localCharacter"),
            "Camera publication observes the real player instead of incomplete creature UI data");
        Check(Calls(plugin.Methods.Single(m => m.Name == "CopyLookStateForRestore"),
            "ImScoutmaster.ScoutmasterControlState", "LookValues"), "Restore uses tested gaze-to-angle conversion");
        Check(!Calls(plugin.Methods.Single(m => m.Name == "CopyLookStateForRestore"), "UnityEngine.Transform", "set_rotation"),
            "Copying view directions cannot rotate the already restored parent/body hierarchy again");
        var restoreRenderers = plugin.Methods.Single(m => m.Name == "RestoreSourceRenderers");
        Check(Calls(restoreRenderers, "ImScoutmaster.ScoutmasterRendererState", "Restore")
            && !Calls(restoreRenderers, plugin.FullName, "RestoreRendererVisibility")
            && !Calls(restoreRenderers, plugin.FullName, "RevealRendererMaterials"),
            "Player source restores shader snapshots rather than revealing the creature presentation");
        Check(Calls(restoreRenderers, "HideTheBody", "Refresh")
            && Calls(restoreRenderers, "Transform.Core.ThirdPartyCameras", "get_ExternalCameraActive"),
            "Native first-person body hiding refreshes on return and yields to external cameras");
        Check(!types.Where(t => t.Namespace == "ImScoutmaster").SelectMany(t => t.Methods)
            .Any(m => Calls(m, "UnityEngine.Camera", "set_cullingMask")), "Scoutmaster never changes the camera render-layer mask");
        Check(Calls(session.Methods.Single(m => m.Name == "ResolveRestoreRotation"),
            "ImScoutmaster.ScoutmasterControlState", "LookValues"), "Body restoration uses the same yaw as camera restoration");
    }

    private static void Main(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("ScoutmasterTakeover <mod DLL> <game Managed directory>");
        ControlFrames();
        Falls();
        Architecture(args[0], args[1]);
        Console.WriteLine("PASS: " + _checks + " checks: readiness/reentry, one input sample per frame, full sight throws, compiled identity/input/camera integration and installed-game API contracts.");
    }
}
