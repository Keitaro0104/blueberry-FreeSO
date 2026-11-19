using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
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
    /// Alur startup (Langkah 5 + perbaikan 5.1):
    /// 1. Hitung DPI/zoom, isi pemuat gambar (ImageSharp).
    /// 2. Cari folder game The Sims 1: folder tersimpan (pemilih folder / mode
    ///    langsung) atau folder `{GetExternalFilesDir}/The Sims` (hasil adb push).
    /// 3. Bila belum ada, tampilkan layar setup: pilih folder game (SAF), lalu
    ///    pilih cara memakai folder itu:
    ///      a. "Pakai langsung" — tanpa menyalin, bila folder ada di jalur nyata
    ///         yang bisa dibaca engine (mis. /storage/emulated/0/IDM/Download/
    ///         The Sims) dan izin "akses semua file" diberikan;
    ///      b. "Salin ke storage aplikasi" (bawaan);
    ///      c. "Salin ke Download/BlueberryFreeSO";
    ///      d. "Salin ke folder lain..." (pilih folder tujuan).
    ///    Penyalinan jalan di foreground service (tetap jalan walau aplikasi
    ///    diminimalkan) dengan notifikasi progres + tombol Batal/Lanjutkan.
    /// 4. Jalankan game dalam mode TS1 murni (BlueberryGame, seperti Simitone).
    /// 5. Semua tahap dicatat ke file log .txt (tombol "Lihat Log" di layar
    ///    setup) — bila penyalinan macet lagi, log menunjukkan tahap terakhir.
    /// </summary>
    [Activity(
        Label = "@string/app_name",
        MainLauncher = true,
        // Ikon blueberry (Resources/Drawable/Icon.png, dari gambar user,
        // dikonversi JPG 350x350 -> PNG 512x512 pada perbaikan Langkah 5.1).
        // CATATAN: file PNG ini biner — tidak bisa di-push lewat GitHub connector.
        // Bila PR nanti tidak berisi Icon.png, user harus mengunggahnya lewat
        // web UI GitHub sebelum merge (pola yang sama seperti Langkah 3).
        Icon = "@drawable/icon",
        AlwaysRetainTaskState = true,
        LaunchMode = LaunchMode.SingleInstance,
        ScreenOrientation = ScreenOrientation.SensorLandscape,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.ScreenSize
    )]
    public class Activity1 : AndroidGameActivity
    {
        private const string LogTag = "BlueberryFreeSO";
        private const int RequestPickGameFolder = 1001;
        private const int RequestPickDestFolder = 1002;
        private const int RequestSaveLog = 1003;
        private const int RequestPostNotifications = 2002;

        private Button _pickButton;
        private Button _resumeButton;
        private Button _cancelButton;
        private Button _logButton;
        private TextView _setupStatus;
        private bool _setupShown;
        private bool _gameStarted;

        // Folder sumber yang sedang menunggu pilihan tujuan (dipakai alur "folder lain").
        private global::Android.Net.Uri _pendingSourceUri;
        private string _pendingSourceRealPath;

        protected override void OnCreate(Bundle bundle)
        {
            base.OnCreate(bundle);
            Window.ClearFlags(WindowManagerFlags.ForceNotFullscreen);
            Window.AddFlags(WindowManagerFlags.Fullscreen);

            // --- Log .txt: pasang dulu, sebelum apa pun ---
            var logDir = GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir.AbsolutePath;
            FileLogger.Init(logDir);
            FileLogger.Log("APP", "Blueberry FreeSO dimulai (versi 1.0-perbaikan-5.1)");

            // Crash (uncaught exception) ditulis ke file .txt sebelum aplikasi menutup,
            // supaya penyebab force close bisa dibaca tanpa logcat.
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                Log.Error(LogTag, "FATAL: " + e.ExceptionObject.ToString());
                FileLogger.LogCrash("AppDomain", e.ExceptionObject);
            };
            AndroidEnvironment.UnhandledExceptionRaiser += (sender, e) =>
            {
                Log.Error(LogTag, "FATAL (Android): " + e.Exception);
                FileLogger.LogCrash("Android", e.Exception);
            };
            TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                Log.Error(LogTag, "FATAL (Task): " + e.Exception);
                FileLogger.LogCrash("Task", e.Exception);
            };

            // Folder game sudah ada (tersimpan / hasil adb push)? Langsung main.
            var folder = new AndroidLocator(this).FindTheSims1();
            if (folder != null)
            {
                FileLogger.Log("APP", "Folder game ditemukan: " + folder);
                RunGame(folder);
            }
            else
            {
                FileLogger.Log("APP", "Folder game belum ada — tampilkan layar setup");
                ShowSetupScreen();
            }
        }

        protected override void OnResume()
        {
            base.OnResume();
            if (!_setupShown || _gameStarted) return;
            CopyForegroundService.ProgressChanged += OnCopyProgress;
            CopyForegroundService.CopyFinished += OnCopyFinished;
            // Salinan selesai selagi aplikasi diminimalkan? Langsung jalankan game.
            var state = CopyStateStore.Load(this);
            if (state.Status == CopyJobStatus.Complete && !string.IsNullOrEmpty(state.DestPath))
            {
                var check = GameFolderValidator.Validate(state.DestPath);
                if (check.IsValid)
                {
                    FileLogger.Log("APP", "Salinan selesai di latar belakang — jalankan game: " + state.DestPath);
                    RunGame(state.DestPath);
                    return;
                }
            }
            RefreshResumeButton();
        }

        protected override void OnPause()
        {
            base.OnPause();
            CopyForegroundService.ProgressChanged -= OnCopyProgress;
            CopyForegroundService.CopyFinished -= OnCopyFinished;
        }

        private void OnCopyProgress(GameFolderCopyProgress p)
        {
            RunOnUiThread(() =>
            {
                if (_setupStatus == null) return;
                _setupStatus.Text = p.StatusText;
                if (_cancelButton != null)
                    _cancelButton.Visibility = p.Phase == CopyPhase.Copying || p.Phase == CopyPhase.Counting
                        ? ViewStates.Visible : ViewStates.Gone;
            });
        }

        private void OnCopyFinished(CopyState state)
        {
            RunOnUiThread(() =>
            {
                if (_gameStarted) return;
                if (state.Status == CopyJobStatus.Complete && !string.IsNullOrEmpty(state.DestPath))
                {
                    RunGame(state.DestPath);
                    return;
                }
                if (_setupStatus != null)
                {
                    _setupStatus.Text = state.Status == CopyJobStatus.Cancelled
                        ? "Penyalinan dibatalkan pada " + state.CopiedFiles + "/" + state.TotalFiles
                            + " file.\nFile yang sudah tersalin tidak perlu disalin ulang — "
                            + "ketuk \"Lanjutkan Penyalinan\"."
                        : "Penyalinan gagal:\n" + state.LastError
                            + "\n\nLihat log (.txt) untuk detail, lalu ketuk "
                            + "\"Lanjutkan Penyalinan\" untuk mencoba lagi.";
                }
                if (_pickButton != null) _pickButton.Enabled = true;
                if (_cancelButton != null) _cancelButton.Visibility = ViewStates.Gone;
                RefreshResumeButton();
            });
        }

        private void RefreshResumeButton()
        {
            if (_resumeButton == null) return;
            var state = CopyStateStore.Load(this);
            _resumeButton.Visibility = state.CanResume ? ViewStates.Visible : ViewStates.Gone;
            if (state.CanResume)
            {
                _resumeButton.Text = "Lanjutkan Penyalinan (" + state.CopiedFiles + "/"
                    + state.TotalFiles + " file, " + CopyEstimator.FormatBytes(state.CopiedBytes)
                    + " sudah tersalin)";
            }
        }

        /// <summary>
        /// Layar setup: menjelaskan bahwa aplikasi butuh file game asli,
        /// dengan tombol untuk memilih folder game. Dibangun lewat kode
        /// (tanpa file layout XML) supaya tetap satu file.
        /// </summary>
        private void ShowSetupScreen()
        {
            _setupShown = true;
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
                + "Cara 2: folder \"The Sims\" sudah ada di HP (mis. di Download atau "
                + "IDM/Download) — pilih folder itu di bawah ini. Bila memungkinkan, "
                + "aplikasi akan memakainya LANGSUNG tanpa menyalin (paling cepat). "
                + "Bila tidak, aplikasi menyalinnya — penyalinan tetap jalan walau "
                + "aplikasi diminimalkan, dengan notifikasi progres.";
            layout.AddView(info);

            _pickButton = new Button(this);
            _pickButton.Text = "Pilih Folder Game";
            _pickButton.Click += (sender, args) => PickFolder(RequestPickGameFolder);
            layout.AddView(_pickButton);

            _resumeButton = new Button(this);
            _resumeButton.Text = "Lanjutkan Penyalinan";
            _resumeButton.Visibility = ViewStates.Gone;
            _resumeButton.Click += (sender, args) =>
            {
                FileLogger.Log("COPY", "Lanjutkan penyalinan diminta dari layar setup");
                RequestNotificationPermission();
                CopyForegroundService.ResumeCopy(this);
                _resumeButton.Visibility = ViewStates.Gone;
                if (_cancelButton != null) _cancelButton.Visibility = ViewStates.Visible;
                if (_setupStatus != null) _setupStatus.Text = "Melanjutkan penyalinan...";
            };
            layout.AddView(_resumeButton);

            _cancelButton = new Button(this);
            _cancelButton.Text = "Batal";
            _cancelButton.Visibility = ViewStates.Gone;
            _cancelButton.Click += (sender, args) =>
            {
                FileLogger.Log("COPY", "Batal diminta dari layar setup");
                CopyForegroundService.CancelCopy(this);
                _cancelButton.Visibility = ViewStates.Gone;
            };
            layout.AddView(_cancelButton);

            _logButton = new Button(this);
            _logButton.Text = "Lihat Log";
            _logButton.Click += (sender, args) => ShowLogDialog();
            layout.AddView(_logButton);

            _setupStatus = new TextView(this);
            _setupStatus.SetTextSize(ComplexUnitType.Sp, 14);
            _setupStatus.SetPadding(0, pad / 2, 0, 0);
            layout.AddView(_setupStatus);

            SetContentView(layout);
            RefreshResumeButton();
        }

        /// <summary>Dialog "Lihat Log": isi file log .txt + tombol simpan ke file .txt.</summary>
        private void ShowLogDialog()
        {
            var scroll = new ScrollView(this);
            var text = new TextView(this);
            text.SetTextSize(ComplexUnitType.Sp, 13);
            text.Text = FileLogger.ReadTail(20000)
                + "\n\nLokasi file log:\n" + (FileLogger.LogPath ?? "(belum dibuat)");
            scroll.AddView(text);

            var builder = new AlertDialog.Builder(this);
            builder.SetTitle("Log Blueberry FreeSO (.txt)");
            builder.SetView(scroll);
            builder.SetPositiveButton("Simpan Log (.txt)", (sender, args) =>
            {
                var intent = new Intent(Intent.ActionCreateDocument);
                intent.AddCategory(Intent.CategoryOpenable);
                intent.SetType("text/plain");
                intent.PutExtra(Intent.ExtraTitle, FileLogger.LogFileName);
                StartActivityForResult(intent, RequestSaveLog);
            });
            builder.SetNegativeButton("Tutup", (sender, args) => { });
            builder.Show();
        }

        /// <summary>Buka pemilih folder sistem (Storage Access Framework).</summary>
        private void PickFolder(int requestCode)
        {
            var intent = new Intent(Intent.ActionOpenDocumentTree);
            intent.AddFlags(ActivityFlags.GrantReadUriPermission
                | ActivityFlags.GrantPersistableUriPermission
                | ActivityFlags.GrantPrefixUriPermission);
            StartActivityForResult(intent, requestCode);
        }

        /// <summary>
        /// Minta izin POST_NOTIFICATIONS (Android 13+) supaya notifikasi progres
        /// tampil. Best-effort: notifikasi foreground service tetap tampil walau
        /// izin ini ditolak, jadi tidak menunggu hasilnya.
        /// </summary>
        private void RequestNotificationPermission()
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu
                && CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications)
                    != Permission.Granted)
            {
                RequestPermissions(
                    new[] { global::Android.Manifest.Permission.PostNotifications },
                    RequestPostNotifications);
            }
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent data)
        {
            base.OnActivityResult(requestCode, resultCode, data);

            if (requestCode == RequestSaveLog)
            {
                if (resultCode == Result.Ok && data?.Data != null)
                {
                    try
                    {
                        using var output = ContentResolver.OpenOutputStream(data.Data);
                        if (output == null) throw new IOException("Tidak bisa membuka file tujuan.");
                        var bytes = System.Text.Encoding.UTF8.GetBytes(FileLogger.ReadTail(int.MaxValue));
                        output.Write(bytes, 0, bytes.Length);
                        Toast.MakeText(this, "Log tersimpan.", ToastLength.Short).Show();
                        FileLogger.Log("APP", "Log diekspor oleh user ke " + data.Data);
                    }
                    catch (Exception e)
                    {
                        FileLogger.LogError("APP", e);
                        Toast.MakeText(this, "Gagal menyimpan log: " + e.Message, ToastLength.Long).Show();
                    }
                }
                return;
            }

            if (requestCode == RequestPickDestFolder)
            {
                if (resultCode != Result.Ok || data?.Data == null)
                {
                    _setupStatus.Text = "Pemilihan folder tujuan dibatalkan — memakai storage aplikasi.";
                    StartCopyToAppStorage();
                    return;
                }
                var destTreeUri = data.Data;
                var destReal = GameFolderManager.ResolveRealPath(this, destTreeUri);
                if (destReal == null)
                {
                    _setupStatus.Text = "Folder tujuan itu tidak bisa dipakai (jalur nyatanya "
                        + "tidak terbaca). Memakai storage aplikasi sebagai gantinya.";
                    FileLogger.Log("COPY", "Folder tujuan custom tidak bisa diurai jalurnya: " + destTreeUri);
                    StartCopyToAppStorage();
                    return;
                }
                if (!GameFolderManager.HasAllFilesAccess(this))
                {
                    ShowNeedAllFilesAccess(
                        "Menyalin ke folder pilihan butuh izin \"akses semua file\".");
                    return;
                }
                StartCopy(_pendingSourceUri, _pendingSourceRealPath,
                    Path.Combine(destReal, "The Sims"));
                return;
            }

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
                FileLogger.Log("SAF", "TakePersistableUriPermission gagal: " + e.Message);
            }
            HandlePickedGameFolder(treeUri);
        }

        /// <summary>
        /// Folder game dipilih: validasi dulu, lalu tawarkan cara memakainya
        /// (langsung / salin ke storage aplikasi / salin ke Download /
        /// salin ke folder lain). Validasi lewat jalur nyata bila bisa diurai
        /// (cepat), lewat SAF bila tidak.
        /// </summary>
        private async void HandlePickedGameFolder(global::Android.Net.Uri treeUri)
        {
            _pickButton.Enabled = false;
            _setupStatus.Text = "Memeriksa folder game...";
            FileLogger.Log("SETUP", "Folder dipilih: " + treeUri);
            try
            {
                var realPath = GameFolderManager.ResolveRealPath(this, treeUri);
                GameFolderValidation validation;
                if (realPath != null)
                {
                    validation = await Task.Run(() => GameFolderValidator.Validate(realPath));
                }
                else
                {
                    validation = await Task.Run(
                        () => GameFolderManager.ValidateTree(this, treeUri));
                }
                if (!validation.IsValid)
                {
                    FileLogger.Log("SETUP", "Folder tidak valid: "
                        + string.Join(", ", validation.MissingFiles));
                    _setupStatus.Text = "Folder tidak valid.\n\n" + validation.UserMessage;
                    _pickButton.Enabled = true;
                    return;
                }
                FileLogger.Log("SETUP", "Folder valid"
                    + (realPath != null ? " (jalur nyata: " + realPath + ")" : " (SAF)"));

                _pendingSourceUri = treeUri;
                _pendingSourceRealPath = realPath;
                ShowUseChoiceDialog(treeUri, realPath);
            }
            catch (Exception e)
            {
                // Error di fase awal (validasi) JANGAN diam — tampilkan di layar
                // + catat ke log. Inilah yang hilang di Langkah 5 (stuck di 0%
                // tanpa pesan).
                Log.Error(LogTag, "Validasi folder game gagal: " + e);
                FileLogger.LogError("SETUP", e);
                _setupStatus.Text = "Gagal memeriksa folder game:\n" + e.Message
                    + "\n\nCoba lagi, atau lihat Log untuk detail.";
                _pickButton.Enabled = true;
            }
        }

        /// <summary>Dialog pilihan cara memakai folder game yang sudah tervalidasi.</summary>
        private void ShowUseChoiceDialog(global::Android.Net.Uri treeUri, string realPath)
        {
            var items = new[]
            {
                "Pakai folder ini langsung (tanpa menyalin — paling cepat)",
                "Salin ke storage aplikasi (bawaan)",
                "Salin ke Download/BlueberryFreeSO",
                "Salin ke folder lain... (pilih)",
            };
            var builder = new AlertDialog.Builder(this);
            builder.SetTitle("Folder game valid — mau dipakai bagaimana?");
            builder.SetItems(items, (sender, args) =>
            {
                switch (args.Which)
                {
                    case 0: UseDirectly(treeUri); break;
                    case 1: StartCopyToAppStorage(); break;
                    case 2:
                        if (!GameFolderManager.HasAllFilesAccess(this))
                        {
                            ShowNeedAllFilesAccess(
                                "Menyalin ke Download butuh izin \"akses semua file\".");
                            break;
                        }
                        StartCopy(treeUri, realPath, GameFolderManager.SharedDownloadRoot());
                        break;
                    default:
                        _setupStatus.Text = "Pilih folder tujuan salinan...";
                        PickFolder(RequestPickDestFolder);
                        break;
                }
            });
            builder.SetNegativeButton("Batal", (sender, args) =>
            {
                _pickButton.Enabled = true;
                _setupStatus.Text = "";
            });
            builder.Show();
        }

        /// <summary>Dialog penjelas saat izin "akses semua file" belum diberikan.</summary>
        private void ShowNeedAllFilesAccess(string reason)
        {
            var builder = new AlertDialog.Builder(this);
            builder.SetTitle("Butuh izin akses semua file");
            builder.SetMessage(reason + "\n\nDi halaman Settings yang terbuka, nyalakan "
                + "\"Izinkan pengelolaan semua file\" untuk Blueberry FreeSO, lalu kembali "
                + "ke aplikasi dan pilih folder game lagi.");
            builder.SetPositiveButton("Buka Settings", (sender, args) =>
            {
                FileLogger.Log("APP", "Membuka Settings akses semua file");
                GameFolderManager.RequestAllFilesAccess(this);
                _pickButton.Enabled = true;
            });
            builder.SetNegativeButton("Salin ke storage aplikasi", (sender, args) =>
            {
                StartCopyToAppStorage();
            });
            builder.Show();
        }

        /// <summary>Mode langsung: pakai folder game di tempatnya tanpa menyalin.</summary>
        private void UseDirectly(global::Android.Net.Uri treeUri)
        {
            var (path, error) = GameFolderManager.TryUseDirectly(this, treeUri);
            if (path != null)
            {
                _setupStatus.Text = "Memakai folder game langsung:\n" + path;
                GameFolderManager.SaveGameFolder(this, path, treeUri.ToString());
                RunGame(path);
                return;
            }
            // Gagal: bila karena izin, tawarkan buka Settings; bila karena
            // alasan lain, tampilkan errornya dengan jelas.
            FileLogger.Log("DIRECT", "Gagal pakai langsung: " + error);
            if (error.Contains("akses semua file"))
            {
                ShowNeedAllFilesAccess(error);
            }
            else
            {
                _setupStatus.Text = "Tidak bisa dipakai langsung:\n" + error
                    + "\n\nPilih \"Salin\" untuk menyalin isinya ke storage aplikasi.";
                _pickButton.Enabled = true;
            }
        }

        private void StartCopyToAppStorage()
        {
            StartCopy(_pendingSourceUri, _pendingSourceRealPath,
                GameFolderManager.AppGameRoot(this));
        }

        /// <summary>
        /// Mulai penyalinan lewat foreground service (tetap jalan walau aplikasi
        /// diminimalkan, dengan notifikasi progres + tombol Batal di notifikasi).
        /// </summary>
        private void StartCopy(global::Android.Net.Uri sourceUri, string sourceRealPath, string destPath)
        {
            _pickButton.Enabled = false;
            if (_resumeButton != null) _resumeButton.Visibility = ViewStates.Gone;
            if (_cancelButton != null) _cancelButton.Visibility = ViewStates.Visible;
            _setupStatus.Text = "Memulai penyalinan...\nTujuan: " + destPath;
            FileLogger.Log("COPY", "Mulai salin: sumber=" + (sourceRealPath ?? sourceUri.ToString())
                + " tujuan=" + destPath);
            RequestNotificationPermission();
            CopyForegroundService.StartCopy(this,
                sourceUri?.ToString(), sourceRealPath, destPath);
        }

        private void RunGame(string gameFolder)
        {
            if (_gameStarted) return;
            _gameStarted = true;
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
