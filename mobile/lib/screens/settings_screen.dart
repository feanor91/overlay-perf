import 'package:flutter/material.dart';

import '../pairing.dart';
import '../settings.dart';
import '../theme.dart';
import 'qr_scan_screen.dart';

/// Reglages de connexion : adresses (locale + distante), jeton, verrou d'ecran.
/// Miroir du panneau « Reglages » de la PWA, avec le blocage de veille en plus
/// (raison d'etre de cette application native).
class SettingsScreen extends StatefulWidget {
  final Settings settings;
  final ValueChanged<Settings> onSaved;

  const SettingsScreen({super.key, required this.settings, required this.onSaved});

  @override
  State<SettingsScreen> createState() => _SettingsScreenState();
}

class _SettingsScreenState extends State<SettingsScreen> {
  late final TextEditingController _local;
  late final TextEditingController _remote;
  late final TextEditingController _token;
  late bool _keepScreenOn;
  bool _tokenVisible = false;

  @override
  void initState() {
    super.initState();
    _local = TextEditingController(text: widget.settings.localUrl);
    _remote = TextEditingController(text: widget.settings.remoteUrl);
    _token = TextEditingController(text: widget.settings.token);
    _keepScreenOn = widget.settings.keepScreenOn;
  }

  @override
  void dispose() {
    _local.dispose();
    _remote.dispose();
    _token.dispose();
    super.dispose();
  }

  void _save() {
    final updated = Settings(
      localUrl: _local.text.trim(),
      remoteUrl: _remote.text.trim(),
      token: _token.text.trim(),
      hidden: widget.settings.hidden,
      keepScreenOn: _keepScreenOn,
      lastPage: widget.settings.lastPage,
    );
    widget.onSaved(updated);
    Navigator.of(context).pop();
  }

  void _forget() {
    final cleared = Settings(keepScreenOn: _keepScreenOn);
    widget.onSaved(cleared);
    Navigator.of(context).pop();
  }

  Future<void> _scanQrCode() async {
    final raw = await Navigator.of(context).push<String>(
      MaterialPageRoute(builder: (_) => const QrScanScreen()),
    );
    if (raw == null || !mounted) return; // annule par l'utilisateur

    final link = PairingLink.parse(raw);
    if (link == null) {
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(content: Text("Ce QR code n'est pas une adresse d'appairage Overlay valide.")),
      );
      return;
    }

    setState(() {
      // Le QR se scanne typiquement chez soi, sur le meme reseau : l'adresse va
      // dans le champ local. L'adresse distante n'est jamais touchee ici.
      _local.text = link.origin;
      if (link.token != null) _token.text = link.token!;
    });

    ScaffoldMessenger.of(context).showSnackBar(SnackBar(
      content: Text(link.token != null
          ? 'Adresse et jeton renseignes depuis le QR code.'
          : "Adresse renseignee, mais aucun jeton trouve dans ce QR code."),
    ));
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('Reglages')),
      body: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          FilledButton.icon(
            onPressed: _scanQrCode,
            icon: const Icon(Icons.qr_code_scanner),
            label: const Text('Scanner le QR code du PC'),
            style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(48)),
          ),
          const SizedBox(height: 8),
          const Text(
            "Depuis l'icone d'Overlay sur le PC : « Appairer un telephone » affiche "
            "l'adresse et le jeton sous forme de QR code, plus rapide et plus sur qu'une saisie manuelle.",
            style: TextStyle(fontSize: 12, color: AppColors.textFaible),
          ),
          const SizedBox(height: 20),
          const Divider(color: AppColors.carteBord),
          const SizedBox(height: 8),
          const Text(
            "Ou renseignez les deux adresses a la main si vous utilisez l'application hors de chez vous : "
            "elle essaie la locale en premier, puis bascule sur la distante.",
            style: TextStyle(fontSize: 13, color: AppColors.textFaible),
          ),
          const SizedBox(height: 16),
          TextField(
            controller: _local,
            keyboardType: TextInputType.url,
            autocorrect: false,
            decoration: const InputDecoration(
              labelText: 'Adresse sur le reseau local',
              hintText: 'http://192.168.1.42:8777',
            ),
          ),
          const SizedBox(height: 12),
          TextField(
            controller: _remote,
            keyboardType: TextInputType.url,
            autocorrect: false,
            decoration: const InputDecoration(
              labelText: 'Adresse a distance (tunnel HTTPS)',
              hintText: 'https://mon-pc.exemple.fr',
            ),
          ),
          const SizedBox(height: 12),
          TextField(
            controller: _token,
            obscureText: !_tokenVisible,
            autocorrect: false,
            decoration: InputDecoration(
              labelText: "Jeton d'acces",
              hintText: 'colle depuis « Appairer un telephone »',
              suffixIcon: IconButton(
                icon: Icon(_tokenVisible ? Icons.visibility_off : Icons.visibility),
                onPressed: () => setState(() => _tokenVisible = !_tokenVisible),
              ),
            ),
          ),
          const SizedBox(height: 8),
          SwitchListTile(
            contentPadding: EdgeInsets.zero,
            title: const Text('Bloquer la mise en veille de l\'ecran'),
            subtitle: const Text(
              "Empeche l'ecran de s'eteindre tant que cette application est ouverte au premier plan.",
              style: TextStyle(fontSize: 12, color: AppColors.textFaible),
            ),
            value: _keepScreenOn,
            onChanged: (v) => setState(() => _keepScreenOn = v),
          ),
          const SizedBox(height: 24),
          Row(
            children: [
              Expanded(
                child: FilledButton(onPressed: _save, child: const Text('Enregistrer')),
              ),
              const SizedBox(width: 12),
              OutlinedButton(onPressed: _forget, child: const Text('Oublier')),
            ],
          ),
        ],
      ),
    );
  }
}
