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
    /// Alur startup (Langkah 5 + perbaikan 5.1 + perbaikan 5.2):
    /// 1. Hitung DPI/zoom, isi pemuat gambar (ImageSharp).
    /// 2. Tampilkan LAYAR LAUNCHER — aplikasi TIDAK PERNAH auto-launch game lagi.
    ///    Launcher menampilkan folder game + statusnya, tombol Play besar, tombol
    ///    Pilih/Ganti Folder, Lanjutkan Penyalinan, Lihat Log, dan Simpan Log ke
    ///    Download. Game baru jalan bila tombol Play ditekan.
    /// 3. Folder game: folder tersimpan (pemilih folder / mode langsung) atau folder
    ///    `{GetExternalFilesDir}/The Sims` (hasil adb push). Bila belum ada,
    ///    launcher menampilkan penjelasan + tombol Pilih Folder Game (alur setup
    ///    lama: pilih folder game (SAF), lalu pilih cara memakainya — langsung /
    ///    salin ke storage aplikasi / salin ke Download / salin ke folder lain).
    ///    Penyalinan jalan di foreground service dengan notifikasi progres +
    ///    tombol Batal/Lanjutkan, dan progresnya juga tampil DI DALAM aplikasi
    ///    (progress bar + nama file + ETA) — bukan cuma di notifikasi.
    /// 4. Game dijalankan dalam mode TS1 murni (BlueberryGame, seperti Simitone).
    /// 5. Semua tahap dicatat ke file log .txt — bila force close, log otomatis
    ///    disalin ke folder Download (tombol "Simpan Log ke Download" juga ada
    ///    di launcher), dan bila game terakhir kali crash, launcher menampilkan
    ///    spanduk peringatan.
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

        private Button _playButton;
        private Button _pickButton;
        private Button _resumeButton;
        private Button _cancelButton;
        private Button _logButton;
        private Button _saveLogDownloadButton;
        private TextView _folderText;
        private TextView _infoText;
        private TextView _statusText;
        private TextView _warningText;
        private TextView _progressText;
        private ProgressBar _progressBar;
        private bool _launcherShown;
        private bool _gameStarted;
        // True bila satu sesi game sudah jalan di proses ini — dipakai OnDestroy
        // untuk mematikan proses (state statis FSO tidak bisa dibuat ulang).
        private bool _gameRan;
        private System.Threading.Timer _heartbeatTimer;
        private System.Threading.Timer _survivalTimer;

        // Folder sumber yang sedang menunggu pilihan tujuan (dipakai alur "folder lain").
        private global::Android.Net.Uri _pendingSourceUri;
        private string _pendingSourceRealPath;

        protected override void OnCreate(Bundle bundle)
        {
            base.OnCreate(bundle);
            Window.ClearFlags(WindowManagerFlags.ForceNotFullscreen);
            Window.AddFlags(WindowManagerFlags.Fullscreen);

            // --- Log .txt: pasang dulu, sebelum apa pun ---
            FileLogger.SetContext(ApplicationContext);
            var logDir = GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir.AbsolutePath;
            FileLogger.Init(logDir);
            FileLogger.Log("APP", "Blueberry FreeSO dimulai (versi 1.0-perbaikan-5.2)");
            FileLogger.Log("APP", "HP: " + Build.Manufacturer + " " + Build.Model
                + ", Android " + Build.VERSION.Release
                + " (SDK " + (int)Build.VERSION.SdkInt + ")");

            // Crash (uncaught exception) ditulis ke file .txt sebelum aplikasi menutup,
            // lalu log otomatis disalin ke folder Download supaya bisa diambil user
            // tanpa masuk ke android/data. Best-effort: tidak boleh melempar.
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                Log.Error(LogTag, "FATAL: " + e.ExceptionObject.ToString());
                FileLogger.LogCrash("AppDomain", e.ExceptionObject);
                SafeExportLogToDownloads();
            };
            AndroidEnvironment.UnhandledExceptionRaiser += (sender, e) =>
            {
                Log.Error(LogTag, "FATAL (Android): " + e.Exception);
                FileLogger.LogCrash("Android", e.Exception);
                SafeExportLogToDownloads();
            };
            TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                Log.Error(LogTag, "FATAL (Task): " + e.Exception);
                FileLogger.LogCrash("Task", e.Exception);
                SafeExportLogToDownloads();
            };

            // Perbaikan 5.2: TIDAK PERNAH auto-launch game lagi — selalu tampilkan
            // layar launcher dulu. Game baru jalan bila tombol Play ditekan.
            ShowLauncher();
        }

        /// <summary>Auto-export log ke Download — best-effort, tidak pernah melempar.</summary>
        private void SafeExportLogToDownloads()
        {
            try
            {
                if (FileLogger.ExportToDownloads())
                    FileLogger.Log("DOWNLOAD", "Log otomatis tersimpan ke Download (saat crash)");
            }
            catch (Exception) { }
        }

        protected override void OnResume()
        {
            base.OnResume();
            if (!_launcherShown || _gameStarted) return;
            CopyForegroundService.ProgressChanged += OnCopyProgress;
            CopyForegroundService.CopyFinished += OnCopyFinished;
            // Salinan (tidak) auto-launch lagi — launcher hanya di-refresh:
            // bila salinan selesai selagi aplikasi diminimalkan, Play aktif.
            RefreshLauncher();
        }

        protected override void OnPause()
        {
            base.OnPause();
            CopyForegroundService.ProgressChanged -= OnCopyProgress;
            CopyForegroundService.CopyFinished -= OnCopyFinished;
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            // Keluar normal setelah game jalan: matikan proses supaya buka
            // berikutnya selalu proses segar. State statis FSO (Content.INSTANCE
            // singleton, Game, GameFacade) tidak bisa dibuat ulang di proses
            // yang sama — itulah yang membuat buka kedua force close terus
            // di versi 5.1 (proses lama masih hidup setelah Back).
            // JANGAN matikan proses bila penyalinan sedang jalan (service
            // penyalinan hidup di proses yang sama) atau bila game tidak
            // pernah jalan di proses ini (keluar dari launcher biasa).
            if (IsFinishing && _gameRan && !IsCopyActive())
            {
                FileLogger.Log("APP", "Keluar dari game — matikan proses supaya buka berikutnya segar");
                GameCrashTracker.MarkCleanExit(this);
                global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
            }
        }

        /// <summary>True bila penyalinan sedang berjalan (service atau state tersimpan).</summary>
        private bool IsCopyActive()
        {
            if (CopyForegroundService.IsRunning) return true;
            var state = CopyStateStore.Load(this);
            return state.Status == CopyJobStatus.Copying
                || state.Status == CopyJobStatus.Counting;
        }

        private void OnCopyProgress(GameFolderCopyProgress p)
        {
            RunOnUiThread(() =>
            {
                if (!_launcherShown) return;
                ApplyProgress(p);
                if (_cancelButton != null)
                    _cancelButton.Visibility = p.Phase == CopyPhase.Copying || p.Phase == CopyPhase.Counting
                        ? ViewStates.Visible : ViewStates.Gone;
                if (_playButton != null) _playButton.Enabled = false;
                if (_statusText != null && p.Phase != CopyPhase.Done)
                    _statusText.Text = p.Phase == CopyPhase.Counting
                        ? "Menghitung file game..."
                        : "Menyalin file game...";
            });
        }

        private void OnCopyFinished(CopyState state)
        {
            RunOnUiThread(() =>
            {
                if (_gameStarted) return;
                RefreshLauncher();
                if (_statusText == null) return;
                // TIDAK auto-RunGame lagi (perbaikan 5.2) — user menekan Play sendiri.
                if (state.Status == CopyJobStatus.Complete && !string.IsNullOrEmpty(state.DestPath))
                {
                    // Simpan folder game secara eksplisit (lagi) — service juga
                    // menyimpannya saat salinan selesai, tapi launcher tidak boleh
                    // bergantung pada satu jalur saja. Bila tulis prefs service
                    // hilang (mis. proses mati sesaat sesudahnya), folder tetap
                    // ketemu dan tombol Play aktif saat aplikasi dibuka lagi.
                    GameFolderManager.SaveGameFolder(this, state.DestPath);
                    FileLogger.Log("COPY", "Folder game disimpan: " + state.DestPath);
                    _statusText.Text = "Salinan selesai — siap dimainkan. Ketuk Play.";
                }
                else if (state.Status == CopyJobStatus.Cancelled)
                {
                    _statusText.Text = "Penyalinan dibatalkan pada " + state.CopiedFiles + "/" + state.TotalFiles
                        + " file.\nFile yang sudah tersalin tidak perlu disalin ulang — "
                        + "ketuk \"Lanjutkan Penyalinan\".";
                }
                else if (state.Status == CopyJobStatus.Failed)
                {
                    _statusText.Text = "Penyalinan gagal:\n" + state.LastError
                        + "\n\nLihat log (.txt) untuk detail, lalu ketuk "
                        + "\"Lanjutkan Penyalinan\" untuk mencoba lagi.";
                }
                if (_pickButton != null) _pickButton.Enabled = true;
                if (_cancelButton != null) _cancelButton.Visibility = ViewStates.Gone;
            });
        }

        /// <summary>
        /// Perbarui semua elemen launcher dari state saat ini: folder game,
        /// status penyalinan, penghitung crash. Dipanggil dari OnResume,
        /// OnCopyFinished, dan setelah operasi folder (pilih/salin/pakai langsung).
        /// </summary>
        private void RefreshLauncher()
        {
            if (!_launcherShown) return;
            var folder = new AndroidLocator(this).FindTheSims1();
            var copyState = CopyStateStore.Load(this);
            // Fallback: bila folder tersimpan tidak ketemu tapi CopyState bilang
            // salinan sudah selesai dan folder tujuannya valid, pulihkan dari
            // CopyState (bukti lapangan 20:35 — setup sempat muncul lagi padahal
            // salinan sudah selesai; penyebab yang mungkin: tulis prefs kalah
            // dengan crash, atau validasi sesaat gagal lalu prefs dihapus).
            if (folder == null && copyState.Status == CopyJobStatus.Complete
                && !string.IsNullOrEmpty(copyState.DestPath)
                && GameFolderValidator.Validate(copyState.DestPath).IsValid)
            {
                GameFolderManager.SaveGameFolder(this, copyState.DestPath);
                FileLogger.Log("APP", "Folder game dipulihkan dari CopyState: " + copyState.DestPath);
                folder = copyState.DestPath;
            }
            bool copyActive = IsCopyActive();

            // Spanduk peringatan: game terakhir kali crash (penghitung >= 1).
            if (_warningText != null)
            {
                bool crashed = GameCrashTracker.HasCrashed(this);
                _warningText.Visibility = crashed ? ViewStates.Visible : ViewStates.Gone;
                if (crashed)
                    _warningText.Text = "Peringatan: terakhir kali game force close saat dijalankan. "
                        + "Lihat log untuk detail, lalu ketuk Play untuk mencoba lagi.";
            }

            if (_folderText != null)
                _folderText.Text = folder != null
                    ? "Folder game:\n" + folder
                    : "Folder game belum dipilih.";

            // Penjelasan cara 1/2 hanya relevan bila folder belum ada.
            if (_infoText != null)
                _infoText.Visibility = folder == null ? ViewStates.Visible : ViewStates.Gone;

            if (_playButton != null)
                _playButton.Enabled = folder != null && !copyActive;

            if (_resumeButton != null)
            {
                _resumeButton.Visibility = copyState.CanResume ? ViewStates.Visible : ViewStates.Gone;
                if (copyState.CanResume)
                {
                    _resumeButton.Text = "Lanjutkan Penyalinan (" + copyState.CopiedFiles + "/"
                        + copyState.TotalFiles + " file, " + CopyEstimator.FormatBytes(copyState.CopiedBytes)
                        + " sudah tersalin)";
                }
            }

            if (copyActive)
            {
                // Tampilkan progres TERKINI segera (bukan kosong): dari service
                // bila masih jalan, atau dari state tersimpan bila tidak.
                ShowCurrentProgress();
                if (_cancelButton != null) _cancelButton.Visibility = ViewStates.Visible;
                if (_statusText != null) _statusText.Text = "Menyalin file game...";
            }
            else
            {
                if (_progressBar != null) _progressBar.Visibility = ViewStates.Gone;
                if (_progressText != null) _progressText.Visibility = ViewStates.Gone;
                if (_cancelButton != null) _cancelButton.Visibility = ViewStates.Gone;
                if (_statusText != null)
                {
                    if (folder != null && copyState.Status == CopyJobStatus.Complete)
                        _statusText.Text = "Salinan selesai — siap dimainkan. Ketuk Play.";
                    else if (folder != null)
                        _statusText.Text = "Siap dimainkan. Ketuk Play.";
                    else if (copyState.Status == CopyJobStatus.Failed && !string.IsNullOrEmpty(copyState.LastError))
                        _statusText.Text = "Penyalinan terakhir gagal:\n" + copyState.LastError;
                    else if (copyState.Status == CopyJobStatus.Cancelled)
                        _statusText.Text = "Penyalinan dibatalkan pada " + copyState.CopiedFiles
                            + "/" + copyState.TotalFiles + " file — ketuk \"Lanjutkan Penyalinan\".";
                    else
                        _statusText.Text = "";
                }
            }
        }

        /// <summary>
        /// Gambar progres penyalinan terkini ke UI launcher. Dipakai saat launcher
        /// dibuka / OnResume di tengah penyalinan — supaya progres tidak kosong
        /// sampai laporan berikutnya datang.
        /// </summary>
        private void ShowCurrentProgress()
        {
            var last = CopyForegroundService.LastProgress;
            if (last != null)
            {
                ApplyProgress(last);
                return;
            }
            // Service baru mulai / proses baru: bangun progres dari state tersimpan.
            var state = CopyStateStore.Load(this);
            if (state.TotalFiles <= 0 && state.CopiedFiles <= 0) return;
            ApplyProgress(new GameFolderCopyProgress
            {
                Phase = state.Status == CopyJobStatus.Counting ? CopyPhase.Counting : CopyPhase.Copying,
                TotalFiles = state.TotalFiles,
                CopiedFiles = state.CopiedFiles,
                TotalBytes = state.TotalBytes,
                CopiedBytes = state.CopiedBytes,
                CountedFiles = state.TotalFiles,
                CountedBytes = state.TotalBytes,
            });
        }

        /// <summary>Gambar satu laporan progres ke progress bar + teks (jalan di UI thread).</summary>
        private void ApplyProgress(GameFolderCopyProgress p)
        {
            if (_progressBar == null || _progressText == null) return;
            _progressBar.Visibility = ViewStates.Visible;
            _progressText.Visibility = ViewStates.Visible;
            if (p.Phase == CopyPhase.Counting)
            {
                _progressBar.Indeterminate = true;
            }
            else
            {
                _progressBar.Indeterminate = false;
                _progressBar.Max = 100;
                _progressBar.Progress = p.Percent;
            }
            _progressText.Text = p.StatusText;
        }

        /// <summary>
        /// Layar launcher: judul, peringatan crash, folder game, progres salin,
        /// tombol Play besar, dan tombol-tombol lain. Dibangun lewat kode (tanpa
        /// file layout XML) supaya tetap satu file. Dibungkus ScrollView supaya
        /// tidak ada konten yang ketutup di bawah layar saat landscape
        /// (bug laporan user 20:19 — teks status tidak kelihatan di aplikasi,
        /// cuma di notifikasi).
        /// </summary>
        private void ShowLauncher()
        {
            _launcherShown = true;
            var density = Resources.DisplayMetrics.Density;
            int pad = (int)(24 * density);

            var scroll = new ScrollView(this);
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

            _warningText = new TextView(this);
            _warningText.SetTextSize(ComplexUnitType.Sp, 15);
            _warningText.SetPadding(0, pad / 2, 0, pad / 2);
            _warningText.SetTextColor(global::Android.Graphics.Color.ParseColor("#C62828"));
            _warningText.Visibility = ViewStates.Gone;
            layout.AddView(_warningText);

            _folderText = new TextView(this);
            _folderText.SetTextSize(ComplexUnitType.Sp, 14);
            _folderText.SetPadding(0, pad / 2, 0, 0);
            layout.AddView(_folderText);

            _infoText = new TextView(this);
            _infoText.SetTextSize(ComplexUnitType.Sp, 16);
            _infoText.SetPadding(0, pad, 0, pad);
            _infoText.Text = "Aplikasi ini butuh file game asli The Sims 1 "
                + "(Complete Collection / Legacy Collection) untuk bisa jalan.\n\n"
                + "Cara 1: salin folder \"The Sims\" dari PC teman ke HP lewat USB/ADB, "
                + "ke folder:\n" + GameFolderManager.AppGameRoot(this) + "\n"
                + "lalu buka lagi aplikasi ini.\n\n"
                + "Cara 2: folder \"The Sims\" sudah ada di HP (mis. di Download atau "
                + "IDM/Download) — pilih folder itu di bawah ini. Bila memungkinkan, "
                + "aplikasi akan memakainya LANGSUNG tanpa menyalin (paling cepat). "
                + "Bila tidak, aplikasi menyalinnya — penyalinan tetap jalan walau "
                + "aplikasi diminimalkan, dengan notifikasi progres.";
            layout.AddView(_infoText);

            _playButton = new Button(this);
            _playButton.Text = "▶  Play";
            _playButton.SetTextSize(ComplexUnitType.Sp, 20);
            _playButton.SetPadding(0, pad / 2, 0, pad / 2);
            _playButton.Enabled = false;
            _playButton.Click += (sender, args) => OnPlayClicked();
            layout.AddView(_playButton);

            _progressBar = new ProgressBar(this, null,
                global::Android.Resource.Attribute.ProgressBarStyleHorizontal);
            _progressBar.Max = 100;
            _progressBar.Visibility = ViewStates.Gone;
            layout.AddView(_progressBar);

            _progressText = new TextView(this);
            _progressText.SetTextSize(ComplexUnitType.Sp, 14);
            _progressText.SetPadding(0, pad / 4, 0, 0);
            _progressText.Visibility = ViewStates.Gone;
            layout.AddView(_progressText);

            _pickButton = new Button(this);
            _pickButton.Text = "Pilih Folder Game";
            _pickButton.Click += (sender, args) => PickFolder(RequestPickGameFolder);
            layout.AddView(_pickButton);

            _resumeButton = new Button(this);
            _resumeButton.Text = "Lanjutkan Penyalinan";
            _resumeButton.Visibility = ViewStates.Gone;
            _resumeButton.Click += (sender, args) =>
            {
                FileLogger.Log("COPY", "Lanjutkan penyalinan diminta dari launcher");
                RequestNotificationPermission();
                CopyForegroundService.ResumeCopy(this);
                _resumeButton.Visibility = ViewStates.Gone;
                if (_cancelButton != null) _cancelButton.Visibility = ViewStates.Visible;
                if (_statusText != null) _statusText.Text = "Melanjutkan penyalinan...";
            };
            layout.AddView(_resumeButton);

            _cancelButton = new Button(this);
            _cancelButton.Text = "Batal";
            _cancelButton.Visibility = ViewStates.Gone;
            _cancelButton.Click += (sender, args) =>
            {
                FileLogger.Log("COPY", "Batal diminta dari launcher");
                CopyForegroundService.CancelCopy(this);
                _cancelButton.Visibility = ViewStates.Gone;
            };
            layout.AddView(_cancelButton);

            _logButton = new Button(this);
            _logButton.Text = "Lihat Log";
            _logButton.Click += (sender, args) => ShowLogDialog();
            layout.AddView(_logButton);

            _saveLogDownloadButton = new Button(this);
            _saveLogDownloadButton.Text = "Simpan Log ke Download";
            _saveLogDownloadButton.Click += (sender, args) =>
            {
                bool ok = false;
                try { ok = FileLogger.ExportToDownloads(); }
                catch (Exception e) { FileLogger.LogError("DOWNLOAD", e); }
                Toast.MakeText(this,
                    ok ? "Log tersimpan di Download/BlueberryFreeSO-log.txt"
                        : "Gagal menyimpan log ke Download.",
                    ToastLength.Long).Show();
                FileLogger.Log("APP", "Simpan Log ke Download diminta user: " + (ok ? "berhasil" : "gagal"));
            };
            layout.AddView(_saveLogDownloadButton);

            _statusText = new TextView(this);
            _statusText.SetTextSize(ComplexUnitType.Sp, 14);
            _statusText.SetPadding(0, pad / 2, 0, 0);
            layout.AddView(_statusText);

            scroll.AddView(layout);
            SetContentView(scroll);
            RefreshLauncher();
        }

        /// <summary>
        /// Tombol Play ditekan: game baru dijalankan sekarang (tidak ada lagi
        /// auto-launch). Penghitung crash naik 1 TEPAT SEBELUM RunGame — bila
        /// game crash / tidak bertahan 20 detik, hitung tetap naik dan launcher
        /// menampilkan peringatan saat dibuka lagi.
        /// </summary>
        private void OnPlayClicked()
        {
            var folder = new AndroidLocator(this).FindTheSims1();
            if (folder == null)
            {
                if (_statusText != null)
                    _statusText.Text = "Folder game belum ada — pilih folder game dulu.";
                return;
            }
            var check = GameFolderValidator.Validate(folder);
            if (!check.IsValid)
            {
                if (_statusText != null)
                    _statusText.Text = "Folder game tidak valid:\n" + check.UserMessage;
                FileLogger.Log("APP", "Play ditekan tapi folder tidak valid: " + folder);
                return;
            }
            int crashCount = GameCrashTracker.BeginLaunch(this);
            FileLogger.Log("APP", "Play ditekan — jalankan game dari " + folder
                + " (penghitung crash: " + crashCount + ")");
            RunGame(folder);
        }

        /// <summary>Dialog "Lihat Log": isi file log .txt + tombol simpan ke file .txt.</summary>
        private void ShowLogDialog()
        {
            var scroll = new ScrollView(this);
            var text = new TextView(this);
            text.SetTextSize(ComplexUnitType.Sp, 13);
            text.Text = FileLogger.ReadTail(20000)
                + "\n\nLokasi file log:\n" + (FileLogger.LogPath ?? "(belum dibuat)")
                + "\n\nBila aplikasi force close, log terbaru juga otomatis tersimpan di:\n"
                + "Download/BlueberryFreeSO-log.txt";
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
                    if (_statusText != null)
                        _statusText.Text = "Pemilihan folder tujuan dibatalkan — memakai storage aplikasi.";
                    StartCopyToAppStorage();
                    return;
                }
                var destTreeUri = data.Data;
                var destReal = GameFolderManager.ResolveRealPath(this, destTreeUri);
                if (destReal == null)
                {
                    if (_statusText != null)
                        _statusText.Text = "Folder tujuan itu tidak bisa dipakai (jalur nyatanya "
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
            if (_statusText != null) _statusText.Text = "Memeriksa folder game...";
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
                    if (_statusText != null)
                        _statusText.Text = "Folder tidak valid.\n\n" + validation.UserMessage;
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
                if (_statusText != null)
                    _statusText.Text = "Gagal memeriksa folder game:\n" + e.Message
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
                        if (_statusText != null) _statusText.Text = "Pilih folder tujuan salinan...";
                        PickFolder(RequestPickDestFolder);
                        break;
                }
            });
            builder.SetNegativeButton("Batal", (sender, args) =>
            {
                _pickButton.Enabled = true;
                if (_statusText != null) _statusText.Text = "";
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

        /// <summary>
        /// Mode langsung: pakai folder game di tempatnya tanpa menyalin.
        /// Perbaikan 5.2: TIDAK auto-RunGame lagi — folder disimpan, launcher
        /// di-refresh, user menekan Play sendiri.
        /// </summary>
        private void UseDirectly(global::Android.Net.Uri treeUri)
        {
            var (path, error) = GameFolderManager.TryUseDirectly(this, treeUri);
            if (path != null)
            {
                GameFolderManager.SaveGameFolder(this, path, treeUri.ToString());
                FileLogger.Log("DIRECT", "Folder langsung disimpan: " + path);
                RefreshLauncher();
                if (_statusText != null)
                    _statusText.Text = "Memakai folder game langsung:\n" + path
                        + "\n\nSiap dimainkan. Ketuk Play.";
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
                if (_statusText != null)
                    _statusText.Text = "Tidak bisa dipakai langsung:\n" + error
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
        /// Progresnya juga tampil di launcher (progress bar + teks) — lihat
        /// OnCopyProgress / ShowCurrentProgress.
        /// </summary>
        private void StartCopy(global::Android.Net.Uri sourceUri, string sourceRealPath, string destPath)
        {
            _pickButton.Enabled = false;
            if (_resumeButton != null) _resumeButton.Visibility = ViewStates.Gone;
            if (_cancelButton != null) _cancelButton.Visibility = ViewStates.Visible;
            if (_progressBar != null) _progressBar.Visibility = ViewStates.Visible;
            if (_playButton != null) _playButton.Enabled = false;
            if (_statusText != null)
                _statusText.Text = "Memulai penyalinan...\nTujuan: " + destPath;
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
            _gameRan = true;
            FileLogger.Log("RUN", "mulai (folder=" + gameFolder + ")");

            // Heartbeat: tiap 10 detik tulis jejak (berapa detik game jalan +
            // LoadProgress konten) — bila crash native, baris terakhir di log
            // menunjukkan game sempat jalan berapa lama dan konten sampai mana.
            // Timer jalan di thread pool (game.Run() memblokir UI thread).
            var runStartedUtc = DateTime.UtcNow;
            _heartbeatTimer = new System.Threading.Timer(_ =>
            {
                try
                {
                    var elapsed = (int)(DateTime.UtcNow - runStartedUtc).TotalSeconds;
                    string progress;
                    try { progress = FSO.Content.Content.LoadProgress.ToString(); }
                    catch (Exception) { progress = "?"; }
                    FileLogger.Log("RUN", "heartbeat " + elapsed + " detik, LoadProgress=" + progress);
                }
                catch (Exception) { }
            }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

            // Survival: bila game bertahan 20 detik, bukan crash startup —
            // penghitung crash direset (launcher tidak menampilkan peringatan).
            _survivalTimer = new System.Threading.Timer(_ =>
            {
                try
                {
                    GameCrashTracker.MarkSurvived(this);
                    FileLogger.Log("RUN", "Game bertahan 20 detik — penghitung crash direset");
                }
                catch (Exception) { }
            }, null, TimeSpan.FromSeconds(20), Timeout.InfiniteTimeSpan);

            try
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
                var copyStarted = DateTime.UtcNow;
                CopyContentOnce(docs);
                FileLogger.Log("RUN", "CopyContentOnce selesai ("
                    + (DateTime.UtcNow - copyStarted).TotalSeconds.ToString("0.0") + " detik)");

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
                FileLogger.Log("RUN", "pengaturan FSOEnvironment + GlobalSettings selesai");

                // --- 7. Jalankan game (pola GameStartProxy, sama seperti PC) ---
                var start = new GameStartProxy();
                start.SetPath(gameFolder);
                FileLogger.Log("RUN", "SetPath selesai: " + gameFolder);

                FileLogger.Log("RUN", "game dibuat (BlueberryGame)");
                BlueberryGame game = new BlueberryGame();
                GameFacade.DirectX = false;
                World.DirectX = false;
                SetContentView((View)game.Services.GetService(typeof(View)));
                FileLogger.Log("RUN", "SetContentView selesai");
                FileLogger.Log("RUN", "game.Run() dipanggil");
                game.Run();
                FileLogger.Log("RUN", "game.Run() kembali (keluar normal)");
                GameCrashTracker.MarkCleanExit(this);
            }
            finally
            {
                try { _heartbeatTimer?.Dispose(); } catch (Exception) { }
                try { _survivalTimer?.Dispose(); } catch (Exception) { }
                _heartbeatTimer = null;
                _survivalTimer = null;
                _gameStarted = false;
            }

            // Game keluar normal: tutup activity — OnDestroy mematikan proses
            // supaya buka berikutnya selalu proses segar (lihat OnDestroy).
            // Bila activity sudah finishing (MonoGame menutupnya sendiri),
            // Finish() tidak berpengaruh.
            if (!IsFinishing) Finish();
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
