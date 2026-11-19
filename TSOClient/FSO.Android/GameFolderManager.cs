using Android.App;
using Android.Content;
using Android.OS;
using Android.Provider;
using AndroidX.DocumentFile.Provider;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FSO.Android
{
    /// <summary>
    /// Menyimpan pilihan folder game + menyalin folder game ke storage aplikasi
    /// atau folder tujuan lain.
    ///
    /// DUA JALUR (perbaikan Langkah 5.1):
    ///
    /// 1. JALUR NYATA (cepat, diutamakan): bila folder yang dipilih user bisa
    ///    diurai menjadi jalur file nyata — mis. folder di penyimpanan bersama
    ///    seperti /storage/emulated/0/IDM/Download/The Sims — penyalinan memakai
    ///    System.IO biasa (RealPathCopier). Directory.EnumerateFiles menghitung
    ///    ribuan file dalam hitungan detik, sedangkan SAF DocumentFile (jalur
    ///    lama Langkah 5) butuh satu query IPC per folder dan itulah yang membuat
    ///    penyalinan Langkah 5 mentok di 0% sebelum file pertama disalin
    ///    (folder tujuan tetap kosong — tidak ada file yang tersalin sama sekali).
    ///
    /// 2. JALUR SAF (fallback): bila jalur nyata tidak bisa diurai, penyalinan
    ///    tetap lewat DocumentFile seperti Langkah 5 — TAPI fase penghitungan
    ///    kini dilaporkan live ("Menghitung file... N file ditemukan") supaya
    ///    layar tidak diam di 0%, dan tiap tahap dicatat ke log .txt.
    ///
    /// 3. MODE LANGSUNG (tanpa menyalin): bila folder game sudah ada di jalur
    ///    nyata yang bisa dibaca engine (mis. di IDM/Download), user bisa
    ///    memilih "Pakai langsung" — tidak ada penyalinan 2–3 GB sama sekali.
    ///    Syaratnya: aplikasi harus punya izin "akses semua file"
    ///    (MANAGE_EXTERNAL_STORAGE, Android 11+), karena tanpa izin itu Android
    ///    melarang aplikasi membaca file .iff di penyimpanan bersama lewat
    ///    jalur langsung. Izin diminta lewat halaman Settings.
    ///
    /// Engine FreeSO membaca file game lewat System.IO biasa (jalur file
    /// sungguhan) — jadi folder tujuan HARUS berupa jalur nyata, bukan URI
    /// "content://" dari SAF. Itu sebabnya folder tujuan custom hanya diterima
    /// bila jalur nyatanya bisa diurai; bila tidak, aplikasi memakai storage
    /// aplikasi dan menjelaskan alasannya.
    /// </summary>
    public static class GameFolderManager
    {
        private const string PrefsName = "blueberry_freeso";
        private const string KeyGameFolderPath = "game_folder_path";
        private const string KeyGameFolderSourceUri = "game_folder_source_uri";

        /// <summary>
        /// Folder root game di storage khusus aplikasi — di sinilah folder "The Sims"
        /// (hasil salinan dari pemilih file, atau hasil adb push dari PC teman)
        /// harus berada. Isinya: GameData/, ExpansionShared/, UserData/, dst.
        /// </summary>
        public static string AppGameRoot(Context context)
        {
            var ext = context.GetExternalFilesDir(null);
            if (ext != null) return Path.Combine(ext.AbsolutePath, "The Sims");
            return Path.Combine(context.FilesDir.AbsolutePath, "The Sims");
        }

        /// <summary>Folder tujuan bawaan untuk salinan di penyimpanan bersama (Download).</summary>
        public static string SharedDownloadRoot()
        {
            return Path.Combine("/storage/emulated/0/Download", "BlueberryFreeSO", "The Sims");
        }

        /// <summary>Folder game yang tersimpan (sudah tervalidasi), atau null.</summary>
        public static string SavedGameFolder(Context context)
        {
            var prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
            var path = prefs.GetString(KeyGameFolderPath, null);
            if (string.IsNullOrEmpty(path)) return null;
            if (!GameFolderValidator.Validate(path).IsValid)
            {
                // Folder tersimpan rusak/dihapus — lupakan supaya setup diulang.
                prefs.Edit().Remove(KeyGameFolderPath).Apply();
                return null;
            }
            return path;
        }

        public static void SaveGameFolder(Context context, string path, string sourceUri = null)
        {
            var editor = context.GetSharedPreferences(PrefsName, FileCreationMode.Private).Edit();
            editor.PutString(KeyGameFolderPath, path);
            if (sourceUri != null) editor.PutString(KeyGameFolderSourceUri, sourceUri);
            editor.Apply();
        }

        // ------------------------------------------------------------------
        // Jalur nyata dari URI SAF
        // ------------------------------------------------------------------

        /// <summary>
        /// Urai tree URI SAF menjadi jalur file nyata (mis. "primary:IDM/Download/The Sims"
        /// → "/storage/emulated/0/IDM/Download/The Sims"). Null bila tidak bisa diurai.
        /// </summary>
        public static string ResolveRealPath(Context context, global::Android.Net.Uri treeUri)
        {
            try
            {
                var docId = DocumentsContract.GetTreeDocumentId(treeUri);
                var real = SafPathDecoder.TryDecodeTreeDocumentId(docId);
                FileLogger.Log("SAF", "Tree URI " + treeUri + " → docId " + docId + " → " + (real ?? "(tidak bisa diurai)"));
                return real;
            }
            catch (Exception e)
            {
                FileLogger.LogError("SAF", e);
                return null;
            }
        }

        /// <summary>
        /// True bila aplikasi boleh membaca file apa pun lewat jalur langsung
        /// (Android 11+: MANAGE_EXTERNAL_STORAGE; Android lama: READ_EXTERNAL_STORAGE).
        /// </summary>
        public static bool HasAllFilesAccess(Context context)
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            {
                return global::Android.OS.Environment.IsExternalStorageManager;
            }
            var permission = global::Android.Content.PM.Permission.Granted ==
                context.CheckSelfPermission(global::Android.Manifest.Permission.ReadExternalStorage);
            return permission;
        }

        /// <summary>
        /// Minta izin "akses semua file": Android 11+ membuka halaman Settings
        /// aplikasi (user menyalakan "Izinkan pengelolaan semua file"); Android
        /// lama meminta izin baca storage biasa. Hasilnya dicek di OnResume /
        /// saat user kembali — tidak ada callback langsung dari Settings.
        /// </summary>
        public static void RequestAllFilesAccess(Activity activity)
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            {
                var intent = new Intent(Settings.ActionManageAppAllFilesAccessPermission);
                intent.SetData(global::Android.Net.Uri.Parse("package:" + activity.PackageName));
                activity.StartActivity(intent);
            }
            else
            {
                activity.RequestPermissions(
                    new[] { global::Android.Manifest.Permission.ReadExternalStorage }, 2001);
            }
        }

        /// <summary>
        /// Coba pakai folder game langsung di tempatnya (tanpa menyalin).
        /// Mengembalikan (path, error): path terisi bila berhasil; bila gagal,
        /// error menjelaskan kenapa (jalur tidak bisa diurai / tidak valid /
        /// izin akses semua file belum diberikan).
        /// </summary>
        public static (string path, string error) TryUseDirectly(Context context, global::Android.Net.Uri treeUri)
        {
            FileLogger.Log("DIRECT", "Mencoba pakai folder langsung dari " + treeUri);
            var real = ResolveRealPath(context, treeUri);
            if (real == null)
            {
                return (null, "Jalur nyata folder ini tidak bisa dibaca dari pemilih file "
                    + "(bukan penyimpanan utama). Pilih \"Salin\" supaya aplikasi menyalin "
                    + "isinya ke storage aplikasi.");
            }
            if (!HasAllFilesAccess(context))
            {
                return (null, "Butuh izin \"akses semua file\" supaya aplikasi bisa membaca "
                    + "file game di " + real + " tanpa menyalin. Nyalakan izinnya di Settings, "
                    + "lalu pilih folder ini lagi — atau pilih \"Salin\" bila tidak mau "
                    + "memberi izin.");
            }
            var check = GameFolderValidator.Validate(real);
            if (!check.IsValid)
            {
                return (null, "Folder tidak valid di jalur nyata:\n- "
                    + string.Join("\n- ", check.MissingFiles));
            }
            FileLogger.Log("DIRECT", "Folder langsung OK: " + real);
            return (real, null);
        }

        // ------------------------------------------------------------------
        // Validasi pohon SAF (tanpa menyalin dulu)
        // ------------------------------------------------------------------

        /// <summary>
        /// Memvalidasi pohon SAF yang dipilih user (tanpa menyalin dulu).
        /// Mengembalikan hasil validasi yang sama seperti GameFolderValidator,
        /// tapi bekerja lewat DocumentFile (URI), bukan jalur file.
        /// </summary>
        public static GameFolderValidation ValidateTree(Context context, global::Android.Net.Uri treeUri)
        {
            var result = new GameFolderValidation();
            var root = DocumentFile.FromTreeUri(context, treeUri);
            if (root == null || !root.Exists())
            {
                result.MissingFiles.AddRange(GameFolderValidator.RequiredFiles);
                return result;
            }

            foreach (var relative in GameFolderValidator.RequiredFiles)
            {
                if (FindInTree(root, relative) == null) result.MissingFiles.Add(relative);
            }

            // Petunjuk bila user malah memilih folder GameData itu sendiri.
            if (result.MissingFiles.Count == GameFolderValidator.RequiredFiles.Length
                && FindInTree(root, "Behavior.iff") != null)
            {
                result.MissingFiles.Clear();
                result.MissingFiles.Add("(kamu memilih folder \"GameData\" — pilih folder DI ATASNYA, "
                    + "yaitu folder instalasi The Sims yang berisi folder \"GameData\")");
            }

            foreach (var child in SafeListFiles(root))
            {
                if (!child.IsDirectory) continue;
                result.TopLevelFolders.Add(child.Name);
                if (child.Name.IndexOf("Expansion", StringComparison.OrdinalIgnoreCase) >= 0)
                    result.HasExpansionFolders = true;
            }

            result.IsValid = result.MissingFiles.Count == 0;
            return result;
        }

        // ------------------------------------------------------------------
        // Penyalinan lewat SAF (fallback) — kini dengan progress fase Counting
        // ------------------------------------------------------------------

        /// <summary>
        /// Menghitung jumlah file + byte di pohon SAF (untuk total progress).
        /// Versi lama (Langkah 5) tidak melaporkan apa-apa selama menghitung —
        /// layar diam di 0% sampai hitung selesai. Versi ini melaporkan jumlah
        /// file yang sudah ditemukan setiap 100 file.
        /// </summary>
        public static void CountTree(DocumentFile dir, ref long files, ref long bytes)
        {
            CountTree(dir, ref files, ref bytes, null);
        }

        public static void CountTree(DocumentFile dir, ref long files, ref long bytes,
            IProgress<GameFolderCopyProgress> progress)
        {
            foreach (var child in SafeListFiles(dir))
            {
                if (child.IsDirectory) CountTree(child, ref files, ref bytes, progress);
                else if (child.IsFile)
                {
                    files++;
                    bytes += Math.Max(0, child.Length());
                    if (progress != null && files % 100 == 0)
                    {
                        progress.Report(new GameFolderCopyProgress
                        {
                            Phase = CopyPhase.Counting,
                            CountedFiles = files,
                            CountedBytes = bytes,
                        });
                    }
                }
            }
        }

        /// <summary>
        /// Menyalin seluruh isi pohon SAF ke folder tujuan di disk.
        /// File tujuan yang sudah ada dengan ukuran sama dilewati (supaya penyalinan
        /// yang terputus bisa dilanjutkan tanpa mengulang dari nol).
        /// Setiap tahap dicatat ke log .txt — bila macet di 0% lagi, log akan
        /// menunjukkan tahap terakhir yang berhasil.
        /// </summary>
        public static async Task CopyTreeAsync(
            Context context, global::Android.Net.Uri treeUri, string destDir,
            IProgress<GameFolderCopyProgress> progress, CancellationToken token)
        {
            var root = DocumentFile.FromTreeUri(context, treeUri)
                ?? throw new IOException("Tidak bisa membuka folder yang dipilih.");
            var totals = new GameFolderCopyProgress { Phase = CopyPhase.Counting };
            FileLogger.Log("COPY", "Mulai hitung file (SAF) dari " + treeUri);
            var started = DateTime.UtcNow;
            long totalFiles = 0, totalBytes = 0;
            await Task.Run(() => CountTree(root, ref totalFiles, ref totalBytes, progress), token);
            totals.TotalFiles = totalFiles;
            totals.TotalBytes = totalBytes;
            totals.CountedFiles = totalFiles;
            totals.CountedBytes = totalBytes;
            FileLogger.Log("COPY", "Hitung selesai: " + totalFiles + " file, "
                + CopyEstimator.FormatBytes(totalBytes) + " dalam "
                + (DateTime.UtcNow - started).TotalSeconds.ToString("0.0") + " detik");
            if (totalFiles == 0)
            {
                // 0 file dari folder yang lolos validasi = ada yang salah di
                // enumerasi SAF. Jangan lanjut diam-diam — lempar error yang jelas.
                throw new IOException("Penghitungan file menghasilkan 0 file, padahal folder "
                    + "lolos validasi. Kemungkinan pemilih file tidak memberi akses baca "
                    + "ke isi folder. Coba pilih folder lagi, atau pakai cara salin lain.");
            }
            totals.Phase = CopyPhase.Copying;
            progress?.Report(Clone(totals));

            await Task.Run(() =>
            {
                Directory.CreateDirectory(destDir);
                CopyRecursive(context, root, destDir, totals, progress, token);
            }, token);
            FileLogger.Log("COPY", "Salin SAF selesai: " + totals.CopiedFiles + " file, "
                + CopyEstimator.FormatBytes(totals.CopiedBytes));
        }

        private static void CopyRecursive(
            Context context, DocumentFile dir, string destDir,
            GameFolderCopyProgress totals, IProgress<GameFolderCopyProgress> progress,
            CancellationToken token)
        {
            var estimator = new CopyEstimator();
            CopyRecursiveInner(context, dir, destDir, totals, estimator, progress, token);
        }

        private static void CopyRecursiveInner(
            Context context, DocumentFile dir, string destDir,
            GameFolderCopyProgress totals, CopyEstimator estimator,
            IProgress<GameFolderCopyProgress> progress, CancellationToken token)
        {
            foreach (var child in SafeListFiles(dir))
            {
                token.ThrowIfCancellationRequested();
                var dest = Path.Combine(destDir, child.Name);
                if (child.IsDirectory)
                {
                    Directory.CreateDirectory(dest);
                    CopyRecursiveInner(context, child, dest, totals, estimator, progress, token);
                }
                else if (child.IsFile)
                {
                    totals.CurrentFile = child.Name;
                    var length = Math.Max(0, child.Length());
                    // Lewati file yang sudah tersalin utuh (lanjutkan salinan terputus).
                    if (File.Exists(dest) && new FileInfo(dest).Length == length)
                    {
                        totals.CopiedFiles++;
                        totals.CopiedBytes += length;
                        estimator.AddSample(totals.CopiedBytes);
                        totals.SpeedBytesPerSec = estimator.SpeedBytesPerSec;
                        totals.EstimatedRemaining = estimator.EstimatedRemaining(totals.TotalBytes);
                        progress?.Report(Clone(totals));
                        continue;
                    }
                    using (var input = context.ContentResolver.OpenInputStream(child.Uri))
                    using (var output = File.OpenWrite(dest))
                    {
                        if (input == null) throw new IOException("Tidak bisa membaca file " + child.Name);
                        input.CopyTo(output);
                    }
                    totals.CopiedFiles++;
                    totals.CopiedBytes += length;
                    estimator.AddSample(totals.CopiedBytes);
                    totals.SpeedBytesPerSec = estimator.SpeedBytesPerSec;
                    totals.EstimatedRemaining = estimator.EstimatedRemaining(totals.TotalBytes);
                    progress?.Report(Clone(totals));
                }
            }
        }

        /// <summary>Mencari file di pohon SAF berdasarkan jalur relatif ("GameData/UIText.iff").</summary>
        public static DocumentFile FindInTree(DocumentFile root, string relativePath)
        {
            var current = root;
            foreach (var part in relativePath.Split('/'))
            {
                if (current == null) return null;
                current = current.FindFile(part);
            }
            return (current != null && current.Exists()) ? current : null;
        }

        private static DocumentFile[] SafeListFiles(DocumentFile dir)
        {
            try { return dir.ListFiles() ?? Array.Empty<DocumentFile>(); }
            catch (Exception e)
            {
                // Dulu (Langkah 5) error ini ditelan diam-diam — folder yang
                // gagal dibaca hilang dari hitungan/salinan tanpa jejak.
                // Sekarang dicatat ke log .txt supaya titik macet terlihat.
                FileLogger.Log("SAF", "ListFiles gagal di " + dir?.Name + ": " + e.Message);
                return Array.Empty<DocumentFile>();
            }
        }

        /// <summary>Salinan snapshot progress (supaya UI tidak membaca objek yang sedang diubah thread latar).</summary>
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
}
}
