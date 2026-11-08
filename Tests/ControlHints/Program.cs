using System;
using Transform.Core;
using UnityEngine;

// Validate production safe-area placement, including a block that remains taller
// than the screen after font fitting. Font glyph rendering still needs Unity.
internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new Exception(message);
    }

    private static void Main()
    {
        int[,] screens = { { 320, 180 }, { 640, 480 }, { 800, 600 }, { 1024, 768 },
            { 1280, 720 }, { 1366, 768 }, { 1920, 1080 }, { 2560, 1440 },
            { 3440, 1440 }, { 3840, 2160 }, { 7680, 4320 }, { 480, 1920 } };
        for (int i = 0; i < screens.GetLength(0); i++)
        {
            int sw = screens[i, 0], sh = screens[i, 1];
            var layout = new ControlHintsLayout(sw, sh);
            Check(layout.FontSize >= 16, "Small screen font must remain readable");
            Check(layout.MaxWidth > 0 && layout.MaxWidth + 2 * layout.Margin <= sw + .01f, "Width exceeds screen");
            foreach (float measuredHeight in new[] { 40f, 180f, 420f, 900f, 2400f, 10000f })
            foreach (float width in new[] { 120f, layout.MaxWidth, sw * 2f })
            {
                Rect block = layout.Place(width, measuredHeight, out float scale);
                Check(scale > 0 && scale <= 1, "Invalid fit scale");
                Check(block.x >= layout.Margin - .01f && block.y >= layout.Margin - .01f, "Block escapes top/left inset");
                Check(block.xMax <= sw - layout.Margin + .01f && block.yMax <= sh - layout.Margin + .01f, "Block escapes right/bottom inset");
                Check(Math.Abs(block.width - width * scale) < .01f && Math.Abs(block.height - measuredHeight * scale) < .01f,
                    "Placement must scale the complete measured block");
            }
        }
        Check(new ControlHintsLayout(1280, 720).FontSize == 16, "720p font size");
        Check(new ControlHintsLayout(1920, 1080).FontSize == 24, "1080p font size");
        Check(new ControlHintsLayout(2560, 1440).FontSize == 32, "1440p font size");
        Check(new ControlHintsLayout(3840, 2160).FontSize == 48, "4K font size");
        Check(new ControlHintsLayout(480, 1920).FontSize < new ControlHintsLayout(1920, 1920).FontSize,
            "Tall narrow windows must consider screen width");
        Console.WriteLine($"Control hints: {_checks} checks passed across 12 screen sizes, including overflowing blocks.");
    }
}
