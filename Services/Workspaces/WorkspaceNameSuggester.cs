// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using TopToolbar.Services.Providers;

namespace TopToolbar.Services.Workspaces
{
    internal static class WorkspaceNameSuggester
    {
        internal static async Task<string> GetNextWorkspaceNameAsync(CancellationToken cancellationToken)
        {
            var names = new List<string>();
            var configStore = new WorkspaceProviderConfigStore();
            var definitionStore = new WorkspaceDefinitionStore(configStore: configStore);

            var definitions = await definitionStore.LoadAllAsync(cancellationToken).ConfigureAwait(false);
            foreach (var definition in definitions)
            {
                if (!string.IsNullOrWhiteSpace(definition?.Name))
                {
                    names.Add(definition.Name);
                }
            }

            var config = await configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            foreach (var button in config?.Buttons ?? [])
            {
                if (!string.IsNullOrWhiteSpace(button?.Name))
                {
                    names.Add(button.Name);
                }
            }

            var max = 0L;
            foreach (var name in names)
            {
                if (TryParseWorkspaceNumber(name, out var number) && number > max)
                {
                    max = number;
                }
            }

            return $"w{max + 1}";
        }

        private static bool TryParseWorkspaceNumber(string name, out long number)
        {
            number = 0;
            var normalized = (name ?? string.Empty).Trim();
            if (normalized.Length < 2 || normalized[0] != 'w' && normalized[0] != 'W')
            {
                return false;
            }

            return long.TryParse(
                normalized[1..],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out number);
        }
    }
}
