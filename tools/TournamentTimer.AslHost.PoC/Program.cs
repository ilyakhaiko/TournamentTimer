using System;
using System.IO;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        AslHostOptions options = null;

        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            options = AslHostOptions.Parse(args);

            if (options.ShowHelp)
            {
                AslHostOptions.PrintHelp();
                return 0;
            }

            var host = new HeadlessAslHost(options);
            host.Run();

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine();

            if (TryPrintFriendlyFatal(ex, options?.TraceMode ?? false))
            {
                return 1;
            }

            Console.WriteLine("FATAL: " + ex.Message);
            Console.WriteLine(ex);
            return 1;
        }
    }

    private static bool TryPrintFriendlyFatal(Exception ex, bool traceMode)
    {
        var badImage = FindInnerException<BadImageFormatException>(ex);

        if (badImage != null)
        {
            Console.WriteLine("FATAL: Dependency architecture mismatch.");
            Console.WriteLine();
            Console.WriteLine("An autosplitter dependency DLL was built for a different processor architecture.");
            Console.WriteLine("Try the other ASL Host architecture for this autosplitter.");
            Console.WriteLine();
            Console.WriteLine("If you started x86, try:");
            Console.WriteLine("  powershell -ExecutionPolicy Bypass -File .\\scripts\\start-asl-host-x64.ps1 -RunId <runId>");
            Console.WriteLine();
            Console.WriteLine("If you started x64, try:");
            Console.WriteLine("  powershell -ExecutionPolicy Bypass -File .\\scripts\\start-asl-host-x86.ps1 -RunId <runId>");
            Console.WriteLine();
            Console.WriteLine("Dependency: " + GetDependencyName(badImage.FileName, badImage.Message));
            Console.WriteLine("Original error: " + badImage.Message);

            if (traceMode)
            {
                Console.WriteLine();
                Console.WriteLine(ex);
            }

            return true;
        }

        var missingFile = FindInnerException<FileNotFoundException>(ex);

        if (missingFile != null && IsLikelyAutosplitterComponent(missingFile))
        {
            Console.WriteLine("FATAL: Missing autosplitter dependency.");
            Console.WriteLine();
            Console.WriteLine("The ASL script tried to load a file from Components, but that file was not found in the ASL Host runtime.");
            Console.WriteLine("Make sure the autosplitter package includes its Components files and that the runner package contains them.");
            Console.WriteLine();
            Console.WriteLine("Missing file: " + GetMissingFileName(missingFile));
            Console.WriteLine();
            Console.WriteLine("If this was assembled manually, copy runs/<RunId>/Components/* into the ASL Host runtime Components folder.");
            Console.WriteLine("The ASL Host can also do this automatically when run assets contain a Components folder.");
            Console.WriteLine("Original error: " + missingFile.Message);

            if (traceMode)
            {
                Console.WriteLine();
                Console.WriteLine(ex);
            }

            return true;
        }

        return false;
    }

    private static TException FindInnerException<TException>(Exception ex)
        where TException : Exception
    {
        var current = ex;

        while (current != null)
        {
            if (current is TException typed)
            {
                return typed;
            }

            current = current.InnerException;
        }

        return null;
    }

    private static bool IsLikelyAutosplitterComponent(FileNotFoundException ex)
    {
        var fileName = ex.FileName ?? string.Empty;
        var message = ex.Message ?? string.Empty;

        return fileName.IndexOf("Components", StringComparison.OrdinalIgnoreCase) >= 0 ||
               message.IndexOf("Components", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string GetMissingFileName(FileNotFoundException ex)
    {
        if (!string.IsNullOrWhiteSpace(ex.FileName))
        {
            return ex.FileName;
        }

        return ex.Message;
    }

    private static string GetDependencyName(string fileName, string message)
    {
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            return fileName;
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return "unknown";
        }

        const string marker = "Could not load file or assembly '";
        var start = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);

        if (start < 0)
        {
            return "unknown";
        }

        start += marker.Length;
        var end = message.IndexOf("'", start, StringComparison.OrdinalIgnoreCase);

        return end > start
            ? message.Substring(start, end - start)
            : "unknown";
    }
}
