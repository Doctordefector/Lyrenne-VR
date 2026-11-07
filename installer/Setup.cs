using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LyrenneVR.Setup;

/// <summary>
/// LyrenneVR-Setup.exe. Installs, upgrades and uninstalls Lyrenne VR:
///   (no args)        the install window
///   /S               install silently
///   /uninstall [/S]  remove everything (the copy in the install folder is the uninstaller)
/// </summary>
static class Program
{
    public const string AppName = "Lyrenne VR";
    public const string Version = "1.0.0";
    public static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);
    public static string AppExe => Path.Combine(InstallDir, "LyrenneVR.exe");
    public static string LayerJson => Path.Combine(InstallDir, "layer", "XR_APILAYER_LYRENNE_wrist_player.json");

    const string LayerKey = @"SOFTWARE\Khronos\OpenXR\1\ApiLayers\Implicit";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LyrenneVR";
    static string Shortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppName + ".lnk");
    // Where install.ps1 used to put it, before there was an installer.
    static string LegacyDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LyrenneVR");

    [STAThread]
    static int Main(string[] args)
    {
        bool silent = args.Any(a => a.Equals("/S", StringComparison.OrdinalIgnoreCase));
        bool uninstall = args.Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase));
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (uninstall)
        {
            if (!silent && MessageBox.Show($"Remove {AppName}? Your settings stay in %LOCALAPPDATA%\\LyrenneVR.", AppName,
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return 1;
            try
            {
                Uninstall();
                if (!silent) MessageBox.Show($"{AppName} was removed.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            catch (Exception e)
            {
                if (!silent) MessageBox.Show("Could not remove everything: " + e.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 2;
            }
        }

        int shot = Array.FindIndex(args, a => a.Equals("/screenshot", StringComparison.OrdinalIgnoreCase));
        if (shot >= 0 && shot + 1 < args.Length)
        {
            // Draws the window to a PNG without showing it, for checking the layout.
            using var form = new SetupForm { StartPosition = FormStartPosition.Manual, Location = new Point(-3000, -3000), ShowInTaskbar = false };
            form.Show();
            Application.DoEvents();
            using var bmp = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
            bmp.Save(args[shot + 1], System.Drawing.Imaging.ImageFormat.Png);
            return 0;
        }

        if (silent)
        {
            try
            {
                Install(startWithWindows: true, _ => { });
                return 0;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e.Message);
                return 2;
            }
        }

        Application.Run(new SetupForm());
        return 0;
    }

    public static void Install(bool startWithWindows, Action<string> status)
    {
        status("Closing Lyrenne VR if it is running...");
        StopApp();

        status("Copying files...");
        Directory.CreateDirectory(InstallDir);
        using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")
                             ?? throw new InvalidOperationException("The installer is damaged (no payload)."))
        using (var zip = new ZipArchive(payload))
        {
            foreach (var entry in zip.Entries)
            {
                string name = entry.FullName.Replace('\\', '/');  // tolerate zips written with backslashes
                string target = Path.GetFullPath(Path.Combine(InstallDir, name));
                if (!target.StartsWith(InstallDir, StringComparison.OrdinalIgnoreCase)) continue;
                if (name.EndsWith("/")) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                try
                {
                    entry.ExtractToFile(target, overwrite: true);
                }
                catch (IOException) when (target.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && target.Contains(@"\layer\"))
                {
                    throw new IOException("A VR game is still using Lyrenne VR's OpenXR layer. Close your VR games and run the installer again.");
                }
            }
        }
        // Uninstall.exe comes inside the payload: the same program built without the app in it.
        string uninstaller = Path.Combine(InstallDir, "Uninstall.exe");

        status("Registering the OpenXR layer for Virtual Desktop (VDXR) games...");
        using (var layers = Registry.LocalMachine.CreateSubKey(LayerKey))
        {
            // Drop registrations of older copies (the script install lived in LocalAppData).
            foreach (var name in layers.GetValueNames())
                if (name.IndexOf("XR_APILAYER_LYRENNE_wrist_player", StringComparison.OrdinalIgnoreCase) >= 0 && !name.Equals(LayerJson, StringComparison.OrdinalIgnoreCase))
                    layers.DeleteValue(name, false);
            layers.SetValue(LayerJson, 0, RegistryValueKind.DWord);
        }

        status("Adding shortcuts...");
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (startWithWindows) run.SetValue("LyrenneVR", $"\"{AppExe}\"");
            else run.DeleteValue("LyrenneVR", false);
        }
        CreateShortcut(Shortcut, AppExe);
        using (var u = Registry.LocalMachine.CreateSubKey(UninstallKey))
        {
            u.SetValue("DisplayName", AppName);
            u.SetValue("DisplayVersion", Version);
            u.SetValue("Publisher", "Lyrenne");
            u.SetValue("DisplayIcon", AppExe);
            u.SetValue("InstallLocation", InstallDir);
            u.SetValue("UninstallString", $"\"{uninstaller}\" /uninstall");
            u.SetValue("QuietUninstallString", $"\"{uninstaller}\" /uninstall /S");
            u.SetValue("URLInfoAbout", "https://github.com/Doctordefector/Lyrenne-VR");
            u.SetValue("NoModify", 1, RegistryValueKind.DWord);
            u.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            u.SetValue("EstimatedSize", (int)(DirSize(InstallDir) / 1024), RegistryValueKind.DWord);
        }

        RemoveLegacyInstall();
        status("Done.");
    }

    public static string SelfPath => Process.GetCurrentProcess().MainModule!.FileName;

    public static void Uninstall()
    {
        StopApp();
        using (var layers = Registry.LocalMachine.OpenSubKey(LayerKey, writable: true))
            if (layers != null)
                foreach (var name in layers.GetValueNames())
                    if (name.IndexOf("XR_APILAYER_LYRENNE_wrist_player", StringComparison.OrdinalIgnoreCase) >= 0)
                        layers.DeleteValue(name, false);
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)) run?.DeleteValue("LyrenneVR", false);
        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false);
        if (File.Exists(Shortcut)) File.Delete(Shortcut);
        RemoveLegacyInstall();

        // This exe is inside the folder being removed, so the folder goes once it has exited.
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{InstallDir}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    /// <summary>
    /// Starts the app as the signed-in user, not elevated. An elevated copy would create its shared
    /// memory with a high integrity label, which the (unelevated) games could then not open.
    /// </summary>
    public static void LaunchUnelevated()
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppExe}\"") { UseShellExecute = false });
    }

    static void StopApp()
    {
        foreach (var p in Process.GetProcessesByName("LyrenneVR"))
        {
            try
            {
                p.Kill();
                p.WaitForExit(3000);
            }
            catch
            {
                // Already gone.
            }
            p.Dispose();
        }
    }

    static void RemoveLegacyInstall()
    {
        try
        {
            if (Directory.Exists(LegacyDir)) Directory.Delete(LegacyDir, recursive: true);
            string oldLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Lyrenne VR.lnk");
            if (File.Exists(oldLink)) File.Delete(oldLink);
        }
        catch
        {
            // A leftover old folder is harmless: nothing points at it any more.
        }
    }

    static void CreateShortcut(string path, string target)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(path);
        link.TargetPath = target;
        link.WorkingDirectory = Path.GetDirectoryName(target);
        link.Description = "Music on your wrist in VR";
        link.Save();
    }

    static long DirSize(string dir) =>
        new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
}

sealed class SetupForm : Form
{
    readonly CheckBox _startWithWindows = new() { Text = "Start with Windows", Checked = true, AutoSize = true };
    readonly CheckBox _launch = new() { Text = "Start Lyrenne VR when done", Checked = true, AutoSize = true };
    readonly Button _install = new() { Text = "Install", Width = 120, Height = 36 };
    readonly Label _status = new() { AutoSize = false, Height = 40, Width = 360 };
    bool _done;

    public SetupForm()
    {
        Text = $"{Program.AppName} {Program.Version} setup";
        Icon = Icon.ExtractAssociatedIcon(Program.SelfPath);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(580, 340);
        BackColor = Color.FromArgb(14, 15, 20);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10f);

        var logo = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(28, 28, 140, 140) };
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("logo.png"))
            if (s != null) logo.Image = Image.FromStream(s);
        var title = new Label { Text = Program.AppName, Font = new Font("Segoe UI Semibold", 20f), AutoSize = true, Location = new Point(190, 30) };
        var blurb = new Label
        {
            Text = "Lyrenne on your wrist in any PC VR game. Look at your wrist to see what's playing, point and pull the trigger to control it.\n\nWorks in SteamVR games and in Virtual Desktop (VDXR) games.",
            AutoSize = false,
            Bounds = new Rectangle(192, 76, 370, 136),
            ForeColor = Color.FromArgb(200, 200, 205),
        };
        _startWithWindows.Location = new Point(192, 220);
        _launch.Location = new Point(192, 248);
        _install.Location = new Point(432, 284);
        _install.BackColor = Color.FromArgb(0xA3, 0x7C, 0x43);
        _install.FlatStyle = FlatStyle.Flat;
        _install.FlatAppearance.BorderSize = 0;
        _status.Location = new Point(28, 288);
        _status.ForeColor = Color.FromArgb(170, 170, 178);
        Controls.AddRange(new Control[] { logo, title, blurb, _startWithWindows, _launch, _install, _status });
        _install.Click += async (_, _) =>
        {
            if (_done) Close();
            else await RunInstall();
        };
    }

    async Task RunInstall()
    {
        _install.Enabled = _startWithWindows.Enabled = _launch.Enabled = false;
        bool startWithWindows = _startWithWindows.Checked, launch = _launch.Checked;
        try
        {
            await Task.Run(() => Program.Install(startWithWindows, s => BeginInvoke((Action)(() => _status.Text = s))));
            if (launch) Program.LaunchUnelevated();
            _status.Text = launch ? "Installed. Lyrenne VR is in your system tray." : "Installed.";
            _install.Text = "Close";
            _install.Enabled = true;
            _done = true;
        }
        catch (Exception e)
        {
            _status.Text = "";
            MessageBox.Show(this, e.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _install.Enabled = _startWithWindows.Enabled = _launch.Enabled = true;
        }
    }
}
