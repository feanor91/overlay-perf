import 'package:flutter/material.dart';

/// Palette sombre, miroir de style.css (webapp) pour une coherence visuelle
/// entre la PWA et l'application native.
class AppColors {
  static const fond = Color(0xFF0B0F17);
  static const carte = Color(0xFF151B26);
  static const carteBord = Color(0xFF222B3A);
  static const texte = Color(0xFFE8EDF5);
  static const textFaible = Color(0xFF8C9AB0);
  static const accent = Color(0xFF4DA3FF);
  static const ok = Color(0xFF3DDC84);
  static const tiede = Color(0xFFF5B545);
  static const chaud = Color(0xFFFF6B5E);
}

ThemeData buildAppTheme() {
  final base = ThemeData.dark(useMaterial3: true);
  return base.copyWith(
    scaffoldBackgroundColor: AppColors.fond,
    colorScheme: base.colorScheme.copyWith(
      surface: AppColors.fond,
      primary: AppColors.accent,
      secondary: AppColors.accent,
    ),
    appBarTheme: const AppBarTheme(
      backgroundColor: AppColors.fond,
      foregroundColor: AppColors.texte,
      elevation: 0,
    ),
    cardTheme: CardThemeData(
      color: AppColors.carte,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(14),
        side: const BorderSide(color: AppColors.carteBord),
      ),
    ),
    textTheme: base.textTheme.apply(bodyColor: AppColors.texte, displayColor: AppColors.texte),
    inputDecorationTheme: InputDecorationTheme(
      filled: true,
      fillColor: AppColors.carte,
      border: OutlineInputBorder(
        borderRadius: BorderRadius.circular(10),
        borderSide: const BorderSide(color: AppColors.carteBord),
      ),
    ),
  );
}
