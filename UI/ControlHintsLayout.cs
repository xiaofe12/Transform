using UnityEngine;

namespace Transform.Core;

// Screen-space geometry shared by the tutorial overlay and its boundary checks.
internal readonly struct ControlHintsLayout
{
    internal readonly float Scale, Margin, Gap, MaxWidth, AvailableHeight;
    internal readonly int FontSize;
    private readonly float _screenWidth, _screenHeight;

    internal ControlHintsLayout(float screenWidth, float screenHeight)
    {
        _screenWidth = Mathf.Max(1f, screenWidth);
        _screenHeight = Mathf.Max(1f, screenHeight);
        Scale = Mathf.Clamp(Mathf.Min(_screenHeight / 900f, _screenWidth / 1280f), 0.78f, 2.4f);
        FontSize = Mathf.RoundToInt(20f * Scale);
        // Retain an inset even in very small windows without consuming the viewport.
        Margin = Mathf.Min(Mathf.Max(18f, 24f * Scale), Mathf.Min(_screenWidth, _screenHeight) * 0.05f);
        Gap = 12f * Scale;
        MaxWidth = Mathf.Min(560f * Scale, _screenWidth - Margin * 2f);
        AvailableHeight = _screenHeight - Margin * 2f;
    }

    // Normally only font fitting is needed. Extreme windows/binding lengths can
    // still overflow at the minimum font, so fit the complete measured block too.
    internal Rect Place(float width, float height, out float drawScale)
    {
        drawScale = Mathf.Min(1f, Mathf.Min((_screenWidth - Margin * 2f) / Mathf.Max(1f, width),
            AvailableHeight / Mathf.Max(1f, height)));
        float fittedWidth = width * drawScale, fittedHeight = height * drawScale;
        return new Rect(_screenWidth - Margin - fittedWidth,
            _screenHeight - Margin - fittedHeight, fittedWidth, fittedHeight);
    }
}
