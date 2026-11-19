using Android.Content;
using System;

namespace FSO.Android
{
    /// <summary>Cara folder game dipakai: disalin, atau dipakai langsung di tempatnya.</summary>
    public enum GameFolderMode
    {
        /// <summary>Folder game disalin ke storage aplikasi / folder tujuan.</summary>
        Copy,
        /// <summary>Folder game dipakai langsung di jalurnya (tanpa menyalin).</summary>
        Direct,
    }

    /// <summary>Status penyalinan yang tersimpan (bertahan walau aplikasi ditutup).</summary>
    public enum CopyJobStatus
    {
        None,
        Counting,
        Copying,
        Cancelled,
        Complete,
        Failed,
    }

    /// <summary>
    /// State pekerjaan penyalinan folder game, disimpan di SharedPreferences
    /// supaya tombol "Lanjutkan" bekerja bahkan setelah aplikasi ditutup dan
    /// dibuka lagi, dan supaya Activity tahu hasil penyalinan yang selesai
    /// di latar belakang (foreground service).
    ///
    /// Resume bekerja karena penyalinan melewati file tujuan yang sudah ada
    /// dengan ukuran sama (RealPathCopier + GameFolderManager.CopyTreeAsync).
    /// </summary>
    public class CopyState
    {
        public CopyJobStatus Status { get; set; } = CopyJobStatus.None;
        public GameFolderMode Mode { get; set; } = GameFolderMode.Copy;
        /// <summary>URI SAF folder sumber (content://...), bila dipilih lewat pemilih folder.</summary>
        public string SourceUri { get; set; }
        /// <summary>Jalur nyata folder sumber, bila bisa diurai dari URI SAF.</summary>
        public string SourceRealPath { get; set; }
        /// <summary>Jalur nyata folder tujuan (atau folder game bila mode Direct).</summary>
        public string DestPath { get; set; }
        public long TotalFiles { get; set; }
        public long CopiedFiles { get; set; }
        public long TotalBytes { get; set; }
        public long CopiedBytes { get; set; }
        public string LastError { get; set; }
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>True bila pekerjaan ini bisa dilanjutkan (pernah mulai, belum selesai).</summary>
        public bool CanResume =>
            (Status == CopyJobStatus.Cancelled || Status == CopyJobStatus.Failed)
            && Mode == GameFolderMode.Copy
            && !string.IsNullOrEmpty(DestPath)
            && (!string.IsNullOrEmpty(SourceUri) || !string.IsNullOrEmpty(SourceRealPath));
    }

    /// <summary>Penyimpanan CopyState di SharedPreferences.</summary>
    public static class CopyStateStore
    {
        private const string PrefsName = "blueberry_freeso_copy";
        private const string KStatus = "status";
        private const string KMode = "mode";
        private const string KSourceUri = "source_uri";
        private const string KSourceRealPath = "source_real_path";
        private const string KDestPath = "dest_path";
        private const string KTotalFiles = "total_files";
        private const string KCopiedFiles = "copied_files";
        private const string KTotalBytes = "total_bytes";
        private const string KCopiedBytes = "copied_bytes";
        private const string KLastError = "last_error";
        private const string KUpdatedUtc = "updated_utc";

        public static CopyState Load(Context context)
        {
            var prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
            var state = new CopyState();
            if (!Enum.TryParse(prefs.GetString(KStatus, "None"), out CopyJobStatus status)) status = CopyJobStatus.None;
            state.Status = status;
            if (!Enum.TryParse(prefs.GetString(KMode, "Copy"), out GameFolderMode mode)) mode = GameFolderMode.Copy;
            state.Mode = mode;
            state.SourceUri = prefs.GetString(KSourceUri, null);
            state.SourceRealPath = prefs.GetString(KSourceRealPath, null);
            state.DestPath = prefs.GetString(KDestPath, null);
            state.TotalFiles = prefs.GetLong(KTotalFiles, 0);
            state.CopiedFiles = prefs.GetLong(KCopiedFiles, 0);
            state.TotalBytes = prefs.GetLong(KTotalBytes, 0);
            state.CopiedBytes = prefs.GetLong(KCopiedBytes, 0);
            state.LastError = prefs.GetString(KLastError, null);
            long ticks = prefs.GetLong(KUpdatedUtc, 0);
            state.UpdatedUtc = ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : DateTime.UtcNow;
            return state;
        }

        public static void Save(Context context, CopyState state)
        {
            state.UpdatedUtc = DateTime.UtcNow;
            var editor = context.GetSharedPreferences(PrefsName, FileCreationMode.Private).Edit();
            editor.PutString(KStatus, state.Status.ToString());
            editor.PutString(KMode, state.Mode.ToString());
            editor.PutString(KSourceUri, state.SourceUri);
            editor.PutString(KSourceRealPath, state.SourceRealPath);
            editor.PutString(KDestPath, state.DestPath);
            editor.PutLong(KTotalFiles, state.TotalFiles);
            editor.PutLong(KCopiedFiles, state.CopiedFiles);
            editor.PutLong(KTotalBytes, state.TotalBytes);
            editor.PutLong(KCopiedBytes, state.CopiedBytes);
            editor.PutString(KLastError, state.LastError);
            editor.PutLong(KUpdatedUtc, state.UpdatedUtc.Ticks);
            editor.Apply();
        }

        public static void Clear(Context context)
        {
            context.GetSharedPreferences(PrefsName, FileCreationMode.Private).Edit().Clear().Apply();
        }
    }
}
