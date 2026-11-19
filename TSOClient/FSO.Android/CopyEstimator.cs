using System;

namespace FSO.Android
{
    /// <summary>
    /// Menghitung kecepatan salin + estimasi sisa waktu dari sampel
    /// (waktu, byte tersalin). Logika murni (tanpa API Android) supaya
    /// bisa diuji di luar HP.
    ///
    /// Kecepatan = byte tersalin / detik sejak mulai. Estimasi = sisa byte /
    /// kecepatan. Estimasi baru dianggap bisa dipercaya bila sudah ada cukup
    /// data (minimal 5 detik dan 1 MB tersalin); sebelum itu
    /// <see cref="EstimatedRemaining"/> bernilai null ("Menghitung estimasi...").
    /// </summary>
    public class CopyEstimator
    {
        private const double MinSecondsForEstimate = 5.0;
        private const long MinBytesForEstimate = 1024 * 1024;

        private readonly DateTime _startedUtc;
        private DateTime _lastSampleUtc;
        private long _lastBytes;

        public CopyEstimator()
        {
            _startedUtc = DateTime.UtcNow;
            _lastSampleUtc = _startedUtc;
            _lastBytes = 0;
        }

        /// <summary>Catat progres terbaru (dipanggil tiap file selesai disalin).</summary>
        public void AddSample(long copiedBytes)
        {
            _lastSampleUtc = DateTime.UtcNow;
            _lastBytes = copiedBytes;
        }

        /// <summary>Kecepatan rata-rata sejak mulai (byte/detik). 0 bila belum ada data.</summary>
        public double SpeedBytesPerSec
        {
            get
            {
                var elapsed = (_lastSampleUtc - _startedUtc).TotalSeconds;
                if (elapsed <= 0 || _lastBytes <= 0) return 0;
                return _lastBytes / elapsed;
            }
        }

        /// <summary>
        /// Estimasi sisa waktu, atau null bila belum cukup data untuk estimasi
        /// yang jujur (jangan tampilkan angka palsu).
        /// </summary>
        public TimeSpan? EstimatedRemaining(long totalBytes)
        {
            var elapsed = (_lastSampleUtc - _startedUtc).TotalSeconds;
            if (elapsed < MinSecondsForEstimate || _lastBytes < MinBytesForEstimate) return null;
            var speed = SpeedBytesPerSec;
            if (speed <= 0) return null;
            var remaining = totalBytes - _lastBytes;
            if (remaining <= 0) return TimeSpan.Zero;
            return TimeSpan.FromSeconds(remaining / speed);
        }

        /// <summary>Format "4 menit 12 detik" / "37 detik" / "1 jam 3 menit".</summary>
        public static string FormatDuration(TimeSpan span)
        {
            if (span.TotalSeconds < 60) return (int)Math.Max(1, span.TotalSeconds) + " detik";
            if (span.TotalHours < 1) return (int)span.TotalMinutes + " menit " + span.Seconds + " detik";
            return (int)span.TotalHours + " jam " + span.Minutes + " menit";
        }

        /// <summary>Format "12,4 MB/detik" / "856 KB/detik".</summary>
        public static string FormatSpeed(double bytesPerSec)
        {
            if (bytesPerSec >= 1024 * 1024)
                return (bytesPerSec / (1024 * 1024)).ToString("0.0") + " MB/detik";
            if (bytesPerSec >= 1024)
                return (bytesPerSec / 1024).ToString("0") + " KB/detik";
            return ((long)bytesPerSec) + " B/detik";
        }

        /// <summary>Format "45 MB / 2,8 GB".</summary>
        public static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024)
                return (bytes / (1024.0 * 1024 * 1024)).ToString("0.0") + " GB";
            if (bytes >= 1024 * 1024)
                return (bytes / (1024.0 * 1024)).ToString("0") + " MB";
            if (bytes >= 1024)
                return (bytes / 1024.0).ToString("0") + " KB";
            return bytes + " B";
        }
    }
}
