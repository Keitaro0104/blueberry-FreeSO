using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Util;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FSO.Android
{
    /// <summary>
    /// Foreground service penyalinan folder game — penyalinan tetap jalan
    /// walau aplikasi diminimalkan / layar dikunci, dengan notifikasi progres
    /// di status bar (persen + nama file saat ini + estimasi).
    ///
    /// Kenapa foreground service (bukan thread biasa di Activity)? Thread di
    /// Activity bisa dihentikan sistem bila aplikasi diminimalkan; foreground
    /// service memberi tahu sistem "pekerjaan ini sedang dipakai user" dan
    /// menampilkannya sebagai notifikasi yang tidak bisa di-swipe.
    ///
    /// Android 13+: notifikasi butuh izin POST_NOTIFICATIONS (diminta Activity
    /// sebelum memulai salinan; notifikasi foreground service sendiri tetap
    /// tampil walau izin itu ditolak). Android 14+ (targetSdk 35): service ini
    /// bertipe dataSync — dideklarasikan di atribut [Service] dan izin
    /// FOREGROUND_SERVICE_DATA_SYNC di AndroidManifest.xml.
    ///
    /// Komunikasi dengan Activity: event statis ProgressChanged (dipakai layar
    /// setup saat aplikasi terbuka) + CopyState di SharedPreferences (dipakai
    /// Activity.OnResume — bila salinan selesai selagi aplikasi diminimalkan,
    /// Activity tahu dan langsung menjalankan game).
    /// </summary>
    [Service(Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
    public class CopyForegroundService : Service
    {
        public const string ActionStart = "start";
        public const string ActionCancel = "cancel";
        public const string ActionResume = "resume";
        public const string ExtraAction = "action";
        public const string ExtraSourceUri = "source_uri";
        public const string ExtraSourceRealPath = "source_real_path";
        public const string ExtraDestPath = "dest_path";

        public const string ChannelId = "blueberry_copy";
        private const int NotificationId = 1001;
        private const int RequestContent = 3001;
        private const int RequestCancel = 3002;
        private const int RequestResume = 3003;

        /// <summary>Progress terakhir (untuk Activity yang baru dibuka).</summary>
        public static GameFolderCopyProgress LastProgress { get; private set; }

        /// <summary>Dipanggil tiap progress berubah (Activity berlangganan saat tampil).</summary>
        public static event Action<GameFolderCopyProgress> ProgressChanged;

        /// <summary>Dipanggil saat pekerjaan selesai/dibatalkan/gagal.</summary>
        public static event Action<CopyState> CopyFinished;

        public static bool IsRunning { get; private set; }

        private static CancellationTokenSource _cts;
        private static readonly object _startLock = new object();

        public static void CancelCopy(Context context)
        {
            lock (_startLock) { _cts?.Cancel(); }
            Log.Info("BlueberryFreeSO", "CopyForegroundService: batal diminta");
        }

        public static void StartCopy(Context context, string sourceUri, string sourceRealPath, string destPath)
        {
            var intent = new Intent(context, typeof(CopyForegroundService));
            intent.PutExtra(ExtraAction, ActionStart);
            intent.PutExtra(ExtraSourceUri, sourceUri);
            intent.PutExtra(ExtraSourceRealPath, sourceRealPath);
            intent.PutExtra(ExtraDestPath, destPath);
            context.StartForegroundService(intent);
        }

        public static void ResumeCopy(Context context)
        {
            var intent = new Intent(context, typeof(CopyForegroundService));
            intent.PutExtra(ExtraAction, ActionResume);
            context.StartForegroundService(intent);
        }

        public override IBinder OnBind(Intent intent) => null;

        public override void OnCreate()
        {
            base.OnCreate();
            CreateChannel();
        }

        public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
        {
            var action = intent?.GetStringExtra(ExtraAction) ?? ActionStart;
            if (action == ActionCancel)
            {
                CancelCopy(this);
                return StartCommandResult.NotSticky;
            }
            if (action == ActionResume)
            {
                var saved = CopyStateStore.Load(this);
                if (saved.CanResume)
                    StartCopyInternal(saved.SourceUri, saved.SourceRealPath, saved.DestPath);
                else
                    StopSelf();
                return StartCommandResult.NotSticky;
            }

            // ActionStart
            var sourceUri = intent.GetStringExtra(ExtraSourceUri);
            var sourceRealPath = intent.GetStringExtra(ExtraSourceRealPath);
            var destPath = intent.GetStringExtra(ExtraDestPath);
            StartCopyInternal(sourceUri, sourceRealPath, destPath);
            return StartCommandResult.NotSticky;
        }

        private void StartCopyInternal(string sourceUri, string sourceRealPath, string destPath)
        {
            lock (_startLock)
            {
                if (IsRunning) return; // sudah ada pekerjaan berjalan — jangan ganda
                IsRunning = true;
                _cts = new CancellationTokenSource();
            }

            var state = new CopyState
            {
                Status = CopyJobStatus.Copying,
                Mode = GameFolderMode.Copy,
                SourceUri = sourceUri,
                SourceRealPath = sourceRealPath,
                DestPath = destPath,
            };
            CopyStateStore.Save(this, state);
            FileLogger.Log("COPY", "Service mulai: sumber=" + (sourceRealPath ?? sourceUri) + " tujuan=" + destPath);

            // Wajib dipanggil segera (Android 12+ membatasi waktu start foreground).
            // Overload 3-arg (dengan tipe service) hanya ada di API 29+.
            var initial = BuildNotification(
                "Menyalin file game...", "Memulai...", 0, false, showCancel: true);
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
                StartForeground(NotificationId, initial, ForegroundService.TypeDataSync);
            else
                StartForeground(NotificationId, initial);

            var token = _cts.Token;
            var ctx = this;
            _ = Task.Run(async () =>
            {
                try
                {
                    var progress = new Progress<GameFolderCopyProgress>(p =>
                    {
                        LastProgress = p;
                        ProgressChanged?.Invoke(p);
                        UpdateNotification(p);
                        // Simpan state berkala (tiap 50 file) supaya "Lanjutkan"
                        // tahu progres terakhir walau aplikasi ditutup mendadak.
                        if (p.CopiedFiles % 50 == 0 || p.Phase == CopyPhase.Done)
                        {
                            state.Status = p.Phase == CopyPhase.Counting ? CopyJobStatus.Counting : CopyJobStatus.Copying;
                            state.TotalFiles = p.TotalFiles;
                            state.CopiedFiles = p.CopiedFiles;
                            state.TotalBytes = p.TotalBytes;
                            state.CopiedBytes = p.CopiedBytes;
                            CopyStateStore.Save(ctx, state);
                        }
                    });

                    if (!string.IsNullOrEmpty(sourceRealPath))
                    {
                        FileLogger.Log("COPY", "Jalur nyata: hitung + salin via System.IO");
                        await RealPathCopier.CopyDirectoryAsync(sourceRealPath, destPath, progress, token);
                    }
                    else
                    {
                        FileLogger.Log("COPY", "Jalur SAF: hitung + salin via DocumentFile");
                        var treeUri = global::Android.Net.Uri.Parse(sourceUri);
                        await GameFolderManager.CopyTreeAsync(ctx, treeUri, destPath, progress, token);
                    }

                    // Validasi hasil salinan sebelum dinyatakan selesai.
                    var check = GameFolderValidator.Validate(destPath);
                    if (!check.IsValid)
                    {
                        throw new IOException("Salinan tidak lengkap, file ini hilang:\n- "
                            + string.Join("\n- ", check.MissingFiles));
                    }

                    state.Status = CopyJobStatus.Complete;
                    state.TotalFiles = LastProgress?.TotalFiles ?? state.TotalFiles;
                    state.CopiedFiles = state.TotalFiles;
                    state.TotalBytes = LastProgress?.TotalBytes ?? state.TotalBytes;
                    state.CopiedBytes = state.TotalBytes;
                    CopyStateStore.Save(ctx, state);
                    GameFolderManager.SaveGameFolder(ctx, destPath, sourceUri);
                    FileLogger.Log("COPY", "Penyalinan SELESAI: " + destPath);
                    ShowFinishedNotification("Penyalinan selesai",
                        "File game sudah tersalin. Ketuk untuk membuka game.");
                    CopyFinished?.Invoke(state);
                }
                catch (System.OperationCanceledException)
                {
                    state.Status = CopyJobStatus.Cancelled;
                    CopyStateStore.Save(ctx, state);
                    FileLogger.Log("COPY", "Penyalinan DIBATALKAN user pada "
                        + state.CopiedFiles + "/" + state.TotalFiles + " file");
                    ShowFinishedNotification("Penyalinan dibatalkan",
                        "Ketuk untuk melanjutkan penyalinan di aplikasi.");
                    CopyFinished?.Invoke(state);
                }
                catch (Exception e)
                {
                    state.Status = CopyJobStatus.Failed;
                    state.LastError = e.Message;
                    CopyStateStore.Save(ctx, state);
                    FileLogger.LogError("COPY", e);
                    ShowFinishedNotification("Penyalinan gagal", e.Message);
                    CopyFinished?.Invoke(state);
                }
                finally
                {
                    lock (_startLock) { IsRunning = false; }
                    try
                    {
                        if (Build.VERSION.SdkInt >= BuildVersionCodes.N)
                            StopForeground(StopForegroundFlags.Remove);
                        else
#pragma warning disable CS0618
                            StopForeground(true);
#pragma warning restore CS0618
                    }
                    catch (Exception) { }
                    StopSelf();
                }
            }, token);
        }

        private void UpdateNotification(GameFolderCopyProgress p)
        {
            try
            {
                string title, text;
                bool indeterminate = false;
                if (p.Phase == CopyPhase.Counting)
                {
                    title = "Menghitung file game...";
                    text = p.CountedFiles + " file ditemukan";
                    indeterminate = true;
                }
                else
                {
                    title = "Menyalin file game... " + p.Percent + "%";
                    text = p.CurrentFile;
                    if (p.EstimatedRemaining.HasValue)
                        text += " — sisa ~" + CopyEstimator.FormatDuration(p.EstimatedRemaining.Value);
                }
                var notification = BuildNotification(title, text, p.Percent, indeterminate, showCancel: true);
                var manager = (NotificationManager)GetSystemService(NotificationService);
                manager.Notify(NotificationId, notification);
            }
            catch (Exception e)
            {
                FileLogger.Log("NOTIF", "Update notifikasi gagal: " + e.Message);
            }
        }

        private void ShowFinishedNotification(string title, string text)
        {
            try
            {
                var notification = BuildNotification(title, text, 100, false, showCancel: false);
                var manager = (NotificationManager)GetSystemService(NotificationService);
                manager.Notify(NotificationId, notification);
            }
            catch (Exception e)
            {
                FileLogger.Log("NOTIF", "Notifikasi selesai gagal: " + e.Message);
            }
        }

        private Notification BuildNotification(
            string title, string text, int percent, bool indeterminate, bool showCancel)
        {
            var contentIntent = PackageManager.GetLaunchIntentForPackage(PackageName);
            var contentPending = PendingIntent.GetActivity(
                this, RequestContent, contentIntent,
                PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

            var builder = new Notification.Builder(this, ChannelId)
                .SetContentTitle(title)
                .SetContentText(text)
                .SetSmallIcon(global::Android.Resource.Drawable.StatSysDownload)
                .SetContentIntent(contentPending)
                .SetOngoing(showCancel)
                .SetOnlyAlertOnce(true);
            if (indeterminate) builder.SetProgress(0, 0, true);
            else builder.SetProgress(100, percent, false);

            if (showCancel)
            {
                var cancelIntent = new Intent(this, typeof(CopyForegroundService));
                cancelIntent.PutExtra(ExtraAction, ActionCancel);
                var cancelPending = PendingIntent.GetService(
                    this, RequestCancel, cancelIntent,
                    PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
                builder.AddAction(
                    new Notification.Action.Builder(null, "Batal", cancelPending).Build());
            }
            else
            {
                var resumeIntent = new Intent(this, typeof(CopyForegroundService));
                resumeIntent.PutExtra(ExtraAction, ActionResume);
                var resumePending = PendingIntent.GetService(
                    this, RequestResume, resumeIntent,
                    PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
                builder.AddAction(
                    new Notification.Action.Builder(null, "Lanjutkan", resumePending).Build());
            }
            return builder.Build();
        }

        private void CreateChannel()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
            var channel = new NotificationChannel(
                ChannelId, "Penyalinan Game", NotificationImportance.Low)
            {
                Description = "Progres penyalinan file game The Sims 1",
            };
            channel.SetSound(null, null);
            var manager = (NotificationManager)GetSystemService(NotificationService);
            manager.CreateNotificationChannel(channel);
        }
    }
}
