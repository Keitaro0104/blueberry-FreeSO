using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Util;
using Android.Views;
using FSO.Client;
using FSO.Common;
using FSO.Files;
using FSO.LotView;
using Microsoft.Xna.Framework;
using System;
using System.IO;
using System.Threading;

namespace FSO.Android
{
    /// <summary>
    /// Activity utama game. Port dari FSODroid lama (TSOClient/FSODroid/FSOActivity.cs,
    /// method RunGame) ke .NET for Android modern — urutan logikanya sama, API-nya baru.
    /// Alurnya: hitung DPI/zoom -&gt; isi pemuat gambar (ImageSharp) -&gt; cari file game
    /// (AndroidLocator) -&gt; isi FSOEnvironment -&gt; GameStartProxy -&gt; TSOGame -&gt; Run().
    /// TTS sengaja TIDAK diaktifkan (ITTSContext.Provider dibiarkan null, jadi panel chat
    /// tidak mencoba bicara) dan DirectX dimatikan (Android hanya bisa OpenGL).
    /// </summary>
    [Activity(
        Label = "@string/app_name",
        MainLauncher = true,
        // Icon sengaja TIDAK dipasang di sini: file Resources/Drawable/Icon.png
        // adalah biner dan tidak ikut ter-push lewat GitHub connector saat PR #3
        // (connector hanya bisa file teks), jadi drawable/icon tidak ada di repo.
        // Bila Icon.png sudah diunggah lewat web UI, kembalikan baris ini:
        // Icon = "@drawable/icon",
        AlwaysRetainTaskState = true,
        LaunchMode = LaunchMode.SingleInstance,
        ScreenOrientation = ScreenOrientation.SensorLandscape,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.ScreenSize
    )]
    public class Activity1 : AndroidGameActivity
    {
        private const string LogTag = "BlueberryFreeSO";

        protected override void OnCreate(Bundle bundle)
        {
            base.OnCreate(bundle);
            Window.ClearFlags(WindowManagerFlags.ForceNotFullscreen);
            Window.AddFlags(WindowManagerFlags.Fullscreen);

            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                Log.Error(LogTag, "FATAL: " + e.ExceptionObject.ToString());
            };

            RunGame();
        }

        private void RunGame()
        {
            // --- 1. DPI / zoom UI, pola FSODroid ---
            // HP kecil: UI diperbesar supaya tombol tidak terlalu kecil.
            var disp = Resources.DisplayMetrics;
            var initSF = 1 + ((int)disp.DensityDpi / 200);
            var dpiScaleFactor = initSF;
            var width = Math.Max(disp.WidthPixels, disp.HeightPixels);
            float uiZoomFactor = 1f;
            while (width / dpiScaleFactor < 800 && dpiScaleFactor > 1)
            {
                dpiScaleFactor--;
                uiZoomFactor = (initSF / dpiScaleFactor);
            }

            // --- 2. Pemuat gambar Android (ImageSharp), ganti System.Drawing di PC ---
            ImageLoaderHelpers.BitmapFunction = AndroidImageLoader.BitmapReader;
            ImageLoaderHelpers.SavePNGFunc = AndroidImageLoader.SavePNG;
            FSO.Files.ImageLoader.UseSoftLoad = false;

            // --- 3. Salin konten game (aset kecil) ke storage, sekali saja ---
            // FSOEnvironment.ContentDir dibaca lewat File IO biasa (bukan lewat ContentManager),
            // jadi file konten harus ada di disk, bukan cuma di dalam APK. Pola FSODroid,
            // tapi hanya saat pertama kali (FSODroid menyalinnya setiap kali jalan).
            var docs = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments);
            CopyContentOnce(docs);

            // --- 4. Cari file game asli (pola FSOProgram: locator + tuning.dat) ---
            var locator = new AndroidLocator(this);
            var gamePath = locator.FindTheSimsOnline();
            if (gamePath == null)
            {
                var message = "File game belum ditemukan di HP ini.\n\n"
                    + "Blueberry FreeSO butuh file game asli (The Sims Online / The Sims 1) "
                    + "di folder:\n" + AndroidLocator.GameRoot(this) + "/TSOClient/\n\n"
                    + "Penyalinan file game dari perangkatmu akan ditambahkan di Langkah 5.";
                Log.Warn(LogTag, "Game files not found at " + AndroidLocator.GameRoot(this));
                ShowFatalMessage("File game belum ditemukan", message);
                return;
            }

            // --- 5. FSOEnvironment, nilai-nilai dari FSODroid ---
            // DirectX mati (Android = OpenGL), keyboard software nyala, GL versi 2.
            FSOEnvironment.ContentDir = Path.Combine(docs, "Content/");
            FSOEnvironment.GFXContentDir = "Content/OGL/"; // dibaca ContentManager dari aset APK
            FSOEnvironment.UserDir = docs;
            FSOEnvironment.Linux = true;
            FSOEnvironment.DirectX = false;
            FSOEnvironment.SoftwareKeyboard = true;
            FSOEnvironment.SoftwareDepth = true;
            FSOEnvironment.UseMRT = false;
            FSOEnvironment.UIZoomFactor = uiZoomFactor;
            FSOEnvironment.DPIScaleFactor = dpiScaleFactor;
            FSOEnvironment.Enable3D = true;
            FSOEnvironment.EnableNPOTMip = false;
            FSOEnvironment.GLVer = 2;
            FSOEnvironment.TexCompress = false;
            FSOEnvironment.TexCompressSupport = false;

            // Di bawah ini hanya agar pembacaan file relatif (mis. blueprint.dtd) jalan.
            Directory.SetCurrentDirectory(docs);

            FSOEnvironment.GameThread = Thread.CurrentThread;

            // Pesan error dari dalam game (mis. konten gagal dimuat) dicatat ke logcat.
            // Di PC pesan ini muncul sebagai MessageBox; di Android belum ada UI dialognya.
            FSOProgram.ShowDialog = (text) => Log.Error(LogTag, "Game: " + text);

            // --- 6. Pengaturan default, nilai-nilai dari FSODroid ---
            // Catatan: GlobalSettings.Default baru boleh disentuh SETELAH UserDir diisi,
            // karena file config.ini disimpan di UserDir.
            GlobalSettings.Default.CityShadows = false;
            var set = GlobalSettings.Default;
            set.TargetRefreshRate = 60;
            set.CurrentLang = "english";
            set.Lighting = true;
            set.SmoothZoom = true;
            set.AntiAlias = 0;
            set.LightingMode = 3;
            set.AmbienceVolume = 10;
            set.FXVolume = 10;
            set.MusicVolume = 10;
            set.VoxVolume = 10;
            set.DirectionalLight3D = false;

            // --- 7. Jalankan game (pola GameStartProxy, sama seperti PC) ---
            var start = new GameStartProxy();
            start.SetPath(gamePath);

            TSOGame game = new TSOGame();
            GameFacade.DirectX = false;
            World.DirectX = false;
            SetContentView((View)game.Services.GetService(typeof(View)));
            game.Run();
        }

        /// <summary>
        /// Menyalin file konten kecil (efek, font, tekstur — dari TSOClient/tso.content/Content,
        /// dipaketkan sebagai aset APK) ke {docs}/Content/, sekali saja. Dicegah salin ulang
        /// dengan file penanda. Konten besar FSO.Content.TSO (Objects/, ~288 MB) BELUM
        /// dipaketkan — itu pekerjaan Langkah 5.
        /// </summary>
        private void CopyContentOnce(string docs)
        {
            var destRoot = Path.Combine(docs, "Content");
            var marker = Path.Combine(destRoot, ".blueberry-content-copied");
            if (File.Exists(marker)) return;

            try
            {
                CopyAssets("Content", destRoot);
                File.WriteAllText(marker, "1");
                Log.Info(LogTag, "Game content copied to " + destRoot);
            }
            catch (Exception e)
            {
                Log.Warn(LogTag, "Failed to copy game content: " + e);
            }
        }

        /// <summary>
        /// Salinan rekursif aset APK ke folder tujuan. Folder dikenali dari Assets.List
        /// (bukan dari ada-tidaknya titik di nama, seperti trik FSODroid lama).
        /// Entri yang gagal disalin dicatat lalu dilewati, supaya satu file rusak
        /// tidak menggagalkan seluruh penyalinan.
        /// </summary>
        private void CopyAssets(string assetFolder, string diskFolder)
        {
            Directory.CreateDirectory(diskFolder);
            foreach (string name in Assets.List(assetFolder) ?? Array.Empty<string>())
            {
                var assetPath = assetFolder + "/" + name;
                var diskPath = Path.Combine(diskFolder, name);
                try
                {
                    var children = Assets.List(assetPath);
                    if (children != null && children.Length > 0)
                    {
                        CopyAssets(assetPath, diskPath);
                    }
                    else
                    {
                        using var output = File.OpenWrite(diskPath);
                        using var input = Assets.Open(assetPath);
                        input.CopyTo(output);
                    }
                }
                catch (Exception e)
                {
                    Log.Warn(LogTag, "Skip asset " + assetPath + ": " + e.Message);
                }
            }
        }

        /// <summary>
        /// Tampilkan pesan fatal sebagai dialog Android lalu tutup activity.
        /// Dipakai saat game tidak bisa jalan sama sekali (mis. file game belum ada).
        /// </summary>
        private void ShowFatalMessage(string title, string message)
        {
            var builder = new AlertDialog.Builder(this);
            builder.SetTitle(title);
            builder.SetMessage(message);
            builder.SetPositiveButton("OK", (sender, args) => Finish());
            builder.SetCancelable(false);
            builder.Show();
        }
    }
}
