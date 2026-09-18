#nullable enable
namespace Playloop.Samples.TraceFixture
{
    /// <summary>
    /// The known path the Trace fixture walks, as pure functions of unscaled
    /// elapsed seconds. No engine types, no randomness, no state: the same
    /// table drives the runtime fixture and the tests that audit its output,
    /// so "known path" has one source of truth.
    ///
    /// <para>
    /// Three rooms side by side on the XY plane, each 20 by 10 units. The
    /// player starts in <c>hall</c> at (1, 5), walks +x at 4 units a second,
    /// attacks for half a second in <c>crypt</c>, jumps once, reaches
    /// <c>vault</c>, stops at (50, 5) and dies there. One named entity, a key
    /// at (30, 8), sits still until it is picked up as the player leaves
    /// <c>crypt</c>.
    /// </para>
    ///
    /// <para>
    /// Two runs in one session. The first run ends with <c>Death</c> at
    /// 13.0 s. Nothing is pushed between runs. At 15.0 s the player respawns
    /// in <c>hall</c> at (1, 5), walks +x at the same speed, stops at
    /// (10, 5) at 17.25 s, and quits at 18.0 s. The key does not come back.
    /// </para>
    /// </summary>
    public static class TraceFixtureRoute
    {
        public const int Hz = 10;
        public const string Plane = "xy";

        public const string RoomHall = "hall";
        public const string RoomCrypt = "crypt";
        public const string RoomVault = "vault";

        /// <summary>Declared bounds, [xMin, yMin, xMax, yMax].</summary>
        public static readonly float[] HallBounds = { 0f, 0f, 20f, 10f };
        public static readonly float[] CryptBounds = { 20f, 0f, 40f, 10f };
        public static readonly float[] VaultBounds = { 40f, 0f, 60f, 10f };

        /// <summary>Action labels; bit i is Actions[i].</summary>
        public static readonly string[] Actions = { "move", "jump", "attack" };
        public const int BitMove = 1 << 0;
        public const int BitJump = 1 << 1;
        public const int BitAttack = 1 << 2;

        public const double StartX = 1.0;
        public const double LaneY = 5.0;
        public const double SpeedUnitsPerSec = 4.0;

        public const double CryptEntrySec = 4.75;
        public const double AttackStartSec = 6.0;
        public const double AttackEndSec = 6.5;
        public const double JumpStartSec = 8.0;
        public const double JumpEndSec = 8.3;
        public const int JumpFacingDeg = 90;
        public const double VaultEntrySec = 9.75;
        public const double StopSec = 12.25;
        public const double EndSec = 13.0;

        /// <summary>The death spot: where the first run stops and ends.</summary>
        public const double DeathX = 50.0;
        public const double DeathY = LaneY;

        /// <summary>The second run opens here, back in <c>hall</c> at the start.</summary>
        public const double RespawnSec = 15.0;
        /// <summary>Where the second run stops walking.</summary>
        public const double Run2StopX = 10.0;
        public const double Run2StopSec = 17.25;
        /// <summary>The second run ends with <c>Quit</c> here, then the session ends.</summary>
        public const double Run2EndSec = 18.0;

        public const string EntityKey = "key";
        public const double KeyX = 30.0;
        public const double KeyY = 8.0;
        public const double KeyClearSec = VaultEntrySec;

        /// <summary>
        /// True between the first run's end and the respawn: the fixture
        /// pushes no state then, and the Trace samples nothing.
        /// </summary>
        public static bool IsBetweenRuns(double tSec) => tSec >= EndSec && tSec < RespawnSec;

        /// <summary>True from the respawn on: the second run.</summary>
        public static bool IsSecondRun(double tSec) => tSec >= RespawnSec;

        /// <summary>Room id at <paramref name="tSec"/>: the room whose x range holds the player.</summary>
        public static string RoomAt(double tSec)
        {
            if (IsSecondRun(tSec)) return RoomHall;
            if (tSec < CryptEntrySec) return RoomHall;
            if (tSec < VaultEntrySec) return RoomCrypt;
            return RoomVault;
        }

        /// <summary>Declared bounds for a room id, or null for an unknown id.</summary>
        public static float[]? BoundsFor(string roomId)
        {
            switch (roomId)
            {
                case RoomHall: return HallBounds;
                case RoomCrypt: return CryptBounds;
                case RoomVault: return VaultBounds;
                default: return null;
            }
        }

        /// <summary>Player position at <paramref name="tSec"/>.</summary>
        public static (double x, double y) PositionAt(double tSec)
        {
            if (IsSecondRun(tSec))
            {
                if (tSec >= Run2StopSec) return (Run2StopX, LaneY);
                return (StartX + SpeedUnitsPerSec * (tSec - RespawnSec), LaneY);
            }
            if (tSec >= StopSec) return (DeathX, DeathY);
            return (StartX + SpeedUnitsPerSec * tSec, LaneY);
        }

        /// <summary>Facing in degrees: 0 along +x, 90 (up) through the jump window.</summary>
        public static int FacingAt(double tSec)
        {
            if (IsSecondRun(tSec)) return 0;
            if (tSec >= JumpStartSec && tSec <= JumpEndSec) return JumpFacingDeg;
            return 0;
        }

        /// <summary>Action mask at <paramref name="tSec"/>.</summary>
        public static int ActionBitsAt(double tSec)
        {
            if (IsSecondRun(tSec)) return tSec >= Run2StopSec ? 0 : BitMove;
            if (tSec >= StopSec) return 0;
            int bits = BitMove;
            if (tSec >= AttackStartSec && tSec <= AttackEndSec) bits |= BitAttack;
            if (tSec >= JumpStartSec && tSec <= JumpEndSec) bits |= BitJump;
            return bits;
        }

        /// <summary>Movement axes at <paramref name="tSec"/>: (1, 0) while walking, (0, 0) once stopped.</summary>
        public static (double ax, double ay) AxesAt(double tSec)
        {
            if (IsSecondRun(tSec)) return tSec >= Run2StopSec ? (0.0, 0.0) : (1.0, 0.0);
            return tSec >= StopSec ? (0.0, 0.0) : (1.0, 0.0);
        }

        /// <summary>The key's position while it exists. False once it has been cleared; it never comes back.</summary>
        public static bool EntityAt(double tSec, out double x, out double y)
        {
            if (tSec >= KeyClearSec)
            {
                x = 0.0;
                y = 0.0;
                return false;
            }
            x = KeyX;
            y = KeyY;
            return true;
        }
    }
}
