import 'package:flutter/material.dart';
import 'package:mobile_scanner/mobile_scanner.dart';

import '../theme.dart';

/// Scanne le QR code affiche par « Appairer un telephone » sur le PC (agent
/// Python ou OverlayPerf.exe) : une URL du type `http://192.168.1.42:8777/#token=...`.
/// Renvoie le texte brut du QR ; l'appelant se charge d'en extraire adresse et jeton.
class QrScanScreen extends StatefulWidget {
  const QrScanScreen({super.key});

  @override
  State<QrScanScreen> createState() => _QrScanScreenState();
}

class _QrScanScreenState extends State<QrScanScreen> {
  final MobileScannerController _controller = MobileScannerController(
    detectionSpeed: DetectionSpeed.noDuplicates,
  );
  bool _handled = false;

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  void _onDetect(BarcodeCapture capture) {
    if (_handled) return;
    final value = capture.barcodes.firstOrNull?.rawValue;
    if (value == null || value.isEmpty) return;
    _handled = true;
    Navigator.of(context).pop(value);
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: const Text('Scanner le QR code'),
        actions: [
          IconButton(
            icon: ValueListenableBuilder(
              valueListenable: _controller,
              builder: (context, state, child) => Icon(
                state.torchState == TorchState.on ? Icons.flash_on : Icons.flash_off,
              ),
            ),
            tooltip: 'Torche',
            onPressed: () => _controller.toggleTorch(),
          ),
        ],
      ),
      body: Stack(
        fit: StackFit.expand,
        children: [
          MobileScanner(
            controller: _controller,
            onDetect: _onDetect,
            errorBuilder: (context, error) => _CameraError(error: error),
          ),
          // Cadre de visee, purement indicatif : mobile_scanner detecte sur toute l'image.
          Center(
            child: Container(
              width: 240,
              height: 240,
              decoration: BoxDecoration(
                border: Border.all(color: AppColors.accent, width: 2),
                borderRadius: BorderRadius.circular(16),
              ),
            ),
          ),
          Positioned(
            left: 24,
            right: 24,
            bottom: 32,
            child: Text(
              "Ouvrez « Appairer un telephone » depuis l'icone d'Overlay sur le PC, "
              "et visez le QR code affiche.",
              textAlign: TextAlign.center,
              style: TextStyle(
                color: Colors.white,
                shadows: [Shadow(blurRadius: 8, color: Colors.black.withValues(alpha: 0.8))],
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class _CameraError extends StatelessWidget {
  final MobileScannerException error;
  const _CameraError({required this.error});

  String _message() {
    switch (error.errorCode) {
      case MobileScannerErrorCode.permissionDenied:
        return "Acces a la camera refuse. Autorisez-le dans les reglages Android "
            "de l'application (Parametres > Applications > Overlay > Autorisations).";
      case MobileScannerErrorCode.controllerUninitialized:
        return "Camera pas encore prete, patientez…";
      default:
        return 'Camera indisponible : ${error.errorDetails?.message ?? error.errorCode}';
    }
  }

  @override
  Widget build(BuildContext context) {
    return ColoredBox(
      color: AppColors.fond,
      child: Center(
        child: Padding(
          padding: const EdgeInsets.all(32),
          child: Text(_message(), textAlign: TextAlign.center, style: const TextStyle(color: AppColors.textFaible)),
        ),
      ),
    );
  }
}
