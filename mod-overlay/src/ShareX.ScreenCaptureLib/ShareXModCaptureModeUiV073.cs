#nullable enable

using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModCaptureModeUiV073
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
                MaxWidth = 390,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            };

            TextBlock readiness = new()
            {
                Text = "Checking…",
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 360,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            };

            TextBlock recipeInfo = new()
            {
                Text = ShareXModCaptureModeProfile.DescribeRecipe(),
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 300,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis
            };

            Button latestRecipe = new()
            {
                Content = "Use latest recipe",
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
                Spacing = 10,
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

            void ApplyVisibility(ShareXModCaptureMode selected)
            {
                bool run = selected == ShareXModCaptureMode.RunRecipe;
                bool chromeMode = selected != ShareXModCaptureMode.Normal;
                latestRecipe.IsVisible = run;
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
                ? "Original image files kept."
                : "Could not resolve the request; originals were left untouched.";
            keep.IsVisible = false;
            delete.IsVisible = false;
        };

        delete.Click += (_, _) =>
        {
            bool ok = ShareXModAppendixRetentionInbox.ResolveDelete(pending, out string result);
            message.Text = ok
                ? result
                : "Delete refused/failed; originals were left untouched. " + result;
            keep.IsVisible = false;
            delete.IsVisible = false;
        };

        return row;
    }

    private static async Task RefreshReadinessAsync(
        ShareXModCaptureMode selected,
        TextBlock readiness,
        TextBlock recipeInfo)
    {
        readiness.Text = selected == ShareXModCaptureMode.Normal
            ? "Ready"
            : "Checking Chrome / Recipe…";

        ShareXModCaptureModeReadinessResult result =
            await ShareXModCaptureModeReadiness.ProbeAsync(selected);

        readiness.Text = result.Ready
            ? "Ready · " + result.Message
            : "Not ready · " + result.Message;

        if (selected == ShareXModCaptureMode.RunRecipe &&
            !string.IsNullOrWhiteSpace(result.RecipePath))
        {
            recipeInfo.Text = result.RecipePath;
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
        ShareXModCaptureMode.RecordRecipe =>
            "Demonstrate once; semantic intent is recorded instead of wheel/mouse macro replay.",
        ShareXModCaptureMode.RunRecipe =>
            "Run the latest semantic Recipe; manual Stop and safety limits remain active.",
        _ =>
            "Original ShareX Start/Stop flow; partial-page capture remains the default."
    };
}
