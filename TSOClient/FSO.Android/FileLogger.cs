using System;
using System.IO;
using System.Text;

namespace FSO.Android
{
    /// <summary>
    /// Logger ke file .txt yang bisa dibuka/dibagikan user.
    ///
    /// File: {GetExternalFilesDir}/BlueberryFreeSO-log.txt
    /// (mis. /storage/emulated/0/Android/data/com.keitaro0104.blueberryfreeso/files/BlueberryFreeSO-log.txt).
    /// Lokasi ini selalu bisa ditulis aplikasi tanpa izin tambahan. Supaya user
    /// bisa mengambil file-nya tanpa masuk ke android/data, layar setup punya
    /// tombol "Lihat Log" (tampil di layar) + "Simpan Log (.txt)" (pemilih file
    /// sistem — user memilih sendiri ke mana file .txt disimpan, mis. Download).
    ///
    /// Yang dicatat: waktu mulai/selesai tiap tahap (pilih folder, validasi,
    /// hitung file, salin), nama file yang sedang disalin, dan error lengkap
    /// (pesan + stack trace). Crash (uncaught exception) juga ditulis ke sini
    /// sebelum aplikasi menutup — dipasang di Activity1.OnCreate.
    ///
    /// Thread-safe (satu lock), dan kegagalan menulis log tidak pernah
    /// melempar exception ke pemanggil (log tidak boleh mematikan aplikasi).
    /// </summary>
    public static partial class FileLogger
    {
        public const string LogFileName = "BlueberryFreeSO-log.txt";

        private static readonly object Lock = new object();
        private static string _logPath;

        /// <summary>Pasang jalur file log (dipanggil sekali dari Activity).</summary>
        public static void Init(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                _logPath = Path.Combine(directory, LogFileName);
            }
            catch (Exception)
            {
                _logPath = null;
            }
        }

        public static string LogPath => _logPath;

        /// <summary>Tulis satu baris log: "2026-11-19 19:30:01 [SETUP] pesan".</summary>
        public static void Log(string tag, string message)
        {
            if (_logPath == null) return;
            try
            {
                var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    + " [" + tag + "] " + message + Environment.NewLine;
                lock (Lock) File.AppendAllText(_logPath, line, Encoding.UTF8);
            }
            catch (Exception)
            {
                // Log gagal ditulis — jangan crash karena log.
            }
        }

        /// <summary>Tulis error lengkap: pesan + stack trace (bila ada).</summary>
        public static void LogError(string tag, Exception e)
        {
            Log(tag, "ERROR: " + e.GetType().Name + ": " + e.Message);
            if (!string.IsNullOrEmpty(e.StackTrace)) Log(tag, e.StackTrace);
            if (e.InnerException != null) LogError(tag, e.InnerException);
        }

        /// <summary>Tulis crash (uncaught exception) — dipanggil handler crash.</summary>
        public static void LogCrash(string source, object exceptionObject)
        {
            Log("CRASH", "Aplikasi crash (" + source + "):");
            if (exceptionObject is Exception e) LogError("CRASH", e);
            else Log("CRASH", exceptionObject?.ToString() ?? "(tidak ada detail)");
        }

        /// <summary>Baca N karakter terakhir file log (untuk dialog "Lihat Log").</summary>
        public static string ReadTail(int maxChars)
        {
            if (_logPath == null || !File.Exists(_logPath)) return "(belum ada log)";
            try
            {
                lock (Lock)
                {
                    using var stream = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    if (stream.Length <= maxChars)
                    {
                        using var reader = new StreamReader(stream, Encoding.UTF8);
                        return reader.ReadToEnd();
                    }
                    stream.Seek(-maxChars, SeekOrigin.End);
                    using var tailReader = new StreamReader(stream, Encoding.UTF8);
                    return "...\n" + tailReader.ReadToEnd();
                }
            }
            catch (Exception e)
            {
                return "(tidak bisa membaca log: " + e.Message + ")";
            }
        }
    }
}
