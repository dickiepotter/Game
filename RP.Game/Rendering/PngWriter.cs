namespace RP.Game.Rendering
{
    using System;
    using System.IO;
    using System.IO.Compression;

    /// <summary>
    /// Writes 8-bit RGBA pixels out as a PNG, with no dependency on anything.
    /// </summary>
    /// <remarks>
    /// <para><b>Why hand-rolled.</b> The alternative is a package, and a screenshot writer is not worth one
    /// -- it would follow the engine onto every platform it ships to in order to do a hundred lines of
    /// work. PNG's container is four chunks and a CRC, and its compression is deflate, which the base
    /// class library already has.</para>
    ///
    /// <para><b>What it is for.</b> Verifying what the renderer actually produced. A test suite can confirm
    /// that geometry is valid, that nothing overflowed and that the frame rate held, and remain perfectly
    /// green while the entire interface is drawn upside down -- which is exactly what happened here once.
    /// Being able to write a frame to disk and look at it closes the one gap the rest of the suite
    /// structurally cannot.</para>
    /// </remarks>
    public static class PngWriter
    {
        /// <summary>Writes RGBA8 pixels, top row first, to a PNG file.</summary>
        /// <param name="path">Where to write.</param>
        /// <param name="pixels">Width * height * 4 bytes, in RGBA order.</param>
        /// <param name="width">Image width in pixels.</param>
        /// <param name="height">Image height in pixels.</param>
        public static void Write(string path, ReadOnlySpan<byte> pixels, int width, int height)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "A PNG needs a positive size.");
            if (pixels.Length < width * height * 4) throw new ArgumentException("Not enough pixel data.", nameof(pixels));

            string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            using var file = new FileStream(path, FileMode.Create, FileAccess.Write);

            file.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

            // IHDR: dimensions, 8 bits per channel, colour type 6 (RGBA), no interlace.
            var header = new byte[13];
            WriteBigEndian(header, 0, (uint)width);
            WriteBigEndian(header, 4, (uint)height);
            header[8] = 8;
            header[9] = 6;
            WriteChunk(file, "IHDR", header);

            WriteChunk(file, "IDAT", Compress(pixels, width, height));
            WriteChunk(file, "IEND", Array.Empty<byte>());
        }

        /// <summary>
        /// Filters and deflates the scanlines.
        /// </summary>
        /// <remarks>
        /// Every row carries filter type 0 -- no prediction. Paeth or Sub would compress a screenshot
        /// appreciably better, and the cost of that is a second implementation of something subtle in a
        /// tool whose whole value is being obviously correct. Disk is cheap and these are transient.
        /// </remarks>
        private static byte[] Compress(ReadOnlySpan<byte> pixels, int width, int height)
        {
            int stride = width * 4;
            var raw = new byte[height * (stride + 1)];

            for (int y = 0; y < height; y++)
            {
                int destination = y * (stride + 1);
                raw[destination] = 0;
                pixels.Slice(y * stride, stride).CopyTo(raw.AsSpan(destination + 1));
            }

            using var output = new MemoryStream();

            // A zlib stream, which is what PNG's IDAT holds: a two-byte header, raw deflate, and an Adler-32
            // of the *uncompressed* bytes. DeflateStream gives the middle part only.
            output.WriteByte(0x78);
            output.WriteByte(0x9C);

            using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
            {
                deflate.Write(raw, 0, raw.Length);
            }

            WriteBigEndianTo(output, Adler32(raw));
            return output.ToArray();
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            var header = new byte[4];
            WriteBigEndian(header, 0, (uint)data.Length);
            stream.Write(header);

            var typeBytes = new byte[4];
            for (int i = 0; i < 4; i++) typeBytes[i] = (byte)type[i];
            stream.Write(typeBytes);
            stream.Write(data);

            // Type first, then data. The CRC is a running computation over the concatenation of the two,
            // and running it the other way round produces a perfectly well-formed number that every reader
            // rejects.
            uint crc = Crc32(data, Crc32(typeBytes, 0xFFFFFFFFu)) ^ 0xFFFFFFFFu;
            WriteBigEndianTo(stream, crc);
        }

        private static void WriteBigEndian(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        private static void WriteBigEndianTo(Stream stream, uint value)
        {
            var buffer = new byte[4];
            WriteBigEndian(buffer, 0, value);
            stream.Write(buffer);
        }

        private static uint Adler32(ReadOnlySpan<byte> data)
        {
            uint a = 1, b = 0;
            foreach (byte value in data)
            {
                a = (a + value) % 65521;
                b = (b + a) % 65521;
            }

            return (b << 16) | a;
        }

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }

            return table;
        }

        /// <summary>Continues a CRC-32 over more bytes. The caller seeds and finalises.</summary>
        private static uint Crc32(ReadOnlySpan<byte> data, uint running)
        {
            uint c = running;
            foreach (byte value in data) c = CrcTable[(c ^ value) & 0xFF] ^ (c >> 8);
            return c;
        }
    }
}
