using System;
using System.Linq;
using ImRaven;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Photon.Pun;
using Transform.Core;

internal static class Program
{
    private static int _checks;
    private static void Check(bool ok, string message) { _checks++; if (!ok) throw new Exception(message); }
    private class Legacy
    {
        private void Death() { }
        private void Warp(UnityEngine.Vector3 position, bool poof) { }
    }
    private class Current
    {
        private void Death(PhotonMessageInfo info) { }
        private void Warp(UnityEngine.Vector3 position, bool poof, PhotonMessageInfo info) { }
        private void Wrong(int value) { }
        private void WrongSuffix(bool value) { }
        private void Generic<T>() { }
        private void Overloaded(PhotonMessageInfo info) { }
        private void Overloaded() { }
    }
    private class Inherited : Legacy { }

    private static void Resolver()
    {
        Type[] warp = { typeof(UnityEngine.Vector3), typeof(bool) };
        foreach (Type type in new[] { typeof(Legacy), typeof(Current), typeof(Inherited) })
        {
            int suffix = type == typeof(Current) ? 1 : 0;
            Check(PatchUtility.FindCompatibleMethod(type, "Death", Type.EmptyTypes)?.GetParameters().Length == suffix,
                "Death patch resolves old/new/inherited RPCs");
            Check(PatchUtility.FindCompatibleMethod(type, "Warp", warp)?.GetParameters().Length == warp.Length + suffix,
                "Warp patch preserves argument types and accepts only the Photon suffix");
        }
        Check(PatchUtility.FindCompatibleMethod(typeof(Current), "Overloaded", null)?.GetParameters().Length == 0, "Exact overload wins");
        foreach (string name in new[] { "Wrong", "WrongSuffix", "Generic", "Missing" })
            Check(PatchUtility.FindCompatibleMethod(typeof(Current), name, null) == null, "Do not patch incompatible overload: " + name);
        Check(PatchUtility.FindCompatibleMethod(null, "Death", null) == null, "Missing type is optional");
    }

    private static void FlightCycle()
    {
        foreach (int fps in new[] { 30, 60, 120, 144, 240 })
        foreach (bool flap in new[] { true, false })
        {
            var cycle = new CrowFlightCycle();
            float last = -1;
            int wraps = 0;
            for (int frame = 0; frame < fps * 20; frame++)
            {
                float value = cycle.Step(1f / fps, 3f, flap) * 3f;
                Check(value >= CrowFlightCycle.StartSeconds - .0001f && value < CrowFlightCycle.EndSeconds + .0001f,
                    "Sample only the moving flight segment, never the frozen tail");
                if (value < last) wraps++;
                last = value;
            }
            Check(Math.Abs(wraps - (flap ? 40 : 26)) <= 1, "Flight loop rate is stable across frame rates");
            foreach (float dt in new[] { -1f, 0f, float.NaN, float.PositiveInfinity, 10000f })
                Check(float.IsFinite(cycle.Step(dt, float.NaN, flap)), "Invalid/large deltas keep a finite animation phase");
        }
    }

    private static void InstalledContracts(string dll, string managed)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(dll);
        using var game = AssemblyDefinition.ReadAssembly(System.IO.Path.Combine(managed, "Assembly-CSharp.dll"));
        var character = game.MainModule.Types.Single(t => t.Name == "Character");
        foreach (string name in new[] { "RPCA_Die", "RPCA_SetDead", "WarpPlayerRPC" })
        {
            var method = character.Methods.Single(m => m.Name == name);
            Check(method.Parameters.Last().ParameterType.FullName == "Photon.Pun.PhotonMessageInfo", "Installed native RPC has compatible suffix: " + name);
            Check(method.Parameters.Count == (name == "WarpPlayerRPC" ? 3 : 1), "Installed RPC has expected argument count");
        }
        var formId = assembly.MainModule.Types.Single(t => t.FullName == "Transform.Core.FormId");
        var ids = formId.Fields.Where(f => f.HasConstant).ToArray();
        Check(ids.Length == 15 && ids.Select(f => f.Constant).Distinct().Count() == 15, "All 15 form IDs are distinct");
        Check((int)ids.Single(f => f.Name == "Raven").Constant == 13, "Legacy Condor room/config bit is unchanged");
        Check((int)ids.Single(f => f.Name == "Crow").Constant == 14, "New Raven appends its own bit");
        var policy = assembly.MainModule.Types.Single(t => t.FullName == "Transform.Core.RoomPolicy");
        Check((int)policy.Fields.Single(f => f.Name == "AllFormsMask").Constant == 32767, "Host all-forms mask includes Raven");
        var visual = assembly.MainModule.Types.Single(t => t.FullName == "ImRaven.CrowVisualAsset");
        var instructions = visual.Methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).ToArray();
        Check(instructions.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "RavenVoiceThief"), "Raven uses installed native Resources path");
        Check(!instructions.Any(i => i.Operand is MethodReference m && m.DeclaringType.FullName == "Photon.Pun.PhotonNetwork"),
            "Visual builder never spawns or owns a gameplay Photon prefab");
        Check(!assembly.MainModule.Resources.Any(r => r.Name.Contains("Crow") || r.Name.Contains("RavenVoiceThief")), "No Raven model payload embedded");
        var controller = assembly.MainModule.Types.Single(t => t.FullName == "ImRaven.RavenController");
        var wire = controller.Methods.Single(m => m.Name == "WriteLocalNetworkPose");
        Check(wire.Body.Instructions.Any(i => i.Operand is MethodReference m && m.DeclaringType.FullName == "ImRaven.CondorNetworkPose"),
            "Both birds keep the tested native-player flight wire pose");
        var remote = controller.Methods.Single(m => m.Name == "ApplyNetworkSyncEvent");
        Check(remote.Body.Instructions.Any(i => i.OpCode == OpCodes.Stfld && i.Operand is FieldReference f && f.Name == "Grounded"),
            "Remote Raven receives explicit ground/flight state instead of guessing from zero velocity");
        var session = assembly.MainModule.Types.Single(t => t.FullName == "ImScoutmaster.Plugin").NestedTypes.Single(t => t.Name == "ActiveScoutmasterSession");
        foreach (string name in new[] { "PushSourceStashPositionToPlayer", "SendSourceCharacterStateRpc" })
            Check(session.Methods.Single(m => m.Name == name).Body.Instructions.Any(i => i.Operand is MethodReference m
                && m.DeclaringType.FullName == "Photon.Pun.PhotonNetwork" && m.Name == "get_IsMasterClient"),
                "Scoutmaster's optional native warp obeys current host authority: " + name);
    }

    private static void Grounding()
    {
        var state = new CrowGroundState();
        var position = new UnityEngine.Vector3(5f, CrowGroundState.Clearance, -3f);
        var walk = new UnityEngine.Vector3(1f, 0f, 1f);
        state.Reset(true);
        for (int frame = 0; frame < 1000; frame++)
        {
            var delta = state.Step(position, walk, 0f, false);
            Check(delta == UnityEngine.Vector3.zero && state.Grounded, "Ground movement stays perched instead of sliding");
            var down = state.Step(position, new UnityEngine.Vector3(0f, -1f, 0f), 0f, false);
            Check(down == UnityEngine.Vector3.zero && state.Grounded, "Holding descend does not pass through support");
        }
        var rise = new UnityEngine.Vector3(0.1f, 0.2f, 0f);
        Check(state.Step(position, rise, 0f, true) == rise && !state.Grounded, "Ascent takes off and accepts flight movement");
        position += rise;
        Check(state.Step(position, UnityEngine.Vector3.zero, 0f, false) == UnityEngine.Vector3.zero && !state.Grounded,
            "Airborne hover remains flight; zero velocity does not imply Idle");
        Check(state.Step(position, walk, 0f, false) == walk && !state.Grounded, "Airborne movement remains available");
        var landing = state.Step(position, new UnityEngine.Vector3(.1f, -1f, .2f), 0f, false);
        Check(state.Grounded && landing.x == .1f && landing.z == .2f
            && Math.Abs(position.y + landing.y - CrowGroundState.Clearance) < .0001f, "Descent finishes its step onto the probed support");
        position += landing;
        Check(state.Step(position, walk, 0f, false) == UnityEngine.Vector3.zero && state.Grounded,
            "Touchdown stops all subsequent ground movement");
        Check(state.Step(position, walk, float.NaN, false) == walk && !state.Grounded, "Missing support cannot keep a false ground latch");
        state.Reset();
        Check(!state.Grounded, "Reentry resets ground state");
    }

    private static void Main(string[] args)
    {
        Resolver();
        FlightCycle();
        Grounding();
        InstalledContracts(args[0], args[1]);
        Console.WriteLine($"PASS: {_checks:N0} bird/update checks: native RPC suffix/authority, stable form IDs, grounded Idle/takeoff/landing, flight loop, resource-only model and player wire pose.");
    }
}
