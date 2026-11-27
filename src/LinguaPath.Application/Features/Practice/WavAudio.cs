using System.Buffers.Binary;

namespace LinguaPath.Application.Features.Practice;

/// <summary>
/// Reads a RIFF/WAVE recording (PCM 8/16/24/32-bit or 32-bit float, any rate, any channels) and converts it to the
/// 16 kHz mono 16-bit PCM WAV that speech services expect. Phones record at 44.1 or 48 kHz, often in stereo.
/// </summary>
public static class WavAudio
{
    public const int TargetRate = 16000;

    public sealed record Info(int SampleRate, int Channels, int BitsPerSample, int Format, TimeSpan Duration);

    public static bool IsWav(ReadOnlySpan<byte> data) =>
        data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WAVE"u8);

    /// <summary>Returns null when the data isn't a WAV file this class can read.</summary>
    public static (Info Info, float[] Mono)? Read(byte[] data)
    {
        if (!IsWav(data)) return null;
        int format = 0, channels = 0, rate = 0, bits = 0, dataOffset = -1, dataLength = 0;
        var pos = 12;
        while (pos + 8 <= data.Length)
        {
            var id = data.AsSpan(pos, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos + 4, 4));
            var body = pos + 8;
            if (size < 0) return null;
            if (id.SequenceEqual("fmt "u8) && body + 16 <= data.Length)
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body + 2, 2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(body + 4, 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body + 14, 2));
                // WAVE_FORMAT_EXTENSIBLE: the real format is the first two bytes of the sub-format GUID.
                if (format == 0xFFFE && size >= 40 && body + 26 <= data.Length)
                    format = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body + 24, 2));
            }
            else if (id.SequenceEqual("data"u8))
            {
                dataOffset = body;
                // Recorders that stream sometimes leave the size at 0 or 0xFFFFFFFF; use what is there.
                dataLength = size == 0 || body + size > data.Length ? data.Length - body : size;
                break;
            }
            pos = body + size + (size & 1);
        }

        if (dataOffset < 0 || channels <= 0 || rate <= 0) return null;
        var pcm = format == 1 && bits is 8 or 16 or 24 or 32;
        var ieee = format == 3 && bits == 32;
        if (!pcm && !ieee) return null;

        var bytesPerSample = bits / 8;
        var frameSize = bytesPerSample * channels;
        var frames = dataLength / frameSize;
        var mono = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            double sum = 0;
            for (var c = 0; c < channels; c++)
            {
                var o = dataOffset + f * frameSize + c * bytesPerSample;
                sum += ieee ? BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(o, 4)) : bits switch
                {
                    8 => (data[o] - 128) / 128.0,
                    16 => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(o, 2)) / 32768.0,
                    24 => ((data[o] | (data[o + 1] << 8) | ((sbyte)data[o + 2] << 16))) / 8388608.0,
                    _ => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(o, 4)) / 2147483648.0
                };
            }
            mono[f] = (float)(sum / channels);
        }
        return (new Info(rate, channels, bits, format, TimeSpan.FromSeconds((double)frames / rate)), mono);
    }

    /// <summary>16 kHz mono 16-bit PCM WAV, or null when the input can't be read.</summary>
    public static byte[]? ToSpeechFormat(byte[] data)
    {
        if (Read(data) is not { } wav) return null;
        var samples = Resample(wav.Mono, wav.Info.SampleRate, TargetRate);
        return Write(samples, TargetRate);
    }

    public static float[] Resample(float[] input, int fromRate, int toRate)
    {
        if (fromRate == toRate || input.Length == 0) return input;
        var length = (int)((long)input.Length * toRate / fromRate);
        var output = new float[length];
        var step = (double)fromRate / toRate;
        // Box-average when going down so high frequencies don't alias into speech; linear interpolation going up.
        var window = Math.Max(1, (int)Math.Floor(step));
        for (var i = 0; i < length; i++)
        {
            var src = i * step;
            if (step > 1)
            {
                var start = (int)src;
                var end = Math.Min(input.Length, start + window);
                double sum = 0;
                for (var j = start; j < end; j++) sum += input[j];
                output[i] = (float)(sum / Math.Max(1, end - start));
            }
            else
            {
                var i0 = (int)src;
                var i1 = Math.Min(input.Length - 1, i0 + 1);
                var t = src - i0;
                output[i] = (float)(input[i0] * (1 - t) + input[i1] * t);
            }
        }
        return output;
    }

    public static byte[] Write(float[] samples, int rate)
    {
        var dataBytes = samples.Length * 2;
        var buffer = new byte[44 + dataBytes];
        var s = buffer.AsSpan();
        "RIFF"u8.CopyTo(s);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], 36 + dataBytes);
        "WAVE"u8.CopyTo(s[8..]);
        "fmt "u8.CopyTo(s[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(s[20..], 1);           // PCM
        BinaryPrimitives.WriteInt16LittleEndian(s[22..], 1);           // mono
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], rate);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], rate * 2);    // byte rate
        BinaryPrimitives.WriteInt16LittleEndian(s[32..], 2);           // block align
        BinaryPrimitives.WriteInt16LittleEndian(s[34..], 16);          // bits
        "data"u8.CopyTo(s[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[40..], dataBytes);
        for (var i = 0; i < samples.Length; i++)
        {
            var v = (short)Math.Clamp(Math.Round(samples[i] * 32767.0), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(s[(44 + i * 2)..], v);
        }
        return buffer;
    }
}
