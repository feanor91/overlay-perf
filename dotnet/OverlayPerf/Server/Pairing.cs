using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace OverlayPerf.Server;

public sealed record PairingSummary(
    IReadOnlyList<string> Addresses,
    IReadOnlyList<string> Urls,
    string LocalUrl,
    string RemoteUrl,
    string PrimaryUrl,
    string Token);

/// <summary>Appairage du telephone : adresse joignable sur le reseau local et QR code.</summary>
public static class Pairing
{
    /// <summary>
    /// Adresses IPv4 par lesquelles le telephone peut joindre cette machine. La premiere est
    /// celle que l'OS utiliserait pour sortir sur le reseau : presque toujours la bonne quand
    /// plusieurs interfaces coexistent (Wi-Fi, VPN, ponts Docker/WSL).
    /// </summary>
    public static List<string> LocalIpAddresses()
    {
        var addresses = new List<string>();
        try
        {
            // Aucun paquet n'est emis : connecter un socket UDP ne fait que choisir la route.
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 80));
            if (probe.LocalEndPoint is IPEndPoint ep)
            {
                addresses.Add(ep.Address.ToString());
            }
        }
        catch (SocketException)
        {
            // hors ligne : on se contente des interfaces
        }

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var text = unicast.Address.ToString();
                    if (!addresses.Contains(text)) addresses.Add(text);
                }
            }
        }
        catch (NetworkInformationException)
        {
        }

        var usable = addresses.Where(a =>
            IPAddress.TryParse(a, out var ip)
            && !IPAddress.IsLoopback(ip)
            && !a.StartsWith("169.254.", StringComparison.Ordinal)
            && !ip.Equals(IPAddress.Any)).ToList();
        return usable.Count > 0 ? usable : ["127.0.0.1"];
    }

    /// <summary>Le jeton passe dans le fragment (<c>#token=</c>) : contrairement a la chaine de
    /// requete, un fragment n'est ni journalise par les serveurs ni transmis dans <c>Referer</c>.</summary>
    public static string PairingUrl(string host, int port, string token, string scheme = "http") =>
        WithToken($"{scheme}://{host}:{port}/", token);

    public static string WithToken(string baseUrl, string token)
    {
        var withSlash = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
        return string.IsNullOrEmpty(token) ? withSlash : $"{withSlash}#token={Uri.EscapeDataString(token)}";
    }

    public static PairingSummary Summary(int port, string token, string publicUrl = "", string scheme = "http")
    {
        var addresses = LocalIpAddresses();
        var urls = addresses.Select(a => PairingUrl(a, port, token, scheme)).ToList();
        var remote = string.IsNullOrEmpty(publicUrl) ? "" : WithToken(publicUrl, token);
        var primary = remote.Length > 0 ? remote : urls[0];
        return new PairingSummary(addresses, urls, urls[0], remote, primary, token);
    }

    /// <summary>QR code en caracteres semi-graphiques (deux lignes par ligne de terminal).</summary>
    public static string RenderQrText(string text)
    {
        using var generator = new QRCoder.QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
        var modules = data.ModuleMatrix;
        var size = modules.Count;
        var lines = new List<string>();
        for (var y = 0; y < size; y += 2)
        {
            var line = new System.Text.StringBuilder();
            for (var x = 0; x < size; x++)
            {
                var upper = modules[y][x];
                var lower = y + 1 < size && modules[y + 1][x];
                line.Append(upper && lower ? '█' : upper ? '▀' : lower ? '▄' : ' ');
            }
            lines.Add(line.ToString());
        }
        return string.Join(Environment.NewLine, lines);
    }
}
