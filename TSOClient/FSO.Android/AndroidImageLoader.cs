using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.IO;

namespace FSO.Android
{
    /// <summary>
    /// Pemuat gambar Android berbasis SixLabors.ImageSharp.
    /// Menggantikan System.Drawing.Bitmap yang dipakai FSO.Windows di PC
    /// (System.Drawing.Common tidak jalan di Android).
    /// Polanya sama dengan CoreImageLoader di server (FSO.Server/Utils/CoreImageLoader.cs),
    /// yang juga memakai ImageSharp: decode ke Bgra32 lalu salin pikselnya.
    /// </summary>
    public static class AndroidImageLoader
    {
        /// <summary>
        /// Kontraknya sama dengan FSO.Windows.Program.BitmapReader: kembalikan byte piksel
        /// dalam urutan BGRA + lebar + tinggi. Diisi ke FSO.Files.ImageLoaderHelpers.BitmapFunction,
        /// dipakai FSO.Files.ImageLoader saat membaca BMP/PNG/dll.
        /// Stream tidak ditutup di sini (pemilik stream yang menutupnya, sama seperti di PC).
        /// </summary>
        public static Tuple<byte[], int, int> BitmapReader(Stream str)
        {
            using var image = Image.Load<Bgra32>(str);
            var data = new byte[image.Width * image.Height * 4];
            image.CopyPixelDataTo(data);
            return new Tuple<byte[], int, int>(data, image.Width, image.Height);
        }

        /// <summary>
        /// Kontraknya sama dengan FSO.Windows.Program.SavePNG: tulis data piksel BGRA
        /// sebagai file PNG ke stream. Diisi ke FSO.Files.ImageLoaderHelpers.SavePNGFunc.
        /// </summary>
        public static void SavePNG(byte[] data, int width, int height, Stream str)
        {
            using var image = Image.LoadPixelData<Bgra32>(data, width, height);
            image.SaveAsPng(str);
        }
    }
}
