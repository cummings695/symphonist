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

    // Line breaks YAML counts besides "\n" and "\r\n"; with these the parser's line numbers would not match ours.
    private static readonly char[] OtherLineBreaks = ['\u0085', '\u2028', '\u2029'];

    private readonly string _text;
    private readonly string _newLine;
    private readonly List<int> _lineStarts;   // offset of each line's first character; one extra entry = text length
    private readonly List<Section> _sections;

    private sealed record Key(string Name, int Line, int Column);   // Line and Column 1-based, as the parser reports

    /// <param name="FirstLine">Zero-based index of the key's line.</param>
    /// <param name="EndLine">Zero-based index one past the section's last line.</param>
    /// <param name="Children">The keys of a block-mapping value, else null.</param>
    private sealed record Section(string Name, int FirstLine, int EndLine, List<Key>? Children);

    private ProjectYamlDocument(string text, List<(Key Key, List<Key>? Children)> keys)
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
            var first = keys[k].Key.Line - 1;
            var next = k + 1 < keys.Count ? keys[k + 1].Key.Line - 1 : _lineStarts.Count - 1;
            var last = next - 1;   // zero-based index of the section's last line
            while (last > first && IsBlankOrComment(LineText(last), 0)) last--;
            _sections.Add(new Section(keys[k].Key.Name, first, last + 1, keys[k].Children));
        }
    }

    public IReadOnlyList<string> Keys => _sections.Select(s => s.Name).ToList();

    /// <exception cref="ProjectYamlEditException">
    /// The root is not a block mapping at column 0, there are several documents or an explicit document end, the file
    /// uses an anchor or alias, a complex or duplicate key, or line breaks other than "\n" and "\r\n".
    /// </exception>
    public static ProjectYamlDocument Parse(string text)
    {
        try
        {
            return new ProjectYamlDocument(text, ReadKeys(text));
        }
        catch (ProjectYamlEditException)
        {
            throw;
        }
        catch (YamlException ex)
        {
            throw Refuse($"it is not valid YAML ({ex.Message})");
        }
        catch (Exception ex)
        {
            throw Refuse($"it could not be read ({ex.Message})");
        }
    }

    private static List<(Key Key, List<Key>? Children)> ReadKeys(string text)
    {
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\r' && (i + 1 == text.Length || text[i + 1] != '\n'))
                throw Refuse("it uses a lone carriage return as a line break");
        if (text.IndexOfAny(OtherLineBreaks) >= 0) throw Refuse("it uses Unicode line separators");

        var keys = new List<(Key Key, List<Key>? Children)>();
        var parser = new Parser(new StringReader(text));
        var documents = 0;
        // One entry per open collection: is it a mapping, is the next node a key, and the keys seen so far.
        var stack = new List<(bool Mapping, bool ExpectKey, HashSet<string>? Keys)>();
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
                case DocumentEnd { IsImplicit: false }:
                    throw Refuse("it has an explicit document end marker (...)");
                case Scalar or SequenceStart or MappingStart when stack.Count == 0:
                    if (e is not MappingStart root) throw Refuse("its root is not a mapping");
                    if (root.Style == MappingStyle.Flow) throw Refuse("its root is a flow mapping");
                    stack.Add((true, true, []));
                    break;
                case Scalar scalar:
                    var (mapping, expectKey, seen) = stack[^1];
                    if (mapping && expectKey)
                    {
                        if (!seen!.Add(scalar.Value)) throw Refuse($"key '{scalar.Value}' appears twice in one mapping");
                        var key = new Key(scalar.Value, (int)scalar.Start.Line, (int)scalar.Start.Column);
                        if (stack.Count == 1)
                        {
                            if (key.Column != 1) throw Refuse("its top-level keys are indented");
                            keys.Add((key, null));
                        }
                        else if (stack.Count == 2 && keys[^1].Children is { } children)
                        {
                            if (children.Any(k => k.Line == key.Line)) keys[^1] = (keys[^1].Key, null);
                            else children.Add(key);
                        }
                    }
                    if (mapping) stack[^1] = (true, !expectKey, seen);
                    break;
                case SequenceStart or MappingStart:
                    if (stack[^1] is { Mapping: true, ExpectKey: true }) throw Refuse("it has a complex key");
                    if (stack.Count == 1 && e is MappingStart { Style: MappingStyle.Block })
                        keys[^1] = (keys[^1].Key, []);
                    stack.Add((e is MappingStart, true, e is MappingStart ? [] : null));
                    break;
                case SequenceEnd or MappingEnd:
                    stack.RemoveAt(stack.Count - 1);
                    if (stack.Count > 0 && stack[^1].Mapping) stack[^1] = stack[^1] with { ExpectKey = true };
                    break;
            }
        }
        if (keys.Count == 0)
            throw Refuse("it is not a mapping of sections");
        return keys;
    }

    /// <summary>
    /// The entries of section <paramref name="section"/> as their source text (key line through its value, comments
    /// inside included), re-indented to two spaces with "\n" line endings; null when the section is absent or not a
    /// block mapping, or an entry cannot be re-indented.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Entries(string section)
    {
        var s = _sections.FirstOrDefault(x => x.Name == section);
        if (s?.Children is not { Count: > 0 } children) return null;
        var result = new Dictionary<string, string>();
        for (var k = 0; k < children.Count; k++)
        {
            var key = children[k];
            var indent = key.Column - 1;
            var first = key.Line - 1;
            var last = (k + 1 < children.Count ? children[k + 1].Line - 1 : s.EndLine) - 1;
            while (last > first && IsBlankOrComment(LineText(last), indent)) last--;
            var text = new StringBuilder();
            for (var line = first; line <= last; line++)
            {
                var content = LineText(line).Replace("\r\n", "\n", StringComparison.Ordinal);
                if (!content.EndsWith('\n')) content += "\n";
                if (content.Length > indent && content[..indent].All(c => c == ' ')) text.Append("  ").Append(content[indent..]);
                else if (IsBlankOrComment(content, int.MaxValue)) text.Append(content);
                else return null;
            }
            result[key.Name] = text.ToString();
        }
        return result;
    }

    /// <summary>
    /// Replaces (text), removes (null) or appends (key not present) sections; all other text is kept byte-for-byte.
    /// Section texts use "\n"; they are written with the file's line ending.
    /// </summary>
    public string Replace(IReadOnlyDictionary<string, string?> sections)
    {
        var result = new StringBuilder();
        var position = 0;
        foreach (var s in _sections)
        {
            if (!sections.TryGetValue(s.Name, out var replacement)) continue;
            var start = _lineStarts[s.FirstLine];
            result.Append(_text, position, start - position);
            if (replacement is not null) result.Append(WithNewLines(replacement));
            position = _lineStarts[s.EndLine];
        }
        result.Append(_text, position, _text.Length - position);

        foreach (var (key, text) in sections)
        {
            if (text is null || _sections.Any(s => s.Name == key)) continue;
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

    /// <summary>Blank, or a comment starting at most <paramref name="maxIndent"/> columns in.</summary>
    private static bool IsBlankOrComment(string line, int maxIndent)
    {
        if (string.IsNullOrWhiteSpace(line)) return true;
        var indent = line.Length - line.TrimStart(' ').Length;
        return indent <= maxIndent && line[indent] == '#';
    }

    private static ProjectYamlEditException Refuse(string reason) =>
        new($"cannot update this project file safely because {reason}; {Refused}");
}
