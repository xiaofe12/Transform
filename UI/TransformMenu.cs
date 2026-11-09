using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace Transform.Core;
internal static class TransformMenu
{
    private static ManualLogSource _log;
    private static ConfigEntry<float> _scaleEntry;   // UI overall scale (1-2).
    private static bool _open;
    private static int _page;
    private static int _settingsFormIndex;
    private static int _selectedFormIndex = -1;   // Selected form card (-1 = none).
    private static bool _loggedGuiError;

    // 自管理双击检测。不依赖 Event.current.clickCount——Unity 的双击判定对两次
    // 点击间的鼠标位移极敏感（手抖一下 clickCount 就重置为 1），"双击"会退化成两次
    // 单击；这里按"同一卡片 + 0.35s 内 + 20px 屏幕距离内"自行判定，手感稳定。
    private static int _lastClickFormIndex = -1;
    private static float _lastClickTime;
    private static Vector2 _lastClickPos;
    private const float DoubleClickWindowSeconds = 0.35f;
    private const float DoubleClickMaxDistSqr = 400f;
    private static Rect _windowRect;

    private const float MaxMenuScale = 2f;

    // 缩放仍用下拉框；参数控制改为输入框。下拉点击会先拦截，避免误触形态卡片。
    private static readonly float[] ScaleSteps =
    {
        1f, 1.1f, 1.2f, 1.3f, 1.4f, 1.5f, 1.6f, 1.7f, 1.8f, 1.9f, 2f
    };
    private static bool _scaleDropdownOpen;
    private static Rect _scaleDropdownHeadRect;
    private static Rect _scaleDropdownListRect;
    private static readonly Dictionary<string, string> FloatInputDrafts = new Dictionary<string, string>();
    private static Vector2 _formsScroll;
    private static Vector2 _detailsScroll, _settingsScroll, _settingsListScroll;
    private static float _settingsContentWidth;
    private static bool _hasWindowPosition;
    private static bool _dragPendingWindow;
    private static bool _draggingWindow;
    private static Vector2 _dragMouseStart;
    private static Rect _dragRectStart;
    private static bool _manualWindowRectOverride;
    private static Rect _manualWindowRect;
    private static readonly List<Rect> _nonDragRects = new List<Rect>();

    // 每次打开菜单时递增：全新的窗口控件 ID 意味着全新的 IMGUI 布局组，避免复用上次
    // 会话因异常/中止遗留的损坏布局状态（IMGUI 按窗口 ID 复用布局组）。
    private static int _windowId = nameof(TransformMenu).GetHashCode();

    // 布局常量（标题条 / 控件高度统一取值）
    private const float TitleBarHeight = 60f;
    private const float ToolbarHeight = 46f;

    private static readonly Color ColWindowBg = Hex(0x171F22);
    private static readonly Color ColTitleStrip = Hex(0x101719);
    private static readonly Color ColBorder = Hex(0x3B4A4E);
    private static readonly Color ColDivider = Hex(0x2D3A3E);
    private static readonly Color ColTextPrimary = Hex(0xF2EFE5);
    private static readonly Color ColTextSecondary = Hex(0xB9C6C6);
    private static readonly Color ColTextMuted = Hex(0x94A6AA);
    private static readonly Color ColAmber = Hex(0xF4AE66);
    private static readonly Color ColGreen = Hex(0x9DCF9A);
    private static readonly Color ColBlue = Hex(0x9AC8D6);
    private static readonly Color ColRed = Hex(0xE7A091);
    private static readonly Color ColCardBg = Hex(0x222D31);
    private static readonly Color ColCardHoverBg = Hex(0x2D3D42);
    private static readonly Color ColCardActiveBg = Hex(0x293F35);
    private static readonly Color ColBtnBg = Hex(0x283438);
    private static readonly Color ColBtnHoverBg = Hex(0x3B4F55);
    private static readonly Color ColBtnHoverText = Hex(0x101719);
    private static readonly Color ColTitleText = Hex(0xF2EFE5);

    // 字体：只用游戏自带字体（中文 = NotoSansSC 游戏简体菜单字体；英文 = NotInter 游戏
    // 主 UI 字体）；_menuFontFound 标记是否已命中，未命中前每次打开菜单都会重试。
    private static Font _font;
    private static Font _appliedSkinFont;
    private static Font _cachedGameMenuFont;
    private static bool? _cachedFontChineseMode;
    private static bool _fontIsGameFont;
    private static bool _menuFontFound;
    private static bool _fontRetryPending;
    private static bool _loggedMenuFont;
    private static bool _loggedFontScanMiss;

    // 圆角贴图（BuildStyles 中生成）
    private static Texture2D _windowTex;
    private static Texture2D _btnNormalTex, _btnHoverTex, _btnActiveTex;
    private static Texture2D _tabActiveTex;
    private static Texture2D _cardNormalTex, _cardHoverTex, _cardActiveTex, _cardRestrictedTex, _cardSelectedTex;
    private static Texture2D _btnPrimaryTex;             // 主操作暖橙实底

    // 样式（OnGUI 首帧构建；字体升级时重建）
    private static bool _stylesBuilt;
    private static bool _texturesBuilt;
    private static GUIStyle _windowStyle;
    private static GUIStyle _titleStyle;
    private static GUIStyle _closeStyle;
    private static GUIStyle _tabActiveStyle, _tabIdleStyle;
    private static GUIStyle _cardBoxStyle;
    private static GUIStyle _cardNameStyle;
    private static GUIStyle _statusActiveStyle;
    private static GUIStyle _statusRestrictedStyle;
    private static GUIStyle _settingsTitleStyle;
    private static GUIStyle _settingsDescStyle;
    private static GUIStyle _settingsSectionHeaderStyle;
    private static GUIStyle _settingsValueLabelStyle;
    /// <summary>每帧 CalcSize/CalcHeight 的共享 GUIContent（使用前必须赋 text），避免 OnGUI 每帧分配。</summary>
    private static readonly GUIContent _scratchContent = new GUIContent();
    private static GUIStyle _settingsInputStyle;
    private static GUIStyle _hintStyle;
    private static GUIStyle _detailNameStyle, _formSwitchOnStyle, _formSwitchOffStyle, _formSwitchTextStyle, _cardCautionStyle;
    private static Texture2D _switchOnTex, _switchOffTex;
    private static GUIStyle _policyNoticeStyle;
    private static GUIStyle _inputStyle;
    private static GUIStyle _btnStyle;            // 中性按钮
    private static GUIStyle _btnPrimaryStyle;     // 主操作暖橙色
    private static GUIStyle _policyLockedGreen, _policyLockedAmber, _policyLockedRed; // 客机只读态（文字调暗、无悬停）
    private static GUIStyle _listItemStyle;          // 参数页左列形态列表项（未选中）
    private static GUIStyle _listItemActiveStyle;    // 参数页左列形态列表项（选中，琥珀高亮）
    private static GUIStyle _invisibleButtonStyle;

    internal static bool IsOpen => _open;

    /// <summary>返回形态页选中的形态引用（null = 无选中）。</summary>
    internal static FormRegistry.FormDescriptor GetSelectedForm()
    {
        var forms = FormRegistry.Forms;
        if (_selectedFormIndex < 0 || _selectedFormIndex >= forms.Count) return null;
        return forms[_selectedFormIndex];
    }

    /// <summary>清除形态页选中与双击状态（场景卸载/回主菜单时调用），防止重进大厅后按 F 误触变身。</summary>
    internal static void ClearSelection()
    {
        _selectedFormIndex = -1;
        _lastClickFormIndex = -1;
        _lastClickTime = 0f;
    }

    internal static void Initialize(ManualLogSource log, ConfigEntry<float> scaleEntry = null)
    {
        _log = log;
        _scaleEntry = scaleEntry;
    }

    /// <summary>Opens or closes the menu. Input gating is handled by the Harmony getter postfixes
    /// in Core.TransformHarmonyPatches, which read this MenuOpen flag.</summary>
    internal static void SetOpen(bool open)
    {
        if (_open == open) return;
        _open = open;
        TransformState.MenuOpen = open;
        if (open)
        {
            _page = 0;
            // 保持上次选中（-1 = 从未选过）。双击变身后菜单关闭，重开仍指向同一
            // 形态——"选中即预览目标"的心智不变，不再每次打开都清空选择。
            _lastClickFormIndex = -1; // 双击窗口重置：重开后的首次点击不会误判为双击。
            _windowId++;             // 全新布局组，规避上次会话遗留的损坏布局
            _loggedGuiError = false; // 每次打开允许记录一次新的渲染错误
            _dragPendingWindow = false;
            _draggingWindow = false;
            _scaleDropdownOpen = false;
            // 游戏字体可能随场景加载较晚：每次打开菜单时若未命中游戏字体则重试。
            if (!_menuFontFound) _fontRetryPending = true;
        }
    }

    internal static void OnGUI()
    {
        if (!_open) return;
        try
        {
            Draw();
        }
        catch (ExitGUIException)
        {
            // GUIUtility.ExitGUI 的干净中止必须向上传播给 Unity：吞掉它会导致 IMGUI
            // 布局状态损坏（"Getting control 0's position ... when doing repaint" 连锁错误）。
            throw;
        }
        catch (Exception ex)
        {
            // IMGUI runs every frame — log a rendering failure once instead of spamming the log.
            if (!_loggedGuiError)
            {
                _loggedGuiError = true;
                _log?.LogError("[Transform] Menu rendering error (logged once):\n" + ex);
            }
        }
    }

    private static void Draw()
    {
        // 先建样式再开窗：窗口需要非空样式；旧实现首帧样式为 null，
        // 字体重试也可能在窗口创建后才换样式，导致同帧前后样式不一致。
        BuildStyles();

    // 强制从 identity 矩阵开始——双击变身/恢复时 GUIUtility.ExitGUI() 会中断本帧，
    // 使末尾的 GUI.matrix 恢复代码不执行，残留缩放矩阵导致下次打开菜单放大/闪烁。
    GUI.matrix = Matrix4x4.identity;

    // 遮罩位于缩放矩阵外，覆盖物理全屏。
    DrawRect(new Rect(0f, 0f, Screen.width, Screen.height), new Color(0.02f, 0.04f, 0.05f, 0.66f));

    float scale = CurrentScale();
    if (Mathf.Abs(scale - 1f) > 0.001f)
    {
        Vector2 pivot = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        GUI.matrix = Matrix4x4.TRS(pivot, Quaternion.identity, Vector3.one * scale)
                   * Matrix4x4.TRS(-pivot, Quaternion.identity, Vector3.one);
    }

    Vector2 windowSize = TransformMenuLayout.WindowSize(Screen.width, Screen.height, scale);
    float width = windowSize.x;
    float height = windowSize.y;
    // 首次打开居中；之后保留用户拖动后的位置。
    Rect windowRect;
    if (_hasWindowPosition && _windowRect.width > 0f && _windowRect.height > 0f)
    {
        windowRect = new Rect(_windowRect.x, _windowRect.y, width, height);
    }
    else
    {
        windowRect = new Rect(
            Screen.width * 0.5f - width * 0.5f,
            Screen.height * 0.5f - height * 0.5f,
            width, height);
    }
    windowRect = ClampWindowRect(windowRect, scale);
    _windowRect = windowRect;

    // 窗口外点击自动关闭：Event.mousePosition 在当前 GUI.matrix 下已经是逻辑坐标，
    // 再做 ScreenToGUIPoint 会在高缩放时错位，导致外点击关闭失效。
    Event outsideClick = Event.current;
    if (outsideClick != null && outsideClick.type == EventType.MouseDown && outsideClick.button == 0
        && !_windowRect.Contains(outsideClick.mousePosition))
    {
        SetOpen(false);
        GUIUtility.ExitGUI(); // 立刻吃掉事件，防止穿透到游戏 UI（拾取/互动等）
    }

    // Use GUI.Window with a fixed rect. GUILayout.Window still participates in auto-layout even
    // with explicit width/height options; GUI.Window behaves closer to the reference UGUI panel:
    // the window rectangle is owned by us and does not get re-estimated on click events.
    _manualWindowRectOverride = false;
    Rect drawnRect = GUI.Window(_windowId, windowRect, DrawWindow, GUIContent.none, _windowStyle);
    Rect nextRect = _manualWindowRectOverride
        ? _manualWindowRect
        : new Rect(drawnRect.x, drawnRect.y, windowRect.width, windowRect.height);
    _windowRect = ClampWindowRect(nextRect, scale);
    _hasWindowPosition = true;

    GUI.matrix = Matrix4x4.identity;
    }

    private static void DrawWindow(int windowId)
    {
        BuildStyles();
        ApplyMenuFontToSkin();
        _nonDragRects.Clear();
        float w = _windowRect.width;
        DrawRect(new Rect(0, 0, w, TitleBarHeight), ColTitleStrip);
        DrawRect(new Rect(18, TitleBarHeight - 3, 34, 3), ColAmber);
        GUI.Label(new Rect(20, 12, 160, 34), Localization.Tr("变身", "Transform"), _titleStyle);

        float tabWidth = Localization.IsChinese ? 94f : 108f;
        Rect formsTab = new Rect(182, 14, tabWidth, 32);
        Rect settingsTab = new Rect(formsTab.xMax + 6, 14, tabWidth, 32);
        RecordNonDragRect(formsTab); RecordNonDragRect(settingsTab);
        if (GUI.Button(formsTab, Localization.Tr("选择形态", "Forms"), _page == 0 ? _tabActiveStyle : _tabIdleStyle) && _page != 0)
        { _page = 0; GUIUtility.ExitGUI(); }
        if (GUI.Button(settingsTab, Localization.Tr("形态设置", "Settings"), _page == 1 ? _tabActiveStyle : _tabIdleStyle) && _page != 1)
        {
            var selected = GetSelectedForm();
            if (selected != null) _settingsFormIndex = _selectedFormIndex;
            _page = 1; GUIUtility.ExitGUI();
        }

        Rect close = new Rect(w - 52, 14, 32, 32);
        RecordNonDragRect(close);
        if (GUI.Button(close, "×", _closeStyle)) { SetOpen(false); GUIUtility.ExitGUI(); }

        float toolbarY = TitleBarHeight + 7;
        Rect policyRect = new Rect(18, toolbarY, Mathf.Min(250, w * 0.42f), 32);
        var plugin = TransformPlugin.Instance;
        bool hasHints = plugin?.ShowControlHints != null;
        _scratchContent.text = Localization.Tr("提示：关", "Hints: Off");
        float hintsWidth = Mathf.Max(90, _tabIdleStyle.CalcSize(_scratchContent).x + 12);
        string scaleLabel = Localization.Tr("面板缩放 ", "Scale ") + FormatScale(CurrentScale()) + "× ▾";
        _scratchContent.text = scaleLabel;
        float scaleWidth = Mathf.Max(134, _tabIdleStyle.CalcSize(_scratchContent).x + 4);
        float totalWidth = (_scaleEntry != null ? scaleWidth : 0) + (hasHints ? hintsWidth : 0)
            + (_scaleEntry != null && hasHints ? 8 : 0);
        bool showNotice = RoomPolicy.IsClientInOnlineRoom || !RoomPolicy.CanTransformNow;
        Rect notice = TransformMenuLayout.ToolbarNotice(w, policyRect.width, totalWidth, out float extraToolbarHeight);
        if (!showNotice) extraToolbarHeight = 0f;
        float toolbarHeight = ToolbarHeight + extraToolbarHeight;
        DrawRect(new Rect(0, TitleBarHeight, w, toolbarHeight), Hex(0x1C272B));
        DrawRect(new Rect(18, TitleBarHeight + toolbarHeight, w - 36, 1), ColDivider);
        RecordNonDragRect(policyRect);
        GUIStyle policyStyle = RoomPolicy.CanManageRoomPolicy ? _btnStyle : LockedPolicyStyle(RoomPolicy.Current);
        if (GUI.Button(policyRect, PolicyControlLabel(RoomPolicy.Current), policyStyle) && RoomPolicy.CanManageRoomPolicy)
        { RoomPolicy.CycleFromMenu(); GUIUtility.ExitGUI(); }
        if (showNotice)
            GUI.Label(notice, !RoomPolicy.CanTransformNow ? PolicyBlockedReason() : HostModStatusText(), _policyNoticeStyle);
        float controlX = w - 18 - totalWidth;
        _scaleDropdownHeadRect = Rect.zero;
        if (_scaleEntry != null)
        {
            _scaleDropdownHeadRect = new Rect(controlX, toolbarY, scaleWidth, 32);
            RecordNonDragRect(_scaleDropdownHeadRect);
            if (GUI.Button(_scaleDropdownHeadRect, scaleLabel, _tabIdleStyle))
            { _scaleDropdownOpen = !_scaleDropdownOpen; GUIUtility.ExitGUI(); }
            controlX += scaleWidth + 8;
        }
        if (hasHints)
        {
            Rect hints = new Rect(controlX, toolbarY, hintsWidth, 32);
            RecordNonDragRect(hints);
            bool show = plugin.ShowControlHints.Value;
            if (GUI.Button(hints, show ? Localization.Tr("提示：开", "Hints: On") : Localization.Tr("提示：关", "Hints: Off"), _tabIdleStyle))
            { plugin.ShowControlHints.Value = !show; _scaleDropdownOpen = false; GUIUtility.ExitGUI(); }
        }
        HandleScaleDropdownInput();

        Rect content = new Rect(18, TitleBarHeight + toolbarHeight + 16, w - 36,
            _windowRect.height - TitleBarHeight - toolbarHeight - 78);
        if (_page == 0) DrawFormsPage(content);
        else
        {
            RecordNonDragRect(content);
            GUILayout.BeginArea(content);
            DrawSettingsPage(content);
            GUILayout.EndArea();
        }

        float footerY = _windowRect.height - 50;
        DrawRect(new Rect(18, footerY - 8, w - 36, 1), ColDivider);
        GUI.Label(new Rect(20, footerY, w - 190, 38), HintText(), _hintStyle);
        Rect restore = new Rect(w - 162, footerY, 144, 36);
        RecordNonDragRect(restore);
        bool guiEnabled = GUI.enabled;
        GUI.enabled = guiEnabled && FormRegistry.ActiveForm != null;
        if (GUI.Button(restore, Localization.Tr("恢复原形", "Restore player"), _btnStyle))
        {
            SetOpen(false); TransformPlugin.Instance?.RequestRestore(); GUIUtility.ExitGUI();
        }
        GUI.enabled = guiEnabled;
        DrawScaleDropdown();
        HandleWindowDrag();
    }

    private static void DrawFormsPage(Rect area)
    {
        var layout = new TransformMenuLayout(area);
        var forms = FormRegistry.Forms;
        RecordNonDragRect(layout.Grid);
        Rect view = new Rect(0, 0, layout.Grid.width - 20, Mathf.Max(layout.Grid.height, layout.ViewHeight(forms.Count)));
        _formsScroll = GUI.BeginScrollView(layout.Grid, _formsScroll, view, false, true);
        for (int i = 0; i < forms.Count; i++) DrawFormCard(forms[i], i, layout.Card(i));
        GUI.EndScrollView();
        DrawSelectionPanel(layout.Details, layout.Stacked);
    }

    private static void DrawSelectionPanel(Rect area, bool stacked)
    {
        var form = GetSelectedForm();
        bool active = form != null && IsActiveForm(form);
        bool canEnter = form != null && RoomPolicy.CanTransformNow && RoomPolicy.IsFormAllowed(form.Id) && SafeCanEnter(form);
        string reason = null;
        if (form != null && !active && !canEnter)
            reason = !RoomPolicy.CanTransformNow ? PolicyBlockedReason()
                : !RoomPolicy.IsFormAllowed(form.Id) ? Localization.Tr("此形态已被房间禁用。", "This form is disabled by the room.")
                : form.UnavailableReason?.Invoke() ?? Localization.Tr("当前状态无法变身。", "You cannot transform in the current state.");
        _cardBoxStyle.normal.background = _cardNormalTex;
        GUI.Box(area, GUIContent.none, _cardBoxStyle);
        RecordNonDragRect(area);
        if (form == null)
        {
            GUI.Label(new Rect(area.x + 18, area.y + 18, area.width - 36, 30),
                Localization.Tr("准备出发", "Ready to explore"), _detailNameStyle);
            return;
        }
        float left = area.x + 16;
        float width = area.width - 32;
        // Compact screens keep the scrollable form information beside the actions.
        float actionWidth = stacked ? Mathf.Min(210, width * 0.40f) : width;
        float textWidth = stacked ? width - actionWidth - 18 : width;
        GUI.Label(new Rect(left, area.y + 16, textWidth, 52), form.Name, _detailNameStyle);
        float descTop = area.y + 74;
        float actionX = stacked ? area.xMax - actionWidth - 16 : left;
        float actionY = stacked ? area.y + 16 : area.yMax - 98;
        Rect description = new Rect(left, descTop, textWidth,
            Mathf.Max(32, (stacked ? area.yMax - 12 : actionY - 14) - descTop));
        string info = FormInformation(form).Replace(" · ", "\n");
        if (reason != null) info = reason + "\n\n" + info;
        _scratchContent.text = info;
        float infoWidth = description.width - 18;
        float infoHeight = _settingsDescStyle.CalcHeight(_scratchContent, infoWidth) + 8;
        _detailsScroll = GUI.BeginScrollView(description, _detailsScroll,
            new Rect(0, 0, infoWidth, Mathf.Max(description.height, infoHeight)), false, false);
        GUI.Label(new Rect(0, 0, infoWidth, infoHeight), info, _settingsDescStyle);
        GUI.EndScrollView();

        Rect action = new Rect(actionX, actionY, actionWidth, 42);
        bool previousEnabled = GUI.enabled;
        GUI.enabled = previousEnabled && (active || canEnter);
        string actionText = active ? Localization.Tr("恢复原形", "Restore player")
            : !canEnter ? Localization.Tr("暂不可用", "Unavailable")
            : FormRegistry.ActiveForm != null ? Localization.Tr("切换形态", "Switch form") : Localization.Tr("变身", "Transform");
        if (GUI.Button(action, actionText, _btnPrimaryStyle))
        {
            SetOpen(false);
            if (active) TransformPlugin.Instance?.RequestRestore();
            else TransformPlugin.Instance?.RequestEnterForm(form.Id);
            GUIUtility.ExitGUI();
        }
        GUI.enabled = previousEnabled;
        Rect settings = new Rect(actionX, action.yMax + 8, actionWidth, 32);
        if (GUI.Button(settings, Localization.Tr("调整此形态", "Form settings"), _btnStyle))
        { _settingsFormIndex = _selectedFormIndex; _settingsScroll = Vector2.zero; _page = 1; GUIUtility.ExitGUI(); }
    }

    private static string PolicyLabel(RoomPolicy.Policy policy)
    {
        switch (policy)
        {
            case RoomPolicy.Policy.LobbyOnly:
                return Localization.Tr("仅在大厅可用（机场）", "Lobby only (Airport)");
            case RoomPolicy.Policy.Disabled:
                return Localization.Tr("禁用变身模组", "Transform mod disabled");
            default:
                return Localization.Tr("允许所有场景使用", "Allowed in all scenes");
        }
    }

    private static string PolicyControlLabel(RoomPolicy.Policy policy)
    {
        if (!RoomPolicy.LocalOnlyPolicyMode) return PolicyLabel(policy);
        switch (policy)
        {
            case RoomPolicy.Policy.LobbyOnly:
                return Localization.Tr("本地：仅大厅", "Local: lobby only");
            case RoomPolicy.Policy.Disabled:
                return Localization.Tr("本地：禁用变身", "Local: disabled");
            default:
                return Localization.Tr("本地：允许变身", "Local: allowed");
        }
    }

    private static string HostModStatusText()
    {
        return RoomPolicy.MasterHasTransformMod
            ? Localization.Tr("主机已安装模组：房间状态由主机接管", "Host has the mod: room policy is controlled by the host")
            : Localization.Tr("主机未安装模组：房间状态仅本地生效", "Host does not have the mod: policy changes are local only");
    }

    private static string PolicyBlockedReason()
    {
        return RoomPolicy.Current == RoomPolicy.Policy.Disabled
            ? Localization.Tr("房间限制：本房间已禁用变身", "Room policy: transforms are disabled in this room")
            : Localization.Tr("房间限制：变身仅在大厅（机场）可用", "Room policy: transforms only in the lobby (Airport)");
    }

    /// <summary>客机的只读策略按钮：保留语义色相但调暗文字、悬停无变化，清晰表达“看得到、改不了”。</summary>
    private static GUIStyle LockedPolicyStyle(RoomPolicy.Policy policy)
    {
        switch (policy)
        {
            case RoomPolicy.Policy.LobbyOnly: return _policyLockedAmber;
            case RoomPolicy.Policy.Disabled:   return _policyLockedRed;
            default:                           return _policyLockedGreen;
        }
    }

    private static void DrawFormCard(FormRegistry.FormDescriptor form, int formIndex, Rect card)
    {
        bool active = IsActiveForm(form);
        bool formAllowed = RoomPolicy.IsFormAllowed(form.Id);
        bool allowed = RoomPolicy.CanTransformNow && formAllowed;
        bool canEnter = allowed && SafeCanEnter(form);
        bool selected = _selectedFormIndex == formIndex;
        bool hover = card.Contains(Event.current.mousePosition);
        _cardBoxStyle.normal.background = selected ? _cardSelectedTex : active ? _cardActiveTex
            : !allowed ? _cardRestrictedTex : hover ? _cardHoverTex : _cardNormalTex;
        GUI.Box(card, GUIContent.none, _cardBoxStyle);
        Color accent = active ? ColGreen : selected ? ColAmber : !allowed ? ColTextMuted : ColBlue;
        DrawRect(new Rect(card.x + 1, card.y + 14, 3, card.height - 28), accent);
        Rect name = TransformMenuLayout.FormName(card);
        GUI.Label(name, form.Name, _cardNameStyle);
        string caution = CardCaution(form.Id);
        if (caution.Length > 0)
            GUI.Label(TransformMenuLayout.FormCaution(card), new GUIContent(caution, FormInformation(form)), _cardCautionStyle);
        string state = active ? Localization.Tr("使用中", "ACTIVE") : !allowed ? Localization.Tr("房间限制", "ROOM RESTRICTED")
            : !canEnter ? Localization.Tr("暂不可用", "UNAVAILABLE") : "";
        Rect toggle = TransformMenuLayout.FormSwitch(card);
        if (state.Length > 0)
            GUI.Label(new Rect(card.x + 14, card.yMax - 30, card.width - 28, 18), state,
                active ? _statusActiveStyle : _statusRestrictedStyle);

        DrawFormSwitch(toggle, form.Id, formAllowed);
        // Two stable click regions leave the policy button completely outside
        // the selection/double-click target, including its disabled guest state.
        bool clicked = GUI.Button(new Rect(card.x, card.y, toggle.x - card.x - 4, card.height),
            GUIContent.none, _invisibleButtonStyle);
        clicked |= GUI.Button(new Rect(toggle.x - 4, toggle.yMax + 4, card.xMax - toggle.x + 4, card.yMax - toggle.yMax - 4),
            GUIContent.none, _invisibleButtonStyle);
        if (!clicked) return;
        bool doubleClick = formIndex == _lastClickFormIndex && Time.unscaledTime - _lastClickTime <= DoubleClickWindowSeconds
            && (Event.current.mousePosition - _lastClickPos).sqrMagnitude <= DoubleClickMaxDistSqr;
        _lastClickFormIndex = formIndex; _lastClickTime = Time.unscaledTime; _lastClickPos = Event.current.mousePosition;
        if (_selectedFormIndex != formIndex) _detailsScroll = Vector2.zero;
        _selectedFormIndex = formIndex;
        if (doubleClick && (active || canEnter))
        {
            SetOpen(false);
            if (active) TransformPlugin.Instance?.RequestRestore();
            else TransformPlugin.Instance?.RequestEnterForm(form.Id);
        }
        GUIUtility.ExitGUI();
    }

    private static void DrawFormSwitch(Rect rect, FormId formId, bool enabled)
    {
        bool canManage = RoomPolicy.CanManageRoomPolicy;
        bool interactive = GUI.enabled && canManage;
        Color previousColor = GUI.color;
        try
        {
            if (!interactive) GUI.color = new Color(previousColor.r, previousColor.g, previousColor.b, previousColor.a * 0.6f);
            _formSwitchTextStyle.normal.textColor = enabled ? ColGreen : ColTextMuted;
            GUI.Label(new Rect(rect.x, rect.y, 34f, rect.height),
                enabled ? Localization.Tr("启用", "On") : Localization.Tr("禁用", "Off"), _formSwitchTextStyle);
            Rect track = new Rect(rect.xMax - 46f, rect.y + 2f, 46f, 22f);
            GUI.Box(track, GUIContent.none, enabled ? _formSwitchOnStyle : _formSwitchOffStyle);
            // A solid inset thumb avoids the old small-texture stretching artifacts.
            Color thumbColor = enabled ? ColGreen : ColTextMuted;
            if (!interactive) thumbColor.a *= 0.6f;
            DrawRect(new Rect(enabled ? track.xMax - 19f : track.x + 3f, track.y + 4f, 16f, 14f), thumbColor);
        }
        finally { GUI.color = previousColor; }
        bool previousEnabled = GUI.enabled;
        GUI.enabled = interactive;
        string tooltip = canManage
            ? enabled ? Localization.Tr("点击禁用此形态", "Disable this form") : Localization.Tr("点击启用此形态", "Enable this form")
            : Localization.Tr("由房主管理", "Controlled by the host");
        bool clicked = GUI.Button(rect, new GUIContent("", tooltip), _invisibleButtonStyle);
        GUI.enabled = previousEnabled;
        if (!clicked) return;
        _lastClickFormIndex = -1;
        RoomPolicy.ToggleFormAllowedFromMenu(formId);
        GUIUtility.ExitGUI();
    }

    private static string FormInformation(FormRegistry.FormDescriptor form)
    {
        string info = form.Description;
        if (HasPickupWarning(form.Id))
            info += "\n\n" + Localization.Tr(
                "注意：主机未安装该模组时，变身对象可能被捡起并复制，请慎用。",
                "Warning: if the host does not have this mod, the transformed object may be picked up and duplicated. Use with care.");
        return info;
    }

    private static bool HasPickupWarning(FormId id) => id == FormId.Frog || id == FormId.Scorpion
        || id == FormId.Coconut || id == FormId.Bomb;

    private static string CardCaution(FormId id)
    {
        if (HasPickupWarning(id))
            return Localization.Tr("注意：无模组主机下拾取可能复制", "No-mod host: pickup may duplicate");
        switch (id)
        {
            case FormId.Crow:
                return Localization.Tr("注意：渡鸦外观需对方更新模组", "Warning: Raven needs updated mod");
            case FormId.Raven:
                return Localization.Tr("注意：秃鹫外观需对方安装模组", "Warning: bird requires the mod");
            case FormId.Statue:
                return Localization.Tr("注意：可被其他玩家撞碎", "Warning: players can shatter");
            case FormId.Tornado:
                return Localization.Tr("注意：会卷起并推开附近玩家", "Warning: pushes nearby players");
            default:
                return "";
        }
    }

    private static void DrawSettingsPage(Rect area)
    {
        var forms = FormRegistry.Forms;
        if (_settingsFormIndex < 0 || _settingsFormIndex >= forms.Count) _settingsFormIndex = 0;
        float listWidth = Mathf.Clamp(area.width * 0.24f, 164, 244);
        GUILayout.BeginHorizontal();
        GUILayout.BeginVertical(GUILayout.Width(listWidth));
        DrawSettingsTitleHeader(Localization.Tr("形态列表", "Forms"));
        _settingsListScroll = GUILayout.BeginScrollView(_settingsListScroll, false, false,
            GUILayout.Height(Mathf.Max(60, area.height - 40)));
        for (int i = 0; i < forms.Count; i++)
        {
            if (GUILayout.Button(forms[i].Name, i == _settingsFormIndex ? _listItemActiveStyle : _listItemStyle, GUILayout.Height(36)))
            { _settingsFormIndex = i; _settingsScroll = Vector2.zero; GUIUtility.ExitGUI(); }
        }
        GUILayout.EndScrollView();
        GUILayout.EndVertical();
        GUILayout.Space(20);
        GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
        _settingsContentWidth = Mathf.Max(200, area.width - listWidth - 52);
        _settingsScroll = GUILayout.BeginScrollView(_settingsScroll, false, false, GUILayout.Height(area.height));
        var form = forms[_settingsFormIndex];
        GUILayout.Label(form.Name, _detailNameStyle, GUILayout.Height(40));
        GUILayout.Label(RoomPolicy.SettingsControlledByHost
            ? Localization.Tr("参数由房主控制", "Settings are controlled by the host")
            : Localization.Tr("修改后立即生效", "Changes apply immediately"), _hintStyle, GUILayout.Height(24));
        GUILayout.Space(16);
        DrawCameraSettings(form);
        GUILayout.Space(20);
        DrawBehaviourSettings(form);
        GUILayout.Space(24);
        bool settingsEnabled = GUI.enabled;
        GUI.enabled = settingsEnabled && !RoomPolicy.SettingsControlledByHost;
        if (GUILayout.Button(Localization.Tr("恢复此形态默认设置", "Reset form to defaults"), _btnStyle, GUILayout.Height(36))) ResetFormDefaults(form);
        GUI.enabled = settingsEnabled;
        GUILayout.Space(12);
        GUILayout.EndScrollView();
        GUILayout.EndVertical();
        GUILayout.EndHorizontal();
    }

    private static void DrawCameraSettings(FormRegistry.FormDescriptor form)
    {
        DrawSettingsSectionHeader(Localization.Tr("第三人称相机（实时生效）", "Third-person camera (live)"));
        GUILayout.Space(12f);
        DrawFloatSlider(Localization.Tr("距离", "Distance"), SafeEntry(form.CameraDistance), form.DistanceMin, form.DistanceMax);
        DrawFloatSlider(Localization.Tr("高度", "Height"), SafeEntry(form.CameraHeight), form.HeightMin, form.HeightMax);
        DrawFloatSlider(Localization.Tr("视场角", "FOV"), SafeEntry(form.CameraFov), form.FovMin, form.FovMax);
    }

    private static void DrawBehaviourSettings(FormRegistry.FormDescriptor form)
    {
        DrawSettingsSectionHeader(Localization.Tr("行为控制（实时生效）", "Behaviour (live)"));
        GUILayout.Space(12f);
        if (form.Params.Count == 0)
        {
            GUILayout.Label(Localization.Tr("该形态暂无可调行为参数。", "No behaviour parameters for this form."),
                _settingsValueLabelStyle, GUILayout.Height(34f));
            return;
        }
        foreach (FormRegistry.ParamDescriptor param in form.Params)
        {
            DrawFloatSlider(param.Label, SafeEntry(param.Entry), param.Min, param.Max);
        }
    }

    /// <summary>
    /// Float input bound straight to the module's ConfigEntry — controllers read the entry every
    /// frame, so valid typed values apply instantly without slider drag event conflicts.
    /// </summary>
    private static void DrawFloatSlider(string label, ConfigEntry<float> entry, float min, float max)
    {
        if (entry == null) return;
        bool compact = _settingsContentWidth < 410;
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, _settingsValueLabelStyle, GUILayout.Width(compact ? Mathf.Max(100, _settingsContentWidth - 112) : 190), GUILayout.Height(36));
        DrawFloatInputField(FloatInputKey(entry), entry, min, max, _settingsInputStyle, GUILayout.Width(compact ? 84 : 110), GUILayout.Height(36));
        if (!compact)
        {
            GUILayout.Space(14);
            GUILayout.Label(min.ToString("0.##") + " – " + max.ToString("0.##"), _hintStyle, GUILayout.Height(36));
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        if (compact) GUILayout.Label(min.ToString("0.##") + " – " + max.ToString("0.##"), _hintStyle, GUILayout.Height(20));
        GUILayout.Space(8);
    }

    private static void ResetFormDefaults(FormRegistry.FormDescriptor form)
    {
        TryReset(SafeEntry(form.CameraDistance));
        TryReset(SafeEntry(form.CameraHeight));
        TryReset(SafeEntry(form.CameraFov));
        foreach (FormRegistry.ParamDescriptor param in form.Params) TryReset(SafeEntry(param.Entry));
        FloatInputDrafts.Clear();
    }

    private static void TryReset(ConfigEntry<float> entry)
    {
        if (entry != null) entry.Value = (float)entry.DefaultValue;
    }

    // ------------------------------------------------------------------
    // 绘制辅助
    // ------------------------------------------------------------------

    private static Color Hex(int rgb)
    {
        return new Color(
            ((rgb >> 16) & 0xFF) / 255f,
            ((rgb >> 8) & 0xFF) / 255f,
            (rgb & 0xFF) / 255f);
    }

    private static void DrawRect(Rect rect, Color color)
    {
        Color previous = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = previous;
    }

    private static float CurrentScale()
    {
        return TransformMenuLayout.FitScale(_scaleEntry?.Value ?? 1f, Screen.width, Screen.height);
    }

    private static string FormatScale(float scale)
    {
        float rounded = Mathf.Round(Mathf.Clamp(scale, 0.5f, MaxMenuScale) * 10f) / 10f;
        return Mathf.Abs(rounded - 1f) < 0.001f ? "1" : rounded.ToString("0.0");
    }

    private static float NearestScaleStep(float scale)
    {
        float best = ScaleSteps[0];
        float bestDelta = Mathf.Abs(scale - best);
        for (int i = 1; i < ScaleSteps.Length; i++)
        {
            float delta = Mathf.Abs(scale - ScaleSteps[i]);
            if (delta < bestDelta)
            {
                best = ScaleSteps[i];
                bestDelta = delta;
            }
        }
        return best;
    }

    private static Rect ClampWindowRect(Rect rect, float scale)
    {
        const float margin = 10f;
        scale = Mathf.Max(0.001f, scale);
        Vector2 pivot = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);

        float minX = pivot.x + (margin - pivot.x) / scale;
        float minY = pivot.y + (margin - pivot.y) / scale;
        float maxX = pivot.x + (Screen.width - margin - pivot.x) / scale - rect.width;
        float maxY = pivot.y + (Screen.height - margin - pivot.y) / scale - rect.height;

        if (maxX < minX) rect.x = Screen.width * 0.5f - rect.width * 0.5f;
        else rect.x = Mathf.Clamp(rect.x, minX, maxX);

        if (maxY < minY) rect.y = Screen.height * 0.5f - rect.height * 0.5f;
        else rect.y = Mathf.Clamp(rect.y, minY, maxY);

        return rect;
    }

    private static Vector2 MouseScreenPoint()
    {
        Vector3 p = Input.mousePosition;
        return new Vector2(p.x, Screen.height - p.y);
    }

    private static string FloatInputKey(ConfigEntry<float> entry)
    {
        if (entry == null || entry.Definition == null) return "float";
        return entry.Definition.Section + "." + entry.Definition.Key;
    }

    private static void DrawFloatInputField(string key, ConfigEntry<float> entry, float min, float max, params GUILayoutOption[] options)
    {
        DrawFloatInputField(key, entry, min, max, _inputStyle, options);
    }

    private static void DrawFloatInputField(string key, ConfigEntry<float> entry, float min, float max, GUIStyle style, params GUILayoutOption[] options)
    {
        if (entry == null) return;
        string controlName = "TransformFloatInput:" + key;
        bool lockedByHost = RoomPolicy.SettingsControlledByHost;
        GUI.SetNextControlName(controlName);
        bool focused = GUI.GetNameOfFocusedControl() == controlName;
        if (!FloatInputDrafts.TryGetValue(key, out string draft) || !focused || lockedByHost)
        {
            draft = Mathf.Clamp(entry.Value, min, max).ToString("0.##");
            FloatInputDrafts[key] = draft;
        }

        bool previousEnabled = GUI.enabled;
        GUI.enabled = previousEnabled && !lockedByHost;
        string next = GUILayout.TextField(draft, style ?? _inputStyle, options);
        GUI.enabled = previousEnabled;

        if (!lockedByHost && next != draft)
        {
            FloatInputDrafts[key] = next;
            if (float.TryParse(next, out float parsed))
            {
                entry.Value = Mathf.Clamp(parsed, min, max);
            }
        }
    }

    private static bool IsScaleDropdownArea(Vector2 localMouse)
    {
        return _scaleDropdownHeadRect.Contains(localMouse)
               || (_scaleDropdownOpen && _scaleDropdownListRect.Contains(localMouse));
    }

    private static void RecordNonDragRect(Rect rect)
    {
        if (rect.width <= 0f || rect.height <= 0f) return;
        _nonDragRects.Add(rect);
    }

    private static bool IsNonDragArea(Vector2 localMouse)
    {
        if (IsScaleDropdownArea(localMouse)) return true;
        for (int i = 0; i < _nonDragRects.Count; i++)
        {
            if (_nonDragRects[i].Contains(localMouse)) return true;
        }
        return false;
    }

    private static void HandleWindowDrag()
    {
        Event e = Event.current;
        if (e == null) return;

        if (e.type == EventType.MouseDown && e.button == 0)
        {
            if (!IsNonDragArea(e.mousePosition))
            {
                _dragPendingWindow = true;
                _draggingWindow = false;
                _dragMouseStart = MouseScreenPoint();
                _dragRectStart = _windowRect;
            }
        }
        else if (e.type == EventType.MouseDrag && e.button == 0 && (_dragPendingWindow || _draggingWindow))
        {
            Vector2 delta = (MouseScreenPoint() - _dragMouseStart) / CurrentScale();
            if (!_draggingWindow && delta.sqrMagnitude < 9f)
            {
                return;
            }

            _draggingWindow = true;
            Rect moved = _dragRectStart;
            moved.x += delta.x;
            moved.y += delta.y;
            _manualWindowRect = ClampWindowRect(moved, CurrentScale());
            _windowRect = _manualWindowRect;
            _manualWindowRectOverride = true;
            e.Use();
        }
        else if ((e.type == EventType.MouseUp && e.button == 0) || e.rawType == EventType.MouseUp)
        {
            _dragPendingWindow = false;
            _draggingWindow = false;
        }
    }

    private static void UpdateScaleDropdownListRect()
    {
        const float itemH = 24f;
        const float maxVisibleItems = 11f;
        _scaleDropdownListRect = new Rect(
            _scaleDropdownHeadRect.x,
            _scaleDropdownHeadRect.yMax + 4f,
            _scaleDropdownHeadRect.width,
            itemH * Mathf.Min(maxVisibleItems, ScaleSteps.Length));
    }

    private static void HandleScaleDropdownInput()
    {
        if (_scaleEntry == null || !_scaleDropdownOpen) return;
        UpdateScaleDropdownListRect();

        Event e = Event.current;
        if (e == null || e.type != EventType.MouseDown || e.button != 0) return;

        if (_scaleDropdownListRect.Contains(e.mousePosition))
        {
            int idx = Mathf.FloorToInt((e.mousePosition.y - _scaleDropdownListRect.y) / 24f);
            if (idx >= 0 && idx < ScaleSteps.Length)
            {
                _scaleEntry.Value = ScaleSteps[idx];
            }
            _scaleDropdownOpen = false;
            e.Use();
            GUIUtility.ExitGUI();
        }
        else if (!_scaleDropdownHeadRect.Contains(e.mousePosition))
        {
            _scaleDropdownOpen = false;
            e.Use(); // 防止关闭下拉的同一击落到形态卡片上。
        }
    }

    private static void DrawScaleDropdown()
    {
        if (_scaleEntry == null || !_scaleDropdownOpen) return;
        const float itemH = 24f;
        UpdateScaleDropdownListRect();
        if (Event.current != null
            && Event.current.type != EventType.Repaint
            && Event.current.type != EventType.Layout)
            return;

        GUI.Box(_scaleDropdownListRect, GUIContent.none, _windowStyle);
        float current = NearestScaleStep(_scaleEntry.Value);
        for (int i = 0; i < ScaleSteps.Length; i++)
        {
            float step = ScaleSteps[i];
            Rect item = new Rect(_scaleDropdownListRect.x, _scaleDropdownListRect.y + i * itemH,
                _scaleDropdownListRect.width, itemH);
            bool selected = Mathf.Abs(step - current) < 0.001f;
            GUI.Box(item, FormatScale(step) + "×", selected ? _tabActiveStyle : _tabIdleStyle);
        }
    }

    private static void DrawSettingsTitleHeader(string text)
    {
        Rect header = GUILayoutUtility.GetRect(0f, 34f, GUILayout.ExpandWidth(true));
        GUI.Label(header, text, _settingsTitleStyle);
    }

    private static void DrawSettingsSectionHeader(string text)
    {
        Rect header = GUILayoutUtility.GetRect(0f, 28f, GUILayout.ExpandWidth(true));
        GUI.Label(header, text, _settingsSectionHeaderStyle);
        Rect line = GUILayoutUtility.GetRect(1f, 2f, GUILayout.ExpandWidth(true));
        DrawRect(line, ColDivider);
        GUILayout.Space(8f);
    }

    /// <summary>
    /// 运行时生成带 1px 描边的抗锯齿圆角贴图（GUIStyle 九宫格 border 拉伸，任意尺寸保持圆角）。
    /// </summary>
    private static Texture2D MakeRoundTexture(int size, Color fill, Color outline, float radius, float outlineWidth)
    {
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        float c = (size - 1) * 0.5f;
        Color[] px = new Color[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = Mathf.Max(Mathf.Abs(x - c) - (c - radius), 0f);
                float dy = Mathf.Max(Mathf.Abs(y - c) - (c - radius), 0f);
                float dist = Mathf.Sqrt(dx * dx + dy * dy);

                Color col;
                if (dist > radius + 0.5f)
                {
                    col = Color.clear;
                }
                else if (dist > radius - 0.5f)
                {
                    // 外缘 AA：描边色到透明的过渡。
                    float t = Mathf.Clamp01(radius + 0.5f - dist);
                    col = Color.Lerp(Color.clear, outline, t);
                }
                else if (dist > radius - outlineWidth - 0.5f)
                {
                    // 描边到填充的过渡。
                    float t = Mathf.Clamp01(dist - (radius - outlineWidth - 0.5f));
                    col = Color.Lerp(fill, outline, t);
                }
                else
                {
                    col = fill;
                }
                px[y * size + x] = col;
            }
        }
        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }

    /// <summary>
    /// 按钮样式工厂：margin 4px（防控件紧贴），垂直 padding 4px（给固定 Height 下的 CJK 行高留足空间）。
    /// </summary>
    private static GUIStyle MakeButtonStyle(Texture2D normal, Texture2D hover, Texture2D active,
        Color text, int fontSize, FontStyle fontStyle)
    {
        GUIStyle s = new GUIStyle
        {
            border = new RectOffset(6, 6, 6, 6),
            padding = new RectOffset(14, 14, 4, 4),
            margin = new RectOffset(4, 4, 4, 4),
            alignment = TextAnchor.MiddleCenter,
            fontSize = fontSize,
            fontStyle = fontStyle,
            wordWrap = false,
        };
        s.normal.background = normal;    s.normal.textColor = text;
        s.hover.background = hover;      s.hover.textColor = text;
        s.active.background = active;    s.active.textColor = text;
        s.onNormal.background = normal;  s.onNormal.textColor = text;
        s.onHover.background = hover;    s.onHover.textColor = text;
        s.onActive.background = active;  s.onActive.textColor = text;
        return s;
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static bool IsActiveForm(FormRegistry.FormDescriptor form)
    {
        return FormRegistry.ActiveForm == form;
    }

    private static bool SafeCanEnter(FormRegistry.FormDescriptor form)
    {
        try
        {
            if (form.CanEnter()) return true;
            // 切换语义：当前有激活形态且目标不是它 → 允许发起进入。各模块 CanTransform 的
            // "another special form" 守卫（isZombie/isBot/isScoutmaster）会拦住入口，但
            // EnterFormRoutine 会先退出当前形态再进入目标——所以入口必须放行，否则
            // 双击/选中+F 的"从 A 切到 B"永远被拦截。
            FormRegistry.FormDescriptor active = FormRegistry.ActiveForm;
            return active != null && active != form;
        }
        catch { return false; }
    }

    private static ConfigEntry<float> SafeEntry(Func<ConfigEntry<float>> entry)
    {
        try { return entry?.Invoke(); }
        catch { return null; }
    }

    private static string HintText()
    {
        string key = TransformPlugin.Instance?.MenuKey?.Value.ToString() ?? "T";

        return Localization.Tr(
            "单击选择 · 双击变身 · 长按 [" + key + "] 开关面板 · 变身中短按恢复",
            "Click to select · double-click to transform · hold [" + key + "] for menu · tap while transformed to restore");
    }

    // ------------------------------------------------------------------
    // 字体（只用游戏自带字体：中文=NotoSansSC，英文=NotInter）
    // ------------------------------------------------------------------

    /// <summary>
    /// 保证菜单字体就绪：只用游戏自带字体。
    /// 中文 = NotoSansSC（游戏简体菜单字体，含拉丁字形，中英文均正常显示）；
    /// 英文 = NotInter（游戏主 UI 字体），未加载时回退其他游戏字体。
    /// 全部未命中时用 IMGUI 默认字体临时渲染，每次打开菜单都会重试。
    /// </summary>
    internal static Font HintFont { get { EnsureFont(); return _font; } }

    private static void EnsureFont()
    {
        RefreshFontCacheForLanguage();
        if (_menuFontFound && _font != null && IsFontUsableForMenu(_font)) return;

        Font gameFont = FindGameMenuFont();
        if (gameFont != null)
        {
            _font = gameFont;
            _fontIsGameFont = true;
            _menuFontFound = true;
            if (!_loggedMenuFont)
            {
                _loggedMenuFont = true;
                _log?.LogInfo("[Transform] 菜单使用游戏字体: " + gameFont.name
                    + "（" + (Localization.IsChinese ? "简体中文" : "英文") + "）");
            }
            return;
        }

        // 游戏字体尚未加载（如刚进游戏就开菜单）：用 IMGUI 默认字体临时渲染，
        // 每次打开菜单都会重试，直到命中游戏字体为止。
        if (_font == null || !IsFontUsableForMenu(_font))
        {
            _font = GUI.skin.font;
        }
    }

    /// <summary>
    /// 按当前语言查找游戏自带字体（传统 Font 资产，IMGUI 可直接使用）。
    /// 中文：FontFallbackSwapper 的简体菜单字体（主菜单场景常驻）→ 按名扫描已加载资产；
    /// 英文：NotInter（游戏主 UI 字体）→ NotoSansSC（游戏简体菜单字体，含拉丁字形）
    /// → LiberationSans（游戏 TMP 默认字体）。
    /// </summary>
    private static Font FindGameMenuFont()
    {
        if (_cachedGameMenuFont != null)
        {
            if (IsFontUsableForMenu(_cachedGameMenuFont)) return _cachedGameMenuFont;
            _cachedGameMenuFont = null; // 场景切换后可能被卸载，重新查找
        }

        if (Localization.IsChinese)
        {
            // 1) 游戏自己的中文字体切换器（挂在主菜单/大厅场景，场景切换后可能已销毁）。
            try
            {
                FontFallbackSwapper swapper = FontFallbackSwapper.instance;
                if (swapper != null && swapper.simplifiedChineseFont != null
                    && swapper.simplifiedChineseFont.sourceFontFile != null
                    && IsFontUsableForMenu(swapper.simplifiedChineseFont.sourceFontFile))
                {
                    return KeepGameFontAlive(swapper.simplifiedChineseFont.sourceFontFile);
                }
            }
            catch { /* instance 已销毁时走资产扫描 */ }
        }
        else
        {
            // 1) NotInter 在 resources.assets 中，先尝试常见 Resources 路径直载。
            Font notInter = TryLoadNotInterFont();
            if (notInter != null) return KeepGameFontAlive(notInter);
        }

        // 2) 按名扫描已加载的传统 Font 资产与 TMP 字体的源字体。
        //    中文：NotoSansSC（游戏简体菜单字体，游戏内场景亦常驻）；
        //    英文：NotInter → NotoSansSC → LiberationSans（均为游戏自带字体）。
        string[] priority = Localization.IsChinese
            ? new[] { "notosanssc", "noto sans sc", "notosanstc", "noto sans tc" }
            : new[] { "notinter", "notosanssc", "noto sans sc", "liberationsans", "liberation sans" };
        try
        {
            List<Font> candidates = new List<Font>();
            foreach (Font f in Resources.FindObjectsOfTypeAll<Font>())
            {
                if (f != null) candidates.Add(f);
            }
            foreach (TMPro.TMP_FontAsset tmp in Resources.FindObjectsOfTypeAll<TMPro.TMP_FontAsset>())
            {
                if (tmp != null && tmp.sourceFontFile != null) candidates.Add(tmp.sourceFontFile);
            }

            foreach (string want in priority)
            {
                foreach (Font f in candidates)
                {
                    if (!IsFontUsableForMenu(f)) continue;
                    if (!(f.name ?? "").ToLowerInvariant().Contains(want)) continue;
                    return KeepGameFontAlive(f);
                }
            }
        }
        catch { /* 游戏字体尚未加载时忽略，下次打开菜单重试 */ }

        if (!_loggedFontScanMiss)
        {
            _loggedFontScanMiss = true;
            _log?.LogInfo("[Transform] 暂未找到游戏字体，菜单临时使用 IMGUI 默认字体，稍后自动重试。");
        }
        return null;
    }

    /// <summary>NotInter 的 Font 资产位于 resources.assets，尝试常见 Resources 路径直载。</summary>
    private static Font TryLoadNotInterFont()
    {
        string[] paths = { "NotInter-Regular", "Fonts/NotInter-Regular", "UI/NotInter-Regular" };
        try
        {
            foreach (string path in paths)
            {
                Font f = Resources.Load<Font>(path);
                if (f != null && IsFontUsableForMenu(f)) return f;
            }
        }
        catch { /* 路径不存在时忽略 */ }
        return null;
    }

    /// <summary>缓存游戏字体并加 DontSave，防止场景切换时被 Unity 卸载。</summary>
    private static Font KeepGameFontAlive(Font font)
    {
        _cachedGameMenuFont = font;
        try { font.hideFlags |= HideFlags.DontSave; } catch { /* 仅影响卸载策略，失败可忽略 */ }
        return font;
    }

    /// <summary>字体是否已请求到字形（排除尚未初始化/被销毁的空壳资产）。</summary>
    private static bool HasGlyphs(Font f)
    {
        try
        {
            f.RequestCharactersInTexture("Ag零", 14);
            return f.GetCharacterInfo('A', out _, 14);
        }
        catch { return false; }
    }

    private static bool IsFontUsableForMenu(Font f)
    {
        if (f == null || !HasGlyphs(f)) return false;

        if (Localization.IsChinese)
        {
            string name = (f.name ?? "").ToLowerInvariant();
            if (name.Contains("daruma") || name.Contains("monoflow"))
            {
                return false;
            }
        }

        return true;
    }

    private static void RefreshFontCacheForLanguage()
    {
        bool chinese = Localization.IsChinese;
        if (_cachedFontChineseMode.HasValue && _cachedFontChineseMode.Value == chinese) return;

        if (_cachedFontChineseMode.HasValue)
        {
            // 语言切换：清空字体缓存，按新语言重新查找游戏字体。
            _font = null;
            _appliedSkinFont = null;
            _cachedGameMenuFont = null;
            _fontIsGameFont = false;
            _menuFontFound = false;
            _fontRetryPending = true;
            _loggedMenuFont = false;
            _loggedFontScanMiss = false;
            _stylesBuilt = false;
        }

        _cachedFontChineseMode = chinese;
    }

    private static void ApplyMenuFontToSkin()
    {
        if (_font == null || GUI.skin == null) return;
        if (ReferenceEquals(_appliedSkinFont, _font)) return;

        try
        {
            GUI.skin.font = _font;
            GUI.skin.label.font = _font;
            GUI.skin.button.font = _font;
            GUI.skin.box.font = _font;
            GUI.skin.window.font = _font;
            GUI.skin.textField.font = _font;
            GUI.skin.textArea.font = _font;
            GUI.skin.toggle.font = _font;
            _appliedSkinFont = _font;
        }
        catch { /* Some skins may be partially immutable during scene transitions. */ }
    }

    // ------------------------------------------------------------------
    // 样式与贴图构建（OnGUI 首帧调用一次）
    // ------------------------------------------------------------------

    private static void BuildStyles()
    {
        RefreshFontCacheForLanguage();
        // 字体重试：菜单重新打开时若游戏字体已加载，则升级字体并重建全部样式。
        if (_fontRetryPending && !_menuFontFound)
        {
            _fontRetryPending = false;
            Font previous = _font;
            EnsureFont();
            if (_font != previous && _fontIsGameFont)
            {
                _stylesBuilt = false; // 用游戏字体重建样式（贴图不受字体影响，不重建）
            }
        }
        if (_stylesBuilt) return;
        _stylesBuilt = true;

        EnsureFont();

        if (!_texturesBuilt)
        {
            _texturesBuilt = true;
            try
            {
                BuildTextures();
            }
            catch (Exception ex)
            {
                _log?.LogWarning("[Transform] Menu texture build failed, falling back to plain styles: " + ex.Message);
            }
        }

        // ---- 窗口 ----
        _windowStyle = new GUIStyle
        {
            normal = { background = _windowTex, textColor = ColTextPrimary },
            onNormal = { background = _windowTex, textColor = ColTextPrimary },
            border = new RectOffset(12, 12, 12, 12),
            padding = new RectOffset(16, 16, 56, 12),
        };

        _titleStyle = new GUIStyle { fontSize = 25, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
        _titleStyle.normal.textColor = ColTitleText;   // 深色标题条配浅色文字
        _titleStyle.font = _font;

        // 关闭按钮：小尺寸独立样式，padding 0 保证 "×" 完整显示。
        _closeStyle = new GUIStyle
        {
            border = new RectOffset(6, 6, 6, 6),
            padding = new RectOffset(0, 0, 0, 0),
            margin = new RectOffset(0, 0, 0, 0),
            alignment = TextAnchor.MiddleCenter,
            fontSize = 18,
            fontStyle = FontStyle.Bold,
        };
        _closeStyle.normal.background = _btnNormalTex;  _closeStyle.normal.textColor = ColTextSecondary;
        _closeStyle.hover.background = _btnHoverTex;    _closeStyle.hover.textColor = ColRed;
        _closeStyle.active.background = _btnActiveTex;  _closeStyle.active.textColor = ColRed;
        _closeStyle.font = _font;

        // ---- 页签：选中项暖橙底深色字，普通项深色底浅色字 ----
        _tabActiveStyle = MakeButtonStyle(_tabActiveTex, _tabActiveTex, _tabActiveTex, ColBtnHoverText, 15, FontStyle.Bold);
        _tabIdleStyle = MakeButtonStyle(_btnNormalTex, _btnHoverTex, _btnActiveTex, ColTextSecondary, 15, FontStyle.Normal);

        // ---- 卡片 ----
        _cardBoxStyle = new GUIStyle
        {
            border = new RectOffset(8, 8, 8, 8),
            normal = { background = _cardNormalTex },
        };

        _cardNameStyle = new GUIStyle { fontSize = 16, fontStyle = FontStyle.Bold, wordWrap = true, clipping = TextClipping.Clip };
        _cardNameStyle.normal.textColor = ColTextPrimary;
        _cardNameStyle.font = _font;

        _detailNameStyle = new GUIStyle { font = _font, fontSize = 23, fontStyle = FontStyle.Bold, wordWrap = true, clipping = TextClipping.Clip };
        _detailNameStyle.normal.textColor = ColTextPrimary;
        _formSwitchTextStyle = new GUIStyle { font = _font, fontSize = 12, alignment = TextAnchor.MiddleRight, clipping = TextClipping.Clip };
        _cardCautionStyle = new GUIStyle { font = _font, fontSize = 11, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
        _cardCautionStyle.normal.textColor = ColAmber;

        _statusActiveStyle = new GUIStyle { fontSize = 11, alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold, clipping = TextClipping.Clip };
        _statusActiveStyle.normal.textColor = ColGreen;
        _statusActiveStyle.font = _font;

        _statusRestrictedStyle = new GUIStyle { fontSize = 11, alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold, clipping = TextClipping.Clip };
        _statusRestrictedStyle.normal.textColor = ColAmber;
        _statusRestrictedStyle.font = _font;

        _settingsTitleStyle = new GUIStyle { fontSize = 18, fontStyle = FontStyle.Bold };
        _settingsTitleStyle.normal.textColor = ColAmber;
        _settingsTitleStyle.font = _font;

        _settingsDescStyle = new GUIStyle { fontSize = 14, wordWrap = true, clipping = TextClipping.Clip };
        _settingsDescStyle.normal.textColor = ColTextSecondary;
        _settingsDescStyle.font = _font;

        _settingsSectionHeaderStyle = new GUIStyle { fontSize = 17, fontStyle = FontStyle.Bold };
        _settingsSectionHeaderStyle.normal.textColor = ColAmber;
        _settingsSectionHeaderStyle.font = _font;

        _settingsValueLabelStyle = new GUIStyle { fontSize = 15, alignment = TextAnchor.MiddleLeft, richText = true };
        _settingsValueLabelStyle.normal.textColor = ColTextPrimary;
        _settingsValueLabelStyle.font = _font;

        _hintStyle = new GUIStyle { fontSize = 13, wordWrap = true, alignment = TextAnchor.MiddleLeft };
        _hintStyle.normal.textColor = ColTextMuted;
        _hintStyle.font = _font;

        _policyNoticeStyle = new GUIStyle { fontSize = 12, fontStyle = FontStyle.Normal, alignment = TextAnchor.MiddleCenter, wordWrap = true, clipping = TextClipping.Clip, padding = new RectOffset(4, 4, 0, 0) };
        _policyNoticeStyle.normal.textColor = ColAmber;
        _policyNoticeStyle.font = _font;

        _inputStyle = new GUIStyle
        {
            normal = { background = _btnNormalTex, textColor = ColTextPrimary },
            focused = { background = _btnActiveTex, textColor = ColTextPrimary },
            hover = { background = _btnHoverTex, textColor = ColTextPrimary },
            active = { background = _btnActiveTex, textColor = ColTextPrimary },
            border = new RectOffset(6, 6, 6, 6),
            padding = new RectOffset(10, 10, 4, 4),
            alignment = TextAnchor.MiddleCenter,
            fontSize = 13,
            fontStyle = FontStyle.Bold,
        };
        _inputStyle.font = _font;

        _settingsInputStyle = new GUIStyle(_inputStyle)
        {
            fontSize = 15,
            padding = new RectOffset(12, 12, 6, 6),
            alignment = TextAnchor.MiddleCenter,
        };
        _settingsInputStyle.font = _font;

        // ---- 按钮：主操作暖橙底，次要操作使用中性色 ----
        _btnStyle = MakeButtonStyle(_btnNormalTex, _btnHoverTex, _btnActiveTex, ColTextPrimary, 13, FontStyle.Normal);
        _btnPrimaryStyle = MakeButtonStyle(_btnPrimaryTex, _tabActiveTex, _btnPrimaryTex, ColBtnHoverText, 16, FontStyle.Bold);
        _formSwitchOnStyle = MakeButtonStyle(_switchOnTex, _switchOnTex, _switchOnTex, ColGreen, 12, FontStyle.Bold);
        _formSwitchOffStyle = MakeButtonStyle(_switchOffTex, _switchOffTex, _switchOffTex, ColTextMuted, 12, FontStyle.Bold);
        foreach (GUIStyle switchStyle in new[] { _formSwitchOnStyle, _formSwitchOffStyle })
        {
            switchStyle.border = new RectOffset(3, 3, 3, 3);
            switchStyle.padding = new RectOffset(4, 4, 2, 2);
            switchStyle.margin = new RectOffset();
            switchStyle.font = _font;
        }
        // 客机只读态：保持深色背景，文字保留房间策略的语义色。
        Color lockedGrey = ColTextMuted;
        _policyLockedGreen = MakeButtonStyle(_btnNormalTex, _btnNormalTex, _btnNormalTex, Color.Lerp(ColGreen, lockedGrey, 0.55f), 13, FontStyle.Bold);
        _policyLockedAmber = MakeButtonStyle(_btnNormalTex, _btnNormalTex, _btnNormalTex, Color.Lerp(ColAmber, lockedGrey, 0.55f), 13, FontStyle.Bold);
        _policyLockedRed = MakeButtonStyle(_btnNormalTex, _btnNormalTex, _btnNormalTex, Color.Lerp(ColRed, lockedGrey, 0.55f), 13, FontStyle.Bold);
        // 参数页左列形态列表项（左对齐、行高 36px）。
        _listItemStyle = MakeButtonStyle(_btnNormalTex, _btnHoverTex, _btnActiveTex, ColTextPrimary, 13, FontStyle.Normal);
        _listItemStyle.alignment = TextAnchor.MiddleLeft;
        _listItemStyle.padding = new RectOffset(8, 8, 4, 4);
        _listItemActiveStyle = MakeButtonStyle(_tabActiveTex, _tabActiveTex, _tabActiveTex, ColBtnHoverText, 13, FontStyle.Bold);
        _listItemActiveStyle.alignment = TextAnchor.MiddleLeft;
        _listItemActiveStyle.padding = new RectOffset(8, 8, 4, 4);

        // 手动补字体（MakeButtonStyle 内不感知 _font）。
        foreach (GUIStyle s in new[] { _tabActiveStyle, _tabIdleStyle, _btnStyle, _btnPrimaryStyle,
                 _policyLockedGreen, _policyLockedAmber, _policyLockedRed,
                 _listItemStyle, _listItemActiveStyle })
        {
            s.font = _font;
        }

        // 普通按钮悬停时提亮文字，与浅色主操作保持明确层级。
        foreach (GUIStyle s in new[] { _tabIdleStyle, _btnStyle, _listItemStyle })
        {
            s.hover.textColor = ColTextPrimary;
            s.onHover.textColor = ColTextPrimary;
        }

        // Fully transparent button skin used as a click-catcher over the form cards.
        _invisibleButtonStyle = new GUIStyle();
    }

    private static void BuildTextures()
    {
        // Opaque charcoal surfaces, fine seams and a warm action accent; generated in memory.
        _windowTex         = MakeRoundTexture(48, ColWindowBg, ColBorder, 9f, 1f);
        _btnNormalTex      = MakeRoundTexture(32, ColBtnBg, ColBorder, 6f, 1f);
        _btnHoverTex       = MakeRoundTexture(32, ColBtnHoverBg, Hex(0x668087), 6f, 1f);
        _btnActiveTex      = MakeRoundTexture(32, Hex(0x34474D), ColBlue, 6f, 1f);
        _tabActiveTex      = MakeRoundTexture(32, ColAmber, ColAmber, 6f, 1f);
        _cardNormalTex     = MakeRoundTexture(40, ColCardBg, ColDivider, 7f, 1f);
        _cardHoverTex      = MakeRoundTexture(40, ColCardHoverBg, Hex(0x668087), 7f, 1f);
        _cardActiveTex     = MakeRoundTexture(40, ColCardActiveBg, ColGreen, 7f, 1f);
        _cardRestrictedTex = MakeRoundTexture(40, Hex(0x242B2E), ColDivider, 7f, 1f);
        _cardSelectedTex   = MakeRoundTexture(40, Hex(0x3B332B), ColAmber, 7f, 1.5f);
        _btnPrimaryTex     = MakeRoundTexture(32, ColAmber, ColAmber, 6f, 1f);
        _switchOnTex       = MakeRoundTexture(32, Hex(0x304B3D), Hex(0x54765E), 2f, 1f);
        _switchOffTex      = MakeRoundTexture(32, Hex(0x192125), ColBorder, 2f, 1f);
    }
}




