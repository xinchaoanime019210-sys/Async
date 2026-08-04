using System.Reflection;

namespace AsyncInput.Mobile;

/// <summary>
/// 可选的 Replay 播放态桥接。两个 Mod 不共享程序集引用，避免加载顺序和版本形成硬依赖。
/// </summary>
internal static class ReplayCompatibility
{
    private const string ReplayPluginTypeName = "Replay.Mobile.ReplayPlugin";
    private const long ProbeIntervalNanos = 1_000_000_000L;

    private static readonly object Sync = new();
    private static Func<bool>? _reader;
    private static long _nextProbeNanos;

    internal static bool IsPlaybackActive
    {
        get
        {
            Func<bool>? reader = Volatile.Read(ref _reader);
            if (reader == null)
            {
                TryResolveReader();
                reader = Volatile.Read(ref _reader);
            }

            try
            {
                return reader?.Invoke() == true;
            }
            catch
            {
                // A mod can be unloaded/reloaded without unloading its assembly.
                // Treat a stale bridge as inactive and probe again later.
                Volatile.Write(ref _reader, null);
                return false;
            }
        }
    }

    private static void TryResolveReader()
    {
        long now = ClockSync.GetMonotonicNanos();
        if (now > 0L && now < Volatile.Read(ref _nextProbeNanos))
            return;

        lock (Sync)
        {
            if (_reader != null)
                return;

            if (now > 0L)
                _nextProbeNanos = now + ProbeIntervalNanos;

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type? type;
                try
                {
                    type = assembly.GetType(ReplayPluginTypeName, throwOnError: false);
                }
                catch
                {
                    continue;
                }

                MethodInfo? getter = type?.GetProperty(
                    "IsPlaybackActive",
                    BindingFlags.Public | BindingFlags.Static)?.GetGetMethod();
                if (getter == null || getter.ReturnType != typeof(bool))
                    continue;

                try
                {
                    Func<bool> reader = (Func<bool>)getter.CreateDelegate(typeof(Func<bool>));
                    Volatile.Write(ref _reader, reader);
                }
                catch
                {
                    // Keep the async input path usable if the runtime refuses
                    // to create a delegate for the optional property.
                }
                return;
            }
        }
    }
}
