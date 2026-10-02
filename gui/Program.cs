using System;
using System.IO;
using Photino.NET;

namespace LibProsperoPkgGui;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        string title = "LibProsperoPKG — PS5 Package Builder";
        string htmlPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");

        if (!File.Exists(htmlPath))
        {
            // Fallback if running from dev directory
            htmlPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "index.html");
        }

        LibProsperoPkg.Util.BuildCleaner.SweepOrphanedTempFiles();

        var window = new PhotinoWindow()
            .SetTitle(title)
            .SetUseOsDefaultSize(false)
            .SetSize(1120, 820)
            .SetMinSize(900, 680)
            .Center()
            .SetContextMenuEnabled(true)
            .SetDevToolsEnabled(true)
            .SetResizable(true)
            .SetFileSystemAccessEnabled(true)
            .SetGrantBrowserPermissions(true);

        var logic = new AppLogic(window);

        MacDragDropBridge.Initialize((paths, x, y) =>
        {
            logic.HandleNativeFilesDropped(paths, x, y);
        });

        window.RegisterWindowClosingHandler((sender, e) =>
        {
            logic.CleanupTempFiles();
            return false;
        });

        window.RegisterWebMessageReceivedHandler((sender, message) =>
        {
            logic.HandleMessage(message);
        });

        if (File.Exists(htmlPath))
        {
            window.Load(new Uri(htmlPath, UriKind.Absolute));
        }
        else
        {
            window.LoadRawString("<html><body><h1 style='color:red;'>wwwroot/index.html not found!</h1></body></html>");
        }

        window.WaitForClose();
        logic.CleanupTempFiles();
    }
}
