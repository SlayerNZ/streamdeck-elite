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
    [PluginActionId("com.mhwlng.elite.neutronplot2row")]
    public class NeutronPlotButton2Row : EliteKeypadBase
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

            [JsonProperty(PropertyName = "infoLower")]
            public string InfoLower { get; set; }

            [JsonProperty(PropertyName = "infoLowerColor")]
            public string InfoLowerColor { get; set; }

            [JsonProperty(PropertyName = "infoBoostColor")]
            public string InfoBoostColor { get; set; }
        }

        // 256×256 button split into 2 equal rows of 128px.
        // Each row: label + value centered as a unit within the row.
        private const int LabelFontPt   = 18;
        private const int LabelValueGap = 4;
        private const int RowHeight     = 128;
        private const int Row1Top       = 0;
        private const int Row2Top       = 128;

        private static readonly TimeSpan LongPressThreshold = TimeSpan.FromMilliseconds(2000);

        private readonly SemaphoreSlim displayLock = new SemaphoreSlim(1, 1);
        private PluginSettings settings;
        private DateTime keyPressedAt = DateTime.MinValue;

        public NeutronPlotButton2Row(SDConnection connection, InitialPayload payload) : base(connection, payload)
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
                Logger.Instance.LogMessage(TracingLevel.ERROR, "NeutronPlotButton2Row AutoClearInvalidCsvAsync: " + ex);
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
                Logger.Instance.LogMessage(TracingLevel.ERROR, "NeutronPlotButton2Row HandleDisplay: " + ex);
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
                DrawInZone(graphics, width, "...",      136, 50, Color.Orange, 34);
                return;
            }

            if (!snapshot.IsLoaded && !string.IsNullOrEmpty(snapshot.SpanshError))
            {
                DrawInZone(graphics, width, snapshot.SpanshError, 86, 86, Color.Red, 28);
                return;
            }

            if (!snapshot.IsLoaded)
            {
                DrawInZone(graphics, width, "NO ROUTE", 86, 86, Color.Gray, 24);
                return;
            }

            DrawLabeledRow(graphics, width, settings.InfoUpper,
                GetDisplayValue(settings.InfoUpper, snapshot),
                ResolveColor(settings.InfoUpper, settings.InfoUpperColor, snapshot),
                Row1Top);

            DrawLabeledRow(graphics, width, settings.InfoLower,
                GetDisplayValue(settings.InfoLower, snapshot),
                ResolveColor(settings.InfoLower, settings.InfoLowerColor, snapshot),
                Row2Top);
        }

        // Draws one row: label + value centered as a unit within the 128px row.
        // Two-pass: find the best value font size, measure both, then center the block.
        private static void DrawLabeledRow(Graphics graphics, int width, string infoType, string value, Color valueColor, int rowTop)
        {
            var label    = GetAutoLabel(infoType);
            var isSystem = IsSystemNameType(infoType);

            string line1 = value, line2 = string.Empty;
            if (isSystem && !string.IsNullOrEmpty(value))
                (line1, line2) = SplitSystemName(value);

            // Measure label
            float labelH = 0f;
            SizeF labelMeasured = SizeF.Empty;
            Font labelFont = null;
            if (!string.IsNullOrEmpty(label))
            {
                labelFont = new Font("Arial", LabelFontPt, FontStyle.Bold);
                labelMeasured = graphics.MeasureString(label, labelFont);
                labelH = labelMeasured.Height;
            }

            float gapH = labelH > 0 ? LabelValueGap : 0f;
            float availForValue = RowHeight - labelH - gapH;

            // Find best value font size (first pass — measure only)
            int chosenSize = 0;
            SizeF vm1 = SizeF.Empty, vm2 = SizeF.Empty;
            float valueBlockH = 0f;

            if (!string.IsNullOrEmpty(value))
            {
                for (var size = 42; size >= 8; size--)
                {
                    using (var f = new Font("Arial", size, FontStyle.Bold))
                    {
                        if (isSystem && !string.IsNullOrEmpty(line2))
                        {
                            vm1 = graphics.MeasureString(line1, f);
                            vm2 = graphics.MeasureString(line2, f);
                            if (vm1.Width > width * 0.9f || vm2.Width > width * 0.9f) continue;
                            var total = vm1.Height + vm2.Height;
                            if (total > availForValue) continue;
                            chosenSize = size;
                            valueBlockH = total;
                            break;
                        }
                        else
                        {
                            vm1 = graphics.MeasureString(value, f);
                            if (vm1.Width > width * 0.9f) continue;
                            if (vm1.Height > availForValue) continue;
                            chosenSize = size;
                            valueBlockH = vm1.Height;
                            break;
                        }
                    }
                }
            }

            // Center the label+gap+value block within the row
            var totalH   = labelH + gapH + valueBlockH;
            var blockTop = rowTop + (RowHeight - totalH) / 2f;

            // Draw label
            if (labelFont != null)
            {
                using (labelFont)
                using (var brush = new SolidBrush(Color.White))
                    graphics.DrawString(label, labelFont, brush,
                        (width - labelMeasured.Width) / 2f, blockTop);
            }

            // Draw value (second pass — render only)
            if (chosenSize > 0)
            {
                var valueTop = blockTop + labelH + gapH;
                using (var f = new Font("Arial", chosenSize, FontStyle.Bold))
                using (var brush = new SolidBrush(valueColor))
                {
                    if (isSystem && !string.IsNullOrEmpty(line2))
                    {
                        graphics.DrawString(line1, f, brush, (width - vm1.Width) / 2f, valueTop);
                        graphics.DrawString(line2, f, brush, (width - vm2.Width) / 2f, valueTop + vm1.Height);
                    }
                    else
                    {
                        graphics.DrawString(value, f, brush, (width - vm1.Width) / 2f, valueTop);
                    }
                }
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

        private static string GetAutoLabel(string infoType)
        {
            switch (infoType)
            {
                case "currentSystemName":   return "CURRENT";
                case "targetSystemName":    return "TARGET";
                case "routeStatus":         return "STATUS";
                case "distanceTravelled":   return "TRIP DIST";
                case "distanceTarget":      return "TGT DIST";
                case "jumpsToRejoin":       return "REJOIN";
                case "destinationDistance": return "DEST DIST";
                case "currentJumpNumber":   return "JUMP NBR";
                case "totalJumps":          return "JUMPS TOT";
                case "jumpsRemaining":      return "JUMPS LEFT";
                case "jumpsToRefuel":       return "FUEL IN";
                case "jumpSummary":         return "SUMMARY";
                case "tripPercentage":      return "PROGRESS";
                case "refuelAtTarget":      return "FUEL STOP";
                case "neutronAtTarget":     return "NEUTRON";
                case "jumpRange":           return "RANGE";
                case "fuelMain":            return "FUEL";
                case "nextJumpStar":        return "NEXT STAR";
                default:                    return string.Empty;
            }
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
            // Shared implementation - see EliteData.GetJumpRange. This was a private copy of the
            // formula and drifted from it, showing 81.5 where the 3-row button showed 81.4.
            var range = EliteData.GetJumpRange(true);

            return $"{range:0.00}LY{(EliteData.IsFsdBoosted ? " ⚡" : "")}";
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
