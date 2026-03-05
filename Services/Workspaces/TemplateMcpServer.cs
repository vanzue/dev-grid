// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TopToolbar.Serialization;

namespace TopToolbar.Services.Workspaces
{
    internal sealed class TemplateMcpServer : IDisposable
    {
        private const string JsonRpcVersion = "2.0";
        private const string ProtocolVersion = "2025-03-26";
        private readonly TemplateStore _templateStore;
        private readonly TemplateNormalizationService _normalizationService;
        private bool _shutdownRequested;
        private bool _exitRequested;
        private bool _disposed;

        public TemplateMcpServer(string templatesDirectoryPath = null)
        {
            _templateStore = new TemplateStore(templatesDirectoryPath);
            _normalizationService = new TemplateNormalizationService(templatesDirectoryPath);
        }

        public async Task<int> RunAsync(CancellationToken cancellationToken)
        {
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();

            while (!cancellationToken.IsCancellationRequested && !_exitRequested)
            {
                var payload = await ReadMessageAsync(input, cancellationToken).ConfigureAwait(false);
                if (payload == null)
                {
                    break;
                }

                var keepRunning = await HandleMessageAsync(payload, output, cancellationToken).ConfigureAwait(false);
                if (!keepRunning)
                {
                    break;
                }
            }

            return 0;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            GC.SuppressFinalize(this);
        }

        private async Task<bool> HandleMessageAsync(string payload, Stream output, CancellationToken cancellationToken)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(payload);
            }
            catch (JsonException ex)
            {
                await WriteNotificationAsync(
                    output,
                    "window/logMessage",
                    new
                    {
                        level = "error",
                        logger = "template-mcp",
                        data = $"Invalid JSON payload: {ex.Message}",
                    },
                    cancellationToken).ConfigureAwait(false);
                return !_exitRequested;
            }

            using (document)
            {
                var root = document.RootElement;
                if (!root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
                {
                    if (TryGetId(root, out var invalidId))
                    {
                        await WriteErrorResponseAsync(
                            output,
                            invalidId,
                            code: -32600,
                            message: "Invalid Request",
                            data: "Missing JSON-RPC method.",
                            cancellationToken).ConfigureAwait(false);
                    }

                    return !_exitRequested;
                }

                var method = methodElement.GetString() ?? string.Empty;
                var hasId = TryGetId(root, out var id);
                var parameters = root.TryGetProperty("params", out var paramsElement)
                    ? paramsElement.Clone()
                    : default;

                if (_shutdownRequested && !string.Equals(method, "exit", StringComparison.Ordinal))
                {
                    if (hasId)
                    {
                        await WriteErrorResponseAsync(
                            output,
                            id,
                            code: -32000,
                            message: "Server is shutting down",
                            data: "Only 'exit' is accepted after shutdown.",
                            cancellationToken).ConfigureAwait(false);
                    }

                    return !_exitRequested;
                }

                try
                {
                    switch (method)
                    {
                        case "initialize":
                        {
                            if (!hasId)
                            {
                                return !_exitRequested;
                            }

                            await WriteResultResponseAsync(
                                output,
                                id,
                                new
                                {
                                    protocolVersion = ProtocolVersion,
                                    capabilities = new
                                    {
                                        tools = new { },
                                    },
                                    serverInfo = new
                                    {
                                        name = "dev-grid-template-mcp",
                                        version = "1.0.0",
                                    },
                                },
                                cancellationToken).ConfigureAwait(false);
                            return !_exitRequested;
                        }

                        case "notifications/initialized":
                        case "initialized":
                        case "ping":
                        {
                            if (hasId)
                            {
                                await WriteResultResponseAsync(output, id, new { }, cancellationToken).ConfigureAwait(false);
                            }

                            return !_exitRequested;
                        }

                        case "shutdown":
                        {
                            _shutdownRequested = true;
                            if (hasId)
                            {
                                await WriteResultResponseAsync(output, id, new { }, cancellationToken).ConfigureAwait(false);
                            }

                            return !_exitRequested;
                        }

                        case "exit":
                        {
                            _exitRequested = true;
                            return false;
                        }

                        case "tools/list":
                        {
                            if (!hasId)
                            {
                                return !_exitRequested;
                            }

                            await WriteResultResponseAsync(
                                output,
                                id,
                                new
                                {
                                    tools = BuildToolDescriptors(),
                                },
                                cancellationToken).ConfigureAwait(false);
                            return !_exitRequested;
                        }

                        case "tools/call":
                        {
                            if (!hasId)
                            {
                                return !_exitRequested;
                            }

                            var toolResponse = await HandleToolCallAsync(parameters, cancellationToken).ConfigureAwait(false);
                            await WriteResultResponseAsync(output, id, toolResponse, cancellationToken).ConfigureAwait(false);
                            return !_exitRequested;
                        }

                        default:
                        {
                            if (!hasId)
                            {
                                return !_exitRequested;
                            }

                            await WriteErrorResponseAsync(
                                output,
                                id,
                                code: -32601,
                                message: "Method not found",
                                data: method,
                                cancellationToken).ConfigureAwait(false);
                            return !_exitRequested;
                        }
                    }
                }
                catch (ArgumentException ex)
                {
                    if (hasId)
                    {
                        await WriteErrorResponseAsync(
                            output,
                            id,
                            code: -32602,
                            message: "Invalid params",
                            data: ex.Message,
                            cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    if (hasId)
                    {
                        await WriteErrorResponseAsync(
                            output,
                            id,
                            code: -32000,
                            message: "Server error",
                            data: ex.Message,
                            cancellationToken).ConfigureAwait(false);
                    }
                }

                return !_exitRequested;
            }
        }

        private async Task<object> HandleToolCallAsync(JsonElement parameters, CancellationToken cancellationToken)
        {
            if (parameters.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("tools/call requires params object.");
            }

            if (!parameters.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException("tools/call params.name is required.");
            }

            var name = nameElement.GetString() ?? string.Empty;
            var arguments = parameters.TryGetProperty("arguments", out var argsElement)
                ? argsElement.Clone()
                : default;

            object payload;
            switch (name)
            {
                case "template.list":
                    payload = await HandleTemplateListAsync(cancellationToken).ConfigureAwait(false);
                    break;

                case "template.get":
                    payload = await HandleTemplateGetAsync(arguments, cancellationToken).ConfigureAwait(false);
                    break;

                case "template.save":
                    payload = await HandleTemplateSaveAsync(arguments, cancellationToken).ConfigureAwait(false);
                    break;

                case "template.validate":
                    payload = await HandleTemplateValidateAsync(arguments, cancellationToken).ConfigureAwait(false);
                    break;

                case "template.delete":
                    payload = await HandleTemplateDeleteAsync(arguments, cancellationToken).ConfigureAwait(false);
                    break;

                case "template.update":
                    payload = await HandleTemplateUpdateAsync(arguments, cancellationToken).ConfigureAwait(false);
                    break;

                case "template.normalize_all":
                    payload = await HandleTemplateNormalizeAsync(arguments, cancellationToken).ConfigureAwait(false);
                    break;

                case "app.discover_executables":
                    payload = HandleAppDiscover(arguments);
                    break;

                case "template.suggest":
                    payload = HandleTemplateSuggest(arguments);
                    break;

                default:
                    throw new ArgumentException($"Unknown tool '{name}'.");
            }

            var text = JsonSerializer.Serialize(payload);
            return new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text,
                    },
                },
                structuredContent = payload,
            };
        }

        private async Task<object> HandleTemplateListAsync(CancellationToken cancellationToken)
        {
            var templates = await _templateStore.LoadAllAsync(cancellationToken).ConfigureAwait(false);
            var ordered = templates
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item => new
                {
                    item.Name,
                    item.DisplayName,
                    item.Description,
                    item.RequiresRepo,
                    item.DefaultRepoRoot,
                    windows = item.Windows?.Select(window => new
                    {
                        window.Role,
                        window.Exe,
                        window.WorkingDirectory,
                        window.Args,
                    }).ToArray() ?? Array.Empty<object>(),
                    item.FocusPriority,
                    layout = new
                    {
                        strategy = item.Layout?.Strategy,
                        monitorPolicy = item.Layout?.MonitorPolicy,
                    },
                    agent = new
                    {
                        item.Agent?.Enabled,
                        item.Agent?.Name,
                        item.Agent?.Command,
                        item.Agent?.WorkingDirectory,
                    },
                    creation = new
                    {
                        item.Creation?.CreateWorktreeByDefault,
                        item.Creation?.WorktreeBaseBranch,
                    },
                })
                .ToArray();

            return new
            {
                count = ordered.Length,
                templates = ordered,
            };
        }

        private async Task<object> HandleTemplateGetAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            var name = GetRequiredString(arguments, "name");
            var template = await _templateStore.LoadByNameAsync(name, cancellationToken).ConfigureAwait(false);
            if (template == null)
            {
                throw new ArgumentException($"Template '{name}' was not found.");
            }

            return new
            {
                template,
            };
        }

        private async Task<object> HandleTemplateSaveAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            if (arguments.ValueKind != JsonValueKind.Object
                || !arguments.TryGetProperty("template", out var templateElement)
                || templateElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("template.save requires arguments.template object.");
            }

            var template = JsonSerializer.Deserialize(
                templateElement.GetRawText(),
                WorkspaceProviderJsonContext.Default.TemplateDefinition);
            if (template == null)
            {
                throw new ArgumentException("Template payload cannot be empty.");
            }

            TemplateDefinitionStandardizer.StandardizeInPlace(template);
            var errors = TemplateDefinitionValidator.Validate(template);
            if (errors.Count > 0)
            {
                return new
                {
                    saved = false,
                    templateName = template.Name,
                    validationErrors = errors,
                };
            }

            await _templateStore.SaveTemplateAsync(template, cancellationToken).ConfigureAwait(false);
            var filePath = WorkspaceStoragePaths.GetTemplateFilePath(template.Name, _templateStore.DirectoryPath);
            return new
            {
                saved = true,
                templateName = template.Name,
                filePath,
            };
        }

        private async Task<object> HandleTemplateValidateAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            var (template, source) = await LoadTemplateForValidationAsync(arguments, cancellationToken).ConfigureAwait(false);
            TemplateDefinitionStandardizer.StandardizeInPlace(template);
            var errors = TemplateDefinitionValidator.Validate(template);
            return new
            {
                valid = errors.Count == 0,
                source,
                templateName = template.Name,
                validationErrors = errors,
                template,
            };
        }

        private async Task<object> HandleTemplateDeleteAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            var name = GetRequiredString(arguments, "name");
            var normalized = WorkspaceStoragePaths.NormalizeTemplateName(name);
            var deleted = await _templateStore.DeleteTemplateAsync(normalized, cancellationToken).ConfigureAwait(false);
            return new
            {
                deleted,
                templateName = normalized,
            };
        }

        private async Task<object> HandleTemplateUpdateAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            if (arguments.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("template.update requires arguments object.");
            }

            var update = BuildUpdateDocument(arguments);
            if (!string.Equals(update.Syntax, "workspace-template-update/v1", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("template.update syntax must be 'workspace-template-update/v1'.");
            }

            var templateName = WorkspaceStoragePaths.NormalizeTemplateName(update.Template);
            if (string.IsNullOrWhiteSpace(templateName))
            {
                throw new ArgumentException("template.update requires template name.");
            }

            var template = await _templateStore.LoadByNameAsync(templateName, cancellationToken).ConfigureAwait(false);
            if (template == null)
            {
                throw new ArgumentException($"Template '{templateName}' was not found.");
            }

            var apply = TemplateUpdateEngine.ApplyInPlace(template, update.Ops);
            if (!apply.Success)
            {
                return new
                {
                    updated = false,
                    templateName,
                    applyErrors = apply.Errors,
                };
            }

            TemplateDefinitionStandardizer.StandardizeInPlace(template);
            var validationErrors = TemplateDefinitionValidator.Validate(template);
            if (validationErrors.Count > 0)
            {
                return new
                {
                    updated = false,
                    templateName,
                    validationErrors,
                    template,
                };
            }

            await _templateStore.SaveTemplateAsync(template, cancellationToken).ConfigureAwait(false);
            var filePath = WorkspaceStoragePaths.GetTemplateFilePath(template.Name, _templateStore.DirectoryPath);
            return new
            {
                updated = true,
                templateName = template.Name,
                filePath,
                template,
            };
        }

        private async Task<object> HandleTemplateNormalizeAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            var dryRun = GetOptionalBool(arguments, "dryRun");
            var report = await _normalizationService.NormalizeAllAsync(dryRun, cancellationToken).ConfigureAwait(false);
            return report;
        }

        private static object HandleAppDiscover(JsonElement arguments)
        {
            var requested = GetOptionalStringArray(arguments, "apps");
            var discovered = ExecutableLocator.DiscoverKnown(requested);
            return new
            {
                count = discovered.Count,
                apps = discovered,
            };
        }

        private static object HandleTemplateSuggest(JsonElement arguments)
        {
            TemplateSuggestionRequest request;
            if (arguments.ValueKind == JsonValueKind.Object)
            {
                request = JsonSerializer.Deserialize<TemplateSuggestionRequest>(
                    arguments.GetRawText(),
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                    }) ?? new TemplateSuggestionRequest();
            }
            else
            {
                request = new TemplateSuggestionRequest();
            }

            var suggestion = TemplateSuggestionService.BuildSuggestion(request);
            return new
            {
                template = suggestion.Template,
                validationErrors = suggestion.ValidationErrors,
                resolvedApps = suggestion.ResolvedApps,
            };
        }

        private static object[] BuildToolDescriptors()
        {
            var objectSchema = new
            {
                type = "object",
                properties = new { },
            };

            return new object[]
            {
                new
                {
                    name = "template.list",
                    description = "List all template definitions.",
                    inputSchema = objectSchema,
                },
                new
                {
                    name = "template.get",
                    description = "Get one template by name.",
                    inputSchema = new
                    {
                        type = "object",
                        required = new[] { "name" },
                        properties = new
                        {
                            name = new
                            {
                                type = "string",
                                description = "Template name.",
                            },
                        },
                    },
                },
                new
                {
                    name = "template.save",
                    description = "Validate, standardize, and save a template definition.",
                    inputSchema = new
                    {
                        type = "object",
                        required = new[] { "template" },
                        properties = new
                        {
                            template = new
                            {
                                type = "object",
                                description = "TemplateDefinition payload.",
                            },
                        },
                    },
                },
                new
                {
                    name = "template.validate",
                    description = "Validate a template payload or existing template by name.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            name = new
                            {
                                type = "string",
                                description = "Existing template name to validate.",
                            },
                            template = new
                            {
                                type = "object",
                                description = "TemplateDefinition payload to validate.",
                            },
                        },
                    },
                },
                new
                {
                    name = "template.delete",
                    description = "Delete a template by name.",
                    inputSchema = new
                    {
                        type = "object",
                        required = new[] { "name" },
                        properties = new
                        {
                            name = new
                            {
                                type = "string",
                                description = "Template name.",
                            },
                        },
                    },
                },
                new
                {
                    name = "template.update",
                    description = "Apply workspace-template-update/v1 operations to an existing template.",
                    inputSchema = new
                    {
                        type = "object",
                        required = new[] { "template", "ops" },
                        properties = new
                        {
                            syntax = new
                            {
                                type = "string",
                                description = "Expected: workspace-template-update/v1",
                            },
                            template = new
                            {
                                type = "string",
                                description = "Template name.",
                            },
                            ops = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "object",
                                    required = new[] { "op", "path" },
                                    properties = new
                                    {
                                        op = new { type = "string" },
                                        path = new { type = "string" },
                                        value = new { },
                                    },
                                },
                            },
                        },
                    },
                },
                new
                {
                    name = "template.normalize_all",
                    description = "Normalize all template JSON files in the templates directory.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            dryRun = new
                            {
                                type = "boolean",
                                description = "If true, report changes without writing files.",
                            },
                        },
                    },
                },
                new
                {
                    name = "app.discover_executables",
                    description = "Discover recommended executables for known apps (copilot, terminal, vscode, visual-studio, explorer, edge).",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            apps = new
                            {
                                type = "array",
                                items = new { type = "string" },
                                description = "Optional subset of app IDs.",
                            },
                        },
                    },
                },
                new
                {
                    name = "template.suggest",
                    description = "Build a standardized template suggestion from high-level app/agent inputs.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            name = new { type = "string" },
                            displayName = new { type = "string" },
                            description = new { type = "string" },
                            requiresRepo = new { type = "boolean" },
                            defaultRepoRoot = new { type = "string" },
                            enableAgent = new { type = "boolean" },
                            agentName = new { type = "string" },
                            agentCommand = new { type = "string" },
                            agentWorkingDirectory = new { type = "string" },
                            layoutStrategy = new { type = "string" },
                            monitorPolicy = new { type = "string" },
                            createWorktreeByDefault = new { type = "boolean" },
                            worktreeBaseBranch = new { type = "string" },
                            apps = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        enabled = new { type = "boolean" },
                                        app = new { type = "string" },
                                        role = new { type = "string" },
                                        cwd = new { type = "string" },
                                        args = new { type = "string" },
                                        init = new { type = "string" },
                                        monitor = new { type = "string" },
                                        processName = new { type = "string" },
                                    },
                                },
                            },
                        },
                    },
                },
            };
        }

        private static bool TryGetId(JsonElement root, out JsonElement id)
        {
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out var idElement))
            {
                id = idElement.Clone();
                return true;
            }

            id = default;
            return false;
        }

        private static string GetRequiredString(JsonElement element, string propertyName)
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty(propertyName, out var value)
                || value.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException($"Missing required string '{propertyName}'.");
            }

            var result = value.GetString();
            if (string.IsNullOrWhiteSpace(result))
            {
                throw new ArgumentException($"Missing required string '{propertyName}'.");
            }

            return result.Trim();
        }

        private static bool GetOptionalBool(JsonElement element, string propertyName)
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty(propertyName, out var value))
            {
                return false;
            }

            return value.ValueKind == JsonValueKind.True
                || (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed) && parsed);
        }

        private static string GetOptionalString(JsonElement element, string propertyName)
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty(propertyName, out var value)
                || value.ValueKind != JsonValueKind.String)
            {
                return string.Empty;
            }

            return value.GetString()?.Trim() ?? string.Empty;
        }

        private static IReadOnlyList<string> GetOptionalStringArray(JsonElement element, string propertyName)
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty(propertyName, out var value)
                || value.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            var values = new List<string>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    values.Add(text.Trim());
                }
            }

            return values;
        }

        private static TemplateUpdateDocument BuildUpdateDocument(JsonElement arguments)
        {
            if (arguments.TryGetProperty("update", out var updateElement) && updateElement.ValueKind == JsonValueKind.Object)
            {
                var nested = JsonSerializer.Deserialize<TemplateUpdateDocument>(updateElement.GetRawText());
                if (nested != null)
                {
                    if (string.IsNullOrWhiteSpace(nested.Template))
                    {
                        nested.Template = GetOptionalString(arguments, "template");
                    }

                    if (string.IsNullOrWhiteSpace(nested.Syntax))
                    {
                        nested.Syntax = "workspace-template-update/v1";
                    }

                    return nested;
                }
            }

            var operations = arguments.TryGetProperty("ops", out var opsElement) && opsElement.ValueKind == JsonValueKind.Array
                ? JsonSerializer.Deserialize<List<TemplateUpdateOperation>>(opsElement.GetRawText()) ?? new List<TemplateUpdateOperation>()
                : new List<TemplateUpdateOperation>();
            return new TemplateUpdateDocument
            {
                Syntax = string.IsNullOrWhiteSpace(GetOptionalString(arguments, "syntax"))
                    ? "workspace-template-update/v1"
                    : GetOptionalString(arguments, "syntax"),
                Template = GetOptionalString(arguments, "template"),
                Ops = operations,
            };
        }

        private async Task<(TemplateDefinition Template, string Source)> LoadTemplateForValidationAsync(
            JsonElement arguments,
            CancellationToken cancellationToken)
        {
            if (arguments.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("template.validate requires arguments object.");
            }

            if (arguments.TryGetProperty("template", out var templateElement) && templateElement.ValueKind == JsonValueKind.Object)
            {
                var fromPayload = JsonSerializer.Deserialize(
                    templateElement.GetRawText(),
                    WorkspaceProviderJsonContext.Default.TemplateDefinition);
                if (fromPayload == null)
                {
                    throw new ArgumentException("template.validate template payload is invalid.");
                }

                return (fromPayload, "payload");
            }

            var name = GetRequiredString(arguments, "name");
            var fromStore = await _templateStore.LoadByNameAsync(name, cancellationToken).ConfigureAwait(false);
            if (fromStore == null)
            {
                throw new ArgumentException($"Template '{name}' was not found.");
            }

            return (fromStore, "store");
        }

        private static async Task<string> ReadMessageAsync(Stream input, CancellationToken cancellationToken)
        {
            var headerBytes = new List<byte>(256);
            var oneByte = new byte[1];
            while (true)
            {
                var read = await input.ReadAsync(oneByte.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    if (headerBytes.Count == 0)
                    {
                        return null;
                    }

                    throw new EndOfStreamException("Unexpected end of stream while reading message header.");
                }

                headerBytes.Add(oneByte[0]);
                var count = headerBytes.Count;
                if (count >= 4
                    && headerBytes[count - 4] == '\r'
                    && headerBytes[count - 3] == '\n'
                    && headerBytes[count - 2] == '\r'
                    && headerBytes[count - 1] == '\n')
                {
                    break;
                }
            }

            var headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
            var contentLength = ParseContentLength(headerText);
            if (contentLength <= 0)
            {
                throw new InvalidDataException("Missing or invalid Content-Length header.");
            }

            var body = ArrayPool<byte>.Shared.Rent(contentLength);
            try
            {
                var offset = 0;
                while (offset < contentLength)
                {
                    var read = await input
                        .ReadAsync(body.AsMemory(offset, contentLength - offset), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new EndOfStreamException("Unexpected end of stream while reading message body.");
                    }

                    offset += read;
                }

                return Encoding.UTF8.GetString(body, 0, contentLength);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(body);
            }
        }

        private static int ParseContentLength(string headerText)
        {
            if (string.IsNullOrWhiteSpace(headerText))
            {
                return 0;
            }

            var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var separator = line.IndexOf(':');
                if (separator <= 0)
                {
                    continue;
                }

                var name = line[..separator].Trim();
                if (!string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = line[(separator + 1)..].Trim();
                if (int.TryParse(value, out var length) && length > 0)
                {
                    return length;
                }
            }

            return 0;
        }

        private static async Task WriteResultResponseAsync(
            Stream output,
            JsonElement id,
            object result,
            CancellationToken cancellationToken)
        {
            var payload = new Dictionary<string, object>
            {
                ["jsonrpc"] = JsonRpcVersion,
                ["id"] = id,
                ["result"] = result,
            };

            await WritePayloadAsync(output, payload, cancellationToken).ConfigureAwait(false);
        }

        private static async Task WriteErrorResponseAsync(
            Stream output,
            JsonElement id,
            int code,
            string message,
            object data,
            CancellationToken cancellationToken)
        {
            var payload = new Dictionary<string, object>
            {
                ["jsonrpc"] = JsonRpcVersion,
                ["id"] = id,
                ["error"] = new
                {
                    code,
                    message,
                    data,
                },
            };

            await WritePayloadAsync(output, payload, cancellationToken).ConfigureAwait(false);
        }

        private static async Task WriteNotificationAsync(
            Stream output,
            string method,
            object parameters,
            CancellationToken cancellationToken)
        {
            var payload = new Dictionary<string, object>
            {
                ["jsonrpc"] = JsonRpcVersion,
                ["method"] = method,
                ["params"] = parameters,
            };

            await WritePayloadAsync(output, payload, cancellationToken).ConfigureAwait(false);
        }

        private static async Task WritePayloadAsync(Stream output, object payload, CancellationToken cancellationToken)
        {
            var body = JsonSerializer.Serialize(payload);
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var headerBytes = Encoding.ASCII.GetBytes($"Content-Length: {bodyBytes.Length}\r\n\r\n");

            await output.WriteAsync(headerBytes.AsMemory(0, headerBytes.Length), cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(bodyBytes.AsMemory(0, bodyBytes.Length), cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
