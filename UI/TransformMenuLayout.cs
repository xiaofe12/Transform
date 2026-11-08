using UnityEngine;

namespace Transform.Core;

// Shared geometry for IMGUI: the action panel never overlaps the scrolling collection.
internal readonly struct TransformMenuLayout
{
    internal const float WindowMargin = 40f;
    internal const float MinWidth = 640f;
    internal const float MinHeight = 480f;

    // The preferred scale is persistent; a temporary screen fit never changes it.
    internal static float FitScale(float preferred, float screenWidth, float screenHeight)
    {
        float fit = Mathf.Min(screenWidth / (MinWidth + WindowMargin), screenHeight / (MinHeight + WindowMargin));
        return Mathf.Min(Mathf.Clamp(preferred, 1f, 2f), Mathf.Max(0.5f, fit));
    }

    internal static Vector2 WindowSize(float screenWidth, float screenHeight, float scale) => new Vector2(
        Mathf.Min(1180f, Mathf.Max(MinWidth, screenWidth / scale - WindowMargin)),
        Mathf.Min(720f, Mathf.Max(MinHeight, screenHeight / scale - WindowMargin)));

    // Center notices between the toolbar controls; use a full-width toolbar row
    // when a narrow window cannot fit a readable two-line message there.
    internal static Rect ToolbarNotice(float windowWidth, float policyWidth, float controlsWidth, out float extraHeight)
    {
        float left = 18f + policyWidth + 16f;
        float right = windowWidth - 18f - controlsWidth - 16f;
        extraHeight = right - left >= 280f ? 0f : 46f;
        return extraHeight == 0f ? new Rect(left, 67f, right - left, 32f)
            : new Rect(18f, 106f, windowWidth - 36f, 40f);
    }

    internal static Rect FormSwitch(Rect card) => new Rect(card.xMax - 100f, card.y + 10f, 90f, 26f);
    internal static Rect FormName(Rect card) => new Rect(card.x + 14f, card.y + 10f, card.width - 122f, 40f);
    internal static Rect FormCaution(Rect card) => new Rect(card.x + 14f, card.y + 52f, card.width - 28f, 16f);

    internal readonly Rect Grid;
    internal readonly Rect Details;
    internal readonly int Columns;
    internal readonly float CardWidth;
    internal const float CardHeight = 100f;
    internal const float Gap = 10f;
    internal readonly bool Stacked;

    internal TransformMenuLayout(Rect area)
    {
        Stacked = area.width < 900f;
        if (Stacked)
        {
            float detailsHeight = Mathf.Clamp(area.height * 0.48f, 144f, 190f);
            Grid = new Rect(area.x, area.y, area.width, Mathf.Max(60f, area.height - detailsHeight - 12f));
            Details = new Rect(area.x, Grid.yMax + 12f, area.width, detailsHeight);
        }
        else
        {
            const float detailWidth = 292f;
            Grid = new Rect(area.x, area.y, area.width - detailWidth - 16f, area.height);
            Details = new Rect(Grid.xMax + 16f, area.y, detailWidth, area.height);
        }
        Columns = Grid.width >= 760f ? 3 : 2;
        CardWidth = (Grid.width - 20f - Gap * (Columns - 1)) / Columns;
    }

    internal Rect Card(int index) => new Rect((index % Columns) * (CardWidth + Gap),
        (index / Columns) * (CardHeight + Gap), CardWidth, CardHeight);

    internal float ViewHeight(int count)
    {
        int rows = Mathf.Max(1, Mathf.CeilToInt(count / (float)Columns));
        return rows * (CardHeight + Gap) - Gap + 2f;
    }
}
