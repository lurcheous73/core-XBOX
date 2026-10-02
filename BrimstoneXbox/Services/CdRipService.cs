using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
        const int ChunkSectors = 128;

        static readonly Guid CdromInterfaceGuid =
            new Guid("53F56308-B6BF-11D0-94F2-00A0C91EFB8B");

        bool _busy;

        public async Task<JsonObject> AutoRipCurrentDiscAsync()
        {
            if (_busy)
                return State("busy");

            _busy = true;
            try
            {
                var selector = CustomDevice.GetDeviceSelector(CdromInterfaceGuid);
                var devices = await DeviceInformation.FindAllAsync(selector);
                if (devices.Count == 0)
                    return await FailAsync("No CD-ROM interface is visible inside the Xbox sandbox.");

                var device = await CustomDevice.FromIdAsync(
                    devices[0].Id,
                    DeviceAccessMode.Read,
                    DeviceSharingMode.Shared);
                if (device == null)
                    return await FailAsync("Xbox refused read access to the optical drive.");

                var toc = await ReadTocAsync(device);
                if (toc.Tracks.Count == 0)
                    return await FailAsync("No audio tracks were found on the inserted disc.");

                var ingest = await GetIngestFolderAsync();
                var discFolder = await ingest.CreateFolderAsync(
                    "CD-" + toc.Fingerprint.Substring(0, 16),
                    CreationCollisionOption.OpenIfExists);
                var statusFile = await discFolder.CreateFileAsync(
                    "rip-status.json",
                    CreationCollisionOption.OpenIfExists);

                var existing = await TryReadJsonAsync(statusFile);
                if (existing != null &&
                    GetString(existing, "state") == "complete" &&
                    GetString(existing, "fingerprint") == toc.Fingerprint)
                {
                    await WritePointerAsync(existing, discFolder.Name);
                    return existing;
                }

                var audioTracks = toc.Tracks.Where(t => t.Audio).ToList();
                var totalSectors = audioTracks.Sum(t => t.EndLba - t.StartLba);
                long discSectorsRead = 0;

                var status = new JsonObject
                {
                    ["state"] = JsonValue.CreateStringValue("ripping"),
                    ["fingerprint"] = JsonValue.CreateStringValue(toc.Fingerprint),
                    ["folder"] = JsonValue.CreateStringValue(discFolder.Name),
                    ["first_track"] = JsonValue.CreateNumberValue(toc.FirstTrack),
                    ["last_track"] = JsonValue.CreateNumberValue(toc.LastTrack),
                    ["track_count"] = JsonValue.CreateNumberValue(audioTracks.Count),
                    ["total_audio_sectors"] = JsonValue.CreateNumberValue(totalSectors),
                    ["sectors_read"] = JsonValue.CreateNumberValue(0),
                    ["progress_percent"] = JsonValue.CreateNumberValue(0),
                    ["pcm"] = JsonValue.CreateStringValue("16-bit stereo 44100 Hz WAV"),
                    ["eject"] = JsonValue.CreateBooleanValue(false)
                };
                var trackResults = new JsonArray();
                status["tracks"] = trackResults;

                await WriteStatusAsync(statusFile, status);
                await WritePointerAsync(status, discFolder.Name);

                var discStopwatch = Stopwatch.StartNew();

                foreach (var track in audioTracks)
                {
                    var sectors = track.EndLba - track.StartLba;
                    var dataBytes = checked(sectors * (long)RawSectorBytes);
                    if (dataBytes > uint.MaxValue - 36L)
                        throw new InvalidOperationException("Track " + track.Number + " is too large for RIFF/WAV.");

                    var fileName = "Track " + track.Number.ToString("00") + ".wav";
                    var wav = await discFolder.CreateFileAsync(
                        fileName,
                        CreationCollisionOption.ReplaceExisting);

                    status["current_track"] = JsonValue.CreateNumberValue(track.Number);
                    status["current_file"] = JsonValue.CreateStringValue(fileName);
                    await WriteStatusAsync(statusFile, status);

                    var hashProvider = HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Sha256);
                    var hash = hashProvider.CreateHash();
                    var trackStopwatch = Stopwatch.StartNew();
                    long trackRead = 0;
                    long lastReported = 0;

                    using (var stream = await wav.OpenAsync(FileAccessMode.ReadWrite))
                    {
                        stream.Size = (ulong)(44L + dataBytes);

                        using (var output = stream.GetOutputStreamAt(0))
                        using (var writer = new DataWriter(output))
                        {
                            writer.ByteOrder = ByteOrder.LittleEndian;
                            WriteWavHeader(writer, checked((uint)dataBytes));
                            await writer.StoreAsync();

                            long current = track.StartLba;
                            while (current < track.EndLba)
                            {
                                var count = (uint)Math.Min(ChunkSectors, track.EndLba - current);
                                var buffer = await ReadAudioAsync(device, current, count);
                                if (buffer == null || buffer.Length != count * RawSectorBytes)
                                    throw new InvalidOperationException(
                                        "Short CDDA read on track " + track.Number +
                                        " at LBA " + current + ".");

                                hash.Append(buffer);

                                using (var reader = DataReader.FromBuffer(buffer))
                                {
                                    var bytes = new byte[buffer.Length];
                                    reader.ReadBytes(bytes);
                                    writer.WriteBytes(bytes);
                                }
                                await writer.StoreAsync();

                                current += count;
                                trackRead += count;
                                discSectorsRead += count;

                                if (trackRead - lastReported >= 750 || current >= track.EndLba)
                                {
                                    lastReported = trackRead;
                                    status["track_sectors_read"] = JsonValue.CreateNumberValue(trackRead);
                                    status["track_sectors_total"] = JsonValue.CreateNumberValue(sectors);
                                    status["sectors_read"] = JsonValue.CreateNumberValue(discSectorsRead);
                                    status["progress_percent"] = JsonValue.CreateNumberValue(
                                        Math.Round(discSectorsRead * 100.0 / totalSectors, 1));
                                    status["elapsed_seconds"] = JsonValue.CreateNumberValue(
                                        Math.Round(discStopwatch.Elapsed.TotalSeconds, 2));
                                    await WriteStatusAsync(statusFile, status);
                                    await WritePointerAsync(status, discFolder.Name);
                                }
                            }

                            await writer.FlushAsync();
                        }
                    }

                    trackStopwatch.Stop();
                    var digest = hash.GetValueAndReset();
                    trackResults.Add(new JsonObject
                    {
                        ["track"] = JsonValue.CreateNumberValue(track.Number),
                        ["file"] = JsonValue.CreateStringValue(fileName),
                        ["start_lba"] = JsonValue.CreateNumberValue(track.StartLba),
                        ["end_lba"] = JsonValue.CreateNumberValue(track.EndLba),
                        ["sectors"] = JsonValue.CreateNumberValue(sectors),
                        ["audio_seconds"] = JsonValue.CreateNumberValue(sectors / 75.0),
                        ["wav_bytes"] = JsonValue.CreateNumberValue(44L + dataBytes),
                        ["sha256_audio"] = JsonValue.CreateStringValue(
                            CryptographicBuffer.EncodeToHexString(digest).ToUpperInvariant()),
                        ["elapsed_seconds"] = JsonValue.CreateNumberValue(
                            Math.Round(trackStopwatch.Elapsed.TotalSeconds, 2))
                    });

                    status["completed_tracks"] = JsonValue.CreateNumberValue(trackResults.Count);
                    await WriteStatusAsync(statusFile, status);
                    await WritePointerAsync(status, discFolder.Name);
                }

                discStopwatch.Stop();
                status["state"] = JsonValue.CreateStringValue("complete");
                status["current_track"] = JsonValue.CreateNumberValue(0);
                status["current_file"] = JsonValue.CreateStringValue("");
                status["sectors_read"] = JsonValue.CreateNumberValue(totalSectors);
                status["progress_percent"] = JsonValue.CreateNumberValue(100);
                status["elapsed_seconds"] = JsonValue.CreateNumberValue(
                    Math.Round(discStopwatch.Elapsed.TotalSeconds, 2));
                status["rip_speed_x"] = JsonValue.CreateNumberValue(
                    discStopwatch.Elapsed.TotalSeconds > 0
                        ? Math.Round((totalSectors / 75.0) / discStopwatch.Elapsed.TotalSeconds, 2)
                        : 0);

                await WriteStatusAsync(statusFile, status);
                await WritePointerAsync(status, discFolder.Name);
                return status;
            }
            catch (Exception ex)
            {
                return await FailAsync(
                    ex.GetType().Name + " 0x" + ex.HResult.ToString("X8") + ": " + ex.Message);
            }
            finally
            {
                _busy = false;
            }
        }

        async Task<TocInfo> ReadTocAsync(CustomDevice device)
        {
            var ioctl = new IOControlCode(
                (ushort)0x0002,
                (ushort)0x0000,
                IOControlAccessMode.Read,
                IOControlBufferingMethod.Buffered);

            var output = new WinBuffer(804);
            var ok = await device.TrySendIOControlAsync(ioctl, null, output);
            if (!ok || output.Length < 12)
                throw new InvalidOperationException("Could not read CD table of contents.");

            using (var reader = DataReader.FromBuffer(output))
            {
                var bytes = new byte[output.Length];
                reader.ReadBytes(bytes);
                var declared = (bytes[0] << 8) | bytes[1];
                var first = bytes[2];
                var last = bytes[3];
                var available = Math.Min(bytes.Length, declared + 2);

                var entries = new List<TocEntry>();
                for (var offset = 4; offset + 7 < available; offset += 8)
                {
                    entries.Add(new TocEntry
                    {
                        Number = bytes[offset + 2],
                        Control = bytes[offset + 1] & 0x0F,
                        Lba = MsfToLba(bytes[offset + 5], bytes[offset + 6], bytes[offset + 7])
                    });
                }

                var leadout = entries.FirstOrDefault(e => e.Number == 0xAA);
                if (leadout == null)
                    throw new InvalidOperationException("CD lead-out was not present in the TOC.");

                var tracks = new List<TrackRange>();
                var normal = entries.Where(e => e.Number >= 1 && e.Number <= 99)
                    .OrderBy(e => e.Number).ToList();

                for (var i = 0; i < normal.Count; i++)
                {
                    var entry = normal[i];
                    var end = i + 1 < normal.Count ? normal[i + 1].Lba : leadout.Lba;
                    tracks.Add(new TrackRange
                    {
                        Number = entry.Number,
                        StartLba = entry.Lba,
                        EndLba = end,
                        Audio = (entry.Control & 0x04) == 0
                    });
                }

                var canonical = first + "|" + last + "|" + leadout.Lba + "|" +
                    string.Join(",", tracks.Select(t =>
                        t.Number + ":" + t.StartLba + ":" + t.EndLba + ":" + (t.Audio ? "A" : "D")));

                var provider = HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Sha256);
                var digest = provider.HashData(CryptographicBuffer.ConvertStringToBinary(
                    canonical,
                    BinaryStringEncoding.Utf8));

                return new TocInfo
                {
                    FirstTrack = first,
                    LastTrack = last,
                    LeadoutLba = leadout.Lba,
                    Fingerprint = CryptographicBuffer.EncodeToHexString(digest).ToUpperInvariant(),
                    Tracks = tracks
                };
            }
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
                writer.WriteUInt32(2);
                input = writer.DetachBuffer();
            }

            var output = new WinBuffer(count * RawSectorBytes);
            var ok = await device.TrySendIOControlAsync(ioctl, input, output);
            if (!ok)
                throw new InvalidOperationException(
                    "IOCTL_CDROM_RAW_READ failed at LBA " + startSector + ".");

            return output;
        }

        static long MsfToLba(byte minute, byte second, byte frame)
        {
            return ((minute * 60L + second) * 75L + frame) - 150L;
        }

        static void WriteWavHeader(DataWriter writer, uint dataBytes)
        {
            writer.WriteBytes(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.WriteUInt32(36U + dataBytes);
            writer.WriteBytes(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            writer.WriteBytes(System.Text.Encoding.ASCII.GetBytes("fmt "));
            writer.WriteUInt32(16);
            writer.WriteUInt16(1);
            writer.WriteUInt16(2);
            writer.WriteUInt32(44100);
            writer.WriteUInt32(176400);
            writer.WriteUInt16(4);
            writer.WriteUInt16(16);
            writer.WriteBytes(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.WriteUInt32(dataBytes);
        }

        async Task<JsonObject> FailAsync(string message)
        {
            var status = new JsonObject
            {
                ["state"] = JsonValue.CreateStringValue("failed"),
                ["error"] = JsonValue.CreateStringValue(message ?? "Unknown CD rip error.")
            };
            try
            {
                await WritePointerAsync(status, "");
            }
            catch { }
            return status;
        }

        static JsonObject State(string state)
        {
            return new JsonObject
            {
                ["state"] = JsonValue.CreateStringValue(state)
            };
        }

        static async Task<StorageFolder> GetIngestFolderAsync()
        {
            var root = ApplicationData.Current.LocalFolder;
            var core = await root.CreateFolderAsync("Core", CreationCollisionOption.OpenIfExists);
            return await core.CreateFolderAsync("Ingest", CreationCollisionOption.OpenIfExists);
        }

        static async Task WritePointerAsync(JsonObject status, string folder)
        {
            var ingest = await GetIngestFolderAsync();
            var pointer = await ingest.CreateFileAsync(
                "auto-rip.json",
                CreationCollisionOption.ReplaceExisting);

            var copy = JsonObject.Parse(status.Stringify());
            copy["disc_folder"] = JsonValue.CreateStringValue(folder ?? "");
            await FileIO.WriteTextAsync(pointer, copy.Stringify());
        }

        static Task WriteStatusAsync(StorageFile file, JsonObject status)
        {
            return FileIO.WriteTextAsync(file, status.Stringify()).AsTask();
        }

        static async Task<JsonObject> TryReadJsonAsync(StorageFile file)
        {
            try
            {
                var text = await FileIO.ReadTextAsync(file);
                JsonObject result;
                return JsonObject.TryParse(text, out result) ? result : null;
            }
            catch
            {
                return null;
            }
        }

        static string GetString(JsonObject obj, string key)
        {
            return obj != null && obj.ContainsKey(key) &&
                   obj[key].ValueType == JsonValueType.String
                ? obj[key].GetString()
                : "";
        }

        sealed class TocEntry
        {
            public int Number;
            public int Control;
            public long Lba;
        }

        sealed class TrackRange
        {
            public int Number;
            public long StartLba;
            public long EndLba;
            public bool Audio;
        }

        sealed class TocInfo
        {
            public int FirstTrack;
            public int LastTrack;
            public long LeadoutLba;
            public string Fingerprint;
            public List<TrackRange> Tracks;
        }
    }
}
