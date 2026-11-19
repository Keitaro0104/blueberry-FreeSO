using FSO.Client;
using FSO.Content;

namespace FSO.Android
{
    /// <summary>
    /// TSOGame dalam mode TS1 murni (seperti Simitone), bukan TS1Hybrid.
    ///
    /// Bukti kode (repo Simitone, Client/Simitone/Simitone.Client/SimitoneGame.cs:131):
    /// Simitone memakai `FSO.Content.Content.Target = FSOEngineMode.TS1`.
    /// Sedangkan TSOGame.Initialize() (TSOClient/tso.client/TSOGame.cs:182) selalu
    /// memaksa `Content.TS1Hybrid = GlobalSettings.Default.TS1HybridEnable`,
    /// yang hanya bisa menghasilkan TS1Hybrid atau TSO — tidak pernah TS1 murni.
    ///
    /// Perbedaan yang penting untuk Android (TSOClient/tso.content/Content.cs):
    /// - Mode TS1 murni TIDAK memindai folder game sebagai file TSO
    ///   (`if (Target != FSOEngineMode.TS1) _ScanFiles(BasePath...)`) — di HP,
    ///   memindai instalasi 2-3 GB dua kali (sebagai TSO lalu sebagai TS1) hanya
    ///   membuang waktu startup.
    /// - Mode TS1 murni TIDAK menginisialisasi UIGraphics TSO (tidak ada gunanya
    ///   untuk Sims 1) dan TIDAK memuat tuning.dat (file The Sims Online).
    /// - Provider yang dipakai sama di kedua mode (TS1=true di keduanya), jadi
    ///   objek/avatar/audio TS1 tetap dimuat dari folder instalasi TS1.
    ///
    /// Flip dilakukan di sini — setelah base.Initialize() membangun Content
    /// (InitBasic, TS1HybridBasePath sudah diisi dari GlobalSettings), dan
    /// sebelum LoadingScreenController memanggil Content.Init() saat layar
    /// loading tampil. Tidak ada perubahan kode di library inti.
    /// </summary>
    public class BlueberryGame : TSOGame
    {
        protected override void Initialize()
        {
            // Log tahap (perbaikan 5.2, diagnosis blackscreen): Initialize() jalan
            // sinkron di UI thread sebelum frame pertama — bila layar hitam,
            // baris terakhir di log menunjukkan tahap mana yang belum selesai.
            FileLogger.Log("RUN", "BlueberryGame.Initialize: sebelum base.Initialize()");
            base.Initialize();
            FileLogger.Log("RUN", "BlueberryGame.Initialize: base selesai, Target dibalik ke TS1");
            // Content.Target di sini adalah FSO.Content.Content (kelas statis engine),
            // BUKAN Game.Content (ContentManager MonoGame — properti turunan dari Game).
            FSO.Content.Content.Target = FSOEngineMode.TS1;
            FileLogger.Log("RUN", "BlueberryGame.Initialize: selesai, LoadProgress="
                + FSO.Content.Content.LoadProgress);
        }
    }
}
