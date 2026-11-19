using Android.OS;
using FSO.Client;
using FSO.Common;
using FSO.Common.Utils;
using FSO.Content;
using Microsoft.Xna.Framework;
using System.Threading;

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
    ///
    /// Perbaikan 5.3: game ini juga yang memperbaiki dua asumsi PC warisan
    /// TSOGame supaya cocok dengan loop Android — Run() Asynchronous (bukan
    /// Synchronous) dan statik thread game yang menunjuk thread tick render
    /// (bukan UI thread). Lihat Run() dan Initialize() di bawah.
    /// </summary>
    public class BlueberryGame : TSOGame
    {
        /// <summary>
        /// Perbaikan 5.3 — jalankan loop secara ASYNCHRONOUS, bukan Synchronous.
        ///
        /// TSOGame memaksa Run(GameRunBehavior.Synchronous) — warisan PC, benar
        /// di desktop (SDL/Windows) yang loop-nya diblokir di thread pemanggil.
        /// Di Android, AndroidGamePlatform.DefaultRunBehavior adalah
        /// Asynchronous: loop digerakkan GLSurfaceView (MonoGameAndroidGameView)
        /// lewat StartRunLoop, dan Run() sinkron langsung kembali tanpa pernah
        /// Tick — itulah yang membuat game 5.2 keluar instan (log HP 21:37 WITA:
        /// "game.Run() dipanggil" lalu "kembali (keluar normal)" di detik yang
        /// sama, Initialize tidak pernah jalan). Template resmi MonoGame Android
        /// memakai Run() default (async). Override ini mengembalikan perilaku
        /// default itu tanpa mengubah library inti (TSOGame tidak disentuh).
        ///
        /// Catatan: Run(GameRunBehavior) tidak disembunyikan oleh TSOGame.Run()
        /// (beda signature), jadi pemanggilan di sini langsung ke
        /// Game.Run(GameRunBehavior) milik MonoGame.
        /// </summary>
        public new void Run()
        {
            Run(GameRunBehavior.Asynchronous);
        }

        protected override void Initialize()
        {
            // Perbaikan 5.3 — arahkan statik thread game ke thread tick yang
            // sebenarnya, SEBELUM base.Initialize() menjalankan kode apa pun
            // yang memakai statik itu.
            //
            // Dengan loop async, Tick (Initialize/Update/Draw) jalan di thread
            // render GLSurfaceView, BUKAN di UI thread. Dua statik ini harus
            // menunjuk thread itu, karena:
            // - TextureRef.Get() (tso.content/Model/TextureRef.cs:143)
            //   menjalankan pekerjaan GL langsung bila thread saat ini ==
            //   FSOEnvironment.GameThread, dan bila tidak, melemparnya lewat
            //   GameThread.NextUpdate&lt;T&gt;().Result (tunggu maks. 5 detik).
            //   Selama Tick pertama (Initialize — bisa bermenit-menit memindai
            //   file), tidak ada Tick berikutnya yang mencerna antrean itu,
            //   jadi beban tekstur dari thread render akan timeout dan
            //   mengembalikan null bila GameThread masih menunjuk UI thread.
            // - GameThread.Game (FSO.Common.Utils) dipakai IsInGameThread() /
            //   InUpdate() untuk merutekan callback ke thread game.
            // Konstruktor TSOGame (tso.client/TSOGame.cs:72) mengisi
            // GameThread.Game dengan thread konstruksi (UI thread — game dibuat
            // di RunGame dari handler klik Play), dan RunGame 5.2 mengisi
            // FSOEnvironment.GameThread dengan UI thread — keduanya benar
            // untuk loop sinkron PC, salah untuk loop async Android. Diperbaiki
            // di sini. Tidak ada perubahan library inti.
            FSOEnvironment.GameThread = Thread.CurrentThread;
            GameThread.Game = Thread.CurrentThread;
            bool isUiThread = Looper.MyLooper() == Looper.MainLooper;
            FileLogger.Log("RUN", "BlueberryGame.Initialize: thread id="
                + Thread.CurrentThread.ManagedThreadId
                + ", UI thread=" + (isUiThread ? "ya" : "tidak"));

            // Log tahap (perbaikan 5.2, diagnosis blackscreen): Initialize()
            // jalan di Tick pertama (thread render, bukan UI thread) sebelum
            // frame pertama — bila layar hitam, baris terakhir di log
            // menunjukkan tahap mana yang belum selesai.
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
