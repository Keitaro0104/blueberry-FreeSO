using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FSO.Android
{
    /// <summary>
    /// Hasil validasi folder game The Sims 1.
    /// </summary>
    public class GameFolderValidation
    {
        /// <summary>True bila semua file penanda wajib ada.</summary>
        public bool IsValid { get; set; }

        /// <summary>Jalur relatif file wajib yang TIDAK ditemukan (kosong bila valid).</summary>
        public List<string> MissingFiles { get; } = new List<string>();

        /// <summary>Folder tingkat atas yang ditemukan di root instalasi (info saja).</summary>
        public List<string> TopLevelFolders { get; } = new List<string>();

        /// <summary>True bila ada folder expansion (nama mengandung "Expansion"). Info saja.</summary>
        public bool HasExpansionFolders { get; set; }

        /// <summary>Pesan untuk user bila tidak valid (Bahasa Indonesia).</summary>
        public string UserMessage
        {
            get
            {
                if (IsValid) return "Folder game valid.";
                return "Folder ini bukan folder instalasi The Sims 1 yang lengkap.\n\n"
                    + "File yang tidak ditemukan:\n- "
                    + string.Join("\n- ", MissingFiles)
                    + "\n\nPilih folder instalasi The Sims (Complete Collection / Legacy Collection) "
                    + "— folder yang di dalamnya ada folder \"GameData\". "
                    + "Jangan pilih folder \"GameData\" itu sendiri.";
            }
        }
    }

    /// <summary>
    /// Memvalidasi folder game The Sims 1 murni dengan System.IO biasa (tanpa API Android),
    /// supaya logikanya bisa dites di luar HP.
    ///
    /// Daftar file penanda — semuanya dibaca engine saat startup dalam mode TS1
    /// (bukti kode di repo FreeSO):
    /// - GameData/Behavior.iff — penanda yang dicek Simitone di semua locator-nya
    ///   (Simitone.Windows/GameLocator/*Locator.cs: FindTheSims1).
    /// - GameData/UIText.iff — string UI, dibaca ContentStrings.LoadTS1()
    ///   (TSOClient/FSO.UI/ContentStrings.cs:122) saat game mulai.
    /// - GameData/Global/Global.far — data global dunia, dibaca WorldGlobalProvider
    ///   (TSOClient/tso.content/WorldGlobalProvider.cs:41).
    /// - GameData/floors.iff, GameData/walls.iff, GameData/Build.iff — data lantai/
    ///   dinding, dibaca WorldFloorProvider / WorldWallProvider mode TS1
    ///   (WorldFloorProvider.cs:108,112; WorldWallProvider.cs:182,185).
    ///
    /// Sengaja TIDAK dicek: tuning.dat (itu file The Sims Online, tidak dipakai
    /// mode TS1 murni) dan folder Objects/ TSO (hanya dipakai mode TSO,
    /// WorldObjectProvider — mode TS1 memakai objek dari instalasi TS1 sendiri).
    /// </summary>
    public static class GameFolderValidator
    {
        /// <summary>File wajib, relatif terhadap root instalasi The Sims 1.</summary>
        public static readonly string[] RequiredFiles =
        {
            "GameData/Behavior.iff",
            "GameData/UIText.iff",
            "GameData/Global/Global.far",
            "GameData/floors.iff",
            "GameData/walls.iff",
            "GameData/Build.iff",
        };

        /// <summary>Memvalidasi folder instalasi The Sims 1.</summary>
        public static GameFolderValidation Validate(string rootPath)
        {
            var result = new GameFolderValidation();

            if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath))
            {
                result.MissingFiles.AddRange(RequiredFiles);
                return result;
            }

            foreach (var relative in RequiredFiles)
            {
                var full = Path.Combine(rootPath, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full)) result.MissingFiles.Add(relative);
            }

            try
            {
                foreach (var dir in Directory.EnumerateDirectories(rootPath))
                {
                    var name = Path.GetFileName(dir);
                    result.TopLevelFolders.Add(name);
                    if (name.IndexOf("Expansion", StringComparison.OrdinalIgnoreCase) >= 0)
                        result.HasExpansionFolders = true;
                }
            }
            catch (Exception)
            {
                // Folder tidak bisa dibaca — biarkan hasil validasi file yang bicara.
            }

            result.IsValid = result.MissingFiles.Count == 0;
            return result;
        }
    }
}
