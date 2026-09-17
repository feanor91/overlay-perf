using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OverlayPerf.Config;

/// <summary>
/// Modification ciblee d'un fichier TOML existant : remplace ou ajoute des cles dans une
/// section sans toucher au reste, commentaires compris. Un fichier de configuration est
/// avant tout un document que l'utilisateur lit : le reserialiser entierement effacerait
/// toutes ses explications.
/// </summary>
public static partial class TomlPatcher
{
    [GeneratedRegex(@"^\s*\[(?<name>[^\]]+)\]\s*(#.*)?$")]
    private static partial Regex SectionHeader();

    /// <summary>Applique <paramref name="values"/> (cle → litteral TOML deja formate) a la section <paramref name="section"/>.</summary>
    public static string SetValues(string text, string section, IReadOnlyDictionary<string, string> values)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();

        var start = lines.FindIndex(l => SectionHeader().Match(l) is { Success: true } m
                                         && m.Groups["name"].Value.Trim() == section);
        if (start < 0)
        {
            // Section absente : on l'ajoute a la fin, separee par une ligne vide.
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add($"[{section}]");
            foreach (var (key, literal) in values)
            {
                lines.AddRange(FormatAssignment(key, literal));
            }
            return string.Join(newline, lines);
        }

        var end = lines.FindIndex(start + 1, l => SectionHeader().IsMatch(l));
        if (end < 0) end = lines.Count;

        foreach (var (key, literal) in values)
        {
            var pattern = new Regex(@"^\s*" + Regex.Escape(key) + @"\s*=");
            var index = lines.FindIndex(start + 1, end - start - 1, l => pattern.IsMatch(l));
            var replacement = FormatAssignment(key, literal);
            if (index >= 0)
            {
                var span = AssignmentSpan(lines, index, end);
                lines.RemoveRange(index, span);
                lines.InsertRange(index, replacement);
                end += replacement.Count - span;
            }
            else
            {
                // Insertion avant les lignes vides qui separent de la section suivante.
                var insertAt = end;
                while (insertAt > start + 1 && lines[insertAt - 1].Trim().Length == 0) insertAt--;
                lines.InsertRange(insertAt, replacement);
                end += replacement.Count;
            }
        }
        return string.Join(newline, lines);
    }

    /// <summary>Nombre de lignes occupees par l'affectation commencant a <paramref name="index"/>
    /// (un tableau peut s'etendre sur plusieurs lignes).</summary>
    private static int AssignmentSpan(List<string> lines, int index, int end)
    {
        var depth = 0;
        for (var i = index; i < end; i++)
        {
            depth += BracketBalance(lines[i]);
            if (depth <= 0) return i - index + 1;
        }
        return end - index;
    }

    /// <summary>Crochets ouverts moins fermes, hors chaines et commentaires.</summary>
    private static int BracketBalance(string line)
    {
        var balance = 0;
        var inString = false;
        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inString)
            {
                if (c == '\\' && quote == '"') { i++; continue; }
                if (c == quote) inString = false;
                continue;
            }
            if (c is '"' or '\'') { inString = true; quote = c; continue; }
            if (c == '#') break;
            if (c == '[') balance++;
            else if (c == ']') balance--;
        }
        return balance;
    }

    private static List<string> FormatAssignment(string key, string literal) =>
        literal.Contains('\n') ? $"{key} = {literal}".Split('\n').ToList() : [$"{key} = {literal}"];

    // --- Litteraux TOML ------------------------------------------------------------------

    public static string String(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in value)
        {
            sb.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when c < 0x20 => $"\\u{(int)c:X4}",
                _ => c.ToString(),
            });
        }
        return sb.Append('"').ToString();
    }

    public static string Bool(bool value) => value ? "true" : "false";
    public static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Toujours avec un point decimal : TOML distingue 1 de 1.0, et la relecture
    /// attend un flottant pour ces cles.</summary>
    public static string Double(double value)
    {
        var text = value.ToString("0.0###", CultureInfo.InvariantCulture);
        return text.Contains('.') ? text : text + ".0";
    }

    /// <summary>Tableau de chaines sur plusieurs lignes, comme dans le modele de configuration.</summary>
    public static string StringArray(IEnumerable<string> values)
    {
        var items = values.ToList();
        if (items.Count == 0) return "[]";
        var sb = new StringBuilder("[\n");
        foreach (var item in items)
        {
            sb.Append("    ").Append(String(item)).Append(",\n");
        }
        return sb.Append(']').ToString();
    }
}

/// <summary>Enregistre les reglages de l'overlay dans le fichier de configuration.</summary>
public static class ConfigWriter
{
    public static void SaveOverlay(string path, OverlayConfig overlay)
    {
        var text = File.Exists(path) ? File.ReadAllText(path) : ConfigTemplate.Text;
        var values = new Dictionary<string, string>
        {
            ["position"] = TomlPatcher.String(overlay.Position),
            ["margin"] = TomlPatcher.Int(overlay.Margin),
            ["opacity"] = TomlPatcher.Double(overlay.Opacity),
            ["text_opacity"] = TomlPatcher.Double(overlay.TextOpacity),
            ["font_size"] = TomlPatcher.Int(overlay.FontSize),
            ["columns"] = TomlPatcher.Int(overlay.Columns),
            ["click_through"] = TomlPatcher.Bool(overlay.ClickThrough),
            ["hotkey"] = TomlPatcher.String(overlay.Hotkey),
            ["hotkey_quit"] = TomlPatcher.String(overlay.HotkeyQuit),
            ["visible_at_start"] = TomlPatcher.Bool(overlay.VisibleAtStart),
            ["metrics"] = TomlPatcher.StringArray(overlay.Metrics),
        };
        var updated = TomlPatcher.SetValues(text, "overlay", values);
        // Relecture avant ecriture : un fichier qu'on ne sait plus lire ne doit jamais etre ecrit.
        AppConfig.Parse(updated, path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, updated, new UTF8Encoding(false));
    }
}
