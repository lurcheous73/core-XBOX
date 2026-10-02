using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;

namespace BrimstoneXbox.Services
{
    sealed class StorageFileHttpContent : HttpContent
    {
        readonly StorageFile _file;
        readonly long _length;

        StorageFileHttpContent(StorageFile file, long length)
        {
            _file = file ?? throw new ArgumentNullException(nameof(file));
            _length = length;
        }

        public static async Task<StorageFileHttpContent> CreateAsync(
            StorageFile file)
        {
            if (file == null)
                throw new ArgumentNullException(nameof(file));
            var props = await file.GetBasicPropertiesAsync();
            return new StorageFileHttpContent(file, checked((long)props.Size));
        }

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext context)
        {
            using (var source = await _file.OpenReadAsync())
            {
                ulong offset = 0;
                while (offset < source.Size)
                {
                    var remaining = source.Size - offset;
                    var requested = (uint)Math.Min(
                        1024 * 1024,
                        (long)Math.Min(remaining, (ulong)uint.MaxValue));

                    using (var input = source.GetInputStreamAt(offset))
                    using (var reader = new DataReader(input))
                    {
                        var loaded = await reader.LoadAsync(requested);
                        if (loaded == 0)
                            break;

                        var bytes = new byte[loaded];
                        reader.ReadBytes(bytes);
                        await stream.WriteAsync(bytes, 0, bytes.Length);
                        offset += loaded;
                    }
                }
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }
    }
}
