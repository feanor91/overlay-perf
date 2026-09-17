import 'package:flutter/material.dart';
import 'package:wakelock_plus/wakelock_plus.dart';

import '../connection_service.dart';
import '../history.dart';
import '../models.dart';
import '../settings.dart';
import '../theme.dart';
import '../widgets/reading_tile.dart';
import 'settings_screen.dart';

class HomeScreen extends StatefulWidget {
  final ConnectionService connection;
  final Settings settings;

  const HomeScreen({super.key, required this.connection, required this.settings});

  @override
  State<HomeScreen> createState() => _HomeScreenState();
}

class _HomeScreenState extends State<HomeScreen> {
  final HistoryStore _history = HistoryStore();
  late Settings _settings;
  late String _page; // 'overlay' ou 'tout'

  @override
  void initState() {
    super.initState();
    _settings = widget.settings;
    _page = _settings.lastPage;
    widget.connection.addListener(_onConnectionUpdate);
    widget.connection.start();
    _applyWakelock();
  }

  @override
  void dispose() {
    widget.connection.removeListener(_onConnectionUpdate);
    super.dispose();
  }

  void _onConnectionUpdate() {
    final snapshot = widget.connection.latest;
    if (snapshot != null) {
      for (final reading in snapshot.readings) {
        if (reading.value != null) _history.push(reading.key, reading.value!);
      }
    }
    setState(() {});
  }

  Future<void> _applyWakelock() async {
    try {
      if (_settings.keepScreenOn) {
        await WakelockPlus.enable();
      } else {
        await WakelockPlus.disable();
      }
    } catch (_) {
      // Pas grave si indisponible (emulateur sans permission) : l'app reste utilisable.
    }
  }

  void _selectPage(String page) {
    setState(() {
      _page = page;
      _settings.lastPage = page;
    });
    _settings.save();
    if (page == 'overlay') widget.connection.refreshOverlaySelectionNow();
  }

  Future<void> _openSettings() async {
    final result = await Navigator.of(context).push<Settings>(
      MaterialPageRoute(builder: (_) => SettingsScreen(settings: _settings, onSaved: (s) => Navigator.of(context).pop(s))),
    );
    if (result != null) {
      setState(() => _settings = result);
      await _settings.save();
      await _applyWakelock();
      widget.connection.updateSettings(_settings);
    }
  }

  String _statusLabel() {
    final label = widget.connection.etiquetteAdresse ?? '';
    switch (widget.connection.state) {
      case LinkState.direct:
        return 'En direct$label';
      case LinkState.attente:
        return 'Connexion…$label';
      case LinkState.jetonRefuse:
        return 'Jeton refuse';
      case LinkState.adresseInvalide:
        return 'Adresse invalide';
      case LinkState.hors:
        return 'Hors ligne';
    }
  }

  Color _statusColor() {
    switch (widget.connection.state) {
      case LinkState.direct:
        return AppColors.ok;
      case LinkState.attente:
        return AppColors.tiede;
      default:
        return AppColors.chaud;
    }
  }

  List<Reading> _visibleReadings(Snapshot snapshot) {
    if (_page == 'overlay') {
      final keys = widget.connection.overlayKeys;
      if (keys == null) return snapshot.readings;
      final byKey = {for (final r in snapshot.readings) r.key: r};
      return keys.map((k) => byKey[k]).whereType<Reading>().toList();
    }
    final overlayKeys = (widget.connection.overlayKeys ?? const <String>[]).toSet();
    return snapshot.readings.where((r) => !overlayKeys.contains(r.key) && !_settings.hidden.contains(r.key)).toList();
  }

  @override
  Widget build(BuildContext context) {
    final snapshot = widget.connection.latest;
    final visible = snapshot != null ? _visibleReadings(snapshot) : const <Reading>[];

    return Scaffold(
      appBar: AppBar(
        title: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisSize: MainAxisSize.min,
          children: [
            const Text('Overlay', style: TextStyle(fontSize: 18)),
            Text(snapshot?.host ?? '—', style: const TextStyle(fontSize: 12, color: AppColors.textFaible)),
          ],
        ),
        actions: [
          Container(
            margin: const EdgeInsets.only(right: 8),
            padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
            decoration: BoxDecoration(
              border: Border.all(color: _statusColor().withValues(alpha: 0.4)),
              borderRadius: BorderRadius.circular(999),
            ),
            child: Text(_statusLabel(), style: TextStyle(fontSize: 11, color: _statusColor())),
          ),
          if (_page == 'tout')
            IconButton(icon: const Icon(Icons.visibility_outlined), tooltip: 'Mesures', onPressed: _showHideDialog),
          IconButton(icon: const Icon(Icons.settings_outlined), tooltip: 'Reglages', onPressed: _openSettings),
        ],
      ),
      body: Column(
        children: [
          _Tabs(current: _page, onSelect: _selectPage),
          if (_page == 'overlay' && widget.connection.overlayError != null)
            _Note(text: widget.connection.overlayError!),
          Expanded(
            child: snapshot == null
                ? _Empty(onOpenSettings: _settings.urls.isEmpty ? _openSettings : null)
                : visible.isEmpty
                    ? const _Empty(message: "Rien a afficher pour l'instant.")
                    : _Grid(readings: visible, history: _history),
          ),
        ],
      ),
    );
  }

  void _showHideDialog() {
    final snapshot = widget.connection.latest;
    if (snapshot == null) return;
    final overlayKeys = (widget.connection.overlayKeys ?? const <String>[]).toSet();
    final candidates = snapshot.readings.where((r) => !overlayKeys.contains(r.key)).toList()
      ..sort((a, b) => a.label.toLowerCase().compareTo(b.label.toLowerCase()));
    showModalBottomSheet(
      context: context,
      backgroundColor: AppColors.carte,
      isScrollControlled: true,
      builder: (context) => StatefulBuilder(
        builder: (context, setSheetState) => DraggableScrollableSheet(
          initialChildSize: 0.7,
          expand: false,
          builder: (context, controller) => ListView(
            controller: controller,
            padding: const EdgeInsets.all(16),
            children: [
              const Text('Mesures affichees', style: TextStyle(fontSize: 16, fontWeight: FontWeight.w600)),
              const SizedBox(height: 8),
              for (final reading in candidates)
                CheckboxListTile(
                  dense: true,
                  contentPadding: EdgeInsets.zero,
                  title: Text(reading.label),
                  subtitle: Text(reading.key, style: const TextStyle(fontSize: 11, color: AppColors.textFaible)),
                  value: !_settings.hidden.contains(reading.key),
                  onChanged: (checked) {
                    setSheetState(() {
                      if (checked == true) {
                        _settings.hidden.remove(reading.key);
                      } else {
                        _settings.hidden.add(reading.key);
                      }
                    });
                    setState(() {});
                    _settings.save();
                  },
                ),
            ],
          ),
        ),
      ),
    );
  }
}

class _Tabs extends StatelessWidget {
  final String current;
  final ValueChanged<String> onSelect;
  const _Tabs({required this.current, required this.onSelect});

  @override
  Widget build(BuildContext context) {
    return Container(
      margin: const EdgeInsets.fromLTRB(16, 8, 16, 4),
      padding: const EdgeInsets.all(4),
      decoration: BoxDecoration(
        color: AppColors.carte,
        border: Border.all(color: AppColors.carteBord),
        borderRadius: BorderRadius.circular(12),
      ),
      child: Row(
        children: [
          Expanded(child: _TabButton(label: 'Overlay', selected: current == 'overlay', onTap: () => onSelect('overlay'))),
          Expanded(child: _TabButton(label: 'Tout le reste', selected: current == 'tout', onTap: () => onSelect('tout'))),
        ],
      ),
    );
  }
}

class _TabButton extends StatelessWidget {
  final String label;
  final bool selected;
  final VoidCallback onTap;
  const _TabButton({required this.label, required this.selected, required this.onTap});

  @override
  Widget build(BuildContext context) {
    return InkWell(
      onTap: onTap,
      borderRadius: BorderRadius.circular(9),
      child: Container(
        padding: const EdgeInsets.symmetric(vertical: 10),
        decoration: BoxDecoration(
          color: selected ? AppColors.fond : Colors.transparent,
          borderRadius: BorderRadius.circular(9),
        ),
        child: Text(
          label,
          textAlign: TextAlign.center,
          style: TextStyle(
            fontWeight: FontWeight.w600,
            fontSize: 13,
            color: selected ? AppColors.texte : AppColors.textFaible,
          ),
        ),
      ),
    );
  }
}

class _Note extends StatelessWidget {
  final String text;
  const _Note({required this.text});

  @override
  Widget build(BuildContext context) {
    return Container(
      margin: const EdgeInsets.fromLTRB(16, 0, 16, 8),
      padding: const EdgeInsets.all(10),
      decoration: BoxDecoration(
        border: Border.all(color: AppColors.carteBord, style: BorderStyle.solid),
        borderRadius: BorderRadius.circular(10),
      ),
      child: Text(text, style: const TextStyle(fontSize: 12, color: AppColors.textFaible)),
    );
  }
}

class _Grid extends StatelessWidget {
  final List<Reading> readings;
  final HistoryStore history;
  const _Grid({required this.readings, required this.history});

  @override
  Widget build(BuildContext context) {
    return GridView.builder(
      padding: const EdgeInsets.fromLTRB(16, 4, 16, 16),
      gridDelegate: const SliverGridDelegateWithMaxCrossAxisExtent(
        maxCrossAxisExtent: 190,
        mainAxisSpacing: 10,
        crossAxisSpacing: 10,
        childAspectRatio: 1.15,
      ),
      itemCount: readings.length,
      itemBuilder: (context, index) {
        final reading = readings[index];
        return ReadingTile(reading: reading, history: history.of(reading.key));
      },
    );
  }
}

class _Empty extends StatelessWidget {
  final String? message;
  final VoidCallback? onOpenSettings;
  const _Empty({this.message, this.onOpenSettings});

  @override
  Widget build(BuildContext context) {
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(32),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Text(
              message ??
                  "En attente de l'agent. Lancez OverlayPerf.exe (ou l'agent Python) sur le PC, "
                      "puis « Appairer un telephone » depuis son icone pour obtenir l'adresse et le jeton.",
              textAlign: TextAlign.center,
              style: const TextStyle(color: AppColors.textFaible, height: 1.5),
            ),
            if (onOpenSettings != null) ...[
              const SizedBox(height: 20),
              FilledButton.icon(
                onPressed: onOpenSettings,
                icon: const Icon(Icons.qr_code_scanner),
                label: const Text('Scanner le QR code du PC'),
              ),
            ],
          ],
        ),
      ),
    );
  }
}
