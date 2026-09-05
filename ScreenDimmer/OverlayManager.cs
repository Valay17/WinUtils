namespace ScreenDimmer;

internal sealed class OverlayManager : IDisposable
{
    private const string AllKey = "*";

    private readonly Config config_;
    private readonly VDTracker desktops_;
    private readonly Dictionary<string, DimOverlay> overlays_ = new(StringComparer.OrdinalIgnoreCase);

    private sealed class LevelKeyComparer : IEqualityComparer<(string Monitor, string Desktop)>
    {
        internal static readonly LevelKeyComparer Instance = new();

        public bool Equals((string Monitor, string Desktop) x, (string Monitor, string Desktop) y) =>
            string.Equals(x.Monitor, y.Monitor, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Desktop, y.Desktop, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Monitor, string Desktop) key) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(key.Monitor),
                StringComparer.OrdinalIgnoreCase.GetHashCode(key.Desktop));
    }

    private readonly Dictionary<(string Monitor, string Desktop), double> levels_ =
        new(LevelKeyComparer.Instance);

    private bool disposed_;

    internal OverlayManager(Config config, VDTracker desktops)
    {
        config_ = config;
        desktops_ = desktops;

        foreach (var entry in config.DimLevels)
        {
            var key = (entry.MonitorId, entry.DesktopId);

            if (levels_.ContainsKey(key))
            {
                Diagnostics.Write(
                    $"config.json has two dim entries for {entry.MonitorId} / {entry.DesktopId} " +
                    "differing only in case - keeping the last. The file will be tidied on the " +
                    "next change.");
            }

            levels_[key] = Math.Clamp(entry.Dim, 0.0, config_.MaximumDim);
        }

        RebuildOverlays();
    }

    internal event Action? Changed;

    internal IReadOnlyCollection<string> MonitorIds => overlays_.Keys;

    private static string IdFor(MonitorInfo monitor) => monitor.DeviceName;

    internal void RebuildOverlays()
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var monitor in Monitors.All())
        {
            var id = IdFor(monitor);
            present.Add(id);

            if (!overlays_.TryGetValue(id, out var overlay))
            {
                overlay = new DimOverlay(id);
                overlay.SetInverted(inverted_);
                overlays_[id] = overlay;
                Diagnostics.Write(
                    $"Overlay created for {id} at " +
                    $"{monitor.Bounds.Width}x{monitor.Bounds.Height}" +
                    $"+{monitor.Bounds.Left}+{monitor.Bounds.Top}.");
            }

            overlay.ApplyBounds(monitor.Bounds);
        }

        foreach (var id in overlays_.Keys.Where(k => !present.Contains(k)).ToList())
        {
            overlays_[id].Dispose();
            overlays_.Remove(id);
            Diagnostics.Write($"Overlay removed for disconnected monitor {id}.");
        }

        Apply();
    }

    private (string Monitor, string Desktop) KeyFor(string monitorId) =>
        (config_.PerMonitor ? monitorId : AllKey,
         config_.PerVirtualDesktop ? desktops_.CurrentDesktopId : AllKey);

    internal double GetDim(string monitorId) =>
        levels_.TryGetValue(KeyFor(monitorId), out var dim) ? dim : 0.0;

    internal double GetDimUnderCursor()
    {
        var id = MonitorIdUnderCursor();
        return id is null ? 0.0 : GetDim(id);
    }

    internal string? MonitorIdUnderCursor()
    {
        var monitor = Monitors.UnderCursor();
        return monitor is { } found ? IdFor(found) : null;
    }

    internal void SetDim(string monitorId, double dim)
    {
        dim = Math.Clamp(dim, 0.0, config_.MaximumDim);
        levels_[KeyFor(monitorId)] = dim;
        Apply();
        Persist();
        Changed?.Invoke();
    }

    internal void AdjustDim(string monitorId, double delta) =>
        SetDim(monitorId, GetDim(monitorId) + delta);

    internal void SetAll(double dim)
    {
        var level = Math.Clamp(dim, 0.0, config_.MaximumDim);

        var monitorKeys = config_.PerMonitor
            ? overlays_.Keys.ToList()
            : new List<string> { AllKey };

        var desktopKeys = DesktopKeysForSetAll();

        foreach (var monitor in monitorKeys)
        {
            foreach (var desktop in desktopKeys)
            {
                levels_[(monitor, desktop)] = level;
            }
        }

        Apply();
        Persist();
        Changed?.Invoke();

        Diagnostics.Write(
            $"Set all: {level * 100:F0}% written to {monitorKeys.Count} monitor key(s) " +
            $"x {desktopKeys.Count} desktop key(s).");
    }

    private List<string> DesktopKeysForSetAll()
    {
        if (!config_.PerVirtualDesktop)
        {
            return new List<string> { AllKey };
        }

        var all = VDTracker.AllDesktopIds();
        if (all.Count > 0)
        {
            return all.ToList();
        }

        Diagnostics.Write(
            "Set all: the virtual desktop list could not be read, so only the current " +
            "desktop was set. Other desktops keep whatever they had.");

        return new List<string> { desktops_.CurrentDesktopId };
    }

    internal void RekeyDesktopAxis(bool nowPerVirtualDesktop)
    {
        var rebuilt = new Dictionary<(string Monitor, string Desktop), double>(LevelKeyComparer.Instance);

        if (nowPerVirtualDesktop)
        {
            var desktops = VDTracker.AllDesktopIds();
            if (desktops.Count == 0)
            {
                Diagnostics.Write(
                    "Axis toggle: the virtual desktop list could not be read, so stored levels " +
                    "were moved to the current desktop only.");
                desktops = new[] { desktops_.CurrentDesktopId };
            }

            foreach (var (key, level) in levels_)
            {
                if (!string.Equals(key.Desktop, AllKey, StringComparison.Ordinal))
                {
                    rebuilt[key] = level;
                    continue;
                }

                foreach (var desktop in desktops)
                {
                    rebuilt[(key.Monitor, desktop)] = level;
                }
            }
        }
        else
        {
            var current = desktops_.CurrentDesktopId;

            foreach (var group in levels_.GroupBy(pair => pair.Key.Monitor, StringComparer.OrdinalIgnoreCase))
            {
                rebuilt[(group.Key, AllKey)] = RepresentativeValue(
                    group, pair => pair.Key.Desktop, current);
            }
        }

        Replace(rebuilt, $"desktop axis {(nowPerVirtualDesktop ? "on" : "off")}");
    }

    internal void RekeyMonitorAxis(bool nowPerMonitor)
    {
        var rebuilt = new Dictionary<(string Monitor, string Desktop), double>(LevelKeyComparer.Instance);

        if (nowPerMonitor)
        {
            var monitors = overlays_.Keys.ToList();

            foreach (var (key, level) in levels_)
            {
                if (!string.Equals(key.Monitor, AllKey, StringComparison.Ordinal))
                {
                    rebuilt[key] = level;
                    continue;
                }

                foreach (var monitor in monitors)
                {
                    rebuilt[(monitor, key.Desktop)] = level;
                }
            }
        }
        else
        {
            var current = MonitorIdUnderCursor();

            foreach (var group in levels_.GroupBy(pair => pair.Key.Desktop, StringComparer.OrdinalIgnoreCase))
            {
                rebuilt[(AllKey, group.Key)] = RepresentativeValue(
                    group, pair => pair.Key.Monitor, current);
            }
        }

        Replace(rebuilt, $"monitor axis {(nowPerMonitor ? "on" : "off")}");
    }

    private static double RepresentativeValue(
        IEnumerable<KeyValuePair<(string Monitor, string Desktop), double>> group,
        Func<KeyValuePair<(string Monitor, string Desktop), double>, string> axisOf,
        string? current)
    {
        var entries = group.ToList();

        if (current is not null)
        {
            foreach (var entry in entries)
            {
                if (string.Equals(axisOf(entry), current, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.Value;
                }
            }
        }

        return entries.Max(entry => entry.Value);
    }

    private void Replace(Dictionary<(string Monitor, string Desktop), double> rebuilt, string what)
    {
        var before = levels_.Count;

        levels_.Clear();
        foreach (var (key, level) in rebuilt)
        {
            levels_[key] = level;
        }

        Persist();
        Changed?.Invoke();

        Diagnostics.Write($"Re-keyed dim levels for {what}: {before} entry(ies) -> {levels_.Count}.");
    }

    internal void Apply()
    {
        foreach (var (id, overlay) in overlays_)
        {
            overlay.ApplyDim(GetDim(id));
        }
    }

    internal void SetInverted(bool inverted)
    {
        if (inverted_ == inverted)
        {
            return;
        }

        inverted_ = inverted;

        foreach (var overlay in overlays_.Values)
        {
            overlay.SetInverted(inverted);
        }

        Diagnostics.Write(
            inverted
                ? "Inversion reported on (Color Invert Window or Windows' native color filter) - overlays switched to white so dimming still dims."
                : "Inversion reported off - overlays back to black.");
    }

    private bool inverted_;

    internal double PeakDim() =>
        overlays_.Keys.Select(GetDim).DefaultIfEmpty(0.0).Max();

    internal double PeakDimAnywhere() =>
        levels_.Values.DefaultIfEmpty(0.0).Max();

    internal bool AnyDimmedHere() => PeakDim() > 0.001;

    internal bool AnyDimmedAnywhere() => PeakDimAnywhere() > 0.001;

    internal void ClearCurrentDesktop()
    {
        var desktop = config_.PerVirtualDesktop ? desktops_.CurrentDesktopId : AllKey;

        var removed = levels_.Keys
            .Where(key => string.Equals(key.Desktop, desktop, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var key in removed)
        {
            levels_.Remove(key);
        }

        Apply();
        Persist();
        Changed?.Invoke();
        Diagnostics.Write($"Cleared {removed.Count} dim level(s) on the current desktop.");
    }

    internal void ClearAll()
    {
        var count = levels_.Count;
        levels_.Clear();
        Apply();
        Persist();
        Changed?.Invoke();
        Diagnostics.Write($"Cleared all {count} dim level(s), every monitor and desktop.");
    }

    private void Persist()
    {
        config_.DimLevels = levels_
            .Where(pair => pair.Value > 0.001)
            .Select(pair => new Config.DimEntry
            {
                MonitorId = pair.Key.Monitor,
                DesktopId = pair.Key.Desktop,
                Dim = pair.Value,
            })
            .ToList();

        config_.Save();
    }

    public void Dispose()
    {
        if (disposed_)
        {
            return;
        }

        disposed_ = true;

        foreach (var overlay in overlays_.Values)
        {
            overlay.ApplyDim(0.0);
            overlay.Dispose();
        }

        overlays_.Clear();
    }
}
