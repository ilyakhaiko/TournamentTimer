using System;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using LiveSplit.Model;
using LiveSplit.UI;
using LiveSplit.UI.Components;

namespace LiveSplit.TournamentTimerBridge
{
    public sealed class TournamentTimerBridgeComponent : LogicComponent
    {
        private readonly TournamentTimerBridgeSettings _settings;
        private readonly LiveSplitState _state;
        private readonly object _sendLock = new object();

        private string _sessionId = CreateSessionId();
        private int _eventCounter;
        private int _lastSentSplitIndex = -1;
        private bool _inputLocked;
        private string _inputLockReason;
        private bool _disposed;

        public TournamentTimerBridgeComponent(LiveSplitState state)
        {
            _state = state;
            _settings = new TournamentTimerBridgeSettings();

            _state.OnStart += StateOnStart;
            _state.OnSplit += StateOnSplit;
            _state.OnReset += StateOnReset;
        }

        public override string ComponentName
        {
            get { return "TournamentTimer Bridge"; }
        }

        public override Control GetSettingsControl(LayoutMode mode)
        {
            return _settings;
        }

        public override XmlNode GetSettings(XmlDocument document)
        {
            return _settings.GetSettings(document);
        }

        public override void SetSettings(XmlNode settings)
        {
            _settings.SetSettings(settings);
        }

        public int GetSettingsHashCode()
        {
            return _settings.GetSettingsHashCode();
        }

        public override void Update(IInvalidator invalidator, LiveSplitState state, float width, float height, LayoutMode mode)
        {
            // Event-driven component. Nothing to redraw.
        }

        public override void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _state.OnStart -= StateOnStart;
            _state.OnSplit -= StateOnSplit;
            _state.OnReset -= StateOnReset;
        }

        private void StateOnStart(object sender, EventArgs e)
        {
            lock (_sendLock)
            {
                // A LiveSplit start means a new local LiveSplit session.
                // If the previous session was locked due to desync, allow this new session to try again.
                _sessionId = CreateSessionId();
                _eventCounter = 0;
                _lastSentSplitIndex = -1;
                _inputLocked = false;
                _inputLockReason = null;
            }

            SendBridgeEvent(
                eventType: "start",
                splitIndex: null,
                splitName: null,
                liveSplitRealTimeMs: 0,
                liveSplitGameTimeMs: 0,
                timerPhase: _state.CurrentPhase.ToString());
        }

        private void StateOnSplit(object sender, EventArgs e)
        {
            if (IsInputLocked())
            {
                return;
            }

            // TimerModel increments CurrentSplitIndex before raising OnSplit,
            // so the completed split is CurrentSplitIndex - 1.
            var completedSplitIndex = _state.CurrentSplitIndex - 1;

            if (completedSplitIndex < 0)
            {
                return;
            }

            if (_state.Run != null && completedSplitIndex >= _state.Run.Count)
            {
                completedSplitIndex = _state.Run.Count - 1;
            }

            if (completedSplitIndex < 0)
            {
                return;
            }

            if (completedSplitIndex == _lastSentSplitIndex)
            {
                return;
            }

            _lastSentSplitIndex = completedSplitIndex;

            var splitName = _state.Run != null && completedSplitIndex < _state.Run.Count
                ? _state.Run[completedSplitIndex].Name
                : null;

            SendBridgeEvent(
                eventType: "split",
                splitIndex: completedSplitIndex,
                splitName: splitName,
                liveSplitRealTimeMs: GetLiveSplitRealTimeMs(_state),
                liveSplitGameTimeMs: GetLiveSplitGameTimeMs(_state),
                timerPhase: _state.CurrentPhase.ToString());
        }

        private void StateOnReset(object sender, TimerPhase oldPhase)
        {
            var wasLocked = false;

            lock (_sendLock)
            {
                wasLocked = _inputLocked;

                // Reset ends the current local LiveSplit session.
                // If this session was locked, reset unlocks the plugin for the next Start.
                _inputLocked = false;
                _inputLockReason = null;
                _lastSentSplitIndex = -1;
            }

            if (wasLocked)
            {
                // Do not send reset after a desync lock.
                // The Runner UI/server already switched to admin control for that attempt.
                return;
            }

            SendBridgeEvent(
                eventType: "reset",
                splitIndex: null,
                splitName: null,
                liveSplitRealTimeMs: GetLiveSplitRealTimeMs(_state),
                liveSplitGameTimeMs: GetLiveSplitGameTimeMs(_state),
                timerPhase: oldPhase.ToString());
        }

        private void SendBridgeEvent(
            string eventType,
            int? splitIndex,
            string splitName,
            long? liveSplitRealTimeMs,
            long? liveSplitGameTimeMs,
            string timerPhase)
        {
            if (!_settings.EnabledBridge)
            {
                return;
            }

            var endpointUrl = _settings.EndpointUrl;

            if (string.IsNullOrWhiteSpace(endpointUrl))
            {
                return;
            }

            string sourceEventId;

            lock (_sendLock)
            {
                if (_inputLocked)
                {
                    return;
                }

                _eventCounter++;
                sourceEventId = string.Format("livesplit:{0}:{1:D6}:{2}", _sessionId, _eventCounter, eventType);
            }

            var json = BuildJson(
                eventType,
                sourceEventId,
                splitIndex,
                splitName,
                liveSplitRealTimeMs,
                liveSplitGameTimeMs,
                timerPhase);

            Task.Run(() =>
            {
                var responseJson = PostJson(endpointUrl, json);

                if (ShouldLockInput(responseJson))
                {
                    LockInput(ExtractBridgeMessage(responseJson));
                }
            });
        }

        private bool IsInputLocked()
        {
            lock (_sendLock)
            {
                return _inputLocked;
            }
        }

        private void LockInput(string reason)
        {
            lock (_sendLock)
            {
                _inputLocked = true;
                _inputLockReason = string.IsNullOrWhiteSpace(reason)
                    ? "runner_ui_rejected_event"
                    : reason;
            }
        }

        private static bool ShouldLockInput(string responseJson)
        {
            if (string.IsNullOrWhiteSpace(responseJson))
            {
                return false;
            }

            var rejected = Regex.IsMatch(
                responseJson,
                "\"accepted\"\\s*:\\s*false",
                RegexOptions.IgnoreCase);

            if (!rejected)
            {
                return false;
            }

            var message = ExtractBridgeMessage(responseJson);

            // These are temporary states, not LiveSplit desync.
            if (string.Equals(message, "runner_not_connected", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(message, "runner_action_in_progress", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private static string ExtractBridgeMessage(string responseJson)
        {
            if (string.IsNullOrWhiteSpace(responseJson))
            {
                return "";
            }

            var match = Regex.Match(
                responseJson,
                "\"message\"\\s*:\\s*\"(?<message>(?:\\\\.|[^\"])*)\"",
                RegexOptions.IgnoreCase);

            if (!match.Success)
            {
                return "";
            }

            return UnescapeJsonString(match.Groups["message"].Value);
        }

        private static string BuildJson(
            string eventType,
            string sourceEventId,
            int? splitIndex,
            string splitName,
            long? liveSplitRealTimeMs,
            long? liveSplitGameTimeMs,
            string timerPhase)
        {
            var officialLiveSplitTimeMs = liveSplitGameTimeMs ?? liveSplitRealTimeMs ?? 0;
            var sb = new StringBuilder();

            sb.Append("{");
            AppendJsonProperty(sb, "protocolVersion", "1", quoteValue: false);
            sb.Append(",");
            AppendJsonProperty(sb, "source", "livesplit", quoteValue: true);
            sb.Append(",");
            AppendJsonProperty(sb, "eventType", eventType, quoteValue: true);
            sb.Append(",");
            AppendJsonProperty(sb, "sourceEventId", sourceEventId, quoteValue: true);
            sb.Append(",");
            AppendJsonProperty(sb, "splitIndex", splitIndex.HasValue ? splitIndex.Value.ToString() : "null", quoteValue: false);
            sb.Append(",");
            AppendJsonProperty(sb, "splitName", splitName, quoteValue: true, allowNull: true);
            sb.Append(",");
            AppendJsonProperty(sb, "liveSplitTimeMs", officialLiveSplitTimeMs.ToString(), quoteValue: false);
            sb.Append(",");
            AppendJsonProperty(sb, "liveSplitRealTimeMs", liveSplitRealTimeMs.HasValue ? liveSplitRealTimeMs.Value.ToString() : "null", quoteValue: false);
            sb.Append(",");
            AppendJsonProperty(sb, "liveSplitGameTimeMs", liveSplitGameTimeMs.HasValue ? liveSplitGameTimeMs.Value.ToString() : "null", quoteValue: false);
            sb.Append(",");
            AppendJsonProperty(sb, "timerPhase", timerPhase, quoteValue: true);
            sb.Append(",");
            AppendJsonProperty(sb, "occurredAtUtc", DateTimeOffset.UtcNow.ToString("o"), quoteValue: true);
            sb.Append("}");

            return sb.ToString();
        }

        private static void AppendJsonProperty(
            StringBuilder sb,
            string name,
            string value,
            bool quoteValue,
            bool allowNull = false)
        {
            sb.Append("\"");
            sb.Append(EscapeJson(name));
            sb.Append("\":");

            if (allowNull && value == null)
            {
                sb.Append("null");
                return;
            }

            if (quoteValue)
            {
                sb.Append("\"");
                sb.Append(EscapeJson(value ?? ""));
                sb.Append("\"");
                return;
            }

            sb.Append(value ?? "null");
        }

        private static string PostJson(string endpointUrl, string json)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(endpointUrl);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Timeout = 1000;
                request.ReadWriteTimeout = 1000;

                var bytes = Encoding.UTF8.GetBytes(json);
                request.ContentLength = bytes.Length;

                using (var requestStream = request.GetRequestStream())
                {
                    requestStream.Write(bytes, 0, bytes.Length);
                }

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var responseStream = response.GetResponseStream())
                {
                    if (responseStream == null)
                    {
                        return "";
                    }

                    using (var reader = new System.IO.StreamReader(responseStream, Encoding.UTF8))
                    {
                        return reader.ReadToEnd();
                    }
                }
            }
            catch (WebException ex)
            {
                // Transport/server errors should not break LiveSplit and should not lock the plugin.
                try
                {
                    using (var response = ex.Response)
                    using (var responseStream = response == null ? null : response.GetResponseStream())
                    {
                        if (responseStream == null)
                        {
                            return "";
                        }

                        using (var reader = new System.IO.StreamReader(responseStream, Encoding.UTF8))
                        {
                            return reader.ReadToEnd();
                        }
                    }
                }
                catch
                {
                    return "";
                }
            }
            catch
            {
                // Do not break LiveSplit if TournamentTimer Runner UI is closed.
                return "";
            }
        }

        private static long? GetLiveSplitRealTimeMs(LiveSplitState state)
        {
            var realTime = state.CurrentTime.RealTime;

            if (!realTime.HasValue)
            {
                return null;
            }

            return Math.Max(0, (long)realTime.Value.TotalMilliseconds);
        }

        private static long? GetLiveSplitGameTimeMs(LiveSplitState state)
        {
            var gameTime = state.CurrentTime.GameTime;

            if (!gameTime.HasValue)
            {
                return null;
            }

            return Math.Max(0, (long)gameTime.Value.TotalMilliseconds);
        }

        private static string EscapeJson(string value)
        {
            if (value == null)
            {
                return "";
            }

            var sb = new StringBuilder(value.Length + 8);

            foreach (var ch in value)
            {
                switch (ch)
                {
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\f':
                        sb.Append("\\f");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (ch < 32)
                        {
                            sb.Append("\\u");
                            sb.Append(((int)ch).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(ch);
                        }

                        break;
                }
            }

            return sb.ToString();
        }

        private static string UnescapeJsonString(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            return value
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\")
                .Replace("\\/", "/")
                .Replace("\\b", "\b")
                .Replace("\\f", "\f")
                .Replace("\\n", "\n")
                .Replace("\\r", "\r")
                .Replace("\\t", "\t");
        }

        private static string CreateSessionId()
        {
            return DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }
    }
}
