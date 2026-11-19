using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;
using FSO.Client;
using FSO.Common;
using FSO.Files;
using FSO.LotView;
using Microsoft.Xna.Framework;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FSO.Android
{
    /// <summary>
    /// Activity utama game. Port dari FSODroid lama (TSOClient/FSODroid/FSOActivity.cs,
    /// method RunGame) ke .NET for Android modern — urutan logikanya sama, API-nya baru.
    ///
    /// Alur startup (Langkah 5):
    /// 1. Hitung DPI/zoom, isi pemuat gambar (ImageSharp).
    /// 2. Cari folder game The Sims 1: folder tersimpan (pemilih folder) atau folder
    ///    `{GetExternalFilesDir}/The Sims` (hasil adb push dari PC teman).
    /// 3. Bila belum ada, tampilkan layar setup dengan tombol "Pilih Folder Game"
    ///    (Storage Access Framework). Folder yang dipilih divalidasi
    ///    (GameData/Behavior.iff dst.) lalu disalin ke storage khusus aplikasi —
    ///    engine butuh jalur file sungguhan, sedangkan SAF hanya memberi URI.
    /// 4. Jalankan game dalam mode TS1 murni (BlueberryGame, seperti Simitone):
    ///    `TS1HybridEnable = true`, `TS1HybridPath` = folder game, string UI dari
    ///    GameData/UIText.iff. TTS sengaja TIDAK diaktifkan dan DirectX dimatikan
    ///    (Android hanya bisa OpenGL).
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
        private const int RequestPickGameFolder = 1001;

        private Button _pickButton;
        private TextView _setupStatus;

        protected override void OnCreate(Bundle bundle)
        {
            base.OnCreate(bundle);
            Window.ClearFlags(WindowManagerFlags.ForceNotFullscreen);
            Window.AddFlags(WindowManagerFlags.Fullscreen);

            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                Log.Error(LogTag, "FATAL: " + e.ExceptionObject.ToString());
            };

            // Folder game sudah ada (tersimpan / hasil adb push)? Langsung main.
            var folder = new AndroidLocator(this).FindTheSims1();
            if (folder != null)
            {
                RunGame(folder);
            }
            else
            {
                ShowSetupScreen();
            }
        }

        /// <summary>
        /// Layar setup: menjelaskan bahwa aplikasi butuh file game asli,
        /// dengan tombol untuk memilih folder game. Dibangun lewat kode
        /// (tanpa file layout XML) supaya tetap satu file.
        /// </summary>
        private void ShowSetupScreen()
        {
            var density = Resources.DisplayMetrics.Density;
            int pad = (int)(24 * density);

            var layout = new LinearLayout(this)
            {
                Orientation = Orientation.Vertical,
            };
            layout.SetPadding(pad, pad, pad, pad);
            layout.SetGravity(GravityFlags.CenterHorizontal);

            var title = new TextView(this);
            title.SetTextSize(ComplexUnitType.Sp, 24);
            title.Text = "Blueberry FreeSO";
            layout.AddView(title);

            var info = new TextView(this);
            info.SetTextSize(ComplexUnitType.Sp, 16);
            info.SetPadding(0, pad, 0, pad);
            info.Text = "Aplikasi ini butuh file game asli The Sims 1 "
                + "(Complete Collection / Legacy Collection) untuk bisa jalan.\n\n"
                + "Cara 1: salin folder \"The Sims\" dari PC teman ke HP lewat USB/ADB, "
                + "ke folder:\n" + GameFolderManager.AppGameRoot(this) + "\n"
                + "lalu buka lagi aplikasi ini.\n\n"
                + "Cara 2: salin folder \"The Sims\" ke HP (mis. ke Download), "
                + "lalu pilih folder itu di bawah ini. Aplikasi akan menyalinnya "
                + "ke storage aplikasi (butuh ruang kosong sekitar 3 GB).";
            layout.AddView(info);

            _pickButton = new Button(this);
            _pickButton.Text = "Pilih Folder Game";
            _pickButton.Click += (sender, args) => PickFolder();
            layout.AddView(_pickButton);

            _setupStatus = new TextView(this);
            _setupStatus.SetTextSize(ComplexUnitType.Sp, 14);
            _setupStatus.SetPadding(0, pad / 2, 0, 0);
            layout.AddView(_setupStatus);

            SetContentView(layout);
        }

        /// <summary>Buka pemilih folder sistem (Storage Access Framework).</summary>
        private void PickFolder()
        {
            var intent = new Intent(Intent.ActionOpenDocumentTree);
            intent.AddFlags(ActivityFlags.GrantReadUriPermission
                | ActivityFlags.GrantPersistableUriPermission
                | ActivityFlags.GrantPrefixUriPermission);
            StartActivityForResult(intent, RequestPickGameFolder);
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            if (requestCode != RequestPickGameFolder) return;

            if (resultCode != Result.Ok || data?.Data == null)
            {
                // User membatalkan pemilih folder: beri pesan yang jelas, lalu tutup
                // (bukan crash). Buka lagi aplikasi untuk mencoba lagi.
                var builder = new AlertDialog.Builder(this);
                builder.SetTitle("Pemilihan dibatalkan");
                builder.SetMessage("Blueberry FreeSO membutuhkan folder game The Sims 1 asli "
                    + "untuk bisa jalan. Aplikasi akan ditutup — buka lagi dan pilih "
                    + "folder game untuk mencoba lagi.");
                builder.SetPositiveButton("OK", (sender, args) => Finish());
                builder.SetCancelable(false);
                builder.Show();
                return;
            }

            var treeUri = data.Data;
            try
            {
                // Simpan izin baca folder ini supaya URI-nya bisa dipakai lagi nanti.
                ContentResolver.TakePersistableUriPermission(
                    treeUri, ActivityFlags.GrantReadUriPermission);
            }
            catch (Exception e)
            {
                Log.Warn(LogTag, "TakePersistableUriPermission gagal: " + e.Message);
            }
            StartCopyFromTree(treeUri);
        }

        /// <summary>
        /// Validasi folder yang dipilih, lalu salin isinya ke storage aplikasi
        /// di thread latar (supaya layar tidak beku), dengan progress.
        /// </summary>
        private async void StartCopyFromTree(global::Android.Net.Uri treeUri)
        {
            _pickButton.Enabled = false;
            _setupStatus.Text = "Memeriksa folder game...";
            try
            {
                var validation = await Task.Run(
                    () => GameFolderManager.ValidateTree(this, treeUri));
                if (!validation.IsValid)
                {
                    _setupStatus.Text = "Folder tidak valid.\n\n" + validation.UserMessage;
                    _pickButton.Enabled = true;
                    return;
                }

                var dest = GameFolderManager.AppGameRoot(this);
                _setupStatus.Text = "Menyalin file game... 0%";
                var progress = new Progress<GameFolderCopyProgress>(p =>
                {
                        _setupStatus.Text = "Menyalin file game... " + p.Percent + "%"
                        + " (" + p.CopiedFiles + "/" + p.TotalFiles + " file)\n" + p.CurrentFile;
                });
                await GameFolderManager.CopyTreeAsync(
                    this, treeUri, dest, progress, CancellationToken.None);

                var check = GameFolderValidator.Validate(dest);
                if (!check.IsValid)
                {
                    _setupStatus.Text = "Salinan tidak lengkap, file ini hilang:\n- "
                        + string.Join("\n- ", check.MissingFiles)
                        + "\n\nCoba pilih folder game lagi.";
                    _pickButton.Enabled = true;
                    return;
                }

                GameFolderManager.SaveGameFolder(this, dest, treeUri.ToString());
                RunGame(dest);
            }
            catch (Exception e)
            {
                Log.Error(LogTag, "Setup folder game gagal: " + e);
                _setupStatus.Text = "Gagal menyalin folder game:\n" + e.Message
                    + "\n\nCoba lagi.";
                _pickButton.Enabled = true;
            }
        }

        private void RunGame(string gameFolder)
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

            // --- 4. Mode TS1 murni (Simone) + folder game ---
            // Simitone: GlobalSettings TS1HybridEnable = true, TS1HybridPath = folder
            // instalasi TS1, dan ContentStrings.TS1 = true supaya string UI dibaca
            // dari GameData/UIText.iff (bukan dari data TSO). StartupPath juga
            // diisi folder TS1 (dipakai Content.InitBasic sebagai BasePath — dalam
            // mode TS1 murni pemindaian BasePath sebagai file TSO dilewati).
            // BlueberryGame membalik Content.Target ke FSOEngineMode.TS1 setelah
            // TSOGame.Initialize() (yang hanya bisa menghasilkan TS1Hybrid/TSO).
            GlobalSettings.Default.TS1HybridEnable = true;
            GlobalSettings.Default.TS1HybridPath = gameFolder;
            FSO.Client.GameContent.ContentStrings.TS1 = true;

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
            start.SetPath(gameFolder);

            BlueberryGame game = new BlueberryGame();
            GameFacade.DirectX = false;
            World.DirectX = false;
            SetContentView((View)game.Services.GetService(typeof(View)));
            game.Run();
        }

        /// <summary>
        /// Menyalin file konten kecil (efek, font, tekstur — dari TSOClient/tso.content/Content,
        /// dipaketkan sebagai aset APK) ke {docs}/Content/, sekali saja. Dicegah salin ulang
        /// dengan file penanda. Konten besar FSO.Content.TSO (Objects/, ~288 MB) TIDAK
        /// dipaketkan — dalam mode TS1, objek dimuat dari instalasi TS1 sendiri
        /// (TS1ObjectProvider), bukan dari Objects/ TSO.
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
        /// Dipakai saat game tidak bisa jalan sama sekali.
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
