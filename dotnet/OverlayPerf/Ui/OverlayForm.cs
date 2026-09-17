using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using OverlayPerf.Config;
using OverlayPerf.Models;

namespace OverlayPerf.Ui;

/// <summary>
/// Fenetre d'overlay : toujours au premier plan, transparente aux clics, dessinee en
/// transparence par pixel (<c>UpdateLayeredWindow</c>) : le fond et les informations ont
/// chacun leur opacite, reglables separement.
/// <para>Limite connue : un jeu en plein ecran exclusif (DirectX) dessine directement sur le
/// balayage ecran et masque toute fenetre, overlay compris. En plein ecran fenetre ou sans
/// bordure (le mode par defaut de la plupart des jeux recents) l'overlay s'affiche normalement.</para>
/// </summary>
public sealed class OverlayForm : Form
{
    private static readonly Color BackgroundColor = Color.FromArgb(8, 11, 18);
    private static readonly Color LabelColor = Color.FromArgb(150, 165, 190);
    private static readonly Color ValueColor = Color.FromArgb(235, 240, 248);
    private static readonly Color OkColor = Color.FromArgb(61, 220, 132);
    private static readonly Color WarmColor = Color.FromArgb(245, 181, 69);
    private static readonly Color HotColor = Color.FromArgb(255, 107, 94);
    private static readonly Color OutlineColor = Color.FromArgb(205, 0, 0, 0);

    private const int InnerMargin = 10;
    private const int ColumnGap = 22;
    private const int LabelValueGap = 16;

    private readonly OverlayConfig _config;
    private Font _font;
    private bool _clickThrough;
    private IReadOnlyList<Reading> _readings = [];
    private (int Width, int Height, int LabelWidth, int ValueWidth, int PerColumn, int LineHeight) _layout;

    public OverlayForm(OverlayConfig config)
    {
        _config = config;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Text = "Overlay";
        // Ni Opacity ni TransparencyKey : ils feraient passer WinForms par
        // SetLayeredWindowAttributes, incompatible avec UpdateLayeredWindow.
        _clickThrough = config.ClickThrough;
        _font = CreateFont(config.FontSize);
        Size = new Size(1, 1);
    }

    private static Font CreateFont(int size)
    {
        var font = new Font("Segoe UI Semibold", size, FontStyle.Regular, GraphicsUnit.Point);
        if (font.Name == "Segoe UI Semibold")
        {
            return font;
        }
        font.Dispose();
        return new Font(FontFamily.GenericSansSerif, size, FontStyle.Bold, GraphicsUnit.Point);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_LAYERED;
            // Appele une premiere fois par le constructeur de Control, avant l'affectation de
            // _config ; la valeur qui compte est celle lue a la creation du handle, plus tard.
            if (_config is { ClickThrough: true })
            {
                // Souris et clavier traversent la fenetre : le jeu dessous reste jouable.
                cp.ExStyle |= NativeMethods.WS_EX_TRANSPARENT;
            }
            return cp;
        }
    }

    // La surface est fournie par UpdateLayeredWindow : rien a peindre par le chemin classique.
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }

    /// <summary>Relit la configuration (modifiee par la fenetre Parametres) et l'applique sans redemarrer.</summary>
    public void ApplySettings(Snapshot? latest)
    {
        if (Math.Abs(_font.SizeInPoints - _config.FontSize) > 0.01)
        {
            _font.Dispose();
            _font = CreateFont(_config.FontSize);
        }
        if (_clickThrough != _config.ClickThrough)
        {
            _clickThrough = _config.ClickThrough;
            if (IsHandleCreated)
            {
                RecreateHandle(); // les styles etendus ne se changent qu'a la creation de la fenetre
            }
        }
        _readings = latest is not null ? Select(latest) : [];
        ComputeLayout();
        AdjustGeometry();
        Render();
    }

    // --- Donnees -----------------------------------------------------------------

    private List<Reading> Select(Snapshot snapshot) =>
        snapshot.Filter(_config.Metrics).Readings.Where(r => r.Value is not null).ToList();

    /// <summary>Remplace les mesures affichees et redimensionne la fenetre si besoin.</summary>
    public void Apply(Snapshot snapshot)
    {
        var selected = Select(snapshot);
        var changedCount = selected.Count != _readings.Count;
        if (!changedCount && selected.Zip(_readings).All(p => p.First.Key == p.Second.Key && p.First.Value == p.Second.Value && p.First.Label == p.Second.Label))
        {
            return;
        }
        _readings = selected;
        ComputeLayout();
        if (changedCount || _layout.Width != Width || _layout.Height != Height)
        {
            AdjustGeometry();
        }
        Render();
    }

    public void Toggle()
    {
        if (Visible)
        {
            Hide();
        }
        else
        {
            ComputeLayout();
            AdjustGeometry();
            Show();
            Render();
            BringToFront();
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Render();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        Render();
    }

    /// <summary>Un jeu qui passe au premier plan peut repasser devant : on reaffirme la position.</summary>
    public void ReassertTopMost()
    {
        if (Visible && IsHandleCreated)
        {
            NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
    }

    // --- Mise en page ------------------------------------------------------------

    private List<(string Label, string Value, Color Color)> Lines() =>
        _readings.Select(r => (r.Label, FormatValue(r), ValueColorFor(r))).ToList();

    private void ComputeLayout()
    {
        var lines = Lines();
        if (lines.Count == 0)
        {
            _layout = default;
            return;
        }
        using var bitmap = new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bitmap);
        var format = StringFormat.GenericTypographic;
        int Measure(string text) => (int)Math.Ceiling(g.MeasureString(text, _font, PointF.Empty, format).Width);
        var labelWidth = lines.Max(l => Measure(l.Label));
        var valueWidth = lines.Max(l => Measure(l.Value));
        var lineHeight = (int)Math.Ceiling(_font.GetHeight(g)) + 4;
        var columns = Math.Max(1, Math.Min(_config.Columns, lines.Count));
        var perColumn = (lines.Count + columns - 1) / columns;
        var columnWidth = labelWidth + LabelValueGap + valueWidth;
        var width = InnerMargin * 2 + columns * columnWidth + (columns - 1) * ColumnGap;
        var height = InnerMargin * 2 + perColumn * lineHeight;
        _layout = (width, height, labelWidth, valueWidth, perColumn, lineHeight);
    }

    private void AdjustGeometry()
    {
        if (_layout.Width == 0)
        {
            Size = new Size(1, 1);
            return;
        }
        var screen = Screen.FromControl(this) ?? Screen.PrimaryScreen;
        var zone = screen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        var margin = _config.Margin;
        var x = zone.Left + margin;
        var y = zone.Top + margin;
        if (_config.Position.EndsWith("right", StringComparison.Ordinal))
        {
            x = zone.Right - _layout.Width - margin;
        }
        if (_config.Position.StartsWith("bottom", StringComparison.Ordinal))
        {
            y = zone.Bottom - _layout.Height - margin;
        }
        SetBounds(x, y, _layout.Width, _layout.Height);
    }

    // --- Rendu --------------------------------------------------------------------

    /// <summary>Dessine l'overlay dans une image ARGB et la remet a Windows en une fois.</summary>
    private void Render()
    {
        if (!IsHandleCreated || !Visible) return;
        var width = Math.Max(1, _layout.Width);
        var height = Math.Max(1, _layout.Height);
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        if (_layout.Width > 0)
        {
            using var g = Graphics.FromImage(bitmap);
            Draw(g);
        }
        Push(bitmap);
    }

    private void Draw(Graphics g)
    {
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // Niveaux de gris, pas ClearType : ce dernier suppose un fond opaque.
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var backgroundAlpha = (int)Math.Round(Math.Clamp(_config.Opacity, 0, 1) * 255);
        var textAlpha = Math.Clamp(_config.TextOpacity, 0, 1);

        using (var background = RoundedRect(new Rectangle(0, 0, _layout.Width - 1, _layout.Height - 1), 10))
        using (var brush = new SolidBrush(Color.FromArgb(backgroundAlpha, BackgroundColor)))
        {
            g.FillPath(brush, background);
        }

        var lines = Lines();
        var columnWidth = _layout.LabelWidth + LabelValueGap + _layout.ValueWidth;
        var emSize = _font.SizeInPoints * g.DpiY / 72f;
        var format = StringFormat.GenericTypographic;
        for (var index = 0; index < lines.Count; index++)
        {
            var (label, value, color) = lines[index];
            var column = index / _layout.PerColumn;
            var row = index % _layout.PerColumn;
            var x = InnerMargin + column * (columnWidth + ColumnGap);
            var y = InnerMargin + row * _layout.LineHeight + 2;
            DrawOutlined(g, label, x, y, emSize, WithAlpha(LabelColor, textAlpha), textAlpha, format);
            var valueWidth = g.MeasureString(value, _font, PointF.Empty, format).Width;
            DrawOutlined(g, value, x + columnWidth - valueWidth, y, emSize, WithAlpha(color, textAlpha), textAlpha, format);
        }
    }

    private static Color WithAlpha(Color color, double factor) =>
        Color.FromArgb((int)Math.Round(color.A * factor), color.R, color.G, color.B);

    /// <summary>Texte cerne de noir : lisible sur un fond de jeu clair comme sombre.</summary>
    private void DrawOutlined(Graphics g, string text, float x, float y, float emSize, Color color, double alpha, StringFormat format)
    {
        using var path = new GraphicsPath();
        path.AddString(text, _font.FontFamily, (int)_font.Style, emSize, new PointF(x, y), format);
        using var pen = new Pen(WithAlpha(OutlineColor, alpha), 2.4f) { LineJoin = LineJoin.Round };
        g.DrawPath(pen, path);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Transmet l'image a la fenetre en couches : Windows la compose lui-meme, alpha compris.</summary>
    private void Push(Bitmap bitmap)
    {
        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        var memDc = NativeMethods.CreateCompatibleDC(screenDc);
        var hBitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            previous = NativeMethods.SelectObject(memDc, hBitmap);
            var size = new NativeMethods.SIZE { cx = bitmap.Width, cy = bitmap.Height };
            var source = new NativeMethods.POINT { x = 0, y = 0 };
            var destination = new NativeMethods.POINT { x = Left, y = Top };
            var blend = new NativeMethods.BLENDFUNCTION
            {
                BlendOp = NativeMethods.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = NativeMethods.AC_SRC_ALPHA,
            };
            NativeMethods.UpdateLayeredWindow(Handle, screenDc, ref destination, ref size, memDc, ref source, 0, ref blend, NativeMethods.ULW_ALPHA);
        }
        finally
        {
            if (previous != IntPtr.Zero) NativeMethods.SelectObject(memDc, previous);
            if (hBitmap != IntPtr.Zero) NativeMethods.DeleteObject(hBitmap);
            NativeMethods.DeleteDC(memDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    // --- Mise en forme des valeurs -----------------------------------------------

    /// <summary>Vert / orange / rouge selon la position dans la plage utile de la sonde.</summary>
    public static Color ValueColorFor(Reading reading)
    {
        if (reading.Value is null) return LabelColor;
        if (reading.Kind is not (Kind.Temperature or Kind.Load or Kind.Power)) return ValueColor;
        var (low, high) = reading.Range;
        var span = high - low;
        if (span <= 0) return ValueColor;
        var ratio = (reading.Value.Value - low) / span;
        return ratio >= 0.85 ? HotColor : ratio >= 0.65 ? WarmColor : OkColor;
    }

    private static double ToGio(double value, string unit) => unit == "MiB" ? value / 1024.0 : value;

    public static string FormatValue(Reading reading)
    {
        if (reading.Value is not { } value) return "—";
        // RAM et VRAM : "utilise / total" en Gio plutot qu'un chiffre brut en MiB sans repere.
        if (reading.Kind == Kind.Memory && reading.Maximum is { } total && total > 0 && Math.Abs(value - total) > 0.001)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0.0} / {1:0.0} Go",
                ToGio(value, reading.Unit), ToGio(total, reading.Unit));
        }
        var abs = Math.Abs(value);
        string text;
        if (abs >= 1000)
        {
            text = value.ToString("#,##0", CultureInfo.InvariantCulture).Replace(',', ' ');
        }
        else if (abs >= 100)
        {
            text = value.ToString("0", CultureInfo.InvariantCulture);
        }
        else
        {
            text = value.ToString("0.0", CultureInfo.InvariantCulture);
        }
        return $"{text} {reading.Unit}".Trim();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _font.Dispose();
        }
        base.Dispose(disposing);
    }
}
