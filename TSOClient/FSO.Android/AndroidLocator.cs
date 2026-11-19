using Android.Content;
using FSO.Client.Utils.GameLocator;
using System.IO;

namespace FSO.Android
{
    /// <summary>
    /// Mencari folder game di storage Android.
    /// Pengganti WindowsLocator (Registry + kernel32.dll) dan LinuxLocator untuk Android.
    /// Folder game mengikuti pola FSODroid lama: {storage aplikasi}/The Sims Online/TSOClient/
    /// dengan file penanda tuning.dat — sama seperti yang dicek FSOProgram di jalur PC.
    /// Catatan: ini masih layout TSO (The Sims Online). Layout file The Sims 1 (TS1Hybrid)
    /// diputuskan di Langkah 5 (file picker).
    /// </summary>
    public class AndroidLocator : ILocator
    {
        private readonly Context _context;

        public AndroidLocator(Context context)
        {
            _context = context;
        }

        /// <summary>
        /// Folder root game di storage aplikasi. Memakai GetExternalFilesDir (folder khusus
        /// aplikasi, tidak butuh izin storage) dengan fallback ke FilesDir internal.
        /// </summary>
        public static string GameRoot(Context context)
        {
            var ext = context.GetExternalFilesDir(null);
            if (ext != null) return Path.Combine(ext.AbsolutePath, "The Sims Online");
            return Path.Combine(context.FilesDir.AbsolutePath, "The Sims Online");
        }

        /// <summary>
        /// Mengembalikan folder TSOClient bila tuning.dat ditemukan di sana,
        /// atau null bila file game belum ada di HP (kasus normal sebelum Langkah 5).
        /// </summary>
        public string FindTheSimsOnline()
        {
            var clientDir = Path.Combine(GameRoot(_context), "TSOClient");
            if (File.Exists(Path.Combine(clientDir, "tuning.dat"))) return clientDir;
            return null;
        }
    }
}
