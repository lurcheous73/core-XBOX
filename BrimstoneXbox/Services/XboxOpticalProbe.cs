using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Devices.Enumeration;
using Windows.Storage;

namespace BrimstoneXbox.Services
{
    public sealed class XboxOpticalProbe
    {
        static readonly string CdromClassGuid = "{4d36e965-e325-11ce-bfc1-08002be10318}";
        static readonly string CdromInterfaceGuid = "{53f56308-b6bf-11d0-94f2-00a0c91efb8b}";

        public async Task<JsonObject> ProbeAsync()
        {
            var result = new JsonObject
            {
                ["ok"] = JsonValue.CreateBooleanValue(true),
                ["class_guid"] = JsonValue.CreateStringValue(CdromClassGuid),
                ["interface_guid"] = JsonValue.CreateStringValue(CdromInterfaceGuid)
            };

            var errors = new JsonArray();
            result["errors"] = errors;

            try
            {
                var props = new[]
                {
                    "System.Devices.ClassGuid",
                    "System.Devices.DeviceInstanceId",
                    "System.Devices.Present",
                    "System.Devices.ProblemCode",
                    "System.Devices.Parent"
                };

                var selector = "System.Devices.ClassGuid:=\""+CdromClassGuid+"\"";
                var devices = await DeviceInformation.FindAllAsync(
                    selector, props, DeviceInformationKind.Device);

                result["devices"] = DeviceArray(devices);
                result["device_count"] = JsonValue.CreateNumberValue(devices.Count);
            }
            catch (Exception ex)
            {
                errors.Add(JsonValue.CreateStringValue("Device enumeration: " + ex.Message));
                result["devices"] = new JsonArray();
                result["device_count"] = JsonValue.CreateNumberValue(0);
            }

            try
            {
                var props = new[]
                {
                    "System.Device.InterfaceClassGuid",
                    "System.Devices.DeviceInstanceId",
                    "System.Devices.Present"
                };

                var selector = "System.Device.InterfaceClassGuid:=\""+CdromInterfaceGuid+"\"";
                var interfaces = await DeviceInformation.FindAllAsync(
                    selector, props, DeviceInformationKind.DeviceInterface);

                result["interfaces"] = DeviceArray(interfaces);
                result["interface_count"] = JsonValue.CreateNumberValue(interfaces.Count);
            }
            catch (Exception ex)
            {
                errors.Add(JsonValue.CreateStringValue("Interface enumeration: " + ex.Message));
                result["interfaces"] = new JsonArray();
                result["interface_count"] = JsonValue.CreateNumberValue(0);
            }

            try
            {
                var folders = await KnownFolders.RemovableDevices.GetFoldersAsync();
                var removable = new JsonArray();

                foreach (var folder in folders)
                {
                    var row = new JsonObject
                    {
                        ["name"] = JsonValue.CreateStringValue(folder.Name ?? ""),
                        ["path"] = JsonValue.CreateStringValue(folder.Path ?? "")
                    };

                    try
                    {
                        var files = await folder.GetFilesAsync();
                        row["file_count"] = JsonValue.CreateNumberValue(files.Count);
                    }
                    catch (Exception ex)
                    {
                        row["files_error"] = JsonValue.CreateStringValue(ex.Message);
                    }

                    removable.Add(row);
                }

                result["removable"] = removable;
                result["removable_count"] = JsonValue.CreateNumberValue(folders.Count);
            }
            catch (Exception ex)
            {
                errors.Add(JsonValue.CreateStringValue("Removable media: " + ex.Message));
                result["removable"] = new JsonArray();
                result["removable_count"] = JsonValue.CreateNumberValue(0);
            }

            var deviceCount = Number(result, "device_count");
            var interfaceCount = Number(result, "interface_count");
            var removableCount = Number(result, "removable_count");

            result["hardware_visible"] = JsonValue.CreateBooleanValue(deviceCount > 0);
            result["interface_visible"] = JsonValue.CreateBooleanValue(interfaceCount > 0);
            result["mounted_media_visible"] = JsonValue.CreateBooleanValue(removableCount > 0);

            return result;
        }

        static JsonArray DeviceArray(DeviceInformationCollection devices)
        {
            var rows = new JsonArray();
            foreach (var device in devices)
            {
                var row = new JsonObject
                {
                    ["id"] = JsonValue.CreateStringValue(device.Id ?? ""),
                    ["name"] = JsonValue.CreateStringValue(device.Name ?? ""),
                    ["kind"] = JsonValue.CreateStringValue(device.Kind.ToString()),
                    ["enabled"] = JsonValue.CreateBooleanValue(device.IsEnabled)
                };

                foreach (var pair in device.Properties)
                {
                    if (pair.Value == null) continue;
                    var key = pair.Key ?? "";
                    if (pair.Value is bool)
                        row[key] = JsonValue.CreateBooleanValue((bool)pair.Value);
                    else if (pair.Value is int)
                        row[key] = JsonValue.CreateNumberValue((int)pair.Value);
                    else if (pair.Value is uint)
                        row[key] = JsonValue.CreateNumberValue((uint)pair.Value);
                    else
                        row[key] = JsonValue.CreateStringValue(pair.Value.ToString());
                }
                rows.Add(row);
            }
            return rows;
        }

        static double Number(JsonObject obj, string key)
        {
            return obj.ContainsKey(key) && obj[key].ValueType == JsonValueType.Number
                ? obj[key].GetNumber()
                : 0;
        }
    }
}
