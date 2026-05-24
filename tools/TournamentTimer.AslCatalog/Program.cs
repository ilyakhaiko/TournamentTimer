using System;
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
                MessageBox.Show(
                    "TournamentTimer ASL Catalog\r\n\r\n" +
                    "Open this tool without arguments to search/install autosplitters.\r\n" +
                    "Optional: --runsRoot=PATH",
                    "ASL Catalog",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                return 0;
            }

            if (options.ConfigureMode)
            {
                var host = new HeadlessAslHost(options);
                host.Run();
                return 0;
            }

            Application.Run(new AutosplitterCatalogForm(options));
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "FATAL: " + ex.Message + "\r\n\r\n" + ex,
                "ASL Catalog error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);

            return 1;
        }
    }
}
