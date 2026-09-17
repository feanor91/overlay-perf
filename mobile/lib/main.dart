import 'package:flutter/material.dart';

import 'connection_service.dart';
import 'screens/home_screen.dart';
import 'settings.dart';
import 'theme.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  final settings = await Settings.load();
  runApp(OverlayPerfApp(settings: settings));
}

class OverlayPerfApp extends StatefulWidget {
  final Settings settings;
  const OverlayPerfApp({super.key, required this.settings});

  @override
  State<OverlayPerfApp> createState() => _OverlayPerfAppState();
}

class _OverlayPerfAppState extends State<OverlayPerfApp> {
  late final ConnectionService _connection;

  @override
  void initState() {
    super.initState();
    _connection = ConnectionService(widget.settings);
  }

  @override
  void dispose() {
    _connection.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'Overlay',
      debugShowCheckedModeBanner: false,
      theme: buildAppTheme(),
      home: HomeScreen(connection: _connection, settings: widget.settings),
    );
  }
}
