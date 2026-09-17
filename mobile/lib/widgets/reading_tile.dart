import 'package:flutter/material.dart';

import '../formatting.dart';
import '../models.dart';
import '../theme.dart';

/// Une mesure : titre, valeur+unite, puis jauge (mesures bornees) ou mini-courbe
/// (le reste), miroir de la tuile de app.js/style.css.
class ReadingTile extends StatelessWidget {
  final Reading reading;
  final List<double> history;

  const ReadingTile({super.key, required this.reading, required this.history});

  @override
  Widget build(BuildContext context) {
    final color = colorForReading(reading);
    final memory = formatMemory(reading);
    return Container(
      padding: const EdgeInsets.fromLTRB(12, 12, 12, 10),
      decoration: BoxDecoration(
        color: AppColors.carte,
        borderRadius: BorderRadius.circular(14),
        border: Border.all(color: AppColors.carteBord),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(
            reading.label,
            style: const TextStyle(fontSize: 12, color: AppColors.textFaible),
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
          ),
          const SizedBox(height: 6),
          if (reading.value == null)
            const Text('—', style: TextStyle(fontSize: 20, color: AppColors.textFaible))
          else
            RichText(
              overflow: TextOverflow.ellipsis,
              text: TextSpan(
                style: TextStyle(fontSize: 24, fontWeight: FontWeight.w600, color: color),
                children: [
                  TextSpan(text: memory ?? formatNumber(reading.value!)),
                  if (memory == null && reading.unit.isNotEmpty)
                    TextSpan(
                      text: ' ${reading.unit}',
                      style: const TextStyle(fontSize: 13, fontWeight: FontWeight.normal, color: AppColors.textFaible),
                    ),
                ],
              ),
            ),
          const SizedBox(height: 8),
          if (reading.gauge)
            _Gauge(ratio: gaugeRatio(reading), color: reading.value == null ? AppColors.carteBord : color)
          else
            SizedBox(height: 28, child: _Sparkline(values: history)),
        ],
      ),
    );
  }
}

class _Gauge extends StatelessWidget {
  final double ratio;
  final Color color;
  const _Gauge({required this.ratio, required this.color});

  @override
  Widget build(BuildContext context) {
    return ClipRRect(
      borderRadius: BorderRadius.circular(999),
      child: LinearProgressIndicator(
        value: ratio,
        minHeight: 5,
        backgroundColor: Colors.white.withValues(alpha: 0.08),
        valueColor: AlwaysStoppedAnimation(color),
      ),
    );
  }
}

class _Sparkline extends StatelessWidget {
  final List<double> values;
  const _Sparkline({required this.values});

  @override
  Widget build(BuildContext context) {
    return CustomPaint(painter: _SparklinePainter(values), size: Size.infinite);
  }
}

class _SparklinePainter extends CustomPainter {
  final List<double> values;
  _SparklinePainter(this.values);

  @override
  void paint(Canvas canvas, Size size) {
    if (values.length < 2) return;
    final low = values.reduce((a, b) => a < b ? a : b);
    final high = values.reduce((a, b) => a > b ? a : b);
    final span = (high - low).abs() < 1e-9 ? 1.0 : (high - low);
    final step = size.width / (values.length - 1);
    final path = Path();
    for (var i = 0; i < values.length; i++) {
      final x = i * step;
      final y = size.height - ((values[i] - low) / span) * size.height;
      if (i == 0) {
        path.moveTo(x, y);
      } else {
        path.lineTo(x, y);
      }
    }
    canvas.drawPath(path, Paint()
      ..color = AppColors.accent
      ..style = PaintingStyle.stroke
      ..strokeWidth = 1.5);
  }

  @override
  bool shouldRepaint(covariant _SparklinePainter oldDelegate) => oldDelegate.values != values;
}
