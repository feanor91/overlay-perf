using Microsoft.Extensions.Logging;

namespace OverlayPerf.Ui;

/// <summary>
/// Raccourcis clavier globaux via <c>RegisterHotKey</c>, poses sur la fenetre cachee de l'hote.
/// La syntaxe de configuration est celle de l'agent Python (pynput) : <c>&lt;ctrl&gt;+&lt;alt&gt;+o</c>,
/// <c>&lt;shift&gt;+&lt;f10&gt;</c>… pour qu'un fichier de configuration existant reste valable.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly ILogger _log;
    private readonly Dictionary<int, (string Combination, Action Callback)> _registered = new();
    private int _nextId = 1;

    public HotkeyManager(IntPtr hwnd, ILogger log)
    {
        _hwnd = hwnd;
        _log = log;
    }

    /// <summary>Active un raccourci ; retourne <c>false</c> (et journalise pourquoi) s'il n'a pu etre pose.</summary>
    public bool Register(string combination, Action callback)
    {
        if (string.IsNullOrWhiteSpace(combination))
        {
            return false;
        }
        if (!TryParse(combination, out var modifiers, out var key, out var error))
        {
            _log.LogWarning("Raccourci {Combination} invalide : {Error}", combination, error);
            return false;
        }
        var id = _nextId++;
        if (!NativeMethods.RegisterHotKey(_hwnd, id, modifiers | NativeMethods.MOD_NOREPEAT, (uint)key))
        {
            var code = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            _log.LogWarning("Raccourci {Combination} indisponible (erreur Win32 {Code}) : deja pris par une autre application ?",
                combination, code);
            return false;
        }
        _registered[id] = (combination, callback);
        _log.LogInformation("Raccourci global actif : {Combination}", combination);
        return true;
    }

    /// <summary>A appeler depuis <c>WndProc</c> sur WM_HOTKEY.</summary>
    public void Dispatch(int id)
    {
        if (!_registered.TryGetValue(id, out var entry))
        {
            return;
        }
        try
        {
            entry.Callback();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Echec de l'action du raccourci {Combination}", entry.Combination);
        }
    }

    public void UnregisterAll()
    {
        foreach (var id in _registered.Keys)
        {
            NativeMethods.UnregisterHotKey(_hwnd, id);
        }
        _registered.Clear();
    }

    public void Dispose() => UnregisterAll();

    /// <summary>Convertit <c>&lt;ctrl&gt;+&lt;alt&gt;+o</c> en modificateurs Win32 et touche virtuelle.</summary>
    public static bool TryParse(string combination, out uint modifiers, out Keys key, out string error)
    {
        modifiers = 0;
        key = Keys.None;
        error = "";
        var parts = combination.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            error = "combinaison vide";
            return false;
        }
        foreach (var raw in parts)
        {
            var part = raw.ToLowerInvariant();
            var token = part.StartsWith('<') && part.EndsWith('>') ? part[1..^1] : part;
            switch (token)
            {
                case "ctrl" or "control" or "ctrl_l" or "ctrl_r":
                    modifiers |= NativeMethods.MOD_CONTROL; continue;
                case "alt" or "alt_l" or "alt_r" or "alt_gr":
                    modifiers |= NativeMethods.MOD_ALT; continue;
                case "shift" or "shift_l" or "shift_r":
                    modifiers |= NativeMethods.MOD_SHIFT; continue;
                case "cmd" or "win" or "super" or "cmd_l" or "cmd_r":
                    modifiers |= NativeMethods.MOD_WIN; continue;
            }
            if (key != Keys.None)
            {
                error = $"plusieurs touches non modificatrices ({token})";
                return false;
            }
            if (!TryKey(token, out key))
            {
                error = $"touche inconnue : {raw}";
                return false;
            }
        }
        if (key == Keys.None)
        {
            error = "aucune touche principale (ex. o, f10, space)";
            return false;
        }
        return true;
    }

    private static bool TryKey(string token, out Keys key)
    {
        key = Keys.None;
        if (token.Length == 1)
        {
            var c = char.ToUpperInvariant(token[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                key = (Keys)c;
                return true;
            }
            key = c switch
            {
                ' ' => Keys.Space, ',' => Keys.Oemcomma, '.' => Keys.OemPeriod, '-' => Keys.OemMinus,
                '=' => Keys.Oemplus, ';' => Keys.OemSemicolon, '/' => Keys.OemQuestion, '`' => Keys.Oemtilde,
                '[' => Keys.OemOpenBrackets, ']' => Keys.OemCloseBrackets, '\'' => Keys.OemQuotes, '\\' => Keys.OemPipe,
                _ => Keys.None,
            };
            return key != Keys.None;
        }
        if (token.Length is 2 or 3 && token[0] == 'f' && int.TryParse(token[1..], out var n) && n is >= 1 and <= 24)
        {
            key = Keys.F1 + (n - 1);
            return true;
        }
        key = token switch
        {
            "space" => Keys.Space, "enter" or "return" => Keys.Enter, "tab" => Keys.Tab,
            "esc" or "escape" => Keys.Escape, "backspace" => Keys.Back, "delete" or "del" => Keys.Delete,
            "insert" or "ins" => Keys.Insert, "home" => Keys.Home, "end" => Keys.End,
            "page_up" or "pageup" => Keys.PageUp, "page_down" or "pagedown" => Keys.PageDown,
            "up" => Keys.Up, "down" => Keys.Down, "left" => Keys.Left, "right" => Keys.Right,
            "pause" => Keys.Pause, "print_screen" or "printscreen" => Keys.PrintScreen,
            "scroll_lock" => Keys.Scroll, "num_lock" => Keys.NumLock, "caps_lock" => Keys.CapsLock,
            "menu" => Keys.Apps,
            _ => Keys.None,
        };
        return key != Keys.None;
    }
}
