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
    private readonly FpsConfig _fpsConfig;
    private readonly Func<Snapshot?> _latest;
    private readonly Action _apply;
    private readonly Action _save;
    private readonly OverlaySnapshot _original;
    private readonly FpsSnapshot _originalFps;

    private readonly (string DeviceName, string Label)[] _monitors = BuildMonitorList();
    private readonly ComboBox _monitor = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly ComboBox _position = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly TrackBar _opacity = new() { Minimum = 0, Maximum = 100, TickFrequency = 5, Width = 220 };
    private readonly Label _opacityValue = new() { AutoSize = true };
    private readonly TrackBar _textOpacity = new() { Minimum = 5, Maximum = 100, TickFrequency = 5, Width = 220 };
    private readonly Label _textOpacityValue = new() { AutoSize = true };
    private readonly NumericUpDown _margin = new() { Minimum = 0, Maximum = 500, Width = 80 };
    private readonly NumericUpDown _fontSize = new() { Minimum = 6, Maximum = 72, Width = 80 };
    private readonly NumericUpDown _columns = new() { Minimum = 1, Maximum = 6, Width = 80 };
    private readonly CheckBox _clickThrough = new() { Text = "Laisser passer les clics vers le jeu (click-through)", AutoSize = true };
    private readonly CheckBox _visibleAtStart = new() { Text = "Afficher l'overlay au demarrage", AutoSize = true };
    private readonly TextBox _hotkey = new() { Width = 160 };
    private readonly TextBox _hotkeyQuit = new() { Width = 160 };
    private readonly TextBox _presentMonPath = new() { Width = 220 };
    private readonly CheckBox _trackFrameGeneration = new()
    {
        Text = "Mesurer le multiplicateur de generation d'images (beta PresentMon)", AutoSize = true,
    };
    private readonly CheckedListBox _metrics = new()
    {
        CheckOnClick = true, IntegralHeight = false, Dock = DockStyle.Fill,
        HorizontalScrollbar = true, // certains noms de sonde (cle comprise) depassent largement la largeur visible
    };
    private readonly TextBox _filter = new() { PlaceholderText = "Filtrer…", Dock = DockStyle.Fill };
    private readonly Label _message = new() { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(700, 0) };

    /// <summary>Toutes les entrees connues (cle, libelle, valeur presente), dans l'ordre courant de la liste.</summary>
    private readonly List<MetricItem> _items = [];

    private sealed record MetricItem(string Key, string Label, string Group, bool HasValue)
    {
        public bool Checked { get; set; }
        public override string ToString() => HasValue ? $"{Label}   [{Key}]" : $"{Label}   [{Key}]  (—)";
    }

    private sealed record OverlaySnapshot(string Position, double Opacity, double TextOpacity, int Margin, int FontSize, int Columns,
        bool ClickThrough, bool VisibleAtStart, string Hotkey, string HotkeyQuit, List<string> Metrics, string Monitor)
    {
        public static OverlaySnapshot Of(OverlayConfig c) => new(c.Position, c.Opacity, c.TextOpacity, c.Margin, c.FontSize, c.Columns,
            c.ClickThrough, c.VisibleAtStart, c.Hotkey, c.HotkeyQuit, [.. c.Metrics], c.Monitor);

        public void RestoreInto(OverlayConfig c)
        {
            c.Position = Position; c.Opacity = Opacity; c.TextOpacity = TextOpacity; c.Margin = Margin; c.FontSize = FontSize; c.Columns = Columns;
            c.ClickThrough = ClickThrough; c.VisibleAtStart = VisibleAtStart; c.Hotkey = Hotkey; c.HotkeyQuit = HotkeyQuit;
            c.Metrics = [.. Metrics]; c.Monitor = Monitor;
        }
    }

    /// <summary>Options PresentMon : separees d'<see cref="OverlaySnapshot"/> car sans effet a
    /// chaud (voir <see cref="TomlPatcher"/>.SaveFps) - seul "Annuler" doit les restaurer telles
    /// quelles dans le formulaire, elles ne passent jamais par <see cref="_apply"/>.</summary>
    private sealed record FpsSnapshot(string PresentMonPath, bool TrackFrameGeneration)
    {
        public static FpsSnapshot Of(FpsConfig c) => new(c.PresentMonPath, c.TrackFrameGeneration);
    }

    public SettingsDialog(OverlayConfig config, FpsConfig fpsConfig, Func<Snapshot?> latest, Action apply, Action save)
    {
        _config = config;
        _fpsConfig = fpsConfig;
        _latest = latest;
        _apply = apply;
        _save = save;
        _original = OverlaySnapshot.Of(config);
        _originalFps = FpsSnapshot.Of(fpsConfig);

        Text = $"OverlayPerf {Server.WebServer.Version} — parametres";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(920, 600);
        MinimumSize = new Size(800, 520);
        ShowInTaskbar = true;

        BuildLayout();
        LoadFromConfig();
        BuildMetricList();
    }

    // --- Construction --------------------------------------------------------------------

    private void BuildLayout()
    {
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(12, 12, 12, 6) };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var root = content; // le reste de la methode construit les deux colonnes sans changement

        // -- Colonne gauche : affichage
        // Libelle au-dessus du controle (une seule colonne) plutot qu'a cote : a cote, la
        // largeur fixe du panneau ne laissait plus assez de place aux controles larges
        // (sliders, cases a cocher au texte long), qui se retrouvaient tronques ou coupes.
        var display = new GroupBox { Text = "Affichage", Dock = DockStyle.Fill, Padding = new Padding(10) };
        // Defilement vertical : le nombre de reglages a fini par depasser la hauteur
        // disponible sur les petites fenetres, laissant les derniers (PresentMon,
        // generation d'images) hors champ sans le moindre moyen d'y acceder.
        var displayScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Row(string label, Control control, bool stretch = false)
        {
            if (label.Length > 0)
            {
                var labelRow = grid.RowCount++;
                grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                grid.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 10, 0, 2) }, 0, labelRow);
            }
            var controlRow = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            control.Margin = new Padding(0, 0, 0, 2);
            if (stretch) control.Dock = DockStyle.Fill; // occupe toute la largeur disponible (sliders, champs de texte)
            grid.Controls.Add(control, 0, controlRow);
        }

        foreach (var (_, label) in _monitors) _monitor.Items.Add(label);
        Row("Ecran d'affichage", _monitor);

        foreach (var (_, label) in Positions) _position.Items.Add(label);
        Row("Coin de l'ecran", _position);

        Row("Opacite du fond", SliderRow(_opacity, _opacityValue), stretch: true);
        Row("Opacite des informations", SliderRow(_textOpacity, _textOpacityValue), stretch: true);
        // Les curseurs agissent en direct : le reglage de transparence se juge a l'oeil.
        _opacity.ValueChanged += (_, _) => PreviewOpacity();
        _textOpacity.ValueChanged += (_, _) => PreviewOpacity();

        Row("Marge (px)", _margin);
        Row("Taille de police", _fontSize);
        Row("Colonnes", _columns);

        var flags = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        flags.Controls.Add(_clickThrough);
        flags.Controls.Add(_visibleAtStart);
        Row("", flags);

        Row("Raccourci afficher/masquer", _hotkey, stretch: true);
        Row("Raccourci quitter", _hotkeyQuit, stretch: true);
        var hint = new Label
        {
            Text = "Syntaxe : <ctrl>+<alt>+o, <shift>+<f10>, <cmd>+space…",
            AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(340, 0),
        };
        Row("", hint);

        Row("Chemin de PresentMon (vide = recherche automatique)", PresentMonPathRow(), stretch: true);
        Row("", _trackFrameGeneration);
        var presentMonHint = new Label
        {
            Text = "Necessite de relancer OverlayPerf pour prendre effet. Limite connue de "
                   + "PresentMon : seuls Intel XeSS-FG et AMD AFMF sont distingues pour l'instant, "
                   + "pas le DLSS Frame Generation NVIDIA (affichera x1,0 en permanence).",
            AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(340, 0),
        };
        Row("", presentMonHint);

        displayScroll.Controls.Add(grid);
        // Panel.AutoScroll ne detecte pas fiablement le depassement d'un enfant "Dock" (verifie
        // a l'ecran : sans cette ligne, aucune barre de defilement n'apparaissait jamais, meme
        // avec un contenu manifestement plus haut que le panneau). Fixer explicitement la taille
        // minimale de defilement d'apres la hauteur voulue de la grille regle le probleme.
        grid.PerformLayout();
        displayScroll.AutoScrollMinSize = new Size(0, grid.PreferredSize.Height);
        display.Controls.Add(displayScroll);
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

        // -- Bas : message + boutons, dans une barre ancree en bas de la FENETRE (pas du
        // tableau de contenu) : ainsi elle reste toujours visible quelle que soit la
        // hauteur prise par les mesures ou les reglages au-dessus, au lieu de dependre
        // d'une ligne "AutoSize" du tableau principal qui pouvait finir hors ecran.
        var bottomBar = new Panel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(12, 8, 12, 12) };
        _message.AutoSize = false;
        _message.Dock = DockStyle.Fill;
        _message.TextAlign = ContentAlignment.MiddleLeft;
        bottomBar.Controls.Add(_message);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false };
        var applyButton = new Button { Text = "Appliquer", Width = 100, Anchor = AnchorStyles.Right };
        var okButton = new Button { Text = "OK", Width = 100, Anchor = AnchorStyles.Right };
        var cancelButton = new Button { Text = "Annuler", Width = 100, Anchor = AnchorStyles.Right };
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
            _fpsConfig.PresentMonPath = _originalFps.PresentMonPath;
            _fpsConfig.TrackFrameGeneration = _originalFps.TrackFrameGeneration;
            _apply();
            DialogResult = DialogResult.Cancel;
            Close();
        };
        buttons.Controls.AddRange([applyButton, okButton, cancelButton]);
        bottomBar.Controls.Add(buttons);
        AcceptButton = okButton;
        CancelButton = cancelButton;

        // Ordre d'ajout important : le contenu (Dock=Fill) d'abord, la barre (Dock=Bottom)
        // ensuite, pour qu'elle reserve sa bande en bas et que le contenu occupe le reste.
        Controls.Add(root);
        Controls.Add(bottomBar);
    }

    /// <summary>Curseur + pourcentage, le curseur occupant toute la largeur disponible
    /// (une largeur fixe le laissait tronque des que le panneau se retrouvait etroit).</summary>
    private static TableLayoutPanel SliderRow(TrackBar slider, Label value)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, AutoSize = true };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        slider.Dock = DockStyle.Fill;
        slider.Margin = new Padding(0);
        slider.ValueChanged += (_, _) => value.Text = $"{slider.Value} %";
        value.Margin = new Padding(8, 8, 0, 0);
        panel.Controls.Add(slider, 0, 0);
        panel.Controls.Add(value, 1, 0);
        return panel;
    }

    /// <summary>Champ + bouton "Parcourir…", le champ occupant toute la largeur disponible.</summary>
    private TableLayoutPanel PresentMonPathRow()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, AutoSize = true };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _presentMonPath.Dock = DockStyle.Fill;
        _presentMonPath.Margin = new Padding(0);
        var browse = new Button { Text = "Parcourir…", AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog
            {
                Title = "Choisir PresentMon",
                Filter = "PresentMon (*.exe)|PresentMon*.exe|Executables (*.exe)|*.exe|Tous les fichiers|*.*",
                FileName = _presentMonPath.Text,
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _presentMonPath.Text = dialog.FileName;
            }
        };
        panel.Controls.Add(_presentMonPath, 0, 0);
        panel.Controls.Add(browse, 1, 0);
        return panel;
    }

    /// <summary>Un moniteur par ecran connecte, plus une entree "automatique" en tete.</summary>
    private static (string DeviceName, string Label)[] BuildMonitorList()
    {
        var items = new List<(string, string)> { ("", "Automatique (ecran principal)") };
        var screens = Screen.AllScreens;
        for (var i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            var suffix = s.Primary ? " (principal)" : "";
            items.Add((s.DeviceName, $"Ecran {i + 1} — {s.Bounds.Width}x{s.Bounds.Height}{suffix}"));
        }
        return [.. items];
    }

    private bool _loading;

    /// <summary>Apercu immediat des deux opacites, sans toucher aux autres reglages ni au fichier.</summary>
    private void PreviewOpacity()
    {
        if (_loading) return;
        _config.Opacity = _opacity.Value / 100.0;
        _config.TextOpacity = _textOpacity.Value / 100.0;
        _config.TextOpacity = _textOpacity.Value / 100.0;
        try
        {
            _apply();
        }
        catch (Exception ex)
        {
            _message.Text = $"Apercu impossible : {ex.Message}";
        }
    }

    private void LoadFromConfig()
    {
        _loading = true;
        _monitor.SelectedIndex = Math.Max(0, Array.FindIndex(_monitors, m => m.DeviceName == _config.Monitor));
        _position.SelectedIndex = Math.Max(0, Array.FindIndex(Positions, p => p.Value == _config.Position));
        _opacity.Value = Math.Clamp((int)Math.Round(_config.Opacity * 100), 0, 100);
        _opacityValue.Text = $"{_opacity.Value} %";
        _textOpacity.Value = Math.Clamp((int)Math.Round(_config.TextOpacity * 100), 5, 100);
        _textOpacityValue.Text = $"{_textOpacity.Value} %";
        _margin.Value = Math.Clamp(_config.Margin, 0, 500);
        _fontSize.Value = Math.Clamp(_config.FontSize, 6, 72);
        _columns.Value = Math.Clamp(_config.Columns, 1, 6);
        _clickThrough.Checked = _config.ClickThrough;
        _visibleAtStart.Checked = _config.VisibleAtStart;
        _hotkey.Text = _config.Hotkey;
        _hotkeyQuit.Text = _config.HotkeyQuit;
        _presentMonPath.Text = _fpsConfig.PresentMonPath;
        _trackFrameGeneration.Checked = _fpsConfig.TrackFrameGeneration;
        _loading = false;
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
        UpdateMetricsHorizontalExtent();
        _metrics.EndUpdate();
        if (keepSelectedKey is not null)
        {
            for (var i = 0; i < _metrics.Items.Count; i++)
            {
                if (_metrics.Items[i] is MetricItem m && m.Key == keepSelectedKey) { _metrics.SelectedIndex = i; break; }
            }
        }
    }

    /// <summary>Un CheckedListBox ne calcule pas seul la largeur de defilement horizontal :
    /// certains libelles (avec leur cle entre crochets) depassent largement la largeur visible,
    /// notamment les sondes materielles au nom long (« AMD Ryzen 7 9800X3D Bus Speed »...).</summary>
    private void UpdateMetricsHorizontalExtent()
    {
        if (_metrics.Items.Count == 0)
        {
            _metrics.HorizontalExtent = 0;
            return;
        }
        using var g = _metrics.CreateGraphics();
        // Marge pour la case a cocher et son padding interne, mesures dans le rendu reel.
        const int checkboxAllowance = 36;
        var widest = _metrics.Items.Cast<object>().Max(item => g.MeasureString(item.ToString(), _metrics.Font).Width);
        _metrics.HorizontalExtent = (int)Math.Ceiling(widest) + checkboxAllowance;
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
        _config.TextOpacity = _textOpacity.Value / 100.0;
        _config.Margin = (int)_margin.Value;
        _config.FontSize = (int)_fontSize.Value;
        _config.Columns = (int)_columns.Value;
        _config.ClickThrough = _clickThrough.Checked;
        _config.VisibleAtStart = _visibleAtStart.Checked;
        _config.Hotkey = _hotkey.Text.Trim();
        _config.HotkeyQuit = _hotkeyQuit.Text.Trim();
        _config.Metrics = checkedKeys;
        _config.Monitor = _monitors[Math.Max(0, _monitor.SelectedIndex)].DeviceName;
        // Sans effet a chaud (voir PresentMonPathRow) : ecrites dans _fpsConfig pour
        // l'enregistrement, mais _apply() (overlay uniquement) ne les lit jamais.
        _fpsConfig.PresentMonPath = _presentMonPath.Text.Trim();
        _fpsConfig.TrackFrameGeneration = _trackFrameGeneration.Checked;
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
