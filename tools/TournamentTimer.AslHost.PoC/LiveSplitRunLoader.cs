using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;

internal static class LiveSplitRunLoader
{
    public static IRun LoadFromLssSimple(string lssPath)
    {
        if (!File.Exists(lssPath))
        {
            throw new FileNotFoundException("splits.lss not found", lssPath);
        }

        var document = XDocument.Load(lssPath);

        var gameName = document.Descendants()
            .FirstOrDefault(x => x.Name.LocalName == "GameName")
            ?.Value
            ?.Trim() ?? "Unknown Game";

        var categoryName = document.Descendants()
            .FirstOrDefault(x => x.Name.LocalName == "CategoryName")
            ?.Value
            ?.Trim() ?? "Unknown Category";

        var run = new Run(new StandardComparisonGeneratorsFactory())
        {
            GameName = gameName,
            CategoryName = categoryName
        };

        var segmentNames = document.Descendants()
            .Where(x => x.Name.LocalName == "Segment")
            .Select(segment => segment.Elements()
                .FirstOrDefault(x => x.Name.LocalName == "Name")
                ?.Value
                ?.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();

        foreach (var name in segmentNames)
        {
            run.Add(new Segment(
                name,
                default(Time),
                default(Time),
                icon: null,
                splitTime: default(Time)));
        }

        return run;
    }
}
