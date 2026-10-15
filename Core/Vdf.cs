using System.Text;

namespace TemizlikPaneli.Core;

/// <summary>Steam'in KeyValues (VDF/ACF) metin biçimi için küçük ayrıştırıcı.</summary>
public sealed class VdfNode
{
    public readonly Dictionary<string, object> Items = new(StringComparer.OrdinalIgnoreCase);

    public string? Str(string key) => Items.TryGetValue(key, out var v) ? v as string : null;
    public VdfNode? Node(string key) => Items.TryGetValue(key, out var v) ? v as VdfNode : null;
    public long Long(string key) => long.TryParse(Str(key), out var l) ? l : 0;

    public static VdfNode Parse(string text)
    {
        var tokens = Tokenize(text).GetEnumerator();
        var root = new VdfNode();
        ParseInto(root, tokens, topLevel: true);
        return root;
    }

    public static VdfNode? Load(string path)
    {
        try { return File.Exists(path) ? Parse(File.ReadAllText(path)) : null; }
        catch { return null; }
    }

    private static void ParseInto(VdfNode node, IEnumerator<string?> tokens, bool topLevel)
    {
        while (tokens.MoveNext())
        {
            var key = tokens.Current;
            if (key == null) // '}' işareti
            {
                if (topLevel) continue;
                return;
            }
            if (key == "{") continue;
            if (!tokens.MoveNext()) return;
            var val = tokens.Current;
            if (val == "{")
            {
                var child = new VdfNode();
                ParseInto(child, tokens, topLevel: false);
                node.Items[key] = child;
            }
            else if (val == null)
            {
                if (!topLevel) return;
            }
            else node.Items[key] = val;
        }
    }

    /// <summary>Belirteçler: dize, "{" ya da null (= "}").</summary>
    private static IEnumerable<string?> Tokenize(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n') i++;
                continue;
            }
            if (c == '{') { i++; yield return "{"; continue; }
            if (c == '}') { i++; yield return null; continue; }
            if (c == '[') // koşullu ifadeler: [$WIN32]
            {
                while (i < s.Length && s[i] != ']') i++;
                i++;
                continue;
            }
            if (c == '"')
            {
                i++;
                var sb = new StringBuilder();
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length)
                    {
                        char n = s[i + 1];
                        sb.Append(n switch { 'n' => '\n', 't' => '\t', '\\' => '\\', '"' => '"', _ => n });
                        i += 2;
                        continue;
                    }
                    sb.Append(s[i++]);
                }
                i++;
                yield return sb.ToString();
                continue;
            }
            int start = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '{' && s[i] != '}' && s[i] != '"') i++;
            yield return s[start..i];
        }
    }
}
