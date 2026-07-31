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
        // Speed is measured as the gap between DialRotate events. At or above SlowGapMs the
        // multiplier is 1; at or below FastGapMs it is the maximum; in between it interpolates.
        private const int DefaultMaxSteps = 1;      // 1 = acceleration off, original behaviour
        private const int MaxMaxSteps = 60;
        private const double SlowGapMs = 200.0;
        private const double FastGapMs = 40.0;

        // Discrete presses are rate limited: steps x TickIntervalMs is real elapsed time, and the
        // per-event cap clips anything beyond it. Both were originally set far too conservatively
        // (20ms / 30), which put a hard ceiling on coarse tuning no matter how high the multiplier
        // went. 10ms sustains ~100 presses/sec; the cap is the last line of defence against a hard
        // flick queueing a multi-second blocking run.
        private const int TickIntervalMs = 10;
        private const int MaxStepsPerEvent = 150;

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
                    MaxSteps = DefaultMaxSteps.ToString()
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
        }

        private PluginSettings settings;
        private string _lastTitle = null;
        private string _lastValue = null;
        private DateTime _lastRotateUtc = DateTime.MinValue;

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
        /// Discrete press per detent, unlike the generic Dial which holds the key down while
        /// you turn. Everything here - tuning, stepped zoom, genus - is a stepped control, and
        /// holding would either overshoot or run away on key auto-repeat.
        /// </summary>
        private static void SendSteps(string function, int steps)
        {
            if (string.IsNullOrEmpty(function)) return;

            if (steps < 1) steps = 1;
            if (steps > MaxStepsPerEvent) steps = MaxStepsPerEvent;

            for (var i = 0; i < steps; i++)
            {
                if (i > 0) Thread.Sleep(TickIntervalMs);

                EliteKeys.SendKeypress(function);
            }
        }

        /// <summary>
        /// Steps to send for this rotation. One per detent when turning slowly, scaling up to
        /// the configured maximum as the gap between rotate events shrinks.
        /// </summary>
        private int StepsFor(int ticks)
        {
            var max = Clamp(settings.MaxSteps, DefaultMaxSteps, 1, MaxMaxSteps);

            var now = DateTime.UtcNow;
            var gapMs = _lastRotateUtc == DateTime.MinValue
                ? SlowGapMs
                : (now - _lastRotateUtc).TotalMilliseconds;
            _lastRotateUtc = now;

            if (max <= 1) return ticks;

            double multiplier;
            if (gapMs >= SlowGapMs) multiplier = 1.0;
            else if (gapMs <= FastGapMs) multiplier = max;
            else multiplier = 1.0 + (max - 1.0) * (SlowGapMs - gapMs) / (SlowGapMs - FastGapMs);

            return (int)Math.Round(ticks * multiplier);
        }

        public override void DialRotate(DialRotatePayload payload)
        {
            if (StreamDeckCommon.InputRunning || Program.Binding == null)
            {
                StreamDeckCommon.ForceStop = true;
                return;
            }

            StreamDeckCommon.ForceStop = false;

            if (payload.Ticks > 0)
            {
                SendSteps(settings.FunctionCw, StepsFor(payload.Ticks));
            }
            else if (payload.Ticks < 0)
            {
                SendSteps(settings.FunctionCcw, StepsFor(-payload.Ticks));
            }
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
            base.Dispose();
        }

    }
}
