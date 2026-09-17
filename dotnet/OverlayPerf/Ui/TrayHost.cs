using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OverlayPerf.Config;

namespace OverlayPerf.Ui;

/// <summary>
/// Fenetre cachee qui porte l'icone de zone de notification, les raccourcis globaux et la
/// minuterie de rafraichissement de l'overlay. C'est le point d'entree "sans terminal" :
/// appairage du telephone, journaux, configuration et sortie propre.
/// </summary>
public sealed class TrayHost : Form
{
    private readonly ILogger _log;
    private readonly NotifyIcon? _tray;
    private readonly HotkeyManager _hotkeys;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Func<string> _status;

    public TrayHost(AppConfig config, ILogger log, Action toggleOverlay, Action? showPairing, Action? openSettings,
        Action tick, Func<string> status)
    {
        _log = log;
        _status = status;
        // Fenetre invisible : ni bordure, ni barre des taches, jamais affichee.
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        Opacity = 0;
        Size = new Size(1, 1);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        Text = "OverlayPerf";
        // Force la creation du handle sans afficher la fenetre.
        _ = Handle;

        _hotkeys = new HotkeyManager(Handle, log);

        if (config.Overlay.TrayIcon)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Afficher / masquer l'overlay", null, (_, _) => toggleOverlay());
            if (openSettings is not null)
            {
                menu.Items.Add("Parametres…", null, (_, _) => openSettings());
            }
            if (showPairing is not null)
            {
                menu.Items.Add("Appairer un telephone…", null, (_, _) => showPairing());
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Ouvrir le journal du jour", null, (_, _) => OpenPath(Logging.LogSetup.CurrentLogFile));
            menu.Items.Add("Ouvrir le dossier des journaux", null, (_, _) => OpenPath(Paths.LogDir));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Etat…", null, (_, _) => ShowStatus());
            menu.Items.Add("Quitter", null, (_, _) => Application.Exit());

            _tray = new NotifyIcon
            {
                Icon = LoadIcon(),
                Text = "OverlayPerf",
                Visible = true,
                ContextMenuStrip = menu,
            };
            _tray.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) toggleOverlay();
            };
        }

        _timer = new System.Windows.Forms.Timer { Interval = Math.Max(100, (int)(config.General.PollInterval * 1000) / 2) };
        _timer.Tick += (_, _) => tick();
        _timer.Start();
    }

    public bool RegisterHotkey(string combination, Action callback) => _hotkeys.Register(combination, callback);

    /// <summary>Remplace tous les raccourcis (apres modification dans la fenetre Parametres).</summary>
    public void ReplaceHotkeys(Action<HotkeyManager> register)
    {
        _hotkeys.UnregisterAll();
        register(_hotkeys);
    }

    /// <summary>Notification non bloquante : seule voie vers l'utilisateur quand il n'y a pas de terminal.</summary>
    public void Notify(string title, string message, ToolTipIcon icon = ToolTipIcon.Warning)
    {
        if (_tray is null) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => Notify(title, message, icon));
            return;
        }
        try
        {
            _tray.ShowBalloonTip(10_000, title, message.Length > 250 ? message[..247] + "…" : message, icon);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Notification impossible");
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_HOTKEY)
        {
            _hotkeys.Dispatch(m.WParam.ToInt32());
            return;
        }
        base.WndProc(ref m);
    }

    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);

    public void OpenConfig()
    {
        try
        {
            if (!File.Exists(Paths.ConfigFile))
            {
                Directory.CreateDirectory(Paths.DataDir);
                File.WriteAllText(Paths.ConfigFile, ConfigTemplate.Text);
                _log.LogInformation("Fichier de configuration cree : {Path}", Paths.ConfigFile);
            }
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Paths.ConfigFile}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Ouverture de la configuration impossible");
        }
    }

    private void OpenPath(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                Directory.CreateDirectory(Paths.LogDir);
                path = Paths.LogDir;
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Ouverture de {Path} impossible", path);
        }
    }

    private void ShowStatus()
    {
        MessageBox.Show(this, _status(), "OverlayPerf — etat", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Reprend l'icone de l'executable lui-meme (app.ico, embarquee via
    /// ApplicationIcon) plutot qu'une copie separee : une seule image source pour
    /// l'exe, la barre des taches et la zone de notification.</summary>
    private static Icon LoadIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (path is not null)
            {
                var extracted = Icon.ExtractAssociatedIcon(path);
                if (extracted is not null)
                {
                    return extracted;
                }
            }
        }
        catch (Exception)
        {
            // repli sur l'icone generique
        }
        return SystemIcons.Application;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
            _hotkeys.Dispose();
            if (_tray is not null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}
