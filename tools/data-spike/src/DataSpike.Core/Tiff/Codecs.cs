using System;
using System.IO;
using System.IO.Compression;

namespace MountainPlanner.DataSpike.Tiff
{
    /// <summary>TIFF LZW decoding (compression 5): MSB-first codes, 9–12 bits, "early change".</summary>
    public static class Lzw
    {
        const int ClearCode = 256, EndOfInformation = 257, MaxCode = 4096;

        public static byte[] Decode(byte[] input, int expectedLength)
        {
            var output = new byte[expectedLength];
            int outPos = 0;
            var prefix = new short[MaxCode];
            var suffix = new byte[MaxCode];
            var length = new short[MaxCode];
            for (int i = 0; i < 256; i++) { prefix[i] = -1; suffix[i] = (byte)i; length[i] = 1; }

            int nextCode = 258, codeWidth = 9, previous = -1;
            long bitBuffer = 0; int bitCount = 0, inPos = 0;
            var stack = new byte[MaxCode];

            while (true)
            {
                while (bitCount < codeWidth)
                {
                    if (inPos >= input.Length) return Finish(output, outPos);
                    bitBuffer = (bitBuffer << 8) | input[inPos++];
                    bitCount += 8;
                }
                int code = (int)((bitBuffer >> (bitCount - codeWidth)) & ((1 << codeWidth) - 1));
                bitCount -= codeWidth;

                if (code == EndOfInformation) break;
                if (code == ClearCode)
                {
                    nextCode = 258; codeWidth = 9; previous = -1;
                    continue;
                }

                int emit;
                byte first;
                if (code < nextCode)
                {
                    emit = code;
                }
                else if (code == nextCode && previous >= 0)
                {
                    emit = previous; // KwKwK case: previous string + its own first byte
                }
                else throw new InvalidDataException($"LZW: bad code {code} (next {nextCode})");

                // Unwind the string for `emit` onto the stack, then copy it out.
                int depth = 0, c = emit;
                while (c >= 0) { stack[depth++] = suffix[c]; c = prefix[c]; }
                first = stack[depth - 1];
                for (int i = depth - 1; i >= 0 && outPos < output.Length; i--) output[outPos++] = stack[i];
                if (code == nextCode && outPos < output.Length) output[outPos++] = first;

                if (previous >= 0 && nextCode < MaxCode)
                {
                    prefix[nextCode] = (short)previous;
                    suffix[nextCode] = first;
                    length[nextCode] = (short)(length[previous] + 1);
                    nextCode++;
                    if (nextCode + 1 >= (1 << codeWidth) && codeWidth < 12) codeWidth++;
                }
                previous = code;
            }
            return Finish(output, outPos);
        }

        static byte[] Finish(byte[] output, int written)
        {
            if (written != output.Length)
                throw new InvalidDataException($"LZW: decoded {written} bytes, expected {output.Length}");
            return output;
        }
    }

    /// <summary>TIFF Deflate (compression 8 and 32946): a zlib stream.</summary>
    public static class Deflate
    {
        public static byte[] Decode(byte[] input, int expectedLength)
        {
            // Skip the two-byte zlib header; DeflateStream reads raw deflate data.
            using (var source = new MemoryStream(input, 2, input.Length - 2))
            using (var inflater = new DeflateStream(source, CompressionMode.Decompress))
            {
                var output = new byte[expectedLength];
                int read = 0;
                while (read < expectedLength)
                {
                    int n = inflater.Read(output, read, expectedLength - read);
                    if (n == 0) break;
                    read += n;
                }
                if (read != expectedLength)
                    throw new InvalidDataException($"Deflate: decoded {read} bytes, expected {expectedLength}");
                return output;
            }
        }
    }

    /// <summary>TIFF predictors: 2 = horizontal differencing, 3 = floating-point (Adobe TIFF Tech Note 3).</summary>
    public static class Predictors
    {
        /// <summary>Undo predictor 2 in place for unsigned 8-bit, single-sample rows.</summary>
        public static void UndoHorizontal8(byte[] data, int width, int rows)
        {
            for (int r = 0; r < rows; r++)
            {
                int row = r * width;
                for (int i = 1; i < width; i++) data[row + i] = (byte)(data[row + i] + data[row + i - 1]);
            }
        }

        /// <summary>
        /// Undo predictor 3 for single-sample float32 rows and return host-order floats. Each row is
        /// byte-wise differenced, and its bytes are stored as planes, most significant byte first.
        /// </summary>
        public static float[] UndoFloatingPoint32(byte[] data, int width, int rows)
        {
            var result = new float[width * rows];
            int rowBytes = width * 4;
            var planar = new byte[rowBytes];
            var word = new byte[4];
            for (int r = 0; r < rows; r++)
            {
                int start = r * rowBytes;
                Buffer.BlockCopy(data, start, planar, 0, rowBytes);
                for (int i = 1; i < rowBytes; i++) planar[i] = (byte)(planar[i] + planar[i - 1]);
                for (int i = 0; i < width; i++)
                {
                    // planar holds big-endian byte planes; build a host-order float.
                    if (BitConverter.IsLittleEndian)
                    {
                        word[3] = planar[i]; word[2] = planar[width + i]; word[1] = planar[2 * width + i]; word[0] = planar[3 * width + i];
                    }
                    else
                    {
                        word[0] = planar[i]; word[1] = planar[width + i]; word[2] = planar[2 * width + i]; word[3] = planar[3 * width + i];
                    }
                    result[r * width + i] = BitConverter.ToSingle(word, 0);
                }
            }
            return result;
        }
    }
}
