using Symphonist.Core.Config;
using Symphonist.Core.Integrations.Providers;

namespace Symphonist.Integrations.Tests;

public class ProviderFieldsTests
{
    private static readonly ProviderRegistry Registry = BuiltInProviders.Registry;

    private static readonly string Dir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "symphonist-fields"));

    public static TheoryData<string, string> Providers()
    {
        var data = new TheoryData<string, string>();
        foreach (var p in Registry.AllTrackers) data.Add("tracker", p.Type);
        foreach (var p in Registry.AllRepos) data.Add("repo", p.Type);
        foreach (var p in Registry.AllBuilds) data.Add("build", p.Type);
        return data;
    }

    private static IReadOnlyList<ProviderField> FieldsOf(string kind, string type) => kind switch
    {
        "tracker" => Registry.Tracker(type).Fields,
        "repo" => Registry.Repo(type).Fields,
        _ => Registry.Build(type).Fields,
    };

    private static object Dummy(ProviderField f) => f.Key switch
    {
        "projectKey" => "ABC",
        _ => f.Kind switch
        {
            ProviderFieldKind.Number => "7",
            ProviderFieldKind.Url => "https://x.example/",
            ProviderFieldKind.Path => Path.Combine(Dir, "clone"),
            ProviderFieldKind.Secret => "secret-x",
            ProviderFieldKind.Choice => f.Options![0],
            ProviderFieldKind.List => (IReadOnlyList<string>)["a", "b"],
            _ => "x",
        },
    };

    private static Dictionary<string, object?> Values(string kind, string type, bool requiredOnly)
    {
        var values = new Dictionary<string, object?> { ["type"] = type };
        foreach (var f in FieldsOf(kind, type).Where(f => f.Required || !requiredOnly)) values[f.Key] = Dummy(f);
        return values;
    }

    private static (object Config, IReadOnlyDictionary<string, object?> Values) ParseAndMap(
        string kind, IReadOnlyDictionary<string, object?> values)
    {
        var type = (string)values["type"]!;
        switch (kind)
        {
            case "tracker":
                var t = Registry.Tracker(type);
                var tc = t.Parse(new TrackerSection(values, Dir));
                return (tc, t.ToValues(tc));
            case "repo":
                var r = Registry.Repo(type);
                var rc = r.Parse(new RepoSection(values, Dir));
                return (rc, r.ToValues(rc));
            default:
                var b = Registry.Build(type);
                var bc = b.Parse(new BuildSection(values, Dir));
                return (bc, b.ToValues(bc));
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void Fields_include_the_shared_keys_and_never_type(string kind, string type)
    {
        var keys = FieldsOf(kind, type).Select(f => f.Key).ToList();
        Assert.DoesNotContain("type", keys);
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        string[] shared = kind switch
        {
            "tracker" => ["tokenSecret"],
            "repo" => ["localPath", "mainBranch", "productionBranch", "tokenSecret"],
            _ => ["timeoutMinutes"],
        };
        foreach (var key in shared) Assert.Contains(key, keys);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void Every_field_key_binds_strictly(string kind, string type)
    {
        var (_, mapped) = ParseAndMap(kind, Values(kind, type, requiredOnly: false));
        foreach (var f in FieldsOf(kind, type))
            Assert.True(mapped.ContainsKey(f.Key), $"{type}: {f.Key} missing from ToValues");
    }

    [Theory]
    [InlineData("orders.example.yaml")]
    [InlineData("orders-github.example.yaml")]
    public void Values_round_trip_through_parse(string sample)
    {
        var dir = SamplesDirectory();
        var project = ProjectConfigLoader.Load(Path.Combine(dir, sample), Registry);

        var tracker = Registry.Tracker(project.Tracker.Type);
        AssertRoundTrip("tracker", tracker.ToValues(project.Tracker), tracker.Type, dir);
        var repo = Registry.Repo(project.Repo.Type);
        AssertRoundTrip("repo", repo.ToValues(project.Repo), repo.Type, dir);
        var build = Registry.Build(project.Build.Type);
        AssertRoundTrip("build", build.ToValues(project.Build), build.Type, dir);
    }

    private static void AssertRoundTrip(string kind, IReadOnlyDictionary<string, object?> values, string type, string dir)
    {
        var section = new Dictionary<string, object?>(values) { ["type"] = type };
        var again = ParseAndMap(kind, section).Values;
        Assert.Equal(values.Keys.Order(), again.Keys.Order());
        foreach (var (key, value) in values)
        {
            if (value is IReadOnlyList<string> list) Assert.Equal(list, (IReadOnlyList<string>)again[key]!);
            else Assert.Equal(value, again[key]);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void Defaults_match_parser_defaults(string kind, string type)
    {
        var (_, mapped) = ParseAndMap(kind, Values(kind, type, requiredOnly: true));
        foreach (var f in FieldsOf(kind, type).Where(f => f.Default is not null))
            Assert.Equal(f.Default, mapped[f.Key]);
    }

    [Theory]
    [InlineData("bitbucket")]
    [InlineData("github")]
    public void Full_name_keys_rebuild_full_name(string type)
    {
        var provider = Registry.Repo(type);
        Assert.Equal(2, provider.FullNameKeys.Count);
        var values = Values("repo", type, requiredOnly: true);
        var parts = "Acme/Orders".Split('/');
        for (var i = 0; i < 2; i++) values[provider.FullNameKeys[i]] = parts[i];
        var config = provider.Parse(new RepoSection(values, Dir));
        Assert.Equal("Acme/Orders", config.FullName);
    }

    private static string SamplesDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "samples", "projects");
            if (Directory.Exists(candidate)) return candidate;
        }

        throw new DirectoryNotFoundException("samples/projects not found above " + AppContext.BaseDirectory);
    }
}
