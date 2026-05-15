using System;

namespace Raptor.Sim;

/// <summary>
/// macOS/BSD libc rand() compatibility for the C parity build.
/// C uses GFX/types.h random(x) == rand() % x after srand(seed).
/// </summary>
internal sealed class LegacyRandom : Random
{
    private const int Modulus = 2147483647;
    private const int Multiplier = 16807;
    private int _state;

    public LegacyRandom(int seed)
    {
        _state = seed % Modulus;
        if (_state <= 0) _state += Modulus - 1;
    }

    public override int Next()
    {
        _state = (int)(((long)_state * Multiplier) % Modulus);
        return _state;
    }

    public override int Next(int maxValue)
    {
        if (maxValue <= 0) throw new ArgumentOutOfRangeException(nameof(maxValue));
        return Next() % maxValue;
    }

    public override int Next(int minValue, int maxValue)
    {
        if (minValue > maxValue) throw new ArgumentOutOfRangeException(nameof(minValue));
        if (minValue == maxValue) return minValue;
        return minValue + Next(maxValue - minValue);
    }
}
