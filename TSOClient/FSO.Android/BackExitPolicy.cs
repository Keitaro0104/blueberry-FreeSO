namespace FSO.Android
{
    /// <summary>
    /// Perbaikan 5.4 — keputusan murni: kapan Back harus keluar paksa.
    ///
    /// Aturan: Back keluar paksa hanya bila keempatnya benar — tombol yang
    /// ditekan memang Back, event-nya key-down, game sedang jalan, dan
    /// game BELUM pernah Tick. Bila game sudah pernah Tick, Back
    /// diteruskan ke game (game yang menangani, lewat GamePad.Back /
    /// Keys.Back). Bila game tidak jalan (launcher), Back berperilaku
    /// normal (activity ditutup Android).
    ///
    /// File murni System (tanpa API Android) supaya logika ini bisa diuji
    /// di VM: Activity1.DispatchKeyEvent hanya mengubah event mentah jadi
    /// empat bool lalu memanggil ShouldForceExit.
    /// </summary>
    internal static class BackExitPolicy
    {
        public static bool ShouldForceExit(bool isBackKey, bool isKeyDown, bool gameStarted, bool hasTicked)
        {
            return isBackKey && isKeyDown && gameStarted && !hasTicked;
        }
    }
}
