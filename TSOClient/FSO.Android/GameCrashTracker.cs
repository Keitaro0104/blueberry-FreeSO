using Android.Content;

namespace FSO.Android
{
    /// <summary>
    /// Penyimpanan CrashCounter di SharedPreferences "blueberry_freeso" — prefs
    /// yang sama dengan pilihan folder game (GameFolderManager), supaya semua
    /// state aplikasi ada di satu tempat.
    ///
    /// Dipakai Activity1: BeginLaunch tepat sebelum RunGame, MarkSurvived bila
    /// game bertahan 20 detik, MarkCleanExit bila game keluar normal.
    /// </summary>
    public static class GameCrashTracker
    {
        private const string PrefsName = "blueberry_freeso";
        private const string KeyCrashCount = "crash_count";

        /// <summary>Hitung crash beruntun saat ini (0 bila tidak pernah crash / sudah pulih).</summary>
        public static int CurrentCount(Context context)
        {
            var prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
            return prefs.GetInt(KeyCrashCount, 0);
        }

        /// <summary>True bila game terakhir kali crash (launcher harus menampilkan peringatan).</summary>
        public static bool HasCrashed(Context context)
        {
            return CurrentCount(context) >= 1;
        }

        /// <summary>
        /// Tambah 1 tepat sebelum RunGame dipanggil. Mengembalikan hitung baru.
        /// </summary>
        public static int BeginLaunch(Context context)
        {
            var counter = new CrashCounter(CurrentCount(context));
            counter.NoteLaunch();
            Save(context, counter.Count);
            return counter.Count;
        }

        /// <summary>Game bertahan 20 detik — bukan crash startup, reset ke 0.</summary>
        public static void MarkSurvived(Context context)
        {
            Save(context, 0);
        }

        /// <summary>
        /// Game keluar normal — reset ke 0. Disimpan SINKRON (Commit, bukan Apply)
        /// karena proses bisa langsung dimatikan sesudahnya (KillProcess saat
        /// keluar dari game) dan tulisan async bisa hilang.
        /// </summary>
        public static void MarkCleanExit(Context context)
        {
            var editor = context.GetSharedPreferences(PrefsName, FileCreationMode.Private).Edit();
            editor.PutInt(KeyCrashCount, 0);
            editor.Commit();
        }

        private static void Save(Context context, int count)
        {
            var editor = context.GetSharedPreferences(PrefsName, FileCreationMode.Private).Edit();
            editor.PutInt(KeyCrashCount, count);
            editor.Apply();
        }
    }
}
