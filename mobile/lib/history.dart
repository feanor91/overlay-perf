/// Historique borne des valeurs recentes, par cle, pour tracer une mini-courbe
/// sur les mesures qui n'ont pas de jauge (miroir de POINTS_COURBE dans app.js).
class HistoryStore {
  static const int maxPoints = 40;
  final Map<String, List<double>> _series = {};

  void push(String key, double value) {
    final series = _series.putIfAbsent(key, () => []);
    series.add(value);
    if (series.length > maxPoints) series.removeAt(0);
  }

  List<double> of(String key) => _series[key] ?? const [];
}
