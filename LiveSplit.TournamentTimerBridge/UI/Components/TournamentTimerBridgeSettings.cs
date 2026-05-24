using System;
using System.Drawing;
using System.Windows.Forms;
using System.Xml;

namespace LiveSplit.TournamentTimerBridge
{
    public sealed class TournamentTimerBridgeSettings : UserControl
    {
        private readonly CheckBox _enabledCheckBox;
        private readonly TextBox _endpointTextBox;
        private readonly Label _statusLabel;

        public TournamentTimerBridgeSettings()
        {
            EnabledBridge = true;
            EndpointUrl = "http://127.0.0.1:52991/api/local/livesplit/events";

            AutoSize = true;
            Dock = DockStyle.Fill;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 2,
                RowCount = 3,
                Padding = new Padding(8)
            };

            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            _enabledCheckBox = new CheckBox
            {
                Text = "Enabled",
                Checked = EnabledBridge,
                AutoSize = true,
                Anchor = AnchorStyles.Left
            };

            _enabledCheckBox.CheckedChanged += (sender, args) =>
            {
                EnabledBridge = _enabledCheckBox.Checked;
            };

            _endpointTextBox = new TextBox
            {
                Text = EndpointUrl,
                Width = 420,
                Anchor = AnchorStyles.Left | AnchorStyles.Right
            };

            _endpointTextBox.TextChanged += (sender, args) =>
            {
                EndpointUrl = _endpointTextBox.Text.Trim();
            };

            _statusLabel = new Label
            {
                Text = "Runner UI must be running and connected. Reset is diagnostic only.",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Anchor = AnchorStyles.Left
            };

            layout.Controls.Add(new Label
            {
                Text = "Bridge:",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);

            layout.Controls.Add(_enabledCheckBox, 1, 0);

            layout.Controls.Add(new Label
            {
                Text = "Endpoint:",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 1);

            layout.Controls.Add(_endpointTextBox, 1, 1);
            layout.Controls.Add(_statusLabel, 1, 2);

            Controls.Add(layout);
        }

        public bool EnabledBridge { get; private set; }

        public string EndpointUrl { get; private set; }

        public XmlNode GetSettings(XmlDocument document)
        {
            var settings = document.CreateElement("Settings");
            AppendElement(document, settings, "Enabled", EnabledBridge.ToString());
            AppendElement(document, settings, "EndpointUrl", EndpointUrl);
            return settings;
        }

        public void SetSettings(XmlNode settings)
        {
            if (settings == null)
            {
                return;
            }

            EnabledBridge = ReadBool(settings, "Enabled", true);
            EndpointUrl = ReadString(settings, "EndpointUrl", "http://127.0.0.1:52991/api/local/livesplit/events");

            _enabledCheckBox.Checked = EnabledBridge;
            _endpointTextBox.Text = EndpointUrl;
        }

        public int GetSettingsHashCode()
        {
            unchecked
            {
                var hash = 17;
                hash = hash * 31 + EnabledBridge.GetHashCode();
                hash = hash * 31 + (EndpointUrl == null ? 0 : EndpointUrl.GetHashCode());
                return hash;
            }
        }

        private static void AppendElement(XmlDocument document, XmlNode parent, string name, string value)
        {
            var element = document.CreateElement(name);
            element.InnerText = value ?? "";
            parent.AppendChild(element);
        }

        private static string ReadString(XmlNode settings, string name, string fallback)
        {
            var node = settings.SelectSingleNode(name);
            return node == null || string.IsNullOrWhiteSpace(node.InnerText)
                ? fallback
                : node.InnerText.Trim();
        }

        private static bool ReadBool(XmlNode settings, string name, bool fallback)
        {
            var value = ReadString(settings, name, fallback.ToString());
            bool parsed;
            return bool.TryParse(value, out parsed) ? parsed : fallback;
        }
    }
}
