import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:http/http.dart' as http;
import 'package:web_socket_channel/io.dart';
import 'package:web_socket_channel/web_socket_channel.dart';

import 'models.dart';
import 'settings.dart';

enum LinkState { hors, attente, direct, jetonRefuse, adresseInvalide }

/// Reproduit la logique de connexion de la PWA (app.js) : bascule entre
/// adresse locale et distante, recul exponentiel, relecture periodique de la
/// selection de l'overlay via `GET /api/overlay`.
class ConnectionService extends ChangeNotifier {
  static const _reconnexionMin = Duration(seconds: 1);
  static const _reconnexionMax = Duration(seconds: 15);
  static const _rafraichissementOverlay = Duration(seconds: 20);

  Settings settings;
  ConnectionService(this.settings);

  WebSocketChannel? _socket;
  StreamSubscription? _sub;
  Timer? _reconnectTimer;
  Timer? _overlayTimer;
  Duration _backoff = _reconnexionMin;
  int _addressIndex = 0;
  bool _disposed = false;

  LinkState state = LinkState.hors;
  String? etiquetteAdresse;
  String? message;
  Snapshot? latest;
  List<String>? overlayKeys;
  String? overlayError;

  bool get isMultiAddress => settings.urls.length > 1;

  void updateSettings(Settings newSettings) {
    settings = newSettings;
    reconnect();
  }

  void start() => _connect();

  void reconnect() {
    _addressIndex = 0;
    _backoff = _reconnexionMin;
    _connect();
  }

  String get _currentBase {
    final urls = settings.urls;
    if (urls.isEmpty) return '';
    return urls[_addressIndex % urls.length];
  }

  String? _wsUrl(String base) {
    Uri uri;
    try {
      uri = Uri.parse(base);
    } catch (_) {
      return null;
    }
    if (uri.scheme != 'http' && uri.scheme != 'https') return null;
    final wsScheme = uri.scheme == 'https' ? 'wss' : 'ws';
    final query = settings.token.isNotEmpty ? {'token': settings.token} : null;
    return Uri(
      scheme: wsScheme,
      host: uri.host,
      port: uri.hasPort ? uri.port : null,
      path: '/ws',
      queryParameters: query,
    ).toString();
  }

  void _connect() {
    _disconnect(notify: false);
    if (settings.urls.isEmpty) {
      state = LinkState.hors;
      message = "Renseignez une adresse dans Reglages pour vous connecter a l'agent.";
      _notify();
      return;
    }
    final base = _currentBase;
    final url = _wsUrl(base);
    if (url == null) {
      state = LinkState.adresseInvalide;
      message = 'Adresse inutilisable : $base. Indiquez une URL complete, '
          'par exemple http://192.168.1.42:8777 ou https://mon-pc.exemple.fr.';
      _notify();
      if (isMultiAddress) _scheduleReconnect(nextAddress: true);
      return;
    }
    _updateLabel();
    state = LinkState.attente;
    _notify();

    try {
      _socket = IOWebSocketChannel.connect(Uri.parse(url), pingInterval: const Duration(seconds: 20));
    } catch (_) {
      _scheduleReconnect();
      return;
    }

    _sub = _socket!.stream.listen(
      (event) {
        if (state != LinkState.direct) {
          _backoff = _reconnexionMin;
          state = LinkState.direct;
          message = '';
          _fetchOverlaySelection(base);
        }
        try {
          final json = jsonDecode(event as String) as Map<String, dynamic>;
          latest = Snapshot.fromJson(json);
        } catch (_) {
          return;
        }
        _notify();
      },
      onDone: () => _handleClose(_socket?.closeCode),
      onError: (_) => _handleClose(null),
      cancelOnError: true,
    );
  }

  void _handleClose(int? code) {
    if (_disposed) return;
    _sub = null;
    _socket = null;
    // 1008 = refus applicatif (jeton invalide) : reessayer en boucle est inutile.
    if (code == 1008) {
      state = LinkState.jetonRefuse;
      message = "Jeton refuse par l'agent. Ouvrez « Appairer un telephone » sur le PC pour en obtenir un valide.";
      _notify();
      return;
    }
    state = LinkState.hors;
    _notify();
    _scheduleReconnect(nextAddress: true);
  }

  void _scheduleReconnect({bool nextAddress = false}) {
    _reconnectTimer?.cancel();
    var delay = _backoff;
    final total = settings.urls.length.clamp(1, 1 << 30);

    if (nextAddress && total > 1) {
      _addressIndex = (_addressIndex + 1) % total;
      // Tant qu'il reste une adresse a essayer dans le tour, on enchaine vite.
      if (_addressIndex != 0) delay = _reconnexionMin;
    }
    if (!nextAddress || total == 1 || _addressIndex == 0) {
      _backoff = Duration(milliseconds: (_backoff.inMilliseconds * 2).clamp(
        _reconnexionMin.inMilliseconds, _reconnexionMax.inMilliseconds));
    }
    _reconnectTimer = Timer(delay, _connect);
  }

  void _updateLabel() {
    etiquetteAdresse = isMultiAddress ? (_addressIndex == 0 ? ' · local' : ' · distant') : null;
  }

  // --- Selection de l'overlay --------------------------------------------------

  Future<void> _fetchOverlaySelection(String base) async {
    _overlayTimer?.cancel();
    try {
      final uri = Uri.parse('$base/api/overlay');
      final headers = settings.token.isNotEmpty ? {'X-Overlay-Token': settings.token} : <String, String>{};
      final response = await http.get(uri, headers: headers).timeout(const Duration(seconds: 5));
      if (response.statusCode == 404) {
        overlayKeys = null;
        overlayError = "Cet agent ne publie pas la selection de l'overlay (version Python) : toutes les mesures sont affichees.";
      } else if (response.statusCode != 200) {
        throw HttpException('HTTP ${response.statusCode}');
      } else {
        final json = jsonDecode(response.body) as Map<String, dynamic>;
        overlayKeys = OverlaySelection.fromJson(json).keys;
        overlayError = null;
      }
    } catch (_) {
      overlayKeys ??= null;
      overlayError ??= "Selection de l'overlay indisponible pour l'instant : toutes les mesures sont affichees.";
    }
    _notify();
    if (!_disposed) {
      _overlayTimer = Timer(_rafraichissementOverlay, () => _fetchOverlaySelection(_currentBase));
    }
  }

  void refreshOverlaySelectionNow() {
    if (state == LinkState.direct) _fetchOverlaySelection(_currentBase);
  }

  // --- Cycle de vie --------------------------------------------------------------

  void _disconnect({bool notify = true}) {
    _reconnectTimer?.cancel();
    _overlayTimer?.cancel();
    _sub?.cancel();
    _sub = null;
    _socket?.sink.close();
    _socket = null;
    if (notify) _notify();
  }

  void _notify() {
    if (!_disposed) notifyListeners();
  }

  @override
  void dispose() {
    _disposed = true;
    _disconnect(notify: false);
    super.dispose();
  }
}
