using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Forms;

namespace LongCapture.Standalone;

internal static class StandaloneUiPolish
{
    internal const string ExperienceVersion = "0.1.7";

    private static readonly ConditionalWeakTable<Form, object> Applied = new();

    private static class Palette
    {
        internal static readonly Color Canvas = Color.FromArgb(246, 247, 250);
        internal static readonly Color Surface = Color.FromArgb(255, 255, 255);
        internal static readonly Color SurfaceMuted = Color.FromArgb(249, 250, 252);
        internal static readonly Color Text = Color.FromArgb(31, 41, 55);
        internal static readonly Color TextMuted = Color.FromArgb(89, 99, 115);
        internal static readonly Color Border = Color.FromArgb(221, 226, 232);
        internal static readonly Color Accent = Color.FromArgb(0, 120, 212);
        internal static readonly Color AccentHover = Color.FromArgb(0, 103, 184);
        internal static readonly Color AccentPressed = Color.FromArgb(0, 90, 158);
    }

    public static void Apply(Form form)
    {
        if (Applied.TryGetValue(form, out _)) return;
        Applied.Add(form, new object());

        form.SuspendLayout();
        try
        {
            bool highContrast = SystemInformation.HighContrast;
            form.Text = $"LongCapture v{ExperienceVersion} · Engine {StandaloneVersion.Value}";
            form.AutoScaleMode = AutoScaleMode.Dpi;
            form.MinimumSize = new Size(820, 700);
            if (form.Width < 1080 || form.Height < 820)
            {
                form.Size = new Size(Math.Max(form.Width, 1080), Math.Max(form.Height, 820));
            }

            if (!highContrast)
            {
                form.BackColor = Palette.Canvas;
                form.ForeColor = Palette.Text;
            }

            TableLayoutPanel? body = FindBody(form);
            if (body is null) return;
            TableLayoutPanel? targetStrip = FindTargetStrip(form, body);

            Label? title = form.Controls.OfType<Label>()
                .FirstOrDefault(x => string.Equals(x.Text, "LongCapture", StringComparison.OrdinalIgnoreCase));
            Label? subtitle = form.Controls.OfType<Label>()
                .FirstOrDefault(x => x.Text.StartsWith("Independent long screenshot", StringComparison.OrdinalIgnoreCase));
            Label? status = form.Controls.OfType<Label>()
                .FirstOrDefault(x => x.Dock == DockStyle.Bottom);

            StyleHeader(title, subtitle, highContrast);
            StyleStatus(status, highContrast);
            if (targetStrip is not null) StyleTargetStrip(targetStrip, highContrast);

            RoundedSurfacePanelV017? bodyHost = WrapBodyInSurface(form, body, highContrast);
            StyleBody(body, highContrast);
            StyleControlTree(body, highContrast);
            if (targetStrip is not null) StyleControlTree(targetStrip, highContrast);

            FlowLayoutPanel? readinessPanel = body.GetControlFromPosition(1, 2) as FlowLayoutPanel;
            FlowLayoutPanel? recipePanel = body.GetControlFromPosition(1, 4) as FlowLayoutPanel;
            Panel? actionPanel = body.GetControlFromPosition(0, 11) as Panel;

            ConfigureReadiness(readinessPanel);
            ConfigureRecipeActions(recipePanel);
            ConfigureSimpleRows(body);
            if (actionPanel is not null) HardenActionPanel(actionPanel, highContrast);

            ComboBox? modeSelector = body.GetControlFromPosition(1, 0) as ComboBox;
            if (modeSelector is not null)
            {
                modeSelector.SelectionChangeCommitted += (_, _) =>
                    BeginReflow(form, () =>
                    {
                        ApplyModeVisibility(body, modeSelector);
                        ReflowModernLayout(form, body, targetStrip, readinessPanel, actionPanel, subtitle, status, bodyHost);
                    });
                ApplyModeVisibility(body, modeSelector);
            }

            void Reflow()
            {
                ReflowModernLayout(form, body, targetStrip, readinessPanel, actionPanel, subtitle, status, bodyHost);
            }

            form.ClientSizeChanged += (_, _) => Reflow();
            form.DpiChanged += (_, _) => BeginReflow(form, Reflow);
            form.Shown += (_, _) => BeginReflow(form, Reflow);
            body.ClientSizeChanged += (_, _) => Reflow();
            if (targetStrip is not null) targetStrip.ClientSizeChanged += (_, _) => Reflow();
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

        TableLayoutPanel? targetStrip = FindTargetStrip(form, body);
        if (targetStrip is null)
        {
            detail = "responsive target/speed command surface not found";
            return false;
        }

        Size original = form.Size;
        try
        {
            foreach (Size size in new[]
            {
                new Size(840, 720),
                new Size(925, 760),
                new Size(1080, 820),
                new Size(1280, 900)
            })
            {
                form.Size = size;
                form.PerformLayout();
                targetStrip.PerformLayout();
                body.PerformLayout();
                ReflowModernLayout(
                    form,
                    body,
                    targetStrip,
                    body.GetControlFromPosition(1, 2) as FlowLayoutPanel,
                    body.GetControlFromPosition(0, 11) as Panel,
                    FindSubtitle(form),
                    FindStatus(form),
                    FindBodyHost(body));
                form.PerformLayout();
                targetStrip.PerformLayout();
                body.PerformLayout();

                if (!body.AutoScroll)
                {
                    detail = $"body AutoScroll disabled at {size.Width}x{size.Height}";
                    return false;
                }

                if (!targetStrip.AutoSize)
                {
                    detail = $"target/speed surface is not AutoSize at {size.Width}x{size.Height}";
                    return false;
                }

                if (!ValidateTargetStrip(targetStrip, out detail)) return false;
                if (!ValidateReadiness(body, out detail)) return false;
                if (!ValidatePrimaryActions(body, out detail)) return false;
                if (!ValidateImportantLabels(form, body, out detail)) return false;
                if (!ValidateButtons(form, out detail)) return false;
                if (!ValidateCheckBoxes(form, out detail)) return false;
                if (!ValidateModernHierarchy(form, body, out detail)) return false;
            }
        }
        finally
        {
            form.Size = original;
            form.PerformLayout();
        }

        detail = "v0.1.7 responsive/modern layout checks passed at 840/925/1080/1280 widths";
        return true;
    }

    public static int CaptureVisualAudit(Form form, string outputDirectory, out string detail)
    {
        try
        {
            if (!Validate(form, out string validateDetail))
            {
                detail = validateDetail;
                return 71;
            }

            Directory.CreateDirectory(outputDirectory);
            Size original = form.Size;
            var captures = new List<object>();
            try
            {
                _ = form.Handle;
                foreach (Size size in new[]
                {
                    new Size(840, 720),
                    new Size(925, 760),
                    new Size(1080, 820),
                    new Size(1280, 900)
                })
                {
                    form.Size = size;
                    form.PerformLayout();
                    Application.DoEvents();

                    int width = Math.Max(1, form.ClientSize.Width);
                    int height = Math.Max(1, form.ClientSize.Height);
                    using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                    form.DrawToBitmap(bitmap, new Rectangle(0, 0, width, height));
                    string name = $"LongCapture-v017-ui-{size.Width}x{size.Height}.png";
                    string path = Path.Combine(outputDirectory, name);
                    bitmap.Save(path, ImageFormat.Png);
                    captures.Add(new { size = $"{size.Width}x{size.Height}", file = name, bytes = new FileInfo(path).Length });
                }
            }
            finally
            {
                form.Size = original;
                form.PerformLayout();
            }

            string manifestPath = Path.Combine(outputDirectory, "ui-audit.json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
            {
                version = ExperienceVersion,
                engine = StandaloneVersion.Value,
                validated = true,
                generatedUtc = DateTime.UtcNow,
                captures
            }, new JsonSerializerOptions { WriteIndented = true }));

            detail = $"modern UI visual audit captured {captures.Count} responsive sizes; {validateDetail}";
            return 0;
        }
        catch (Exception ex)
        {
            LongCaptureLog.Error("v0.1.7 visual UI audit failed", ex);
            detail = ex.GetType().Name + ": " + ex.Message;
            return 79;
        }
    }

    private static void StyleHeader(Label? title, Label? subtitle, bool highContrast)
    {
        if (title is not null)
        {
            title.Height = 62;
            title.Padding = new Padding(24, 12, 24, 0);
            title.Font = new Font("Segoe UI Semibold", 24F, FontStyle.Regular, GraphicsUnit.Point);
            title.TextAlign = ContentAlignment.MiddleLeft;
            title.AccessibleName = "LongCapture title";
            if (!highContrast) title.ForeColor = Palette.Text;
        }

        if (subtitle is not null)
        {
            subtitle.Text = $"Reliable long screenshots · Browser Assisted adaptive quality · Recipes · UI v{ExperienceVersion}";
            subtitle.AutoSize = true;
            subtitle.AutoEllipsis = false;
            subtitle.Padding = new Padding(26, 0, 24, 12);
            subtitle.MinimumSize = new Size(0, 38);
            subtitle.Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
            subtitle.AccessibleName = "LongCapture subtitle";
            if (!highContrast) subtitle.ForeColor = Palette.TextMuted;
        }
    }

    private static void StyleStatus(Label? status, bool highContrast)
    {
        if (status is null) return;
        status.AutoSize = true;
        status.AutoEllipsis = false;
        status.MinimumSize = new Size(0, 48);
        status.Padding = new Padding(24, 12, 24, 12);
        status.Font = new Font("Segoe UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point);
        status.AccessibleName = "LongCapture status";
        if (!highContrast)
        {
            status.BackColor = Palette.Surface;
            status.ForeColor = Palette.TextMuted;
            status.Paint += (_, e) =>
            {
                using var pen = new Pen(Palette.Border);
                e.Graphics.DrawLine(pen, 0, 0, status.ClientSize.Width, 0);
            };
        }
    }

    private static void StyleTargetStrip(TableLayoutPanel targetStrip, bool highContrast)
    {
        targetStrip.SuspendLayout();
        try
        {
            targetStrip.AutoSize = true;
            targetStrip.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            targetStrip.GrowStyle = TableLayoutPanelGrowStyle.AddRows;
            targetStrip.Padding = new Padding(24, 10, 24, 12);
            targetStrip.MinimumSize = new Size(0, 58);
            foreach (RowStyle row in targetStrip.RowStyles)
            {
                row.SizeType = SizeType.AutoSize;
                row.Height = 0;
            }

            if (!highContrast)
            {
                targetStrip.BackColor = Palette.Surface;
                targetStrip.ForeColor = Palette.Text;
                targetStrip.Paint += (_, e) =>
                {
                    using var pen = new Pen(Palette.Border);
                    e.Graphics.DrawLine(pen, 0, targetStrip.ClientSize.Height - 1, targetStrip.ClientSize.Width, targetStrip.ClientSize.Height - 1);
                };
            }

            FlowLayoutPanel? speedBar = FindSpeedBar(targetStrip);
            if (speedBar is not null)
            {
                speedBar.AutoSize = true;
                speedBar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                speedBar.WrapContents = true;
                speedBar.FlowDirection = FlowDirection.LeftToRight;
                speedBar.Dock = DockStyle.Fill;
                speedBar.Padding = new Padding(0, 4, 0, 0);
                speedBar.Margin = new Padding(0, 4, 0, 0);
                speedBar.MinimumSize = new Size(0, 42);
            }
        }
        finally
        {
            targetStrip.ResumeLayout(true);
        }
    }

    private static RoundedSurfacePanelV017? WrapBodyInSurface(Form form, TableLayoutPanel body, bool highContrast)
    {
        if (body.Parent is RoundedSurfacePanelV017 existing) return existing;
        if (body.Parent is not Form parent || !ReferenceEquals(parent, form)) return null;

        int index = form.Controls.GetChildIndex(body);
        form.Controls.Remove(body);

        var gutter = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 14, 20, 18),
            Margin = Padding.Empty,
            BackColor = highContrast ? SystemColors.Control : Palette.Canvas
        };

        var surface = new RoundedSurfacePanelV017
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = new Padding(1),
            SurfaceColor = highContrast ? SystemColors.Window : Palette.Surface,
            BorderColor = highContrast ? SystemColors.WindowText : Palette.Border,
            CanvasColor = highContrast ? SystemColors.Control : Palette.Canvas,
            CornerRadius = 12
        };

        body.Dock = DockStyle.Fill;
        surface.Controls.Add(body);
        gutter.Controls.Add(surface);
        form.Controls.Add(gutter);
        form.Controls.SetChildIndex(gutter, Math.Min(index, form.Controls.Count - 1));
        return surface;
    }

    private static RoundedSurfacePanelV017? FindBodyHost(TableLayoutPanel body) =>
        body.Parent as RoundedSurfacePanelV017;

    private static void StyleBody(TableLayoutPanel body, bool highContrast)
    {
        body.AutoScroll = true;
        body.Padding = new Padding(24, 20, 24, 20);
        body.GrowStyle = TableLayoutPanelGrowStyle.FixedSize;
        body.Margin = Padding.Empty;
        if (!highContrast)
        {
            body.BackColor = Palette.Surface;
            body.ForeColor = Palette.Text;
        }

        for (int i = 0; i < body.RowStyles.Count; i++)
        {
            body.RowStyles[i].SizeType = SizeType.AutoSize;
            body.RowStyles[i].Height = 0;
        }

        foreach (Control control in body.Controls)
        {
            control.Margin = new Padding(4, 6, 4, 6);
            if (control is Label label)
            {
                label.AutoEllipsis = false;
                if (body.GetColumn(label) == 0 && body.GetColumnSpan(label) == 1)
                {
                    label.Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Regular, GraphicsUnit.Point);
                    if (!highContrast) label.ForeColor = Palette.TextMuted;
                }
            }
            else if (control is ComboBox or NumericUpDown or TextBox)
            {
                control.MinimumSize = new Size(0, 36);
            }
        }
    }

    private static void StyleControlTree(Control root, bool highContrast)
    {
        foreach (Control control in Descendants(root))
        {
            switch (control)
            {
                case Button button:
                    StyleButton(button, highContrast, primary: IsPrimaryCaptureButton(button));
                    break;
                case ComboBox combo:
                    combo.FlatStyle = FlatStyle.Flat;
                    combo.MinimumSize = new Size(combo.MinimumSize.Width, 36);
                    if (!highContrast)
                    {
                        combo.BackColor = Palette.Surface;
                        combo.ForeColor = Palette.Text;
                    }
                    break;
                case NumericUpDown numeric:
                    numeric.BorderStyle = BorderStyle.FixedSingle;
                    numeric.MinimumSize = new Size(numeric.MinimumSize.Width, 36);
                    if (!highContrast)
                    {
                        numeric.BackColor = Palette.Surface;
                        numeric.ForeColor = Palette.Text;
                    }
                    break;
                case TextBox text:
                    text.BorderStyle = BorderStyle.FixedSingle;
                    text.MinimumSize = new Size(text.MinimumSize.Width, 36);
                    if (!highContrast)
                    {
                        text.BackColor = Palette.Surface;
                        text.ForeColor = Palette.Text;
                    }
                    break;
                case CheckBox check:
                    check.AutoSize = true;
                    check.Padding = new Padding(2, 5, 2, 5);
                    if (!highContrast) check.ForeColor = Palette.Text;
                    break;
                case Label label:
                    if (!highContrast && bodyLikeValueLabel(label)) label.ForeColor = Palette.TextMuted;
                    break;
            }
        }

        static bool bodyLikeValueLabel(Label label) =>
            label.Font.Size <= 10.5F && !string.Equals(label.AccessibleName, "LongCapture title", StringComparison.Ordinal);
    }

    private static bool IsPrimaryCaptureButton(Button button) =>
        button.Text.StartsWith("Start long capture", StringComparison.OrdinalIgnoreCase) ||
        button.Text.StartsWith("Stop capture", StringComparison.OrdinalIgnoreCase) ||
        button.Text.StartsWith("Start Browser Assisted Capture", StringComparison.OrdinalIgnoreCase) ||
        button.Text.StartsWith("Stop Browser Assisted Capture", StringComparison.OrdinalIgnoreCase);

    private static void StyleButton(Button button, bool highContrast, bool primary)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.Padding = primary ? new Padding(18, 8, 18, 8) : new Padding(12, 6, 12, 6);
        button.MinimumSize = new Size(button.MinimumSize.Width, primary ? 48 : 36);
        if (!primary && button.Dock != DockStyle.Fill) button.AutoSize = true;

        if (!highContrast)
        {
            if (primary)
            {
                button.BackColor = Palette.Accent;
                button.ForeColor = Color.White;
                button.FlatAppearance.BorderColor = Palette.Accent;
                button.FlatAppearance.MouseOverBackColor = Palette.AccentHover;
                button.FlatAppearance.MouseDownBackColor = Palette.AccentPressed;
            }
            else
            {
                button.BackColor = Palette.Surface;
                button.ForeColor = Palette.Text;
                button.FlatAppearance.BorderColor = Palette.Border;
                button.FlatAppearance.MouseOverBackColor = Palette.SurfaceMuted;
                button.FlatAppearance.MouseDownBackColor = Color.FromArgb(239, 242, 246);
            }
            button.FlatAppearance.BorderSize = 1;
        }

        int radius = primary ? 8 : 6;
        void Round() => ApplyRoundedRegion(button, radius);
        button.SizeChanged += (_, _) => Round();
        Round();
    }

    private static void ConfigureReadiness(FlowLayoutPanel? panel)
    {
        if (panel is null) return;
        panel.WrapContents = true;
        panel.AutoSize = true;
        panel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        panel.Padding = new Padding(0, 1, 0, 1);
        panel.MinimumSize = new Size(0, 48);
        panel.FlowDirection = FlowDirection.LeftToRight;

        Button? probe = panel.Controls.OfType<Button>().FirstOrDefault();
        if (probe is not null)
        {
            probe.AutoSize = true;
            probe.MinimumSize = new Size(132, 36);
            probe.Margin = new Padding(0, 0, 10, 0);
        }

        Label? readiness = panel.Controls.OfType<Label>().FirstOrDefault();
        if (readiness is not null)
        {
            readiness.AutoSize = true;
            readiness.AutoEllipsis = false;
            readiness.Margin = new Padding(0, 7, 0, 5);
            readiness.Padding = Padding.Empty;
        }
    }

    private static void ConfigureRecipeActions(FlowLayoutPanel? panel)
    {
        if (panel is null) return;
        panel.WrapContents = true;
        panel.AutoSize = true;
        panel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        panel.Padding = new Padding(0, 1, 0, 1);
        panel.MinimumSize = new Size(0, 46);
        foreach (Button button in panel.Controls.OfType<Button>())
        {
            button.AutoSize = true;
            button.MinimumSize = new Size(button.Text.StartsWith("Review", StringComparison.OrdinalIgnoreCase) ? 142 : 122, 36);
            button.Margin = new Padding(0, 0, 10, 0);
        }
    }

    private static void ConfigureSimpleRows(TableLayoutPanel body)
    {
        if (body.GetControlFromPosition(1, 1) is Button browserButton)
        {
            browserButton.AutoSize = true;
            browserButton.MinimumSize = new Size(184, 36);
        }

        if (body.GetControlFromPosition(1, 9) is CheckBox startPosition)
        {
            startPosition.AutoSize = true;
            startPosition.MinimumSize = new Size(0, 36);
        }

        if (body.GetControlFromPosition(1, 10) is Label output)
        {
            output.AutoSize = true;
            output.AutoEllipsis = false;
            output.Padding = new Padding(0, 8, 0, 8);
        }
    }

    private static void HardenActionPanel(Panel actionPanel, bool highContrast)
    {
        Button? capture = actionPanel.Controls.OfType<Button>().FirstOrDefault(IsPrimaryCaptureButton);
        Button? openOutput = actionPanel.Controls.OfType<Button>()
            .FirstOrDefault(x => !ReferenceEquals(x, capture));
        Label? quality = actionPanel.Controls.OfType<Label>().FirstOrDefault();

        if (capture is null || openOutput is null || quality is null)
        {
            actionPanel.MinimumSize = new Size(0, 156);
            return;
        }

        actionPanel.SuspendLayout();
        try
        {
            actionPanel.Controls.Clear();
            actionPanel.Padding = Padding.Empty;
            actionPanel.AutoSize = true;
            actionPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            actionPanel.MinimumSize = new Size(0, 128);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 2,
                Margin = Padding.Empty,
                Padding = new Padding(0, 10, 0, 4),
                GrowStyle = TableLayoutPanelGrowStyle.FixedSize
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            capture.Dock = DockStyle.Fill;
            capture.AutoSize = false;
            capture.Height = 54;
            capture.MinimumSize = new Size(0, 54);
            capture.Margin = new Padding(0, 0, 0, 12);
            StyleButton(capture, highContrast, primary: true);

            openOutput.Dock = DockStyle.Top;
            openOutput.AutoSize = true;
            openOutput.MinimumSize = new Size(152, 36);
            openOutput.Margin = new Padding(0, 0, 14, 0);
            StyleButton(openOutput, highContrast, primary: false);

            quality.Dock = DockStyle.Fill;
            quality.AutoSize = true;
            quality.AutoEllipsis = false;
            quality.MinimumSize = new Size(0, 36);
            quality.Margin = new Padding(0, 5, 0, 0);
            quality.TextAlign = ContentAlignment.MiddleLeft;
            if (!highContrast) quality.ForeColor = Palette.TextMuted;

            layout.Controls.Add(capture, 0, 0);
            layout.SetColumnSpan(capture, 2);
            layout.Controls.Add(openOutput, 0, 1);
            layout.Controls.Add(quality, 1, 1);
            actionPanel.Controls.Add(layout);
        }
        finally
        {
            actionPanel.ResumeLayout(true);
        }
    }

    private static void ApplyModeVisibility(TableLayoutPanel body, ComboBox modeSelector)
    {
        string mode = modeSelector.SelectedItem?.ToString() ?? string.Empty;
        bool normal = mode.StartsWith("Normal Long Capture", StringComparison.OrdinalIgnoreCase);
        bool browserAgent = mode.StartsWith("Browser Assisted Capture", StringComparison.OrdinalIgnoreCase);
        bool runRecipe = mode.StartsWith("Run Recipe", StringComparison.OrdinalIgnoreCase);

        // Progressive disclosure: do not make users scan disabled browser/recipe controls
        // that cannot do anything in the current mode. Row 4 remains visible in Browser
        // Assisted because v0.1.5+ dynamically replaces it with Repair precision.
        SetRowVisible(body, 1, !normal);
        SetRowVisible(body, 3, runRecipe);
        SetRowVisible(body, 4, browserAgent || runRecipe);
    }

    private static void SetRowVisible(TableLayoutPanel body, int row, bool visible)
    {
        foreach (Control control in body.Controls.Cast<Control>().Where(x => body.GetRow(x) == row))
        {
            control.Visible = visible;
        }
        if (row >= 0 && row < body.RowStyles.Count)
        {
            body.RowStyles[row].SizeType = SizeType.AutoSize;
            body.RowStyles[row].Height = visible ? 0 : 0;
        }
    }

    private static void ReflowModernLayout(
        Form form,
        TableLayoutPanel body,
        TableLayoutPanel? targetStrip,
        FlowLayoutPanel? readinessPanel,
        Panel? actionPanel,
        Label? subtitle,
        Label? status,
        RoundedSurfacePanelV017? bodyHost)
    {
        if (body.IsDisposed || form.IsDisposed) return;

        int maxLabel = body.Controls.OfType<Label>()
            .Where(x => body.GetColumn(x) == 0 && body.GetColumnSpan(x) == 1 && x.Visible)
            .Select(x => x.GetPreferredSize(Size.Empty).Width)
            .DefaultIfEmpty(140)
            .Max();
        int labelColumn = Math.Clamp(maxLabel + 20, 145, 220);
        int maxByWindow = Math.Max(145, (int)Math.Round(body.ClientSize.Width * 0.28));
        labelColumn = Math.Min(labelColumn, maxByWindow);
        if (body.ColumnStyles.Count > 0)
        {
            body.ColumnStyles[0].SizeType = SizeType.Absolute;
            body.ColumnStyles[0].Width = labelColumn;
        }

        int contentWidth = Math.Max(220, body.ClientSize.Width - body.Padding.Horizontal - labelColumn - 18);

        if (readinessPanel is not null && !readinessPanel.IsDisposed)
        {
            Button? probe = readinessPanel.Controls.OfType<Button>().FirstOrDefault();
            Label? readiness = readinessPanel.Controls.OfType<Label>().FirstOrDefault();
            if (readiness is not null)
            {
                int probeWidth = probe?.PreferredSize.Width ?? 132;
                int oneLineAvailable = contentWidth - probeWidth - 18;
                int width = oneLineAvailable >= 260 ? oneLineAvailable : contentWidth;
                readiness.MaximumSize = new Size(Math.Max(180, width), 0);
                Size preferred = readiness.GetPreferredSize(new Size(Math.Max(180, width), 0));
                int probeHeight = probe?.PreferredSize.Height ?? 36;
                readinessPanel.MinimumSize = new Size(0, Math.Max(48, Math.Max(probeHeight, preferred.Height) + 8));
            }
        }

        if (body.GetControlFromPosition(1, 10) is Label output)
        {
            output.MaximumSize = new Size(contentWidth, 0);
        }

        if (actionPanel is not null && !actionPanel.IsDisposed)
        {
            Button? openOutput = Descendants(actionPanel).OfType<Button>()
                .FirstOrDefault(x => !IsPrimaryCaptureButton(x));
            Label? quality = Descendants(actionPanel).OfType<Label>().FirstOrDefault();
            if (quality is not null)
            {
                int openWidth = openOutput?.PreferredSize.Width ?? 152;
                int width = Math.Max(220, body.ClientSize.Width - body.Padding.Horizontal - openWidth - 40);
                quality.MaximumSize = new Size(width, 0);
                Size preferred = quality.GetPreferredSize(new Size(width, 0));
                actionPanel.MinimumSize = new Size(0, Math.Max(128, 98 + preferred.Height));
            }
        }

        if (targetStrip is not null && !targetStrip.IsDisposed)
        {
            foreach (RowStyle row in targetStrip.RowStyles)
            {
                row.SizeType = SizeType.AutoSize;
                row.Height = 0;
            }

            FlowLayoutPanel? speedBar = FindSpeedBar(targetStrip);
            if (speedBar is not null)
            {
                speedBar.WrapContents = true;
                speedBar.AutoSize = true;
                speedBar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                int available = Math.Max(260, targetStrip.ClientSize.Width - targetStrip.Padding.Horizontal - 8);
                speedBar.MaximumSize = new Size(available, 0);
                Label? speedHint = speedBar.Controls.OfType<Label>()
                    .FirstOrDefault(x => x.Text.StartsWith("Fixed", StringComparison.OrdinalIgnoreCase) ||
                                         x.Text.StartsWith("Target", StringComparison.OrdinalIgnoreCase));
                if (speedHint is not null)
                {
                    speedHint.AutoSize = true;
                    speedHint.AutoEllipsis = false;
                    speedHint.MaximumSize = new Size(available, 0);
                    speedHint.Margin = new Padding(0, 6, 0, 4);
                    if (!SystemInformation.HighContrast) speedHint.ForeColor = Palette.TextMuted;
                }
            }
        }

        if (subtitle is not null)
        {
            subtitle.MaximumSize = new Size(Math.Max(300, form.ClientSize.Width - 48), 0);
        }
        if (status is not null)
        {
            status.MaximumSize = new Size(Math.Max(300, form.ClientSize.Width - 48), 0);
        }

        bodyHost?.Invalidate();
    }

    private static bool ValidateTargetStrip(TableLayoutPanel targetStrip, out string detail)
    {
        FlowLayoutPanel? speedBar = FindSpeedBar(targetStrip);
        if (speedBar is null)
        {
            detail = "Capture speed responsive bar not found";
            return false;
        }
        if (!speedBar.WrapContents || !speedBar.AutoSize)
        {
            detail = "Capture speed bar is not wrap/AutoSize responsive";
            return false;
        }

        Rectangle display = speedBar.DisplayRectangle;
        foreach (Control control in speedBar.Controls)
        {
            if (!control.Visible) continue;
            if (control.Bounds.Right > display.Right + 3 || control.Bounds.Bottom > display.Bottom + 3)
            {
                detail = $"Capture speed child clipped: {control.GetType().Name} '{control.Text}' bounds={control.Bounds} display={display}";
                return false;
            }
        }

        Label? hint = speedBar.Controls.OfType<Label>()
            .FirstOrDefault(x => x.Text.StartsWith("Fixed", StringComparison.OrdinalIgnoreCase) ||
                                 x.Text.StartsWith("Target", StringComparison.OrdinalIgnoreCase));
        if (hint is not null && !ValidateLabelHeight(hint, "capture speed hint", out detail)) return false;

        detail = string.Empty;
        return true;
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

        if (!panel.WrapContents)
        {
            detail = "readiness panel is not allowed to wrap";
            return false;
        }

        Rectangle display = panel.DisplayRectangle;
        if (label.Bounds.Right > display.Right + 3 || label.Bounds.Bottom > display.Bottom + 3)
        {
            detail = $"readiness text clipped: label={label.Bounds}, display={display}";
            return false;
        }

        return ValidateLabelHeight(label, "readiness", out detail);
    }

    private static bool ValidatePrimaryActions(TableLayoutPanel body, out string detail)
    {
        if (body.GetControlFromPosition(0, 11) is not Panel panel)
        {
            detail = "primary action panel missing";
            return false;
        }

        Button? capture = Descendants(panel).OfType<Button>().FirstOrDefault(IsPrimaryCaptureButton);
        Button? open = Descendants(panel).OfType<Button>()
            .FirstOrDefault(x => x.Text.StartsWith("Open output folder", StringComparison.OrdinalIgnoreCase));
        Label? quality = Descendants(panel).OfType<Label>().FirstOrDefault();

        if (capture is null || open is null || quality is null)
        {
            detail = "primary actions were not rebuilt into the responsive action layout";
            return false;
        }
        if (capture.Bounds.Height < 52 || open.Bounds.Height < 34)
        {
            detail = "primary action button height is too small";
            return false;
        }
        if (!SystemInformation.HighContrast && capture.BackColor != Palette.Accent)
        {
            detail = "primary capture action is not visually distinguished";
            return false;
        }
        return ValidateLabelHeight(quality, "quality", out detail);
    }

    private static bool ValidateImportantLabels(Form form, TableLayoutPanel body, out string detail)
    {
        var labels = new List<(Label? Label, string Name)>
        {
            (body.GetControlFromPosition(1, 10) as Label, "output"),
            (FindSubtitle(form), "subtitle"),
            (FindStatus(form), "status")
        };

        foreach ((Label? label, string name) in labels)
        {
            if (label is null)
            {
                detail = $"{name} label missing";
                return false;
            }
            if (label.AutoEllipsis)
            {
                detail = $"{name} label still uses AutoEllipsis";
                return false;
            }
            if (!ValidateLabelHeight(label, name, out detail)) return false;
        }

        detail = string.Empty;
        return true;
    }

    private static bool ValidateLabelHeight(Label label, string name, out string detail)
    {
        int width = label.MaximumSize.Width > 0
            ? label.MaximumSize.Width
            : Math.Max(1, label.ClientSize.Width);
        Size preferred = label.GetPreferredSize(new Size(width, 0));
        if (label.ClientSize.Height + 3 < preferred.Height)
        {
            detail = $"{name} text clipped vertically: client={label.ClientSize}, preferred={preferred}";
            return false;
        }
        detail = string.Empty;
        return true;
    }

    private static bool ValidateButtons(Control root, out string detail)
    {
        foreach (Button button in Descendants(root).OfType<Button>())
        {
            if (!button.Visible || string.IsNullOrWhiteSpace(button.Text)) continue;
            Size measured = TextRenderer.MeasureText(
                button.Text,
                button.Font,
                Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            int horizontalPadding = Math.Max(12, button.Padding.Horizontal);
            int verticalPadding = Math.Max(8, button.Padding.Vertical);
            if (button.ClientSize.Width + 3 < measured.Width + horizontalPadding ||
                button.ClientSize.Height + 3 < measured.Height + verticalPadding)
            {
                detail = $"button text clipped: {button.Text} ({button.ClientSize.Width}x{button.ClientSize.Height}, text {measured.Width}x{measured.Height})";
                return false;
            }
        }
        detail = string.Empty;
        return true;
    }

    private static bool ValidateCheckBoxes(Control root, out string detail)
    {
        foreach (CheckBox check in Descendants(root).OfType<CheckBox>())
        {
            if (!check.Visible || string.IsNullOrWhiteSpace(check.Text)) continue;
            Size preferred = check.GetPreferredSize(Size.Empty);
            if (check.ClientSize.Width + 3 < preferred.Width || check.ClientSize.Height + 3 < preferred.Height)
            {
                detail = $"checkbox text clipped: {check.Text} client={check.ClientSize} preferred={preferred}";
                return false;
            }
        }
        detail = string.Empty;
        return true;
    }

    private static bool ValidateModernHierarchy(Form form, TableLayoutPanel body, out string detail)
    {
        if (!form.Text.Contains($"v{ExperienceVersion}", StringComparison.OrdinalIgnoreCase))
        {
            detail = "window title does not expose the v0.1.7 experience version";
            return false;
        }

        ComboBox? mode = body.GetControlFromPosition(1, 0) as ComboBox;
        if (mode is not null && (mode.SelectedItem?.ToString() ?? string.Empty).StartsWith("Normal", StringComparison.OrdinalIgnoreCase))
        {
            if (body.GetControlFromPosition(1, 1)?.Visible == true ||
                body.GetControlFromPosition(1, 3)?.Visible == true ||
                body.GetControlFromPosition(1, 4)?.Visible == true)
            {
                detail = "inactive Browser/Recipe rows are still visible in Normal mode";
                return false;
            }
        }

        detail = string.Empty;
        return true;
    }

    private static void BeginReflow(Control control, Action reflow)
    {
        if (control.IsHandleCreated) control.BeginInvoke(reflow);
        else reflow();
    }

    private static TableLayoutPanel? FindBody(Form form) =>
        Descendants(form).OfType<TableLayoutPanel>()
            .FirstOrDefault(x => x.ColumnCount == 2 && x.RowCount >= 12);

    private static TableLayoutPanel? FindTargetStrip(Form form, TableLayoutPanel body) =>
        Descendants(form).OfType<TableLayoutPanel>()
            .FirstOrDefault(x => !ReferenceEquals(x, body) && x.Controls.OfType<ComboBox>().Any());

    private static FlowLayoutPanel? FindSpeedBar(TableLayoutPanel targetStrip) =>
        Descendants(targetStrip).OfType<FlowLayoutPanel>()
            .FirstOrDefault(x => x.Controls.OfType<Button>().Any(b =>
                b.Text.Contains("benchmark", StringComparison.OrdinalIgnoreCase)));

    private static Label? FindSubtitle(Form form) =>
        Descendants(form).OfType<Label>()
            .FirstOrDefault(x => string.Equals(x.AccessibleName, "LongCapture subtitle", StringComparison.Ordinal)) ??
        Descendants(form).OfType<Label>()
            .FirstOrDefault(x => x.Text.Contains("Browser Assisted adaptive quality", StringComparison.OrdinalIgnoreCase));

    private static Label? FindStatus(Form form) =>
        Descendants(form).OfType<Label>()
            .FirstOrDefault(x => string.Equals(x.AccessibleName, "LongCapture status", StringComparison.Ordinal)) ??
        form.Controls.OfType<Label>().FirstOrDefault(x => x.Dock == DockStyle.Bottom);

    private static void ApplyRoundedRegion(Control control, int radius)
    {
        if (control.Width <= 1 || control.Height <= 1) return;
        int diameter = Math.Max(2, radius * 2);
        Rectangle bounds = new(0, 0, control.Width, control.Height);
        using var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        Region? old = control.Region;
        control.Region = new Region(path);
        old?.Dispose();
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }
}

internal sealed class RoundedSurfacePanelV017 : Panel
{
    public Color SurfaceColor { get; set; } = Color.White;
    public Color BorderColor { get; set; } = Color.Gainsboro;
    public Color CanvasColor { get; set; } = SystemColors.Control;
    public int CornerRadius { get; set; } = 12;

    public RoundedSurfacePanelV017()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        BackColor = Color.Transparent;
        Resize += (_, _) => UpdateRoundedRegion();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(CanvasColor);
        Rectangle rect = ClientRectangle;
        rect.Width = Math.Max(1, rect.Width - 1);
        rect.Height = Math.Max(1, rect.Height - 1);
        using GraphicsPath path = RoundedPath(rect, CornerRadius);
        using var brush = new SolidBrush(SurfaceColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Rectangle rect = ClientRectangle;
        rect.Width = Math.Max(1, rect.Width - 1);
        rect.Height = Math.Max(1, rect.Height - 1);
        using GraphicsPath path = RoundedPath(rect, CornerRadius);
        using var pen = new Pen(BorderColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawPath(pen, path);
    }

    private void UpdateRoundedRegion()
    {
        if (Width <= 1 || Height <= 1) return;
        Rectangle rect = ClientRectangle;
        using GraphicsPath path = RoundedPath(rect, CornerRadius);
        Region? old = Region;
        Region = new Region(path);
        old?.Dispose();
    }

    private static GraphicsPath RoundedPath(Rectangle rect, int radius)
    {
        int diameter = Math.Max(2, Math.Min(radius * 2, Math.Min(rect.Width, rect.Height)));
        var path = new GraphicsPath();
        path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}