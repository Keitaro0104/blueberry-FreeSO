using Android.Content;
using AndroidX.DocumentFile.Provider;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FSO.Android
{
    /// <summary>
    /// Kemajuan penyalinan folder game (untuk progress bar di layar setup).
    /// </summary>
    public class GameFolderCopyProgress
    {
        public long TotalFiles { get; set; }
        public long CopiedFiles { get; set; }
        public long TotalBytes { get; set; }
        public long CopiedBytes { get; set; }
        public string CurrentFile { get; set; } = "";
        public int Percent => TotalBytes <= 0 ? 0 : (int)(CopiedBytes * 100 / TotalBytes);
    }

    /// <summary>
    /// Menyimpan pilihan folder game + menyalin folder game dari Storage Access
    /// Framework (SAF) ke storage khusus aplikasi.
    ///
    /// Kenapa harus disalin? Engine FreeSO membaca file game lewat System.IO biasa
    /// (jalur file sungguhan, mis. "GameData/UIText.iff"). Folder yang dipilih lewat
    /// pemilih file Android (ACTION_OPEN_DOCUMENT_TREE) hanya memberi URI
    /// "content://", bukan jalur file — engine tidak bisa membacanya. Jadi isi
    /// folder disalin sekali ke folder khusus aplikasi (GetExternalFilesDir),
    /// yang bisa dibaca sebagai jalur biasa dan tidak butuh izin storage luas.
    /// Pola yang sama dipakai davius (adb push ke /sdcard/Android/data/.../files/).
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

        /// <summary>
        /// Menghitung jumlah file + byte di pohon SAF (untuk total progress).
        /// </summary>
        public static void CountTree(DocumentFile dir, ref long files, ref long bytes)
        {
            foreach (var child in SafeListFiles(dir))
            {
                if (child.IsDirectory) CountTree(child, ref files, ref bytes);
                else if (child.IsFile) { files++; bytes += Math.Max(0, child.Length()); }
            }
        }

        /// <summary>
        /// Menyalin seluruh isi pohon SAF ke folder tujuan di disk.
        /// File tujuan yang sudah ada dengan ukuran sama dilewati (supaya penyalinan
        /// yang terputus bisa dilanjutkan tanpa mengulang dari nol).
        /// </summary>
        public static async Task CopyTreeAsync(
            Context context, global::Android.Net.Uri treeUri, string destDir,
            IProgress<GameFolderCopyProgress> progress, CancellationToken token)
        {
            var root = DocumentFile.FromTreeUri(context, treeUri)
                ?? throw new IOException("Tidak bisa membuka folder yang dipilih.");
            var totals = new GameFolderCopyProgress();
            long totalFiles = 0, totalBytes = 0;
            await Task.Run(() => CountTree(root, ref totalFiles, ref totalBytes), token);
            totals.TotalFiles = totalFiles;
            totals.TotalBytes = totalBytes;
            progress?.Report(totals);

            await Task.Run(() =>
            {
                Directory.CreateDirectory(destDir);
                CopyRecursive(context, root, destDir, totals, progress, token);
            }, token);
        }

        private static void CopyRecursive(
            Context context, DocumentFile dir, string destDir,
            GameFolderCopyProgress totals, IProgress<GameFolderCopyProgress> progress,
            CancellationToken token)
        {
            foreach (var child in SafeListFiles(dir))
            {
                token.ThrowIfCancellationRequested();
                var dest = Path.Combine(destDir, child.Name);
                if (child.IsDirectory)
                {
                    Directory.CreateDirectory(dest);
                    CopyRecursive(context, child, dest, totals, progress, token);
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
                        progress?.Report(totals);
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
                    progress?.Report(totals);
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
            catch (Exception) { return Array.Empty<DocumentFile>(); }
        }
    }
}
