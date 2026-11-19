using Android.Content;
using Android.OS;
using Android.Provider;
using System;
using System.IO;

namespace FSO.Android
{
    /// <summary>
    /// Bagian FileLogger yang butuh API Android: menyimpan salinan log ke folder
    /// Download (dipakai tombol "Simpan Log ke Download" + auto-export saat
    /// force close). Dipisah ke file sendiri supaya FileLogger.cs tetap murni
    /// System.IO dan bisa diuji di luar HP.
    ///
    /// Cara kerja (Android 10+ / API 29+, tanpa izin akses semua file):
    /// MediaStore Downloads — insert baris baru (DISPLAY_NAME, MIME text/plain,
    /// RELATIVE_PATH "Download"), lalu tulis isi log lewat ContentResolver.
    /// Bila sudah ada file lama dengan nama sama, dihapus dulu lewat
    /// query + delete (best-effort) supaya di Download tinggal satu file log.
    ///
    /// Android lama (&lt; API 29): tulis langsung ke
    /// /storage/emulated/0/Download/ (izin WRITE_EXTERNAL_STORAGE maxSdk 28
    /// sudah ada di manifest).
    ///
    /// Batasan (jujur): crash native (SIGSEGV, bukan exception C#) tidak
    /// melewati handler managed, jadi auto-export tidak sempat jalan — log
    /// tahap terakhir tetap ada di file log biasa (android/data).
    /// Semua method di sini best-effort dan tidak pernah melempar exception.
    /// </summary>
    public static partial class FileLogger
    {
        private static Context _appContext;

        /// <summary>Simpan ApplicationContext (dipanggil sekali dari Activity.OnCreate).</summary>
        public static void SetContext(Context context)
        {
            try { _appContext = context?.ApplicationContext; }
            catch (Exception) { _appContext = null; }
        }

        /// <summary>Simpan isi log ke Download/BlueberryFreeSO-log.txt (pakai context tersimpan).</summary>
        public static bool ExportToDownloads()
        {
            return ExportToDownloads(_appContext);
        }

        /// <summary>Simpan isi log ke Download/BlueberryFreeSO-log.txt.</summary>
        public static bool ExportToDownloads(Context context)
        {
            try
            {
                if (context == null) return false;
                var text = ReadTail(int.MaxValue) ?? "";
                var bytes = System.Text.Encoding.UTF8.GetBytes(text);

                if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
                {
                    var resolver = context.ContentResolver;
                    var collection = MediaStore.Downloads.ExternalContentUri;

                    // Hapus file lama dengan nama sama (best-effort).
                    try
                    {
                        using var cursor = resolver.Query(collection,
                            new[] { "_id" }, "display_name = ?",
                            new[] { LogFileName }, null);
                        if (cursor != null)
                        {
                            int idCol = cursor.GetColumnIndex("_id");
                            while (cursor.MoveToNext())
                            {
                                var oldUri = ContentUris.WithAppendedId(
                                    collection, cursor.GetLong(idCol));
                                try { resolver.Delete(oldUri, null, null); }
                                catch (Exception) { }
                            }
                        }
                    }
                    catch (Exception) { /* lanjut — insert baru tetap dicoba */ }

                    var values = new ContentValues();
                    values.Put("display_name", LogFileName);
                    values.Put("mime_type", "text/plain");
                    values.Put("relative_path", global::Android.OS.Environment.DirectoryDownloads);
                    var uri = resolver.Insert(collection, values);
                    if (uri == null) return false;
                    using var output = resolver.OpenOutputStream(uri);
                    if (output == null) return false;
                    output.Write(bytes, 0, bytes.Length);
                    output.Flush();
                    return true;
                }
                else
                {
#pragma warning disable CS0618 // GetExternalStoragePublicDirectory usang di API 29+, tapi cabang ini hanya jalan di API < 29.
                    var downloads = global::Android.OS.Environment.GetExternalStoragePublicDirectory(
                        global::Android.OS.Environment.DirectoryDownloads);
#pragma warning restore CS0618
                    if (downloads == null) return false;
                    Directory.CreateDirectory(downloads.AbsolutePath);
                    File.WriteAllBytes(
                        Path.Combine(downloads.AbsolutePath, LogFileName), bytes);
                    return true;
                }
            }
            catch (Exception e)
            {
                try { Log("DOWNLOAD", "Export log ke Download gagal: " + e.Message); }
                catch (Exception) { }
                return false;
            }
        }
    }
}
