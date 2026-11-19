using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FSO.Android
{
    /// <summary>Fase penyalinan (dipakai progress bar + notifikasi + log).</summary>
    public enum CopyPhase
    {
        /// <summary>Menghitung total file/byte sebelum menyalin.</summary>
        Counting,
        /// <summary>Menyalin file.</summary>
        Copying,
        /// <summary>Selesai (semua file tersalin).</summary>
        Done,
        /// <summary>Dibatalkan user.</summary>
        Cancelled,
        /// <summary>Gagal (error).</summary>
        Failed,
    }

    /// <summary>
    /// Kemajuan penyalinan folder game (untuk layar setup + notifikasi).
    /// Menggantikan GameFolderCopyProgress lama (Langkah 5) dengan tambahan:
    /// fase (Counting/Copying), jumlah file yang sudah ditemukan saat menghitung,
    /// kecepatan + estimasi sisa waktu.
    /// </summary>
    public class GameFolderCopyProgress
    {
        public CopyPhase Phase { get; set; } = CopyPhase.Counting;
        public long TotalFiles { get; set; }
        public long CopiedFiles { get; set; }
        public long TotalBytes { get; set; }
        public long CopiedBytes { get; set; }
        /// <summary>File yang ditemukan sejauh ini selama fase Counting.</summary>
        public long CountedFiles { get; set; }
        public long CountedBytes { get; set; }
        /// <summary>Nama file yang sedang disalin (relatif, mis. "GameData/Global/Global.far").</summary>
        public string CurrentFile { get; set; } = "";
        /// <summary>Kecepatan salin (byte/detik), 0 bila belum bisa dihitung.</summary>
        public double SpeedBytesPerSec { get; set; }
        /// <summary>Estimasi sisa waktu, null bila belum cukup data (jangan tebak).</summary>
        public TimeSpan? EstimatedRemaining { get; set; }

        public int Percent => TotalBytes <= 0 ? 0 : (int)(CopiedBytes * 100 / TotalBytes);

        /// <summary>Teks satu baris untuk layar setup / notifikasi.</summary>
        public string StatusText
        {
            get
            {
                if (Phase == CopyPhase.Counting)
                {
                    return "Menghitung file... " + CountedFiles + " file ditemukan"
                        + (CountedBytes > 0 ? " (" + CopyEstimator.FormatBytes(CountedBytes) + ")" : "")
                        + "\nAplikasi sedang mendata isi folder game — ini bisa makan waktu "
                        + "bila foldernya besar. Bukan macet.";
                }
                var speed = SpeedBytesPerSec > 0
                    ? CopyEstimator.FormatSpeed(SpeedBytesPerSec) : "menghitung kecepatan...";
                var eta = EstimatedRemaining.HasValue
                    ? "sisa ~" + CopyEstimator.FormatDuration(EstimatedRemaining.Value)
                    : "Menghitung estimasi...";
                return "Menyalin: " + CurrentFile + "\n"
                    + CopiedFiles + "/" + TotalFiles + " file, "
                    + CopyEstimator.FormatBytes(CopiedBytes) + " / " + CopyEstimator.FormatBytes(TotalBytes)
                    + " (" + Percent + "%)\n" + speed + " — " + eta;
            }
        }
    }

    /// <summary>
    /// Menyalin folder game lewat jalur file nyata (System.IO) — dipakai bila
    /// folder sumber sudah ada di jalur nyata yang bisa dibaca aplikasi
    /// (mis. /storage/emulated/0/IDM/Download/The Sims) dan folder tujuan
    /// juga jalur nyata.
    ///
    /// Kenapa ini jauh lebih cepat daripada SAF DocumentFile (Langkah 5)?
    /// Directory.EnumerateFiles menghitung ribuan file dalam hitungan detik,
    /// sedangkan tiap ListFiles() SAF adalah satu query IPC yang lambat —
    /// itulah yang membuat penyalinan Langkah 5 mentok di 0% sebelum file
    /// pertama disalin (folder tujuan tetap kosong).
    ///
    /// Resume: file tujuan yang sudah ada dengan ukuran sama dilewati, jadi
    /// penyalinan yang dibatalkan bisa dilanjutkan tanpa mengulang dari nol.
    /// Logika murni System.IO (tanpa API Android) supaya bisa diuji di luar HP.
    /// </summary>
    public static class RealPathCopier
    {
        /// <summary>
        /// Salin folder sumber ke tujuan. Progress dilaporkan live: fase Counting
        /// (jumlah file yang sudah ditemukan) lalu Copying (nama file + ETA).
        /// </summary>
        public static async Task CopyDirectoryAsync(
            string sourceDir, string destDir,
            IProgress<GameFolderCopyProgress> progress, CancellationToken token)
        {
            if (!Directory.Exists(sourceDir))
                throw new DirectoryNotFoundException("Folder sumber tidak ditemukan: " + sourceDir);

            var state = new GameFolderCopyProgress { Phase = CopyPhase.Counting };
            // Hitung dulu total file/byte — cepat lewat System.IO. Laporkan
            // jumlah yang sudah ditemukan supaya layar tidak diam di 0%.
            await Task.Run(() =>
            {
                long files = 0, bytes = 0;
                foreach (var file in EnumerateFilesSafe(sourceDir))
                {
                    token.ThrowIfCancellationRequested();
                    files++;
                    try { bytes += new FileInfo(file).Length; } catch (Exception) { }
                    // Laporkan tiap 100 file (jangan spam UI tiap file).
                    if (files % 100 == 0)
                    {
                        state.CountedFiles = files;
                        state.CountedBytes = bytes;
                        progress?.Report(Clone(state));
                    }
                }
                state.TotalFiles = files;
                state.TotalBytes = bytes;
                state.CountedFiles = files;
                state.CountedBytes = bytes;
            }, token);

            state.Phase = CopyPhase.Copying;
            progress?.Report(Clone(state));
            var estimator = new CopyEstimator();

            await Task.Run(() =>
            {
                Directory.CreateDirectory(destDir);
                CopyRecursive(sourceDir, destDir, sourceDir, state, estimator, progress, token);
            }, token);

            state.Phase = CopyPhase.Done;
            state.CurrentFile = "";
            progress?.Report(Clone(state));
        }

        private static void CopyRecursive(
            string dir, string destDir, string rootDir,
            GameFolderCopyProgress state, CopyEstimator estimator,
            IProgress<GameFolderCopyProgress> progress, CancellationToken token)
        {
            foreach (var subDir in EnumerateDirectoriesSafe(dir))
            {
                token.ThrowIfCancellationRequested();
                var relative = GetRelative(rootDir, subDir);
                Directory.CreateDirectory(Path.Combine(destDir, relative));
                CopyRecursive(subDir, destDir, rootDir, state, estimator, progress, token);
            }

            foreach (var file in EnumerateFilesSafe(dir, topOnly: true))
            {
                token.ThrowIfCancellationRequested();
                var relative = GetRelative(rootDir, file);
                state.CurrentFile = relative;
                var dest = Path.Combine(destDir, relative);
                long length;
                try { length = new FileInfo(file).Length; } catch (Exception) { length = 0; }

                // Lewati file yang sudah tersalin utuh (lanjutkan salinan terputus).
                if (File.Exists(dest) && length > 0 && new FileInfo(dest).Length == length)
                {
                    state.CopiedFiles++;
                    state.CopiedBytes += length;
                    estimator.AddSample(state.CopiedBytes);
                    state.SpeedBytesPerSec = estimator.SpeedBytesPerSec;
                    state.EstimatedRemaining = estimator.EstimatedRemaining(state.TotalBytes);
                    progress?.Report(Clone(state));
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                using (var input = File.OpenRead(file))
                using (var output = File.OpenWrite(dest))
                {
                    input.CopyTo(output);
                }
                state.CopiedFiles++;
                state.CopiedBytes += length;
                estimator.AddSample(state.CopiedBytes);
                state.SpeedBytesPerSec = estimator.SpeedBytesPerSec;
                state.EstimatedRemaining = estimator.EstimatedRemaining(state.TotalBytes);
                progress?.Report(Clone(state));
            }
        }

        private static string GetRelative(string root, string full)
        {
            if (!root.EndsWith(Path.DirectorySeparatorChar.ToString()))
                root += Path.DirectorySeparatorChar;
            return full.StartsWith(root) ? full.Substring(root.Length) : Path.GetFileName(full);
        }

        private static GameFolderCopyProgress Clone(GameFolderCopyProgress p)
        {
            return new GameFolderCopyProgress
            {
                Phase = p.Phase,
                TotalFiles = p.TotalFiles,
                CopiedFiles = p.CopiedFiles,
                TotalBytes = p.TotalBytes,
                CopiedBytes = p.CopiedBytes,
                CountedFiles = p.CountedFiles,
                CountedBytes = p.CountedBytes,
                CurrentFile = p.CurrentFile,
                SpeedBytesPerSec = p.SpeedBytesPerSec,
                EstimatedRemaining = p.EstimatedRemaining,
            };
        }

        private static System.Collections.Generic.IEnumerable<string> EnumerateFilesSafe(string dir, bool topOnly = false)
        {
            try
            {
                // ToList() supaya enumerasi terjadi DI DALAM try — Directory.
                // EnumerateFiles malas (lazy): tanpa ini, error saat iterasi
                // (mis. subfolder tak bisa dibaca) akan lolos dari try/catch.
                return Directory.EnumerateFiles(dir, "*",
                    topOnly ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories)
                    .ToList();
            }
            catch (Exception)
            {
                return System.Linq.Enumerable.Empty<string>();
            }
        }

        private static System.Collections.Generic.IEnumerable<string> EnumerateDirectoriesSafe(string dir)
        {
            try { return Directory.EnumerateDirectories(dir).ToList(); }
            catch (Exception) { return System.Linq.Enumerable.Empty<string>(); }
        }
    }
}
