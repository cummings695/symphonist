namespace Symphonist.Core.Integrations.Providers;

/// <summary>
/// How a field is edited and written. <see cref="MultilineText"/> is free text that may span lines (written as a
/// literal block scalar when it does); every other scalar kind is a single line.
/// </summary>
public enum ProviderFieldKind { Text, Number, Url, Path, Secret, Choice, List, MultilineText }

/// <summary>One key a provider's YAML section accepts, for the setup screen and the YAML writer.</summary>
/// <param name="Key">camelCase YAML key inside the section (never "type").</param>
/// <param name="Default">The value the parser uses when the key is absent; the writer omits a value equal to it.</param>
public sealed record ProviderField(string Key, string Label, ProviderFieldKind Kind, bool Required = false,
    string? Default = null, IReadOnlyList<string>? Options = null, string Help = "");
