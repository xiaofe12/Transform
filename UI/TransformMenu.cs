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

    private const float MinMenuScale = 1f;
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
    private const float TitleBarHeight = 52f;
    private const float FixedWindowWidth = 1180f;
    private const float FixedWindowHeight = 640f;
    private const float WindowScreenMargin = 40f;

    // ------------------------------------------------------------------
    // 主题色板（童军营登机牌风——奶油纸感 + 深棕粗描边 + 营橙/营绿点缀）
    // 与游戏官方视觉一致："bold colours, soft-edged, thick-lined UI, scout camp tone"。
    // ------------------------------------------------------------------
    private static readonly Color ColWindowBg   = Hex(0xF0E3C8); // 窗口底：奶油纸感
    private static readonly Color ColTitleStrip = Hex(0x5B4636); // 标题条：深棕帆布色
    private static readonly Color ColBorder     = Hex(0x5B4636); // 粗描边：深棕
    private static readonly Color ColDivider    = Hex(0xE3D2AC); // 细分隔线：浅棕
    private static readonly Color ColTextPrimary  = Hex(0x4A3B2E); // 主文字：深棕
    private static readonly Color ColTextSecondary= Hex(0x7A6A52); // 次级文字（卡片描述等）：中棕
    private static readonly Color ColTextMuted    = Hex(0xA39278); // 提示文字：浅棕（奶油底上 ~7:1）
    private static readonly Color ColAmber = Hex(0xE8703A); // 营橙：主强调/受限/操作
    private static readonly Color ColGreen = Hex(0x5C9E4A); // 营绿：当前/确认
    private static readonly Color ColBlue  = Hex(0x4E90C9); // 天蓝：就绪
    private static readonly Color ColRed   = Hex(0xC0553F); // 砖红：禁用/危险
    private static readonly Color ColCardBg      = Hex(0xFBF5E4); // 卡片底：奶油白
    private static readonly Color ColCardHoverBg = Hex(0xFFF9E9); // 卡片悬停提亮
    private static readonly Color ColCardActiveBg= Hex(0xEAF4DF); // 当前形态卡：淡营绿
    private static readonly Color ColBtnBg       = Hex(0xFBF5E4); // 按钮底
    private static readonly Color ColBtnHoverBg  = Hex(0xE8703A); // 按钮悬停：营橙
    private static readonly Color ColBtnHoverText= Hex(0xFFFFFF); // 按钮悬停文字：白
    private static readonly Color ColTitleText   = Hex(0xF5EBD8); // 标题条文字：奶油

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
    private static Texture2D _bannerTex;
    private static Texture2D _btnNormalTex, _btnHoverTex, _btnActiveTex;
    private static Texture2D _tabActiveTex;
    private static Texture2D _cardNormalTex, _cardHoverTex, _cardActiveTex, _cardRestrictedTex, _cardSelectedTex;
    private static Texture2D _btnPrimaryTex;             // 主操作（恢复原形）营绿实底
    private static Texture2D _policyGreenTex, _policyAmberTex, _policyRedTex; // 策略按钮实底

    // 样式（OnGUI 首帧构建；字体升级时重建）
    private static bool _stylesBuilt;
    private static bool _texturesBuilt;
    private static GUIStyle _windowStyle;
    private static GUIStyle _titleStyle;
    private static GUIStyle _closeStyle;
    private static GUIStyle _tabActiveStyle, _tabIdleStyle;
    private static GUIStyle _cardBoxStyle;
    private static GUIStyle _cardNameStyle;
    private static GUIStyle _cardDescStyle;
    private static GUIStyle _statusActiveStyle;
    private static GUIStyle _statusBlockedStyle;
    private static GUIStyle _statusRestrictedStyle;
    private static GUIStyle _settingsTitleStyle;
    private static GUIStyle _settingsDescStyle;
    private static GUIStyle _settingsSectionHeaderStyle;
    private static GUIStyle _settingsValueLabelStyle;
    /// <summary>每帧 CalcSize/CalcHeight 的共享 GUIContent（使用前必须赋 text），避免 OnGUI 每帧分配。</summary>
    private static readonly GUIContent _scratchContent = new GUIContent();
    private static GUIStyle _settingsInputStyle;
    private static GUIStyle _hintStyle;
    private static GUIStyle _policyNoticeStyle;
    private static GUIStyle _inputStyle;
    private static GUIStyle _btnStyle;            // 中性按钮
    private static GUIStyle _btnPrimaryStyle;     // 主操作（恢复原形）绿色
    private static GUIStyle _policyBtnGreen, _policyBtnAmber, _policyBtnRed;
    private static GUIStyle _policyLockedGreen, _policyLockedAmber, _policyLockedRed; // 客机只读态（文字调暗、无悬停）
    private static GUIStyle _listItemStyle;          // 参数页左列形态列表项（未选中）
    private static GUIStyle _listItemActiveStyle;    // 参数页左列形态列表项（选中，琥珀高亮）
    private static GUIStyle _invisibleButtonStyle;

    internal static bool IsOpen => _open;

    /// <summary>返回形态页单击选中的形态 ID（-1 = 无选中）。供 TransformPlugin 短按 F 变身使用。</summary>
    internal static int SelectedFormIndex => _selectedFormIndex;

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

    // 1) 遮罩：矩阵外，物理全屏（暖棕，融入游戏画面）。
    DrawRect(new Rect(0f, 0f, Screen.width, Screen.height), new Color(0.24f, 0.20f, 0.15f, 0.45f));

    float scale = CurrentScale();
    if (scale > 1.001f)
    {
        Vector2 pivot = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        GUI.matrix = Matrix4x4.TRS(pivot, Quaternion.identity, Vector3.one * scale)
                   * Matrix4x4.TRS(-pivot, Quaternion.identity, Vector3.one);
    }

    // 逻辑可用尺寸（缩放矩阵内的布局上限）。
    float logW = Screen.width / scale;
    float logH = Screen.height / scale;
    // 统一固定大窗口。只在屏幕确实放不下时按屏幕边距收缩，避免点击时
    // GUILayout 根据内容重新估算窗口高宽，引发底部 HUD/菜单闪一下。
    float width = Mathf.Min(FixedWindowWidth, Mathf.Max(640f, logW - WindowScreenMargin));
    float height = Mathf.Min(FixedWindowHeight, Mathf.Max(420f, logH - WindowScreenMargin));
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

        // ---------------- 标题条：[变身面板] [形态][参数调整] ─── [缩放下拉框] ─── [策略按钮] [×] ----------------
        // 缩放控件保留下拉框；下拉事件先于内容页处理，避免误触形态卡片。
        DrawRect(new Rect(0f, 0f, w, TitleBarHeight), ColTitleStrip);
        DrawRect(new Rect(0f, TitleBarHeight, w, 2f), ColBorder);

        // 标题（左）：英文 "Transform Panel" 比中文宽，不能用固定 110px。
        string titleText = Localization.Tr("变身面板", "Transform Panel");
        float tabY = (TitleBarHeight - 30f) * 0.5f;
        _scratchContent.text = titleText;
        float titleW = Mathf.Clamp(_titleStyle.CalcSize(_scratchContent).x + 16f, 118f, Mathf.Max(118f, w * 0.28f));
        Rect titleRect = new Rect(14f, (TitleBarHeight - 30f) * 0.5f, titleW, 30f);
        ShadowedLabel(titleRect, titleText, _titleStyle);

        // 页签：形态 / 参数调整（与标题平齐，紧贴在标题右侧）
        _scratchContent.text = Localization.Tr("形态", "Forms");
        float formsW = Mathf.Max(70f, _tabIdleStyle.CalcSize(_scratchContent).x + 30f);
        _scratchContent.text = Localization.Tr("参数调整", "Settings");
        float settingsW = Mathf.Max(86f, _tabIdleStyle.CalcSize(_scratchContent).x + 30f);
        float formsX = titleRect.xMax + 14f;
        float settingsX = formsX + formsW + 8f;
        Rect formsTabRect = new Rect(formsX, tabY, formsW, 30f);
        RecordNonDragRect(formsTabRect);
        if (GUI.Button(formsTabRect,
                Localization.Tr("形态", "Forms"), _page == 0 ? _tabActiveStyle : _tabIdleStyle))
        {
            if (_page != 0) { _page = 0; GUIUtility.ExitGUI(); }
        }
        Rect settingsTabRect = new Rect(settingsX, tabY, settingsW, 30f);
        RecordNonDragRect(settingsTabRect);
        if (GUI.Button(settingsTabRect,
                Localization.Tr("参数调整", "Settings"), _page == 1 ? _tabActiveStyle : _tabIdleStyle))
        {
            if (_page != 1) { _page = 1; GUIUtility.ExitGUI(); }
        }

        // 策略按钮：紧贴 X 关闭按钮（用户要求移到最右侧）。三档循环。
        // 无管理权的客机显示锁定样式，由 CycleFromMenu 内部权限检查兜底忽略。
        GUIStyle policyStyle = RoomPolicy.CanManageRoomPolicy
            ? PolicyButtonStyle(RoomPolicy.Current)
            : LockedPolicyStyle(RoomPolicy.Current);
        float policyW = Mathf.Min(220f, w - 200f);
        float policyX = w - 52f - 34f - 6f - policyW; // X 按钮左侧留 6px 间距
        Rect policyRect = new Rect(policyX, tabY, policyW, 30f);
        RecordNonDragRect(policyRect);
        if (GUI.Button(policyRect,
                PolicyControlLabel(RoomPolicy.Current), policyStyle))
        {
            RoomPolicy.CycleFromMenu();
            GUIUtility.ExitGUI();
        }

        // 当前已有形态时，把恢复操作放在标题栏中段，避免占用形态页底部空间。
        Rect restoreTopRect = Rect.zero;
        if (FormRegistry.ActiveForm != null)
        {
            const float restoreW = 104f;
            restoreTopRect = new Rect(policyX - 10f - restoreW, tabY, restoreW, 30f);
            RecordNonDragRect(restoreTopRect);
            if (GUI.Button(restoreTopRect, Localization.Tr("恢复", "Restore"), _btnPrimaryStyle))
            {
                SetOpen(false);
                TransformPlugin.Instance?.RequestRestore();
                GUIUtility.ExitGUI();
            }
        }

        // 关闭按钮：最右
        Rect closeRect = new Rect(w - 52f, tabY, 34f, 30f);
        RecordNonDragRect(closeRect);
        if (GUI.Button(closeRect, "×", _closeStyle))
        {
            SetOpen(false);
            GUIUtility.ExitGUI();
        }

        // ---- 标题条中间空白处放 UI 缩放下拉框（1 / 1.1 / ... / 2.0） ----
        _scaleDropdownHeadRect = Rect.zero;
        if (_scaleEntry != null)
        {
            // 页签到策略按钮之间的空白区
            float scaleLeft = settingsTabRect.xMax + 12f;
            float scaleRight = (restoreTopRect.width > 0f ? restoreTopRect.x : policyX) - 10f;
            float avail = scaleRight - scaleLeft;
            if (avail > 140f)
            {
                float cur = CurrentScale();
                _scaleDropdownHeadRect = new Rect(scaleLeft, tabY, Mathf.Min(138f, avail), 30f);
                RecordNonDragRect(_scaleDropdownHeadRect);
                string label = Localization.Tr("缩放", "Scale") + " " + FormatScale(cur) + "× ▾";
                if (GUI.Button(_scaleDropdownHeadRect, label, _tabIdleStyle))
                {
                    _scaleDropdownOpen = !_scaleDropdownOpen;
                    GUIUtility.ExitGUI();
                }
            }
        }

        HandleScaleDropdownInput();

        Rect contentRect = new Rect(16f, TitleBarHeight + 8f, w - 32f, _windowRect.height - TitleBarHeight - 46f);
        Rect hintRect = new Rect(18f, _windowRect.height - 22f, w - 36f, 18f);
        if (_page == 0)
        {
            DrawFormsPage(contentRect);
        }
        else
        {
            GUILayout.BeginArea(contentRect);
            DrawSettingsPage(contentRect);
            GUILayout.EndArea();
        }

        // 底部提示固定绘制，不参与页面布局计算，避免点击/选择导致窗口高度重排。
        GUI.Label(hintRect, HintText(), _hintStyle);

        DrawScaleDropdown();
        HandleWindowDrag();
    }

    // ------------------------------------------------------------------
    // Page 1 — form selection cards + 房间策略选项条.
    // ------------------------------------------------------------------

    private static void DrawFormsPage(Rect area)
    {
        // 移除顶部状态条（"当前：XX / 未变身 + 未装模组同步"）——用户确认不需要该功能行。

        float y = area.y;

        // 客机端主机模组状态横幅。复用房间限制提示框：房主安装时说明由房主接管；
        // 房主未安装时说明菜单调整只影响本地，不写入大厅/房间属性。
        if (RoomPolicy.IsClientInOnlineRoom)
        {
            Rect hostBanner = new Rect(area.x, y, area.width, 36f);
            GUI.Box(hostBanner, GUIContent.none, _bannerStyle());
            ShadowedLabel(hostBanner, HostModStatusText(), _policyNoticeStyle);
            y += 42f;
        }

        // 房间策略限制横幅（禁用 / 仅大厅但当前不在大厅）。
        if (!RoomPolicy.CanTransformNow)
        {
            Rect banner = new Rect(area.x, y, area.width, 36f);
            GUI.Box(banner, GUIContent.none, _bannerStyle());
            ShadowedLabel(banner, PolicyBlockedReason(), _policyNoticeStyle);
            y += 42f;
        }

        System.Collections.Generic.IReadOnlyList<FormRegistry.FormDescriptor> forms = FormRegistry.Forms;

        // 英文 1.8x/1.9x 下三列卡片太窄。改为两列 + 垂直滚动条；
        // 卡片保持适中尺寸，避免两列后单张卡片过高过宽。
        const int columns = 2;
        const float gapX = 18f;
        const float gapY = 9f;
        const float sideInset = 28f;
        const float scrollBarW = 24f;
        int rows = Mathf.Max(1, Mathf.CeilToInt(forms.Count / (float)columns));
        float restoreReserve = 0f;
        Rect scrollRect = new Rect(area.x, y, area.width, Mathf.Max(80f, area.yMax - y - restoreReserve));
        RecordNonDragRect(scrollRect);
        float cardH = 108f;
        float viewHeight = rows * cardH + Mathf.Max(0, rows - 1) * gapY + 4f;
        Rect viewRect = new Rect(0f, 0f, scrollRect.width - scrollBarW, Mathf.Max(scrollRect.height, viewHeight));
        float cardW = (viewRect.width - sideInset * 2f - gapX * (columns - 1)) / columns;

        _formsScroll = GUI.BeginScrollView(scrollRect, _formsScroll, viewRect, false, true);
        for (int row = 0; row < forms.Count; row += columns)
        {
            for (int c = 0; c < columns; c++)
            {
                int index = row + c;
                if (index < forms.Count)
                {
                    Rect card = new Rect(sideInset + c * (cardW + gapX), (row / columns) * (cardH + gapY), cardW, cardH);
                    DrawFormCard(forms[index], index, card);
                }
            }
        }
        GUI.EndScrollView();

        // 房间策略按钮已上移至标题条（与标题齐平），底部不再重复显示。
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

    /// <summary>策略按钮配色：绿 = 全场景可用，琥珀 = 仅大厅，红 = 禁用（色彩语义与卡片状态一致）。</summary>
    private static GUIStyle PolicyButtonStyle(RoomPolicy.Policy policy)
    {
        switch (policy)
        {
            case RoomPolicy.Policy.LobbyOnly: return _policyBtnAmber;
            case RoomPolicy.Policy.Disabled:   return _policyBtnRed;
            default:                           return _policyBtnGreen;
        }
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
        bool isActive = IsActiveForm(form);
        bool policyBlocked = !RoomPolicy.CanTransformNow;
        bool formAllowed = RoomPolicy.IsFormAllowed(form.Id);
        bool formBlocked = !formAllowed;
        bool canEnter = !policyBlocked && !formBlocked && SafeCanEnter(form);
        bool isSelected = _selectedFormIndex == formIndex && !isActive;
        string knownReason = null;
        if (!isActive && !policyBlocked && !formBlocked && !canEnter && form.UnavailableReason != null)
        {
            knownReason = form.UnavailableReason();
        }

        float toggleSize = Mathf.Min(card.height - 20f, 92f) * 0.8f;
        Rect toggleRect = new Rect(card.xMax - toggleSize - 12f, card.y + (card.height - toggleSize) * 0.5f,
            toggleSize, toggleSize);
        RecordNonDragRect(card);
        bool hover = Event.current.type == EventType.Repaint && card.Contains(Event.current.mousePosition);

        Texture2D bg = isActive ? _cardActiveTex
            : isSelected ? _cardSelectedTex
            : policyBlocked || formBlocked ? _cardRestrictedTex
            : hover && canEnter ? _cardHoverTex
            : _cardNormalTex;
        _cardBoxStyle.normal.background = bg;
        GUI.Box(card, GUIContent.none, _cardBoxStyle);

        if (isActive)
        {
            DrawRect(new Rect(card.x, card.y, card.width, 2f), ColGreen);
            DrawRect(new Rect(card.x, card.yMax - 2f, card.width, 2f), ColGreen);
            DrawRect(new Rect(card.x, card.y, 2f, card.height), ColGreen);
            DrawRect(new Rect(card.xMax - 2f, card.y, 2f, card.height), ColGreen);
        }

        Color strip = isActive ? ColGreen
            : policyBlocked || formBlocked ? ColAmber
            : canEnter ? ColBlue
            : ColTextMuted;
        DrawRect(new Rect(card.x + 6f, card.y + 9f, 4f, card.height - 18f), strip);

        float left = card.x + 20f;
        float contentRight = toggleRect.x - 14f;
        float contentW = Mathf.Max(1f, contentRight - left);
        Rect nameRect = new Rect(left, card.y + 14f, contentW * 0.48f, 30f);
        ShadowedLabel(nameRect, form.Name, _cardNameStyle);

        string status = isActive ? Localization.Tr("当前形态", "Active")
            : policyBlocked ? Localization.Tr("房间已限制", "Restricted")
            : formBlocked ? Localization.Tr("房间已禁用", "Disabled by room")
            : canEnter ? ""
            : !string.IsNullOrEmpty(knownReason) ? knownReason
            : Localization.Tr("当前不可用", "Unavailable");
        if (status.Length > 0)
        {
            GUIStyle statusStyle = isActive ? _statusActiveStyle
                : policyBlocked || formBlocked || !string.IsNullOrEmpty(knownReason) ? _statusRestrictedStyle
                : _statusBlockedStyle;
            Rect statusRect = new Rect(left + contentW * 0.50f, card.y + 14f, contentW * 0.50f, 30f);
            ShadowedLabel(statusRect, status, statusStyle);
        }

        if (isSelected)
        {
            DrawRect(new Rect(card.x, card.y, card.width, 3f), ColAmber);
            DrawRect(new Rect(card.x, card.yMax - 3f, card.width, 3f), ColAmber);
            DrawRect(new Rect(card.x, card.y, 3f, card.height), ColAmber);
            DrawRect(new Rect(card.xMax - 3f, card.y, 3f, card.height), ColAmber);
        }

        string desc = CardDescription(form, true);
        float descW = contentW;
        float descY = card.y + 43f;
        float descMaxH = Mathf.Max(34f, card.yMax - descY - 8f);
        _scratchContent.text = desc;
        float descH = Mathf.Clamp(_cardDescStyle.CalcHeight(_scratchContent, descW), 34f, descMaxH);
        Rect descRect = new Rect(left, descY, descW, descH);
        ShadowedLabel(descRect, desc, _cardDescStyle);

        bool canManagePolicy = RoomPolicy.CanManageRoomPolicy;
        GUIStyle toggleStyle = formAllowed
            ? (canManagePolicy ? _policyBtnRed : _policyLockedGreen)
            : (canManagePolicy ? _policyBtnGreen : _policyLockedRed);
        string toggleText = canManagePolicy
            ? (formAllowed ? Localization.Tr("禁用", "Disable") : Localization.Tr("启用", "Enable"))
            : (formAllowed ? Localization.Tr("已启用", "On") : Localization.Tr("已禁用", "Off"));
        RecordNonDragRect(toggleRect);
        if (GUI.Button(toggleRect, toggleText, toggleStyle))
        {
            RoomPolicy.ToggleFormAllowedFromMenu(form.Id);
            GUIUtility.ExitGUI();
        }

        Rect clickArea = new Rect(card.x, card.y, Mathf.Max(1f, toggleRect.x - card.x - 6f), card.height);
        RecordNonDragRect(clickArea);
        if (GUI.Button(clickArea, GUIContent.none, _invisibleButtonStyle))
        {
            bool isDoubleClick = formIndex == _lastClickFormIndex
                && Time.unscaledTime - _lastClickTime <= DoubleClickWindowSeconds
                && (Event.current.mousePosition - _lastClickPos).sqrMagnitude <= DoubleClickMaxDistSqr;
            _lastClickFormIndex = formIndex;
            _lastClickTime = Time.unscaledTime;
            _lastClickPos = Event.current.mousePosition;

            if (isActive)
            {
                if (isDoubleClick)
                {
                    SetOpen(false);
                    TransformPlugin.Instance?.RequestRestore();
                    GUIUtility.ExitGUI();
                    return;
                }
                _selectedFormIndex = formIndex;
            }
            else if (canEnter)
            {
                if (isDoubleClick)
                {
                    SetOpen(false);
                    TransformPlugin.Instance?.RequestEnterForm(form.Id);
                    GUIUtility.ExitGUI();
                    return;
                }
                _selectedFormIndex = formIndex;
            }
        }
    }
    // ------------------------------------------------------------------
    // Page 2 — per-form camera + behaviour parameters (live).
    // ------------------------------------------------------------------

    private static void DrawSettingsPage(Rect area)
    {
        System.Collections.Generic.IReadOnlyList<FormRegistry.FormDescriptor> forms = FormRegistry.Forms;
        if (_settingsFormIndex < 0 || _settingsFormIndex >= forms.Count) _settingsFormIndex = 0;

        float hostBannerReserve = RoomPolicy.IsClientInOnlineRoom ? 50f : 0f;
        if (RoomPolicy.IsClientInOnlineRoom)
        {
            Rect hostBanner = GUILayoutUtility.GetRect(0f, 36f, GUILayout.ExpandWidth(true));
            GUI.Box(hostBanner, GUIContent.none, _bannerStyle());
            ShadowedLabel(hostBanner, HostModStatusText(), _policyNoticeStyle);
            RecordLastControlRect();
            GUILayout.Space(8f);
        }

        GUILayout.BeginHorizontal();

        // 左列：14 个形态可直接展示；行高按页面高度精确压入内容区，避免底部提示重叠。
        float listWidth = Mathf.Clamp(area.width * 0.23f, 230f, 280f);
        float listItemH = Mathf.Clamp((area.height - hostBannerReserve - 48f) / Mathf.Max(1, forms.Count) - 3f, 24f, 32f);
        GUILayout.BeginVertical(GUILayout.Width(listWidth));
        DrawSettingsTitleHeader(Localization.Tr("选择形态", "Select form"));
        GUILayout.Space(6f);
        for (int i = 0; i < forms.Count; i++)
        {
            bool selected = i == _settingsFormIndex;
            GUIStyle itemStyle = selected ? _listItemActiveStyle : _listItemStyle;
            if (GUILayout.Button(forms[i].Name, itemStyle, GUILayout.Height(listItemH)))
            {
                if (!selected)
                {
                    _settingsFormIndex = i;
                    GUIUtility.ExitGUI();
                }
            }
            RecordLastControlRect();
        }
        GUILayout.EndVertical();

        GUILayout.Space(10f);

        // 右侧：当前形态说明 + 参数分组。宽屏时相机/行为并排，窄屏时自动上下排列。
        GUILayout.BeginVertical(GUILayout.ExpandWidth(true));

        FormRegistry.FormDescriptor form = forms[_settingsFormIndex];
        ShadowedLabel(GUILayoutUtility.GetRect(0f, 34f, GUILayout.ExpandWidth(true)),
            form.Name, _settingsTitleStyle);
        GUILayout.Label(CardDescription(form, false), _settingsDescStyle, GUILayout.MinHeight(72f), GUILayout.ExpandWidth(true));
        RecordLastControlRect();
        GUILayout.Space(18f);

        float rightWidth = Mathf.Max(1f, area.width - listWidth - 22f);
        bool twoColumns = rightWidth >= 760f;
        if (twoColumns)
        {
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width((rightWidth - 28f) * 0.5f));
            DrawCameraSettings(form);
            GUILayout.EndVertical();
            GUILayout.Space(28f);
            GUILayout.BeginVertical(GUILayout.Width((rightWidth - 28f) * 0.5f));
            DrawBehaviourSettings(form);
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }
        else
        {
            DrawCameraSettings(form);
            GUILayout.Space(24f);
            DrawBehaviourSettings(form);
        }

        GUILayout.Space(28f);
        string resetText = Localization.Tr("恢复该形态默认参数", "Reset this form to defaults");
        _scratchContent.text = resetText;
        float resetW = Mathf.Clamp(_btnStyle.CalcSize(_scratchContent).x + 44f, 220f, 360f);
        GUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        if (GUILayout.Button(resetText, _btnStyle, GUILayout.Height(36f), GUILayout.Width(resetW)))
        {
            ResetFormDefaults(form);
        }
        RecordLastControlRect();
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();

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

        // 单行布局 [label]: [input] [range]。滑杆会与窗口拖动 / 下拉弹层抢事件，
        // 改为输入框后参数仍实时生效，且不会干扰拖窗。
        GUILayout.BeginHorizontal();
        GUILayout.Label(label + ":", _settingsValueLabelStyle, GUILayout.Width(180f), GUILayout.Height(34f));
        string key = FloatInputKey(entry);
        DrawFloatInputField(key, entry, min, max, _settingsInputStyle, GUILayout.Width(112f), GUILayout.Height(34f));
        GUILayout.Space(10f);
        GUILayout.Label("<color=#A39278>" + min.ToString("0.##") + " - " + max.ToString("0.##") + "</color>",
            _settingsValueLabelStyle, GUILayout.Width(120f), GUILayout.Height(34f));
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(8f);
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
        return _scaleEntry != null ? Mathf.Clamp(_scaleEntry.Value, MinMenuScale, MaxMenuScale) : 1f;
    }

    private static string FormatScale(float scale)
    {
        float rounded = Mathf.Round(Mathf.Clamp(scale, MinMenuScale, MaxMenuScale) * 10f) / 10f;
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
        RecordLastControlRect();
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

    private static void RecordLastControlRect()
    {
        if (Event.current == null || Event.current.type == EventType.Layout) return;
        RecordNonDragRect(GUILayoutUtility.GetLastRect());
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
        float current = NearestScaleStep(CurrentScale());
        for (int i = 0; i < ScaleSteps.Length; i++)
        {
            float step = ScaleSteps[i];
            Rect item = new Rect(_scaleDropdownListRect.x, _scaleDropdownListRect.y + i * itemH,
                _scaleDropdownListRect.width, itemH);
            bool selected = Mathf.Abs(step - current) < 0.001f;
            GUI.Box(item, FormatScale(step) + "×", selected ? _tabActiveStyle : _tabIdleStyle);
        }
    }

    /// <summary>
    /// 带 1px 投影的文字：先画一层半透明偏移副本再画正文，给关键文字提供轮廓衬托
    /// （IMGUI 无原生文字描边）。投影色为半透明深棕，适配奶油纸感底色。
    /// </summary>
    private static void ShadowedLabel(Rect rect, string text, GUIStyle style)
    {
        Color original = style.normal.textColor;
        style.normal.textColor = new Color(0.29f, 0.23f, 0.18f, 0.35f);
        GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text, style);
        style.normal.textColor = original;
        GUI.Label(rect, text, style);
    }

    /// <summary>
    /// 在徽章内画白色对勾（两段旋转矩形，无需字形——"✓" 在部分字体缺字形，手绘最稳）。
    /// </summary>
    private static void DrawSettingsTitleHeader(string text)
    {
        Rect header = GUILayoutUtility.GetRect(0f, 34f, GUILayout.ExpandWidth(true));
        ShadowedLabel(header, text, _settingsTitleStyle);
    }

    private static void DrawSettingsSectionHeader(string text)
    {
        Rect header = GUILayoutUtility.GetRect(0f, 28f, GUILayout.ExpandWidth(true));
        ShadowedLabel(header, text, _settingsSectionHeaderStyle);
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

    private static GUIStyle _bannerBoxStyle;

    private static GUIStyle _bannerStyle()
    {
        if (_bannerBoxStyle == null)
        {
            _bannerBoxStyle = new GUIStyle
            {
                border = new RectOffset(8, 8, 8, 8),
                normal = { background = _bannerTex },
            };
        }
        return _bannerBoxStyle;
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

    private static string CardDescription(FormRegistry.FormDescriptor form, bool compactPickupWarning)
    {
        string desc = form.Description;
        if (!NeedsPickupWarning(form.Id))
        {
            return desc;
        }
        return desc + "\n" + PickupWarningText(compactPickupWarning);
    }

    private static bool NeedsPickupWarning(FormId id)
    {
        return id == FormId.Frog
               || id == FormId.Scorpion
               || id == FormId.Coconut
               || id == FormId.Bomb;
    }

    private static string PickupWarningText(bool compact)
    {
        return compact
            ? Localization.Tr(
                "主机未装模组：被捡起可能复制，请慎用",
                "Host without mod: pickup may duplicate. Use care.")
            : Localization.Tr(
                "提示：主机未安装该模组时，变身对象可能被捡起并复制，请慎用",
                "Warning: if the host does not have this mod, the transformed object may be picked up and duplicated. Use with care");
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
            "单击选中 · 按 [" + key + "] 变身 · 双击直接变身/切换 · 长按 [" + key + "] 1 秒开/关菜单 · 变身中短按 [" + key + "] 恢复原形",
            "Click to select · press [" + key + "] to transform · double-click to transform/switch · hold [" + key + "] 1s to open/close · short-press [" + key + "] while transformed to restore");
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

        _titleStyle = new GUIStyle { fontSize = 19, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
        _titleStyle.normal.textColor = ColTitleText;   // 标题条深棕底 → 奶油字
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

        // ---- 页签（选中 = 营橙实底白字，未选中 = 奶油底深棕字） ----
        _tabActiveStyle = MakeButtonStyle(_tabActiveTex, _tabActiveTex, _tabActiveTex, ColBtnHoverText, 15, FontStyle.Bold);
        _tabIdleStyle = MakeButtonStyle(_btnNormalTex, _btnHoverTex, _btnActiveTex, ColTextSecondary, 15, FontStyle.Normal);

        // ---- 卡片 ----
        _cardBoxStyle = new GUIStyle
        {
            border = new RectOffset(8, 8, 8, 8),
            normal = { background = _cardNormalTex },
        };

        _cardNameStyle = new GUIStyle { fontSize = 17, fontStyle = FontStyle.Bold, wordWrap = false };
        _cardNameStyle.normal.textColor = ColTextPrimary;
        _cardNameStyle.font = _font;

        _cardDescStyle = new GUIStyle { fontSize = 11, wordWrap = true, clipping = TextClipping.Clip };
        _cardDescStyle.normal.textColor = ColTextSecondary;
        _cardDescStyle.font = _font;

        _statusActiveStyle = new GUIStyle { fontSize = 15, alignment = TextAnchor.MiddleRight, fontStyle = FontStyle.Bold };
        _statusActiveStyle.normal.textColor = ColGreen;
        _statusActiveStyle.font = _font;

        _statusBlockedStyle = new GUIStyle { fontSize = 15, alignment = TextAnchor.MiddleRight, fontStyle = FontStyle.Bold };
        _statusBlockedStyle.normal.textColor = ColTextMuted;
        _statusBlockedStyle.font = _font;

        _statusRestrictedStyle = new GUIStyle { fontSize = 15, alignment = TextAnchor.MiddleRight, fontStyle = FontStyle.Bold };
        _statusRestrictedStyle.normal.textColor = ColAmber;
        _statusRestrictedStyle.font = _font;

        _settingsTitleStyle = new GUIStyle { fontSize = 18, fontStyle = FontStyle.Bold };
        _settingsTitleStyle.normal.textColor = ColAmber;
        _settingsTitleStyle.font = _font;

        _settingsDescStyle = new GUIStyle { fontSize = 14, wordWrap = true, clipping = TextClipping.Clip };
        _settingsDescStyle.normal.textColor = ColTextPrimary;
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

        _policyNoticeStyle = new GUIStyle { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        _policyNoticeStyle.normal.textColor = Hex(0xC75A28); // 横幅浅橙底 → 深营橙文字
        _policyNoticeStyle.font = _font;

        _inputStyle = new GUIStyle
        {
            normal = { background = _btnNormalTex, textColor = ColTextPrimary },
            focused = { background = _btnActiveTex, textColor = ColTextPrimary },
            hover = { background = _btnHoverTex, textColor = ColBtnHoverText },
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

        // ---- 按钮（主操作/策略 = 实色底 + 白字；悬停轻微提亮） ----
        _btnStyle = MakeButtonStyle(_btnNormalTex, _btnHoverTex, _btnActiveTex, ColTextPrimary, 13, FontStyle.Normal);
        _btnPrimaryStyle = MakeButtonStyle(_btnPrimaryTex, _btnPrimaryTex, _btnPrimaryTex, ColBtnHoverText, 14, FontStyle.Bold);
        _policyBtnGreen = MakeButtonStyle(_policyGreenTex, _policyGreenTex, _policyGreenTex, ColBtnHoverText, 13, FontStyle.Bold);
        _policyBtnAmber = MakeButtonStyle(_policyAmberTex, _policyAmberTex, _policyAmberTex, ColBtnHoverText, 13, FontStyle.Bold);
        _policyBtnRed = MakeButtonStyle(_policyRedTex, _policyRedTex, _policyRedTex, ColBtnHoverText, 13, FontStyle.Bold);
        // 客机只读态：奶油底 + 深棕描边（无悬停变化），文字调暗表达禁改。
        Color lockedGrey = new Color(0.64f, 0.58f, 0.47f);
        _policyLockedGreen = MakeButtonStyle(_btnNormalTex, _btnNormalTex, _btnNormalTex, Color.Lerp(ColGreen, lockedGrey, 0.55f), 13, FontStyle.Bold);
        _policyLockedAmber = MakeButtonStyle(_btnNormalTex, _btnNormalTex, _btnNormalTex, Color.Lerp(ColAmber, lockedGrey, 0.55f), 13, FontStyle.Bold);
        _policyLockedRed = MakeButtonStyle(_btnNormalTex, _btnNormalTex, _btnNormalTex, Color.Lerp(ColRed, lockedGrey, 0.55f), 13, FontStyle.Bold);
        // 参数页左列形态列表项（左对齐、行高 36px）。
        _listItemStyle = MakeButtonStyle(_btnNormalTex, _btnHoverTex, _btnActiveTex, ColTextPrimary, 13, FontStyle.Normal);
        _listItemStyle.alignment = TextAnchor.MiddleCenter;
        _listItemStyle.padding = new RectOffset(8, 8, 4, 4);
        _listItemActiveStyle = MakeButtonStyle(_tabActiveTex, _tabActiveTex, _tabActiveTex, ColBtnHoverText, 13, FontStyle.Bold);
        _listItemActiveStyle.alignment = TextAnchor.MiddleCenter;
        _listItemActiveStyle.padding = new RectOffset(8, 8, 4, 4);

        // 手动补字体（MakeButtonStyle 内不感知 _font）。
        foreach (GUIStyle s in new[] { _tabActiveStyle, _tabIdleStyle, _btnStyle, _btnPrimaryStyle,
                 _policyBtnGreen, _policyBtnAmber, _policyBtnRed,
                 _policyLockedGreen, _policyLockedAmber, _policyLockedRed,
                 _listItemStyle, _listItemActiveStyle })
        {
            s.font = _font;
        }

        // 普通按钮 hover 背景 = 营橙实底 → hover 文字转白保证可读
        // （策略按钮/主操作按钮本身常驻白字；locked 系列 hover 背景不变，不处理）。
        foreach (GUIStyle s in new[] { _tabIdleStyle, _btnStyle, _listItemStyle })
        {
            s.hover.textColor = ColBtnHoverText;
            s.onHover.textColor = ColBtnHoverText;
        }

        // Fully transparent button skin used as a click-catcher over the form cards.
        _invisibleButtonStyle = new GUIStyle();
    }

    private static void BuildTextures()
    {
        // 游戏画风：描边 1px→2px（thick-lined），奶油纸感底 + 深棕粗描边 + 营橙/营绿点缀。
        Color btnOutlineN = ColBorder;                    // 深棕描边
        Color btnOutlineH = Hex(0x6B5542);                // 悬停深棕提亮
        _windowTex         = MakeRoundTexture(48, ColWindowBg,   ColBorder, 12f, 2f);
        _bannerTex         = MakeRoundTexture(32, Hex(0xFBE7D3), ColAmber, 8f, 2f); // 受限横幅：浅营橙底+营橙描边
        _btnNormalTex      = MakeRoundTexture(32, ColBtnBg,      btnOutlineN, 8f, 2f);
        _btnHoverTex       = MakeRoundTexture(32, ColBtnHoverBg, btnOutlineH, 8f, 2f);
        _btnActiveTex      = MakeRoundTexture(32, Hex(0xF0D9B4), btnOutlineH, 8f, 2f);
        _tabActiveTex      = MakeRoundTexture(32, ColAmber,      Hex(0xC75A28), 8f, 2f); // 选中页签/列表项：营橙实底
        _cardNormalTex     = MakeRoundTexture(40, ColCardBg,      ColBorder, 10f, 2f);
        _cardHoverTex      = MakeRoundTexture(40, ColCardHoverBg, Hex(0x6B5542), 10f, 2f);
        _cardActiveTex     = MakeRoundTexture(40, ColCardActiveBg, ColGreen, 10f, 2f);
        _cardRestrictedTex = MakeRoundTexture(40, Hex(0xF6E7CE),  ColAmber, 10f, 2f);
        _cardSelectedTex   = MakeRoundTexture(40, Hex(0xFBE7D3),  ColAmber, 10f, 3f); // 选中：淡营橙底+营橙粗描边（选择框）
        // 实色主操作/策略按钮（营绿 / 营橙 / 砖红实底 + 深色描边 + 白字）。
        _btnPrimaryTex     = MakeRoundTexture(32, ColGreen, Hex(0x3F7A32), 8f, 2f);
        _policyGreenTex    = MakeRoundTexture(32, ColGreen, Hex(0x3F7A32), 8f, 2f);
        _policyAmberTex    = MakeRoundTexture(32, ColAmber, Hex(0xC75A28), 8f, 2f);
        _policyRedTex      = MakeRoundTexture(32, ColRed,   Hex(0x8E3A2C), 8f, 2f);
    }
}




