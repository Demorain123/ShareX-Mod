#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace ShareX.ScreenCaptureLib;

internal sealed class ShareXModCaptureRecipeReviewWindowV080 : Window
{
    private readonly string recipePath;
    private readonly Dictionary<int, CheckBox> stepChecks = new();
    private readonly TextBlock status;
    private readonly CheckBox? pageLoopCheck;
    private readonly TextBox? pageLoopMaxPages;

    private ShareXModCaptureRecipeReviewWindowV080(
        ShareXModRecipeReviewSnapshot snapshot,
        ShareXModPageLoopReviewInfo loopInfo)
    {
        recipePath = snapshot.RecipePath;

        Title = "Review Capture Recipe";
        Width = 800;
        Height = 720;
        MinWidth = 580;
        MinHeight = 440;

        TextBlock summary = new()
        {
            Text = snapshot.Summary,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(12, 10, 12, 4)
        };

        TextBlock explanation = new()
        {
            Text = "Uncheck steps you do not want unattended automation to execute. Page checks are required. A disabled Next Page step safely stops before that transition. Approval is invalidated when the Recipe changes.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(12, 0, 12, 8)
        };

        StackPanel stepPanel = new()
        {
            Orientation = Orientation.Vertical,
            Spacing = 6,
            Margin = new Thickness(10)
        };

        foreach (ShareXModRecipeReviewStep step in snapshot.Steps)
        {
            CheckBox check = new()
            {
                IsChecked = step.Enabled,
                IsEnabled = step.CanDisable,
                Content = $"{step.Title}  [{step.Risk}]",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            TextBlock detail = new()
            {
                Text = step.Detail,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new Thickness(28, 0, 4, 4)
            };

            StackPanel item = new()
            {
                Orientation = Orientation.Vertical,
                Spacing = 1
            };
            item.Children.Add(check);
            item.Children.Add(detail);
            stepPanel.Children.Add(item);
            stepChecks[step.Index] = check;
        }

        if (loopInfo.Candidate)
        {
            pageLoopCheck = new CheckBox
            {
                IsChecked = loopInfo.Approved,
                Content = "Repeat this simple page template until Next Page disappears / stops changing"
            };

            pageLoopMaxPages = new TextBox
            {
                Text = loopInfo.MaxPages.ToString(),
                Width = 90,
                Watermark = "Max pages"
            };

            StackPanel loopRow = new()
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(8, 12, 8, 4)
            };
            loopRow.Children.Add(pageLoopCheck);
            loopRow.Children.Add(new TextBlock
            {
                Text = "Max pages:",
                VerticalAlignment = VerticalAlignment.Center
            });
            loopRow.Children.Add(pageLoopMaxPages);

            TextBlock loopDetail = new()
            {
                Text = $"Loop candidate: {loopInfo.CaptureRangeCount} vertical range(s) + semantic Next Page. This is separately approved and still obeys manual Stop, transition verification, Page Guard and cycle detection.",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new Thickness(28, 0, 8, 8)
            };

            stepPanel.Children.Add(loopRow);
            stepPanel.Children.Add(loopDetail);
        }
        else
        {
            TextBlock loopUnavailable = new()
            {
                Text = "Page template loop: not offered for this Recipe · " + loopInfo.Reason,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new Thickness(8, 12, 8, 4)
            };
            stepPanel.Children.Add(loopUnavailable);
        }

        ScrollViewer scroll = new()
        {
            Content = stepPanel,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };

        status = new TextBlock
        {
            Text = snapshot.HasValidApproval && snapshot.Approved
                ? "Recipe approved for unattended run."
                : "Recipe not yet approved for unattended run.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 340
        };

        Button approve = new()
        {
            Content = "Approve & save",
            MinWidth = 120
        };

        Button revoke = new()
        {
            Content = "Save but do not approve",
            MinWidth = 150
        };

        Button close = new()
        {
            Content = "Close",
            MinWidth = 80
        };

        approve.Click += (_, _) => Save(approved: true);
        revoke.Click += (_, _) => Save(approved: false);
        close.Click += (_, _) => Close();

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(12, 8),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(status);
        buttons.Children.Add(revoke);
        buttons.Children.Add(approve);
        buttons.Children.Add(close);

        DockPanel root = new();
        DockPanel.SetDock(summary, Dock.Top);
        DockPanel.SetDock(explanation, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(summary);
        root.Children.Add(explanation);
        root.Children.Add(buttons);
        root.Children.Add(scroll);

        Content = root;
    }

    public static bool TryOpen(Window owner, string? recipePath)
    {
        ShareXModRecipeReviewSnapshot? snapshot =
            ShareXModCaptureRecipeReviewService.Load(recipePath);

        if (snapshot == null)
        {
            return false;
        }

        try
        {
            ShareXModPageLoopReviewInfo loopInfo =
                ShareXModCaptureRecipePageLoopReview.Inspect(
                    snapshot.RecipePath,
                    ShareXModV04Settings.Load());

            ShareXModCaptureRecipeReviewWindowV080 window =
                new(snapshot, loopInfo);
            window.Show(owner);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void Save(bool approved)
    {
        try
        {
            int[] disabled = stepChecks
                .Where(x => x.Value.IsEnabled && x.Value.IsChecked != true)
                .Select(x => x.Key)
                .OrderBy(x => x)
                .ToArray();

            ShareXModRecipeReviewSnapshot? updated =
                ShareXModCaptureRecipeReviewService.Save(
                    recipePath,
                    disabled,
                    approved);

            bool loopSaved = true;
            if (pageLoopCheck != null && pageLoopMaxPages != null)
            {
                bool loopApproved = pageLoopCheck.IsChecked == true;
                int maxPages = int.TryParse(pageLoopMaxPages.Text, out int parsed)
                    ? Math.Clamp(parsed, 1, 10000)
                    : Math.Clamp(ShareXModV04Settings.Load().CaptureRecipePageLoopMaxPages, 1, 10000);

                loopSaved = ShareXModCaptureRecipePageLoopApproval.Save(
                    recipePath,
                    loopApproved && approved,
                    maxPages);
            }

            status.Text = updated == null || !loopSaved
                ? "Could not save all Recipe review settings."
                : updated.Approved
                    ? $"Approved · {updated.DisabledSteps.Length} disabled step(s)."
                    : $"Saved · not approved · {updated.DisabledSteps.Length} disabled step(s).";
        }
        catch (Exception ex)
        {
            status.Text = "Could not save Recipe review: " + ex.GetType().Name;
        }
    }
}
