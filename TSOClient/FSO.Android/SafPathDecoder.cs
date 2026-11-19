using System;

namespace FSO.Android
{
    /// <summary>
    /// Mengurai document ID dari Storage Access Framework (SAF) menjadi
    /// jalur file nyata di penyimpanan bersama.
    ///
    /// Contoh: tree URI yang dipilih user di pemilih folder —
    ///   content://com.android.externalstorage.documents/tree/primary%3AIDM%2FDownload%2FThe%20Sims
    /// punya tree document ID "primary:IDM/Download/The Sims" (didapat lewat
    /// DocumentsContract.GetTreeDocumentId), yang berarti jalur nyata
    ///   /storage/emulated/0/IDM/Download/The Sims
    ///
    /// Logika murni (string saja, tanpa API Android) supaya bisa diuji di luar HP.
    /// Mengembalikan null bila tidak bisa diurai (mis. volume yang tidak dikenal).
    /// </summary>
    public static class SafPathDecoder
    {
        /// <summary>Urai document ID "volume:path/relatif" menjadi jalur nyata.</summary>
        public static string TryDecodeTreeDocumentId(string documentId)
        {
            if (string.IsNullOrEmpty(documentId)) return null;
            var colon = documentId.IndexOf(':');
            if (colon <= 0) return null;

            var volume = documentId.Substring(0, colon);
            var relative = documentId.Substring(colon + 1);
            if (string.IsNullOrEmpty(relative)) return null;

            string root;
            if (volume.Equals("primary", StringComparison.OrdinalIgnoreCase))
            {
                root = "/storage/emulated/0";
            }
            else
            {
                // Volume lain (mis. kartu SD "XXXX-XXXX") — best effort.
                root = "/storage/" + volume;
            }

            return root + "/" + relative.Replace('\\', '/');
        }
    }
}
