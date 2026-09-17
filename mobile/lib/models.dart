/// Modeles de donnees : miroir exact du JSON envoye par l'agent (Python ou
/// OverlayPerf.exe), pour que les deux versions du serveur fonctionnent sans
/// distinction cote application.
library;

/// Une mesure unitaire (`cpu.temp`, `gpu.0.fan.rpm`...).
class Reading {
  final String key;
  final String label;
  final double? value;
  final String unit;
  final String group;
  final String kind;
  final double min;
  final double max;
  final bool gauge;
  final String source;
  final Map<String, dynamic> extra;

  const Reading({
    required this.key,
    required this.label,
    required this.value,
    required this.unit,
    required this.group,
    required this.kind,
    required this.min,
    required this.max,
    required this.gauge,
    required this.source,
    this.extra = const {},
  });

  factory Reading.fromJson(Map<String, dynamic> json) => Reading(
        key: json['key'] as String,
        label: json['label'] as String,
        value: (json['value'] as num?)?.toDouble(),
        unit: json['unit'] as String? ?? '',
        group: json['group'] as String? ?? 'system',
        kind: json['kind'] as String? ?? 'load',
        min: (json['min'] as num?)?.toDouble() ?? 0.0,
        max: (json['max'] as num?)?.toDouble() ?? 100.0,
        gauge: json['gauge'] as bool? ?? false,
        source: json['source'] as String? ?? '',
        extra: (json['extra'] as Map?)?.cast<String, dynamic>() ?? const {},
      );
}

/// Etat complet du systeme a un instant donne.
class Snapshot {
  final double timestamp;
  final String host;
  final List<Reading> readings;

  const Snapshot({required this.timestamp, required this.host, required this.readings});

  factory Snapshot.fromJson(Map<String, dynamic> json) => Snapshot(
        timestamp: (json['timestamp'] as num?)?.toDouble() ?? 0.0,
        host: json['host'] as String? ?? '—',
        readings: (json['readings'] as List? ?? const [])
            .map((r) => Reading.fromJson((r as Map).cast<String, dynamic>()))
            .toList(),
      );

  /// Restreint aux cles demandees, dans cet ordre ; un motif finissant par
  /// `*` agit comme un prefixe. Une liste vide renvoie tout.
  List<Reading> filter(List<String> keys) {
    if (keys.isEmpty) return readings;
    final byKey = {for (final r in readings) r.key: r};
    final seen = <String>{};
    final result = <Reading>[];
    for (final pattern in keys) {
      if (pattern.endsWith('*')) {
        final prefix = pattern.substring(0, pattern.length - 1);
        for (final r in readings) {
          if (seen.contains(r.key)) continue;
          if (r.key.startsWith(prefix)) {
            result.add(r);
            seen.add(r.key);
          }
        }
      } else if (byKey.containsKey(pattern) && seen.add(pattern)) {
        result.add(byKey[pattern]!);
      }
    }
    return result;
  }
}

/// Reponse de `GET /api/overlay` : la selection montree par l'overlay a l'ecran.
class OverlaySelection {
  final List<String> keys;
  final List<String> patterns;
  final String position;
  final double opacity;
  final int columns;
  final bool enabled;

  const OverlaySelection({
    required this.keys,
    required this.patterns,
    required this.position,
    required this.opacity,
    required this.columns,
    required this.enabled,
  });

  factory OverlaySelection.fromJson(Map<String, dynamic> json) => OverlaySelection(
        keys: (json['keys'] as List? ?? const []).cast<String>(),
        patterns: (json['patterns'] as List? ?? const []).cast<String>(),
        position: json['position'] as String? ?? 'top-left',
        opacity: (json['opacity'] as num?)?.toDouble() ?? 0.75,
        columns: (json['columns'] as num?)?.toInt() ?? 1,
        enabled: json['enabled'] as bool? ?? true,
      );
}

/// Famille de mesures, dans l'ordre d'affichage de la page « Tout le reste ».
const List<String> groupOrder = [
  'fps', 'cpu', 'gpu', 'memory', 'fan', 'storage', 'network', 'system',
];

const Map<String, String> groupTitles = {
  'fps': 'Images par seconde',
  'cpu': 'Processeur',
  'gpu': 'Carte graphique',
  'memory': 'Memoire',
  'fan': 'Ventilateurs',
  'storage': 'Stockage',
  'network': 'Reseau',
  'system': 'Systeme',
};

int groupRank(String group) {
  final i = groupOrder.indexOf(group);
  return i < 0 ? 99 : i;
}
