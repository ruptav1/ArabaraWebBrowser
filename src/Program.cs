using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Arabara
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // Started by an update: wait for the old Arabara to exit, then remove what it left.
            if (args.Contains("--after")) Updater.AfterUpdate(args, args.Contains("--update-test-done") ? null : Updater.WorkFolder);
            if (args.Contains("--update-test-done"))
            {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "update-test.done"), Updater.Current.ToString());
                return 0;
            }
            if (args.Contains("--apply-update-test")) return Updater.ApplyTest(args);
            if (args.Contains("--update-check")) return Updater.CheckTest();
            if (args.Contains("--edition")) return Edition.Public ? Edition.PublicExitCode : Edition.PersonalExitCode;
            if (args.Contains("--self-test")) return SelfTests.Run();
            if (args.Contains("--memory-bench")) return MemoryBench.Run(args);
            if (args.Contains("--soak-test")) return SoakTest.Run(args);
            if (args.Contains("--own-audio-test")) return SoakTest.OwnAudioTest();
            if (args.Contains("--frame-test")) return FrameTest();
            if (args.Contains("--capture-test")) return CaptureTest.Run(args);
            if (args.Contains("--music-test")) return MusicTest.Run();
            if (args.Contains("--audio-test")) return AudioFileTest.Run(args[Array.IndexOf(args, "--audio-test") + 1]);
            if (args.Contains("--home-test"))
            {
                UiFonts.Initialize();
                try { return SpinningMark.RenderTest(SelfTests.ResultsRoot); }
                finally { UiFonts.Shutdown(); }
            }
            if (args.Contains("--intro-test"))
            {
                UiFonts.Initialize();
                try { return StartupIntro.RenderTest(SelfTests.ResultsRoot); }
                finally { UiFonts.Shutdown(); }
            }
            if (args.Contains("--preview-image"))
            {
                UiFonts.Initialize();
                try { return StartupIntro.PreviewImage(Path.GetFullPath(args[Array.IndexOf(args, "--preview-image") + 1])); }
                finally { UiFonts.Shutdown(); }
            }
            bool smoke = args.Contains("--smoke-test");
            // The existing profile was moved intact during the Arabara rename.
            string dataRoot = smoke ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", ".test-profile") :
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Arabara");
            bool first;
            using (var mutex = new Mutex(true, smoke ? "Local\\Arabara.Smoke" : "Local\\Arabara.Browser", out first))
            {
                if (!first)
                {
                    MessageBox.Show("Arabara is already running. Please use the existing window.", "Arabara");
                    return 0;
                }
                try
                {
                    // An update that could not finish tidying up (see Updater).
                    if (!smoke) Updater.CleanUp(AppDomain.CurrentDomain.BaseDirectory, null);
                    UiFonts.Initialize();
                    var form = new BrowserForm(Path.GetFullPath(dataRoot), smoke, args.Contains("--reopen"));
                    Application.Run(form);
                    return form.ExitCode;
                }
                catch (Exception ex)
                {
                    if (smoke) SelfTests.WriteResult("smoke-tests.txt", ex.ToString());
                    else MessageBox.Show("Unable to start Arabara.\n" + ex.Message, "Arabara", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
                finally { UiFonts.Shutdown(); }
            }
        }

        // --frame-test (with --no-intro): opens the real window on an isolated profile,
        // captures it from the screen maximized and restored, then exits.
        private static int FrameTest()
        {
            UiFonts.Initialize();
            try
            {
                string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", ".test-profile-frame"));
                var form = new BrowserForm(root, false);
                form.Shown += async delegate
                {
                    await System.Threading.Tasks.Task.Delay(1500);
                    Capture(form, "frame-maximized.png");
                    await form.FrameTestLiveAsync(Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..")));
                    Capture(form, "frame-live.png");
                    form.WindowState = FormWindowState.Normal;
                    form.Bounds = new System.Drawing.Rectangle(80, 80, 1100, 700);
                    await System.Threading.Tasks.Task.Delay(1000);
                    Capture(form, "frame-normal.png");
                    form.Close();
                };
                Application.Run(form);
                return 0;
            }
            finally { UiFonts.Shutdown(); }
        }

        private static void Capture(Form form, string name)
        {
            var bounds = form.Bounds;
            var screen = Screen.FromControl(form).Bounds;
            bounds.Intersect(screen);
            using (var image = new System.Drawing.Bitmap(bounds.Width, bounds.Height))
            {
                using (var g = System.Drawing.Graphics.FromImage(image)) g.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);
                image.Save(Path.Combine(SelfTests.ResultsRoot, name));
            }
        }
    }
}
