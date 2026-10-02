using DiscUtils.Udf;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;
using Windows.Storage.Streams;

namespace BrimstoneXbox.Services
{
    public sealed class BluRayAudioService
    {
        readonly ApplicationDataContainer _settings = ApplicationData.Current.LocalSettings;
        JsonObject _lastScan = State("not_run");
        JsonObject _lastRip = State("idle");
        bool _ripBusy;

        public JsonObject LastScan => _lastScan;
        public JsonObject LastRip => _lastRip;
        public bool Busy => _ripBusy;

        public async Task<JsonObject> ScanAsync()
        {
            var removable = KnownFolders.RemovableDevices;
            var volumes = await removable.GetFoldersAsync();

            foreach (var volume in volumes)
            {
                var scan = await ScanVolumeAsync(volume);
                if (GetBool(scan, "available"))
                {
                    _lastScan = scan;
                    await PersistAsync("bluray-scan.json", scan);
                    return scan;
                }
            }

            // Xbox commonly exposes optical UDF through the raw CD-ROM device
            // without mounting it into KnownFolders.RemovableDevices. Fall
            // through to the same SCSI/UDF path used by the optical probe.
            var raw = await ScanRawUdfAsync();
            if (GetBool(raw, "available"))
            {
                _lastScan = raw;
                await PersistAsync("bluray-scan.json", raw);
                return raw;
            }

            _lastScan = new JsonObject
            {
                ["state"] = JsonValue.CreateStringValue(
                    GetString(raw, "state", "no_bluray")),
                ["available"] = JsonValue.CreateBooleanValue(false),
                ["disc_type"] = JsonValue.CreateStringValue(""),
                ["title_count"] = JsonValue.CreateNumberValue(0),
                ["titles"] = new JsonArray()
            };
            if (raw.ContainsKey("error"))
                _lastScan["error"] = raw["error"];
            await PersistAsync("bluray-scan.json", _lastScan);
            return _lastScan;
        }

        public async Task<JsonObject> CreateRipPlanAsync(
            string playlistFile,
            bool keepVideo)
        {
            var scan = await ScanAsync();
            if (!GetBool(scan, "available"))
                throw new InvalidOperationException("No readable Blu-ray BDMV volume is available.");

            if (string.IsNullOrWhiteSpace(playlistFile))
            {
                var recommended = FindRecommended(scan);
                playlistFile = recommended == null
                    ? ""
                    : GetString(recommended, "playlist", "");
            }

            var title = FindTitle(scan, playlistFile);
            if (title == null)
                throw new ArgumentException("The requested Blu-ray playlist was not found.");

            var protection = GetBool(scan, "protection_detected");
            var readable = GetBool(scan, "streams_readable");
            var decrypted = GetBool(scan, "streams_decrypted");
            var nativeReadable = readable && (!protection || decrypted);

            var plan = new JsonObject
            {
                ["state"] = JsonValue.CreateStringValue("ready"),
                ["disc_type"] = JsonValue.CreateStringValue("bluray-audio"),
                ["disc_label"] = JsonValue.CreateStringValue(GetString(scan, "disc_label", "")),
                ["playlist"] = JsonValue.CreateStringValue(GetString(title, "playlist", "")),
                ["playlist_id"] = JsonValue.CreateStringValue(GetString(title, "playlist_id", "")),
                ["duration_seconds"] = JsonValue.CreateNumberValue(GetNumber(title, "duration_seconds", 0)),
                ["output_container"] = JsonValue.CreateStringValue("mkv"),
                ["audio_policy"] = JsonValue.CreateStringValue("copy"),
                ["video_policy"] = JsonValue.CreateStringValue(keepVideo ? "copy" : "discard"),
                ["keep_video"] = JsonValue.CreateBooleanValue(keepVideo),
                ["preserve_chapters"] = JsonValue.CreateBooleanValue(true),
                ["protection_detected"] = JsonValue.CreateBooleanValue(protection),
                ["streams_readable"] = JsonValue.CreateBooleanValue(readable),
                ["streams_decrypted"] = JsonValue.CreateBooleanValue(decrypted),
                ["disc_key"] = JsonValue.CreateStringValue(GetString(scan, "disc_key", "")),
                ["backend"] = JsonValue.CreateStringValue(
                    nativeReadable ? "xbox-mkv-remux" : "makemkv-helper"),
                ["backend_state"] = JsonValue.CreateStringValue(
                    nativeReadable ? "remux_engine_pending" : "helper_required"),
                ["clips"] = title.GetNamedArray("clips", new JsonArray()),
                ["chapters"] = title.GetNamedArray("chapters", new JsonArray()),
                ["chapter_count"] = JsonValue.CreateNumberValue(
                    title.GetNamedArray("chapters", new JsonArray()).Count),
                ["created_utc"] = JsonValue.CreateStringValue(DateTimeOffset.UtcNow.ToString("o"))
            };

            plan["helper_contract"] = new JsonObject
            {
                ["tool"] = JsonValue.CreateStringValue("makemkvcon"),
                ["mode"] = JsonValue.CreateStringValue("lossless-remux"),
                ["selection"] = JsonValue.CreateStringValue("playlist"),
                ["playlist"] = JsonValue.CreateStringValue(GetString(title, "playlist", "")),
                ["return_container"] = JsonValue.CreateStringValue("mkv")
            };

            await PersistAsync("bluray-rip-plan.json", plan);
            return plan;
        }

        public async Task<JsonObject> AutoRipCurrentDiscAsync()
        {
            var scan = await ScanAsync();
            if (!GetBool(scan, "available") || GetNumber(scan, "title_count", 0) <= 0)
                return State("idle");

            var discKey = GetString(scan, "disc_key", "");
            object saved;
            var lastKey = _settings.Values.TryGetValue("blurayLastAutoRipKey", out saved)
                ? saved as string
                : "";

            if (!string.IsNullOrWhiteSpace(discKey) &&
                string.Equals(discKey, lastKey, StringComparison.Ordinal))
                return await CurrentRipStatusAsync();

            var result = await RipSelectedTitleAsync("", false);
            var state = GetString(result, "state", "");

            if (!string.IsNullOrWhiteSpace(discKey) &&
                (state == "source_staged" || state == "helper_required"))
                _settings.Values["blurayLastAutoRipKey"] = discKey;

            return result;
        }

        public async Task<JsonObject> RipSelectedTitleAsync(
            string playlistFile,
            bool keepVideo)
        {
            if (_ripBusy)
                throw new InvalidOperationException("A Blu-ray rip is already running.");

            var plan = await CreateRipPlanAsync(playlistFile, keepVideo);
            if (string.Equals(GetString(plan, "backend", ""), "makemkv-helper", StringComparison.OrdinalIgnoreCase))
            {
                _lastRip = new JsonObject
                {
                    ["state"] = JsonValue.CreateStringValue("helper_required"),
                    ["disc_type"] = JsonValue.CreateStringValue("bluray-audio"),
                    ["playlist"] = JsonValue.CreateStringValue(GetString(plan, "playlist", "")),
                    ["backend"] = JsonValue.CreateStringValue("makemkv-helper"),
                    ["error"] = JsonValue.CreateStringValue(
                        "AACS-protected Blu-ray needs a MakeMKV-capable helper with direct access to the optical disc.")
                };
                await PersistAsync("bluray-rip-status.json", _lastRip);
                var completedDiscKey = GetString(plan, "disc_key", "");
                if (!string.IsNullOrWhiteSpace(completedDiscKey))
                    _settings.Values["blurayLastAutoRipKey"] = completedDiscKey;
                return _lastRip;
            }

            _ripBusy = true;
            try
            {
                var removable = KnownFolders.RemovableDevices;
                var volumes = await removable.GetFoldersAsync();
                StorageFolder selectedVolume = null;
                StorageFolder streamFolder = null;

                foreach (var volume in volumes)
                {
                    try
                    {
                        var bdmv = await volume.GetFolderAsync("BDMV");
                        var stream = await bdmv.GetFolderAsync("STREAM");
                        selectedVolume = volume;
                        streamFolder = stream;
                        break;
                    }
                    catch
                    {
                    }
                }

                if (selectedVolume == null || streamFolder == null)
                    throw new InvalidOperationException("The Blu-ray STREAM folder is not readable.");

                var clips = plan.GetNamedArray("clips", new JsonArray());
                if (clips.Count == 0)
                    throw new InvalidOperationException("The selected Blu-ray title contains no clips.");

                var root = ApplicationData.Current.LocalFolder;
                var core = await root.CreateFolderAsync(
                    "Core", CreationCollisionOption.OpenIfExists);
                var ingest = await core.CreateFolderAsync(
                    "Ingest", CreationCollisionOption.OpenIfExists);
                var bluRayRoot = await ingest.CreateFolderAsync(
                    "BluRay", CreationCollisionOption.OpenIfExists);

                var safeLabel = SafeName(GetString(plan, "disc_label", "Blu-ray"));
                var playlistId = SafeName(GetString(plan, "playlist_id", "title"));
                var folderName = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") +
                    "-" + safeLabel + "-" + playlistId;
                var jobFolder = await bluRayRoot.CreateFolderAsync(
                    folderName, CreationCollisionOption.GenerateUniqueName);
                var sourceFolder = await jobFolder.CreateFolderAsync(
                    "SOURCE", CreationCollisionOption.OpenIfExists);

                var uniqueFiles = new List<string>();
                foreach (var value in clips)
                {
                    if (value.ValueType != JsonValueType.Object)
                        continue;

                    var name = GetString(value.GetObject(), "stream_file", "");
                    if (!string.IsNullOrWhiteSpace(name) &&
                        !uniqueFiles.Any(x => string.Equals(
                            x, name, StringComparison.OrdinalIgnoreCase)))
                        uniqueFiles.Add(name);
                }

                ulong totalBytes = 0;
                var sources = new List<SourceFile>();
                foreach (var name in uniqueFiles)
                {
                    var source = await streamFolder.GetFileAsync(name);
                    var props = await source.GetBasicPropertiesAsync();
                    totalBytes += props.Size;
                    sources.Add(new SourceFile
                    {
                        File = source,
                        Name = name,
                        Bytes = props.Size
                    });
                }

                ulong copiedBytes = 0;
                var copied = new JsonArray();
                _lastRip = new JsonObject
                {
                    ["state"] = JsonValue.CreateStringValue("ripping"),
                    ["disc_type"] = JsonValue.CreateStringValue("bluray-audio"),
                    ["disc_label"] = JsonValue.CreateStringValue(selectedVolume.Name ?? ""),
                    ["playlist"] = JsonValue.CreateStringValue(GetString(plan, "playlist", "")),
                    ["playlist_id"] = JsonValue.CreateStringValue(GetString(plan, "playlist_id", "")),
                    ["folder"] = JsonValue.CreateStringValue(jobFolder.Name ?? ""),
                    ["file_count"] = JsonValue.CreateNumberValue(sources.Count),
                    ["completed_files"] = JsonValue.CreateNumberValue(0),
                    ["bytes_total"] = JsonValue.CreateNumberValue(totalBytes),
                    ["bytes_copied"] = JsonValue.CreateNumberValue(0),
                    ["progress_percent"] = JsonValue.CreateNumberValue(0),
                    ["current_file"] = JsonValue.CreateStringValue(""),
                    ["files"] = copied,
                    ["started_utc"] = JsonValue.CreateStringValue(DateTimeOffset.UtcNow.ToString("o"))
                };
                await PersistAsync("bluray-rip-status.json", _lastRip);

                for (var i = 0; i < sources.Count; i++)
                {
                    var source = sources[i];
                    _lastRip["current_file"] =
                        JsonValue.CreateStringValue(source.Name ?? "");
                    await PersistAsync("bluray-rip-status.json", _lastRip);

                    var targetName = (i + 1).ToString("000") + "-" + source.Name;
                    var target = await source.File.CopyAsync(
                        sourceFolder,
                        targetName,
                        NameCollisionOption.ReplaceExisting);

                    copiedBytes += source.Bytes;
                    copied.Add(new JsonObject
                    {
                        ["source"] = JsonValue.CreateStringValue(source.Name ?? ""),
                        ["file"] = JsonValue.CreateStringValue(target.Name ?? targetName),
                        ["bytes"] = JsonValue.CreateNumberValue(source.Bytes)
                    });

                    _lastRip["completed_files"] =
                        JsonValue.CreateNumberValue(i + 1);
                    _lastRip["bytes_copied"] =
                        JsonValue.CreateNumberValue(copiedBytes);
                    _lastRip["progress_percent"] =
                        JsonValue.CreateNumberValue(totalBytes == 0
                            ? 0
                            : Math.Round(copiedBytes * 100.0 / totalBytes, 1));
                    await PersistAsync("bluray-rip-status.json", _lastRip);
                }

                var manifest = JsonObject.Parse(plan.Stringify());
                manifest["state"] = JsonValue.CreateStringValue("source_staged");
                manifest["source_folder"] = JsonValue.CreateStringValue(
                    "Core/Ingest/BluRay/" + jobFolder.Name + "/SOURCE");
                manifest["staged_files"] = copied;
                manifest["completed_utc"] =
                    JsonValue.CreateStringValue(DateTimeOffset.UtcNow.ToString("o"));

                var manifestFile = await jobFolder.CreateFileAsync(
                    "title-manifest.json",
                    CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(manifestFile, manifest.Stringify());

                _lastRip["state"] = JsonValue.CreateStringValue("source_staged");
                _lastRip["current_file"] = JsonValue.CreateStringValue("");
                _lastRip["progress_percent"] = JsonValue.CreateNumberValue(100);
                _lastRip["manifest"] = JsonValue.CreateStringValue(
                    "Core/Ingest/BluRay/" + jobFolder.Name + "/title-manifest.json");
                _lastRip["backend_state"] =
                    JsonValue.CreateStringValue("remux_engine_pending");
                _lastRip["completed_utc"] =
                    JsonValue.CreateStringValue(DateTimeOffset.UtcNow.ToString("o"));

                await PersistAsync("bluray-rip-status.json", _lastRip);
                return _lastRip;
            }
            catch (Exception ex)
            {
                _lastRip = new JsonObject
                {
                    ["state"] = JsonValue.CreateStringValue("failed"),
                    ["error"] = JsonValue.CreateStringValue(Describe(ex))
                };
                await PersistAsync("bluray-rip-status.json", _lastRip);
                return _lastRip;
            }
            finally
            {
                _ripBusy = false;
            }
        }

        public async Task<JsonObject> CurrentRipStatusAsync()
        {
            try
            {
                var root = ApplicationData.Current.LocalFolder;
                var core = await root.GetFolderAsync("Core");
                var ingest = await core.GetFolderAsync("Ingest");
                var file = await ingest.GetFileAsync("bluray-rip-status.json");
                var text = await FileIO.ReadTextAsync(file);
                JsonObject parsed;
                if (JsonObject.TryParse(text, out parsed))
                {
                    _lastRip = parsed;
                    return parsed;
                }
            }
            catch
            {
            }

            return _lastRip ?? State("idle");
        }

        static string SafeName(string value)
        {
            var raw = string.IsNullOrWhiteSpace(value) ? "disc" : value.Trim();
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var cleaned = new string(raw
                .Select(ch => invalid.Contains(ch) ? '-' : ch)
                .ToArray())
                .Trim()
                .TrimEnd('.');
            return string.IsNullOrWhiteSpace(cleaned) ? "disc" : cleaned;
        }

        async Task<JsonObject> ScanRawUdfAsync()
        {
            try
            {
                var optical = await ScsiOpticalStream.OpenAsync();

                return await Task.Run(() =>
                {
                    using (optical)
                    {
                        optical.Position = 0;
                        if (!UdfReader.Detect(optical))
                            return State("no_udf");

                        optical.Position = 0;
                        using (var udf = new UdfReader(
                            optical,
                            optical.SectorSize))
                        {
                            if (!udf.DirectoryExists(@"BDMV") ||
                                !udf.DirectoryExists(@"BDMV\PLAYLIST") ||
                                !udf.DirectoryExists(@"BDMV\STREAM"))
                                return State("not_bluray");

                            var protection =
                                udf.DirectoryExists(@"AACS");
                            var streamPaths = udf.GetFiles(
                                @"BDMV\STREAM",
                                "*.m2ts",
                                SearchOption.TopDirectoryOnly);
                            var streamNames = new HashSet<string>(
                                streamPaths.Select(
                                    System.IO.Path.GetFileName),
                                StringComparer.OrdinalIgnoreCase);

                            var streamsReadable =
                                streamPaths != null &&
                                streamPaths.Length > 0;
                            var streamsDecrypted = false;

                            // If AACS is present on the raw optical filesystem,
                            // do not mistake MPEG-TS framing for decrypted media.
                            // A mounted/decrypted source can still use the normal
                            // StorageFolder path above.
                            if (!protection && streamsReadable)
                            {
                                using (var sample = udf.OpenFile(
                                    streamPaths[0],
                                    FileMode.Open,
                                    FileAccess.Read))
                                {
                                    streamsDecrypted =
                                        LooksLikeReadableM2ts(sample);
                                }
                            }

                            var playlistPaths = udf.GetFiles(
                                @"BDMV\PLAYLIST",
                                "*.mpls",
                                SearchOption.TopDirectoryOnly);
                            var parsed = new List<PlaylistInfo>();
                            var parseErrors = new JsonArray();

                            foreach (var path in playlistPaths
                                .OrderBy(
                                    value => value,
                                    StringComparer.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    byte[] bytes;
                                    using (var source = udf.OpenFile(
                                        path,
                                        FileMode.Open,
                                        FileAccess.Read))
                                    {
                                        bytes = ReadAllBytes(source);
                                    }

                                    var info = ParsePlaylist(
                                        System.IO.Path.GetFileName(path),
                                        bytes,
                                        streamNames);
                                    if (info.Clips.Count > 0 &&
                                        info.DurationTicks > 0)
                                        parsed.Add(info);
                                }
                                catch (Exception ex)
                                {
                                    parseErrors.Add(new JsonObject
                                    {
                                        ["playlist"] =
                                            JsonValue.CreateStringValue(
                                                System.IO.Path.GetFileName(
                                                    path) ?? ""),
                                        ["error"] =
                                            JsonValue.CreateStringValue(
                                                Describe(ex))
                                    });
                                }
                            }

                            var ordered = parsed
                                .OrderByDescending(
                                    value => value.DurationTicks)
                                .ThenBy(
                                    value => value.FileName,
                                    StringComparer.OrdinalIgnoreCase)
                                .ToList();

                            var seen = new HashSet<string>(
                                StringComparer.Ordinal);
                            var unique = new List<PlaylistInfo>();
                            var duplicateCount = 0;
                            foreach (var item in ordered)
                            {
                                if (!seen.Add(item.Signature()))
                                {
                                    duplicateCount++;
                                    continue;
                                }
                                unique.Add(item);
                            }

                            var titles = new JsonArray();
                            for (var index = 0;
                                 index < unique.Count;
                                 index++)
                                titles.Add(ToJson(
                                    unique[index],
                                    index == 0));

                            var label = udf.VolumeLabel ?? "";
                            return new JsonObject
                            {
                                ["state"] =
                                    JsonValue.CreateStringValue("ready"),
                                ["available"] =
                                    JsonValue.CreateBooleanValue(true),
                                ["disc_type"] =
                                    JsonValue.CreateStringValue("bluray"),
                                ["source"] =
                                    JsonValue.CreateStringValue("raw-scsi-udf"),
                                ["disc_label"] =
                                    JsonValue.CreateStringValue(label),
                                ["protection_detected"] =
                                    JsonValue.CreateBooleanValue(protection),
                                ["streams_readable"] =
                                    JsonValue.CreateBooleanValue(
                                        streamsReadable),
                                ["streams_decrypted"] =
                                    JsonValue.CreateBooleanValue(
                                        streamsDecrypted),
                                ["disc_key"] =
                                    JsonValue.CreateStringValue(
                                        BuildDiscKey(label, unique)),
                                ["title_count"] =
                                    JsonValue.CreateNumberValue(unique.Count),
                                ["duplicate_playlists_filtered"] =
                                    JsonValue.CreateNumberValue(
                                        duplicateCount),
                                ["titles"] = titles,
                                ["parse_errors"] = parseErrors
                            };
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                return new JsonObject
                {
                    ["state"] =
                        JsonValue.CreateStringValue(
                            "raw_udf_scan_failed"),
                    ["available"] =
                        JsonValue.CreateBooleanValue(false),
                    ["error"] =
                        JsonValue.CreateStringValue(Describe(ex))
                };
            }
        }

        async Task<JsonObject> ScanVolumeAsync(StorageFolder volume)
        {
            StorageFolder bdmv;
            try
            {
                bdmv = await volume.GetFolderAsync("BDMV");
            }
            catch
            {
                return State("not_bluray");
            }

            StorageFolder playlistFolder;
            StorageFolder streamFolder;
            try
            {
                playlistFolder = await bdmv.GetFolderAsync("PLAYLIST");
                streamFolder = await bdmv.GetFolderAsync("STREAM");
            }
            catch (Exception ex)
            {
                return new JsonObject
                {
                    ["state"] = JsonValue.CreateStringValue("bdmv_incomplete"),
                    ["available"] = JsonValue.CreateBooleanValue(false),
                    ["disc_label"] = JsonValue.CreateStringValue(volume.Name ?? ""),
                    ["error"] = JsonValue.CreateStringValue(Describe(ex))
                };
            }

            var protection = await HasFolderAsync(volume, "AACS");
            var streamNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var streamsReadable = true;
            var streamsDecrypted = false;

            try
            {
                var streamFiles = await streamFolder.GetFilesAsync();
                foreach (var file in streamFiles)
                    streamNames.Add(file.Name ?? "");

                var firstM2ts = streamFiles.FirstOrDefault(file =>
                    string.Equals(System.IO.Path.GetExtension(file.Name), ".m2ts",
                        StringComparison.OrdinalIgnoreCase));
                if (firstM2ts != null)
                    streamsDecrypted = await LooksLikeReadableM2tsAsync(firstM2ts);
            }
            catch
            {
                streamsReadable = false;
            }

            var playlistFiles = await playlistFolder.GetFilesAsync();
            var parsed = new List<PlaylistInfo>();
            var parseErrors = new JsonArray();

            foreach (var file in playlistFiles
                .Where(f => string.Equals(
                    System.IO.Path.GetExtension(f.Name),
                    ".mpls",
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Name))
            {
                try
                {
                    var bytes = await ReadBytesAsync(file);
                    var info = ParsePlaylist(file.Name, bytes, streamNames);
                    if (info.Clips.Count > 0 && info.DurationTicks > 0)
                        parsed.Add(info);
                }
                catch (Exception ex)
                {
                    parseErrors.Add(new JsonObject
                    {
                        ["playlist"] = JsonValue.CreateStringValue(file.Name ?? ""),
                        ["error"] = JsonValue.CreateStringValue(Describe(ex))
                    });
                }
            }

            var ordered = parsed
                .OrderByDescending(p => p.DurationTicks)
                .ThenBy(p => p.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var unique = new List<PlaylistInfo>();
            var duplicateCount = 0;

            foreach (var item in ordered)
            {
                var signature = item.Signature();
                if (!seen.Add(signature))
                {
                    duplicateCount++;
                    continue;
                }
                unique.Add(item);
            }

            var titles = new JsonArray();
            for (var i = 0; i < unique.Count; i++)
                titles.Add(ToJson(unique[i], i == 0));

            return new JsonObject
            {
                ["state"] = JsonValue.CreateStringValue("ready"),
                ["available"] = JsonValue.CreateBooleanValue(true),
                ["disc_type"] = JsonValue.CreateStringValue("bluray"),
                ["disc_label"] = JsonValue.CreateStringValue(volume.Name ?? ""),
                ["protection_detected"] = JsonValue.CreateBooleanValue(protection),
                ["streams_readable"] = JsonValue.CreateBooleanValue(streamsReadable),
                ["streams_decrypted"] = JsonValue.CreateBooleanValue(streamsDecrypted),
                ["disc_key"] = JsonValue.CreateStringValue(BuildDiscKey(volume.Name, unique)),
                ["title_count"] = JsonValue.CreateNumberValue(unique.Count),
                ["duplicate_playlists_filtered"] = JsonValue.CreateNumberValue(duplicateCount),
                ["titles"] = titles,
                ["parse_errors"] = parseErrors
            };
        }

        static PlaylistInfo ParsePlaylist(
            string fileName,
            byte[] bytes,
            HashSet<string> streamNames)
        {
            if (bytes == null || bytes.Length < 20)
                throw new InvalidOperationException("MPLS file is too short.");

            if (!string.Equals(ReadAscii(bytes, 0, 4), "MPLS", StringComparison.Ordinal))
                throw new InvalidOperationException("MPLS signature is missing.");

            var playlistOffset = checked((int)ReadUInt32Be(bytes, 8));
            var markOffset = checked((int)ReadUInt32Be(bytes, 12));
            if (playlistOffset < 0 || playlistOffset + 10 > bytes.Length)
                throw new InvalidOperationException("MPLS playlist offset is invalid.");

            var playItemCount = ReadUInt16Be(bytes, playlistOffset + 6);
            var cursor = playlistOffset + 10;
            var info = new PlaylistInfo
            {
                FileName = fileName ?? "",
                PlaylistId = System.IO.Path.GetFileNameWithoutExtension(fileName ?? "")
            };

            for (var i = 0; i < playItemCount; i++)
            {
                if (cursor + 2 > bytes.Length)
                    throw new InvalidOperationException("MPLS play-item table is truncated.");

                var itemLength = ReadUInt16Be(bytes, cursor);
                var itemStart = cursor + 2;
                var itemEnd = itemStart + itemLength;

                if (itemLength < 20 || itemEnd > bytes.Length)
                    throw new InvalidOperationException("MPLS play item is invalid.");

                var clipId = ReadAscii(bytes, itemStart, 5);
                var codec = ReadAscii(bytes, itemStart + 5, 4);
                var inTime = ReadUInt32Be(bytes, itemStart + 12);
                var outTime = ReadUInt32Be(bytes, itemStart + 16);

                if (!string.IsNullOrWhiteSpace(clipId) && outTime >= inTime)
                {
                    var streamFile = clipId + ".m2ts";
                    info.DurationTicks += outTime - inTime;
                    info.Clips.Add(new ClipInfo
                    {
                        PlayItemIndex = i,
                        Id = clipId,
                        Codec = codec,
                        StreamFile = streamFile,
                        InTime = inTime,
                        OutTime = outTime,
                        TitleStartTicks = info.DurationTicks - (outTime - inTime),
                        StreamPresent = streamNames == null || streamNames.Count == 0
                            ? false
                            : streamNames.Contains(streamFile)
                    });
                }

                cursor = itemEnd;
            }

            ParsePlaylistMarks(bytes, markOffset, info);
            return info;
        }

        static void ParsePlaylistMarks(
            byte[] bytes,
            int markOffset,
            PlaylistInfo info)
        {
            if (bytes == null ||
                info == null ||
                markOffset <= 0 ||
                markOffset + 6 > bytes.Length)
                return;

            var sectionLength = ReadUInt32Be(bytes, markOffset);
            var sectionEndLong = (long)markOffset + 4L + sectionLength;
            var sectionEnd = (int)Math.Min(
                bytes.Length,
                Math.Max(markOffset + 6L, sectionEndLong));
            var markCount = ReadUInt16Be(bytes, markOffset + 4);
            var cursor = markOffset + 6;

            var marks = new List<ChapterMarkInfo>();
            for (var markIndex = 0; markIndex < markCount; markIndex++)
            {
                // PlayListMark entries are fixed 14-byte records:
                // reserved, type, PlayItem ref, 45 kHz timestamp,
                // entry ES PID and duration.
                if (cursor + 14 > sectionEnd)
                    break;

                var markType = bytes[cursor + 1];
                var playItemRef = ReadUInt16Be(bytes, cursor + 2);
                var markTime = ReadUInt32Be(bytes, cursor + 4);
                var entryEsPid = ReadUInt16Be(bytes, cursor + 8);
                var markDuration = ReadUInt32Be(bytes, cursor + 10);
                cursor += 14;

                // Type 1 is an EntryMark (chapter). Link marks are navigation
                // metadata and must not become music tracks.
                if (markType != 1)
                    continue;

                var clip = info.Clips.FirstOrDefault(
                    item => item.PlayItemIndex == playItemRef);
                if (clip == null)
                    continue;

                var relative = markTime > clip.InTime
                    ? (ulong)(markTime - clip.InTime)
                    : 0UL;
                var clipDuration = (ulong)Math.Max(
                    0L,
                    (long)clip.OutTime - clip.InTime);
                if (relative > clipDuration)
                    relative = clipDuration;

                var titleTicks = clip.TitleStartTicks + relative;
                if (titleTicks > info.DurationTicks)
                    titleTicks = info.DurationTicks;

                marks.Add(new ChapterMarkInfo
                {
                    MarkIndex = markIndex,
                    MarkType = markType,
                    PlayItemRef = playItemRef,
                    MarkTime = markTime,
                    EntryEsPid = entryEsPid,
                    MarkDuration = markDuration,
                    TitleStartTicks = titleTicks,
                    StreamFile = clip.StreamFile ?? ""
                });
            }

            var ordered = marks
                .OrderBy(mark => mark.TitleStartTicks)
                .ThenBy(mark => mark.MarkIndex)
                .ToList();

            // Collapse duplicate entry marks at the same authored timestamp.
            var unique = new List<ChapterMarkInfo>();
            foreach (var mark in ordered)
            {
                if (unique.Count > 0 &&
                    unique[unique.Count - 1].TitleStartTicks ==
                        mark.TitleStartTicks)
                    continue;
                unique.Add(mark);
            }

            // A chapter list should cover the whole programme. Some discs
            // omit an explicit zero mark, so synthesize only that boundary.
            if (unique.Count == 0 ||
                unique[0].TitleStartTicks > 0)
            {
                unique.Insert(0, new ChapterMarkInfo
                {
                    MarkIndex = -1,
                    MarkType = 1,
                    PlayItemRef = 0,
                    MarkTime = info.Clips.Count > 0
                        ? info.Clips[0].InTime
                        : 0,
                    EntryEsPid = 0,
                    MarkDuration = 0,
                    TitleStartTicks = 0,
                    StreamFile = info.Clips.Count > 0
                        ? info.Clips[0].StreamFile ?? ""
                        : ""
                });
            }

            for (var index = 0; index < unique.Count; index++)
            {
                var start = unique[index].TitleStartTicks;
                var end = index + 1 < unique.Count
                    ? unique[index + 1].TitleStartTicks
                    : info.DurationTicks;

                if (end <= start)
                    continue;

                unique[index].Number = info.Chapters.Count + 1;
                unique[index].DurationTicks = end - start;
                info.Chapters.Add(unique[index]);
            }
        }

        static JsonObject ToJson(PlaylistInfo info, bool recommended)
        {
            var clips = new JsonArray();
            foreach (var clip in info.Clips)
            {
                clips.Add(new JsonObject
                {
                    ["clip_id"] = JsonValue.CreateStringValue(clip.Id ?? ""),
                    ["codec_id"] = JsonValue.CreateStringValue(clip.Codec ?? ""),
                    ["stream_file"] = JsonValue.CreateStringValue(clip.StreamFile ?? ""),
                    ["stream_present"] = JsonValue.CreateBooleanValue(clip.StreamPresent),
                    ["in_time"] = JsonValue.CreateNumberValue(clip.InTime),
                    ["out_time"] = JsonValue.CreateNumberValue(clip.OutTime),
                    ["duration_seconds"] = JsonValue.CreateNumberValue(
                        Math.Max(0, clip.OutTime - clip.InTime) / 45000.0)
                });
            }

            var chapters = new JsonArray();
            foreach (var chapter in info.Chapters)
            {
                chapters.Add(new JsonObject
                {
                    ["number"] = JsonValue.CreateNumberValue(chapter.Number),
                    ["mark_index"] = JsonValue.CreateNumberValue(chapter.MarkIndex),
                    ["mark_type"] = JsonValue.CreateNumberValue(chapter.MarkType),
                    ["play_item_ref"] = JsonValue.CreateNumberValue(chapter.PlayItemRef),
                    ["mark_time"] = JsonValue.CreateNumberValue(chapter.MarkTime),
                    ["entry_es_pid"] = JsonValue.CreateNumberValue(chapter.EntryEsPid),
                    ["mark_duration"] = JsonValue.CreateNumberValue(chapter.MarkDuration),
                    ["title_start_ticks"] = JsonValue.CreateNumberValue(chapter.TitleStartTicks),
                    ["start_seconds"] = JsonValue.CreateNumberValue(
                        chapter.TitleStartTicks / 45000.0),
                    ["duration_ticks"] = JsonValue.CreateNumberValue(chapter.DurationTicks),
                    ["duration_seconds"] = JsonValue.CreateNumberValue(
                        chapter.DurationTicks / 45000.0),
                    ["stream_file"] = JsonValue.CreateStringValue(
                        chapter.StreamFile ?? "")
                });
            }

            return new JsonObject
            {
                ["playlist"] = JsonValue.CreateStringValue(info.FileName ?? ""),
                ["playlist_id"] = JsonValue.CreateStringValue(info.PlaylistId ?? ""),
                ["duration_seconds"] = JsonValue.CreateNumberValue(info.DurationTicks / 45000.0),
                ["play_item_count"] = JsonValue.CreateNumberValue(info.Clips.Count),
                ["clip_count"] = JsonValue.CreateNumberValue(
                    info.Clips.Select(c => c.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()),
                ["recommended"] = JsonValue.CreateBooleanValue(recommended),
                ["chapter_count"] = JsonValue.CreateNumberValue(info.Chapters.Count),
                ["chapters"] = chapters,
                ["clips"] = clips
            };
        }

        static async Task<bool> LooksLikeReadableM2tsAsync(StorageFile file)
        {
            try
            {
                using (var stream = await file.OpenReadAsync())
                using (var input = stream.GetInputStreamAt(0))
                using (var reader = new DataReader(input))
                {
                    var loaded = await reader.LoadAsync(5);
                    if (loaded < 5)
                        return false;

                    var bytes = new byte[5];
                    reader.ReadBytes(bytes);

                    // Blu-ray M2TS packets carry a four-byte arrival timestamp
                    // followed by the MPEG-TS sync byte 0x47.
                    return bytes[4] == 0x47;
                }
            }
            catch
            {
                return false;
            }
        }

        static string BuildDiscKey(
            string label,
            IList<PlaylistInfo> titles)
        {
            var first = titles == null || titles.Count == 0
                ? null
                : titles[0];

            return (label ?? "") + "|" +
                (titles == null ? 0 : titles.Count) + "|" +
                (first == null ? "" : first.PlaylistId) + "|" +
                (first == null ? 0UL : first.DurationTicks);
        }

        static byte[] ReadAllBytes(Stream source)
        {
            if (source == null)
                return new byte[0];

            using (var memory = new MemoryStream())
            {
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    var read = source.Read(
                        buffer,
                        0,
                        buffer.Length);
                    if (read <= 0)
                        break;
                    memory.Write(buffer, 0, read);
                }
                return memory.ToArray();
            }
        }

        static bool LooksLikeReadableM2ts(Stream stream)
        {
            if (stream == null || !stream.CanRead)
                return false;

            var start = stream.CanSeek
                ? stream.Position
                : 0;
            try
            {
                var bytes = new byte[5];
                var offset = 0;
                while (offset < bytes.Length)
                {
                    var read = stream.Read(
                        bytes,
                        offset,
                        bytes.Length - offset);
                    if (read <= 0)
                        return false;
                    offset += read;
                }
                return bytes[4] == 0x47;
            }
            finally
            {
                if (stream.CanSeek)
                    stream.Position = start;
            }
        }

        static async Task<byte[]> ReadBytesAsync(StorageFile file)
        {
            var buffer = await FileIO.ReadBufferAsync(file);
            using (var reader = DataReader.FromBuffer(buffer))
            {
                var bytes = new byte[buffer.Length];
                reader.ReadBytes(bytes);
                return bytes;
            }
        }

        static async Task<bool> HasFolderAsync(StorageFolder root, string name)
        {
            try
            {
                await root.GetFolderAsync(name);
                return true;
            }
            catch
            {
                return false;
            }
        }

        static JsonObject FindRecommended(JsonObject scan)
        {
            if (scan == null ||
                !scan.ContainsKey("titles") ||
                scan["titles"].ValueType != JsonValueType.Array)
                return null;

            foreach (var value in scan.GetNamedArray("titles"))
            {
                if (value.ValueType != JsonValueType.Object)
                    continue;
                var title = value.GetObject();
                if (GetBool(title, "recommended"))
                    return title;
            }

            return scan.GetNamedArray("titles").Count > 0 &&
                   scan.GetNamedArray("titles")[0].ValueType == JsonValueType.Object
                ? scan.GetNamedArray("titles")[0].GetObject()
                : null;
        }

        static JsonObject FindTitle(JsonObject scan, string playlist)
        {
            if (scan == null ||
                !scan.ContainsKey("titles") ||
                scan["titles"].ValueType != JsonValueType.Array)
                return null;

            var requested = playlist ?? "";
            foreach (var value in scan.GetNamedArray("titles"))
            {
                if (value.ValueType != JsonValueType.Object)
                    continue;

                var title = value.GetObject();
                if (string.Equals(
                        GetString(title, "playlist", ""),
                        requested,
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        GetString(title, "playlist_id", ""),
                        System.IO.Path.GetFileNameWithoutExtension(requested),
                        StringComparison.OrdinalIgnoreCase))
                    return title;
            }
            return null;
        }

        static ushort ReadUInt16Be(byte[] data, int offset)
        {
            Require(data, offset, 2);
            return (ushort)((data[offset] << 8) | data[offset + 1]);
        }

        static uint ReadUInt32Be(byte[] data, int offset)
        {
            Require(data, offset, 4);
            return ((uint)data[offset] << 24) |
                   ((uint)data[offset + 1] << 16) |
                   ((uint)data[offset + 2] << 8) |
                   data[offset + 3];
        }

        static string ReadAscii(byte[] data, int offset, int count)
        {
            Require(data, offset, count);
            return Encoding.ASCII.GetString(data, offset, count).Trim('\0', ' ');
        }

        static void Require(byte[] data, int offset, int count)
        {
            if (data == null || offset < 0 || count < 0 || offset + count > data.Length)
                throw new InvalidOperationException("MPLS structure is truncated.");
        }

        static async Task PersistAsync(string name, JsonObject value)
        {
            try
            {
                var root = ApplicationData.Current.LocalFolder;
                var core = await root.CreateFolderAsync(
                    "Core", CreationCollisionOption.OpenIfExists);
                var ingest = await core.CreateFolderAsync(
                    "Ingest", CreationCollisionOption.OpenIfExists);
                var file = await ingest.CreateFileAsync(
                    name, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, value.Stringify());
            }
            catch
            {
                // A diagnostic/plan write must not make optical scanning fail.
            }
        }

        static JsonObject State(string state)
        {
            return new JsonObject
            {
                ["state"] = JsonValue.CreateStringValue(state ?? ""),
                ["available"] = JsonValue.CreateBooleanValue(false)
            };
        }

        static string GetString(JsonObject obj, string key, string fallback)
        {
            return obj != null &&
                   obj.ContainsKey(key) &&
                   obj[key].ValueType == JsonValueType.String
                ? obj[key].GetString()
                : fallback;
        }

        static double GetNumber(JsonObject obj, string key, double fallback)
        {
            return obj != null &&
                   obj.ContainsKey(key) &&
                   obj[key].ValueType == JsonValueType.Number
                ? obj[key].GetNumber()
                : fallback;
        }

        static bool GetBool(JsonObject obj, string key)
        {
            return obj != null &&
                   obj.ContainsKey(key) &&
                   obj[key].ValueType == JsonValueType.Boolean &&
                   obj[key].GetBoolean();
        }

        static string Describe(Exception ex)
        {
            return ex.GetType().Name + " 0x" + ex.HResult.ToString("X8") +
                   ": " + ex.Message;
        }

        sealed class SourceFile
        {
            public StorageFile File;
            public string Name;
            public ulong Bytes;
        }

        sealed class PlaylistInfo
        {
            public string FileName;
            public string PlaylistId;
            public ulong DurationTicks;
            public readonly List<ClipInfo> Clips = new List<ClipInfo>();
            public readonly List<ChapterMarkInfo> Chapters =
                new List<ChapterMarkInfo>();

            public string Signature()
            {
                return DurationTicks + "|" +
                    string.Join(",", Clips.Select(c =>
                        c.Id + ":" + c.InTime + ":" + c.OutTime));
            }
        }

        sealed class ChapterMarkInfo
        {
            public int Number;
            public int MarkIndex;
            public byte MarkType;
            public ushort PlayItemRef;
            public uint MarkTime;
            public ushort EntryEsPid;
            public uint MarkDuration;
            public ulong TitleStartTicks;
            public ulong DurationTicks;
            public string StreamFile;
        }

        sealed class ClipInfo
        {
            public int PlayItemIndex;
            public string Id;
            public string Codec;
            public string StreamFile;
            public uint InTime;
            public uint OutTime;
            public ulong TitleStartTicks;
            public bool StreamPresent;
        }
    }
}
