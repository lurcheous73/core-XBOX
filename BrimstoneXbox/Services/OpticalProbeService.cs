using System;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Devices.Custom;
using Windows.Devices.Enumeration;
using Windows.Foundation.Metadata;
using Windows.Storage;
using Windows.Storage.Streams;
using WinBuffer = Windows.Storage.Streams.Buffer;

namespace BrimstoneXbox.Services
{
    public sealed class OpticalProbeService
    {
        static readonly Guid CdromInterfaceGuid =
            new Guid("53F56308-B6BF-11D0-94F2-00A0C91EFB8B");

        JsonObject _last = Empty("not_run");

        public JsonObject LastResult => _last;

        public async Task<JsonObject> ProbeAsync()
        {
            var result = new JsonObject
            {
                ["ok"] = JsonValue.CreateBooleanValue(true),
                ["interface_guid"] = JsonValue.CreateStringValue(CdromInterfaceGuid.ToString("B")),
                ["api_present"] = JsonValue.CreateBooleanValue(
                    ApiInformation.IsTypePresent("Windows.Devices.Custom.CustomDevice"))
            };

            var devicesJson = new JsonArray();
            result["devices"] = devicesJson;

            if (!ApiInformation.IsTypePresent("Windows.Devices.Custom.CustomDevice"))
            {
                result["status"] = JsonValue.CreateStringValue("custom_device_api_unavailable");
                _last = result;
                await PersistAsync(result);
                return result;
            }

            try
            {
                var selector = CustomDevice.GetDeviceSelector(CdromInterfaceGuid);
                result["selector"] = JsonValue.CreateStringValue(selector ?? "");

                var devices = await DeviceInformation.FindAllAsync(selector);
                result["device_count"] = JsonValue.CreateNumberValue(devices.Count);

                foreach (var device in devices)
                {
                    var row = new JsonObject
                    {
                        ["id"] = JsonValue.CreateStringValue(device.Id ?? ""),
                        ["name"] = JsonValue.CreateStringValue(device.Name ?? ""),
                        ["enabled"] = JsonValue.CreateBooleanValue(device.IsEnabled),
                        ["default"] = JsonValue.CreateBooleanValue(device.IsDefault)
                    };

                    try
                    {
                        var access = DeviceAccessInformation.CreateFromId(device.Id);
                        row["access"] = JsonValue.CreateStringValue(
                            access.CurrentStatus.ToString());
                    }
                    catch (Exception ex)
                    {
                        row["access"] = JsonValue.CreateStringValue("unknown");
                        row["access_error"] = JsonValue.CreateStringValue(Describe(ex));
                    }

                    CustomDevice custom = null;
                    try
                    {
                        custom = await CustomDevice.FromIdAsync(
                            device.Id,
                            DeviceAccessMode.Read,
                            DeviceSharingMode.Shared);

                        row["raw_open"] = JsonValue.CreateBooleanValue(custom != null);

                        if (custom != null)
                        {
                            var toc = await ReadTocAsync(custom);
                            row["toc"] = toc;

                            var rawSector = await ReadRawAudioSectorAsync(custom);
                            row["raw_sector_test"] = rawSector;
                        }
                    }
                    catch (Exception ex)
                    {
                        row["raw_open"] = JsonValue.CreateBooleanValue(false);
                        row["raw_open_error"] = JsonValue.CreateStringValue(Describe(ex));
                    }
                    devicesJson.Add(row);
                }

                result["status"] = JsonValue.CreateStringValue(
                    devices.Count > 0 ? "devices_found" : "no_cdrom_interface");
            }
            catch (Exception ex)
            {
                result["ok"] = JsonValue.CreateBooleanValue(false);
                result["status"] = JsonValue.CreateStringValue("probe_failed");
                result["error"] = JsonValue.CreateStringValue(Describe(ex));
            }

            _last = result;
            await PersistAsync(result);
            return result;
        }

        async Task PersistAsync(JsonObject result)
        {
            try
            {
                var root = ApplicationData.Current.LocalFolder;
                var core = await root.CreateFolderAsync("Core", CreationCollisionOption.OpenIfExists);
                var ingest = await core.CreateFolderAsync("Ingest", CreationCollisionOption.OpenIfExists);
                var file = await ingest.CreateFileAsync(
                    "optical-probe.json",
                    CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, result.Stringify());
            }
            catch
            {
                // Diagnostic persistence must never make optical probing fail.
            }
        }

        async Task<JsonObject> ReadRawAudioSectorAsync(CustomDevice device)
        {
            // IOCTL_CDROM_RAW_READ:
            // CTL_CODE(FILE_DEVICE_CD_ROM=2, function=0x000F,
            //          METHOD_OUT_DIRECT, FILE_READ_ACCESS)
            //
            // RAW_READ_INFO is:
            //   LARGE_INTEGER DiskOffset (8)
            //   ULONG SectorCount       (4)
            //   TRACK_MODE_TYPE          (4)
            //
            // For the first audio sector: offset 0, one sector, CDDA (2).
            var ioctl = new IOControlCode(
                (ushort)0x0002,
                (ushort)0x000F,
                IOControlAccessMode.Read,
                IOControlBufferingMethod.DirectOutput);

            IBuffer input;
            using (var writer = new DataWriter())
            {
                writer.ByteOrder = ByteOrder.LittleEndian;
                writer.WriteInt64(0);
                writer.WriteUInt32(1);
                writer.WriteUInt32(2); // TRACK_MODE_TYPE.CDDA
                input = writer.DetachBuffer();
            }

            var output = new WinBuffer(2352);
            var ok = await device.TrySendIOControlAsync(ioctl, input, output);

            var result = new JsonObject
            {
                ["ioctl"] = JsonValue.CreateStringValue("IOCTL_CDROM_RAW_READ"),
                ["success"] = JsonValue.CreateBooleanValue(ok),
                ["requested_sectors"] = JsonValue.CreateNumberValue(1),
                ["requested_bytes"] = JsonValue.CreateNumberValue(2352),
                ["bytes"] = JsonValue.CreateNumberValue(output.Length),
                ["track_mode"] = JsonValue.CreateStringValue("CDDA")
            };

            if (!ok || output.Length == 0)
                return result;

            using (var reader = DataReader.FromBuffer(output))
            {
                var bytes = new byte[output.Length];
                reader.ReadBytes(bytes);

                var nonZero = 0;
                for (var i = 0; i < bytes.Length; i++)
                    if (bytes[i] != 0) nonZero++;

                var previewLength = Math.Min(32, bytes.Length);
                var preview = new byte[previewLength];
                Array.Copy(bytes, preview, previewLength);

                result["non_zero_bytes"] = JsonValue.CreateNumberValue(nonZero);
                result["first_32_hex"] = JsonValue.CreateStringValue(
                    BitConverter.ToString(preview).Replace("-", ""));
            }

            return result;
        }

        async Task<JsonObject> ReadTocAsync(CustomDevice device)
        {
            // IOCTL_CDROM_READ_TOC:
            // CTL_CODE(FILE_DEVICE_CD_ROM=2, function=0, METHOD_BUFFERED, FILE_READ_ACCESS)
            var ioctl = new IOControlCode(
                (ushort)0x0002,
                (ushort)0x0000,
                IOControlAccessMode.Read,
                IOControlBufferingMethod.Buffered);

            var output = new WinBuffer(804);
            var ok = await device.TrySendIOControlAsync(ioctl, null, output);

            var toc = new JsonObject
            {
                ["ioctl"] = JsonValue.CreateStringValue("IOCTL_CDROM_READ_TOC"),
                ["success"] = JsonValue.CreateBooleanValue(ok),
                ["bytes"] = JsonValue.CreateNumberValue(output.Length)
            };

            if (!ok || output.Length < 4)
                return toc;

            using (var reader = DataReader.FromBuffer(output))
            {
                var bytes = new byte[output.Length];
                reader.ReadBytes(bytes);

                var declared = (bytes[0] << 8) | bytes[1];
                var firstTrack = bytes[2];
                var lastTrack = bytes[3];

                toc["declared_length"] = JsonValue.CreateNumberValue(declared);
                toc["first_track"] = JsonValue.CreateNumberValue(firstTrack);
                toc["last_track"] = JsonValue.CreateNumberValue(lastTrack);

                var tracks = new JsonArray();
                var available = Math.Min(bytes.Length, declared + 2);
                for (var offset = 4; offset + 7 < available; offset += 8)
                {
                    var track = bytes[offset + 2];
                    var adrControl = bytes[offset + 1];
                    var address =
                        ((uint)bytes[offset + 4] << 24) |
                        ((uint)bytes[offset + 5] << 16) |
                        ((uint)bytes[offset + 6] << 8) |
                        bytes[offset + 7];

                    tracks.Add(new JsonObject
                    {
                        ["track"] = JsonValue.CreateNumberValue(track),
                        ["control"] = JsonValue.CreateNumberValue(adrControl & 0x0F),
                        ["adr"] = JsonValue.CreateNumberValue((adrControl >> 4) & 0x0F),
                        ["address"] = JsonValue.CreateNumberValue(address)
                    });
                }
                toc["entries"] = tracks;
            }

            return toc;
        }

        static string Describe(Exception ex)
        {
            return ex.GetType().Name + " 0x" +
                   ex.HResult.ToString("X8") + ": " + ex.Message;
        }

        static JsonObject Empty(string status)
        {
            return new JsonObject
            {
                ["ok"] = JsonValue.CreateBooleanValue(false),
                ["status"] = JsonValue.CreateStringValue(status)
            };
        }
    }
}
