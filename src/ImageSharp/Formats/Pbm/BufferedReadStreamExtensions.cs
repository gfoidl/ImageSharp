// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Runtime.CompilerServices;
using SixLabors.ImageSharp.IO;

namespace SixLabors.ImageSharp.Formats.Pbm;

/// <summary>
/// Extensions methods for <see cref="BufferedReadStream"/>.
/// </summary>
internal static class BufferedReadStreamExtensions
{
    /// <summary>
    /// Skip over any whitespace or any comments.
    /// </summary>
    public static void SkipWhitespaceAndComments(this BufferedReadStream stream)
    {
        bool isWhitespace;
        do
        {
            int val = stream.ReadByte();

            // Comments start with '#' and end at the next new-line.
            if (val == 0x23)
            {
                int innerValue;
                do
                {
                    innerValue = stream.ReadByte();
                }
                while (!IsNewlineOrEndOfStream(innerValue));

                // Continue searching for whitespace.
                val = innerValue;
            }

            isWhitespace = IsWhitespace(val);
        }
        while (isWhitespace);
        stream.Seek(-1, SeekOrigin.Current);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool IsNewlineOrEndOfStream(int val)
        {
            // See comment below for how this works
            if (Environment.Is64BitProcess)
            {
                const ulong magicConstant = 0x8010000000000000UL;

                ulong i = (uint)val + 0x1;  // - -0x1 -> + 0x1
                ulong shift = magicConstant << (int)i;
                ulong mask = i - 64;

                return (long)(shift & mask) < 0;
            }

            return val is 0x0a or -0x1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool IsWhitespace(int val)
        {
            // The magic constant 0xC800010000000000 is a 64 bit value containing 1s at the indices
            // of all whitespace chars minus 0x09 in a backwards order (from the MSB downwards).
            // The subtraction of 0x09 is necessary so that the entire range fits in 64 bits.
            //
            // From the input 0x09 is subtracted, then it's zero-extended to ulong, meaning the upper
            // 32 bits are 0. Then the constant is left shifted by that offset.
            // A bitmask, that has the sign bit (the highest bit) set iff 'val' is in the [0x09, 0x09 + 64) range,
            // is applied. Thus we only need to check if the final result is < 0, which will only be the case if
            // 'i' was the index of a set bit in the magic constant, and if val was in the allowed range.
            /* Constant created with
                using System;
                using System.Linq;

                int[] chars = { 0x09, 0x0a, 0x0d, 0x20 };
                int min = chars.Min();
                ulong magic = 0;

                foreach (int c in chars)
                {
                    int idx = c - min;
                    magic |= 1UL << (64 - 1 - idx);
                }

                Console.WriteLine(magic);
                Console.WriteLine($"0x{magic:X16}");
             */

            if (Environment.Is64BitProcess)
            {
                const ulong magicConstant = 0xC800010000000000UL;

                ulong i = (uint)val - 0x09;
                ulong shift = magicConstant << (int)i;
                ulong mask = i - 64;

                return (long)(shift & mask) < 0;
            }

            return val is 0x09 or 0x0a or 0x0d or 0x20;
        }
    }

    /// <summary>
    /// Read a decimal text value.
    /// </summary>
    /// <returns>The integer value of the decimal.</returns>
    public static int ReadDecimal(this BufferedReadStream stream)
    {
        int value = 0;
        while (true)
        {
            int current = stream.ReadByte() - 0x30;
            if ((uint)current > 9)
            {
                break;
            }

            value = (value * 10) + current;
        }

        return value;
    }
}
