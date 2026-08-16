#nullable enable

using System;
using System.Collections.Generic;

namespace ShareX.ScreenCaptureLib;

/// <summary>
/// Groups stationary tiles with a one-tile gap tolerance. Large fixed panels frequently fragment into
/// several small components because text/icons inside the panel move the per-tile score. Treating each
/// fragment independently lets a visually huge panel bypass an area cap. Gap-tolerant grouping makes
/// the area safety decision on the whole screen-local UI cluster instead.
/// </summary>
internal static class ShareXModComponentGroupingV020
{
    internal static IEnumerable<List<int>> Group(HashSet<int> tiles, int columns, int rows)
    {
        var seen = new HashSet<int>();
        foreach (int start in tiles)
        {
            if (!seen.Add(start)) continue;
            var component = new List<int>();
            var queue = new Queue<int>();
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                int key = queue.Dequeue();
                component.Add(key);
                int y = key / columns;
                int x = key % columns;

                // Radius 2 bridges one empty tile caused by changing text/antialiasing inside one
                // fixed control, without joining distant independent UI regions.
                for (int dy = -2; dy <= 2; dy++)
                {
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        if (Math.Abs(dx) + Math.Abs(dy) > 2) continue;
                        int nx = x + dx;
                        int ny = y + dy;
                        if (nx < 0 || nx >= columns || ny < 0 || ny >= rows) continue;
                        int neighbour = ny * columns + nx;
                        if (tiles.Contains(neighbour) && seen.Add(neighbour)) queue.Enqueue(neighbour);
                    }
                }
            }

            yield return component;
        }
    }
}
