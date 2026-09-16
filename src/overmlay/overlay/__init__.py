"""Overlay a l'ecran (PySide6, dependance optionnelle)."""

__all__ = ["HotkeyListener", "OverlayWindow", "creer_minuterie"]


def __getattr__(name: str):
    # Import paresseux : `overmlay serve` ne doit pas exiger PySide6.
    if name == "HotkeyListener":
        from overmlay.overlay.hotkeys import HotkeyListener

        return HotkeyListener
    if name in ("OverlayWindow", "creer_minuterie"):
        from overmlay.overlay import window

        return getattr(window, name)
    raise AttributeError(f"module {__name__!r} n'a pas d'attribut {name!r}")
