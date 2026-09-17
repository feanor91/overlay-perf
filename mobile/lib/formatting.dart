import 'package:flutter/material.dart';

import 'models.dart';
import 'theme.dart';

/// Mise en forme des valeurs et couleurs, miroir de app.js (webapp) et de
/// OverlayForm.cs (overlay a l'ecran) : les trois doivent s'accorder.

double _versGio(double value, String unit) => unit == 'MiB' ? value / 1024.0 : value;

String formatNumber(double value) {
  final absolute = value.abs();
  if (absolute >= 10000) return value.round().toString();
  if (absolute >= 100) return value.toStringAsFixed(0);
  if (absolute >= 10) return value.toStringAsFixed(1);
  return value.toStringAsFixed(absolute < 1 ? 2 : 1);
}

/// RAM et VRAM : "utilise / total" en Gio, deduit de la pleine echelle deja
/// fournie par l'agent (`max`) - aucune mesure supplementaire necessaire.
String? formatMemory(Reading reading) {
  if (reading.kind != 'memory' || reading.value == null) return null;
  if (reading.max <= 0 || (reading.value! - reading.max).abs() < 0.001) return null;
  final used = _versGio(reading.value!, reading.unit).toStringAsFixed(1);
  final total = _versGio(reading.max, reading.unit).toStringAsFixed(1);
  return '$used / $total Go';
}

String formatValue(Reading reading) {
  if (reading.value == null) return '—';
  final memory = formatMemory(reading);
  if (memory != null) return memory;
  final number = formatNumber(reading.value!);
  return reading.unit.isEmpty ? number : '$number ${reading.unit}';
}

/// Vert / orange / rouge selon la position dans la plage utile de la sonde.
Color colorForReading(Reading reading) {
  if (reading.value == null) return AppColors.textFaible;
  if (reading.kind != 'temperature' && reading.kind != 'load' && reading.kind != 'power') {
    return AppColors.texte;
  }
  final span = reading.max - reading.min;
  if (span <= 0) return AppColors.texte;
  final ratio = (reading.value! - reading.min) / span;
  if (ratio >= 0.85) return AppColors.chaud;
  if (ratio >= 0.65) return AppColors.tiede;
  return AppColors.ok;
}

double gaugeRatio(Reading reading) {
  if (reading.value == null) return 0;
  final span = reading.max - reading.min;
  if (span <= 0) return 0;
  return ((reading.value! - reading.min) / span).clamp(0.0, 1.0);
}
