using System.IO;

namespace WindowsFS.App.Services;

/// <summary>Checks local MP3 framing before passing a path to the media decoder. Never parses tag URLs.</summary>
internal static class Mp3Preview
{
    internal static void Validate(Stream stream)
    {
        Span<byte> tag = stackalloc byte[10];
        if (stream.ReadAtLeast(tag, tag.Length, throwOnEndOfStream: false) != tag.Length)
            throw new InvalidDataException("Incomplete MP3 header.");
        long start = 0;
        if (tag[..3].SequenceEqual("ID3"u8))
        {
            if (tag[3] is < 2 or > 4 || tag[4] == 255 || (tag[6] | tag[7] | tag[8] | tag[9]) >= 128)
                throw new InvalidDataException("Invalid ID3 header.");
            var tagSize = (tag[6] << 21) | (tag[7] << 14) | (tag[8] << 7) | tag[9];
            if (tagSize > 8 * 1024 * 1024) throw new InvalidDataException("ID3 size limit exceeded.");
            start = 10L + tagSize + (tag[3] == 4 && (tag[5] & 0x10) != 0 ? 10 : 0);
        }
        // Validate consecutive frame boundaries, including VBR. ID3 alone is not proof of audio.
        Span<byte> first = stackalloc byte[4];
        Span<byte> second = stackalloc byte[4];
        stream.Position = start;
        stream.ReadExactly(first);
        var firstSize = FrameSize(first);
        stream.Position = start + firstSize;
        stream.ReadExactly(second);
        var secondSize = FrameSize(second);
        if ((first[1] & 0x1e) != (second[1] & 0x1e) || (first[2] & 0x0c) != (second[2] & 0x0c) ||
            start + firstSize + secondSize > stream.Length)
            throw new InvalidDataException("Invalid MP3 frame sequence.");
    }

    private static int FrameSize(ReadOnlySpan<byte> header)
    {
        var version = (header[1] >> 3) & 3;
        var rateIndex = header[2] >> 4;
        var frequencyIndex = (header[2] >> 2) & 3;
        if (header[0] != 255 || (header[1] & 0xe0) != 0xe0 || version == 1 ||
            ((header[1] >> 1) & 3) != 1 || rateIndex is 0 or 15 || frequencyIndex == 3 || (header[3] & 3) == 2)
            throw new InvalidDataException("Invalid or unsupported MPEG Layer III header.");
        ReadOnlySpan<int> mpeg1Rates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
        ReadOnlySpan<int> lowRates = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
        ReadOnlySpan<int> frequencies = [44100, 48000, 32000];
        var frequency = frequencies[frequencyIndex] / (version == 3 ? 1 : version == 2 ? 2 : 4);
        var rate = (version == 3 ? mpeg1Rates : lowRates)[rateIndex] * 1000;
        return (version == 3 ? 144 : 72) * rate / frequency + ((header[2] >> 1) & 1);
    }
}
