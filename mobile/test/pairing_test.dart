import 'package:flutter_test/flutter_test.dart';
import 'package:overlay_perf/pairing.dart';

void main() {
  group('PairingLink.parse', () {
    test('adresse locale avec port et jeton', () {
      final link = PairingLink.parse('http://192.168.1.42:8777/#token=Ab12-Cd_34');
      expect(link, isNotNull);
      expect(link!.origin, 'http://192.168.1.42:8777');
      expect(link.token, 'Ab12-Cd_34');
    });

    test('adresse distante https sans port explicite', () {
      final link = PairingLink.parse('https://mon-pc.exemple.fr/#token=xyz');
      expect(link!.origin, 'https://mon-pc.exemple.fr');
      expect(link.token, 'xyz');
    });

    test('jeton avec caracteres a encoder (genere par Uri.EscapeDataString cote agent)', () {
      final link = PairingLink.parse('http://h:1/#token=a%2Fb%3D');
      expect(link!.token, 'a/b=');
    });

    test('sans fragment : adresse seule, aucun jeton', () {
      final link = PairingLink.parse('http://192.168.1.42:8777/');
      expect(link!.origin, 'http://192.168.1.42:8777');
      expect(link.token, isNull);
    });

    test('texte qui n\'est pas une URL http(s) est rejete', () {
      expect(PairingLink.parse('non-une-url'), isNull);
      expect(PairingLink.parse('ftp://serveur/'), isNull);
      expect(PairingLink.parse(''), isNull);
    });
  });
}
