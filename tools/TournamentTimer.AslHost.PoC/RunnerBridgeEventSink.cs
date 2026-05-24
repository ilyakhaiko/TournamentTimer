using System;
using System.IO;
using System.Net;
using System.Text;

internal sealed class RunnerBridgeEventSink
{
    private readonly string _endpointUrl;
    private bool _bridgeUnavailableHintPrinted;

    public RunnerBridgeEventSink(string endpointUrl)
    {
        if (string.IsNullOrWhiteSpace(endpointUrl))
        {
            throw new ArgumentException("Bridge endpoint URL is required.", nameof(endpointUrl));
        }

        _endpointUrl = endpointUrl;
    }

    public void SendStart()
    {
        Send("start", null);
    }

    public void SendSplit(int splitIndex, string splitName)
    {
        // Runner bridge validates SplitIndex. SplitName is intentionally not sent yet.
        Send("split", splitIndex);
    }

    public void SendReset()
    {
        Send("reset", null);
    }

    private void Send(string eventType, int? splitIndex)
    {
        try
        {
            ServicePointManager.Expect100Continue = false;

            var json = BuildJson(eventType, splitIndex);
            var bytes = Encoding.UTF8.GetBytes(json);

            var request = (HttpWebRequest)WebRequest.Create(_endpointUrl);
            request.Method = "POST";
            request.ContentType = "application/json; charset=utf-8";
            request.ContentLength = bytes.Length;
            request.Timeout = 5000;
            request.ReadWriteTimeout = 5000;
            request.KeepAlive = false;
            request.ProtocolVersion = HttpVersion.Version10;
            request.ServicePoint.Expect100Continue = false;

            using (var requestStream = request.GetRequestStream())
            {
                requestStream.Write(bytes, 0, bytes.Length);
            }

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var responseStream = response.GetResponseStream())
            using (var reader = new StreamReader(responseStream ?? Stream.Null))
            {
                var body = reader.ReadToEnd();
                var accepted = ContainsJsonBool(body, "accepted", true);
                var alreadyProcessed = ContainsJsonBool(body, "alreadyProcessed", true);
                var status = ExtractJsonString(body, "status");
                var message = ExtractJsonString(body, "message");

                var details = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(status)) details.Append(" status=").Append(status);
                if (alreadyProcessed) details.Append(" alreadyProcessed=true");
                if (!string.IsNullOrWhiteSpace(message)) details.Append(" message=").Append(message);

                Console.WriteLine($"[bridge] {eventType} -> HTTP {(int)response.StatusCode} accepted {accepted.ToString().ToLowerInvariant()}{details}");

                if (!accepted && !string.IsNullOrWhiteSpace(body))
                {
                    Console.WriteLine("[bridge] response body: " + Truncate(body.Trim(), 500));
                }
            }
        }
        catch (WebException ex)
        {
            var status = ex.Response is HttpWebResponse httpResponse
                ? ((int)httpResponse.StatusCode).ToString()
                : "no_http_response";

            var body = ReadWebExceptionBody(ex);
            Console.WriteLine($"[bridge] {eventType} -> FAILED {status}: {ex.Message}{FormatBody(body)}");

            if (string.Equals(status, "no_http_response", StringComparison.OrdinalIgnoreCase) && !_bridgeUnavailableHintPrinted)
            {
                Console.WriteLine("[bridge] hint: Runner UI local bridge is not available. Start Runner UI, press Connect, then start ASL Host.");
                _bridgeUnavailableHintPrinted = true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[bridge] {eventType} -> FAILED: {ex.Message}");
        }
    }

    private static string BuildJson(string eventType, int? splitIndex)
    {
        var builder = new StringBuilder();
        builder.Append("{");
        builder.Append("\"protocolVersion\":1,");
        builder.Append("\"source\":\"asl-host-poc\",");
        builder.Append("\"eventType\":\"").Append(EscapeJson(eventType)).Append("\",");
        builder.Append("\"sourceEventId\":\"").Append(Guid.NewGuid().ToString("N")).Append("\",");
        builder.Append("\"occurredAtUtc\":\"").Append(DateTimeOffset.UtcNow.ToString("O")).Append("\"");

        if (splitIndex != null)
        {
            builder.Append(",\"splitIndex\":").Append(splitIndex.Value);
        }

        builder.Append("}");
        return builder.ToString();
    }

    private static bool ContainsJsonBool(string json, string propertyName, bool value)
    {
        if (json == null)
        {
            return false;
        }

        var needle = "\"" + propertyName + "\":" + value.ToString().ToLowerInvariant();
        return json.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string ExtractJsonString(string json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "";
        }

        var needle = "\"" + propertyName + "\":";
        var index = json.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return "";
        }

        index += needle.Length;
        while (index < json.Length && char.IsWhiteSpace(json[index]))
        {
            index++;
        }

        if (index >= json.Length || json[index] != '"')
        {
            return "";
        }

        index++;
        var builder = new StringBuilder();
        var escaping = false;

        for (; index < json.Length; index++)
        {
            var ch = json[index];

            if (escaping)
            {
                builder.Append(ch);
                escaping = false;
                continue;
            }

            if (ch == '\\')
            {
                escaping = true;
                continue;
            }

            if (ch == '"')
            {
                break;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static string EscapeJson(string value)
    {
        if (value == null)
        {
            return "";
        }

        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n");
    }

    private static string ReadWebExceptionBody(WebException ex)
    {
        try
        {
            if (ex.Response == null)
            {
                return "";
            }

            using (var stream = ex.Response.GetResponseStream())
            using (var reader = new StreamReader(stream ?? Stream.Null))
            {
                return reader.ReadToEnd();
            }
        }
        catch
        {
            return "";
        }
    }

    private static string FormatBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "";
        }

        return " body=" + Truncate(body.Trim(), 300);
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value ?? "";
        }

        return value.Substring(0, maxLength) + "...";
    }
}
