/// Decode l'URL d'appairage produite par « overlay pair » / « Appairer un
/// telephone » (agent Python ou OverlayPerf.exe) : `http://hote:port/#token=...`.
/// Le jeton voyage dans le fragment (`#`), pas la chaine de requete : il n'est
/// ainsi jamais journalise par un serveur ni transmis dans un en-tete Referer.
class PairingLink {
  final String origin;
  final String? token;

  const PairingLink({required this.origin, this.token});

  static PairingLink? parse(String text) {
    Uri uri;
    try {
      uri = Uri.parse(text.trim());
    } catch (_) {
      return null;
    }
    if ((uri.scheme != 'http' && uri.scheme != 'https') || uri.host.isEmpty) return null;

    final origin = uri.hasPort ? '${uri.scheme}://${uri.host}:${uri.port}' : '${uri.scheme}://${uri.host}';

    String? token;
    if (uri.fragment.isNotEmpty) {
      try {
        token = Uri.splitQueryString(uri.fragment)['token'];
      } catch (_) {
        // fragment mal forme : on garde quand meme l'adresse, sans jeton
      }
    }
    return PairingLink(origin: origin, token: token);
  }
}
