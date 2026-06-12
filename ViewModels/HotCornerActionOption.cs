// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace TopToolbar.ViewModels
{
    public sealed class HotCornerActionOption
    {
        public HotCornerActionOption(string actionId, string title)
        {
            ActionId = actionId ?? string.Empty;
            Title = string.IsNullOrWhiteSpace(title) ? ActionId : title.Trim();
        }

        public string ActionId { get; }

        public string Title { get; }
    }
}
