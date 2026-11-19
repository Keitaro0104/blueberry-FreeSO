using Android.Content;
using FSO.Client.Utils.GameLocator;
using System.IO;

namespace FSO.Android
{
    /// <summary>
    /// Mencari folder game The Sims 1 (bukan The Sims Online) di storage Android.
    /// Pengganti WindowsLocator (Registry + kernel32.dll) dan LinuxLocator untuk Android.
    ///
    /// Urutan pencarian:
    /// 1. Folder yang tersimpan di SharedPreferences (hasil pemilih folder Langkah 5
    ///    — bisa berupa salinan di storage aplikasi, folder custom di penyimpanan
    ///    bersama, atau folder game yang dipakai langsung tanpa menyalin).
    /// 2. Folder `{GetExternalFilesDir}/The Sims` — tempat file game disalin lewat
    ///    USB/ADB dari PC teman (pola davius: adb push ke
    ///    /sdcard/Android/data/com.keitaro0104.blueberryfreeso/files/The Sims/).
    ///
    /// Folder yang dicari adalah ROOT instalasi The Sims 1 (Complete Collection /
    /// Legacy Collection) — folder yang di dalamnya ada GameData/ — BUKAN folder
    /// TSOClient + tuning.dat (itu layout The Sims Online, hanya dipakai mode TSO).
    /// Validasi memakai GameFolderValidator (penanda GameData/Behavior.iff dst.,
    /// mengikuti Simitone: Simitone.Windows/GameLocator/*Locator.cs).
    /// </summary>
    public class AndroidLocator : ILocator
    {
        private readonly Context _context;

        public AndroidLocator(Context context)
        {
            _context = context;
        }

        /// <summary>Folder root game di storage khusus aplikasi (tidak butuh izin storage).</summary>
        public static string GameRoot(Context context)
        {
            return GameFolderManager.AppGameRoot(context);
        }

        /// <summary>
        /// Mengembalikan folder instalasi The Sims 1 bila sudah ada dan valid,
        /// atau null bila file game belum ada di HP.
        /// </summary>
        public string FindTheSims1()
        {
            var saved = GameFolderManager.SavedGameFolder(_context);
            if (saved != null) return saved;

            var direct = GameRoot(_context);
            if (GameFolderValidator.Validate(direct).IsValid)
            {
                GameFolderManager.SaveGameFolder(_context, direct);
                return direct;
            }
            return null;
        }

        /// <summary>
        /// Wajib ada karena interface ILocator (FSO.Client) hanya mendeklarasikan ini.
        /// Di Android, "game" yang dicari selalu The Sims 1 — jadi didelegasikan
        /// ke FindTheSims1().
        /// </summary>
        public string FindTheSimsOnline()
        {
            return FindTheSims1();
        }
    }
}
