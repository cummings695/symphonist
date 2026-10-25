using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Symphonist.Core.Onboarding;

public sealed class ProjectYamlEditException(string message) : Exception(message);

/// <summary>
/// A project file seen as top-level sections, so single sections can be replaced while every other byte (comments,
/// hand formatting) is kept. A section starts at its key's line and ends just before the next top-level key's line,
/// minus trailing blank lines and column-0 comment lines, which stay with the next section.
/// </summary>
public sealed class ProjectYamlDocument
{
    private const string Refused = "edit this file by hand";

    private readonly string _text;
    private readonly string _newLine;
    private readonly List<int> _lineStarts;   // offset of each line's first character; one extra entry = text length
    private readonly List<(string Key, int Start, int End)> _sections;   // character offsets, End exclusive

    private ProjectYamlDocument(string text, List<(string Key, int Line)> keys)
    {
        _text = text;
        _newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        _lineStarts = [0];
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n' && i + 1 < text.Length) _lineStarts.Add(i + 1);
        _lineStarts.Add(text.Length);

        _sections = [];
        for (var k = 0; k < keys.Count; k++)
        {
            var first = keys[k].Line - 1;
            var next = k + 1 < keys.Count ? keys[k + 1].Line - 1 : _lineStarts.Count - 1;
            var last = next - 1;   // zero-based index of the section's last line
            while (last > first && IsBlankOrComment(LineText(last))) last--;
            _sections.Add((keys[k].Key, _lineStarts[first], _lineStarts[last + 1]));
        }
    }

    public IReadOnlyList<string> Keys => _sections.Select(s => s.Key).ToList();

    /// <exception cref="ProjectYamlEditException">
    /// The root is not a block mapping, there are several documents, or the file uses an anchor or alias.
    /// </exception>
    public static ProjectYamlDocument Parse(string text)
    {
        var keys = new List<(string Key, int Line)>();
        try
        {
            var parser = new Parser(new StringReader(text));
            var documents = 0;
            var depth = 0;
            var rootSeen = false;
            var expectKey = true;
            while (parser.MoveNext())
            {
                var e = parser.Current!;
                if (e is AnchorAlias) throw Refuse("it uses YAML aliases");
                if (e is NodeEvent { Anchor.IsEmpty: false }) throw Refuse("it uses YAML anchors");
                switch (e)
                {
                    case DocumentStart:
                        if (++documents > 1) throw Refuse("it holds several YAML documents");
                        break;
                    case MappingStart mapping when depth == 0:
                        if (mapping.Style == MappingStyle.Flow) throw Refuse("its root is a flow mapping");
                        rootSeen = true;
                        depth++;
                        break;
                    case Scalar or SequenceStart or MappingStart when depth == 0:
                        throw Refuse("its root is not a mapping");
                    case Scalar scalar when depth == 1:
                        if (expectKey)
                        {
                            if (keys.Any(k => k.Key == scalar.Value)) throw Refuse($"key '{scalar.Value}' appears twice");
                            keys.Add((scalar.Value, (int)scalar.Start.Line));
                        }
                        expectKey = !expectKey;
                        break;
                    case SequenceStart or MappingStart:
                        if (depth == 1 && expectKey) throw Refuse("it has a complex top-level key");
                        depth++;
                        break;
                    case SequenceEnd or MappingEnd:
                        depth--;
                        if (depth == 1) expectKey = true;
                        break;
                }
            }
            if (!rootSeen) throw Refuse("it is not a mapping of sections");
        }
        catch (YamlException ex)
        {
            throw Refuse($"it is not valid YAML ({ex.Message})");
        }
        return new ProjectYamlDocument(text, keys);
    }

    /// <summary>
    /// Replaces (text), removes (null) or appends (key not present) sections; all other text is kept byte-for-byte.
    /// Section texts use "\n"; they are written with the file's line ending.
    /// </summary>
    public string Replace(IReadOnlyDictionary<string, string?> sections)
    {
        var result = new StringBuilder();
        var position = 0;
        foreach (var (key, start, end) in _sections)
        {
            if (!sections.TryGetValue(key, out var replacement)) continue;
            result.Append(_text, position, start - position);
            if (replacement is not null) result.Append(WithNewLines(replacement));
            position = end;
        }
        result.Append(_text, position, _text.Length - position);

        foreach (var (key, text) in sections)
        {
            if (text is null || _sections.Any(s => s.Key == key)) continue;
            if (result.Length > 0 && result[^1] != '\n') result.Append(_newLine);
            if (result.Length > 0) result.Append(_newLine);
            result.Append(WithNewLines(text));
        }
        return result.ToString();
    }

    private string WithNewLines(string text)
    {
        if (!text.EndsWith('\n')) text += "\n";
        return _newLine == "\n" ? text : text.Replace("\n", _newLine, StringComparison.Ordinal);
    }

    private string LineText(int index) => _text[_lineStarts[index].._lineStarts[index + 1]];

    private static bool IsBlankOrComment(string line) => string.IsNullOrWhiteSpace(line) || line.StartsWith('#');

    private static ProjectYamlEditException Refuse(string reason) =>
        new($"cannot update this project file safely because {reason}; {Refused}");
}
