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

namespace TopToolbar.Providers;

public sealed class EverythingSearchProvider : IActionProvider, IToolbarGroupProvider
{
    public const string ProviderId = "EverythingSearchProvider";
    public const string OpenSearchActionId = "everything.search.open";
    public const string GroupId = "everything-search";

    private const string SearchButtonId = "everything-search::open";

    public string Id => ProviderId;

    public Task<ProviderInfo> GetInfoAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(new ProviderInfo("Everything Search", "1.0"));
    }

    public async IAsyncEnumerable<ActionDescriptor> DiscoverAsync(
        ActionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return new ActionDescriptor
        {
            Id = OpenSearchActionId,
            ProviderId = Id,
            Title = "Search",
            Subtitle = "Search files and folders with Everything.",
            Kind = ActionKind.Command,
            GroupHint = GroupId,
            Order = 0,
            Icon = new ActionIcon
            {
                Type = ActionIconType.Glyph,
                Value = "\uE721",
            },
            CanExecute = true,
        };

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public Task<ButtonGroup> CreateGroupAsync(ActionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var group = new ButtonGroup
        {
            Id = GroupId,
            Name = "Search",
            Description = "Everything-powered file search",
            IsEnabled = true,
            Layout = new ToolbarGroupLayout
            {
                Style = ToolbarGroupLayoutStyle.Icon,
                Overflow = ToolbarGroupOverflowMode.Wrap,
            },
        };

        group.Buttons.Add(new ToolbarButton
        {
            Id = SearchButtonId,
            Name = "Search",
            Description = "Search files and folders with Everything.",
            IconType = ToolbarIconType.Catalog,
            IconGlyph = "\uE721",
            IsEnabled = true,
            Action = new ToolbarAction
            {
                Type = ToolbarActionType.Provider,
                ProviderId = Id,
                ProviderActionId = OpenSearchActionId,
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
        if (!string.Equals(actionId, OpenSearchActionId, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new ActionResult
            {
                Ok = false,
                Message = "Unknown Everything search action.",
            });
        }

        return Task.FromResult(new ActionResult
        {
            Ok = true,
            Message = string.Empty,
        });
    }
}
