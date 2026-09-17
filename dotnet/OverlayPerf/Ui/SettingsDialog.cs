using OverlayPerf.Config;
using OverlayPerf.Models;

namespace OverlayPerf.Ui;

/// <summary>
/// Fenetre d'administration de l'overlay : position, transparence, police, colonnes,
/// raccourcis, et choix/ordre des mesures affichees. « Appliquer » agit a chaud sur
/// l'overlay sans rien ecrire ; « OK » applique et enregistre dans config.toml (en
/// conservant les commentaires du fichier) ; « Annuler » remet l'etat d'ouverture.
/// </summary>
public sealed class SettingsDialog : Form
{
    private static readonly (string Value, string Label)[] Positions =
    [
        ("top-left", "Haut gauche"),
        ("top-right", "Haut droite"),
        ("bottom-left", "Bas gauche"),
        ("bottom-right", "Bas droite"),
    ];

    private static readonly string[] GroupOrder = ["fps", "cpu", "gpu", "memory", "fan", "storage", "network", "system"];

    private readonly OverlayConfig _config;
    private readonly Func<Snapshot?> _latest;
    private readonly Action _apply;
    private readonly Action _save;
    private readonly OverlaySnapshot _original;

    private readonly ComboBox _position = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly TrackBar _opacity = new() { Minimum = 5, Maximum = 100, TickFrequency = 5, Width = 220 };
    private readonly Label _opacityValue = new() { AutoSize = true };
    private readonly NumericUpDown _margin = new() { Minimum = 0, Maximum = 500, Width = 80 };
    private readonly NumericUpDown _fontSize = new() { Minimum = 6, Maximum = 72, Width = 80 };
    private readonly NumericUpDown _columns = new() { Minimum = 1, Maximum = 6, Width = 80 };
    private readonly CheckBox _clickThrough = new() { Text = "Laisser passer les clics vers le jeu (click-through)", AutoSize = true };
    private readonly CheckBox _visibleAtStart = new() { Text = "Afficher l'overlay au demarrage", AutoSize = true };
    private readonly TextBox _hotkey = new() { Width = 160 };
    private readonly TextBox _hotkeyQuit = new() { Width = 160 };
    private readonly CheckedListBox _metrics = new() { CheckOnClick = true, IntegralHeight = false, Dock = DockStyle.Fill };
    private readonly TextBox _filter = new() { PlaceholderText = "Filtrer…", Dock = DockStyle.Fill };
    private readonly Label _message = new() { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(700, 0) };

    /// <summary>Toutes les entrees connues (cle, libelle, valeur presente), dans l'ordre courant de la liste.</summary>
    private readonly List<MetricItem> _items = [];

    private sealed record MetricItem(string Key, string Label, string Group, bool HasValue)
    {
        public bool Checked { get; set; }
        public override string ToString() => HasValue ? $"{Label}   [{Key}]" : $"{Label}   [{Key}]  (—)";
    }

    private sealed record OverlaySnapshot(string Position, double Opacity, int Margin, int FontSize, int Columns,
        bool ClickThrough, bool VisibleAtStart, string Hotkey, string HotkeyQuit, List<string> Metrics)
    {
        public static OverlaySnapshot Of(OverlayConfig c) => new(c.Position, c.Opacity, c.Margin, c.FontSize, c.Columns,
            c.ClickThrough, c.VisibleAtStart, c.Hotkey, c.HotkeyQuit, [.. c.Metrics]);

        public void RestoreInto(OverlayConfig c)
        {
            c.Position = Position; c.Opacity = Opacity; c.Margin = Margin; c.FontSize = FontSize; c.Columns = Columns;
            c.ClickThrough = ClickThrough; c.VisibleAtStart = VisibleAtStart; c.Hotkey = Hotkey; c.HotkeyQuit = HotkeyQuit;
            c.Metrics = [.. Metrics];
        }
    }

    public SettingsDialog(OverlayConfig config, Func<Snapshot?> latest, Action apply, Action save)
    {
        _config = config;
        _latest = latest;
        _apply = apply;
        _save = save;
        _original = OverlaySnapshot.Of(config);

        Text = "OverlayPerf — parametres";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(880, 600);
        MinimumSize = new Size(760, 520);
        ShowInTaskbar = true;

        BuildLayout();
        LoadFromConfig();
        BuildMetricList();
    }

    // --- Construction --------------------------------------------------------------------

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(12) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        // -- Colonne gauche : affichage
        var display = new GroupBox { Text = "Affichage", Dock = DockStyle.Fill, Padding = new Padding(10) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Row(string label, Control control)
        {
            var row = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 8, 2) }, 0, row);
            control.Margin = new Padding(0, 4, 0, 2);
            grid.Controls.Add(control, 1, row);
        }

        foreach (var (_, label) in Positions) _position.Items.Add(label);
        Row("Coin de l'ecran", _position);

        var opacityPanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        _opacity.ValueChanged += (_, _) => _opacityValue.Text = $"{_opacity.Value} %";
        _opacityValue.Margin = new Padding(6, 8, 0, 0);
        opacityPanel.Controls.Add(_opacity);
        opacityPanel.Controls.Add(_opacityValue);
        Row("Opacite", opacityPanel);

        Row("Marge (px)", _margin);
        Row("Taille de police", _fontSize);
        Row("Colonnes", _columns);

        var flags = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        flags.Controls.Add(_clickThrough);
        flags.Controls.Add(_visibleAtStart);
        Row("", flags);

        Row("Raccourci afficher/masquer", _hotkey);
        Row("Raccourci quitter", _hotkeyQuit);
        var hint = new Label
        {
            Text = "Syntaxe : <ctrl>+<alt>+o, <shift>+<f10>, <cmd>+space…",
            AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(300, 0),
        };
        Row("", hint);

        display.Controls.Add(grid);
        root.Controls.Add(display, 0, 0);

        // -- Colonne droite : mesures
        var metricsBox = new GroupBox { Text = "Mesures affichees dans l'overlay (ordre = ordre d'affichage)", Dock = DockStyle.Fill, Padding = new Padding(10) };
        var metricsLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
        metricsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        metricsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        metricsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        metricsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        metricsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _filter.TextChanged += (_, _) => RefreshMetricList();
        metricsLayout.Controls.Add(_filter, 0, 0);
        metricsLayout.SetColumnSpan(_filter, 2);

        _metrics.ItemCheck += (_, e) =>
        {
            if (_metrics.Items[e.Index] is MetricItem item) item.Checked = e.NewValue == CheckState.Checked;
        };
        metricsLayout.Controls.Add(_metrics, 0, 1);

        var side = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(8, 0, 0, 0) };
        var up = new Button { Text = "Monter", Width = 90 };
        var down = new Button { Text = "Descendre", Width = 90 };
        var all = new Button { Text = "Tout cocher", Width = 90 };
        var none = new Button { Text = "Tout decocher", Width = 90 };
        up.Click += (_, _) => MoveSelected(-1);
        down.Click += (_, _) => MoveSelected(+1);
        all.Click += (_, _) => SetAll(true);
        none.Click += (_, _) => SetAll(false);
        side.Controls.AddRange([up, down, all, none]);
        metricsLayout.Controls.Add(side, 1, 1);

        var help = new Label
        {
            Text = "Les mesures sans valeur actuellement sont marquees (—). Cocher une mesure l'ajoute a la fin ; "
                   + "Monter/Descendre reglent l'ordre. A l'enregistrement, les motifs (fan.*) sont remplaces par les cles exactes.",
            AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(480, 0), Margin = new Padding(0, 6, 0, 0),
        };
        metricsLayout.Controls.Add(help, 0, 2);
        metricsLayout.SetColumnSpan(help, 2);

        metricsBox.Controls.Add(metricsLayout);
        root.Controls.Add(metricsBox, 1, 0);

        // -- Bas : message + boutons
        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(_message, 0, 0);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true };
        var applyButton = new Button { Text = "Appliquer", Width = 100 };
        var okButton = new Button { Text = "OK", Width = 100 };
        var cancelButton = new Button { Text = "Annuler", Width = 100 };
        applyButton.Click += (_, _) => TryApply();
        okButton.Click += (_, _) =>
        {
            if (!TryApply()) return;
            try
            {
                _save();
            }
            catch (Exception ex)
            {
                _message.Text = $"Enregistrement impossible : {ex.Message}";
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        };
        cancelButton.Click += (_, _) =>
        {
            _original.RestoreInto(_config);
            _apply();
            DialogResult = DialogResult.Cancel;
            Close();
        };
        buttons.Controls.AddRange([applyButton, okButton, cancelButton]);
        bottom.Controls.Add(buttons, 1, 0);
        root.Controls.Add(bottom, 0, 1);
        root.SetColumnSpan(bottom, 2);
        AcceptButton = okButton;
        CancelButton = cancelButton;
    }

    private void LoadFromConfig()
    {
        _position.SelectedIndex = Math.Max(0, Array.FindIndex(Positions, p => p.Value == _config.Position));
        _opacity.Value = Math.Clamp((int)Math.Round(_config.Opacity * 100), 5, 100);
        _opacityValue.Text = $"{_opacity.Value} %";
        _margin.Value = Math.Clamp(_config.Margin, 0, 500);
        _fontSize.Value = Math.Clamp(_config.FontSize, 6, 72);
        _columns.Value = Math.Clamp(_config.Columns, 1, 6);
        _clickThrough.Checked = _config.ClickThrough;
        _visibleAtStart.Checked = _config.VisibleAtStart;
        _hotkey.Text = _config.Hotkey;
        _hotkeyQuit.Text = _config.HotkeyQuit;
    }

    /// <summary>Selection courante d'abord (dans l'ordre de l'overlay), puis le reste par famille.</summary>
    private void BuildMetricList()
    {
        _items.Clear();
        var snapshot = _latest();
        var known = snapshot?.Readings.ToDictionary(r => r.Key) ?? new Dictionary<string, Reading>();
        var selected = snapshot is not null
            ? snapshot.Filter(_config.Metrics).Readings.Select(r => r.Key).ToList()
            : _config.Metrics.Where(m => !m.EndsWith('*')).ToList();

        foreach (var key in selected)
        {
            known.TryGetValue(key, out var reading);
            _items.Add(new MetricItem(key, reading?.Label ?? key, reading?.Group.Wire() ?? "", reading?.Value is not null) { Checked = true });
        }
        var rest = known.Values
            .Where(r => !selected.Contains(r.Key))
            .OrderBy(r => { var i = Array.IndexOf(GroupOrder, r.Group.Wire()); return i < 0 ? 99 : i; })
            .ThenBy(r => r.Label, StringComparer.CurrentCultureIgnoreCase);
        foreach (var reading in rest)
        {
            _items.Add(new MetricItem(reading.Key, reading.Label, reading.Group.Wire(), reading.Value is not null));
        }
        RefreshMetricList();
    }

    private void RefreshMetricList(string? keepSelectedKey = null)
    {
        var filter = _filter.Text.Trim();
        _metrics.BeginUpdate();
        _metrics.Items.Clear();
        foreach (var item in _items)
        {
            if (filter.Length > 0
                && !item.Label.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                && !item.Key.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            _metrics.Items.Add(item, item.Checked);
        }
        _metrics.EndUpdate();
        if (keepSelectedKey is not null)
        {
            for (var i = 0; i < _metrics.Items.Count; i++)
            {
                if (_metrics.Items[i] is MetricItem m && m.Key == keepSelectedKey) { _metrics.SelectedIndex = i; break; }
            }
        }
    }

    private void MoveSelected(int delta)
    {
        if (_metrics.SelectedItem is not MetricItem item) return;
        var index = _items.IndexOf(item);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _items.Count) return;
        (_items[index], _items[target]) = (_items[target], _items[index]);
        RefreshMetricList(item.Key);
    }

    private void SetAll(bool value)
    {
        foreach (var item in _items) item.Checked = value;
        RefreshMetricList();
    }

    // --- Application ---------------------------------------------------------------------

    private bool TryApply()
    {
        _message.Text = "";
        foreach (var (text, name) in new[] { (_hotkey.Text, "afficher/masquer"), (_hotkeyQuit.Text, "quitter") })
        {
            if (text.Trim().Length > 0 && !HotkeyManager.TryParse(text.Trim(), out _, out _, out var error))
            {
                _message.Text = $"Raccourci « {name} » invalide : {error}";
                return false;
            }
        }
        var checkedKeys = _items.Where(i => i.Checked).Select(i => i.Key).ToList();
        if (checkedKeys.Count == 0)
        {
            _message.Text = "Cochez au moins une mesure, sinon l'overlay serait vide.";
            return false;
        }

        _config.Position = Positions[Math.Max(0, _position.SelectedIndex)].Value;
        _config.Opacity = _opacity.Value / 100.0;
        _config.Margin = (int)_margin.Value;
        _config.FontSize = (int)_fontSize.Value;
        _config.Columns = (int)_columns.Value;
        _config.ClickThrough = _clickThrough.Checked;
        _config.VisibleAtStart = _visibleAtStart.Checked;
        _config.Hotkey = _hotkey.Text.Trim();
        _config.HotkeyQuit = _hotkeyQuit.Text.Trim();
        _config.Metrics = checkedKeys;
        try
        {
            _apply();
        }
        catch (Exception ex)
        {
            _message.Text = $"Application impossible : {ex.Message}";
            return false;
        }
        return true;
    }
}
