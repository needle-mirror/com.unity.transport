using System.Diagnostics;
using Unity.Burst;
using UnityEngine;

namespace Unity.Networking.Transport.Utilities
{
    /// <summary>
    /// Tracks the last time the app was brought in the foreground.
    /// </summary>
    internal class AppForegroundTracker
    {
        private static readonly SharedStatic<long> s_LastForegroundTimestamp =
            SharedStatic<long>.GetOrCreate<AppForegroundTracker, LastForegroundTimestampKey>();
        
        private class LastForegroundTimestampKey {}

        public static long LastForegroundTimestamp => s_LastForegroundTimestamp.Data;

#if UNITY_IOS && !DOTS_RUNTIME // See MTT-3702 regarding DOTS Runtime.
        [RuntimeInitializeOnLoadMethod]
        private static void InitializeTracker()
        {
            Application.focusChanged += OnFocusChanged;
            s_LastForegroundTimestamp.Data = 0;
        }
#endif

        internal static void OnFocusChanged(bool focused)
        {
            if (focused)
            {
                var stopwatchTime = Stopwatch.GetTimestamp();
                var ts = stopwatchTime / (Stopwatch.Frequency / 1000);
                s_LastForegroundTimestamp.Data = ts;
            }
        }
    }
}