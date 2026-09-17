using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OverlayPerf.Fps;

/// <summary>
/// Nom du processus proprietaire de la fenetre au premier plan : c'est presque toujours
/// celui du jeu en cours de partie, y compris en plein ecran exclusif (qui vole
/// systematiquement le focus). Sert de cible precise a PresentMon plutot qu'une liste noire
/// d'applications a exclure, forcement incomplete : n'importe quelle application a rendu
/// materiel (Explorateur, Gestionnaire des taches, un navigateur...) peut s'y glisser un jour.
/// </summary>
internal static class ForegroundProcess
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>Nom de fichier (avec extension) du processus au premier plan, ou <c>null</c>
    /// si indeterminable (aucune fenetre au premier plan, processus deja termine...).</summary>
    public static string? Name()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return null;
            }
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0)
            {
                return null;
            }
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName + ".exe";
        }
        catch (Exception)
        {
            // Processus disparu entre la lecture du PID et l'ouverture (Process.GetProcessById),
            // ou requete refusee (fenetre d'un processus plus privilegie) : rien d'anormal.
            return null;
        }
    }
}
