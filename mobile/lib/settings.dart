import 'package:shared_preferences/shared_preferences.dart';

/// Reglages persistants sur le telephone, equivalents de localStorage dans la
/// PWA : adresses (locale + distante), jeton, mesures masquees sur la page
/// « Tout le reste », et verrou d'ecran.
class Settings {
  static const _keyLocalUrl = 'overlay.local_url';
  static const _keyRemoteUrl = 'overlay.remote_url';
  static const _keyToken = 'overlay.token';
  static const _keyHidden = 'overlay.hidden';
  static const _keyKeepScreenOn = 'overlay.keep_screen_on';
  static const _keyLastPage = 'overlay.page';

  String localUrl;
  String remoteUrl;
  String token;
  Set<String> hidden;
  bool keepScreenOn;
  String lastPage;

  Settings({
    this.localUrl = '',
    this.remoteUrl = '',
    this.token = '',
    Set<String>? hidden,
    this.keepScreenOn = true,
    this.lastPage = 'overlay',
  }) : hidden = hidden ?? <String>{};

  /// Adresses a essayer, dans l'ordre : locale d'abord (chez soi), distante ensuite.
  List<String> get urls => [
        if (localUrl.trim().isNotEmpty) _stripSlash(localUrl),
        if (remoteUrl.trim().isNotEmpty) _stripSlash(remoteUrl),
      ];

  static String _stripSlash(String url) {
    var text = url.trim();
    while (text.endsWith('/')) {
      text = text.substring(0, text.length - 1);
    }
    return text;
  }

  static Future<Settings> load() async {
    final prefs = await SharedPreferences.getInstance();
    return Settings(
      localUrl: prefs.getString(_keyLocalUrl) ?? '',
      remoteUrl: prefs.getString(_keyRemoteUrl) ?? '',
      token: prefs.getString(_keyToken) ?? '',
      hidden: (prefs.getStringList(_keyHidden) ?? const []).toSet(),
      // Le blocage de veille est le point de depart de cette application :
      // actif par defaut, l'utilisateur le desactive s'il le souhaite.
      keepScreenOn: prefs.getBool(_keyKeepScreenOn) ?? true,
      lastPage: prefs.getString(_keyLastPage) ?? 'overlay',
    );
  }

  Future<void> save() async {
    final prefs = await SharedPreferences.getInstance();
    await prefs.setString(_keyLocalUrl, localUrl);
    await prefs.setString(_keyRemoteUrl, remoteUrl);
    await prefs.setString(_keyToken, token);
    await prefs.setStringList(_keyHidden, hidden.toList());
    await prefs.setBool(_keyKeepScreenOn, keepScreenOn);
    await prefs.setString(_keyLastPage, lastPage);
  }
}
