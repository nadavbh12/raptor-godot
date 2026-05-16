namespace Raptor.View;

internal static class HudWarning
{
    public const int ShieldLowThreshold = 10;
    public const int MapBottom = 200 - 18;
    public const int SystemDamageY = MapBottom - 9;

    public sealed class State
    {
        // RAP_DisplayStats static starts TRUE and toggles only while shield is low.
        private bool _blinkFlag = true;
        private int _damageBlinksRemaining;

        public bool ShieldLowVisible { get; private set; }
        public bool SystemDamageVisible { get; private set; }

        public void Tick(int shield, int gameLoopIter, bool systemDamaged)
        {
            if (shield > ShieldLowThreshold)
            {
                ShieldLowVisible = false;
                SystemDamageVisible = false;
                return;
            }

            if ((gameLoopIter % 8) == 0)
            {
                _blinkFlag = !_blinkFlag;
                if (_blinkFlag && _damageBlinksRemaining > 0)
                    _damageBlinksRemaining--;
            }

            if (systemDamaged)
                _damageBlinksRemaining = 2;

            ShieldLowVisible = _blinkFlag;
            SystemDamageVisible = _blinkFlag && _damageBlinksRemaining > 0;
        }
    }

    public static int CenterX(int spriteWidth) => (320 - spriteWidth) >> 1;
}
