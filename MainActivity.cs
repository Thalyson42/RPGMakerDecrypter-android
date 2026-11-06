using Android.App;
using Android.Content;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using RPGMakerDecrypter.RGSSAD;

[assembly: UsesPermission("android.permission.MANAGE_EXTERNAL_STORAGE")]

namespace RpgDecryptApp;

[Activity(Label = "RPG Maker Decrypter", MainLauncher = true, Exported = true)]
public class MainActivity : Activity
{
    private const int PickRequest = 1001;
    private TextView _log;
    private Button _pickButton;

    protected override void OnCreate(Bundle savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var layout = new LinearLayout(this) { Orientation = Orientation.Vertical };
        layout.SetPadding(32, 48, 32, 32);

        var info = new TextView(this)
        {
            Text = "Extracts .rgssad / .rgss2a / .rgss3a archives (RPG Maker XP, VX, VX Ace).\n" +
                   "Output: /sdcard/RPGMakerDecrypter/<archive name>/\n" +
                   "If that folder is not writable, grant \"All files access\" first; " +
                   "otherwise the app's own folder is used."
        };
        layout.AddView(info);

        var permButton = new Button(this) { Text = "1. Grant all files access" };
        permButton.Click += (s, e) => OpenAllFilesAccessSettings();
        layout.AddView(permButton);

        _pickButton = new Button(this) { Text = "2. Choose archive and extract" };
        _pickButton.Click += (s, e) => PickFile();
        layout.AddView(_pickButton);

        _log = new TextView(this) { Text = "Ready." };
        var scroll = new ScrollView(this);
        scroll.AddView(_log);
        layout.AddView(scroll);

        SetContentView(layout);
    }

    private void OpenAllFilesAccessSettings()
    {
        try
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            {
                var intent = new Intent(
                    Settings.ActionManageAppAllFilesAccessPermission,
                    Android.Net.Uri.Parse("package:" + PackageName));
                StartActivity(intent);
            }
            else
            {
                Log("Not needed on this Android version.");
            }
        }
        catch (Exception ex)
        {
            Log("Could not open settings: " + ex.Message);
        }
    }

    private void PickFile()
    {
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*");
        StartActivityForResult(intent, PickRequest);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent data)
    {
        base.OnActivityResult(requestCode, resultCode, data);

        if (requestCode != PickRequest || resultCode != Result.Ok || data?.Data == null)
        {
            return;
        }

        var uri = data.Data;
        _pickButton.Enabled = false;
        Log("Selected: " + uri);

        Task.Run(() =>
        {
            try
            {
                Extract(uri);
            }
            catch (Exception ex)
            {
                Log("ERROR: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                RunOnUiThread(() => _pickButton.Enabled = true);
            }
        });
    }

    private string GetDisplayName(Android.Net.Uri uri)
    {
        string name = null;
        using var cursor = ContentResolver.Query(uri, new[] { "_display_name" }, null, null, null);
        if (cursor != null && cursor.MoveToFirst())
        {
            name = cursor.GetString(0);
        }
        return string.IsNullOrWhiteSpace(name) ? "archive" : name;
    }

    private void Extract(Android.Net.Uri uri)
    {
        var displayName = GetDisplayName(uri);
        var baseName = Path.GetFileNameWithoutExtension(displayName);
        var tempPath = Path.Combine(CacheDir.AbsolutePath, "input.bin");

        Log("Copying archive...");
        using (var input = ContentResolver.OpenInputStream(uri))
        using (var output = File.Create(tempPath))
        {
            input.CopyTo(output);
        }

        // Detect format from the header instead of trusting the extension:
        // "RGSSAD\0" followed by a version byte (1 = XP/VX, 3 = VX Ace).
        var header = new byte[8];
        using (var fs = File.OpenRead(tempPath))
        {
            if (fs.Read(header, 0, 8) < 8)
            {
                throw new InvalidDataException("File is too small to be an RGSSAD archive.");
            }
        }
        if (System.Text.Encoding.ASCII.GetString(header, 0, 6) != "RGSSAD")
        {
            throw new InvalidDataException("This is not an RGSSAD archive (header not found).");
        }
        var version = header[7];

        var outDir = PrepareOutputDirectory(baseName);
        Log("Extracting to: " + outDir);

        RPGMakerDecrypter.RGSSAD.RGSSAD archive;
        if (version == 1)
        {
            archive = new RGSSADv1(tempPath);
        }
        else if (version == 3)
        {
            archive = new RGSSADv3(tempPath);
        }
        else
        {
            throw new NotSupportedException("Unsupported RGSSAD version: " + version);
        }

        using (archive)
        {
            archive.ExtractAllFiles(outDir, true);
        }

        File.Delete(tempPath);
        Log("Done. Look for the Data folder in:\n" + outDir);
    }

    private string PrepareOutputDirectory(string baseName)
    {
        var shared = Path.Combine(
            Android.OS.Environment.ExternalStorageDirectory.AbsolutePath,
            "RPGMakerDecrypter",
            baseName);

        try
        {
            Directory.CreateDirectory(shared);
            var probe = Path.Combine(shared, ".probe");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return shared;
        }
        catch (Exception)
        {
            var fallback = Path.Combine(GetExternalFilesDir(null).AbsolutePath, baseName);
            Directory.CreateDirectory(fallback);
            Log("Shared folder not writable (grant all files access). Using app folder.");
            return fallback;
        }
    }

    private void Log(string message)
    {
        RunOnUiThread(() => _log.Text = _log.Text + "\n" + message);
    }
}
