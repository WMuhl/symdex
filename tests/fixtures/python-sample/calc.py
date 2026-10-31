"""Calculator module."""

PI = 3.14159

class Calculator:
    """A small calculator."""

    precision = 2

    def __init__(self, bias):
        self.bias = bias

    @staticmethod
    def add(a, b):
        """Add two numbers."""
        return a + b + helper()


def helper():
    """A constant offset."""
    return 1
