// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TopToolbar.Actions;
using TopToolbar.Models;

namespace TopToolbar.Providers
{
    /// <summary>
    /// Built-in provider that exposes a screenshot capture action. Like every other action it can be
    /// pinned to the bar and/or the ring; the actual interactive capture UI is launched on the UI
    /// thread by <c>ToolbarWindow</c> (see ToolbarWindow.Screenshot.cs) because it owns a top-most
    /// overlay window.
    /// </summary>
    public sealed class ScreenshotProvider : IActionProvider, IToolbarGroupProvider
    {
        public const string ProviderId = "ScreenshotProvider";
        public const string CaptureActionId = "screenshot.capture";

        private const string GroupId = "capture";
        private const string GroupName = "Capture";
        private const string ButtonId = "capture::screenshot";
        private const string CaptureGlyph = "\uE7A8";

        public string Id => ProviderId;

        public Task<ProviderInfo> GetInfoAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new ProviderInfo("Screenshot", "1.0"));
        }

        public async IAsyncEnumerable<ActionDescriptor> DiscoverAsync(
            ActionContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask.ConfigureAwait(false);

            yield return new ActionDescriptor
            {
                Id = CaptureActionId,
                ProviderId = Id,
                Title = "Screenshot",
                Subtitle = "Freeze the screen and copy a region to the clipboard",
                Kind = ActionKind.Command,
                GroupHint = GroupId,
                Order = 0,
                Icon = new ActionIcon
                {
                    Type = ActionIconType.Glyph,
                    Value = CaptureGlyph,
                },
                CanExecute = true,
            };
        }

        public Task<ButtonGroup> CreateGroupAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var group = new ButtonGroup
            {
                Id = GroupId,
                Name = GroupName,
                Description = "Screen capture",
                IsEnabled = true,
                Layout = new ToolbarGroupLayout
                {
                    Style = ToolbarGroupLayoutStyle.Icon,
                    Overflow = ToolbarGroupOverflowMode.Wrap,
                },
            };

            group.Buttons.Add(new ToolbarButton
            {
                Id = ButtonId,
                Name = "Screenshot",
                Description = "Freeze the screen and copy a region to the clipboard",
                IconType = ToolbarIconType.Catalog,
                IconGlyph = CaptureGlyph,
                IsEnabled = true,
                Surfaces = ActionSurfaces.Bar | ActionSurfaces.Ring,
                Action = new ToolbarAction
                {
                    Type = ToolbarActionType.Provider,
                    ProviderId = Id,
                    ProviderActionId = CaptureActionId,
                },
            });

            return Task.FromResult(group);
        }

        public Task<ActionResult> InvokeAsync(
            string actionId,
            JsonElement? args,
            ActionContext context,
            IProgress<ActionProgress> progress,
            CancellationToken cancellationToken)
        {
            // Invocation is intercepted on the UI thread by ToolbarWindow which owns the overlay window.
            // This path is a no-op fallback so the action is still considered valid if invoked directly.
            return Task.FromResult(new ActionResult
            {
                Ok = true,
                Message = string.Empty,
            });
        }
    }
}
