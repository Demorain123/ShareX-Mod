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
    along with this program; if not, write to the Free Software Foundation, Inc.,
    51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.
*/

#endregion License Information (GPL v3)

using System;

namespace ShareX.ScreenCaptureLib
{
    public class ScrollingCaptureOptions
    {
        public int StartDelay { get; set; } = 300;
        public bool AutoScrollTop { get; set; } = false;
        public int ScrollDelay { get; set; } = 300;
        public ScrollMethod ScrollMethod { get; set; } = ScrollMethod.MouseWheel;
        public int ScrollAmount { get; set; } = 2;
        public bool AutoIgnoreBottomEdge { get; set; } = true;
        public bool AutoUpload { get; set; } = false;
        public bool ShowRegion { get; set; } = true;

        // LongCapture opt-in quality extensions. Defaults intentionally preserve
        // upstream ShareX scrolling behavior for callers that do not enable them.
        public bool AdaptiveSettle { get; set; } = false;
        public int AdaptiveSettleProbeInterval { get; set; } = 100;
        public int AdaptiveSettleStableSamples { get; set; } = 2;
        public int AdaptiveSettleMaxDelay { get; set; } = 2500;
        public double AdaptiveSettleChangedFraction { get; set; } = 0.015;
        public bool SuppressStationaryOverlays { get; set; } = false;

        public Action<ScrollingCaptureTelemetryEvent>? TelemetrySink { get; set; }
        public Action<ScrollingCaptureFrameEvent>? FrameSink { get; set; }
    }
}