// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;

namespace TopToolbar.Services.Everything;

public sealed class EverythingSearchResponse
{
    public bool IsAvailable { get; init; }

    public string StatusMessage { get; init; } = string.Empty;

    public IReadOnlyList<EverythingSearchResult> Results { get; init; } = new List<EverythingSearchResult>();

    public static EverythingSearchResponse Unavailable(string message)
    {
        return new EverythingSearchResponse
        {
            IsAvailable = false,
            StatusMessage = message ?? string.Empty,
            Results = new List<EverythingSearchResult>(),
        };
    }
}
