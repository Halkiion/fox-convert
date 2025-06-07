using System.IO.Compression;
using System.Runtime.InteropServices;
using fox_convert;

class Program
{
    const string ChromeWebStorePrefix = "https://chromewebstore.google.com/detail/";
    static string CrxFileName = "extension.crx";
    static string TempDir = "temp_extract";
    static string OutputDir = "output";

    static void Main(string[] args)
    {
        Console.Title = "fox-convert";
        bool paramMode = args.Length > 0;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            string linuxDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "fox-convert");
            CrxFileName = Path.Combine(linuxDir, "extension.crx");
            TempDir = Path.Combine(linuxDir, "temp_extract");
            OutputDir = Path.Combine(linuxDir, "output");
        }

        if (!paramMode)
            ShowWelcome();

        while (true)
        {
            string storeUrl = GetStoreUrl(args, paramMode);
            string extId = GetExtensionId(storeUrl);

            try
            {
                Console.WriteLine($"Downloading {CrxFileName} and extracting to temporary directory...");
                CrxHandlers.DownloadCrxAsync(CrxHandlers.GetCrxUrl(extId), CrxFileName).Wait();
                CrxHandlers.ExtractFromCrxToFolder(CrxFileName, TempDir).Wait();

                Console.WriteLine("Starting conversion process...");
                string manifestPath = Path.Combine(TempDir, "manifest.json");
                string extName = Patchers.GetExtensionName(manifestPath);
                Patchers.ConvertManifestAsync(manifestPath, TempDir, ShowWarn).Wait();

                Patchers.PatchChromeExtensionUrls(TempDir, ShowWarn);
                Patchers.PatchUnsafeStringMethods(TempDir, ShowWarn);

                Directory.CreateDirectory(OutputDir);
                string destFolder = FindOrCreateExtensionOutputFolder(extId, extName);
                Directory.CreateDirectory(destFolder);

                string zipPath = Path.Combine(destFolder, $"{extId}.xpi");
                if (File.Exists(zipPath)) File.Delete(zipPath);
                ZipFile.CreateFromDirectory(TempDir, zipPath);
                Console.WriteLine($"Firefox extension bundle created: {zipPath}");
            }
            catch (Exception ex)
            {
                ShowError($"{ex.Message}");
            }
            finally
            {
                try
                {
                    if (Directory.Exists(TempDir))
                        Directory.Delete(TempDir, true);
                }
                catch (Exception e)
                {
                    ShowWarn($"Failed to cleanup temp directory: {e.Message}");
                }
                try
                {
                    if (File.Exists(CrxFileName))
                        File.Delete(CrxFileName);
                }
                catch (Exception e)
                {
                    ShowWarn($"Failed to delete CRX file: {e.Message}");
                }
            }

            if (paramMode) Environment.Exit(0);
            else Console.WriteLine();
        }
    }

    static string FindOrCreateExtensionOutputFolder(string extId, string extName)
    {
        foreach (var subdir in Directory.GetDirectories(OutputDir))
        {
            string candidateZip = Path.Combine(subdir, $"{extId}.xpi");
            if (File.Exists(candidateZip))
            {
                return subdir;
            }
        }
        string safeExtName = MakeSafeFolderName(extName);
        string newFolder = Path.Combine(OutputDir, safeExtName);
        Directory.CreateDirectory(newFolder);
        return newFolder;
    }

    static string MakeSafeFolderName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');

        return name.Trim();
    }

    static void ShowWelcome() =>
        Console.WriteLine("Welcome to fox-convert. Press ctrl+c at any moment to close the application.\r\n");

    static string GetStoreUrl(string[] args, bool paramMode)
    {
        string storeUrl;
        do
        {
            storeUrl = paramMode ? args[0].Trim() : Prompt("Enter Chrome Store link: ").Trim();
            if (!storeUrl.StartsWith(ChromeWebStorePrefix))
            {
                ShowError("!InvalidUrl");
                if (paramMode) Environment.Exit(1);
            }
        } while (!storeUrl.StartsWith(ChromeWebStorePrefix));
        return storeUrl;
    }

    static string GetExtensionId(string storeUrl) =>
        storeUrl.TrimEnd('/').Split('/')[^1];

    static void ShowError(string err)
    {
        string error = "ERROR: ";
        switch (err)
        {
            case "!InvalidUrl":
                error += "Invalid URL format.";
                break;
            default:
                error += err;
                break;
        }
        Console.WriteLine(error);
    }
    public static void ShowWarn(string warn) =>
        Console.WriteLine($"WARNING: {warn}");

    static string Prompt(string message)
    {
        Console.Write(message);
        var input = Console.ReadLine();
        return input ?? string.Empty;
    }
}
