"""Brazilian currency formatting."""

from __future__ import annotations


def format_brazilian_currency(value: float) -> str:
    """Format a float as ``R$ 1.234,56``.

    Uses dot thousands separators and a comma decimal separator. The demo never
    deals with negative values, but the function handles them for completeness.
    """
    formatted = f"{value:,.2f}"
    return "R$ " + formatted.replace(",", "\x00").replace(".", ",").replace("\x00", ".")
