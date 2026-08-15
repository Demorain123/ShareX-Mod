#nullable enable

using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModCaptureModeUiV075
{
    private static readonly ConditionalWeakTable<Window, object> Attached = new();

    public static void Attach(Window window)
    {
        try
        {
            if (Attached.TryGetValue(window, out _)) return;
            if (window.Content is not Control existing) return;

            ShareXModCaptureModeProfileData profile = ShareXModCaptureModeProfile.Current;

            ComboBox mode = new()
            {
                MinWidth = 190,
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
                },
                VerticalAlignment = VerticalAlignment.Center
            };

            TextBlock hint = new()
            {
                Text = Hint(profile.Mode),
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 350,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            };

            TextBlock readiness = new()
            {
                Text = "Checking…",
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 330,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            };

            TextBlock recipeInfo = new()
            {
                Text = ShareXModCaptureModeProfile.DescribeRecipe(),
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 260,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis
            };

            Button latestRecipe = new()
            {
                Content = "Use latest recipe",
                VerticalAlignment = VerticalAlignment.Center
            };

            Button reviewRecipe = new()
            {
                Content = "Review recipe",
                VerticalAlignment = VerticalAlignment.Center
            };

            Button openCaptureBrowser = new()
            {
                Content = "Open Capture Browser",
                VerticalAlignment = VerticalAlignment.Center
            };

            StackPanel modeRow = new()
            {
                Orientation = Orientation.Horizontal,
                Spacing = 9,
                Margin = new Thickness(10, 7),
                VerticalAlignment = VerticalAlignment.Center
            };

            modeRow.Children.Add(new TextBlock
            {
                Text = "Capture mode:",
                VerticalAlignment = VerticalAlignment.Center
            });
            modeRow.Children.Add(mode);
            modeRow.Children.Add(hint);
            modeRow.Children.Add(readiness);
            modeRow.Children.Add(openCaptureBrowser);
            modeRow.Children.Add(latestRecipe);
            modeRow.Children.Add(reviewRecipe);
            modeRow.Children.Add(recipeInfo);

            StackPanel shell = new()
            {
                Orientation = Orientation.Vertical,
                Spacing = 0
            };
            shell.Children.Add(modeRow);

            ShareXModRetentionPending? pending = ShareXModAppendixRetentionInbox.FindLatest();
            if (pending != null)
            {
                shell.Children.Add(BuildRetentionRow(pending));
            }

            Border header = new()
            {
                Child = shell,
                BorderThickness = new Thickness(0, 0, 0, 1)
            };

            DockPanel root = new();
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);
            root.Children.Add(existing);
            window.Content = root;
            Attached.Add(window, new object());

            string ResolveRecipePath()
            {
                string path = ShareXModCaptureModeProfile.Current.RecipePath;
                if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
                {
                    path = ShareXModCaptureModeProfile.FindLatestRecipe() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        ShareXModCaptureModeProfile.SetRecipePath(path);
                    }
                }
                return path;
            }

            void ApplyVisibility(ShareXModCaptureMode selected)
            {
                bool run = selected == ShareXModCaptureMode.RunRecipe;
                bool chromeMode = selected != ShareXModCaptureMode.Normal;
                latestRecipe.IsVisible = run;
                reviewRecipe.IsVisible = run;
                recipeInfo.IsVisible = run;
                openCaptureBrowser.IsVisible = chromeMode;
            }

            latestRecipe.Click += async (_, _) =>
            {
                string? latest = ShareXModCaptureModeProfile.FindLatestRecipe();
                recipeInfo.Text = string.IsNullOrWhiteSpace(latest)
                    ? "No recorded recipe yet"
                    : ShareXModCaptureModeProfile.SetRecipePath(latest).RecipePath;

                await RefreshReadinessAsync(
                    SelectedMode(mode.SelectedIndex),
                    readiness,
                    recipeInfo);
            };

            reviewRecipe.Click += async (_, _) =>
            {
                string path = ResolveRecipePath();
                if (string.IsNullOrWhiteSpace(path))
                {
                    readiness.Text = "Not ready · No recorded Recipe to review.";
                    return;
                }

                bool opened = ShareXModCaptureRecipeReviewWindowV080.TryOpen(window, path);
                readiness.Text = opened
                    ? "Review window opened. Save/approve there, then readiness will refresh."
                    : "Could not open Recipe review.";

                if (opened)
                {
                    await Task.Delay(250);
                }
            };

            openCaptureBrowser.Click += async (_, _) =>
            {
                openCaptureBrowser.IsEnabled = false;
                readiness.Text = "Starting a separate persistent Capture Browser profile…";

                ShareXModChromeDedicatedProfileResult result =
                    await ShareXModChromeDedicatedProfileLauncher.LaunchAsync(
                        ShareXModV04Settings.Load());

                readiness.Text = result.Started
                    ? "Capture Browser ready · navigate/sign in there, then use Record/Run."
                    : "Could not start Capture Browser · " + result.Detail;

                openCaptureBrowser.IsEnabled = true;

                if (result.Started)
                {
                    await Task.Delay(250);
                    await RefreshReadinessAsync(
                        SelectedMode(mode.SelectedIndex),
                        readiness,
                        recipeInfo);
                }
            };

            mode.SelectionChanged += async (_, _) =>
            {
                ShareXModCaptureMode selected = SelectedMode(mode.SelectedIndex);
                ShareXModCaptureModeProfileData updated =
                    ShareXModCaptureModeProfile.SetMode(selected);

                hint.Text = Hint(updated.Mode);
                recipeInfo.Text = ShareXModCaptureModeProfile.DescribeRecipe();
                ApplyVisibility(updated.Mode);

                await RefreshReadinessAsync(
                    updated.Mode,
                    readiness,
                    recipeInfo);
            };

            ApplyVisibility(profile.Mode);
            _ = RefreshReadinessAsync(profile.Mode, readiness, recipeInfo);
        }
        catch
        {
            // Convenience UI must never break the original Scrolling Capture window.
        }
    }

    private static StackPanel BuildRetentionRow(ShareXModRetentionPending pending)
    {
        TextBlock message = new()
        {
            Text = $"Embedded image originals: {pending.Files.Length} file(s) waiting for your choice.",
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };

        Button keep = new() { Content = "Keep originals" };
        Button delete = new() { Content = "Delete embedded originals" };

        StackPanel row = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(10, 4, 10, 8),
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(message);
        row.Children.Add(keep);
        row.Children.Add(delete);

        keep.Click += (_, _) =>
        {
            bool ok = ShareXModAppendixRetentionInbox.ResolveKeep(pending);
            message.Text = ok
                ? "Embedded originals kept."
                : "Could not update retention state.";
            keep.IsEnabled = false;
            delete.IsEnabled = false;
        };

        delete.Click += (_, _) =>
        {
            bool ok = ShareXModAppendixRetentionInbox.ResolveDelete(pending, out string result);
            message.Text = ok
                ? result
                : string.IsNullOrWhiteSpace(result)
                    ? "Could not safely delete every embedded original."
                    : result;
            keep.IsEnabled = false;
            delete.IsEnabled = false;
        };

        return row;
    }

    private static async Task RefreshReadinessAsync(
        ShareXModCaptureMode mode,
        TextBlock readiness,
        TextBlock recipeInfo)
    {
        try
        {
            string path = ShareXModCaptureModeProfile.Current.RecipePath;
            if (mode == ShareXModCaptureMode.RunRecipe &&
                (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)))
            {
                path = ShareXModCaptureModeProfile.FindLatestRecipe() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    ShareXModCaptureModeProfile.SetRecipePath(path);
                    recipeInfo.Text = ShareXModCaptureModeProfile.DescribeRecipe();
                }
            }

            ShareXModChromeReadinessResult status =
                await ShareXModChromeReadiness.ProbeAsync(
                    ShareXModV04Settings.Load(),
                    mode,
                    path);

            readiness.Text = status.Ready
                ? $"Ready · {status.Detail}"
                : $"Not ready · {status.Detail}";
        }
        catch
        {
            readiness.Text = "Readiness check failed.";
        }
    }

    private static ShareXModCaptureMode SelectedMode(int index) => index switch
    {
        1 => ShareXModCaptureMode.RecordRecipe,
        2 => ShareXModCaptureMode.RunRecipe,
        _ => ShareXModCaptureMode.Normal
    };

    private static string Hint(ShareXModCaptureMode mode) => mode switch
    {
        ShareXModCaptureMode.RecordRecipe => "Demonstrate what should be captured; actions become a semantic Recipe.",
        ShareXModCaptureMode.RunRecipe => "Run only after reviewing the selected Recipe; semantic actions are re-resolved before execution.",
        _ => "Original Start/Stop scrolling capture. No browser automation required."
    };
}
