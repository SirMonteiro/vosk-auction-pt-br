// Simulates UnityMicrophoneCapture's conversion chain over a WAV file and feeds the result to
// the real Vosk engine. This is the end-to-end check for capture bugs: the WAV is resampled
// exactly as the microphone path would, chunk by chunk, then decoded.
//
//   AsrProbe <model> <wav> [deviceRate]
//
// Without a device rate it decodes the file directly (the "engine works" baseline).
// With a device rate it re-runs the capture conversion chain first.

using System;
using System.Collections.Generic;
using System.IO;
using AuctionVoice.ASR;
using AuctionVoice.Audio;
using AuctionVoice.Core;
using AuctionVoice.NLU;

public static class AsrProbe
{
    public static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: AsrProbe <modelPath> <wav...> [--device-rate N]");
            return 2;
        }

        string modelPath = args[0];
        int deviceRate = 0;
        var files = new List<string>();

        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--device-rate" && i + 1 < args.Length)
            {
                deviceRate = int.Parse(args[++i]);
                continue;
            }

            files.Add(args[i]);
        }

        var engine = new VoskSpeechEngine(16000) { MaxAlternatives = 5 };
        engine.Initialize(modelPath);
        Console.WriteLine($"model: {engine.ModelName}");

        var parser = new AuctionBidParser();
        int failures = 0;

        foreach (string file in files)
        {
            byte[] pcm = ReadWavAsPcm16(file);
            if (pcm.Length == 0)
            {
                Console.WriteLine($"{Path.GetFileName(file)}: unreadable");
                failures++;
                continue;
            }

            float sourceMs = pcm.Length / 2f / 16000f * 1000f;

            // Recreate the capture chain when a device rate is given: convert chunk by chunk at
            // the device rate, then resample. This is what the microphone path does.
            if (deviceRate > 0 && deviceRate != 16000)
            {
                pcm = SimulateCaptureChain(pcm, deviceRate, 100);
            }

            float captureMs = pcm.Length / 2f / 16000f * 1000f;
            float rms = Rms(pcm);
            bool silent = PcmAudio.IsSilent(pcm);

            var session = engine.CreateSession();
            session.Start();
            session.Feed(new byte[16000 * 300 / 1000 * 2], 16000 * 300 / 1000 * 2);
            session.Feed(pcm, pcm.Length);
            session.Feed(new byte[16000 * 400 / 1000 * 2], 16000 * 400 / 1000 * 2);

            AsrResult asr = session.Finish();
            List<string> candidates = asr.NBestTexts();
            BidResult bid = candidates.Count > 0
                ? parser.ParseNBest(candidates)
                : new BidResult { RejectReason = BidRejectReason.Silence };

            string kind = bid.Kind.ToWire();
            Console.WriteLine(
                $"{Path.GetFileName(file)}: src={sourceMs:0}ms capture={captureMs:0}ms "
                + $"rms={rms:0.000} silent={silent} asr=\"{asr.Text}\" -> {kind} {bid.Value:0.##}");

            if (string.IsNullOrEmpty(asr.Text))
            {
                failures++;
            }

            session.Dispose();
        }

        engine.Dispose();
        Console.WriteLine(failures == 0
            ? "all files produced text"
            : $"{failures} file(s) produced no text");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Replays UnityMicrophoneCapture's conversion for a given device rate: upsample the 16 kHz
    /// fixture to the device rate, split into 100 ms chunks, convert each to PCM16, and run the
    /// stateful resampler back down to 16 kHz.
    /// </summary>
    private static byte[] SimulateCaptureChain(byte[] pcm16k, int deviceRate, int chunkMs)
    {
        byte[] atDeviceRate = PcmAudio.Resample(pcm16k, 16000, deviceRate);

        int deviceSamples = deviceRate * chunkMs / 1000;
        var chunkBuffer = new byte[deviceSamples * 2];
        var floats = new float[deviceSamples];
        var resampler = new Resampler(deviceRate, 16000);
        var output = new MemoryStream();

        int offset = 0;
        int totalSamples = atDeviceRate.Length / 2;
        while (offset < totalSamples)
        {
            int take = Math.Min(deviceSamples, totalSamples - offset);
            for (int i = 0; i < take; i++)
            {
                int b = (offset + i) * 2;
                short s = (short)(atDeviceRate[b] | (atDeviceRate[b + 1] << 8));
                floats[i] = s / 32768f;
            }

            int bytes = PcmAudio.FloatToPcm16(floats, take, chunkBuffer);
            byte[] converted = resampler.Process(chunkBuffer, bytes);
            output.Write(converted, 0, converted.Length);
            offset += take;
        }

        return output.ToArray();
    }

    private static float Rms(byte[] pcm)
    {
        double sum = 0d;
        int samples = pcm.Length / 2;
        for (int i = 0; i < samples; i++)
        {
            short s = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            double v = s / 32768d;
            sum += v * v;
        }

        return samples > 0 ? (float)Math.Sqrt(sum / samples) : 0f;
    }

    private static byte[] ReadWavAsPcm16(string path)
    {
        using (var stream = File.OpenRead(path))
        using (var reader = new BinaryReader(stream))
        {
            if (new string(reader.ReadChars(4)) != "RIFF")
            {
                return Array.Empty<byte>();
            }

            reader.ReadInt32();
            if (new string(reader.ReadChars(4)) != "WAVE")
            {
                return Array.Empty<byte>();
            }

            int channels = 1;
            int rate = 16000;
            int bits = 16;
            byte[] data = Array.Empty<byte>();

            while (stream.Position + 8 <= stream.Length)
            {
                string chunkId = new string(reader.ReadChars(4));
                int size = reader.ReadInt32();
                if (chunkId == "fmt ")
                {
                    reader.ReadInt16();
                    channels = reader.ReadInt16();
                    rate = reader.ReadInt32();
                    reader.ReadInt32();
                    reader.ReadInt16();
                    bits = reader.ReadInt16();
                    if (size > 16)
                    {
                        reader.ReadBytes(size - 16);
                    }
                }
                else if (chunkId == "data")
                {
                    data = reader.ReadBytes(size);
                }
                else
                {
                    reader.ReadBytes(size);
                }

                if (size % 2 == 1)
                {
                    reader.ReadByte();
                }
            }

            if (channels == 1 && rate == 16000 && bits == 16)
            {
                return data;
            }

            int sampleCount = data.Length / (bits / 8);
            var mono = new short[sampleCount / channels];
            for (int i = 0; i < mono.Length; i++)
            {
                mono[i] = BitConverter.ToInt16(data, i * channels * 2);
            }

            return PcmAudio.Resample(ShortsToBytes(mono), rate, 16000);
        }
    }

    private static byte[] ShortsToBytes(short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            bytes[i * 2] = (byte)(samples[i] & 0xFF);
            bytes[i * 2 + 1] = (byte)((samples[i] >> 8) & 0xFF);
        }

        return bytes;
    }
}
