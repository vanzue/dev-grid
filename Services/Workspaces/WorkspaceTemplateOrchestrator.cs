// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TopToolbar.Services.Display;
using TopToolbar.Services.Providers;

namespace TopToolbar.Services.Workspaces
{
    internal sealed class WorkspaceTemplateOrchestrator : IDisposable
    {
        internal readonly record struct CreateResult(bool Success, string Message, WorkspaceDefinition Workspace, bool Launched);
        internal readonly record struct SwitchResult(bool Success, string Message, string WorkspaceId, WorkspaceSwitchDiagnostics Diagnostics);
        private readonly record struct WorktreeCreationResult(bool Success, string WorktreePath, string Message);

        internal sealed class CreateWorkspaceRequest
        {
            public string TemplateName { get; set; } = string.Empty;

            public string PresetName { get; set; } = string.Empty;

            public string LayoutStrategy { get; set; } = string.Empty;

            public string InstanceName { get; set; } = string.Empty;

            public string RepoRoot { get; set; } = string.Empty;

            public IReadOnlyList<string> FocusRoles { get; set; } = Array.Empty<string>();

            public IReadOnlyList<WorkspaceAppSpec> Apps { get; set; } = Array.Empty<WorkspaceAppSpec>();

            public string SaveTemplateName { get; set; } = string.Empty;

            public bool NoLaunch { get; set; }

            public bool? CreateWorktree { get; set; }

            public string WorktreeBaseBranch { get; set; } = string.Empty;

            public bool EphemeralLaunch { get; set; }

            public IProgress<string> Progress { get; set; }
        }

        private readonly TemplateStore _templateStore;
        private readonly WorkspaceProviderConfigStore _configStore;
        private readonly WorkspaceDefinitionStore _definitionStore;
        private readonly WorkspaceButtonStore _buttonStore;
        private readonly WorkspacesRuntimeService _runtimeService;
        private readonly DisplayManager _displayManager;
        private bool _disposed;

        public WorkspaceTemplateOrchestrator(string providerConfigPath = null)
        {
            _templateStore = new TemplateStore();
            _configStore = new WorkspaceProviderConfigStore(providerConfigPath);
            _definitionStore = new WorkspaceDefinitionStore(null, _configStore);
            _buttonStore = new WorkspaceButtonStore(_configStore, _definitionStore);
            _runtimeService = new WorkspacesRuntimeService(providerConfigPath);
            _displayManager = new DisplayManager();
        }

        public Task<IReadOnlyList<TemplateDefinition>> ListTemplatesAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(WorkspaceTemplateOrchestrator));
            return _templateStore.LoadAllAsync(cancellationToken);
        }

        public Task<TemplateDefinition> GetTemplateAsync(string templateName, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(WorkspaceTemplateOrchestrator));
            return _templateStore.LoadByNameAsync(templateName, cancellationToken);
        }

        public Task SaveTemplateAsync(TemplateDefinition template, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(WorkspaceTemplateOrchestrator));
            return _templateStore.SaveTemplateAsync(template, cancellationToken);
        }

        public Task<bool> DeleteTemplateAsync(string templateName, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(WorkspaceTemplateOrchestrator));
            var normalized = WorkspaceStoragePaths.NormalizeTemplateName(templateName);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return Task.FromResult(false);
            }

            return _templateStore.DeleteTemplateAsync(normalized, cancellationToken);
        }

        public Task<CreateResult> CreateFromTemplateAsync(
            string templateName,
            string instanceName,
            string repoRoot,
            bool noLaunch,
            CancellationToken cancellationToken)
        {
            return CreateWorkspaceAsync(
                new CreateWorkspaceRequest
                {
                    TemplateName = templateName ?? string.Empty,
                    InstanceName = instanceName ?? string.Empty,
                    RepoRoot = repoRoot ?? string.Empty,
                    NoLaunch = noLaunch,
                },
                cancellationToken);
        }

        public async Task<CreateResult> CreateWorkspaceAsync(CreateWorkspaceRequest request, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(WorkspaceTemplateOrchestrator));
            ArgumentNullException.ThrowIfNull(request);

            void ReportProgress(string message)
            {
                if (string.IsNullOrWhiteSpace(message))
                {
                    return;
                }

                try
                {
                    request.Progress?.Report(message);
                }
                catch
                {
                    // Progress reporting must never break workspace creation.
                }
            }

            var hasTemplate = !string.IsNullOrWhiteSpace(request.TemplateName);
            var hasPreset = !string.IsNullOrWhiteSpace(request.PresetName);
            var hasApps = request.Apps != null && request.Apps.Count > 0;
            if (!hasTemplate && !hasPreset && !hasApps)
            {
                return new CreateResult(false, "Either --template, --preset, or --app must be provided.", null, false);
            }

            TemplateDefinition template;
            if (hasTemplate)
            {
                ReportProgress("Loading template...");
                var templateName = WorkspaceStoragePaths.NormalizeTemplateName(request.TemplateName);
                template = await _templateStore.LoadByNameAsync(templateName, cancellationToken).ConfigureAwait(false);
                if (template == null)
                {
                    return new CreateResult(false, $"Template '{templateName}' was not found.", null, false);
                }
            }
            else
            {
                template = BuildGeneratedTemplate(request);
            }

            ApplyOverrides(template, request);
            ReportProgress("Validating template...");
            TemplateDefinitionStandardizer.StandardizeInPlace(template);
            var errors = TemplateDefinitionValidator.Validate(template);
            if (errors.Count > 0)
            {
                return new CreateResult(false, $"Template '{template.Name}' is invalid: {string.Join("; ", errors)}", null, false);
            }

            if (!hasTemplate && !string.IsNullOrWhiteSpace(request.SaveTemplateName))
            {
                var save = CloneTemplate(template);
                save.Name = WorkspaceStoragePaths.NormalizeTemplateName(request.SaveTemplateName);
                save.DisplayName = BuildDisplayNameFromTemplateName(save.Name);
                TemplateDefinitionStandardizer.StandardizeInPlace(save);
                var saveErrors = TemplateDefinitionValidator.Validate(save);
                if (saveErrors.Count > 0)
                {
                    return new CreateResult(false, $"Template '{save.Name}' is invalid: {string.Join("; ", saveErrors)}", null, false);
                }

                await _templateStore.SaveTemplateAsync(save, cancellationToken).ConfigureAwait(false);
            }

            var instanceName = (request.InstanceName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(instanceName))
            {
                if (!request.EphemeralLaunch)
                {
                    return new CreateResult(false, "Workspace name is required.", null, false);
                }

                instanceName = BuildEphemeralInstanceName(template);
            }

            var resolvedRepo = ResolveRepoRoot(request.RepoRoot, template.DefaultRepoRoot);
            if (template.RequiresRepo && string.IsNullOrWhiteSpace(resolvedRepo))
            {
                return new CreateResult(false, $"Template '{template.Name}' requires --repo.", null, false);
            }

            if (!string.IsNullOrWhiteSpace(resolvedRepo) && !Directory.Exists(resolvedRepo))
            {
                return new CreateResult(false, $"Repo path '{resolvedRepo}' does not exist.", null, false);
            }

            if (!request.EphemeralLaunch)
            {
                var existing = await _definitionStore.LoadAllAsync(cancellationToken).ConfigureAwait(false);
                if (existing.Any(ws =>
                    ws != null
                    && string.Equals(ws.TemplateName, template.Name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(ws.InstanceName, instanceName, StringComparison.OrdinalIgnoreCase)))
                {
                    return new CreateResult(false, $"Workspace '{BuildWorkspaceTitle(template, instanceName)}' already exists in template '{template.Name}'.", null, false);
                }
            }

            var effectiveRepoRoot = resolvedRepo;
            var shouldCreateWorktree = request.CreateWorktree ?? (template.Creation?.CreateWorktreeByDefault ?? false);
            if (shouldCreateWorktree)
            {
                ReportProgress("Setting up git worktree...");
                if (string.IsNullOrWhiteSpace(resolvedRepo))
                {
                    return new CreateResult(false, "Worktree creation requires a repository path.", null, false);
                }

                var baseBranch = !string.IsNullOrWhiteSpace(request.WorktreeBaseBranch)
                    ? request.WorktreeBaseBranch
                    : template.Creation?.WorktreeBaseBranch;
                var worktree = await CreateGitWorktreeAsync(resolvedRepo, instanceName, baseBranch, cancellationToken).ConfigureAwait(false);
                if (!worktree.Success)
                {
                    return new CreateResult(false, worktree.Message, null, false);
                }

                effectiveRepoRoot = worktree.WorktreePath;
            }

            var workspace = BuildWorkspaceDefinition(template, instanceName, effectiveRepoRoot);

            if (request.EphemeralLaunch)
            {
                if (request.NoLaunch)
                {
                    return new CreateResult(true, "Window set prepared.", workspace, Launched: false);
                }

                ReportProgress("Starting workspace launch...");
                var launchedEphemeral = await _runtimeService.LaunchWorkspaceAsync(workspace, cancellationToken, request.Progress).ConfigureAwait(false);
                return launchedEphemeral
                    ? new CreateResult(true, "Window set launched.", workspace, Launched: true)
                    : new CreateResult(false, "Window set launch failed.", workspace, Launched: false);
            }

            await _definitionStore.SaveWorkspaceAsync(workspace, cancellationToken).ConfigureAwait(false);
            await _buttonStore.EnsureButtonAsync(workspace, cancellationToken).ConfigureAwait(false);

            if (request.NoLaunch)
            {
                return new CreateResult(true, "Workspace created.", workspace, Launched: false);
            }

            ReportProgress("Starting workspace launch...");
            var launched = await _runtimeService.LaunchWorkspaceAsync(workspace.Id, cancellationToken, request.Progress).ConfigureAwait(false);
            return launched
                ? new CreateResult(true, "Workspace created and launched.", workspace, Launched: true)
                : new CreateResult(false, "Workspace created but launch failed.", workspace, Launched: false);
        }

        public async Task<SwitchResult> SwitchByIdAsync(string workspaceId, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(WorkspaceTemplateOrchestrator));

            var trimmed = (workspaceId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return new SwitchResult(false, "Workspace ID is required.", string.Empty, new WorkspaceSwitchDiagnostics
                {
                    Ok = false,
                    WorkspaceId = string.Empty,
                    Errors = new List<string> { "not-found: workspace id is required." },
                });
            }

            var diagnostics = await _runtimeService
                .LaunchWorkspaceDetailedAsync(trimmed, cancellationToken, allowLaunchMissingWindows: false)
                .ConfigureAwait(false)
                ?? new WorkspaceSwitchDiagnostics
                {
                    Ok = false,
                    WorkspaceId = trimmed,
                    Errors = new List<string> { "launch-timeout: runtime did not return diagnostics." },
                };

            if (diagnostics.Ok)
            {
                var hasWarnings = diagnostics.Errors != null && diagnostics.Errors.Count > 0;
                return new SwitchResult(true, hasWarnings ? "Workspace switched with warnings." : "Workspace switched.", diagnostics.WorkspaceId, diagnostics);
            }

            var firstError = diagnostics.Errors != null && diagnostics.Errors.Count > 0
                ? diagnostics.Errors[0]
                : $"Workspace '{trimmed}' failed to launch.";
            return new SwitchResult(false, firstError, diagnostics.WorkspaceId, diagnostics);
        }

        public async Task<SwitchResult> SwitchByTemplateAndNameAsync(
            string templateName,
            string instanceName,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(WorkspaceTemplateOrchestrator));

            var trimmedTemplate = WorkspaceStoragePaths.NormalizeTemplateName(templateName);
            var trimmedInstance = (instanceName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trimmedTemplate) || string.IsNullOrWhiteSpace(trimmedInstance))
            {
                return new SwitchResult(false, "Both --template and --name are required.", string.Empty, new WorkspaceSwitchDiagnostics
                {
                    Ok = false,
                    WorkspaceId = string.Empty,
                    Errors = new List<string> { "not-found: both template and name are required." },
                });
            }

            var existing = await _definitionStore.LoadAllAsync(cancellationToken).ConfigureAwait(false);
            var match = existing.FirstOrDefault(ws =>
                ws != null
                && string.Equals(ws.TemplateName, trimmedTemplate, StringComparison.OrdinalIgnoreCase)
                && string.Equals(ws.InstanceName, trimmedInstance, StringComparison.OrdinalIgnoreCase));

            if (match == null || string.IsNullOrWhiteSpace(match.Id))
            {
                return new SwitchResult(false, $"Workspace '{trimmedTemplate} / {trimmedInstance}' was not found.", string.Empty, new WorkspaceSwitchDiagnostics
                {
                    Ok = false,
                    WorkspaceId = string.Empty,
                    Errors = new List<string> { $"not-found: workspace '{trimmedTemplate}/{trimmedInstance}' was not found." },
                });
            }

            return await SwitchByIdAsync(match.Id, cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _runtimeService.Dispose();
            _displayManager.Dispose();
            GC.SuppressFinalize(this);
        }

        private static TemplateDefinition BuildGeneratedTemplate(CreateWorkspaceRequest request)
        {
            var preset = (request.PresetName ?? string.Empty).Trim();
            var layout = (request.LayoutStrategy ?? string.Empty).Trim();
            var apps = MergeApps(BuildDefaultApps(preset), request.Apps);
            var roles = apps.Select(a => a.Role).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (roles.Count == 0)
            {
                roles.Add("app");
            }

            var templateName = !string.IsNullOrWhiteSpace(request.SaveTemplateName)
                ? WorkspaceStoragePaths.NormalizeTemplateName(request.SaveTemplateName)
                : (!string.IsNullOrWhiteSpace(preset)
                    ? $"preset-{WorkspaceStoragePaths.NormalizeTemplateName(preset)}"
                    : "inline");
            var strategy = !string.IsNullOrWhiteSpace(layout) ? layout : (!string.IsNullOrWhiteSpace(preset) ? preset : "side-by-side");

            var windows = apps.Select(a => new TemplateWindowDefinition
            {
                Role = a.Role,
                Exe = a.Exe,
                WorkingDirectory = a.Cwd,
                Args = a.Args,
                Init = a.Init,
                Monitor = a.Monitor,
                MatchHints = new TemplateMatchHints { ProcessName = InferProcessName(a.Exe) },
            }).ToList();

            return new TemplateDefinition
            {
                SchemaVersion = 1,
                Name = templateName,
                DisplayName = BuildDisplayNameFromTemplateName(templateName),
                Description = "Generated from ws new.",
                RequiresRepo = windows.Any(w =>
                    ContainsRepoToken(w.Exe) || ContainsRepoToken(w.WorkingDirectory) || ContainsRepoToken(w.Args) || ContainsRepoToken(w.Init)),
                FocusPriority = request.FocusRoles != null && request.FocusRoles.Count > 0
                    ? request.FocusRoles.Select(r => r.Trim().ToLowerInvariant()).Where(r => !string.IsNullOrWhiteSpace(r)).ToList()
                    : BuildDefaultFocusRoles(roles),
                Layout = new TemplateLayoutDefinition
                {
                    Strategy = strategy,
                    MonitorPolicy = "primary",
                    Preset = preset,
                    Slots = WorkspaceLayoutEngine.BuildSlots(strategy, roles),
                },
                Windows = windows,
                Agent = new TemplateAgentDefinition(),
                Creation = new TemplateCreationDefinition
                {
                    CreateWorktreeByDefault = false,
                    WorktreeBaseBranch = "main",
                },
            };
        }

        private static List<WorkspaceAppSpec> MergeApps(
            IReadOnlyList<WorkspaceAppSpec> presetApps,
            IReadOnlyList<WorkspaceAppSpec> inlineApps)
        {
            var merged = new List<WorkspaceAppSpec>();
            var byRole = new Dictionary<string, WorkspaceAppSpec>(StringComparer.OrdinalIgnoreCase);

            if (presetApps != null)
            {
                foreach (var app in presetApps)
                {
                    if (string.IsNullOrWhiteSpace(app.Role))
                    {
                        continue;
                    }

                    byRole[app.Role] = app;
                }
            }

            if (inlineApps != null)
            {
                foreach (var app in inlineApps)
                {
                    if (string.IsNullOrWhiteSpace(app.Role))
                    {
                        continue;
                    }

                    byRole[app.Role] = app;
                }
            }

            merged.AddRange(byRole.Values);
            return merged;
        }

        private static void ApplyOverrides(TemplateDefinition template, CreateWorkspaceRequest request)
        {
            if (request.FocusRoles != null && request.FocusRoles.Count > 0)
            {
                template.FocusPriority = request.FocusRoles
                    .Where(r => !string.IsNullOrWhiteSpace(r))
                    .Select(r => r.Trim().ToLowerInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            if (!string.IsNullOrWhiteSpace(request.LayoutStrategy))
            {
                var roles = template.Windows.Select(w => w.Role).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                template.Layout ??= new TemplateLayoutDefinition();
                template.Layout.Strategy = request.LayoutStrategy.Trim();
                template.Layout.Slots = WorkspaceLayoutEngine.BuildSlots(template.Layout.Strategy, roles);
            }
        }

        private WorkspaceDefinition BuildWorkspaceDefinition(TemplateDefinition template, string instanceName, string repoRoot)
        {
            var monitors = _displayManager.GetSnapshot();
            var slots = template.Layout?.Slots ?? new List<TemplateLayoutSlotDefinition>();
            var slotMap = WorkspaceLayoutEngine.BuildSlotLookup(slots);
            var globalMonitorPolicy = template.Layout?.MonitorPolicy;
            var fallbackMonitor = WorkspaceLayoutEngine.ResolveMonitor(monitors, globalMonitorPolicy, null);
            var occupiedByMonitor = new Dictionary<string, List<WorkspaceLayoutEngine.LayoutRect>>(StringComparer.OrdinalIgnoreCase);

            var apps = new List<ApplicationDefinition>();
            foreach (var window in template.Windows)
            {
                if (window == null || string.IsNullOrWhiteSpace(window.Role) || string.IsNullOrWhiteSpace(window.Exe))
                {
                    continue;
                }

                slotMap.TryGetValue(window.Role, out var slot);
                var monitor = WorkspaceLayoutEngine.ResolveMonitor(monitors, globalMonitorPolicy, window.Monitor) ?? fallbackMonitor;
                var monitorRect = monitor != null && !monitor.DpiAwareWorkRect.IsEmpty
                    ? monitor.DpiAwareWorkRect
                    : (monitor?.DpiAwareRect ?? new DisplayRect(0, 0, 1600, 900));
                var baseRect = WorkspaceLayoutEngine.ComputeRect(slot, monitorRect);
                var monitorKey = monitor?.Id ?? $"index:{monitor?.Index ?? 0}";
                if (!occupiedByMonitor.TryGetValue(monitorKey, out var occupied))
                {
                    occupied = new List<WorkspaceLayoutEngine.LayoutRect>();
                    occupiedByMonitor[monitorKey] = occupied;
                }

                var resolvedRect = WorkspaceLayoutEngine.ResolveOverlap(baseRect, occupied, monitorRect);
                occupied.Add(resolvedRect);
                var exe = ApplyTokens(window.Exe, template, instanceName, repoRoot);
                var args = ApplyTokens(window.Args, template, instanceName, repoRoot);
                var init = ApplyTokens(window.Init, template, instanceName, repoRoot);
                var cwd = ApplyTokens(window.WorkingDirectory, template, instanceName, repoRoot);
                var commandLine = string.IsNullOrWhiteSpace(args)
                    ? init
                    : (string.IsNullOrWhiteSpace(init) ? args : $"{args} {init}".Trim());

                ApplyRepoScopedDefaults(template, window, repoRoot, exe, ref cwd, ref commandLine);

                apps.Add(new ApplicationDefinition
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = BuildApplicationName(exe, window.Role),
                    Role = window.Role ?? string.Empty,
                    Path = exe,
                    Title = window.MatchHints?.Title ?? string.Empty,
                    CommandLineArguments = commandLine,
                    WorkingDirectory = cwd,
                    // For template-created workspaces, first launch should create dedicated instances
                    // instead of stealing unrelated already-open windows. Once launched, binding reuse applies.
                    LaunchNewIfUnbound = true,
                    AppUserModelId = window.MatchHints?.AppUserModelId ?? string.Empty,
                    MonitorIndex = monitor?.Index ?? 0,
                    Position = new ApplicationDefinition.ApplicationPosition
                    {
                        X = resolvedRect.X,
                        Y = resolvedRect.Y,
                        Width = resolvedRect.Width,
                        Height = resolvedRect.Height,
                    },
                });
            }

            var title = BuildWorkspaceTitle(template, instanceName);
            return new WorkspaceDefinition
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = title,
                TemplateName = template.Name,
                InstanceName = instanceName,
                WorkspaceTitle = title,
                RepoRoot = repoRoot ?? string.Empty,
                FocusPriority = template.FocusPriority?.Select(role => role).ToList() ?? new List<string>(),
                CreationTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                IsShortcutNeeded = false,
                MoveExistingWindows = true,
                RuntimeSessionOnly = true,
                RuntimeSessionId = WorkspaceRuntimeSession.SessionId,
                Monitors = monitors.Select(m => new MonitorDefinition
                {
                    Id = m.Id,
                    InstanceId = m.InstanceId,
                    Number = m.Index,
                    Dpi = m.Dpi,
                    DpiAwareRect = new MonitorDefinition.MonitorRect { Left = m.DpiAwareRect.Left, Top = m.DpiAwareRect.Top, Width = m.DpiAwareRect.Width, Height = m.DpiAwareRect.Height },
                    DpiUnawareRect = new MonitorDefinition.MonitorRect { Left = m.DpiUnawareRect.Left, Top = m.DpiUnawareRect.Top, Width = m.DpiUnawareRect.Width, Height = m.DpiUnawareRect.Height },
                    DpiAwareWorkRect = new MonitorDefinition.MonitorRect { Left = m.DpiAwareWorkRect.Left, Top = m.DpiAwareWorkRect.Top, Width = m.DpiAwareWorkRect.Width, Height = m.DpiAwareWorkRect.Height },
                    DpiUnawareWorkRect = new MonitorDefinition.MonitorRect { Left = m.DpiUnawareWorkRect.Left, Top = m.DpiUnawareWorkRect.Top, Width = m.DpiUnawareWorkRect.Width, Height = m.DpiUnawareWorkRect.Height },
                }).ToList(),
                Applications = apps,
            };
        }

        private static string ResolveRepoRoot(string explicitRepo, string defaultRepo)
        {
            var candidate = string.IsNullOrWhiteSpace(explicitRepo) ? defaultRepo : explicitRepo;
            return string.IsNullOrWhiteSpace(candidate) ? string.Empty : Environment.ExpandEnvironmentVariables(candidate).Trim();
        }

        private static void ApplyRepoScopedDefaults(
            TemplateDefinition template,
            TemplateWindowDefinition window,
            string repoRoot,
            string exe,
            ref string cwd,
            ref string commandLine)
        {
            if (string.IsNullOrWhiteSpace(repoRoot) || window == null)
            {
                return;
            }

            var role = (window.Role ?? string.Empty).Trim();
            var isVsCodeEditor = string.Equals(role, "editor", StringComparison.OrdinalIgnoreCase)
                && IsExecutable(exe, "code.exe");
            if (isVsCodeEditor)
            {
                cwd = repoRoot;
                if (string.IsNullOrWhiteSpace(commandLine))
                {
                    commandLine = $"--new-window {QuoteArgument(repoRoot)}";
                    return;
                }

                if (!ContainsArgumentToken(commandLine, "--new-window")
                    && !ContainsArgumentToken(commandLine, "-n"))
                {
                    commandLine = $"--new-window {commandLine}".Trim();
                }

                var hasRepoArg = commandLine.IndexOf(repoRoot, StringComparison.OrdinalIgnoreCase) >= 0
                    || commandLine.IndexOf("{repo}", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!hasRepoArg)
                {
                    commandLine = $"{commandLine} {QuoteArgument(repoRoot)}".Trim();
                }

                return;
            }

            var isLogsExplorer = string.Equals(role, "logs", StringComparison.OrdinalIgnoreCase)
                && IsExecutable(exe, "explorer.exe");
            if (isLogsExplorer)
            {
                if (string.IsNullOrWhiteSpace(commandLine))
                {
                    commandLine = QuoteArgument(repoRoot);
                }

                if (!ContainsExplorerNewWindowSwitch(commandLine))
                {
                    commandLine = $"/n, {commandLine}".Trim();
                }

                return;
            }

            var isTerminal = string.Equals(role, "terminal", StringComparison.OrdinalIgnoreCase)
                && IsExecutable(exe, "wt.exe");
            if (!isTerminal || template?.Agent?.Enabled != true)
            {
                return;
            }

            cwd = repoRoot;
            if (!string.IsNullOrWhiteSpace(commandLine))
            {
                commandLine = NormalizeAgentTerminalCommandLine(commandLine, template.Agent?.Name);
                return;
            }

            var agentCommand = NormalizeAgentCommandText(template.Agent?.Command, template.Agent?.Name);
            commandLine = $"pwsh -NoExit -Command {QuoteArgument(agentCommand)}";
        }

        private static string NormalizeAgentTerminalCommandLine(string commandLine, string fallbackAgentName)
        {
            var current = (commandLine ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(current))
            {
                return string.Empty;
            }

            var hasKnownShell = current.IndexOf("pwsh", StringComparison.OrdinalIgnoreCase) >= 0
                || current.IndexOf("powershell", StringComparison.OrdinalIgnoreCase) >= 0
                || current.IndexOf("cmd.exe", StringComparison.OrdinalIgnoreCase) >= 0
                || current.StartsWith("cmd ", StringComparison.OrdinalIgnoreCase)
                || string.Equals(current, "cmd", StringComparison.OrdinalIgnoreCase)
                || current.IndexOf(" wsl", StringComparison.OrdinalIgnoreCase) >= 0
                || current.StartsWith("wsl", StringComparison.OrdinalIgnoreCase)
                || current.IndexOf(" bash", StringComparison.OrdinalIgnoreCase) >= 0
                || current.StartsWith("bash", StringComparison.OrdinalIgnoreCase);
            if (hasKnownShell)
            {
                return current;
            }

            var normalizedCommand = NormalizeAgentCommandText(current, fallbackAgentName);
            return string.IsNullOrWhiteSpace(normalizedCommand)
                ? current
                : $"pwsh -NoExit -Command {QuoteArgument(normalizedCommand)}";
        }

        private static bool ContainsExplorerNewWindowSwitch(string arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments))
            {
                return false;
            }

            var value = arguments.Trim();
            return value.StartsWith("/n", StringComparison.OrdinalIgnoreCase)
                || value.Contains(" /n", StringComparison.OrdinalIgnoreCase)
                || value.Contains(",/n", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsArgumentToken(string arguments, string token)
        {
            if (string.IsNullOrWhiteSpace(arguments) || string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            return arguments.StartsWith(token + " ", StringComparison.OrdinalIgnoreCase)
                || arguments.Equals(token, StringComparison.OrdinalIgnoreCase)
                || arguments.EndsWith(" " + token, StringComparison.OrdinalIgnoreCase)
                || arguments.Contains(" " + token + " ", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsExecutable(string executablePath, string expectedExe)
        {
            if (string.IsNullOrWhiteSpace(executablePath) || string.IsNullOrWhiteSpace(expectedExe))
            {
                return false;
            }

            var actual = Path.GetFileName(executablePath.Trim().Trim('"'));
            return string.Equals(actual, expectedExe, StringComparison.OrdinalIgnoreCase);
        }

        private static string QuoteArgument(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            var escaped = normalized.Replace("\"", "\\\"", StringComparison.Ordinal);
            return $"\"{escaped}\"";
        }

        private static string NormalizeAgentCommandText(string value, string fallbackName = null)
        {
            var normalized = DequoteArgument(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                normalized = string.IsNullOrWhiteSpace(fallbackName) ? "copilot" : fallbackName.Trim();
            }

            return DequoteArgument(normalized);
        }

        private static string DequoteArgument(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            for (var i = 0; i < 4; i++)
            {
                var before = normalized;
                normalized = normalized.Replace("\\\"", "\"", StringComparison.Ordinal).Trim();

                if (normalized.Length >= 2 && normalized[0] == '"' && normalized[^1] == '"')
                {
                    normalized = normalized.Substring(1, normalized.Length - 2).Trim();
                }

                if (normalized.Length >= 2 && normalized[0] == '\'' && normalized[^1] == '\'')
                {
                    normalized = normalized.Substring(1, normalized.Length - 2).Trim();
                }

                if (string.Equals(before, normalized, StringComparison.Ordinal))
                {
                    break;
                }
            }

            return normalized.Trim();
        }

        private static string ApplyTokens(string value, TemplateDefinition template, string instanceName, string repoRoot)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return value
                .Replace("{repo}", repoRoot ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("{instanceName}", instanceName ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("{workspaceTitle}", BuildWorkspaceTitle(template, instanceName), StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildWorkspaceTitle(TemplateDefinition template, string instanceName)
        {
            var display = string.IsNullOrWhiteSpace(template?.DisplayName) ? template?.Name : template.DisplayName;
            return $"{(string.IsNullOrWhiteSpace(display) ? "Workspace" : display)} · {instanceName}".Trim();
        }

        private static string BuildEphemeralInstanceName(TemplateDefinition template)
        {
            var baseName = string.IsNullOrWhiteSpace(template?.Name) ? "workspace" : template.Name;
            var slug = BuildWorktreeSlug(baseName);
            if (string.IsNullOrWhiteSpace(slug))
            {
                slug = "workspace";
            }

            return $"{slug}-{DateTime.Now:yyyyMMdd-HHmmss}";
        }

        private static string BuildApplicationName(string exe, string role)
        {
            var file = string.IsNullOrWhiteSpace(exe) ? string.Empty : Path.GetFileName(exe);
            return string.IsNullOrWhiteSpace(file) ? (string.IsNullOrWhiteSpace(role) ? "app" : role) : file;
        }

        private static string InferProcessName(string exe)
        {
            var file = string.IsNullOrWhiteSpace(exe) ? string.Empty : Path.GetFileName(exe.Trim().Trim('"'));
            if (string.IsNullOrWhiteSpace(file))
            {
                return string.Empty;
            }

            return file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? file[..^4]
                : file;
        }

        private static bool ContainsRepoToken(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.IndexOf("{repo}", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static async Task<WorktreeCreationResult> CreateGitWorktreeAsync(
            string repoRoot,
            string instanceName,
            string baseBranch,
            CancellationToken cancellationToken)
        {
            var normalizedRepo = (repoRoot ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalizedRepo))
            {
                return new WorktreeCreationResult(false, string.Empty, "Repository path is required for worktree creation.");
            }

            if (!Directory.Exists(normalizedRepo))
            {
                return new WorktreeCreationResult(false, string.Empty, $"Repo path '{normalizedRepo}' does not exist.");
            }

            if (!IsGitRepository(normalizedRepo))
            {
                return new WorktreeCreationResult(false, string.Empty, $"Repo path '{normalizedRepo}' is not a git repository.");
            }

            var slug = BuildWorktreeSlug(instanceName);
            if (string.IsNullOrWhiteSpace(slug))
            {
                return new WorktreeCreationResult(false, string.Empty, "Workspace name is invalid for worktree branch naming.");
            }

            var branchName = slug;
            var worktreeRoot = Path.Combine(normalizedRepo, ".worktrees");
            var worktreePath = Path.Combine(worktreeRoot, slug);
            if (Directory.Exists(worktreePath))
            {
                return new WorktreeCreationResult(false, string.Empty, $"Worktree path '{worktreePath}' already exists.");
            }

            Directory.CreateDirectory(worktreeRoot);

            var effectiveBaseBranch = string.IsNullOrWhiteSpace(baseBranch)
                ? "main"
                : baseBranch.Trim();

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                startInfo.ArgumentList.Add("-C");
                startInfo.ArgumentList.Add(normalizedRepo);
                startInfo.ArgumentList.Add("worktree");
                startInfo.ArgumentList.Add("add");
                startInfo.ArgumentList.Add("-b");
                startInfo.ArgumentList.Add(branchName);
                startInfo.ArgumentList.Add(worktreePath);
                startInfo.ArgumentList.Add(effectiveBaseBranch);

                using var process = new Process { StartInfo = startInfo };
                if (!process.Start())
                {
                    return new WorktreeCreationResult(false, string.Empty, "Failed to start git process.");
                }

                var stdOutTask = process.StandardOutput.ReadToEndAsync();
                var stdErrTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                var stdOut = (await stdOutTask.ConfigureAwait(false)).Trim();
                var stdErr = (await stdErrTask.ConfigureAwait(false)).Trim();

                if (process.ExitCode != 0)
                {
                    var details = string.IsNullOrWhiteSpace(stdErr) ? stdOut : stdErr;
                    if (string.IsNullOrWhiteSpace(details))
                    {
                        details = $"git exited with code {process.ExitCode}.";
                    }

                    return new WorktreeCreationResult(false, string.Empty, $"Failed to create git worktree: {details}");
                }

                if (!Directory.Exists(worktreePath))
                {
                    return new WorktreeCreationResult(false, string.Empty, $"Worktree path '{worktreePath}' was not created.");
                }

                return new WorktreeCreationResult(true, worktreePath, $"Created worktree '{branchName}'.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Win32Exception ex)
            {
                return new WorktreeCreationResult(false, string.Empty, $"Failed to execute git: {ex.Message}");
            }
            catch (Exception ex)
            {
                return new WorktreeCreationResult(false, string.Empty, $"Failed to create git worktree: {ex.Message}");
            }
        }

        private static bool IsGitRepository(string repoRoot)
        {
            var gitPath = Path.Combine(repoRoot, ".git");
            return Directory.Exists(gitPath) || File.Exists(gitPath);
        }

        private static string BuildWorktreeSlug(string workspaceName)
        {
            if (string.IsNullOrWhiteSpace(workspaceName))
            {
                return string.Empty;
            }

            var source = workspaceName.Trim().ToLowerInvariant();
            var builder = new StringBuilder(source.Length);
            var previousWasDash = false;
            foreach (var ch in source)
            {
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
                {
                    builder.Append(ch);
                    previousWasDash = false;
                    continue;
                }

                if (!previousWasDash)
                {
                    builder.Append('-');
                    previousWasDash = true;
                }
            }

            var slug = builder.ToString().Trim('-');
            return string.IsNullOrWhiteSpace(slug) ? "workspace" : slug;
        }

        private static TemplateDefinition CloneTemplate(TemplateDefinition template)
        {
            return new TemplateDefinition
            {
                SchemaVersion = template.SchemaVersion,
                Name = template.Name,
                DisplayName = template.DisplayName,
                Description = template.Description,
                RequiresRepo = template.RequiresRepo,
                DefaultRepoRoot = template.DefaultRepoRoot,
                FocusPriority = template.FocusPriority?.Select(r => r).ToList() ?? new List<string>(),
                Layout = new TemplateLayoutDefinition
                {
                    Strategy = template.Layout?.Strategy ?? string.Empty,
                    MonitorPolicy = template.Layout?.MonitorPolicy ?? string.Empty,
                    Preset = template.Layout?.Preset ?? string.Empty,
                    Slots = template.Layout?.Slots?.Select(s => new TemplateLayoutSlotDefinition
                    {
                        Role = s.Role,
                        X = s.X,
                        Y = s.Y,
                        Width = s.Width,
                        Height = s.Height,
                        MinWidth = s.MinWidth,
                        MinHeight = s.MinHeight,
                    }).ToList() ?? new List<TemplateLayoutSlotDefinition>(),
                },
                Windows = template.Windows?.Select(w => new TemplateWindowDefinition
                {
                    Role = w.Role,
                    Exe = w.Exe,
                    WorkingDirectory = w.WorkingDirectory,
                    Args = w.Args,
                    Init = w.Init,
                    Monitor = w.Monitor,
                    MatchHints = new TemplateMatchHints
                    {
                        ProcessName = w.MatchHints?.ProcessName ?? string.Empty,
                        ProcessPath = w.MatchHints?.ProcessPath ?? string.Empty,
                        Title = w.MatchHints?.Title ?? string.Empty,
                        AppUserModelId = w.MatchHints?.AppUserModelId ?? string.Empty,
                    },
                }).ToList() ?? new List<TemplateWindowDefinition>(),
                Agent = new TemplateAgentDefinition
                {
                    Enabled = template.Agent?.Enabled ?? false,
                    Name = template.Agent?.Name ?? "copilot",
                    Command = template.Agent?.Command ?? string.Empty,
                    WorkingDirectory = template.Agent?.WorkingDirectory ?? "{repo}",
                },
                Creation = new TemplateCreationDefinition
                {
                    CreateWorktreeByDefault = template.Creation?.CreateWorktreeByDefault ?? false,
                    WorktreeBaseBranch = template.Creation?.WorktreeBaseBranch ?? "main",
                },
            };
        }

        private static List<WorkspaceAppSpec> BuildDefaultApps(string preset)
        {
            if (string.IsNullOrWhiteSpace(preset))
            {
                return new List<WorkspaceAppSpec>();
            }

            if (string.Equals(preset, "triple-columns", StringComparison.OrdinalIgnoreCase))
            {
                return new List<WorkspaceAppSpec>
                {
                    new("editor", "code.exe", "{repo}", "{repo}", string.Empty, string.Empty),
                    new("terminal", "wt.exe", "{repo}", string.Empty, string.Empty, string.Empty),
                    new("browser", "msedge.exe", string.Empty, string.Empty, string.Empty, string.Empty),
                };
            }

            if (string.Equals(preset, "grid-2x2", StringComparison.OrdinalIgnoreCase))
            {
                return new List<WorkspaceAppSpec>
                {
                    new("editor", "code.exe", "{repo}", "{repo}", string.Empty, string.Empty),
                    new("terminal", "wt.exe", "{repo}", string.Empty, string.Empty, string.Empty),
                    new("browser", "msedge.exe", string.Empty, string.Empty, string.Empty, string.Empty),
                    new("notes", "notepad.exe", string.Empty, string.Empty, string.Empty, string.Empty),
                };
            }

            return new List<WorkspaceAppSpec>
            {
                new("editor", "code.exe", "{repo}", "{repo}", string.Empty, string.Empty),
                new("terminal", "wt.exe", "{repo}", string.Empty, string.Empty, string.Empty),
            };
        }

        private static List<string> BuildDefaultFocusRoles(IReadOnlyList<string> roles)
        {
            var ordered = new List<string>();
            if (roles.Any(r => string.Equals(r, "editor", StringComparison.OrdinalIgnoreCase)))
            {
                ordered.Add("editor");
            }

            if (roles.Any(r => string.Equals(r, "terminal", StringComparison.OrdinalIgnoreCase)))
            {
                ordered.Add("terminal");
            }

            foreach (var role in roles)
            {
                if (!ordered.Contains(role, StringComparer.OrdinalIgnoreCase))
                {
                    ordered.Add(role);
                }
            }

            return ordered;
        }

        private static string BuildDisplayNameFromTemplateName(string templateName)
        {
            if (string.IsNullOrWhiteSpace(templateName))
            {
                return "Workspace";
            }

            var parts = templateName.Split('-', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                parts[i] = part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..];
            }

            return parts.Length == 0 ? "Workspace" : string.Join(" ", parts);
        }

    }
}
