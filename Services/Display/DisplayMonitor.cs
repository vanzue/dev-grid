// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace TopToolbar.Services.Display
{
    internal sealed class DisplayMonitor
    {
        public DisplayMonitor(
            string id,
            string instanceId,
            int index,
            int dpi,
            DisplayRect dpiAwareRect,
            DisplayRect dpiUnawareRect,
            DisplayRect dpiAwareWorkRect,
            DisplayRect dpiUnawareWorkRect)
        {
            Id = id ?? string.Empty;
            InstanceId = instanceId ?? string.Empty;
            Index = index;
            Dpi = dpi;
            DpiAwareRect = dpiAwareRect;
            DpiUnawareRect = dpiUnawareRect;
            DpiAwareWorkRect = dpiAwareWorkRect.IsEmpty ? dpiAwareRect : dpiAwareWorkRect;
            DpiUnawareWorkRect = dpiUnawareWorkRect.IsEmpty ? dpiUnawareRect : dpiUnawareWorkRect;
        }

        public string Id { get; }

        public string InstanceId { get; }

        public int Index { get; }

        public int Dpi { get; }

        public DisplayRect DpiAwareRect { get; }

        public DisplayRect DpiUnawareRect { get; }

        public DisplayRect DpiAwareWorkRect { get; }

        public DisplayRect DpiUnawareWorkRect { get; }

        public DisplayRect Bounds => DpiAwareRect;
    }
}
