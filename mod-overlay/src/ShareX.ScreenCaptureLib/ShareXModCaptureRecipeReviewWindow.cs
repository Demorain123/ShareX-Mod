#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace ShareX.ScreenCaptureLib;

internal sealed class ShareXModCaptureRecipeReviewWindow : Window
{
    private readonly string recipePath;
    private readonly Dictionary<int, CheckBox> stepChecks = new();
    private readonly TextBlock status;

    private ShareXModCaptureRecipeReviewWindow(
        ShareXModRecipeReviewSnapshot snapshot)
    {
        recipePath = snapshot.RecipePath;

        Title = "Review Capture Recipe";
        Width = 760;
        Height = 680;
        MinWidth = 560;
        MinHeight = 420;

        TextBlock summary = new()
        {
            Text = snapshot.Summary,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(12, 10, 12, 4)
        };

        TextBlock explanation = new()
        {
            Text = snapshot.DynamicFeed
                ? "Dynamic feed detected. Disabled Next Page steps stop safely; other unchecked steps are skipped. Approval is tied to this exact Recipe file."
                : "Uncheck steps you do not want unattended automation to execute. Page checks are required and cannot be disabled. Approval is tied to this exact Recipe file.",
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

        ScrollViewer scroll = new()
        {
            Content = stepPanel,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };

        status = new TextBlock
        {
            Text = snapshot.HasValidApproval && snapshot.Approved
                ? "Approved for unattended run."
                : "Not yet approved for unattended run.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
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
            ShareXModCaptureRecipeReviewWindow window = new(snapshot);
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

            status.Text = updated == null
                ? "Could not save Recipe review."
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
