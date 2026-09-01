using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using BarRaider.SdTools;
using EliteJournalReader;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Elite.Buttons
{
    [PluginActionId("com.mhwlng.elite.latlonginfo")]
    public class LatLongInfo : EliteKeypadBase
    {
        protected class PluginSettings
        {
            public static PluginSettings CreateDefaultSettings()
            {
                return new PluginSettings
                {
                    PrimaryImageFilename = string.Empty,
                    DefaultImageFilename = string.Empty,
                    LatColor = "#00ff00",
                    LongColor = "#00aaff",
                    LatVerticalPosition = "28",
                    LongVerticalPosition = "128",
                    TextBold = "true"
                };
            }

            [FilenameProperty]
            [JsonProperty(PropertyName = "primaryImage")]
            public string PrimaryImageFilename { get; set; }

            [FilenameProperty]
            [JsonProperty(PropertyName = "defaultImage")]
            public string DefaultImageFilename { get; set; }

            [JsonProperty(PropertyName = "latColor")]
            public string LatColor { get; set; }

            [JsonProperty(PropertyName = "longColor")]
            public string LongColor { get; set; }

            [JsonProperty(PropertyName = "latVerticalPosition")]
            public string LatVerticalPosition { get; set; }

            [JsonProperty(PropertyName = "longVerticalPosition")]
            public string LongVerticalPosition { get; set; }

            [JsonProperty(PropertyName = "textBold")]
            public string TextBold { get; set; }
        }

        private PluginSettings settings;
        private Bitmap _primaryImage = null;
        private Bitmap _defaultImage = null;
        private string _primaryFile;
        private string _defaultFile;
        // Fixed label size at a 256px canvas, scaled proportionally for other sizes.
        // Same value as NavInfoButton.LabelFontPt so the buttons look consistent.
        private const int LabelFontPt = 18;

        private SolidBrush _latBrush = new SolidBrush(Color.Lime);
        private SolidBrush _longBrush = new SolidBrush(Color.FromArgb(0, 170, 255));

        private void DrawLabelAndValue(Graphics graphics, string label, string value, SolidBrush brush, double verticalPosition, int width)
        {
            if (string.IsNullOrEmpty(value)) return;

            var isBold = settings.TextBold == "true";
            var fontStyle = isBold ? FontStyle.Bold : FontStyle.Regular;

            var scale = width / 256.0;

            // Keep the stacked label+value pair inside its row so the two groups cannot collide.
            float maxBlockHeight = width * 0.36f;

            // Label is a fixed small size so LAT and LONG always match each other; only the value
            // auto-scales into the height left over. Mirrors NavInfoButton's LabelFontPt pattern.
            var labelPt = (int)(LabelFontPt * scale);
            if (labelPt < 8) labelPt = 8;

            using (var labelFont = new Font("Arial", labelPt, fontStyle))
            {
                var lsf = new StringFormat(StringFormat.GenericTypographic);
                lsf.SetMeasurableCharacterRanges(new[] { new CharacterRange(0, label.Length) });
                var lb = graphics.MeasureCharacterRanges(label, labelFont, new RectangleF(0, 0, 1000, 1000), lsf)[0].GetBounds(graphics);

                float labelBlock = lb.Height * 1.1f;
                float valueMaxHeight = maxBlockHeight - labelBlock;

                var startSize = (int)(72 * scale);
                if (startSize < 8) startSize = 8;

                for (int adjustedSize = startSize; adjustedSize >= 8; adjustedSize -= 1)
                {
                    using (var valueFont = new Font("Arial", adjustedSize, fontStyle))
                    {
                        var vsf = new StringFormat(StringFormat.GenericTypographic);
                        vsf.SetMeasurableCharacterRanges(new[] { new CharacterRange(0, value.Length) });
                        var vb = graphics.MeasureCharacterRanges(value, valueFont, new RectangleF(0, 0, 1000, 1000), vsf)[0].GetBounds(graphics);

                        if (vb.Width > width * 0.95f || vb.Height > valueMaxHeight) continue;

                        var drawFmt = new StringFormat(StringFormat.GenericTypographic);
                        float currentY = (float)(verticalPosition * scale);

                        graphics.DrawString(label, labelFont, brush, (width - lb.Width) / 2.0f, currentY - lb.Y, drawFmt);
                        currentY += labelBlock;
                        graphics.DrawString(value, valueFont, brush, (width - vb.Width) / 2.0f, currentY - vb.Y, drawFmt);
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// Draws the normal layout with placeholder values on a synthesised black canvas, for when
        /// there is no lat/long and the user has supplied no Not Active image. Showing dashes makes
        /// it obvious the button is alive but has no data, which a frozen last reading does not.
        /// </summary>
        private async Task DrawPlaceholderAsync()
        {
            try
            {
                using (var bitmap = new Bitmap(256, 256))
                {
                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.Clear(Color.Black);

                        var width = bitmap.Width;
                        var latPos = double.TryParse(settings.LatVerticalPosition, out double lp) ? lp : 28.0;
                        var longPos = double.TryParse(settings.LongVerticalPosition, out double lop) ? lop : 128.0;

                        DrawLabelAndValue(graphics, "LAT", "--", _latBrush, latPos, width);
                        DrawLabelAndValue(graphics, "LONG", "--", _longBrush, longPos, width);
                    }

                    await Connection.SetImageAsync(BarRaider.SdTools.Tools.ImageToBase64(bitmap, true));
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.LogMessage(TracingLevel.FATAL, "LatLongInfo DrawPlaceholderAsync " + ex);
            }
        }

        private async Task HandleDisplay()
        {
            var s = EliteData.StatusData;

            if (!s.HasLatLong)
            {
                if (!string.IsNullOrEmpty(_defaultFile))
                {
                    await Connection.SetImageAsync(_defaultFile);
                    return;
                }

                // No Not Active image configured. Returning here would leave the last live reading
                // frozen on the button, which reads as current data long after it stopped being
                // true, so draw placeholders instead.
                await DrawPlaceholderAsync();
                return;
            }

            var myBitmap = _primaryImage ?? _defaultImage;
            var imgBase64 = _primaryFile ?? _defaultFile;

            var latText = $"{s.Latitude:F4}°";
            var longText = $"{s.Longitude:F4}°";

            try
            {
                using (var bitmap = myBitmap != null ? new Bitmap(myBitmap) : new Bitmap(256, 256))
                {
                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        // No background image configured: draw onto solid black rather than bailing out
                        if (myBitmap == null)
                            graphics.Clear(Color.Black);

                        var width = bitmap.Width;
                        var latPos = double.TryParse(settings.LatVerticalPosition, out double lp) ? lp : 28.0;
                        var longPos = double.TryParse(settings.LongVerticalPosition, out double lop) ? lop : 128.0;

                        DrawLabelAndValue(graphics, "LAT", latText, _latBrush, latPos, width);
                        DrawLabelAndValue(graphics, "LONG", longText, _longBrush, longPos, width);
                    }

                    imgBase64 = BarRaider.SdTools.Tools.ImageToBase64(bitmap, true);
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.LogMessage(TracingLevel.FATAL, "LatLongInfo HandleDisplay " + ex);
            }

            await Connection.SetImageAsync(imgBase64);
        }

        public LatLongInfo(SDConnection connection, InitialPayload payload) : base(connection, payload)
        {
            if (payload.Settings == null || payload.Settings.Count == 0)
            {
                settings = PluginSettings.CreateDefaultSettings();
                Connection.SetSettingsAsync(JObject.FromObject(settings)).Wait();
            }
            else
            {
                settings = payload.Settings.ToObject<PluginSettings>();
                InitializeSettings();
                AsyncHelper.RunSync(HandleDisplay);
            }

            Program.JournalWatcher.MessageReceived += HandleEliteEvents;
        }

        public void HandleEliteEvents(object sender, MessageReceivedEventArgs args)
        {
            AsyncHelper.RunSync(HandleDisplay);
        }

        public override void KeyPressed(KeyPayload payload) { }
        public override void KeyReleased(KeyPayload payload) { }

        public override void Dispose()
        {
            base.Dispose();
            Program.JournalWatcher.MessageReceived -= HandleEliteEvents;
        }

        public override async void OnTick()
        {
            base.OnTick();
            await HandleDisplay();
        }

        public override void ReceivedSettings(ReceivedSettingsPayload payload)
        {
            BarRaider.SdTools.Tools.AutoPopulateSettings(settings, payload.Settings);
            InitializeSettings();
            AsyncHelper.RunSync(HandleDisplay);
        }

        private void InitializeSettings()
        {
            if (string.IsNullOrEmpty(settings.LatColor)) settings.LatColor = "#00ff00";
            if (string.IsNullOrEmpty(settings.LongColor)) settings.LongColor = "#00aaff";
            if (string.IsNullOrEmpty(settings.LatVerticalPosition)) settings.LatVerticalPosition = "28";
            if (string.IsNullOrEmpty(settings.LongVerticalPosition)) settings.LongVerticalPosition = "128";
            if (string.IsNullOrEmpty(settings.TextBold)) settings.TextBold = "true";

            try
            {
                var converter = new ColorConverter();
                _latBrush = new SolidBrush((Color)converter.ConvertFromString(settings.LatColor));
                _longBrush = new SolidBrush((Color)converter.ConvertFromString(settings.LongColor));

                if (_primaryImage != null) { _primaryImage.Dispose(); _primaryImage = null; _primaryFile = null; }
                if (_defaultImage != null) { _defaultImage.Dispose(); _defaultImage = null; _defaultFile = null; }

                if (File.Exists(settings.PrimaryImageFilename))
                {
                    _primaryImage = (Bitmap)Image.FromFile(settings.PrimaryImageFilename);
                    _primaryFile = Tools.FileToBase64(settings.PrimaryImageFilename, true);
                }

                if (File.Exists(settings.DefaultImageFilename))
                {
                    _defaultImage = (Bitmap)Image.FromFile(settings.DefaultImageFilename);
                    _defaultFile = Tools.FileToBase64(settings.DefaultImageFilename, true);
                }
                else
                {
                    _defaultImage = _primaryImage;
                    _defaultFile = _primaryFile;
                }

                if (_primaryImage == null)
                {
                    _primaryImage = _defaultImage;
                    _primaryFile = _defaultFile;
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.LogMessage(TracingLevel.FATAL, "LatLongInfo InitializeSettings " + ex);
            }

            Connection.SetSettingsAsync(JObject.FromObject(settings)).Wait();
        }
    }
}