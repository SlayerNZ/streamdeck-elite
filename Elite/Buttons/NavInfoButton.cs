using System;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BarRaider.SdTools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Elite.Buttons
{
    [PluginActionId("com.mhwlng.elite.navinfo")]
    public class NavInfoButton : EliteKeypadBase
    {
        protected class PluginSettings
        {
            public static PluginSettings CreateDefaultSettings() => new PluginSettings
            {
                InfoUpper      = string.Empty,
                InfoUpperColor = "#ffffff",
                InfoLower      = string.Empty,
                InfoLowerColor = "#ffffff",
                InfoBoostColor = "#00ff00"
            };

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

        private const int LabelFontPt   = 18;
        private const int LabelValueGap = 4;
        private const int RowHeight     = 128;
        private const int Row1Top       = 0;
        private const int Row2Top       = 128;

        private readonly SemaphoreSlim displayLock = new SemaphoreSlim(1, 1);
        private PluginSettings settings;

        public NavInfoButton(SDConnection connection, InitialPayload payload) : base(connection, payload)
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

        public override void KeyPressed(KeyPayload payload) { }
        public override void KeyReleased(KeyPayload payload) { }

        public override async void OnTick()
        {
            base.OnTick();
            await HandleDisplay();
        }

        public override void ReceivedSettings(ReceivedSettingsPayload payload)
        {
            BarRaider.SdTools.Tools.AutoPopulateSettings(settings, payload.Settings);
            AsyncHelper.RunSync(HandleDisplay);
        }

        public override void Dispose()
        {
            displayLock?.Dispose();
            base.Dispose();
        }

        private async Task HandleDisplay()
        {
            if (!await displayLock.WaitAsync(0)) return;
            try
            {
                var snapshot = NavRouteService.GetSnapshot();

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
                Logger.Instance.LogMessage(TracingLevel.ERROR, "NavInfoButton HandleDisplay: " + ex);
            }
            finally
            {
                displayLock.Release();
            }
        }

        private static Bitmap CreateDefaultBitmap()
        {
            var bitmap = new Bitmap(256, 256);
            using (var g = Graphics.FromImage(bitmap))
                g.Clear(Color.Black);
            return bitmap;
        }

        private void DrawInfoZones(Graphics graphics, int width, NavRouteSnapshot snapshot)
        {
            DrawLabeledRow(graphics, width, settings.InfoUpper,
                GetDisplayValue(settings.InfoUpper, snapshot),
                ResolveColor(settings.InfoUpper, settings.InfoUpperColor),
                Row1Top);

            DrawLabeledRow(graphics, width, settings.InfoLower,
                GetDisplayValue(settings.InfoLower, snapshot),
                ResolveColor(settings.InfoLower, settings.InfoLowerColor),
                Row2Top);
        }

        private static void DrawLabeledRow(Graphics graphics, int width, string infoType, string value, Color valueColor, int rowTop)
        {
            var label    = GetAutoLabel(infoType);
            var isSystem = IsSystemNameType(infoType);

            string line1 = value, line2 = string.Empty;
            if (isSystem && !string.IsNullOrEmpty(value))
                (line1, line2) = SplitSystemName(value);

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

            var totalH   = labelH + gapH + valueBlockH;
            var blockTop = rowTop + (RowHeight - totalH) / 2f;

            if (labelFont != null)
            {
                using (labelFont)
                using (var brush = new SolidBrush(Color.White))
                    graphics.DrawString(label, labelFont, brush,
                        (width - labelMeasured.Width) / 2f, blockTop);
            }

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
            infoType == "currentSystem"     ||
            infoType == "targetSystem"      ||
            infoType == "destinationSystem";

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
                case "jumpRange":           return "RANGE";
                case "fuelMain":            return "FUEL";
                case "currentSystem":       return "CURRENT";
                case "jumpSummary":         return "JUMPS";
                case "targetSystem":        return "TARGET";
                case "destinationSystem":   return "DEST SYS";
                case "hopDistance":         return "TGT DIST";
                case "destinationDistance": return "DEST DIST";
                case "distanceTravelled":   return "TRIP DIST";
                case "tripPercentage":      return "PROGRESS";
                case "fuelStar":            return "FUEL STOP";
                case "neutronStar":         return "NEUTRON";
                case "jumpsToFuelStar":     return "FUEL IN";
                case "estJumpsInTank":      return "JUMP CAP";
                case "nextJumpStar":        return "NEXT STAR";
                default:                    return string.Empty;
            }
        }

        private static string GetDisplayValue(string infoType, NavRouteSnapshot snapshot)
        {
            switch (infoType)
            {
                case "jumpRange":
                    return FormatJumpRange();

                case "fuelMain":
                    return $"{EliteData.StatusData.Fuel.FuelMain:0.0}t";

                case "nextJumpStar":
                    return EliteData.FormatNextJumpStar();

                case "currentSystem":
                    return snapshot.SystemCurrent;

                case "jumpSummary":
                    return snapshot.IsLoaded ? snapshot.JumpSummary : string.Empty;

                case "targetSystem":
                    return snapshot.IsLoaded ? snapshot.SystemTarget : string.Empty;

                case "destinationSystem":
                    return snapshot.IsLoaded ? snapshot.SystemDestination : string.Empty;

                case "hopDistance":
                    return snapshot.IsLoaded ? $"{snapshot.HopDistance:#,##0.0} LY" : string.Empty;

                case "destinationDistance":
                    return snapshot.IsLoaded ? $"{snapshot.DistanceDestination:#,##0.0} LY" : string.Empty;

                case "distanceTravelled":
                    return snapshot.IsLoaded ? $"{snapshot.DistanceTravelled:#,##0.0} LY" : string.Empty;

                case "tripPercentage":
                    return snapshot.IsLoaded ? $"{snapshot.TripPercent:F1}%" : string.Empty;

                case "fuelStar":
                    return snapshot.IsLoaded ? (snapshot.IsFuelStar ? "YES" : "NO") : string.Empty;

                case "neutronStar":
                    return snapshot.IsLoaded ? (snapshot.IsNeutronStar ? "YES" : "NO") : string.Empty;

                case "jumpsToFuelStar":
                    return snapshot.IsLoaded && snapshot.JumpsToNextFuelStar >= 0
                        ? snapshot.JumpsToNextFuelStar.ToString()
                        : string.Empty;

                case "estJumpsInTank":
                    return snapshot.IsLoaded && snapshot.EstJumpsInTank >= 0
                        ? snapshot.EstJumpsInTank.ToString()
                        : string.Empty;

                default:
                    return string.Empty;
            }
        }

        private static string FormatJumpRange()
        {
            // Shared implementation - this used to be a private copy of the formula, which drifted
            // from EliteData's when the SCO Mk II fix landed and showed 81.5 against the 3-row
            // button's 81.4 for the same ship.
            var range = EliteData.GetJumpRange(true);

            return $"{range:0.0} LY{(EliteData.IsFsdBoosted ? " ⚡" : "")}";
        }

        private Color ResolveColor(string infoType, string hex)
        {
            if (infoType == "jumpRange" && EliteData.IsFsdBoosted)
                return ParseColor(settings.InfoBoostColor, Color.FromArgb(0, 255, 0));
            return ParseColor(hex, Color.White);
        }

        private static Color ParseColor(string hex, Color fallback)
        {
            try   { return (Color)new ColorConverter().ConvertFromString(hex); }
            catch { return fallback; }
        }
    }
}
