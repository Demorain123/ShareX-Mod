using System;
using System.Drawing;
using System.Windows.Forms;

namespace LongCapture.Standalone;

/// <summary>
/// Release-candidate checks that must exercise the standalone shell itself rather than a
/// test-only copy of the logic. The stress runner loads the published LongCapture.exe and
/// invokes this suite before repeating the real F8-style capture smoke path.
/// </summary>
internal static class LongCaptureRcSelfTests
{
    public static string RunOrThrow()
    {
        TestTargetLifecycleAndRecovery();
        TestScaledLayoutResilience();
        return "LongCapture RC shell self-tests passed: target loss/minimize/resize recovery and 100-200% scaled-font layout resilience.";
    }

    private static void TestTargetLifecycleAndRecovery()
    {
        using var target = new Form
        {
            Text = "LongCapture RC lifecycle target",
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(180, 160, 520, 360),
            BackColor = Color.White,
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.SizableToolWindow
        };
        target.Controls.Add(new Label
        {
            Text = "LongCapture target lifecycle fixture",
            AutoSize = true,
            Location = new Point(20, 20)
        });
        target.Show();
        target.Refresh();
        Application.DoEvents();

        if (!CaptureTargetService.TryCreateTarget(target.Handle, out CaptureTargetDescriptor? descriptor, out string firstDetail) || descriptor is null)
        {
            throw new InvalidOperationException("RC target lifecycle fixture could not create its initial target: " + firstDetail);
        }

        Rectangle initial = descriptor.Bounds;
        target.Size = new Size(target.Width + 96, target.Height + 72);
        target.Refresh();
        Application.DoEvents();

        if (!CaptureTargetService.TryRefreshTarget(descriptor, out CaptureTargetDescriptor? resized, out string resizeDetail) || resized is null)
        {
            throw new InvalidOperationException("RC target lifecycle fixture could not refresh after resize: " + resizeDetail);
        }

        if (resized.Bounds.Width <= initial.Width || resized.Bounds.Height <= initial.Height)
        {
            throw new InvalidOperationException(
                $"RC target refresh retained stale geometry after resize: before={initial}, after={resized.Bounds}.");
        }

        target.WindowState = FormWindowState.Minimized;
        Application.DoEvents();
        if (CaptureTargetService.TryRefreshTarget(resized, out _, out string minimizedDetail))
        {
            throw new InvalidOperationException("RC target refresh unexpectedly accepted a minimized target.");
        }
        if (!minimizedDetail.Contains("minimized", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("RC minimized-target failure did not provide an actionable diagnostic: " + minimizedDetail);
        }

        target.WindowState = FormWindowState.Normal;
        target.Refresh();
        Application.DoEvents();
        if (!CaptureTargetService.TryRefreshTarget(resized, out CaptureTargetDescriptor? restored, out string restoredDetail) || restored is null)
        {
            throw new InvalidOperationException("RC target did not recover after restore: " + restoredDetail);
        }

        target.Close();
        Application.DoEvents();
        if (CaptureTargetService.TryRefreshTarget(restored, out _, out string closedDetail))
        {
            throw new InvalidOperationException("RC target refresh unexpectedly accepted a closed HWND.");
        }
        if (!closedDetail.Contains("no longer", StringComparison.OrdinalIgnoreCase) &&
            !closedDetail.Contains("valid", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("RC closed-target failure did not provide an actionable diagnostic: " + closedDetail);
        }

        // Prove the target service is not poisoned by a failure: a fresh target must still
        // be discoverable immediately after the closed/minimized cases above.
        using var replacement = new Form
        {
            Text = "LongCapture RC replacement target",
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(240, 200, 440, 300),
            BackColor = Color.White,
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.FixedToolWindow
        };
        replacement.Show();
        replacement.Refresh();
        Application.DoEvents();

        if (!CaptureTargetService.TryCreateTarget(replacement.Handle, out CaptureTargetDescriptor? replacementDescriptor, out string replacementDetail) || replacementDescriptor is null)
        {
            throw new InvalidOperationException("RC target service did not recover after target-loss cases: " + replacementDetail);
        }

        replacement.Close();
        Application.DoEvents();
    }

    private static void TestScaledLayoutResilience()
    {
        // A hosted runner cannot change the Windows runner's actual monitor DPI. Scaling the
        // inherited form font is an intentionally conservative surrogate: it exercises the
        // same preferred-size/wrapping pressure that caused the v0.1.0-v0.1.1 text clipping.
        foreach (float scale in new[] { 1.00f, 1.25f, 1.50f, 2.00f })
        {
            using var form = new MainForm();
            StandaloneUiPolish.Apply(form);
            using var scaledFont = new Font(
                form.Font.FontFamily,
                form.Font.Size * scale,
                form.Font.Style,
                GraphicsUnit.Point);
            form.Font = scaledFont;
            form.PerformLayout();
            Application.DoEvents();

            if (!StandaloneUiPolish.Validate(form, out string detail))
            {
                throw new InvalidOperationException($"RC layout failed at simulated {scale:P0} text/DPI pressure: {detail}");
            }
        }
    }
}
