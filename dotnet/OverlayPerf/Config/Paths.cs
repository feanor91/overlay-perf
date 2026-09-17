using System.Security.Cryptography;

namespace OverlayPerf.Config;

/// <summary>
/// Emplacements sur disque. Volontairement identiques a ceux de l'agent Python
/// (<c>%LOCALAPPDATA%\overlay</c>) : une configuration et un jeton deja en place
/// restent valables, les telephones deja appaires n'ont rien a refaire.
/// </summary>
public static class Paths
{
    public const string AppName = "overlay";

    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    public static string ConfigFile => Path.Combine(DataDir, "config.toml");
    public static string TokenFile => Path.Combine(DataDir, "token");
    public static string LogDir => Path.Combine(DataDir, "logs");

    /// <summary>Retourne le jeton d'acces a l'API : celui de la configuration s'il est
    /// renseigne, sinon un jeton aleatoire genere une fois puis conserve sur disque.</summary>
    public static string ResolveToken(AppConfig config, bool create = true)
    {
        if (!string.IsNullOrEmpty(config.Server.Token))
        {
            return config.Server.Token;
        }
        if (File.Exists(TokenFile))
        {
            var existing = File.ReadAllText(TokenFile).Trim();
            if (existing.Length > 0)
            {
                return existing;
            }
        }
        return create ? RotateToken() : "";
    }

    /// <summary>Remplace le jeton persiste par un nouveau, invalidant tous les telephones.
    /// Un jeton fixe dans la configuration prime toujours sur le fichier : le remplacer ici
    /// serait silencieusement sans effet, d'ou le refus explicite plutot que d'agir en vain.</summary>
    public static string RotateToken(AppConfig? config = null)
    {
        if (!string.IsNullOrEmpty(config?.Server.Token))
        {
            throw new ConfigException(
                "[server] token est fixe dans la configuration : modifiez-le a la main "
                + "ou videz-le pour laisser OverlayPerf gerer le jeton.");
        }
        var token = NewToken();
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(TokenFile, token);
        return token;
    }

    /// <summary>Equivalent de <c>secrets.token_urlsafe(24)</c> : 24 octets aleatoires en base64url.</summary>
    public static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
