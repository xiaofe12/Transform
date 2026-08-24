using System;
using System.Globalization;
using BepInEx.Logging;

namespace Transform.Core;

/// <summary>
/// UI localization for the unified Transform menu. Follows the game language: Simplified and
/// Traditional Chinese use Chinese text; every other language uses English.
/// Detection mirrors the proven approach from the standalone Scoutmaster mod:
///   1. PlayerPrefs "LanguageSetting" (int index; 9 = Simplified Chinese, 10 = Traditional Chinese)
///   2. LocalizedText.CURRENT_LANGUAGE name as fallback ("Chinese"/"中文"/"zh*")
/// </summary>
internal static class Localization
{
    private const int SimplifiedChineseLanguageIndex = 9;
    private const int TraditionalChineseLanguageIndex = 10;

    private static ManualLogSource _log;
    private static bool? _cachedChinese;
    private static float _nextRefreshTime;

    /// <summary>True when the game runs in Simplified or Traditional Chinese.</summary>
    internal static bool IsChinese
    {
        get
        {
            if (_cachedChinese.HasValue) return _cachedChinese.Value;
            _cachedChinese = DetectChinese();
            return _cachedChinese.Value;
        }
    }

    internal static void Initialize(ManualLogSource log)
    {
        _log = log;
        _cachedChinese = null;
    }

    /// <summary>
    /// Re-checks the language at most twice a second. The game applies the language setting
    /// after the first scene load, so a one-shot read at plugin Awake can be wrong.
    /// </summary>
    internal static void Tick()
    {
        float now;
        try { now = UnityEngine.Time.unscaledTime; }
        catch { return; }
        if (now < _nextRefreshTime) return;
        _nextRefreshTime = now + 0.5f;

        bool detected = DetectChinese();
        if (_cachedChinese.HasValue && _cachedChinese.Value == detected) return;

        _cachedChinese = detected;
        _log?.LogInfo("[Transform] Menu language: " + (detected ? "Chinese" : "English"));
    }

    /// <summary>Picks the Chinese or English variant of a label.</summary>
    internal static string Tr(string chinese, string english)
    {
        return IsChinese ? chinese : english;
    }

    private static bool DetectChinese()
    {
        try
        {
            if (TryGetConfiguredGameLanguage(out bool configuredChinese)) return configuredChinese;
            if (TryGetLocalizedTextLanguageName(out string languageName)) return IsChineseLanguageName(languageName);
        }
        catch (Exception ex)
        {
            _log?.LogWarning("[Transform] Language detection failed: " + ex.Message);
        }
        return false;
    }

    private static bool TryGetConfiguredGameLanguage(out bool isChineseLanguage)
    {
        isChineseLanguage = false;
        try
        {
            if (!UnityEngine.PlayerPrefs.HasKey("LanguageSetting")) return false;

            int languageValue = UnityEngine.PlayerPrefs.GetInt("LanguageSetting", int.MinValue);
            if (languageValue != int.MinValue)
            {
                isChineseLanguage = IsChineseLanguageIndex(languageValue);
                return true;
            }

            string languageText = UnityEngine.PlayerPrefs.GetString("LanguageSetting", string.Empty);
            if (string.IsNullOrWhiteSpace(languageText)) return false;
            if (int.TryParse(languageText, NumberStyles.Integer, CultureInfo.InvariantCulture, out languageValue))
            {
                isChineseLanguage = IsChineseLanguageIndex(languageValue);
                return true;
            }

            isChineseLanguage = IsChineseLanguageName(languageText);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetLocalizedTextLanguageName(out string languageName)
    {
        languageName = string.Empty;
        try
        {
            languageName = LocalizedText.CURRENT_LANGUAGE.ToString();
            return !string.IsNullOrWhiteSpace(languageName);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsChineseLanguageName(string languageName)
    {
        if (string.IsNullOrWhiteSpace(languageName)) return false;
        if (int.TryParse(languageName.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int languageValue))
        {
            return IsChineseLanguageIndex(languageValue);
        }
        return languageName.IndexOf("Chinese", StringComparison.OrdinalIgnoreCase) >= 0
            || languageName.IndexOf("中文", StringComparison.OrdinalIgnoreCase) >= 0
            || languageName.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsChineseLanguageIndex(int languageValue)
    {
        return languageValue == SimplifiedChineseLanguageIndex
            || languageValue == TraditionalChineseLanguageIndex;
    }
}
