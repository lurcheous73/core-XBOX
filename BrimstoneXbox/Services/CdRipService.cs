using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Devices.Custom;
using Windows.Devices.Enumeration;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Storage;
using Windows.Storage.Streams;
using WinBuffer = Windows.Storage.Streams.Buffer;

namespace BrimstoneXbox.Services
{
    public sealed class CdRipService
    {
        const int RawSectorBytes = 2352;
        const int FramesPerSecond = 75;
        const int ChunkSectors = 32;

        static readonly Guid CdromInterfaceGuid =
            new Guid("53F56308-B6BF-11D0-94F2-00A0C91EFB8B");

        public async Task<JsonObject> RipFirstTrackProofAsync()
        {
            var result = new JsonObject
            {
                ["state"] = JsonValue.CreateStringValue("starting"),
                ["track"] = JsonValue.CreateNumberValue(1)
            };

            try
            {
                var ingest = await GetIngestFolderAsync();
                var statusFile = await ingest.CreateFileAsync(
                    "track01-proof.json",
                    CreationCollisionOption.ReplaceExisting);
                await WriteStatusAsync(statusFile, result);

                var selector = CustomDevice.GetDeviceSelector(CdromInterfaceGuid);
                var devices = await DeviceInformation.FindAllAsync(selector);
                if (devices.Count == 0)
                    throw new InvalidOperationException("No CD-ROM interface is visible inside the Xbox sandbox.");

                var device = await CustomDevice.FromIdAsync(
                    devices[0].Id,
                    DeviceAccessMode.Read,
                    DeviceSharingMode.Shared);
                if (device == null)
                    throw new InvalidOperationException("Xbox refused read access to the optical drive.");

                var toc = await ReadTrackBoundsAsync(device);
                var startLba = toc.Item1;
                var endLba = toc.Item2;
                var sectorsTotal = endLba - startLba;
                if (sectorsTotal <= 0)
                    throw new InvalidOperationException("Track 1 has an invalid TOC range.");

                var dataBytes = checked(sectorsTotal * (long)RawSectorBytes);
                if (dataBytes > uint.MaxValue - 36L)
                    throw new InvalidOperationException("Track is too large for a RIFF/WAV proof file.");

                var wav = await ingest.CreateFileAsync(
                    "track01-proof.wav",
                    CreationCollisionOption.ReplaceExisting);

                result["state"] = JsonValue.CreateStringValue("ripping");
                result["file"] = JsonValue.CreateStringValue(wav.Name);
                result["start_lba"] = JsonValue.CreateNumberValue(startLba);
                result["end_lba"] = JsonValue.CreateNumberValue(endLba);
                result["sectors_total"] = JsonValue.CreateNumberValue(sectorsTotal);
                result["sectors_read"] = JsonValue.CreateNumberValue(0);
                result["audio_seconds"] = JsonValue.CreateNumberValue(
                    sectorsTotal / (double)FramesPerSecond);
                result["pcm"] = JsonValue.CreateStringValue("16-bit stereo 44100 Hz");
                result["data_bytes"] = JsonValue.CreateNumberValue(dataBytes);
                result["progress_percent"] = JsonValue.CreateNumberValue(0);
                await WriteStatusAsync(statusFile, result);

                var hashProvider = HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Sha256);
                var hash = hashProvider.CreateHash();
                var stopwatch = Stopwatch.StartNew();

                using (var stream = await wav.OpenAsync(FileAccessMode.ReadWrite))
                {
                    stream.Size = (ulong)(44L + dataBytes);

                    using (var output = stream.GetOutputStreamAt(0))
                    using (var writer = new DataWriter(output))
                    {
                        writer.ByteOrder = ByteOrder.LittleEndian;
                        WriteWavHeader(writer, checked((uint)dataBytes));
                        await writer.StoreAsync();

                        long current = startLba;
                        long lastReported = 0;

                        while (current < endLba)
                        {
                            var count = (uint)Math.Min(ChunkSectors, endLba - current);
                            var buffer = await ReadAudioAsync(device, current, count);
                            if (buffer == null || buffer.Length != count * RawSectorBytes)
                                throw new InvalidOperationException(
                                    "Short CDDA read at LBA " + current +
                                    ": expected " + (count * RawSectorBytes) +
                                    " bytes, got " + (buffer == null ? 0 : buffer.Length) + ".");

                            hash.Append(buffer);

                            using (var reader = DataReader.FromBuffer(buffer))
                            {
                                var bytes = new byte[buffer.Length];
                                reader.ReadBytes(bytes);
                                writer.WriteBytes(bytes);
                            }
                            await writer.StoreAsync();

                            current += count;
                            var read = current - startLba;

                            if (read - lastReported >= 750 || current >= endLba)
                            {
                                lastReported = read;
                                result["sectors_read"] = JsonValue.CreateNumberValue(read);
                                result["progress_percent"] = JsonValue.CreateNumberValue(
                                    Math.Round(read * 100.0 / sectorsTotal, 1));
                                result["elapsed_seconds"] = JsonValue.CreateNumberValue(
                                    Math.Round(stopwatch.Elapsed.TotalSeconds, 2));
                                await WriteStatusAsync(statusFile, result);
                            }
                        }

                        await writer.FlushAsync();
                    }
                }

                stopwatch.Stop();
                var digest = hash.GetValueAndReset();

                result["state"] = JsonValue.CreateStringValue("complete");
                result["sectors_read"] = JsonValue.CreateNumberValue(sectorsTotal);
                result["progress_percent"] = JsonValue.CreateNumberValue(100);
                result["wav_bytes"] = JsonValue.CreateNumberValue(44L + dataBytes);
                result["sha256_audio"] = JsonValue.CreateStringValue(
                    CryptographicBuffer.EncodeToHexString(digest).ToUpperInvariant());
                result["elapsed_seconds"] = JsonValue.CreateNumberValue(
                    Math.Round(stopwatch.Elapsed.TotalSeconds, 2));
                result["rip_speed_x"] = JsonValue.CreateNumberValue(
                    stopwatch.Elapsed.TotalSeconds > 0
                        ? Math.Round((sectorsTotal / 75.0) / stopwatch.Elapsed.TotalSeconds, 2)
                        : 0);
                await WriteStatusAsync(statusFile, result);
            }
            catch (Exception ex)
            {
                result["state"] = JsonValue.CreateStringValue("failed");
                result["error"] = JsonValue.CreateStringValue(
                    ex.GetType().Name + " 0x" + ex.HResult.ToString("X8") + ": " + ex.Message);
                try
                {
                    var ingest = await GetIngestFolderAsync();
                    var statusFile = await ingest.CreateFileAsync(
                        "track01-proof.json",
                        CreationCollisionOption.ReplaceExisting);
                    await WriteStatusAsync(statusFile, result);
                }
                catch { }
            }

            return result;
        }

        async Task<Tuple<long,long>> ReadTrackBoundsAsync(CustomDevice device)
        {
            var ioctl = new IOControlCode(
                (ushort)0x0002,
                (ushort)0x0000,
                IOControlAccessMode.Read,
                IOControlBufferingMethod.Buffered);

            var output = new WinBuffer(804);
            var ok = await device.TrySendIOControlAsync(ioctl, null, output);
            if (!ok || output.Length < 20)
                throw new InvalidOperationException("Could not read CD table of contents.");

            using (var reader = DataReader.FromBuffer(output))
            {
                var bytes = new byte[output.Length];
                reader.ReadBytes(bytes);

                var declared = (bytes[0] << 8) | bytes[1];
                var available = Math.Min(bytes.Length, declared + 2);
                long? track1 = null;
                long? next = null;

                for (var offset = 4; offset + 7 < available; offset += 8)
                {
                    var track = bytes[offset + 2];
                    var control = bytes[offset + 1] & 0x0F;
                    var lba = MsfToLba(bytes[offset + 5], bytes[offset + 6], bytes[offset + 7]);

                    if (track == 1)
                    {
                        if ((control & 0x04) != 0)
                            throw new InvalidOperationException("Track 1 is a data track, not CDDA.");
                        track1 = lba;
                    }
                    else if (track1.HasValue && !next.HasValue)
                    {
                        next = lba;
                        break;
                    }
                }

                if (!track1.HasValue || !next.HasValue)
                    throw new InvalidOperationException("Track 1 boundaries were not present in the TOC.");

                return Tuple.Create(track1.Value, next.Value);
            }
        }

        static long MsfToLba(byte minute, byte second, byte frame)
        {
            return ((minute * 60L + second) * 75L + frame) - 150L;
        }

        async Task<IBuffer> ReadAudioAsync(CustomDevice device, long startSector, uint count)
        {
            var ioctl = new IOControlCode(
                (ushort)0x0002,
                (ushort)0x000F,
                IOControlAccessMode.Read,
                IOControlBufferingMethod.DirectOutput);

            IBuffer input;
            using (var writer = new DataWriter())
            {
                writer.ByteOrder = ByteOrder.LittleEndian;
                writer.WriteInt64(startSector * 2048L);
                writer.WriteUInt32(count);
                writer.WriteUInt32(2); // TRACK_MODE_TYPE.CDDA
                input = writer.DetachBuffer();
            }

            var output = new WinBuffer(count * RawSectorBytes);
            var ok = await device.TrySendIOControlAsync(ioctl, input, output);
            if (!ok)
                throw new InvalidOperationException(
                    "IOCTL_CDROM_RAW_READ failed at LBA " + startSector + ".");

            return output;
        }

        static void WriteWavHeader(DataWriter writer, uint dataBytes)
        {
            writer.WriteBytes(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.WriteUInt32(36U + dataBytes);
            writer.WriteBytes(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            writer.WriteBytes(System.Text.Encoding.ASCII.GetBytes("fmt "));
            writer.WriteUInt32(16);
            writer.WriteUInt16(1);       // PCM
            writer.WriteUInt16(2);       // stereo
            writer.WriteUInt32(44100);
            writer.WriteUInt32(176400);  // 44100 * 2 * 16/8
            writer.WriteUInt16(4);       // block align
            writer.WriteUInt16(16);
            writer.WriteBytes(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.WriteUInt32(dataBytes);
        }

        static async Task<StorageFolder> GetIngestFolderAsync()
        {
            var root = ApplicationData.Current.LocalFolder;
            var core = await root.CreateFolderAsync("Core", CreationCollisionOption.OpenIfExists);
            return await core.CreateFolderAsync("Ingest", CreationCollisionOption.OpenIfExists);
        }

        static Task WriteStatusAsync(StorageFile file, JsonObject result)
        {
            return FileIO.WriteTextAsync(file, result.Stringify()).AsTask();
        }
    }
}
