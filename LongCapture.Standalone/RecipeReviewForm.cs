using ShareX.ScreenCaptureLib;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace LongCapture.Standalone;

internal sealed class RecipeReviewForm : Form
{
    private readonly LongCaptureRecipeReviewInfo info;
    private readonly CheckedListBox steps = new();
    private readonly Label approvalLabel = new();

    public RecipeReviewForm(LongCaptureRecipeReviewInfo info)
    {
        this.info = info;
        Text = "Review Capture Recipe";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(760, 560);
        Size = new Size(900, 680);
        Font = new Font("Segoe UI", 10F);

        var heading = new Label
        {
            Text = "Review before unattended replay",
            Dock = DockStyle.Top,
            Height = 52,
            Font = new Font("Segoe UI Semibold", 16F),
            Padding = new Padding(14, 12, 0, 0)
        };

        var summary = new Label
        {
            Text = info.Summary + "\r\n" + info.RecipePath,
            Dock = DockStyle.Top,
            Height = 64,
            Padding = new Padding(16, 4, 16, 4),
            AutoEllipsis = true
        };

        approvalLabel.Text = info.HasValidApproval && info.Approved
            ? "This exact recipe version is already approved. Saving again refreshes approval after any step changes."
            : "Review every step. Uncheck optional steps you do not want replayed, then approve this exact recipe version.";
        approvalLabel.Dock = DockStyle.Top;
        approvalLabel.Height = 54;
        approvalLabel.Padding = new Padding(16, 4, 16, 4);

        steps.Dock = DockStyle.Fill;
        steps.CheckOnClick = true;
        steps.HorizontalScrollbar = true;
        steps.ItemCheck += StepsOnItemCheck;

        foreach (LongCaptureRecipeStepInfo step in info.Steps)
        {
            steps.Items.Add(FormatStep(step), step.Enabled);
        }

        var approve = new Button
        {
            Text = "Approve recipe for Run Recipe",
            Dock = DockStyle.Right,
            Width = 250
        };
        approve.Click += (_, _) => Approve();

        var cancel = new Button
        {
            Text = "Cancel",
            Dock = DockStyle.Right,
            Width = 110
        };
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;

        var buttons = new Panel { Dock = DockStyle.Bottom, Height = 58, Padding = new Padding(12) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(approve);

        Controls.Add(steps);
        Controls.Add(approvalLabel);
        Controls.Add(summary);
        Controls.Add(heading);
        Controls.Add(buttons);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        CaptureExclusion.Apply(this, "recipe-review-form");
    }

    private void StepsOnItemCheck(object? sender, ItemCheckEventArgs e)
    {
        if (e.Index < 0 || e.Index >= info.Steps.Length) return;
        LongCaptureRecipeStepInfo step = info.Steps[e.Index];
        if (!step.CanDisable && e.NewValue != CheckState.Checked)
        {
            e.NewValue = CheckState.Checked;
            BeginInvoke(new Action(() =>
                MessageBox.Show(this,
                    "This checkpoint is required for fail-closed replay and cannot be disabled.",
                    "Required recipe step",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information)));
        }
    }

    private void Approve()
    {
        var disabled = new List<int>();
        for (int i = 0; i < info.Steps.Length; i++)
        {
            if (info.Steps[i].CanDisable && !steps.GetItemChecked(i))
            {
                disabled.Add(info.Steps[i].Index);
            }
        }

        LongCaptureRecipeReviewInfo? saved = LongCaptureStandaloneBridge.SaveRecipeReview(
            info.RecipePath,
            disabled,
            approved: true,
            note: "Approved in LongCapture Standalone");

        if (saved is null || !saved.HasValidApproval || !saved.Approved)
        {
            MessageBox.Show(this,
                "The approval file could not be written or verified. The recipe will remain blocked from unattended replay.",
                "Recipe approval failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private static string FormatStep(LongCaptureRecipeStepInfo step)
    {
        string optional = step.CanDisable ? "optional" : "required";
        return $"[{step.Risk}] {step.Title} — {step.Detail} ({optional})";
    }
}
