using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace LongCapture.Standalone;

/// <summary>
/// Keeps button hit targets and captions usable when Windows DPI or the inherited UI font grows.
/// This is deliberately content-driven rather than a list of hard-coded widths so future buttons
/// receive the same release-quality treatment automatically.
/// </summary>
internal static class StandaloneButtonTextFit
{
    private const int HorizontalSafety = 20;
    private const int VerticalSafety = 12;

    public static void Apply(Control root)
    {
        foreach (Button button in Buttons(root))
        {
            if (string.IsNullOrWhiteSpace(button.Text)) continue;

            Size measured = TextRenderer.MeasureText(
                button.Text,
                button.Font,
                Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

            Size currentMinimum = button.MinimumSize;
            button.MinimumSize = new Size(
                Math.Max(currentMinimum.Width, measured.Width + HorizontalSafety),
                Math.Max(currentMinimum.Height, measured.Height + VerticalSafety));

            // AutoSize buttons should actually consume the new preferred width. Docked primary
            // actions retain their layout semantics; the Table/Flow panels honour MinimumSize.
            if (button.AutoSize && button.Dock == DockStyle.None)
            {
                button.Size = button.GetPreferredSize(Size.Empty);
            }
        }
    }

    private static IEnumerable<Button> Buttons(Control root)
    {
        foreach (Control child in root.Controls)
        {
            if (child is Button button) yield return button;
            foreach (Button nested in Buttons(child)) yield return nested;
        }
    }
}
