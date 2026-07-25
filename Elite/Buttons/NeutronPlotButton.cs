using System;
using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BarRaider.SdTools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Elite.Buttons
{
    [PluginActionId("com.mhwlng.elite.neutronplot")]
    public class NeutronPlotButton : EliteKeypadBase
    {
        protected class PluginSettings
        {
            public static PluginSettings CreateDefaultSettings() => new PluginSettings
            {
                CsvPath           = string.Empty,
                ClearFile         = string.Empty,
                Function          = string.Empty,
                FunctionLongPress = string.Empty,
                InfoUpper         = string.Empty,
                InfoUpperColor    = "#ffffff",
                InfoMid           = string.Empty,
                InfoMidColor      = "#ffffff",
                InfoLower         = string.Empty,
                InfoLowerColor    = "#ffffff",
                InfoBoostColor    = "#00ff00",
                RouteLabel        = string.Empty
            };

            [FilenameProperty]
            [JsonProperty(PropertyName = "csvPath")]
            public string CsvPath { get; set; }

            [JsonProperty(PropertyName = "clearFile")]
            public string ClearFile { get; set; }

            [JsonProperty(PropertyName = "routeLabel")]
            public string RouteLabel { get; set; }

            [JsonProperty(PropertyName = "function")]
            public string Function { get; set; }

            [JsonProperty(PropertyName = "functionLongPress")]
            public string FunctionLongPress { get; set; }

            [JsonProperty(PropertyName = "infoUpper")]
            public string InfoUpper { get; set; }

            [JsonProperty(PropertyName = "infoUpperColor")]
            public string InfoUpperColor { get; set; }

            [JsonProperty(PropertyName = "infoMid")]
            public string InfoMid { get; set; }

            [JsonProperty(PropertyName = "infoMidColor")]
            public string InfoMidColor { get; set; }

            [JsonProperty(PropertyName = "infoLower")]
            public string InfoLower { get; set; }

            [JsonProperty(PropertyName = "infoLowerColor")]
            public string InfoLowerColor { get; set; }

            [JsonProperty(PropertyName = "infoBoostColor")]
            public string InfoBoostColor { get; set; }
        }

        private static readonly TimeSpan LongPressThreshold = TimeSpan.FromMilliseconds(2000);

        private readonly SemaphoreSlim displayLock = new SemaphoreSlim(1, 1);
        private PluginSettings settings;
        private DateTime keyPressedAt = DateTime.MinValue;

        public NeutronPlotButton(SDConnection connection, InitialPayload payload) : base(connection, payload)
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

            AsyncHelper.RunSync(HandleDisplay);
        }

        public override void KeyPressed(KeyPayload payload)
        {
            keyPressedAt = DateTime.UtcNow;
        }

        public override void KeyReleased(KeyPayload payload)
        {
            var elapsed = DateTime.UtcNow - keyPressedAt;
            keyPressedAt = DateTime.MinValue;

            if (elapsed >= LongPressThreshold)
                ExecuteFunction(settings.FunctionLongPress);
            else
                ExecuteFunction(settings.Function);

            AsyncHelper.RunSync(HandleDisplay);
        }

        public override async void OnTick()
        {
            base.OnTick();
            await HandleDisplay();
        }

        public override void ReceivedSettings(ReceivedSettingsPayload payload)
        {
            var previousCsvPath = settings.CsvPath;
            BarRaider.SdTools.Tools.AutoPopulateSettings(settings, payload.Settings);

            if (settings.ClearFile == "true")
            {
                NeutronPlotRoute.CsvClear();
                settings.CsvPath   = string.Empty;
                settings.ClearFile = string.Empty;
                Connection.SetSettingsAsync(JObject.FromObject(settings)).Wait();
            }
            else if (!string.IsNullOrEmpty(settings.CsvPath) && settings.CsvPath != previousCsvPath)
            {
                if (File.Exists(settings.CsvPath))
                {
                    var snapshot = NeutronPlotRoute.CsvNew(settings.CsvPath);
                    if (!snapshot.IsLoaded)
                    {
                        Connection.ShowAlert().Wait();
                        _ = AutoClearInvalidCsvAsync();
                    }
                }
                else
                {
                    Connection.ShowAlert().Wait();
                    _ = AutoClearInvalidCsvAsync();
                }
            }

            AsyncHelper.RunSync(HandleDisplay);
        }

        public override void Dispose()
        {
            displayLock?.Dispose();
            base.Dispose();
        }

        private async Task AutoClearInvalidCsvAsync()
        {
            try
            {
                await Task.Delay(1000);
                NeutronPlotRoute.CsvClear();
                settings.CsvPath   = string.Empty;
                settings.ClearFile = string.Empty;
                await Connection.SetSettingsAsync(JObject.FromObject(settings));
                await HandleDisplay();
            }
            catch (Exception ex)
            {
                Logger.Instance.LogMessage(TracingLevel.ERROR, "NeutronPlotButton AutoClearInvalidCsvAsync: " + ex);
            }
        }

        private async Task HandleDisplay()
        {
            if (!await displayLock.WaitAsync(0)) return;
            try
            {
                var snapshot = NeutronPlotRoute.GetSnapshot();
                await SyncRouteSettings(snapshot);

                using (var bitmap = CreateDefaultBitmap())
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    DrawInfoZones(graphics, bitmap.Width, snapshot);

                    var imgBase64 = BarRaider.SdTools.Tools.ImageToBase64(bitmap, true);
                    await Connection.SetImageAsync(imgBase64);
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.LogMessage(TracingLevel.ERROR, "NeutronPlotButton HandleDisplay: " + ex);
            }
            finally
            {
                displayLock.Release();
            }
        }

        // Keeps the PI's CsvPath/route-label in sync with the shared route service state.
        private async Task SyncRouteSettings(NeutronPlotSnapshot snapshot)
        {
            var changed = false;

            if (snapshot.IsSpanshRoute)
            {
                // Spansh routes have no CSV file — keep the file input empty.
                if (!string.IsNullOrEmpty(settings.CsvPath)) { settings.CsvPath = string.Empty; changed = true; }
            }
            else if (snapshot.IsLoaded && string.IsNullOrEmpty(settings.CsvPath))
            {
                settings.CsvPath = snapshot.CsvPath; changed = true;
            }
            else if (!snapshot.IsLoaded && !string.IsNullOrEmpty(settings.CsvPath))
            {
                settings.CsvPath = string.Empty; changed = true;
            }

            var desiredLabel = snapshot.IsPlotting ? "Plotting..."
                : snapshot.IsSpanshRoute ? "Auto Plot Route"
                : string.Empty;
            if (settings.RouteLabel != desiredLabel) { settings.RouteLabel = desiredLabel; changed = true; }

            if (changed) await Connection.SetSettingsAsync(JObject.FromObject(settings));
        }

        private static Bitmap CreateDefaultBitmap()
        {
            var bitmap = new Bitmap(256, 256);
            using (var g = Graphics.FromImage(bitmap))
                g.Clear(Color.Black);
            return bitmap;
        }

        private void DrawInfoZones(Graphics graphics, int width, NeutronPlotSnapshot snapshot)
        {
            if (snapshot.IsPlotting)
            {
                DrawInZone(graphics, width, "PLOTTING", 70, 66, Color.Orange, 34);
                DrawInZone(graphics, width, "...",      130, 50, Color.Orange, 34);
                return;
            }

            if (!snapshot.IsLoaded && !string.IsNullOrEmpty(snapshot.SpanshError))
            {
                DrawInZone(graphics, width, snapshot.SpanshError, 86, 66, Color.Red, 28);
                return;
            }

            if (!snapshot.IsLoaded)
            {
                DrawInZone(graphics, width, "NO ROUTE", 86, 66, Color.Gray, 24);
                return;
            }

            const int upperPt = 48; // 36px × 4/3
            const int midPt   = 48;
            const int lowerPt = 48;

            var upperIsSystem = IsSystemNameType(settings.InfoUpper);
            var midIsSystem   = IsSystemNameType(settings.InfoMid);

            var upperValue = GetDisplayValue(settings.InfoUpper, snapshot);
            var midValue   = GetDisplayValue(settings.InfoMid,   snapshot);
            var lowerValue = GetDisplayValue(settings.InfoLower, snapshot);

            var upperColor = ResolveColor(settings.InfoUpper, settings.InfoUpperColor, snapshot);
            var midColor   = ResolveColor(settings.InfoMid,   settings.InfoMidColor,   snapshot);
            var lowerColor = ResolveColor(settings.InfoLower, settings.InfoLowerColor, snapshot);

            if (upperIsSystem)
            {
                // Rows 1+2 combined (142px) = system name block; Row 3 = lower value
                var (line1, line2) = SplitSystemName(upperValue);
                DrawSystemNameInZone(graphics, width, line1, line2, 10, 142, upperColor, upperPt);
                DrawInZone(graphics, width, lowerValue, 162, 58, lowerColor, lowerPt);
            }
            else if (midIsSystem)
            {
                // Row 1 = upper value; Rows 2+3 combined (134px) = system name block
                var (line1, line2) = SplitSystemName(midValue);
                DrawInZone(graphics, width, upperValue, 10,  66,  upperColor, upperPt);
                DrawSystemNameInZone(graphics, width, line1, line2, 86, 134, midColor, midPt);
            }
            else
            {
                DrawInZone(graphics, width, upperValue, 10,  66, upperColor, upperPt);
                DrawInZone(graphics, width, midValue,   86,  66, midColor,   midPt);
                DrawInZone(graphics, width, lowerValue, 162, 58, lowerColor, lowerPt);
            }
        }

        private static bool IsSystemNameType(string infoType) =>
            infoType == "targetSystemName" ||
            infoType == "currentSystemName";

        private static (string, string) SplitSystemName(string name)
        {
            if (string.IsNullOrEmpty(name)) return (name, string.Empty);
            var match = Regex.Match(name, @"^(.*?)\s+([A-Z]{2,4}-[A-Z]\s+[a-zA-Z]\d+(?:-\d+)?)$");
            if (match.Success)
                return (match.Groups[1].Value, match.Groups[2].Value);
            var lastSpace = name.LastIndexOf(' ');
            if (lastSpace > 0)
                return (name.Substring(0, lastSpace), name.Substring(lastSpace + 1));
            return (name, string.Empty);
        }

        private static string GetDisplayValue(string infoType, NeutronPlotSnapshot snapshot)
        {
            switch (infoType)
            {
                case "currentSystemName":   return snapshot.SystemCurrent;
                case "targetSystemName":    return snapshot.SystemTarget;
                case "routeStatus":         return snapshot.RouteStatus;
                case "distanceTravelled":   return snapshot.WaypointCurrent >= 0 ? $"{snapshot.DistanceTravelled:#,##0.0} LY" : string.Empty;
                case "distanceTarget":
                    if (snapshot.WaypointCurrent >= 0) return $"{snapshot.DistanceTarget:#,##0.0} LY";
                    return snapshot.IsOffRouteEstimate ? $"~{snapshot.DistanceTarget:#,##0.0} LY" : string.Empty;
                case "jumpsToRejoin":       return snapshot.IsOffRouteEstimate ? $"~{snapshot.JumpsToRejoin}" : string.Empty;
                case "destinationDistance": return snapshot.WaypointCurrent >= 0 ? $"{snapshot.DistanceDestination:#,##0.0} LY" : string.Empty;
                case "currentJumpNumber":   return (snapshot.WaypointMax - snapshot.JumpRemaining).ToString();
                case "totalJumps":          return snapshot.WaypointMax.ToString();
                case "jumpsRemaining":      return snapshot.JumpRemaining.ToString();
                case "jumpsToRefuel":       return snapshot.JumpsToRefuel > 0 ? $"{FuelIcon} {snapshot.JumpsToRefuel}" : string.Empty;
                case "jumpSummary":         return snapshot.JumpSummary;
                case "tripPercentage":      return $"{snapshot.JumpPercent:F1}%";
                case "refuelAtTarget":      return FormatRefuel(snapshot);
                case "neutronAtTarget":     return snapshot.StarNeutron;
                case "jumpRange":           return FormatJumpRange();
                case "fuelMain":            return $"{EliteData.StatusData.Fuel.FuelMain:0.0}t";
                case "nextJumpStar":        return EliteData.FormatNextJumpStar();
                default:                    return string.Empty;
            }
        }

        // ⛽ glyph behind a constant so it can be swapped for a text tag if it doesn't render on device.
        private const string FuelIcon = "⛽";

        // Extends "Refuel at Target": shows the nearest scoopable star distance once EDSM-enriched,
        // else falls back to the original yes/blank behaviour until the lookup completes.
        private static string FormatRefuel(NeutronPlotSnapshot snapshot)
        {
            if (!double.IsNaN(snapshot.ScoopableLs))
            {
                if (snapshot.ScoopableLs < 0) return string.Empty;            // known: no scoopable star
                return snapshot.ScoopableLs < 100
                    ? $"{FuelIcon} {snapshot.ScoopableLs:0.0} Ls"
                    : $"{FuelIcon} >100 Ls";
            }
            return snapshot.StarRefuel;                                       // not yet enriched
        }

        private static string FormatJumpRange()
        {
            var range = EliteData.GetJumpRange(boosted: true);
            return $"{range:0.0} LY{(EliteData.IsFsdBoosted ? " ⚡" : "")}";
        }

        private Color ResolveColor(string infoType, string hex, NeutronPlotSnapshot snapshot = null)
        {
            if (infoType == "jumpRange" && EliteData.IsFsdBoosted)
                return ParseColor(settings.InfoBoostColor, Color.FromArgb(0, 255, 0));

            // Fuel star within easy reach (< 50 Ls) → green; otherwise the configured colour (white).
            if (infoType == "refuelAtTarget" && snapshot != null &&
                !double.IsNaN(snapshot.ScoopableLs) && snapshot.ScoopableLs >= 0 && snapshot.ScoopableLs < 50)
                return Color.FromArgb(0, 255, 0);

            var color = ParseColor(hex, Color.White);

            // Off-route metrics grey out — except distanceTarget when it's a live straight-line
            // estimate (the "~" already signals it's approximate, so show it at full strength).
            if (snapshot != null && snapshot.WaypointCurrent < 0 && IsWaypointCurrentDependent(infoType)
                && !(snapshot.IsOffRouteEstimate && infoType == "distanceTarget"))
                return Color.FromArgb(color.R / 2, color.G / 2, color.B / 2);

            return color;
        }

        private static bool IsWaypointCurrentDependent(string infoType) =>
            infoType == "tripPercentage"      ||
            infoType == "distanceTravelled"   ||
            infoType == "distanceTarget"      ||
            infoType == "destinationDistance" ||
            infoType == "currentJumpNumber"   ||
            infoType == "jumpsRemaining"      ||
            infoType == "jumpsToRefuel"       ||
            infoType == "jumpSummary";

        private static Color ParseColor(string hex, Color fallback)
        {
            try   { return (Color)new ColorConverter().ConvertFromString(hex); }
            catch { return fallback; }
        }

        private static void DrawInZone(Graphics graphics, int width, string text, int zoneTop, int zoneHeight, Color color, int maxPt)
        {
            if (string.IsNullOrEmpty(text)) return;
            for (var size = maxPt; size >= 8; size--)
            {
                using (var font = new Font("Arial", size, FontStyle.Bold))
                using (var brush = new SolidBrush(color))
                {
                    var measured = graphics.MeasureString(text, font);
                    if (measured.Width > width * 0.9f) continue;
                    if (measured.Height > zoneHeight) continue;
                    var x = (width - measured.Width) / 2f;
                    var y = zoneTop + (zoneHeight - measured.Height) / 2f;
                    graphics.DrawString(text, font, brush, x, y);
                    return;
                }
            }
        }

        private static void DrawSystemNameInZone(Graphics graphics, int width, string line1, string line2, int zoneTop, int zoneHeight, Color color, int maxPt)
        {
            if (string.IsNullOrEmpty(line1)) return;
            if (string.IsNullOrEmpty(line2))
            {
                DrawInZone(graphics, width, line1, zoneTop, zoneHeight, color, maxPt);
                return;
            }
            for (var size = maxPt; size >= 8; size--)
            {
                using (var font = new Font("Arial", size, FontStyle.Bold))
                using (var brush = new SolidBrush(color))
                {
                    var m1 = graphics.MeasureString(line1, font);
                    var m2 = graphics.MeasureString(line2, font);
                    if (m1.Width > width * 0.9f) continue;
                    if (m2.Width > width * 0.9f) continue;
                    var totalHeight = m1.Height + m2.Height;
                    if (totalHeight > zoneHeight) continue;
                    var blockTop = zoneTop + (zoneHeight - totalHeight) / 2f;
                    graphics.DrawString(line1, font, brush, (width - m1.Width) / 2f, blockTop);
                    graphics.DrawString(line2, font, brush, (width - m2.Width) / 2f, blockTop + m1.Height);
                    return;
                }
            }
        }

        private void ExecuteFunction(string function)
        {
            switch (function)
            {
                case "initializeRoute":
                    NeutronPlotRoute.RouteInitialize();
                    break;
                case "previousSystem":
                    NeutronPlotRoute.RoutePrevious();
                    break;
                case "nextSystem":
                    NeutronPlotRoute.RouteNext();
                    break;
                case "copyCurrent":
                    NeutronPlotRoute.RouteSelect();
                    break;
                case "autoPlot":
                    // No FSD target (or fetch already running) → flash the alert icon, leave route intact.
                    if (!NeutronPlotRoute.StartSpanshPlot())
                        Connection.ShowAlert().Wait();
                    break;
                case "clearRoute":
                    NeutronPlotRoute.CsvClear();
                    break;
            }
        }
    }
}
