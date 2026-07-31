using System;
using System.Threading;
using System.Threading.Tasks;
using BarRaider.SdTools;
using BarRaider.SdTools.Payloads;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// ReSharper disable StringLiteralTypo

namespace Elite.Buttons
{

    [PluginActionId("com.mhwlng.elite.explorationdial")]
    public class ExplorationDial : EliteDialBase
    {
        // How long a body resolved in the FSS stays on the touch strip before the
        // display falls back to whatever body you are actually at.
        private const int FssFlashMs = 5000;

        // FSS tuning moves one small step per keypress, so one step per detent is unusably slow
        // to sweep the band with. "Max steps per detent" adds acceleration: turn slowly and you
        // still get exactly one step (fine control near a signal), spin fast and each detent is
        // worth progressively more, up to the configured maximum.
        //
        // Speed is measured as ticks per DialRotate event (see StepsFor). NoGapMs is only the
        // placeholder used for the first rotation, where there is no previous event to compare
        // against; the gap itself is logged as a diagnostic and no longer affects behaviour.
        private const int DefaultMaxSteps = 1;      // 1 = acceleration off, original behaviour
        private const int MaxMaxSteps = 60;
        private const double NoGapMs = 0.0;

        // Ticks per rotate event at which the multiplier is considered maxed out. Stream Deck
        // batches detents when spinning quickly, so this is the primary speed signal.
        private const double FastTicks = 4.0;

        // Discrete presses are rate limited: steps x TickIntervalMs is real elapsed time, and the
        // per-event cap clips anything beyond it. Both were originally set far too conservatively
        // (20ms / 30), which put a hard ceiling on coarse tuning no matter how high the multiplier
        // went. 10ms sustains ~100 presses/sec; the cap is the last line of defence against a hard
        // flick queueing a multi-second blocking run.
        private const int TickIntervalMs = 10;

        // Both timings are exposed in the property inspector so they can be dialled in against
        // real hardware without a rebuild. Defaults come from in-game telemetry: a slow,
        // deliberate turn produced single-tick events 120-190ms apart, so a 100ms hold threshold
        // sits just below that and separates a genuine spin from deliberate clicking.
        private const int DefaultHoldThresholdMs = 100;
        private const int MaxHoldThresholdMs = 500;
        private const int DefaultReleaseDelayMs = 120;
        private const int MaxReleaseDelayMs = 1000;

        private const int WatcherPollMs = 25;

        // Some FSS actions charge while the key is held rather than firing on a tap - the
        // Discovery Scan (honk) is the obvious one. The dial press holds for as long as you
        // hold it, but a touch tap is a single event with no release to follow, so the touch
        // slots hold the key for this long instead.
        private const int DefaultHoldMs = 0;
        private const int MaxHoldMs = 5000;

        protected class PluginSettings
        {
            public static PluginSettings CreateDefaultSettings()
            {
                return new PluginSettings
                {
                    FunctionCcw = string.Empty,
                    FunctionCw = string.Empty,
                    FunctionPress = string.Empty,
                    FunctionTouchPress = string.Empty,
                    FunctionTouchLongPress = string.Empty,
                    HoldMs = DefaultHoldMs.ToString(),
                    MaxSteps = DefaultMaxSteps.ToString(),
                    HoldThresholdMs = DefaultHoldThresholdMs.ToString(),
                    ReleaseDelayMs = DefaultReleaseDelayMs.ToString()
                };
            }

            [JsonProperty(PropertyName = "functionccw")]
            public string FunctionCcw { get; set; }

            [JsonProperty(PropertyName = "functioncw")]
            public string FunctionCw { get; set; }

            [JsonProperty(PropertyName = "functionpress")]
            public string FunctionPress { get; set; }

            [JsonProperty(PropertyName = "functiontouchpress")]
            public string FunctionTouchPress { get; set; }

            [JsonProperty(PropertyName = "functiontouchlongpress")]
            public string FunctionTouchLongPress { get; set; }

            [JsonProperty(PropertyName = "holdms")]
            public string HoldMs { get; set; }

            [JsonProperty(PropertyName = "maxsteps")]
            public string MaxSteps { get; set; }

            [JsonProperty(PropertyName = "holdthresholdms")]
            public string HoldThresholdMs { get; set; }

            [JsonProperty(PropertyName = "releasedelayms")]
            public string ReleaseDelayMs { get; set; }
        }

        private PluginSettings settings;
        private string _lastTitle = null;
        private string _lastValue = null;
        private DateTime _lastRotateUtc = DateTime.MinValue;   // last rotation, for the hold release
        private DateTime _lastEventUtc = DateTime.MinValue;    // last rotate event, for spin detection

        private readonly object _holdLock = new object();
        private string _holdFunction = null;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Thread _holdWatcherThread;

        public ExplorationDial(SDConnection connection, InitialPayload payload) : base(connection, payload)
        {
            if (payload.Settings == null || payload.Settings.Count == 0)
            {
                settings = PluginSettings.CreateDefaultSettings();
                Connection.SetSettingsAsync(JObject.FromObject(settings)).Wait();
            }
            else
            {
                settings = payload.Settings.ToObject<PluginSettings>();
                BackfillDefaults();
            }

            _holdWatcherThread = new Thread(HoldWatcher)
            {
                Name = "Exploration Dial Hold Watcher",
                IsBackground = true
            };
            _holdWatcherThread.Start();
        }

        /// <summary>
        /// A button placed before a setting existed has no key for it, so it deserialises as null
        /// and silently behaves as whatever the clamp floor happens to be - which is how
        /// acceleration shipped switched off for every dial placed before 4.2.0.4. Fill the gaps
        /// and write them back so older buttons heal themselves on load.
        /// </summary>
        private void BackfillDefaults()
        {
            var changed = false;

            if (string.IsNullOrEmpty(settings.MaxSteps)) { settings.MaxSteps = DefaultMaxSteps.ToString(); changed = true; }
            if (string.IsNullOrEmpty(settings.HoldMs)) { settings.HoldMs = DefaultHoldMs.ToString(); changed = true; }
            if (string.IsNullOrEmpty(settings.HoldThresholdMs)) { settings.HoldThresholdMs = DefaultHoldThresholdMs.ToString(); changed = true; }
            if (string.IsNullOrEmpty(settings.ReleaseDelayMs)) { settings.ReleaseDelayMs = DefaultReleaseDelayMs.ToString(); changed = true; }

            if (changed)
            {
                Connection.SetSettingsAsync(JObject.FromObject(settings)).Wait();
            }
        }

        private static int Clamp(string value, int fallback, int min, int max)
        {
            if (!int.TryParse(value, out var result)) result = fallback;

            if (result < min) result = min;
            if (result > max) result = max;

            return result;
        }

        /// <summary>
        /// Which body the touch strip should describe: one just resolved in the FSS wins
        /// for a few seconds, otherwise the body we are actually at.
        /// </summary>
        private static string DisplayBody()
        {
            if (!string.IsNullOrEmpty(EliteData.LastSignalBody) &&
                (DateTime.UtcNow - EliteData.LastSignalUtc).TotalMilliseconds < FssFlashMs)
            {
                return EliteData.LastSignalBody;
            }

            return EliteData.StatusData?.BodyName;
        }

        /// <summary>
        /// Bodies are named "<system> <designator>"; the system part is noise on a 200px strip.
        /// </summary>
        private static string ShortBodyName(string bodyName)
        {
            if (string.IsNullOrEmpty(bodyName)) return string.Empty;

            var system = EliteData.StarSystem;
            if (!string.IsNullOrEmpty(system) &&
                bodyName.StartsWith(system, StringComparison.OrdinalIgnoreCase) &&
                bodyName.Length > system.Length)
            {
                return bodyName.Substring(system.Length).Trim();
            }

            return bodyName;
        }

        public override void OnTick()
        {
            base.OnTick();

            var body = DisplayBody();

            var title = string.Empty;
            var value = string.Empty;

            if (!string.IsNullOrEmpty(body) &&
                EliteData.SignalCache.TryGetValue(body, out var signals) &&
                (signals.BiologyCount > 0 || signals.GeologyCount > 0))
            {
                title = ShortBodyName(body);
                value = $"{signals.BiologyCount}\U0001F33F  {signals.GeologyCount}\U0001F30B";
            }

            // Only push when something actually changed - the tick runs continuously and
            // the touch strip does not need redrawing every second.
            if (title == _lastTitle && value == _lastValue) return;

            _lastTitle = title;
            _lastValue = value;

            Task.Run(async () =>
            {
                try
                {
                    await Connection.SetFeedbackAsync("title", title);
                    await Connection.SetFeedbackAsync("value", value);
                }
                catch (Exception ex)
                {
                    Logger.Instance.LogMessage(TracingLevel.ERROR, $"ExplorationDial SetFeedback: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// One discrete press. Used for a single detent, where the point is to move exactly one
        /// step and stop.
        /// </summary>
        private static void SendOneStep(string function)
        {
            if (string.IsNullOrEmpty(function)) return;

            EliteKeys.SendKeypress(function);
        }

        /// <summary>
        /// Begin (or continue) holding a function's key down while the dial is being spun.
        /// </summary>
        private void StartOrRefreshHold(string function)
        {
            if (string.IsNullOrEmpty(function)) return;

            lock (_holdLock)
            {
                if (_holdFunction != function)
                {
                    ReleaseHoldLocked();          // direction change - let go of the other way first
                    EliteKeys.SendKeypressDown(function);
                    _holdFunction = function;
                }

                _lastRotateUtc = DateTime.UtcNow;
            }
        }

        private void ReleaseHoldLocked()
        {
            if (_holdFunction == null) return;

            EliteKeys.SendKeypressUp(_holdFunction);
            _holdFunction = null;
        }

        /// <summary>
        /// Watches for the spin stopping and releases the held key.
        /// </summary>
        private void HoldWatcher()
        {
            while (!_cts.IsCancellationRequested)
            {
                lock (_holdLock)
                {
                    var releaseDelay = Clamp(settings.ReleaseDelayMs, DefaultReleaseDelayMs, 0, MaxReleaseDelayMs);

                    if (_holdFunction != null &&
                        (DateTime.UtcNow - _lastRotateUtc).TotalMilliseconds >= releaseDelay)
                    {
                        Logger.Instance.LogMessage(TracingLevel.DEBUG,
                            $"ExplorationDial release: {_holdFunction}");

                        ReleaseHoldLocked();
                    }
                }

                Thread.Sleep(WatcherPollMs);
            }

            lock (_holdLock) { ReleaseHoldLocked(); }
        }

        /// <remarks>
        /// Two modes, because bursts of discrete presses cannot be un-queued. A fast sweep used to
        /// enqueue up to 80 presses per event at 10ms each, so nearly a second of movement was
        /// already committed the moment the dial stopped - the frequency kept sliding past the
        /// target and overshot badly.
        ///
        /// Holding the key instead means the game's own auto-repeat does the moving and releasing
        /// stops it at once, so the tail is only the release latency. Single detents stay discrete
        /// so one click is still exactly one step.
        ///
        /// Speed is judged on ticks-per-event: Stream Deck reports a fast spin by BATCHING detents
        /// into one event rather than sending events more often. An inter-event gap measure was
        /// tried and removed - a slow deliberate turn produces single-tick events 120-190ms apart,
        /// which any sensible gap threshold misreads as fast.
        /// </remarks>
        public override void DialRotate(DialRotatePayload payload)
        {
            if (Program.Binding == null) return;

            StreamDeckCommon.ForceStop = false;

            var ticks = payload.Ticks;
            if (ticks == 0) return;

            var function = ticks > 0 ? settings.FunctionCw : settings.FunctionCcw;
            var magnitude = Math.Abs(ticks);

            var max = Clamp(settings.MaxSteps, DefaultMaxSteps, 1, MaxMaxSteps);
            var holdThreshold = Clamp(settings.HoldThresholdMs, DefaultHoldThresholdMs, 0, MaxHoldThresholdMs);

            var now = DateTime.UtcNow;
            var gapMs = _lastEventUtc == DateTime.MinValue
                ? double.MaxValue
                : (now - _lastEventUtc).TotalMilliseconds;
            _lastEventUtc = now;

            // A spin is either detents batched into one event, or events arriving closer together
            // than the threshold. Already holding counts as still spinning.
            var spinning = magnitude >= FastTicks
                        || gapMs <= holdThreshold
                        || _holdFunction != null;

            if (max <= 1 || !spinning)
            {
                Logger.Instance.LogMessage(TracingLevel.DEBUG,
                    $"ExplorationDial rotate: ticks={ticks} gap={(gapMs > 99999 ? -1 : gapMs):F0}ms step (max={max} thr={holdThreshold})");

                for (var i = 0; i < magnitude; i++)
                {
                    if (i > 0) Thread.Sleep(TickIntervalMs);
                    SendOneStep(function);
                }
                return;
            }

            Logger.Instance.LogMessage(TracingLevel.DEBUG,
                $"ExplorationDial rotate: ticks={ticks} gap={(gapMs > 99999 ? -1 : gapMs):F0}ms hold (thr={holdThreshold})");

            StartOrRefreshHold(function);
        }

        public override void DialDown(DialPayload payload)
        {
            if (StreamDeckCommon.InputRunning || Program.Binding == null)
            {
                StreamDeckCommon.ForceStop = true;
                return;
            }

            StreamDeckCommon.ForceStop = false;

            // Held, not tapped: the Discovery Scan charges while its key is down, so the dial
            // press has to mirror how long you actually hold the encoder. Discrete actions are
            // unaffected - a quick press is still a quick press.
            EliteKeys.SendKeypressDown(settings.FunctionPress);
        }

        public override void DialUp(DialPayload payload)
        {
            if (Program.Binding == null) return;

            EliteKeys.SendKeypressUp(settings.FunctionPress);
        }

        public override void TouchPress(TouchpadPressPayload payload)
        {
            if (StreamDeckCommon.InputRunning || Program.Binding == null)
            {
                StreamDeckCommon.ForceStop = true;
                return;
            }

            StreamDeckCommon.ForceStop = false;

            var function = payload.IsLongPress
                ? settings.FunctionTouchLongPress
                : settings.FunctionTouchPress;

            if (string.IsNullOrEmpty(function)) return;

            var hold = Clamp(settings.HoldMs, DefaultHoldMs, 0, MaxHoldMs);

            if (hold <= 0)
            {
                EliteKeys.SendKeypress(function);
                return;
            }

            // A touch is one event with no release to wait for, so synthesise the hold.
            EliteKeys.SendKeypressDown(function);
            Thread.Sleep(hold);
            EliteKeys.SendKeypressUp(function);
        }

        public override void ReceivedSettings(ReceivedSettingsPayload payload)
        {
            BarRaider.SdTools.Tools.AutoPopulateSettings(settings, payload.Settings);
        }

        public override void Dispose()
        {
            // Cancel first, then let the watcher's exit path release anything still held so the
            // key cannot be left stuck down if the button is removed mid-spin.
            _cts.Cancel();
            _holdWatcherThread?.Join(500);
            lock (_holdLock) { ReleaseHoldLocked(); }

            base.Dispose();
        }

    }
}
