import 'package:flutter_test/flutter_test.dart';

import 'package:overlay_perf/settings.dart';
import 'package:overlay_perf/main.dart';

void main() {
  testWidgets('demarre sans erreur sans aucun reglage', (tester) async {
    await tester.pumpWidget(OverlayPerfApp(settings: Settings()));
    await tester.pump();
    // Sans agent joignable : l'ecran d'attente s'affiche, pas d'exception.
    expect(find.text('Overlay'), findsWidgets);
  });
}
