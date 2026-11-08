using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Transform.Core;
using UnityEngine;

internal static class Program
{
    private static int _checks;
    private static void Check(bool ok, string reason) { _checks++; if (!ok) throw new Exception(reason); }
    private static bool Unlimited(Character c)
    {
        bool result = c.infiniteStam;
        CreatureStamina.InfiniteStaminaPostfix(c, ref result);
        return result;
    }
    private static float Total(Character c)
    {
        float result = c.stamina;
        CreatureStamina.TotalStaminaPostfix(c, ref result);
        return result;
    }

    // Interpret the installed game's early-return path, refusing any unexpected field/native call.
    // This verifies that native usage/exhaustion methods exit before touching stamina or HUD.
    private static bool NativeUnlimitedPath(MethodDefinition method, Character character, float usage = 1f)
    {
        object[] args = { character, usage, true, false };
        var stack = new Stack<object>();
        var locals = new Dictionary<int, object>();
        Instruction ip = method.Body.Instructions[0];
        bool Truth(object value) => Convert.ToDouble(value) != 0;
        for (int steps = 0; steps < 100 && ip != null; steps++)
        {
            Instruction next = ip.Next;
            switch (ip.OpCode.Code)
            {
                case Code.Nop: break;
                case Code.Ldarg_0: stack.Push(args[0]); break;
                case Code.Ldarg_1: stack.Push(args[1]); break;
                case Code.Ldc_I4_0: stack.Push(0); break;
                case Code.Ldc_I4_1: stack.Push(1); break;
                case Code.Ldc_R4: stack.Push(ip.Operand); break;
                case Code.Ldloc_0: stack.Push(locals[0]); break;
                case Code.Stloc_0: locals[0] = stack.Pop(); break;
                case Code.Br: case Code.Br_S: next = (Instruction)ip.Operand; break;
                case Code.Brtrue: case Code.Brtrue_S: if (Truth(stack.Pop())) next = (Instruction)ip.Operand; break;
                case Code.Brfalse: case Code.Brfalse_S: if (!Truth(stack.Pop())) next = (Instruction)ip.Operand; break;
                case Code.Ceq:
                {
                    double b = Convert.ToDouble(stack.Pop()), a = Convert.ToDouble(stack.Pop());
                    stack.Push(a == b ? 1 : 0); break;
                }
                case Code.Bne_Un: case Code.Bne_Un_S:
                {
                    double b = Convert.ToDouble(stack.Pop()), a = Convert.ToDouble(stack.Pop());
                    if (a != b) next = (Instruction)ip.Operand;
                    break;
                }
                case Code.Call: case Code.Callvirt:
                {
                    var call = (MethodReference)ip.Operand;
                    if (call.DeclaringType.Name != "Character" || call.Name != "get_infiniteStam")
                        throw new Exception("Unexpected call before unlimited early return: " + call.FullName);
                    stack.Push(Unlimited((Character)stack.Pop())); break;
                }
                case Code.Ret: return Truth(stack.Pop());
                default: throw new Exception("Unlimited path reaches unexpected instruction: " + ip);
            }
            ip = next;
        }
        throw new Exception("Native unlimited path did not return");
    }

    private static void Stamina(string gameDirectory)
    {
        using var game = AssemblyDefinition.ReadAssembly(Path.Combine(gameDirectory, "Assembly-CSharp.dll"));
        var native = game.MainModule.Types.Single(t => t.Name == "Character");
        var usageMethod = native.Methods.Single(m => m.Name == "UseStamina");
        var outMethods = new[] { "OutOfStamina", "OutOfRegularStamina" }.Select(n => native.Methods.Single(m => m.Name == n)).ToArray();
        var original = new Character { stamina = 0.13f };
        var zombie = new Character();
        var leader = new Character();
        for (int session = 0; session < 50; session++)
        {
            ImZombie.ZombieController.ActiveZombieCharacter = zombie;
            ImScoutmaster.Plugin.Controlled = leader;
            foreach (var creature in new[] { zombie, leader })
            foreach (float stored in new[] { 0f, 0.001f, 0.1f, 0.9f })
            {
                creature.stamina = stored;
                creature.infiniteStam = false; // Native temporary buff expired.
                Check(Unlimited(creature), "Controlled creature remains unlimited after native buff expiry");
                Check(Total(creature) == 1f, "Zero stamina cannot slow climbing");
                foreach (float usage in new[] { 0.0001f, 0.1f, 1f, 50f })
                    Check(NativeUnlimitedPath(usageMethod, creature, usage), "Native consumption exits before mutation/HUD access");
                foreach (var method in outMethods)
                    Check(!NativeUnlimitedPath(method, creature), "Native exhaustion check allows creature movement");
                Check(creature.stamina == stored && !creature.infiniteStam, "No persistent stamina/flag writes");
            }
            Check(!Unlimited(original) && Total(original) == 0.13f, "Original player and other forms keep native stamina");
            original.infiniteStam = true;
            Check(Unlimited(original), "Existing player buffs remain native");
            original.infiniteStam = false;
            ImZombie.ZombieController.ActiveZombieCharacter = null;
            ImScoutmaster.Plugin.Controlled = null;
            Check(!Unlimited(zombie) && !Unlimited(leader), "Exit/pooling immediately restores native rules");
        }
    }

    private static void Hud()
    {
        var hud = new UiElement();
        var group = new UiElement();
        var neighbor = new GameObject();
        var initiallyHidden = new GameObject(); initiallyHidden.SetActive(false);
        hud.transform.Children.Add(group.transform); hud.transform.Children.Add(neighbor.transform); hud.transform.Children.Add(initiallyHidden.transform);
        GUIManager.instance = new GUIManager { hudCanvas = hud, staminaCanvasGroup = group, bar = new UiElement() };
        FormRegistry.AnyActive = true;
        TransformHud.TickHide();
        Check(!group.gameObject.activeSelf && !neighbor.activeSelf, "Normal transformed HUD includes no stamina bar");
        group.gameObject.SetActive(true);
        TransformHud.TickHide();
        Check(!group.gameObject.activeSelf, "A bar reactivated by another updater is hidden again");
        var optionalBar = new GameObject();
        TransformHud.HideStatusObject(optionalBar);
        Check(!optionalBar.activeSelf, "Optional mod bars use shared reversible hiding");
        FormRegistry.AnyActive = false;
        TransformHud.Restore();
        Check(group.gameObject.activeSelf && neighbor.activeSelf && optionalBar.activeSelf && !initiallyHidden.activeSelf, "Exit restores HUD and optional bars without enabling unrelated hidden elements");
        TransformHud.Restore();
        Check(group.gameObject.activeSelf, "Repeated restoration is harmless");
        FormRegistry.AnyActive = true;
        ThirdPartyCameras.ExternalCameraActive = true;
        TransformHud.TickHide();
        Check(!group.gameObject.activeSelf && neighbor.activeSelf, "Free camera keeps other HUD control while stamina stays hidden");
        FormRegistry.AnyActive = false;
        TransformHud.Restore();
        Check(group.gameObject.activeSelf, "Exit under free camera restores captured visibility");
        var fallback = new UiElement();
        GUIManager.instance = new GUIManager { bar = fallback };
        TransformHud.TickHide();
        Check(!fallback.gameObject.activeSelf, "Bar fallback is hidden even before HUD canvas is ready");
        TransformHud.Restore();
        Check(fallback.gameObject.activeSelf, "Fallback visibility restores");
        ThirdPartyCameras.ExternalCameraActive = false;
        GUIManager.instance = null;
        TransformHud.TickHide(); TransformHud.Restore();
    }

    private static IEnumerable<TypeDefinition> All(TypeDefinition t) => new[] { t }.Concat(t.NestedTypes.SelectMany(All));
    private static void Integration(string dll)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(dll);
        var types = assembly.MainModule.Types.SelectMany(All).ToArray();
        var leader = types.Single(t => t.FullName == "ImScoutmaster.Plugin");
        Check(!leader.Methods.Any(m => m.Name.Contains("JumpStamina") || m.Name.Contains("PeakStats")), "Old leader jump cost and duplicate bar integration are removed");
        var zombie = types.Single(t => t.FullName == "ImZombie.ZombieController");
        Check(!zombie.Methods.Any(m => m.Name == "SyncPlayerStamina"), "Zombie source stamina synchronization is removed");
        foreach (var type in types.Where(t => t.Namespace == "ImZombie" || t.Namespace == "ImScoutmaster"))
        foreach (var method in type.Methods.Where(m => m.HasBody))
        foreach (var i in method.Body.Instructions)
        {
            bool writes = i.OpCode.Code == Code.Stfld || i.OpCode.Code == Code.Stsfld;
            Check(!(writes && i.Operand is FieldReference f && new[] { "sprintStaminaUsage", "jumpStaminaUsage", "jumpStaminaUsageSprinting" }.Contains(f.Name)), "No creature stamina-rate copy remains");
            Check(!(i.Operand is MethodReference r && r.DeclaringType.Name == "CharacterData" && r.Name == "set_currentStamina"), "No creature/source stamina mutation remains");
        }
        var hud = types.Single(t => t.FullName == "Transform.Core.TransformHud");
        Check(hud.Methods.Count(m => m.Name == "TickHide") == 1 && !hud.Methods.Single(m => m.Name == "TickHide").HasParameters, "No keep-stamina HUD branch remains");
        var policy = types.Single(t => t.FullName == "Transform.Core.CreatureStamina");
        Check(policy.Methods.Count(m => m.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyPostfix")) == 2, "Compiled game integration contains both scoped stamina overrides");
        Check(types.Where(t => t.FullName.StartsWith("Transform.TransformPlugin")).SelectMany(t => t.Methods).Where(m => m.HasBody)
            .Any(m => m.Body.Instructions.Any(i => i.OpCode.Code == Code.Ldtoken && i.Operand is TypeReference r && r.FullName == policy.FullName)
                && m.Body.Instructions.Any(i => i.Operand is MethodReference r && r.Name == "PatchAll")), "Startup installs the separate stamina patch class");
    }

    private static void HarmonyIntegration()
    {
        var harmony = new HarmonyLib.Harmony("Transform.Tests.CreatureStamina");
        try
        {
            harmony.PatchAll(typeof(CreatureStamina));
            var original = new Character { stamina = 0.12f };
            var creature = new Character();
            ImZombie.ZombieController.ActiveZombieCharacter = creature;
            Check(creature.infiniteStam && creature.GetTotalStamina() == 1f, "Harmony hooks native-style zombie getters");
            Check(!original.infiniteStam && original.GetTotalStamina() == 0.12f, "Harmony preserves ordinary player getters");
            ImZombie.ZombieController.ActiveZombieCharacter = null;
            ImScoutmaster.Plugin.Controlled = creature;
            Check(creature.infiniteStam && creature.GetTotalStamina() == 1f, "Harmony hooks native-style leader getters");
            ImScoutmaster.Plugin.Controlled = null;
            Check(!creature.infiniteStam && creature.GetTotalStamina() == 0f, "Actual patched getters revert on exit");
        }
        finally { ImZombie.ZombieController.ActiveZombieCharacter = null; ImScoutmaster.Plugin.Controlled = null; harmony.UnpatchSelf(); }
    }

    private static void Main(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("FormStamina <mod DLL> <game Managed directory>");
        Stamina(args[1]); Hud(); Integration(args[0]); HarmonyIntegration();
        Console.WriteLine("PASS: " + _checks + " assertions/contracts: scoped unlimited stamina, installed-game usage/exhaustion early returns, buff expiry, pooling/exit, HUD hiding/restoration/free camera, removal of shared costs and duplicate bar integration.");
    }
}
