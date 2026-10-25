using System.Globalization;
using System.Reflection;
using Symphonist.Core.Config;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Symphonist.Core.Integrations.Providers;

/// <summary>
/// One raw <c>tracker</c>/<c>repo</c>/<c>build</c> block of a project file. The keys every provider of that kind
/// shares are read by the subclass; <see cref="Bind{T}"/> maps the rest strictly onto a provider-specific DTO.
/// </summary>
public class ProviderSection
{
    private static readonly ISerializer Serializer = new SerializerBuilder().Build();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    private readonly IReadOnlyDictionary<string, object?> _values;
    private readonly HashSet<string> _sharedKeys;

    public ProviderSection(string path, IReadOnlyDictionary<string, object?> values, string configDirectory,
        IReadOnlyCollection<string> sharedKeys)
    {
        Path = path;
        _values = values;
        ConfigDirectory = configDirectory;
        _sharedKeys = new HashSet<string>(sharedKeys, StringComparer.Ordinal);
        Type = Required(Text("type"), "type");
    }

    /// <summary>"tracker", "repo" or "build".</summary>
    public string Path { get; }

    public string Type { get; }

    public string ConfigDirectory { get; }

    /// <summary>
    /// Strict: every key not in the shared keys must be a camelCase property of <typeparamref name="T"/>, else
    /// "unknown field '{Path}.{key}'".
    /// </summary>
    public T Bind<T>() where T : new()
    {
        var known = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .Select(p => CamelCaseNamingConvention.Instance.Apply(p.Name))
            .ToHashSet(StringComparer.Ordinal);

        var rest = new Dictionary<string, object?>();
        foreach (var (key, value) in _values)
        {
            if (_sharedKeys.Contains(key)) continue;
            if (!known.Contains(key)) throw new ProjectConfigException($"unknown field '{Path}.{key}'");
            rest[key] = value;
        }

        try
        {
            return Deserializer.Deserialize<T>(Serializer.Serialize(rest)) ?? new T();
        }
        catch (YamlException ex)
        {
            throw new ProjectConfigException($"invalid '{Path}': {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    public string Required(string? value, string field) =>
        string.IsNullOrWhiteSpace(value) ? throw new ProjectConfigException($"missing required '{Path}.{field}'") : value;

    /// <summary>
    /// The scalar under <paramref name="key"/> as text, or null when absent, null or blank (so defaults apply and
    /// <see cref="Required"/> reports it missing). A list or map is "invalid '{Path}.{key}'".
    /// </summary>
    protected string? Text(string key)
    {
        if (!_values.TryGetValue(key, out var v) || v is null) return null;
        if (v is System.Collections.IEnumerable and not string)
            throw new ProjectConfigException($"invalid '{Path}.{key}'");
        var text = Convert.ToString(v, CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}

public sealed class TrackerSection : ProviderSection
{
    public TrackerSection(IReadOnlyDictionary<string, object?> values, string configDirectory)
        : base("tracker", values, configDirectory, ["type", "tokenSecret"])
    {
        TokenSecret = Required(Text("tokenSecret"), "tokenSecret");
    }

    public string TokenSecret { get; }
}

public sealed class RepoSection : ProviderSection
{
    public RepoSection(IReadOnlyDictionary<string, object?> values, string configDirectory)
        : base("repo", values, configDirectory,
            ["type", "tokenSecret", "localPath", "mainBranch", "productionBranch"])
    {
        TokenSecret = Required(Text("tokenSecret"), "tokenSecret");
        LocalPath = System.IO.Path.GetFullPath(Required(Text("localPath"), "localPath"), configDirectory);
        MainBranch = Text("mainBranch") ?? "main";
        ProductionBranch = Text("productionBranch") ?? "production";
    }

    public string TokenSecret { get; }

    /// <summary>Absolute; relative values resolve against <see cref="ProviderSection.ConfigDirectory"/>.</summary>
    public string LocalPath { get; }

    public string MainBranch { get; }

    public string ProductionBranch { get; }
}

public sealed class BuildSection : ProviderSection
{
    public BuildSection(IReadOnlyDictionary<string, object?> values, string configDirectory)
        : base("build", values, configDirectory, ["type", "timeoutMinutes"])
    {
        var raw = Text("timeoutMinutes");
        if (raw is null) TimeoutMinutes = 45;
        else if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
            TimeoutMinutes = minutes;
        else throw new ProjectConfigException("invalid 'build.timeoutMinutes'");
    }

    public int TimeoutMinutes { get; }
}
