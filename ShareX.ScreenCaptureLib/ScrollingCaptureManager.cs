#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using ShareX.HelpersLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib
{
    internal class ScrollingCaptureManager : IDisposable
    {
        public ScrollingCaptureOptions Options { get; private set; }
        public Bitmap Result { get; private set; }
        public bool IsCapturing { get; private set; }

        private Bitmap lastScreenshot;
        private Bitmap previousScreenshot;
        private bool stopRequested;
        private ScrollingCaptureStatus status;
        private int bestMatchCount, bestMatchIndex, bestIgnoreBottomOffset;
        private WindowInfo selectedWindow;
        private Rectangle selectedRectangle;
        private int frameIndex;

        public ScrollingCaptureManager(ScrollingCaptureOptions options)
        {
            Options = options;
        }

        public void Dispose()
        {
            Reset();
        }

        private void Reset(bool keepResult = false)
        {
            if (lastScreenshot != null)
            {
                lastScreenshot.Dispose();
                lastScreenshot = null;
            }

            if (previousScreenshot != null)
            {
                previousScreenshot.Dispose();
                previousScreenshot = null;
            }

            if (!keepResult && Result != null)
            {
                Result.Dispose();
                Result = null;
            }
        }

        public async Task<ScrollingCaptureStatus> StartCapture()
        {
            if (!IsCapturing && selectedWindow != null && !selectedRectangle.IsEmpty)
            {
                IsCapturing = true;
                stopRequested = false;
                status = ScrollingCaptureStatus.Failed;
                bestMatchCount = 0;
                bestMatchIndex = 0;
                bestIgnoreBottomOffset = 0;
                frameIndex = 0;
                Reset();

                Emit(new ScrollingCaptureTelemetryEvent
                {
                    Kind = ScrollingCaptureTelemetryKind.CaptureStarted,
                    CaptureRectangle = selectedRectangle,
                    Message = $"scrollMethod={Options.ScrollMethod}; scrollAmount={Options.ScrollAmount}; adaptiveSettle={Options.AdaptiveSettle}; suppressStationary={Options.SuppressStationaryOverlays}"
                });

                ScrollingCaptureRegionWindow regionWindow = null;

                if (Options.ShowRegion)
                {
                    regionWindow = new ScrollingCaptureRegionWindow(selectedRectangle);
                    regionWindow.Show();
                }

                try
                {
                    selectedWindow.Activate();

                    await Task.Delay(Math.Max(0, Options.StartDelay));

                    if (Options.AutoScrollTop)
                    {
                        InputHelpers.SendKeyPress(VirtualKeyCode.HOME);
                        NativeMethods.SendMessage(selectedWindow.Handle, (int)WindowsMessages.VSCROLL, (int)ScrollBarCommands.SB_TOP, 0);

                        await Task.Delay(Math.Max(0, Options.ScrollDelay));
                    }

                    Screenshot screenshot = new Screenshot()
                    {
                        CaptureCursor = false
                    };

                    while (!stopRequested)
                    {
                        lastScreenshot = screenshot.CaptureRectangle(selectedRectangle);
                        frameIndex++;
                        EmitFrame(lastScreenshot);
                        Emit(new ScrollingCaptureTelemetryEvent
                        {
                            Kind = ScrollingCaptureTelemetryKind.FrameCaptured,
                            FrameIndex = frameIndex,
                            CaptureRectangle = selectedRectangle,
                            ResultHeightBefore = Result?.Height ?? 0,
                            Message = $"frame={lastScreenshot.Width}x{lastScreenshot.Height}"
                        });

                        if (CompareLastTwoImages())
                        {
                            Emit(new ScrollingCaptureTelemetryEvent
                            {
                                Kind = ScrollingCaptureTelemetryKind.Warning,
                                FrameIndex = frameIndex,
                                CaptureRectangle = selectedRectangle,
                                Message = "current frame is pixel-identical to previous frame; treating as no further visual progress"
                            });
                            break;
                        }

                        IssueScroll();

                        Stopwatch timer = Stopwatch.StartNew();

                        if (lastScreenshot != null)
                        {
                            Bitmap newResult = await CombineImagesAsync(Result, lastScreenshot);

                            if (newResult != null)
                            {
                                Result?.Dispose();
                                Result = newResult;
                            }
                            else
                            {
                                break;
                            }
                        }

                        if (stopRequested)
                        {
                            break;
                        }

                        if (lastScreenshot != null)
                        {
                            if (previousScreenshot != null)
                            {
                                previousScreenshot.Dispose();
                            }

                            previousScreenshot = lastScreenshot;
                            lastScreenshot = null;
                        }

                        if (Options.AdaptiveSettle)
                        {
                            await WaitForVisualSettleAsync(screenshot, (int)timer.ElapsedMilliseconds);
                        }
                        else
                        {
                            int delay = Options.ScrollDelay - (int)timer.ElapsedMilliseconds;

                            if (delay > 0)
                            {
                                await Task.Delay(delay);
                            }
                        }
                    }
                }
                finally
                {
                    regionWindow?.Close();

                    Reset(true);
                    IsCapturing = false;
                    Emit(new ScrollingCaptureTelemetryEvent
                    {
                        Kind = ScrollingCaptureTelemetryKind.CaptureCompleted,
                        FrameIndex = frameIndex,
                        ResultHeightAfter = Result?.Height ?? 0,
                        CaptureRectangle = selectedRectangle,
                        Message = $"status={status}; stopRequested={stopRequested}"
                    });
                }
            }

            return status;
        }

        public void StopCapture()
        {
            if (IsCapturing)
            {
                stopRequested = true;
            }
        }

        public bool SelectWindow()
        {
            return RegionCaptureTasks.GetRectangleRegion(out selectedRectangle, out selectedWindow, new RegionCaptureOptions());
        }

        private void IssueScroll()
        {
            switch (Options.ScrollMethod)
            {
                case ScrollMethod.MouseWheel:
                    InputHelpers.SendMouseWheel(-120 * Options.ScrollAmount);
                    break;
                case ScrollMethod.DownArrow:
                    for (int i = 0; i < Options.ScrollAmount; i++)
                    {
                        InputHelpers.SendKeyPress(VirtualKeyCode.DOWN);
                    }
                    break;
                case ScrollMethod.PageDown:
                    InputHelpers.SendKeyPress(VirtualKeyCode.NEXT);
                    break;
                case ScrollMethod.ScrollMessage:
                    for (int i = 0; i < Options.ScrollAmount; i++)
                    {
                        NativeMethods.SendMessage(selectedWindow.Handle, (int)WindowsMessages.VSCROLL, (int)ScrollBarCommands.SB_LINEDOWN, 0);
                    }
                    break;
            }

            Emit(new ScrollingCaptureTelemetryEvent
            {
                Kind = ScrollingCaptureTelemetryKind.ScrollIssued,
                FrameIndex = frameIndex,
                CaptureRectangle = selectedRectangle,
                Message = $"method={Options.ScrollMethod}; amount={Options.ScrollAmount}"
            });
        }

        private async Task WaitForVisualSettleAsync(Screenshot screenshot, int alreadyElapsedMilliseconds)
        {
            int minimumDelay = Math.Max(0, Options.ScrollDelay);
            int maxDelay = Math.Max(minimumDelay, Options.AdaptiveSettleMaxDelay);
            int probeInterval = Math.Max(40, Options.AdaptiveSettleProbeInterval);
            int requiredStable = Math.Max(1, Options.AdaptiveSettleStableSamples);
            double threshold = Math.Max(0.0001, Math.Min(0.25, Options.AdaptiveSettleChangedFraction));

            int waited = Math.Max(0, alreadyElapsedMilliseconds);
            int remainingMinimum = minimumDelay - waited;
            if (remainingMinimum > 0)
            {
                await Task.Delay(remainingMinimum);
                waited += remainingMinimum;
            }

            if (stopRequested) return;

            Bitmap previousProbe = null;
            int stableSamples = 0;
            double lastChangedFraction = 1.0;

            try
            {
                previousProbe = screenshot.CaptureRectangle(selectedRectangle);

                while (!stopRequested && waited < maxDelay)
                {
                    int interval = Math.Min(probeInterval, maxDelay - waited);
                    if (interval <= 0) break;
                    await Task.Delay(interval);
                    waited += interval;

                    using Bitmap currentProbe = screenshot.CaptureRectangle(selectedRectangle);
                    lastChangedFraction = ComputeChangedFraction(previousProbe, currentProbe);
                    bool stable = lastChangedFraction <= threshold;
                    stableSamples = stable ? stableSamples + 1 : 0;

                    Emit(new ScrollingCaptureTelemetryEvent
                    {
                        Kind = ScrollingCaptureTelemetryKind.SettleProbe,
                        FrameIndex = frameIndex,
                        WaitedMilliseconds = waited,
                        ChangedFraction = lastChangedFraction,
                        Confidence = Math.Max(0.0, 1.0 - Math.Min(1.0, lastChangedFraction / threshold)),
                        CaptureRectangle = selectedRectangle,
                        Message = $"stableSamples={stableSamples}/{requiredStable}; threshold={threshold:F4}"
                    });

                    previousProbe.Dispose();
                    previousProbe = (Bitmap)currentProbe.Clone();

                    if (stableSamples >= requiredStable)
                    {
                        Emit(new ScrollingCaptureTelemetryEvent
                        {
                            Kind = ScrollingCaptureTelemetryKind.SettleCompleted,
                            FrameIndex = frameIndex,
                            WaitedMilliseconds = waited,
                            ChangedFraction = lastChangedFraction,
                            TimedOut = false,
                            CaptureRectangle = selectedRectangle,
                            Message = "visual settle guard reached stable sample requirement"
                        });
                        return;
                    }
                }

                if (!stopRequested)
                {
                    Emit(new ScrollingCaptureTelemetryEvent
                    {
                        Kind = ScrollingCaptureTelemetryKind.SettleCompleted,
                        FrameIndex = frameIndex,
                        WaitedMilliseconds = waited,
                        ChangedFraction = lastChangedFraction,
                        TimedOut = true,
                        CaptureRectangle = selectedRectangle,
                        Message = "visual settle guard hit maximum wait; continuing with warning"
                    });
                }
            }
            finally
            {
                previousProbe?.Dispose();
            }
        }

        private static unsafe double ComputeChangedFraction(Bitmap first, Bitmap second)
        {
            if (first == null || second == null || first.Width != second.Width || first.Height != second.Height)
            {
                return 1.0;
            }

            Rectangle bounds = new Rectangle(0, 0, first.Width, first.Height);
            BitmapData firstData = first.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            BitmapData secondData = second.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

            try
            {
                int marginX = Math.Max(2, first.Width / 20);
                int marginY = Math.Max(2, first.Height / 20);
                int sampleStep = Math.Max(6, Math.Min(first.Width, first.Height) / 120);
                long changed = 0;
                long total = 0;

                for (int y = marginY; y < first.Height - marginY; y += sampleStep)
                {
                    byte* rowA = (byte*)firstData.Scan0 + y * firstData.Stride;
                    byte* rowB = (byte*)secondData.Scan0 + y * secondData.Stride;
                    for (int x = marginX; x < first.Width - marginX; x += sampleStep)
                    {
                        byte* a = rowA + x * 4;
                        byte* b = rowB + x * 4;
                        int difference = Math.Abs(a[0] - b[0]) + Math.Abs(a[1] - b[1]) + Math.Abs(a[2] - b[2]);
                        if (difference > 36) changed++;
                        total++;
                    }
                }

                return total == 0 ? 0.0 : changed / (double)total;
            }
            finally
            {
                first.UnlockBits(firstData);
                second.UnlockBits(secondData);
            }
        }

        private bool IsScrollReachedBottom(IntPtr handle)
        {
            SCROLLINFO scrollInfo = new SCROLLINFO();
            scrollInfo.cbSize = (uint)Marshal.SizeOf(scrollInfo);
            scrollInfo.fMask = (uint)(ScrollInfoMask.SIF_RANGE | ScrollInfoMask.SIF_PAGE | ScrollInfoMask.SIF_TRACKPOS);

            if (NativeMethods.GetScrollInfo(handle, (int)SBOrientation.SB_VERT, ref scrollInfo))
            {
                return scrollInfo.nMax == scrollInfo.nTrackPos + scrollInfo.nPage - 1;
            }

            return CompareLastTwoImages();
        }

        private bool CompareLastTwoImages()
        {
            if (lastScreenshot != null && previousScreenshot != null)
            {
                return ImageHelpers.CompareImages(lastScreenshot, previousScreenshot);
            }

            return false;
        }

        private async Task<Bitmap> CombineImagesAsync(Bitmap result, Bitmap currentImage)
        {
            return await Task.Run(() => CombineImages(result, currentImage));
        }

        private Bitmap CombineImages(Bitmap result, Bitmap currentImage)
        {
            if (result == null)
            {
                status = ScrollingCaptureStatus.Successful;
                Emit(new ScrollingCaptureTelemetryEvent
                {
                    Kind = ScrollingCaptureTelemetryKind.StitchComputed,
                    FrameIndex = frameIndex,
                    ResultHeightBefore = 0,
                    ResultHeightAfter = currentImage.Height,
                    Confidence = 1.0,
                    CaptureRectangle = selectedRectangle,
                    Message = "seed frame"
                });
                return (Bitmap)currentImage.Clone();
            }

            int resultHeightBefore = result.Height;
            int matchCount = 0;
            int matchIndex = 0;
            int matchLimit = currentImage.Height / 2;

            int ignoreSideOffset = Math.Max(50, currentImage.Width / 20);
            ignoreSideOffset = Math.Min(ignoreSideOffset, currentImage.Width / 3);

            Rectangle rect = new Rectangle(ignoreSideOffset, result.Height - currentImage.Height, currentImage.Width - ignoreSideOffset * 2, currentImage.Height);

            BitmapData bdResult = result.LockBits(new Rectangle(0, 0, result.Width, result.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            BitmapData bdCurrentImage = currentImage.LockBits(new Rectangle(0, 0, currentImage.Width, currentImage.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int stride = bdResult.Stride;
            int pixelSize = stride / result.Width;
            IntPtr resultScan0 = bdResult.Scan0 + pixelSize * ignoreSideOffset;
            IntPtr currentImageScan0 = bdCurrentImage.Scan0 + pixelSize * ignoreSideOffset;
            int compareLength = pixelSize * rect.Width;

            int ignoreBottomOffsetMax = currentImage.Height / 3;
            int ignoreBottomOffset = Math.Max(50, currentImage.Height / 10);

            if (Options.AutoIgnoreBottomEdge)
            {
                IntPtr resultScan0Last = resultScan0 + (result.Height - 1) * stride;
                IntPtr currentImageScan0Last = currentImageScan0 + (currentImage.Height - 1) * stride;

                for (int i = 0; i <= ignoreBottomOffsetMax; i++)
                {
                    if (NativeMethods.memcmp(resultScan0Last - i * stride, currentImageScan0Last - i * stride, compareLength) != 0)
                    {
                        ignoreBottomOffset += i;
                        break;
                    }
                }

                ignoreBottomOffset = Math.Max(ignoreBottomOffset, bestIgnoreBottomOffset);
            }

            ignoreBottomOffset = Math.Min(ignoreBottomOffset, ignoreBottomOffsetMax);

            int rectBottom = rect.Bottom - ignoreBottomOffset - 1;

            for (int currentImageY = currentImage.Height - 1; currentImageY >= 0 && matchCount < matchLimit; currentImageY--)
            {
                int currentMatchCount = 0;

                for (int y = 0; currentImageY - y >= 0 && currentMatchCount < matchLimit; y++)
                {
                    if (NativeMethods.memcmp(resultScan0 + ((rectBottom - y) * stride), currentImageScan0 + ((currentImageY - y) * stride), compareLength) == 0)
                    {
                        currentMatchCount++;
                    }
                    else
                    {
                        break;
                    }
                }

                if (currentMatchCount > matchCount)
                {
                    matchCount = currentMatchCount;
                    matchIndex = currentImageY;
                }
            }

            result.UnlockBits(bdResult);
            currentImage.UnlockBits(bdCurrentImage);

            bool bestGuess = false;

            if (matchCount == 0 && bestMatchCount > 0)
            {
                matchCount = bestMatchCount;
                matchIndex = bestMatchIndex;
                ignoreBottomOffset = bestIgnoreBottomOffset;
                bestGuess = true;
            }

            if (matchCount > 0)
            {
                int matchHeight = currentImage.Height - matchIndex - 1;

                if (matchHeight > 0)
                {
                    if (matchCount > bestMatchCount)
                    {
                        bestMatchCount = matchCount;
                        bestMatchIndex = matchIndex;
                        bestIgnoreBottomOffset = ignoreBottomOffset;
                    }

                    int stationaryTileCount = 0;
                    int stationaryArea = 0;
                    if (Options.SuppressStationaryOverlays && previousScreenshot != null && previousScreenshot.Size == currentImage.Size)
                    {
                        SuppressStationaryOverlaysInResult(result, previousScreenshot, currentImage, matchHeight, out stationaryTileCount, out stationaryArea);
                    }

                    Bitmap newResult = new Bitmap(result.Width, result.Height - ignoreBottomOffset + matchHeight);

                    using (Graphics g = Graphics.FromImage(newResult))
                    {
                        g.CompositingMode = CompositingMode.SourceCopy;
                        g.InterpolationMode = InterpolationMode.NearestNeighbor;

                        g.DrawImage(result, new Rectangle(0, 0, result.Width, result.Height - ignoreBottomOffset),
                            new Rectangle(0, 0, result.Width, result.Height - ignoreBottomOffset), GraphicsUnit.Pixel);
                        g.DrawImage(currentImage, new Rectangle(0, result.Height - ignoreBottomOffset, currentImage.Width, matchHeight),
                            new Rectangle(0, matchIndex + 1, currentImage.Width, matchHeight), GraphicsUnit.Pixel);
                    }

                    if (bestGuess)
                    {
                        status = ScrollingCaptureStatus.PartiallySuccessful;
                    }
                    else if (status != ScrollingCaptureStatus.PartiallySuccessful)
                    {
                        status = ScrollingCaptureStatus.Successful;
                    }

                    double confidenceDenominator = Math.Max(8.0, Math.Min(matchLimit, Math.Max(8, currentImage.Height / 16)));
                    double confidence = Math.Min(1.0, matchCount / confidenceDenominator);
                    if (bestGuess) confidence = Math.Min(confidence, 0.35);

                    Emit(new ScrollingCaptureTelemetryEvent
                    {
                        Kind = ScrollingCaptureTelemetryKind.StitchComputed,
                        FrameIndex = frameIndex,
                        ResultHeightBefore = resultHeightBefore,
                        ResultHeightAfter = newResult.Height,
                        EstimatedScrollPixels = matchHeight,
                        MatchRows = matchCount,
                        Confidence = confidence,
                        UsedBestGuess = bestGuess,
                        StationaryTileCount = stationaryTileCount,
                        StationaryPixelArea = stationaryArea,
                        CaptureRectangle = selectedRectangle,
                        Message = $"ignoreBottom={ignoreBottomOffset}; matchIndex={matchIndex}"
                    });

                    if (stationaryTileCount > 0)
                    {
                        Emit(new ScrollingCaptureTelemetryEvent
                        {
                            Kind = ScrollingCaptureTelemetryKind.StationaryOverlayDetected,
                            FrameIndex = frameIndex,
                            EstimatedScrollPixels = matchHeight,
                            StationaryTileCount = stationaryTileCount,
                            StationaryPixelArea = stationaryArea,
                            CaptureRectangle = selectedRectangle,
                            Message = "high-confidence stationary textured tiles were removed from the preceding viewport before append"
                        });
                    }

                    if (confidence < 0.35 || bestGuess)
                    {
                        Emit(new ScrollingCaptureTelemetryEvent
                        {
                            Kind = ScrollingCaptureTelemetryKind.Warning,
                            FrameIndex = frameIndex,
                            EstimatedScrollPixels = matchHeight,
                            MatchRows = matchCount,
                            Confidence = confidence,
                            UsedBestGuess = bestGuess,
                            CaptureRectangle = selectedRectangle,
                            Message = bestGuess ? "stitch used historical best-guess fallback" : "stitch confidence is low"
                        });
                    }

                    return newResult;
                }
            }

            status = ScrollingCaptureStatus.Failed;
            Emit(new ScrollingCaptureTelemetryEvent
            {
                Kind = ScrollingCaptureTelemetryKind.Warning,
                FrameIndex = frameIndex,
                ResultHeightBefore = resultHeightBefore,
                MatchRows = matchCount,
                CaptureRectangle = selectedRectangle,
                Message = "no usable vertical overlap was found; capture stopped instead of emitting a guessed stitch"
            });

            return null;
        }

        private unsafe void SuppressStationaryOverlaysInResult(
            Bitmap result,
            Bitmap previousFrame,
            Bitmap currentFrame,
            int scrollPixels,
            out int stationaryTileCount,
            out int stationaryArea)
        {
            stationaryTileCount = 0;
            stationaryArea = 0;
            if (scrollPixels <= 0 || scrollPixels >= currentFrame.Height) return;
            if (result.Height < previousFrame.Height) return;

            const int tile = 40;
            const int sample = 8;
            const double sameThreshold = 7.0;
            const double translatedThreshold = 18.0;
            const double textureThreshold = 13.0;

            Rectangle bounds = new Rectangle(0, 0, previousFrame.Width, previousFrame.Height);
            BitmapData previousData = previousFrame.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            BitmapData currentData = currentFrame.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var detected = new List<Rectangle>();

            try
            {
                int leftMargin = Math.Max(0, previousFrame.Width / 100);
                int rightLimit = previousFrame.Width - leftMargin;
                for (int y = scrollPixels; y < previousFrame.Height - 4; y += tile)
                {
                    int height = Math.Min(tile, previousFrame.Height - y);
                    int sourceY = y - scrollPixels;
                    if (sourceY < 0 || sourceY + height > currentFrame.Height) continue;

                    for (int x = leftMargin; x < rightLimit - 4; x += tile)
                    {
                        int width = Math.Min(tile, rightLimit - x);
                        double sameDifference = 0;
                        double translatedDifference = 0;
                        double texture = 0;
                        int samples = 0;

                        for (int sy = 4; sy < height; sy += sample)
                        {
                            byte* previousRow = (byte*)previousData.Scan0 + (y + sy) * previousData.Stride;
                            byte* currentSameRow = (byte*)currentData.Scan0 + (y + sy) * currentData.Stride;
                            byte* currentTranslatedRow = (byte*)currentData.Scan0 + (sourceY + sy) * currentData.Stride;
                            for (int sx = 4; sx < width; sx += sample)
                            {
                                byte* p = previousRow + (x + sx) * 4;
                                byte* same = currentSameRow + (x + sx) * 4;
                                byte* translated = currentTranslatedRow + (x + sx) * 4;
                                sameDifference += PixelDifference(p, same);
                                translatedDifference += PixelDifference(p, translated);

                                if (sx + 2 < width)
                                {
                                    byte* neighbor = previousRow + (x + sx + 2) * 4;
                                    texture += PixelDifference(p, neighbor);
                                }
                                samples++;
                            }
                        }

                        if (samples == 0) continue;
                        sameDifference /= samples;
                        translatedDifference /= samples;
                        texture /= samples;

                        // A stationary overlay is screen-stable but does not obey the
                        // page's vertical translation. Requiring texture prevents blank
                        // backgrounds from being mistaken for fixed controls.
                        if (sameDifference <= sameThreshold &&
                            translatedDifference >= translatedThreshold &&
                            texture >= textureThreshold)
                        {
                            detected.Add(new Rectangle(x, y, width, height));
                        }
                    }
                }
            }
            finally
            {
                previousFrame.UnlockBits(previousData);
                currentFrame.UnlockBits(currentData);
            }

            if (detected.Count == 0) return;

            int previousViewportTop = result.Height - previousFrame.Height;
            using Graphics graphics = Graphics.FromImage(result);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;

            foreach (Rectangle tileRect in detected)
            {
                Rectangle source = new Rectangle(tileRect.X, tileRect.Y - scrollPixels, tileRect.Width, tileRect.Height);
                Rectangle destination = new Rectangle(tileRect.X, previousViewportTop + tileRect.Y, tileRect.Width, tileRect.Height);
                if (destination.Top < 0 || destination.Bottom > result.Height) continue;

                graphics.DrawImage(currentFrame, destination, source, GraphicsUnit.Pixel);
                stationaryTileCount++;
                stationaryArea += tileRect.Width * tileRect.Height;
            }
        }

        private static unsafe double PixelDifference(byte* a, byte* b)
        {
            return (Math.Abs(a[0] - b[0]) + Math.Abs(a[1] - b[1]) + Math.Abs(a[2] - b[2])) / 3.0;
        }

        private void EmitFrame(Bitmap bitmap)
        {
            if (Options.FrameSink == null || bitmap == null) return;
            try
            {
                Options.FrameSink(new ScrollingCaptureFrameEvent
                {
                    FrameIndex = frameIndex,
                    Timestamp = DateTimeOffset.Now,
                    CaptureRectangle = selectedRectangle,
                    Frame = bitmap
                });
            }
            catch
            {
                // Diagnostics must never become a capture failure path.
            }
        }

        private void Emit(ScrollingCaptureTelemetryEvent value)
        {
            if (Options.TelemetrySink == null) return;
            try
            {
                Options.TelemetrySink(value);
            }
            catch
            {
                // Diagnostics must never become a capture failure path.
            }
        }
    }
}
