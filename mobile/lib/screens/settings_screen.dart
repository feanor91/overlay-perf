import 'package:flutter/material.dart';

import '../settings.dart';
import '../theme.dart';

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

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('Reglages')),
      body: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          const Text(
            "Renseignez les deux adresses si vous utilisez l'application hors de chez vous : "
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
