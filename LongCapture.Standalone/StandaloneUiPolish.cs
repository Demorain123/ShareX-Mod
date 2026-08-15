using System;
using System.Drawing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace LongCapture.Standalone;

internal static class StandaloneUiPolish
{
    private static readonly ConditionalWeakTable<Form, object> Applied = new();

    public static void Apply(Form form)
    {
        if (Applied.TryGetValue(form, out _)) return;
        Applied.Add(form, new object());

        form.SuspendLayout();
        try
        {
            form.Text = $"LongCapture Standalone v{StandaloneVersion.Value}";
            form.AutoScaleMode = AutoScaleMode.Dpi;
            form.MinimumSize = new Size(840, 720);
            if (form.Width < 960 || form.Height < 800)
            {
                form.Size = new Size(Math.Max(form.Width, 960), Math.Max(form.Height, 800));
            }

            TableLayoutPanel? body = FindBody(form);
            if (body is null) return;

            body.AutoScroll = true;
            body.Padding = new Padding(20, 16, 20, 16);
            body.GrowStyle = TableLayoutPanelGrowStyle.FixedSize;

            for (int i = 0; i < Math.Min(11, body.RowStyles.Count); i++)
            {
                body.RowStyles[i].SizeType = SizeType.AutoSize;
                body.RowStyles[i].Height = 0;
            }

            foreach (Control control in body.Controls)
            {
                control.Margin = new Padding(4, 5, 4, 5);

                if (control is Label label)
                {
                    label.AutoEllipsis = false;
                }
                else if (control is ComboBox or NumericUpDown or TextBox)
                {
                    control.MinimumSize = new Size(0, 34);
                }
            }

            FlowLayoutPanel? readinessPanel = body.GetControlFromPosition(1, 2) as FlowLayoutPanel;
            if (readinessPanel is not null)
            {
                readinessPanel.WrapContents = false;
                readinessPanel.Padding = new Padding(0, 1, 0, 1);
                readinessPanel.MinimumSize = new Size(0, 48);

                Button? probe = readinessPanel.Controls.OfType<Button>().FirstOrDefault();
                if (probe is not null)
                {
                    probe.AutoSize = true;
                    probe.MinimumSize = new Size(130, 34);
                }

                Label? readiness = readinessPanel.Controls.OfType<Label>().FirstOrDefault();
                if (readiness is not null)
                {
                    readiness.AutoSize = true;
                    readiness.AutoEllipsis = false;
                    readiness.Margin = new Padding(10, 5, 0, 5);
                    readiness.Padding = Padding.Empty;
                }
            }

            FlowLayoutPanel? recipePanel = body.GetControlFromPosition(1, 4) as FlowLayoutPanel;
            if (recipePanel is not null)
            {
                recipePanel.WrapContents = true;
                recipePanel.Padding = new Padding(0, 1, 0, 1);
                recipePanel.MinimumSize = new Size(0, 46);

                foreach (Button button in recipePanel.Controls.OfType<Button>())
                {
                    button.AutoSize = true;
                    button.MinimumSize = new Size(button.Text.StartsWith("Review", StringComparison.OrdinalIgnoreCase) ? 140 : 120, 34);
                }
            }

            if (body.GetControlFromPosition(1, 1) is Button browserButton)
            {
                browserButton.AutoSize = true;
                browserButton.MinimumSize = new Size(175, 34);
            }

            if (body.GetControlFromPosition(1, 9) is CheckBox startPosition)
            {
                startPosition.AutoSize = true;
                startPosition.MinimumSize = new Size(0, 34);
            }

            if (body.GetControlFromPosition(1, 10) is Label output)
            {
                output.AutoSize = true;
                output.AutoEllipsis = false;
                output.Padding = new Padding(0, 7, 0, 7);
            }

            Panel? actionPanel = body.GetControlFromPosition(0, 11) as Panel;
            if (actionPanel is not null)
            {
                actionPanel.MinimumSize = new Size(0, 156);

                foreach (Button button in actionPanel.Controls.OfType<Button>())
                {
                    if (!button.Text.StartsWith("Start long capture", StringComparison.OrdinalIgnoreCase) &&
                        !button.Text.StartsWith("Stop capture", StringComparison.OrdinalIgnoreCase))
                    {
                        button.AutoSize = true;
                        button.MinimumSize = new Size(150, 34);
                    }
                }

                Label? quality = actionPanel.Controls.OfType<Label>().FirstOrDefault();
                if (quality is not null)
                {
                    quality.AutoSize = true;
                    quality.AutoEllipsis = false;
                    quality.MinimumSize = new Size(0, 36);
                }
            }

            Label? subtitle = form.Controls.OfType<Label>()
                .FirstOrDefault(x => x.Text.StartsWith("Independent long screenshot", StringComparison.OrdinalIgnoreCase));
            if (subtitle is not null)
            {
                subtitle.AutoSize = true;
                subtitle.MinimumSize = new Size(0, 42);
            }

            Label? status = form.Controls.OfType<Label>()
                .FirstOrDefault(x => x.Dock == DockStyle.Bottom);
            if (status is not null)
            {
                status.AutoSize = true;
                status.AutoEllipsis = false;
                status.MinimumSize = new Size(0, 46);
            }

            void Reflow()
            {
                ReflowWrappedText(form, body, readinessPanel, actionPanel, subtitle, status);
            }

            form.ClientSizeChanged += (_, _) => Reflow();
            form.DpiChanged += (_, _) => BeginReflow(form, Reflow);
            body.ClientSizeChanged += (_, _) => Reflow();
            if (readinessPanel is not null) readinessPanel.ClientSizeChanged += (_, _) => Reflow();

            BeginReflow(form, Reflow);
        }
        finally
        {
            form.ResumeLayout(true);
        }
    }

    public static bool Validate(Form form, out string detail)
    {
        TableLayoutPanel? body = FindBody(form);
        if (body is null)
        {
            detail = "main TableLayoutPanel not found";
            return false;
        }

        Size original = form.Size;
        try
        {
            foreach (Size size in new[] { new Size(960, 800), new Size(840, 720), new Size(1100, 900) })
            {
                form.Size = size;
                form.PerformLayout();
                body.PerformLayout();
                ReflowWrappedText(
                    form,
                    body,
                    body.GetControlFromPosition(1, 2) as FlowLayoutPanel,
                    body.GetControlFromPosition(0, 11) as Panel,
                    form.Controls.OfType<Label>().FirstOrDefault(x => x.Text.StartsWith("Independent long screenshot", StringComparison.OrdinalIgnoreCase)),
                    form.Controls.OfType<Label>().FirstOrDefault(x => x.Dock == DockStyle.Bottom));
                form.PerformLayout();
                body.PerformLayout();

                if (!body.AutoScroll)
                {
                    detail = $"body AutoScroll disabled at {size.Width}x{size.Height}";
                    return false;
                }

                if (!ValidateReadiness(body, out detail)) return false;
                if (!ValidateRecipeActions(body, out detail)) return false;
                if (!ValidateButtons(form, out detail)) return false;
            }
        }
        finally
        {
            form.Size = original;
            form.PerformLayout();
        }

        detail = "responsive layout checks passed";
        return true;
    }

    private static void BeginReflow(Control control, Action reflow)
    {
        if (control.IsHandleCreated)
        {
            control.BeginInvoke(reflow);
        }
        else
        {
            reflow();
        }
    }

    private static TableLayoutPanel? FindBody(Form form) =>
        form.Controls.OfType<TableLayoutPanel>()
            .FirstOrDefault(x => x.ColumnCount == 2 && x.RowCount >= 12);

    private static void ReflowWrappedText(
        Form form,
        TableLayoutPanel body,
        FlowLayoutPanel? readinessPanel,
        Panel? actionPanel,
        Label? subtitle,
        Label? status)
    {
        int labelColumn = body.ColumnStyles.Count > 0
            ? (int)Math.Round(body.ColumnStyles[0].Width)
            : 180;
        int contentWidth = Math.Max(260, body.ClientSize.Width - body.Padding.Horizontal - labelColumn - 18);

        if (readinessPanel is not null)
        {
            Button? probe = readinessPanel.Controls.OfType<Button>().FirstOrDefault();
            Label? readiness = readinessPanel.Controls.OfType<Label>().FirstOrDefault();
            if (readiness is not null)
            {
                int probeWidth = probe?.PreferredSize.Width ?? 130;
                int width = Math.Max(220, contentWidth - probeWidth - 30);
                readiness.MaximumSize = new Size(width, 0);
                Size preferred = readiness.GetPreferredSize(new Size(width, 0));
                readinessPanel.MinimumSize = new Size(0, Math.Max(48, preferred.Height + 12));
            }
        }

        if (body.GetControlFromPosition(1, 10) is Label output)
        {
            output.MaximumSize = new Size(contentWidth, 0);
        }

        if (actionPanel is not null)
        {
            Label? quality = actionPanel.Controls.OfType<Label>().FirstOrDefault();
            if (quality is not null)
            {
                int width = Math.Max(300, body.ClientSize.Width - body.Padding.Horizontal - 8);
                quality.MaximumSize = new Size(width, 0);
                Size preferred = quality.GetPreferredSize(new Size(width, 0));
                actionPanel.MinimumSize = new Size(0, Math.Max(156, 120 + preferred.Height));
            }
        }

        if (subtitle is not null)
        {
            subtitle.MaximumSize = new Size(Math.Max(320, form.ClientSize.Width - 40), 0);
        }

        if (status is not null)
        {
            status.MaximumSize = new Size(Math.Max(320, form.ClientSize.Width - 36), 0);
        }
    }

    private static bool ValidateReadiness(TableLayoutPanel body, out string detail)
    {
        if (body.GetControlFromPosition(1, 2) is not FlowLayoutPanel panel)
        {
            detail = "readiness panel missing";
            return false;
        }

        Label? label = panel.Controls.OfType<Label>().FirstOrDefault();
        if (label is null)
        {
            detail = "readiness label missing";
            return false;
        }

        Rectangle bounds = label.Bounds;
        if (bounds.Right > panel.ClientSize.Width + 2 || bounds.Bottom > panel.ClientSize.Height + 2)
        {
            detail = $"readiness text clipped: label={bounds}, panel={panel.ClientRectangle}";
            return false;
        }

        return true;
    }

    private static bool ValidateRecipeActions(TableLayoutPanel body, out string detail)
    {
        if (body.GetControlFromPosition(1, 4) is not FlowLayoutPanel panel)
        {
            detail = "recipe action panel missing";
            return false;
        }

        foreach (Button button in panel.Controls.OfType<Button>())
        {
            if (button.Bounds.Right > panel.ClientSize.Width + 2 || button.Bounds.Bottom > panel.ClientSize.Height + 2)
            {
                detail = $"recipe button clipped: {button.Text}";
                return false;
            }
        }

        return true;
    }

    private static bool ValidateButtons(Control root, out string detail)
    {
        foreach (Button button in Descendants(root).OfType<Button>())
        {
            if (string.IsNullOrWhiteSpace(button.Text)) continue;

            Size measured = TextRenderer.MeasureText(button.Text, button.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            if (button.ClientSize.Width + 2 < measured.Width + 12 || button.ClientSize.Height + 2 < measured.Height + 8)
            {
                detail = $"button text clipped: {button.Text} ({button.ClientSize.Width}x{button.ClientSize.Height}, text {measured.Width}x{measured.Height})";
                return false;
            }
        }

        detail = string.Empty;
        return true;
    }

    private static System.Collections.Generic.IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }
}
