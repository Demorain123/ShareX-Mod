#nullable enable

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModCaptureModeUi
{
    private const string AttachedMarker = "ShareXModCaptureModeUiAttached";

    public static void Attach(Window window)
    {
        try
        {
            if (window.Tag is string marker && marker.Contains(AttachedMarker, StringComparison.Ordinal))
            {
                return;
            }

            if (window.Content is not Control existing)
            {
                return;
            }

            ShareXModCaptureModeProfileData profile = ShareXModCaptureModeProfile.Current;

            TextBlock recipeInfo = new()
            {
                Text = ShareXModCaptureModeProfile.DescribeRecipe(),
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 360,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis
            };

            ComboBox mode = new()
            {
                MinWidth = 180,
                HorizontalAlignment = HorizontalAlignment.Left,
                ItemsSource = new[]
                {
                    "Normal scrolling capture",
                    "Record Capture Recipe",
                    "Run latest Capture Recipe"
                },
                SelectedIndex = profile.Mode switch
                {
                    ShareXModCaptureMode.RecordRecipe => 1,
                    ShareXModCaptureMode.RunRecipe => 2,
                    _ => 0
                }
            };

            TextBlock hint = new()
            {
                Text = Hint(profile.Mode),
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 520,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            };

            Button refreshRecipe = new()
            {
                Content = "Use latest recipe",
                VerticalAlignment = VerticalAlignment.Center
            };

            refreshRecipe.Click += (_, _) =>
            {
                string? latest = ShareXModCaptureModeProfile.FindLatestRecipe();
                if (!string.IsNullOrWhiteSpace(latest))
                {
                    ShareXModCaptureModeProfile.SetRecipePath(latest);
                    recipeInfo.Text = ShareXModCaptureModeProfile.DescribeRecipe();
                }
                else
                {
                    recipeInfo.Text = "No recorded recipe yet";
                }
            };

            mode.SelectionChanged += (_, _) =>
            {
                ShareXModCaptureMode selected = mode.SelectedIndex switch
                {
                    1 => ShareXModCaptureMode.RecordRecipe,
                    2 => ShareXModCaptureMode.RunRecipe,
                    _ => ShareXModCaptureMode.Normal
                };

                ShareXModCaptureModeProfileData updated =
                    ShareXModCaptureModeProfile.SetMode(selected);

                hint.Text = Hint(updated.Mode);
                recipeInfo.Text = ShareXModCaptureModeProfile.DescribeRecipe();
                refreshRecipe.IsVisible = updated.Mode == ShareXModCaptureMode.RunRecipe;
                recipeInfo.IsVisible = updated.Mode == ShareXModCaptureMode.RunRecipe;
            };

            refreshRecipe.IsVisible = profile.Mode == ShareXModCaptureMode.RunRecipe;
            recipeInfo.IsVisible = profile.Mode == ShareXModCaptureMode.RunRecipe;

            StackPanel bar = new()
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Margin = new Thickness(10, 8),
                VerticalAlignment = VerticalAlignment.Center
            };

            bar.Children.Add(new TextBlock
            {
                Text = "Capture mode:",
                VerticalAlignment = VerticalAlignment.Center
            });
            bar.Children.Add(mode);
            bar.Children.Add(hint);
            bar.Children.Add(refreshRecipe);
            bar.Children.Add(recipeInfo);

            Border header = new()
            {
                Child = bar,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(2)
            };

            DockPanel root = new();
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);
            root.Children.Add(existing);
            window.Content = root;

            window.Tag = string.IsNullOrWhiteSpace(window.Tag?.ToString())
                ? AttachedMarker
                : window.Tag + ";" + AttachedMarker;
        }
        catch
        {
            // UI convenience must never prevent the existing scrolling-capture window from opening.
        }
    }

    private static string Hint(ShareXModCaptureMode mode) => mode switch
    {
        ShareXModCaptureMode.RecordRecipe =>
            "Demonstrate the content/actions once; scrolling is normalized into capture intent.",

        ShareXModCaptureMode.RunRecipe =>
            "Runs the most recent semantic Recipe in the background; manual Stop still works.",

        _ =>
            "Original ShareX Start/Stop behavior. Partial-page capture remains supported."
    };
}
