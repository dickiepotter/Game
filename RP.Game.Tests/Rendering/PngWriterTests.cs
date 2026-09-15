namespace RP.Game.Tests.Rendering
{
    using System;
    using System.IO;
    using System.IO.Compression;
    using FluentAssertions;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using RP.Game.Rendering;

    /// <summary>
    /// The screenshot encoder, read back byte for byte.
    /// </summary>
    /// <remarks>
    /// Every mistake available here produces a file rather than an exception. A CRC computed over the
    /// chunk's bytes in the wrong order is a perfectly well-formed number that every reader rejects; a
    /// missing Adler-32 or a wrong filter byte gives a file that opens as garbage. None of that is visible
    /// from the writing end, so these tests read the file back and check it against what went in.
    /// </remarks>
    [TestClass]
    public sealed class PngWriterTests
    {
        private static string TempFile() => Path.Combine(Path.GetTempPath(), $"rpg-png-{Guid.NewGuid():N}.png");

        /// <summary>Builds a test image with a different colour in every pixel.</summary>
        private static byte[] Gradient(int width, int height)
        {
            var pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = ((y * width) + x) * 4;
                    pixels[i] = (byte)(x * 7);
                    pixels[i + 1] = (byte)(y * 11);
                    pixels[i + 2] = (byte)((x + y) * 3);
                    pixels[i + 3] = 255;
                }
            }

            return pixels;
        }

        [TestMethod]
        public void ItWritesTheSignatureAndAHeaderThatMatchesTheImage()
        {
            string path = TempFile();
            try
            {
                PngWriter.Write(path, Gradient(19, 7), 19, 7);
                byte[] file = File.ReadAllBytes(path);

                file[..8].Should().Equal(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);

                // IHDR is the first chunk: 4 bytes of length, then the type, then the fields.
                new string(new[] { (char)file[12], (char)file[13], (char)file[14], (char)file[15] })
                    .Should().Be("IHDR");

                ReadBigEndian(file, 16).Should().Be(19u, "width");
                ReadBigEndian(file, 20).Should().Be(7u, "height");
                file[24].Should().Be(8, "bit depth");
                file[25].Should().Be(6, "colour type 6 is RGBA");
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void EveryChunkCarriesACorrectCrc()
        {
            // The one that has already been wrong. The CRC runs over the type and then the data, and
            // computing it the other way round yields a number that is well-formed and universally
            // rejected -- with nothing on the writing side to suggest anything happened.
            string path = TempFile();
            try
            {
                PngWriter.Write(path, Gradient(31, 13), 31, 13);
                byte[] file = File.ReadAllBytes(path);

                int offset = 8;
                int chunks = 0;

                while (offset < file.Length)
                {
                    uint length = ReadBigEndian(file, offset);
                    uint stated = ReadBigEndian(file, offset + 8 + (int)length);
                    uint actual = Crc32(file.AsSpan(offset + 4, 4 + (int)length));

                    actual.Should().Be(stated, "chunk at offset {0} should have a correct CRC", offset);

                    offset += 12 + (int)length;
                    chunks++;
                }

                chunks.Should().Be(3, "IHDR, IDAT and IEND");
                offset.Should().Be(file.Length, "the chunks should account for the whole file exactly");
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void ThePixelsComeBackExactlyAsTheyWentIn()
        {
            // A screenshot whose colours are subtly wrong is worse than no screenshot, because it is
            // believed. Decoding the IDAT back and comparing every byte is the only check that means
            // anything here.
            const int Width = 23;
            const int Height = 9;

            string path = TempFile();
            try
            {
                byte[] original = Gradient(Width, Height);
                PngWriter.Write(path, original, Width, Height);

                byte[] decoded = DecodeScanlines(File.ReadAllBytes(path), Width, Height);
                decoded.Should().Equal(original);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void TheAdlerChecksumCoversTheUncompressedBytes()
        {
            string path = TempFile();
            try
            {
                PngWriter.Write(path, Gradient(16, 16), 16, 16);
                byte[] file = File.ReadAllBytes(path);

                (int start, int length) = FindChunk(file, "IDAT");

                // The last four bytes of a zlib stream are an Adler-32 of what went into it.
                uint stated = ReadBigEndian(file, start + length - 4);
                uint actual = Adler32(Inflate(file.AsSpan(start + 2, length - 6)));

                actual.Should().Be(stated);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void ItRefusesAnImageItCannotWrite()
        {
            string path = TempFile();

            Action tooSmall = () => PngWriter.Write(path, new byte[16], 0, 4);
            tooSmall.Should().Throw<ArgumentOutOfRangeException>();

            Action notEnoughPixels = () => PngWriter.Write(path, new byte[16], 8, 8);
            notEnoughPixels.Should().Throw<ArgumentException>();
        }

        // ---- Reading the format back -----------------------------------------------------------------

        private static uint ReadBigEndian(ReadOnlySpan<byte> data, int offset)
            => ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];

        private static (int Start, int Length) FindChunk(byte[] file, string type)
        {
            int offset = 8;
            while (offset < file.Length)
            {
                int length = (int)ReadBigEndian(file, offset);
                string name = new string(new[] { (char)file[offset + 4], (char)file[offset + 5], (char)file[offset + 6], (char)file[offset + 7] });

                if (name == type) return (offset + 8, length);
                offset += 12 + length;
            }

            throw new InvalidOperationException($"No {type} chunk.");
        }

        private static byte[] Inflate(ReadOnlySpan<byte> deflated)
        {
            using var input = new MemoryStream(deflated.ToArray());
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            deflate.CopyTo(output);
            return output.ToArray();
        }

        /// <summary>Inflates the IDAT and strips the per-row filter byte, which the writer always sets to 0.</summary>
        private static byte[] DecodeScanlines(byte[] file, int width, int height)
        {
            (int start, int length) = FindChunk(file, "IDAT");
            byte[] raw = Inflate(file.AsSpan(start + 2, length - 6));

            int stride = width * 4;
            raw.Length.Should().Be(height * (stride + 1), "one filter byte per row plus the row itself");

            var pixels = new byte[height * stride];
            for (int y = 0; y < height; y++)
            {
                raw[y * (stride + 1)].Should().Be(0, "row {0} should use filter type 0", y);
                Array.Copy(raw, (y * (stride + 1)) + 1, pixels, y * stride, stride);
            }

            return pixels;
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

        private static uint Crc32(ReadOnlySpan<byte> data)
        {
            uint c = 0xFFFFFFFFu;
            foreach (byte value in data)
            {
                c ^= value;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            return c ^ 0xFFFFFFFFu;
        }
    }
}
