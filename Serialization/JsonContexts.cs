using System.Text.Json;
using System.Text.Json.Serialization;

namespace TopToolbar.Serialization;

internal sealed class ProviderIconTypeConverter : JsonStringEnumConverter<TopToolbar.Models.Providers.ProviderIconType>
{
    public ProviderIconTypeConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}

internal sealed class ExternalProviderTypeConverter : JsonStringEnumConverter<TopToolbar.Providers.Configuration.ExternalProviderType>
{
    public ExternalProviderTypeConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(TopToolbar.Extensions.ExtensionManifest))]
internal partial class ExtensionManifestJsonContext : JsonSerializerContext
{
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    Converters = new[] { typeof(ProviderIconTypeConverter) },
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(TopToolbar.Models.Providers.WorkspaceProviderConfig))]
[JsonSerializable(typeof(TopToolbar.Services.Workspaces.WorkspaceDefinition))]
[JsonSerializable(typeof(TopToolbar.Services.Workspaces.WorkspaceDocument))]
[JsonSerializable(typeof(TopToolbar.Services.Workspaces.TemplateDefinition))]
[JsonSerializable(typeof(TopToolbar.Services.Workspaces.WorkspaceSwitchDiagnostics))]
internal partial class WorkspaceProviderJsonContext : JsonSerializerContext
{
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    Converters = new[] { typeof(ExternalProviderTypeConverter) },
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(TopToolbar.Providers.Configuration.ProviderConfig))]
internal partial class ProviderConfigJsonContext : JsonSerializerContext
{
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    AllowTrailingCommas = true,
    WriteIndented = true,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(TopToolbar.Models.ToolbarConfig))]
internal partial class ToolbarConfigJsonContext : JsonSerializerContext
{
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    Converters = new[] { typeof(ProviderIconTypeConverter) })]
[JsonSerializable(typeof(TopToolbar.Services.Workspaces.WorkspaceDefinition))]
internal partial class DefaultJsonContext : JsonSerializerContext
{
}

// AOT-compatible context for deep cloning workspace types
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(TopToolbar.Services.Workspaces.WorkspaceDefinition))]
[JsonSerializable(typeof(TopToolbar.Services.Workspaces.ApplicationDefinition))]
[JsonSerializable(typeof(TopToolbar.Services.Workspaces.MonitorDefinition))]
internal partial class DeepCloneJsonContext : JsonSerializerContext
{
}
