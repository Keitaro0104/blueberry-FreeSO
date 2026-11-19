using System;

namespace FSO.Android
{
    /// <summary>
    /// State machine penghitung crash beruntun (logika murni, tanpa API Android,
    /// supaya bisa diuji di luar HP).
    ///
    /// Aturan:
    /// - Tiap kali game akan dijalankan, hitung naik 1 — dianggap crash sampai
    ///   terbukti sebaliknya (NoteLaunch dipanggil TEPAT SEBELUM RunGame).
    /// - Bila game bertahan 20 detik (timer heartbeat) atau keluar normal,
    ///   hitung direset ke 0 (NoteSurvived / NoteCleanExit).
    /// - Bila hitung &gt;= 1 saat launcher dibuka, tampilkan spanduk peringatan —
    ///   BUKAN boot-loop: tombol Play tetap tersedia ("Coba lagi").
    ///
    /// Nilai hitung disimpan di SharedPreferences oleh GameCrashTracker.
    /// </summary>
    public class CrashCounter
    {
        public int Count { get; private set; }

        public CrashCounter(int initialCount = 0)
        {
            Count = Math.Max(0, initialCount);
        }

        /// <summary>Game akan dijalankan — anggap crash sampai terbukti sebaliknya.</summary>
        public void NoteLaunch()
        {
            Count++;
        }

        /// <summary>Game bertahan cukup lama (20 detik) — bukan crash startup.</summary>
        public void NoteSurvived()
        {
            Count = 0;
        }

        /// <summary>Game keluar normal (Back / keluar dari game).</summary>
        public void NoteCleanExit()
        {
            Count = 0;
        }

        /// <summary>True bila game terakhir kali (atau beberapa kali) crash.</summary>
        public bool ShouldWarn => Count >= 1;
    }
}
