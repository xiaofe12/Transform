using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Transform.Core;
using UnityEngine;

// Exercise production layout and Unity's managed geometry without a running game.
internal static class Program
{
    private static int _checks;
    private const int FormCount = 15;
    private static void Check(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new Exception(message);
    }

    private static bool Contains(Rect parent, Rect child) => child.x >= parent.x - .01f
        && child.y >= parent.y - .01f && child.xMax <= parent.xMax + .01f && child.yMax <= parent.yMax + .01f;

    private static object Bounds(Rect rect) => new { x = rect.x, y = rect.y, width = rect.width, height = rect.height };

    private static void Main(string[] args)
    {
        int[,] screens = { { 640, 480 }, { 1024, 768 }, { 1280, 720 }, { 1366, 768 },
            { 1920, 1080 }, { 2560, 1440 }, { 3440, 1440 }, { 3840, 2160 } };
        var previews = new List<object>();
        for (int screen = 0; screen < screens.GetLength(0); screen++)
        for (int step = 10; step <= 20; step++)
        for (int notice = 0; notice <= 1; notice++)
        {
            float sw = screens[screen, 0], sh = screens[screen, 1];
            float requested = step / 10f;
            float scale = TransformMenuLayout.FitScale(requested, sw, sh);
            Vector2 size = TransformMenuLayout.WindowSize(sw, sh, scale);
            string context = $"{sw}x{sh} scale={requested} notice={notice}";
            Check(scale <= requested && scale >= .5f, context + ": invalid fitted scale");
            Check(size.x * scale <= sw - 19 && size.y * scale <= sh - 19, context + ": window offscreen");
            Rect toolbarNotice = TransformMenuLayout.ToolbarNotice(size.x, 250, 280, out float extraToolbarHeight);
            if (notice == 0) extraToolbarHeight = 0;
            Rect area = new Rect(18, 122 + extraToolbarHeight, size.x - 36, size.y - 184 - extraToolbarHeight);
            if (notice != 0)
            {
                Check(toolbarNotice.yMax < area.y, context + ": notice overlaps content");
                Check(toolbarNotice.x >= 18 && toolbarNotice.xMax <= size.x - 18, context + ": notice outside toolbar");
                if (extraToolbarHeight == 0)
                    Check(toolbarNotice.x >= 284 && toolbarNotice.xMax <= size.x - 314,
                        context + ": notice overlaps toolbar buttons");
                else
                    Check(Math.Abs(toolbarNotice.center.x - size.x / 2) < .01f, context + ": narrow notice must be centered");
            }
            var layout = new TransformMenuLayout(area);
            Check(Contains(area, layout.Grid) && Contains(area, layout.Details), context + ": panes escape content area");
            Check(!layout.Grid.Overlaps(layout.Details), context + ": grid overlaps actions");
            Check(layout.Details.height >= 144, context + ": action buttons clipped");
            Check(layout.CardWidth >= 240, context + ": form names too narrow");
            Rect view = new Rect(0, 0, layout.Grid.width - 20, layout.ViewHeight(FormCount));
            for (int card = 0; card < FormCount; card++)
            {
                Check(Contains(view, layout.Card(card)), context + ": inaccessible card " + card);
                Rect toggle = TransformMenuLayout.FormSwitch(layout.Card(card));
                Rect name = TransformMenuLayout.FormName(layout.Card(card));
                Check(Contains(layout.Card(card), toggle), context + ": switch escapes card");
                Check(!name.Overlaps(toggle) && name.width >= 115, context + ": switch overlaps form title");
                Rect caution = TransformMenuLayout.FormCaution(layout.Card(card));
                Check(Contains(layout.Card(card), caution) && !caution.Overlaps(name) && !caution.Overlaps(toggle),
                    context + ": caution overlaps title or switch");
                for (int other = card + 1; other < FormCount; other++)
                    Check(!layout.Card(card).Overlaps(layout.Card(other)), context + ": cards overlap");
            }
            if (notice == 0 && ((requested == 1 && sw == 1920) || (requested == 2 && sw == 1280)))
            {
                var cards = new List<object>();
                for (int card = 0; card < FormCount; card++) cards.Add(Bounds(layout.Card(card)));
                previews.Add(new { screenWidth = sw, screenHeight = sh, width = size.x, height = size.y,
                    grid = Bounds(layout.Grid), details = Bounds(layout.Details), layout.Stacked, cards });
            }
        }
        Check(!new TransformMenuLayout(new Rect(0, 0, 900, 300)).Stacked, "Wide boundary must use sidebar");
        Check(new TransformMenuLayout(new Rect(0, 0, 899, 300)).Stacked, "Narrow boundary must stack");
        if (args.Length > 0) File.WriteAllText(args[0], JsonSerializer.Serialize(previews));
        Console.WriteLine($"Menu layout: {_checks} checks passed across 8 resolutions, 11 scales and room notices.");
    }
}
