using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using Zorro.ControllerSupport;

namespace Transform.Core;

/// <summary>A noninteractive overlay; labels follow the game's current input scheme and bindings.</summary>
internal static class FormControlHints
{
    private static GUIStyle _textStyle, _keyStyle, _titleStyle;
    private static readonly List<Hint> Hints = new List<Hint>();
    private struct Hint
    {
        internal string Key, Action;
        internal Hint(string key, string action) { Key = key; Action = action; }
    }
    private static FormId? _form;
    private static float _refreshAt;

    internal static void OnGUI(TransformPlugin plugin)
    {
        if (plugin.ShowControlHints?.Value != true || TransformMenu.IsOpen || ThirdPartyCameras.ExternalCameraActive
            || (GUIManager.instance != null && GUIManager.instance.windowBlockingInput)) return;
        var form = FormRegistry.ActiveForm;
        if (form == null) return;
        if (_form != form.Id || Time.unscaledTime >= _refreshAt)
        {
            _form = form.Id;
            _refreshAt = Time.unscaledTime + 0.3f;
            Build(form);
        }
        if (_textStyle == null)
        {
            _textStyle = new GUIStyle(GUI.skin.label)
            {
                wordWrap = true, richText = false, padding = new RectOffset(),
                alignment = TextAnchor.UpperLeft, fixedWidth = 0f, fixedHeight = 0f
            };
            _textStyle.normal.background = null;
            _keyStyle = new GUIStyle(_textStyle) { alignment = TextAnchor.UpperRight, fontStyle = FontStyle.Bold };
            _titleStyle = new GUIStyle(_textStyle) { fontStyle = FontStyle.Bold };
        }
        // Use a larger tutorial-sized label than the old HUD, while keeping a
        // conservative lower bound on small laptop/windowed resolutions.
        // Width participates too: a tall narrow window must wrap before it
        // grows beyond the screen edge.
        var layout = new ControlHintsLayout(Screen.width, Screen.height);
        float scale = layout.Scale;
        Font hintFont = TransformMenu.HintFont;
        foreach (GUIStyle style in new[] { _textStyle, _keyStyle, _titleStyle })
        {
            style.font = hintFont;
            style.fontSize = layout.FontSize;
        }
        _textStyle.normal.textColor = new Color(0.98f, 0.98f, 0.96f);
        _titleStyle.normal.textColor = _textStyle.normal.textColor;
        // Bright gold bindings and near-white actions retain contrast over dark
        // scenery; a dark outline keeps both readable against snow and sky.
        _keyStyle.normal.textColor = new Color(1f, 0.83f, 0.40f);
        float gap = layout.Gap;
        float maxWidth = layout.MaxWidth;
        float keyWidth, actionWidth, width, height;
        Measure(maxWidth, gap, scale, out keyWidth, out actionWidth, out width, out height);

        // A form can have several rows and a controller binding can be long.
        // Re-measure after reducing the font so the complete hint block always
        // stays inside the safe screen area instead of clipping at the bottom.
        while (height > layout.AvailableHeight && _textStyle.fontSize > 14)
        {
            int size = _textStyle.fontSize - 1;
            _textStyle.fontSize = size; _keyStyle.fontSize = size; _titleStyle.fontSize = size;
            Measure(maxWidth, gap, scale, out keyWidth, out actionWidth, out width, out height);
        }
        Rect block = layout.Place(width, height, out float drawScale);
        float y = 0f;
        Color color = GUI.color;
        Color contentColor = GUI.contentColor;
        int depth = GUI.depth;
        Matrix4x4 matrix = GUI.matrix;
        try
        {
            GUI.depth = -20;
            GUI.color = Color.white;
            GUI.contentColor = Color.white;
            GUI.matrix = Matrix4x4.TRS(new Vector3(block.x, block.y, 0f), Quaternion.identity, Vector3.one * drawScale);
            foreach (Hint hint in Hints)
            {
                float rowHeight = RowHeight(hint, keyWidth, actionWidth, width);
                if (hint.Key != null)
                {
                    TutorialLabel(new Rect(0f, y, keyWidth, rowHeight), hint.Key, _keyStyle, scale);
                    TutorialLabel(new Rect(keyWidth + gap, y, actionWidth, rowHeight), hint.Action, _textStyle, scale);
                }
                else TutorialLabel(new Rect(0f, y, width, rowHeight), hint.Action, _titleStyle, scale);
                y += rowHeight + 5f * scale;
            }
        }
        finally { GUI.matrix = matrix; GUI.color = color; GUI.contentColor = contentColor; GUI.depth = depth; }
    }

    private static void TutorialLabel(Rect rect, string text, GUIStyle style, float scale)
    {
        // Outline every side of the glyph, including bindings. A single offset
        // shadow leaves most edges washed out over very bright terrain.
        Color foreground = style.normal.textColor;
        try
        {
            style.normal.textColor = new Color(0.055f, 0.065f, 0.075f, 0.95f);
            float offset = Mathf.Max(1f, Mathf.Round(1.5f * scale));
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                GUI.Label(new Rect(rect.x + dx * offset, rect.y + dy * offset, rect.width, rect.height), text, style);
            }
        }
        finally { style.normal.textColor = foreground; }
        GUI.Label(rect, text, style);
    }

    private static float RowHeight(Hint hint, float keyWidth, float actionWidth, float width)
        => hint.Key == null ? _titleStyle.CalcHeight(new GUIContent(hint.Action), width)
            : Mathf.Max(_keyStyle.CalcHeight(new GUIContent(hint.Key), keyWidth),
                _textStyle.CalcHeight(new GUIContent(hint.Action), actionWidth));

    private static void Measure(float maxWidth, float gap, float scale,
        out float keyWidth, out float actionWidth, out float width, out float height)
    {
        float standaloneWidth = 0f;
        keyWidth = 0f;
        actionWidth = 0f;
        foreach (Hint hint in Hints)
        {
            if (hint.Key == null)
                standaloneWidth = Mathf.Max(standaloneWidth, _titleStyle.CalcSize(new GUIContent(hint.Action)).x);
            else
            {
                keyWidth = Mathf.Max(keyWidth, _keyStyle.CalcSize(new GUIContent(hint.Key)).x);
                actionWidth = Mathf.Max(actionWidth, _textStyle.CalcSize(new GUIContent(hint.Action)).x);
            }
        }
        // Anchor both columns to their measured text block. The action column
        // receives the remaining width and wraps naturally when needed.
        keyWidth = Mathf.Min(Mathf.Ceil(keyWidth + 2f * scale), maxWidth * 0.42f);
        width = Mathf.Min(maxWidth, Mathf.Ceil(
            Mathf.Max(standaloneWidth, keyWidth + gap + actionWidth + 2f * scale)));
        actionWidth = Mathf.Max(1f, width - keyWidth - gap);
        height = 0f;
        foreach (Hint hint in Hints)
            height += RowHeight(hint, keyWidth, actionWidth, width) + 5f * scale;
        if (Hints.Count > 0) height -= 5f * scale;
    }

    private static string Tr(string zh, string en) => Localization.Tr(zh, en);

    private static string Binding(InputAction action, KeyCode fallback)
    {
        bool pad = (int)InputHandler.GetCurrentUsedInputScheme() != 0;
        if (action != null)
        {
            for (int i = 0; i < action.bindings.Count; i++)
            {
                var binding = action.bindings[i];
                if (binding.isComposite || binding.isPartOfComposite) continue;
                string path = binding.effectivePath ?? "";
                bool gamepad = path.IndexOf("Gamepad", StringComparison.OrdinalIgnoreCase) >= 0;
                bool keyboard = path.IndexOf("Keyboard", StringComparison.OrdinalIgnoreCase) >= 0
                    || path.IndexOf("Mouse", StringComparison.OrdinalIgnoreCase) >= 0;
                if (pad ? !gamepad : !keyboard) continue;
                string label = action.GetBindingDisplayString(i, InputBinding.DisplayStringOptions.DontIncludeInteractions);
                if (!string.IsNullOrEmpty(label)) return label;
            }
        }
        return fallback == KeyCode.None ? Tr("移动摇杆", "Move stick") : fallback.ToString();
    }

    private static void Build(FormRegistry.FormDescriptor form)
    {
        Hints.Clear();
        Action<InputAction, KeyCode, string, string> add = (action, key, zh, en) =>
            Hints.Add(new Hint(Binding(action, key), Tr(zh, en)));
        add(CharacterInput.action_move, KeyCode.None, "移动", "Move");
        // Directional keyboard bindings are separate actions; label their configured keys.
        if ((int)InputHandler.GetCurrentUsedInputScheme() == 0)
        {
            Hints[0] = new Hint(string.Join("/", new[] {
                Binding(CharacterInput.action_moveForward, KeyCode.W), Binding(CharacterInput.action_moveLeft, KeyCode.A),
                Binding(CharacterInput.action_moveBackward, KeyCode.S), Binding(CharacterInput.action_moveRight, KeyCode.D) }), Tr("移动", "Move"));
        }
        switch (form.Id)
        {
            case FormId.Crow:
                Hints[0] = new Hint(Hints[0].Key, Tr("飞行移动", "Move in flight"));
                add(CharacterInput.action_jump, ImRaven.RavenPlugin.CrowSettings.UpKey.Value, "起飞 / 上升", "Take off / rise");
                add(CharacterInput.action_crouch, ImRaven.RavenPlugin.CrowSettings.DownKey.Value, "下降 / 降落", "Descend / land");
                add(CharacterInput.action_sprint, ImRaven.RavenPlugin.CrowSettings.SprintKey.Value, "加速", "Boost");
                break;
            case FormId.Raven:
                add(CharacterInput.action_jump, ImRaven.RavenPlugin.UpKey.Value, "上升", "Rise");
                add(CharacterInput.action_crouch, ImRaven.RavenPlugin.DownKey.Value, "下降", "Descend");
                add(CharacterInput.action_sprint, ImRaven.RavenPlugin.SprintKey.Value, "加速", "Boost");
                break;
            case FormId.Ghost:
                add(CharacterInput.action_jump, ImGhost.GhostPlugin.UpKey.Value, "上升", "Rise");
                add(CharacterInput.action_crouch, ImGhost.GhostPlugin.DownKey.Value, "下降", "Descend");
                add(CharacterInput.action_sprint, ImGhost.GhostPlugin.SprintKey.Value, "加速", "Boost");
                add(CharacterInput.action_useSecondary, ImGhost.GhostPlugin.AttackKey.Value, "蓄力爆发", "Charge burst");
                break;
            case FormId.Tornado:
                Hints.Add(new Hint(null, Tr("自动悬浮 · 靠近玩家推动", "Auto hover · Push nearby players")));
                break;
            case FormId.Zombie:
            case FormId.ZombiePlayer:
            case FormId.MushroomZombie:
                add(CharacterInput.action_sprint, ImZombie.ZombiePlugin.SprintKey.Value, "加速", "Sprint");
                add(CharacterInput.action_crouch, ImZombie.ZombiePlugin.CrouchKey.Value, "蹲伏", "Crouch");
                add(CharacterInput.action_jump, ImZombie.ZombiePlugin.JumpKey.Value, "跳跃", "Jump");
                add(CharacterInput.action_usePrimary, ImZombie.ZombiePlugin.ClimbKey.Value, "攀爬", "Climb");
                add(CharacterInput.action_useSecondary, ImZombie.ZombiePlugin.AttackKey.Value, "扑咬", "Bite");
                break;
            case FormId.Scoutmaster:
                add(CharacterInput.action_sprint, KeyCode.LeftShift, "奔跑", "Sprint");
                add(CharacterInput.action_jump, KeyCode.Space, "跳跃", "Jump");
                add(CharacterInput.action_usePrimary, KeyCode.Mouse0, "攀爬", "Climb");
                add(CharacterInput.action_useSecondary, KeyCode.Mouse1, "按住抓取 / 松开投掷", "Hold to grab / release to throw");
                Hints.Add(new Hint(ImScoutmaster.Plugin.ToggleKey.Value.ToString(), Tr("手动跌落", "Manual fall")));
                break;
            default:
                KeyCode sprint = form.Id == FormId.Statue ? Statue.StatuePlugin.SprintKey.Value
                    : form.Id == FormId.Tumbleweed ? ImTumbleweed.TumbleweedPlugin.SprintKey.Value : KeyCode.LeftShift;
                KeyCode jump = form.Id == FormId.Statue ? Statue.StatuePlugin.JumpKey.Value
                    : form.Id == FormId.Tumbleweed ? ImTumbleweed.TumbleweedPlugin.JumpKey.Value : ImCritter.CritterPlugin.JumpKey.Value;
                add(CharacterInput.action_sprint, sprint, form.Id == FormId.Frog ? "跳得更远" : "加速", form.Id == FormId.Frog ? "Longer leap" : "Sprint");
                if (form.Id != FormId.Beetle && form.Id != FormId.Scorpion)
                    add(CharacterInput.action_jump, jump, form.Id == FormId.Frog ? "蛙跳" : "跳跃", form.Id == FormId.Frog ? "Leap" : "Jump");
                string zh = null, en = null;
                switch (form.Id)
                {
                    case FormId.Tumbleweed: zh = "冲刺"; en = "Dash"; break;
                    case FormId.Frog: zh = "吐舌"; en = "Tongue"; break;
                    case FormId.Beetle: zh = "冲撞"; en = "Ram"; break;
                    case FormId.Scorpion: zh = "蛰刺"; en = "Sting"; break;
                    case FormId.Coconut: zh = "按住蓄力 / 松开砸地"; en = "Hold to charge / release to slam"; break;
                    case FormId.Bomb: zh = "点燃引线"; en = "Ignite fuse"; break;
                }
                if (zh != null) add(CharacterInput.action_useSecondary,
                    form.Id == FormId.Tumbleweed ? KeyCode.Mouse1 : ImCritter.CritterPlugin.AttackKey.Value, zh, en);
                break;
        }
    }
}
