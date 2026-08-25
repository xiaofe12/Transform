using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Transform.Statue;

namespace Transform.Core;

/// <summary>Playable transformation forms offered by the unified menu.</summary>
internal enum FormId
{
    Zombie,
    ZombiePlayer,
    MushroomZombie,
    Scoutmaster,
    Ghost,
    Tumbleweed,
    Tornado,
    Statue,
    Frog,
    Beetle,
    Scorpion,
    Coconut,
    Bomb,
    Cactus
}

/// <summary>
/// Catalog of every transformation form. Each entry binds one form to its backing module and
/// exposes the live-adjustable camera and behaviour parameters shown on menu page 2. Config
/// entries are resolved lazily because modules bind them during TransformPlugin.Awake.
/// </summary>
internal static class FormRegistry
{
    /// <summary>A single float parameter rendered as a slider on menu page 2.</summary>
    internal sealed class ParamDescriptor
    {
        internal readonly string LabelZh;
        internal readonly string LabelEn;
        internal readonly Func<ConfigEntry<float>> Entry;
        internal readonly float Min;
        internal readonly float Max;

        internal ParamDescriptor(string labelZh, string labelEn, Func<ConfigEntry<float>> entry, float min, float max)
        {
            LabelZh = labelZh;
            LabelEn = labelEn;
            Entry = entry;
            Min = min;
            Max = max;
        }

        internal string Label => Localization.Tr(LabelZh, LabelEn);
    }

    /// <summary>A boolean parameter rendered as a toggle on menu page 2.</summary>
    internal sealed class BoolParamDescriptor
    {
        internal readonly string LabelZh;
        internal readonly string LabelEn;
        internal readonly Func<ConfigEntry<bool>> Entry;
        internal readonly bool Visible;   // false 时 UI 不渲染（仍可读写 ConfigEntry）

        internal BoolParamDescriptor(string labelZh, string labelEn, Func<ConfigEntry<bool>> entry, bool visible = true)
        {
            LabelZh = labelZh;
            LabelEn = labelEn;
            Entry = entry;
            Visible = visible;
        }

        internal string Label => Localization.Tr(LabelZh, LabelEn);
    }

    internal sealed class FormDescriptor
    {
        internal readonly FormId Id;
        internal readonly string NameZh;
        internal readonly string NameEn;
        internal readonly string DescZh;
        internal readonly string DescEn;
        internal readonly Func<bool> IsActive;
        internal readonly Func<bool> CanEnter;
        internal readonly Func<bool> Enter;
        internal readonly Action Exit;

        /// <summary>Per-form third-person camera parameters (live-adjustable on page 2).</summary>
        internal readonly Func<ConfigEntry<float>> CameraDistance;
        internal readonly Func<ConfigEntry<float>> CameraHeight;
        internal readonly Func<ConfigEntry<float>> CameraFov;
        internal readonly float DistanceMin;
        internal readonly float DistanceMax;
        internal readonly float HeightMin;
        internal readonly float HeightMax;
        internal readonly float FovMin;
        internal readonly float FovMax;

        /// <summary>Behaviour parameters shown on page 2 below the camera block.</summary>
        internal readonly List<ParamDescriptor> Params = new List<ParamDescriptor>();
        internal readonly List<BoolParamDescriptor> BoolParams = new List<BoolParamDescriptor>();

        /// <summary>Optional localized reason shown on the card when the form cannot be entered
        /// for a KNOWN reason (e.g. "host lacks the mod"). Null = generic "Unavailable".</summary>
        internal readonly Func<string> UnavailableReason;

        internal string Name => Localization.Tr(NameZh, NameEn);
        internal string Description => Localization.Tr(DescZh, DescEn);

        internal FormDescriptor(
            FormId id,
            string nameZh, string nameEn, string descZh, string descEn,
            Func<bool> isActive, Func<bool> canEnter, Func<bool> enter, Action exit,
            Func<ConfigEntry<float>> cameraDistance, Func<ConfigEntry<float>> cameraHeight, Func<ConfigEntry<float>> cameraFov,
            float distanceMin, float distanceMax, float heightMin, float heightMax, float fovMin, float fovMax,
            Func<string> unavailableReason = null)
        {
            Id = id;
            NameZh = nameZh;
            NameEn = nameEn;
            DescZh = descZh;
            DescEn = descEn;
            IsActive = isActive;
            CanEnter = canEnter;
            Enter = enter;
            Exit = exit;
            CameraDistance = cameraDistance;
            CameraHeight = cameraHeight;
            CameraFov = cameraFov;
            DistanceMin = distanceMin;
            DistanceMax = distanceMax;
            HeightMin = heightMin;
            HeightMax = heightMax;
            FovMin = fovMin;
            FovMax = fovMax;
            UnavailableReason = unavailableReason;
        }
    }

    private static readonly List<FormDescriptor> _forms = new List<FormDescriptor>
    {
        new FormDescriptor(
            FormId.Zombie,
            "普通僵尸", "Normal Zombie",
            "WASD 移动 · Shift 加速 · Ctrl 蹲伏 · 空格跳跃 · 左键攀爬 · 右键扑咬",
            "WASD move · Shift sprint · Ctrl crouch · Space jump · LMB climb · RMB bite",
            () => ImZombie.ZombiePlugin.ActiveAppearance == ImZombie.ZombiePlugin.ZombieAppearanceOption.Mushroom,
            () => ImZombie.ZombiePlugin.CanEnter(Character.localCharacter),
            () => ImZombie.ZombiePlugin.Enter(Character.localCharacter, ImZombie.ZombiePlugin.ZombieAppearanceOption.Mushroom),
            ImZombie.ZombiePlugin.Exit,
            () => ImZombie.ZombiePlugin.CameraDistance,
            () => ImZombie.ZombiePlugin.CameraHeight,
            () => ImZombie.ZombiePlugin.CameraFov,
            1.5f, 6f, 0.3f, 1.8f, 60f, 110f)
        {
            Params =
            {
                new ParamDescriptor("移动速度", "Movement speed", () => ImZombie.ZombiePlugin.MovementSpeed, 0f, 40f),
                new ParamDescriptor("加速倍率", "Sprint multiplier", () => ImZombie.ZombiePlugin.SprintMultiplier, 1f, 5f),
                new ParamDescriptor("跳跃力", "Jump force", () => ImZombie.ZombiePlugin.JumpForce, 0f, 25f),
                new ParamDescriptor("扑咬冷却", "Bite cooldown", () => ImZombie.ZombiePlugin.AttackCooldown, 0.1f, 5f)
            }
        },
        new FormDescriptor(
            FormId.ZombiePlayer,
            "玩家化僵尸", "Player Zombie",
            "保留玩家装备：WASD 移动 · Shift 加速 · Ctrl 蹲伏 · 空格跳跃 · 左键攀爬 · 右键扑咬",
            "Player outfit: WASD move · Shift sprint · Ctrl crouch · Space jump · LMB climb · RMB bite",
            () => ImZombie.ZombiePlugin.ActiveAppearance == ImZombie.ZombiePlugin.ZombieAppearanceOption.Player,
            () => ImZombie.ZombiePlugin.CanEnter(Character.localCharacter),
            () => ImZombie.ZombiePlugin.Enter(Character.localCharacter, ImZombie.ZombiePlugin.ZombieAppearanceOption.Player),
            ImZombie.ZombiePlugin.Exit,
            () => ImZombie.ZombiePlugin.CameraDistance,
            () => ImZombie.ZombiePlugin.CameraHeight,
            () => ImZombie.ZombiePlugin.CameraFov,
            1.5f, 6f, 0.3f, 1.8f, 60f, 110f)
        {
            Params =
            {
                new ParamDescriptor("移动速度", "Movement speed", () => ImZombie.ZombiePlugin.MovementSpeed, 0f, 40f),
                new ParamDescriptor("加速倍率", "Sprint multiplier", () => ImZombie.ZombiePlugin.SprintMultiplier, 1f, 5f),
                new ParamDescriptor("跳跃力", "Jump force", () => ImZombie.ZombiePlugin.JumpForce, 0f, 25f),
                new ParamDescriptor("扑咬冷却", "Bite cooldown", () => ImZombie.ZombiePlugin.AttackCooldown, 0.1f, 5f)
            }
        },
        new FormDescriptor(
            FormId.MushroomZombie,
            "大蘑菇僵尸", "Mushroom-Man Zombie",
            "恐僵外观：WASD 移动 · Shift 加速 · Ctrl 蹲伏 · 空格跳跃 · 左键攀爬 · 右键扑咬",
            "Phobia look: WASD move · Shift sprint · Ctrl crouch · Space jump · LMB climb · RMB bite",
            () => ImZombie.ZombiePlugin.ActiveAppearance == ImZombie.ZombiePlugin.ZombieAppearanceOption.MushroomMan,
            () => ImZombie.ZombiePlugin.CanEnter(Character.localCharacter),
            () => ImZombie.ZombiePlugin.Enter(Character.localCharacter, ImZombie.ZombiePlugin.ZombieAppearanceOption.MushroomMan),
            ImZombie.ZombiePlugin.Exit,
            () => ImZombie.ZombiePlugin.CameraDistance,
            () => ImZombie.ZombiePlugin.CameraHeight,
            () => ImZombie.ZombiePlugin.CameraFov,
            1.5f, 6f, 0.3f, 1.8f, 60f, 110f)
        {
            Params =
            {
                new ParamDescriptor("移动速度", "Movement speed", () => ImZombie.ZombiePlugin.MovementSpeed, 0f, 40f),
                new ParamDescriptor("加速倍率", "Sprint multiplier", () => ImZombie.ZombiePlugin.SprintMultiplier, 1f, 5f),
                new ParamDescriptor("跳跃力", "Jump force", () => ImZombie.ZombiePlugin.JumpForce, 0f, 25f),
                new ParamDescriptor("扑咬冷却", "Bite cooldown", () => ImZombie.ZombiePlugin.AttackCooldown, 0.1f, 5f)
            }
        },
        new FormDescriptor(
            FormId.Scoutmaster,
            "童军领队", "Scoutmaster",
            "WASD 移动 · Shift 奔跑 · 空格跳跃 · 右键按住抓取/松开投掷 · G 手动跌落",
            "WASD move · Shift sprint · Space jump · hold RMB grab / release throw · G manual fall",
            () => ImScoutmaster.Plugin.Instance != null && ImScoutmaster.Plugin.Instance.IsFormActive,
            () => ImScoutmaster.Plugin.Instance != null && ImScoutmaster.Plugin.Instance.CanEnterScoutmasterForm(),
            () => ImScoutmaster.Plugin.Instance != null && ImScoutmaster.Plugin.Instance.EnterScoutmasterFormExternal(),
            () => { var instance = ImScoutmaster.Plugin.Instance; if (instance != null) instance.ExitScoutmasterFormExternal(); },
            () => ImScoutmaster.Plugin.ThirdPersonDistance,
            () => ImScoutmaster.Plugin.ThirdPersonHeightOffset,
            null,
            2f, 16f, -2f, 6f, 60f, 110f)
        {
            Params =
            {
                new ParamDescriptor("投掷力度", "Throw force", () => ImScoutmaster.Plugin.ThrowForce, 100f, 2500f),
                new ParamDescriptor("投掷上抛 bias", "Throw up bias", () => ImScoutmaster.Plugin.ThrowUpBias, 0f, 0.8f)
            }
        },
        new FormDescriptor(
            FormId.Ghost,
            "幽灵", "Ghost",
            "无视碰撞飞行 · WASD 移动 · 空格上升 · Ctrl 下降 · Shift 加速 · 右键蓄力爆发",
            "Fly through anything · WASD move · Space rise · Ctrl descend · Shift sprint · RMB charge burst",
            () => ImGhost.GhostPlugin.IsActive,
            () => ImGhost.GhostPlugin.CanEnter(Character.localCharacter),
            () => ImGhost.GhostPlugin.Enter(Character.localCharacter),
            ImGhost.GhostPlugin.Exit,
            () => ImGhost.GhostPlugin.CameraDistance,
            () => ImGhost.GhostPlugin.CameraHeight,
            () => ImGhost.GhostPlugin.CameraFov,
            4f, 20f, 1f, 10f, 60f, 110f)
        {
            Params =
            {
                new ParamDescriptor("飞行速度", "Flight speed", () => ImGhost.GhostPlugin.MovementSpeed, 0f, 30f),
                new ParamDescriptor("升降速度", "Vertical speed", () => ImGhost.GhostPlugin.VerticalSpeed, 0f, 20f),
                new ParamDescriptor("加速倍率", "Sprint multiplier", () => ImGhost.GhostPlugin.SprintMultiplier, 1f, 5f),
                new ParamDescriptor("放电半径", "Discharge radius", () => ImGhost.GhostPlugin.AttackRadius, 0.5f, 10f),
                new ParamDescriptor("击退力度", "Knockback force", () => ImGhost.GhostPlugin.KnockbackForce, 0f, 30f)
            }
        },
        new FormDescriptor(
            FormId.Tumbleweed,
            "风滚草", "Tumbleweed",
            "WASD 滚动 · Shift 加速 · 空格弹跳 · 右键向前冲刺",
            "WASD roll · Shift sprint · Space hop · RMB forward dash",
            () => ImTumbleweed.TumbleweedPlugin.IsActive,
            () => ImTumbleweed.TumbleweedPlugin.CanEnter(Character.localCharacter),
            () => ImTumbleweed.TumbleweedPlugin.Enter(Character.localCharacter),
            ImTumbleweed.TumbleweedPlugin.Exit,
            () => ImTumbleweed.TumbleweedPlugin.CameraDistance,
            () => ImTumbleweed.TumbleweedPlugin.CameraHeight,
            () => ImTumbleweed.TumbleweedPlugin.CameraFov,
            6f, 30f, 1f, 14f, 60f, 110f)
        {
            Params =
            {
                new ParamDescriptor("推动力", "Movement force", () => ImTumbleweed.TumbleweedPlugin.MovementForce, 0f, 60f),
                new ParamDescriptor("加速倍率", "Sprint multiplier", () => ImTumbleweed.TumbleweedPlugin.SprintMultiplier, 1f, 5f),
                new ParamDescriptor("最大速度", "Max speed", () => ImTumbleweed.TumbleweedPlugin.MaxSpeed, 2f, 60f),
                new ParamDescriptor("弹跳速度", "Hop speed", () => ImTumbleweed.TumbleweedPlugin.JumpSpeed, 1f, 30f),
                new ParamDescriptor("冲刺力度", "Dash force", () => ImTumbleweed.TumbleweedPlugin.DashForce, 5f, 80f),
                new ParamDescriptor("冲刺冷却", "Dash cooldown", () => ImTumbleweed.TumbleweedPlugin.DashCooldown, 0.2f, 10f)
            }
        },
        new FormDescriptor(
            FormId.Tornado,
            "龙卷风", "Tornado",
            "WASD 飞行 · 自动保持悬浮 · 靠近玩家会卷起并推开",
            "WASD fly · auto-hover · nearby players get swept up and pushed away",
            () => ImTornado.WindPlugin.IsActive,
            () => ImTornado.WindPlugin.CanEnter(Character.localCharacter),
            () => ImTornado.WindPlugin.Enter(Character.localCharacter),
            ImTornado.WindPlugin.Exit,
            () => ImTornado.WindPlugin.CameraDistance,
            () => ImTornado.WindPlugin.CameraHeight,
            () => ImTornado.WindPlugin.CameraFov,
            8f, 30f, 2f, 14f, 60f, 110f)
        {
            Params =
            {
                new ParamDescriptor("移动速度", "Movement speed", () => ImTornado.WindPlugin.MovementSpeed, 0f, 40f),
                new ParamDescriptor("悬浮高度", "Hover height", () => ImTornado.WindPlugin.HoverHeight, 1f, 25f),
                new ParamDescriptor("卷起推力", "Push force", () => ImTornado.WindPlugin.PushForce, 0f, 60f)
            }
        },
        new FormDescriptor(
            FormId.Statue,
            "石化侦察兵", "Petrified Scout",
            "WASD 翻滚 · Shift 加速（耗体力）· 空格跳动 · 可被其他玩家撞碎",
            "WASD roll · Shift sprint (stamina) · Space hop · can be shattered by players",
            () => StatuePlugin.IsActive,
            () => StatuePlugin.CanEnter(Character.localCharacter),
            () => StatuePlugin.Enter(Character.localCharacter),
            StatuePlugin.Exit,
            () => StatuePlugin.CameraDistance,
            () => StatuePlugin.CameraHeight,
            () => StatuePlugin.CameraFov,
            3f, 15f, 1f, 8f, 60f, 110f)
        {
            Params =
            {
                new ParamDescriptor("推动力", "Movement force", () => StatuePlugin.MovementForce, 0f, 60f),
                new ParamDescriptor("加速倍率（耗体力）", "Sprint multiplier (stamina)", () => StatuePlugin.SprintMultiplier, 1f, 5f),
                new ParamDescriptor("跳动速度", "Hop speed", () => StatuePlugin.JumpSpeed, 1f, 30f)
            }
        },
        new FormDescriptor(
            FormId.Frog,
            "青蛙", "Frog",
            "WASD 选方向 · 空格蛙跳 · 按住 Shift 跳更远 · 右键伸舌抓人",
            "WASD aim · Space leap · hold Shift for longer leap · RMB tongue grab",
            () => ImCritter.CritterPlugin.IsActive && ImCritter.CritterPlugin.ActiveKind == ImCritter.CritterKind.Frog,
            () => ImCritter.CritterPlugin.CanEnter(Character.localCharacter),
            () => ImCritter.CritterPlugin.Enter(Character.localCharacter, ImCritter.CritterKind.Frog),
            ImCritter.CritterPlugin.Exit,
            () => ImCritter.CritterPlugin.CameraDistance(ImCritter.CritterKind.Frog),
            () => ImCritter.CritterPlugin.CameraHeight(ImCritter.CritterKind.Frog),
            () => ImCritter.CritterPlugin.CameraFov(ImCritter.CritterKind.Frog),
            2f, 20f, 0.3f, 10f, 60f, 110f,
            null)
        {
            Params =
            {
                new ParamDescriptor("蛙跳距离", "Leap distance", () => ImCritter.CritterPlugin.MaxSpeed(ImCritter.CritterKind.Frog), 2f, 40f),
                new ParamDescriptor("蛙跳高度", "Leap height", () => ImCritter.CritterPlugin.JumpSpeed(ImCritter.CritterKind.Frog), 1f, 30f),
                new ParamDescriptor("蛙跳倍率", "Leap power", () => ImCritter.CritterPlugin.JumpPower(ImCritter.CritterKind.Frog), 0.1f, 3f),
                new ParamDescriptor("攻击冷却", "Attack cooldown", () => ImCritter.CritterPlugin.AttackCooldown(ImCritter.CritterKind.Frog), 0.1f, 10f)
            }
        },
        new FormDescriptor(
            FormId.Beetle,
            "甲虫", "Beetle",
            "WASD 爬行 · Shift 加速（耗体力）· 右键冲撞击退",
            "WASD crawl · Shift sprint (stamina) · RMB ram knockback",
            () => ImCritter.CritterPlugin.IsActive && ImCritter.CritterPlugin.ActiveKind == ImCritter.CritterKind.Beetle,
            () => ImCritter.CritterPlugin.CanEnter(Character.localCharacter),
            () => ImCritter.CritterPlugin.Enter(Character.localCharacter, ImCritter.CritterKind.Beetle),
            ImCritter.CritterPlugin.Exit,
            () => ImCritter.CritterPlugin.CameraDistance(ImCritter.CritterKind.Beetle),
            () => ImCritter.CritterPlugin.CameraHeight(ImCritter.CritterKind.Beetle),
            () => ImCritter.CritterPlugin.CameraFov(ImCritter.CritterKind.Beetle),
            2f, 20f, 0.3f, 10f, 60f, 110f,
            null)
        {
            Params =
            {
                new ParamDescriptor("加速倍率（耗体力）", "Sprint multiplier (stamina)", () => ImCritter.CritterPlugin.SprintMultiplier(ImCritter.CritterKind.Beetle), 1f, 5f),
                new ParamDescriptor("最大速度", "Max speed", () => ImCritter.CritterPlugin.MaxSpeed(ImCritter.CritterKind.Beetle), 2f, 60f),
                new ParamDescriptor("攻击冷却", "Attack cooldown", () => ImCritter.CritterPlugin.AttackCooldown(ImCritter.CritterKind.Beetle), 0.1f, 10f)
            }
        },
        new FormDescriptor(
            FormId.Scorpion,
            "蝎子", "Scorpion",
            "WASD 移动 · Shift 加速 · 空格弹跳 · 右键蜇击（击退+中毒）",
            "WASD move · Shift sprint · Space hop · RMB sting (knockback + poison)",
            () => ImCritter.CritterPlugin.IsActive && ImCritter.CritterPlugin.ActiveKind == ImCritter.CritterKind.Scorpion,
            () => ImCritter.CritterPlugin.CanEnter(Character.localCharacter),
            () => ImCritter.CritterPlugin.Enter(Character.localCharacter, ImCritter.CritterKind.Scorpion),
            ImCritter.CritterPlugin.Exit,
            () => ImCritter.CritterPlugin.CameraDistance(ImCritter.CritterKind.Scorpion),
            () => ImCritter.CritterPlugin.CameraHeight(ImCritter.CritterKind.Scorpion),
            () => ImCritter.CritterPlugin.CameraFov(ImCritter.CritterKind.Scorpion),
            2f, 20f, 0.3f, 10f, 60f, 110f,
            null)
        {
            Params =
            {
                new ParamDescriptor("加速倍率", "Sprint multiplier", () => ImCritter.CritterPlugin.SprintMultiplier(ImCritter.CritterKind.Scorpion), 1f, 5f),
                new ParamDescriptor("最大速度", "Max speed", () => ImCritter.CritterPlugin.MaxSpeed(ImCritter.CritterKind.Scorpion), 2f, 60f),
                new ParamDescriptor("弹跳速度", "Hop speed", () => ImCritter.CritterPlugin.JumpSpeed(ImCritter.CritterKind.Scorpion), 1f, 30f),
                new ParamDescriptor("攻击冷却", "Attack cooldown", () => ImCritter.CritterPlugin.AttackCooldown(ImCritter.CritterKind.Scorpion), 0.1f, 10f)
            }
        },
        new FormDescriptor(
            FormId.Coconut,
            "椰子", "Coconut",
            "WASD 滚动 · Shift 加速 · 空格跳跃 · 右键蓄力砸向准星 · 碎裂后当前位置恢复",
            "WASD roll · Shift sprint · Space jump · hold RMB slam at crosshair · restore where cracked",
            () => ImCritter.CritterPlugin.IsActive && ImCritter.CritterPlugin.ActiveKind == ImCritter.CritterKind.Coconut,
            () => ImCritter.CritterPlugin.CanEnter(Character.localCharacter),
            () => ImCritter.CritterPlugin.Enter(Character.localCharacter, ImCritter.CritterKind.Coconut),
            ImCritter.CritterPlugin.Exit,
            () => ImCritter.CritterPlugin.CameraDistance(ImCritter.CritterKind.Coconut),
            () => ImCritter.CritterPlugin.CameraHeight(ImCritter.CritterKind.Coconut),
            () => ImCritter.CritterPlugin.CameraFov(ImCritter.CritterKind.Coconut),
            2f, 20f, 0.3f, 10f, 60f, 110f,
            null)
        {
            Params =
            {
                new ParamDescriptor("加速倍率", "Sprint multiplier", () => ImCritter.CritterPlugin.SprintMultiplier(ImCritter.CritterKind.Coconut), 1f, 5f),
                new ParamDescriptor("最大速度", "Max speed", () => ImCritter.CritterPlugin.MaxSpeed(ImCritter.CritterKind.Coconut), 2f, 60f),
                new ParamDescriptor("跳跃速度", "Jump speed", () => ImCritter.CritterPlugin.JumpSpeed(ImCritter.CritterKind.Coconut), 1f, 30f),
                new ParamDescriptor("砸击冷却", "Slam cooldown", () => ImCritter.CritterPlugin.AttackCooldown(ImCritter.CritterKind.Coconut), 0.1f, 10f)
            }
        },
        new FormDescriptor(
            FormId.Bomb,
            "炸弹", "Bomb",
            "WASD/方向键滚动 · Shift 加速 · 空格跳跃 · 右键手动点燃 · 爆炸后当前位置恢复",
            "WASD/arrow keys roll · Shift sprint · Space jump · RMB light fuse · restore where exploded",
            () => ImCritter.CritterPlugin.IsActive && ImCritter.CritterPlugin.ActiveKind == ImCritter.CritterKind.Bomb,
            () => ImCritter.CritterPlugin.CanEnter(Character.localCharacter),
            () => ImCritter.CritterPlugin.Enter(Character.localCharacter, ImCritter.CritterKind.Bomb),
            ImCritter.CritterPlugin.Exit,
            () => ImCritter.CritterPlugin.CameraDistance(ImCritter.CritterKind.Bomb),
            () => ImCritter.CritterPlugin.CameraHeight(ImCritter.CritterKind.Bomb),
            () => ImCritter.CritterPlugin.CameraFov(ImCritter.CritterKind.Bomb),
            2f, 20f, 0.3f, 10f, 60f, 110f,
            null)
        {
            Params =
            {
                new ParamDescriptor("推动力", "Movement force", () => ImCritter.CritterPlugin.MovementForce(ImCritter.CritterKind.Bomb), 0f, 60f),
                new ParamDescriptor("加速倍率", "Sprint multiplier", () => ImCritter.CritterPlugin.SprintMultiplier(ImCritter.CritterKind.Bomb), 1f, 5f),
                new ParamDescriptor("最大速度", "Max speed", () => ImCritter.CritterPlugin.MaxSpeed(ImCritter.CritterKind.Bomb), 2f, 60f),
                new ParamDescriptor("跳跃速度", "Jump speed", () => ImCritter.CritterPlugin.JumpSpeed(ImCritter.CritterKind.Bomb), 1f, 30f),
                new ParamDescriptor("点燃冷却", "Ignite cooldown", () => ImCritter.CritterPlugin.AttackCooldown(ImCritter.CritterKind.Bomb), 0.1f, 10f)
            }
        }
    };

    /// <summary>All forms in menu order.</summary>
    internal static IReadOnlyList<FormDescriptor> Forms => _forms;

    /// <summary>Returns the currently active form, or null when the player is normal.</summary>
    internal static FormDescriptor ActiveForm
    {
        get
        {
            foreach (FormDescriptor form in _forms)
            {
                try
                {
                    if (form.IsActive()) return form;
                }
                catch { /* module not ready — treat as inactive */ }
            }
            return null;
        }
    }

    /// <summary>True when any transformation form is active.</summary>
    internal static bool AnyActive => ActiveForm != null;

    /// <summary>Exits whatever form is currently active. Returns true when a form was exited.</summary>
    internal static bool ExitActiveForm()
    {
        FormDescriptor active = ActiveForm;
        if (active == null) return false;
        try
        {
            active.Exit();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}



