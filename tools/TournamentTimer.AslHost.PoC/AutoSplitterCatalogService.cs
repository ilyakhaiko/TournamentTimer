using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

internal sealed class AutoSplitterCatalogService
{
    public const string DefaultCatalogUrl = "https://raw.githubusercontent.com/LiveSplit/LiveSplit.AutoSplitters/master/LiveSplit.AutoSplitters.xml";

    public IReadOnlyList<AutoSplitterCatalogEntry> LoadCatalog(string catalogUrl)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

        var xmlText = DownloadString(catalogUrl);
        var document = new XmlDocument
        {
            XmlResolver = null
        };
        document.LoadXml(xmlText);

        var nodes = document.SelectNodes("//*[local-name()='AutoSplitter']");
        var result = new List<AutoSplitterCatalogEntry>();

        if (nodes == null)
        {
            return result;
        }

        foreach (XmlNode node in nodes)
        {
            var games = GetDescendantTexts(node, "Game");
            var urls = GetDescendantTexts(node, "URL");

            if (games.Count == 0 || urls.Count == 0)
            {
                continue;
            }

            var type = GetNodeText(node, "Type");
            var description = GetNodeText(node, "Description");
            var classification = AutoSplitterAssetClassification.FromUrls(urls, type);

            result.Add(new AutoSplitterCatalogEntry
            {
                PrimaryGame = games[0],
                Games = games,
                Type = type,
                Description = description,
                Urls = urls,
                Classification = classification
            });
        }

        return result
            .OrderBy(entry => entry.PrimaryGame, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<AutoSplitterCatalogEntry> Search(IReadOnlyList<AutoSplitterCatalogEntry> catalog, string query, int maxResults = 100)
    {
        if (catalog == null || catalog.Count == 0)
        {
            return Array.Empty<AutoSplitterCatalogEntry>();
        }

        query = query ?? "";
        var normalizedQuery = NormalizeText(query);

        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return catalog.Take(maxResults).ToArray();
        }

        return catalog
            .Select(entry => new
            {
                Entry = entry,
                Score = ScoreEntry(entry, normalizedQuery)
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Entry.PrimaryGame, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .Select(item => item.Entry)
            .ToArray();
    }

    public AutoSplitterInstallResult Install(AutoSplitterInstallRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (request.Entry == null)
        {
            throw new InvalidOperationException("No autosplitter selected.");
        }

        if (string.IsNullOrWhiteSpace(request.RunId))
        {
            throw new InvalidOperationException("RunId is required.");
        }

        var entry = request.Entry;
        var classification = entry.Classification ?? AutoSplitterAssetClassification.FromUrls(entry.Urls, entry.Type);
        var destinationDir = Path.Combine(Path.GetFullPath(request.RunsRoot), request.RunId.Trim());
        var messages = new List<string>();

        if (classification.IsWasmLike)
        {
            throw new NotSupportedException("This looks like WASM / AutoSplittingRuntime. Current ASL Host supports ASL scripts only.");
        }

        if (classification.Asl.Count == 0)
        {
            throw new NotSupportedException("No .asl URL found. Current ASL Host supports ASL scripts only.");
        }

        Directory.CreateDirectory(destinationDir);

        if (classification.HasHelperDependencies)
        {
            messages.Add("Compatibility warning: this ASL uses helper/dependency files. Install is best-effort; test this autosplitter before tournament use.");
        }

        var aslUrl = classification.Asl[0];
        var aslPath = Path.Combine(destinationDir, "autosplitter.asl");
        DownloadFile(aslUrl, aslPath);
        messages.Add("Downloaded autosplitter.asl");

        if (classification.Asl.Count > 1)
        {
            var extrasDir = Path.Combine(destinationDir, "extras");
            Directory.CreateDirectory(extrasDir);

            foreach (var extraUrl in classification.Asl.Skip(1))
            {
                var name = GetFileNameFromUrl(extraUrl, "autosplitter-extra.asl");
                DownloadFile(extraUrl, Path.Combine(extrasDir, name));
                messages.Add("Downloaded extra ASL: " + name);
            }
        }

        var componentsDir = Path.Combine(destinationDir, "Components");

        foreach (var url in classification.AslHelp)
        {
            var path = Path.Combine(componentsDir, "asl-help");
            DownloadFile(url, path);
            CopyToHostComponents(request.RunsRoot, path, "asl-help", messages);
            messages.Add("Downloaded Components/asl-help");
        }

        foreach (var url in classification.Uhara)
        {
            var name = GetFileNameFromUrl(url, "uhara");
            var path = Path.Combine(componentsDir, name);
            DownloadFile(url, path);
            CopyToHostComponents(request.RunsRoot, path, name, messages);
            messages.Add("Downloaded Components/" + name);
        }

        foreach (var url in classification.EmuHelp)
        {
            var name = GetFileNameFromUrl(url, "emu-help");
            var path = Path.Combine(componentsDir, name);
            DownloadFile(url, path);
            CopyToHostComponents(request.RunsRoot, path, name, messages);
            messages.Add("Downloaded Components/" + name);
        }

        foreach (var url in classification.Dll)
        {
            var name = GetFileNameFromUrl(url, "component.dll");
            var path = Path.Combine(componentsDir, name);
            DownloadFile(url, path);
            CopyToHostComponents(request.RunsRoot, path, name, messages);
            messages.Add("Downloaded DLL dependency: " + name);
        }

        foreach (var url in classification.Xml)
        {
            var name = GetFileNameFromUrl(url, "settings.xml");
            DownloadFile(url, Path.Combine(destinationDir, name));
            messages.Add("Downloaded XML sidecar: " + name);
        }

        var splitsPath = Path.Combine(destinationDir, "splits.lss");

        if (!string.IsNullOrWhiteSpace(request.SplitsPath))
        {
            File.Copy(Path.GetFullPath(request.SplitsPath), splitsPath, overwrite: true);
            messages.Add("Copied splits.lss from selected file.");
        }
        else if (!File.Exists(splitsPath))
        {
            NewPlaceholderSplits(splitsPath, request.PlaceholderSplitCount, entry.PrimaryGame);
            messages.Add("Created placeholder splits.lss. Replace it with real splits for tournament use.");
        }

        var settingsPath = Path.Combine(destinationDir, "asl-settings.json");

        if (request.ClearExistingSettings && File.Exists(settingsPath))
        {
            File.Delete(settingsPath);
            messages.Add("Deleted old asl-settings.json.");
        }

        if (File.Exists(settingsPath))
        {
            messages.Add("Kept existing asl-settings.json. Use Configure if these settings need to change.");
        }
        else
        {
            messages.Add("asl-settings.json was not created during install. Use Configure to create it after ASL startup.");
        }

        return new AutoSplitterInstallResult
        {
            RunId = request.RunId.Trim(),
            DestinationDir = destinationDir,
            AutosplitterPath = aslPath,
            SplitsPath = splitsPath,
            Messages = messages.ToArray()
        };
    }

    private static int ScoreEntry(AutoSplitterCatalogEntry entry, string normalizedQuery)
    {
        var score = 0;

        foreach (var gameName in entry.Games)
        {
            var normalized = NormalizeText(gameName);

            if (normalized == normalizedQuery)
            {
                score = Math.Max(score, 100);
            }
            else if (normalized.StartsWith(normalizedQuery + " ") || normalized.StartsWith(normalizedQuery))
            {
                score = Math.Max(score, 85);
            }
            else if (normalized.Contains(normalizedQuery))
            {
                score = Math.Max(score, 70);
            }
            else if (normalizedQuery.Contains(normalized))
            {
                score = Math.Max(score, 50);
            }
        }

        return score;
    }

    private static string NormalizeText(string value)
    {
        if (value == null)
        {
            return "";
        }

        return Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]+", " ").Trim();
    }

    private static string DownloadString(string url)
    {
        using (var client = new WebClient())
        {
            client.Headers[HttpRequestHeader.UserAgent] = "TournamentTimer.AslHost";
            return client.DownloadString(url);
        }
    }

    private static void DownloadFile(string url, string path)
    {
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

        using (var client = new WebClient())
        {
            client.Headers[HttpRequestHeader.UserAgent] = "TournamentTimer.AslHost";
            client.DownloadFile(url, path);
        }
    }

    private static void CopyToHostComponents(string runsRoot, string sourcePath, string relativeComponentPath, List<string> messages)
    {
        var runsDirectory = new DirectoryInfo(Path.GetFullPath(runsRoot));
        var hostRoot = runsDirectory.Parent;

        if (hostRoot == null)
        {
            return;
        }

        foreach (var arch in new[] { "x86", "x64" })
        {
            var archRoot = Path.Combine(hostRoot.FullName, arch);

            if (!Directory.Exists(archRoot))
            {
                continue;
            }

            var targetPath = Path.Combine(Path.Combine(archRoot, "Components"), relativeComponentPath);
            var targetDir = Path.GetDirectoryName(targetPath);

            if (!string.IsNullOrWhiteSpace(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            File.Copy(sourcePath, targetPath, overwrite: true);
            messages?.Add("Also copied component to ASL Host " + arch + " runtime: " + relativeComponentPath);
        }
    }

    private static void NewPlaceholderSplits(string path, int count, string gameName)
    {
        count = Math.Max(1, count);
        var segments = new StringBuilder();

        for (var i = 1; i <= count; i++)
        {
            segments.AppendLine("    <Segment>");
            segments.AppendLine("      <Name>Split " + i + "</Name>");
            segments.AppendLine("      <Icon />");
            segments.AppendLine("      <SplitTimes />");
            segments.AppendLine("      <BestSegmentTime />");
            segments.AppendLine("      <SegmentHistory />");
            segments.AppendLine("    </Segment>");
        }

        var xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n" +
            "<Run version=\"1.7.0\">\r\n" +
            "  <GameIcon />\r\n" +
            "  <GameName>" + SecurityElement.Escape(gameName ?? "Autosplitter Test") + "</GameName>\r\n" +
            "  <CategoryName>Autosplitter Test</CategoryName>\r\n" +
            "  <LayoutPath />\r\n" +
            "  <Metadata>\r\n" +
            "    <Run id=\"\" />\r\n" +
            "    <Platform usesEmulator=\"False\"></Platform>\r\n" +
            "    <Region></Region>\r\n" +
            "    <Variables />\r\n" +
            "  </Metadata>\r\n" +
            "  <Offset>00:00:00</Offset>\r\n" +
            "  <AttemptCount>0</AttemptCount>\r\n" +
            "  <AttemptHistory />\r\n" +
            "  <Segments>\r\n" +
            segments +
            "  </Segments>\r\n" +
            "  <AutoSplitterSettings />\r\n" +
            "</Run>\r\n";

        File.WriteAllText(path, xml, new UTF8Encoding(false));
    }

    private static string GetFileNameFromUrl(string url, string fallback)
    {
        try
        {
            var uri = new Uri(url);
            var name = Path.GetFileName(uri.LocalPath);

            if (!string.IsNullOrWhiteSpace(name))
            {
                return Uri.UnescapeDataString(name);
            }
        }
        catch
        {
            // Fall through.
        }

        return fallback;
    }

    private static string GetNodeText(XmlNode node, string localName)
    {
        var found = node.SelectSingleNode("./*[local-name()='" + localName + "']");
        return found == null ? "" : (found.InnerText ?? "").Trim();
    }

    private static IReadOnlyList<string> GetDescendantTexts(XmlNode node, string localName)
    {
        var result = new List<string>();
        var found = node.SelectNodes(".//*[local-name()='" + localName + "']");

        if (found == null)
        {
            return result;
        }

        foreach (XmlNode item in found)
        {
            var text = (item.InnerText ?? "").Trim();

            if (!string.IsNullOrWhiteSpace(text))
            {
                result.Add(text);
            }
        }

        return result;
    }
}

internal sealed class AutoSplitterCatalogEntry
{
    public string PrimaryGame { get; set; }
    public IReadOnlyList<string> Games { get; set; } = Array.Empty<string>();
    public string Type { get; set; }
    public string Description { get; set; }
    public IReadOnlyList<string> Urls { get; set; } = Array.Empty<string>();
    public AutoSplitterAssetClassification Classification { get; set; }

    public bool IsUnsupported
    {
        get
        {
            return Classification == null ||
                   Classification.IsWasmLike ||
                   Classification.Asl.Count == 0;
        }
    }

    public bool HasCompatibilityWarning
    {
        get
        {
            return !IsUnsupported && Classification.HasHelperDependencies;
        }
    }

    public string CompatibilityLabel
    {
        get
        {
            if (Classification == null)
            {
                return "unknown";
            }

            if (Classification.IsWasmLike)
            {
                return "unsupported: WASM";
            }

            if (Classification.Asl.Count == 0)
            {
                return "unsupported: no ASL";
            }

            if (Classification.HasHelperDependencies)
            {
                return "ASL + helpers / warning";
            }

            return "simple ASL";
        }
    }

    public string CompatibilityNote
    {
        get
        {
            if (Classification == null)
            {
                return "Compatibility could not be detected.";
            }

            if (Classification.IsWasmLike)
            {
                return "This entry looks like WASM / AutoSplittingRuntime. The current ASL Host supports ASL scripts only.";
            }

            if (Classification.Asl.Count == 0)
            {
                return "No .asl file was found in this catalog entry. The current ASL Host supports ASL scripts only.";
            }

            if (Classification.HasHelperDependencies)
            {
                return "This ASL uses helper/dependency files. Install copies them into run assets, but compatibility is best-effort; test before tournament use.";
            }

            return "Plain .asl entry. This is the safest path for the current ASL Host.";
        }
    }

    public string DependencyLabel => Classification == null ? "" : Classification.DependencyLabel;
}

internal sealed class AutoSplitterAssetClassification
{
    public List<string> Asl { get; } = new List<string>();
    public List<string> Wasm { get; } = new List<string>();
    public List<string> Dll { get; } = new List<string>();
    public List<string> AslHelp { get; } = new List<string>();
    public List<string> Uhara { get; } = new List<string>();
    public List<string> EmuHelp { get; } = new List<string>();
    public List<string> Xml { get; } = new List<string>();
    public List<string> Other { get; } = new List<string>();

    public bool IsWasmLike { get; private set; }

    public bool HasHelperDependencies =>
        AslHelp.Count > 0 || Uhara.Count > 0 || EmuHelp.Count > 0 || Dll.Count > 0 || Xml.Count > 0;

    public string DependencyLabel
    {
        get
        {
            var parts = new List<string>();

            if (AslHelp.Count > 0) parts.Add("asl-help");
            if (Uhara.Count > 0) parts.Add("uhara");
            if (EmuHelp.Count > 0) parts.Add("emu-help");
            if (Dll.Count > 0) parts.Add("dll");
            if (Xml.Count > 0) parts.Add("xml");
            if (Wasm.Count > 0) parts.Add("wasm");
            if (Other.Count > 0) parts.Add("other");

            return parts.Count == 0 ? "—" : string.Join(", ", parts);
        }
    }

    private static string GetUrlPathLower(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        try
        {
            return new Uri(url).LocalPath.ToLowerInvariant();
        }
        catch
        {
            return url.ToLowerInvariant();
        }
    }

    public static AutoSplitterAssetClassification FromUrls(IEnumerable<string> urls, string type)
    {
        var result = new AutoSplitterAssetClassification();

        if (!string.IsNullOrWhiteSpace(type) &&
            (type.IndexOf("wasm", StringComparison.OrdinalIgnoreCase) >= 0 ||
             type.IndexOf("AutoSplittingRuntime", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            result.IsWasmLike = true;
        }

        foreach (var url in urls ?? Array.Empty<string>())
        {
            var lower = (url ?? "").ToLowerInvariant();
            var pathLower = GetUrlPathLower(url);

            if (pathLower.EndsWith(".asl"))
            {
                result.Asl.Add(url);
            }
            else if (pathLower.EndsWith(".wasm"))
            {
                result.Wasm.Add(url);
                result.IsWasmLike = true;
            }
            else if (pathLower.EndsWith(".dll"))
            {
                result.Dll.Add(url);
            }
            else if (lower.Contains("asl-help"))
            {
                result.AslHelp.Add(url);
            }
            else if (lower.Contains("uhara"))
            {
                result.Uhara.Add(url);
            }
            else if (lower.Contains("emu-help"))
            {
                result.EmuHelp.Add(url);
            }
            else if (pathLower.EndsWith(".xml"))
            {
                result.Xml.Add(url);
            }
            else
            {
                result.Other.Add(url);
            }
        }

        return result;
    }
}

internal sealed class AutoSplitterInstallRequest
{
    public AutoSplitterCatalogEntry Entry { get; set; }
    public string RunId { get; set; }
    public string RunsRoot { get; set; }
    public string SplitsPath { get; set; }
    public int PlaceholderSplitCount { get; set; } = 100;
    public bool ClearExistingSettings { get; set; } = true;
}

internal sealed class AutoSplitterInstallResult
{
    public string RunId { get; set; }
    public string DestinationDir { get; set; }
    public string AutosplitterPath { get; set; }
    public string SplitsPath { get; set; }
    public IReadOnlyList<string> Messages { get; set; } = Array.Empty<string>();
}
