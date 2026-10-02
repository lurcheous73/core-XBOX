using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Devices.Custom;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;
using WinBuffer = Windows.Storage.Streams.Buffer;

namespace BrimstoneXbox.Services
{
    public sealed class ScsiOpticalStream : Stream
    {
        static readonly Guid CdromInterfaceGuid =
            new Guid("53F56308-B6BF-11D0-94F2-00A0C91EFB8B");

        readonly CustomDevice _device;
        readonly int _sectorSize;
        readonly long _length;
        long _position;
        const int CacheSectors = 64;
        uint _cacheStartLba = uint.MaxValue;
        int _cacheSectorCount;
        byte[] _cacheData;

        ScsiOpticalStream(CustomDevice device, uint lastLba, int sectorSize)
        {
            _device = device;
            _sectorSize = sectorSize;
            _length = ((long)lastLba + 1L) * sectorSize;
        }

        public int SectorSize => _sectorSize;

        public static async Task<ScsiOpticalStream> OpenAsync()
        {
            var selector = CustomDevice.GetDeviceSelector(CdromInterfaceGuid);
            var devices = await DeviceInformation.FindAllAsync(selector);
            if (devices.Count == 0)
                throw new IOException("No Xbox optical device interface is available.");

            var device = await CustomDevice.FromIdAsync(
                devices[0].Id,
                DeviceAccessMode.ReadWrite,
                DeviceSharingMode.Shared);
            if (device == null)
                throw new IOException("Xbox refused read/write access to the optical device handle.");

            try
            {
                var capacity = await ReadCapacityAsync(device);
                if (capacity.BlockSize <= 0)
                    throw new IOException("Optical drive returned an invalid logical block size.");

                return new ScsiOpticalStream(
                    device,
                    capacity.LastLba,
                    capacity.BlockSize);
            }
            catch
            {
                throw;
            }
        }

        static async Task<CapacityInfo> ReadCapacityAsync(CustomDevice device)
        {
            var cdb = new byte[10];
            cdb[0] = 0x25; // READ CAPACITY(10)
            var data = await SendScsiAsync(device, cdb, 8);
            if (data.Length < 8)
                throw new IOException("READ CAPACITY returned fewer than 8 bytes.");

            return new CapacityInfo
            {
                LastLba = ReadUInt32Be(data, 0),
                BlockSize = checked((int)ReadUInt32Be(data, 4))
            };
        }

        async Task<byte[]> ReadSectorsAsync(
            uint lba,
            ushort sectorCount)
        {
            if (sectorCount == 0)
                return new byte[0];

            var cdb = new byte[10];
            cdb[0] = 0x28; // READ(10)
            cdb[2] = (byte)((lba >> 24) & 0xFF);
            cdb[3] = (byte)((lba >> 16) & 0xFF);
            cdb[4] = (byte)((lba >> 8) & 0xFF);
            cdb[5] = (byte)(lba & 0xFF);
            cdb[7] = (byte)((sectorCount >> 8) & 0xFF);
            cdb[8] = (byte)(sectorCount & 0xFF);
            return await SendScsiAsync(
                _device,
                cdb,
                checked(_sectorSize * sectorCount));
        }

        static async Task<byte[]> SendScsiAsync(
            CustomDevice device,
            byte[] cdb,
            int dataLength)
        {
            const int sptSize = 56;
            const int senseSize = 32;
            const int dataOffset = 88;
            var packetBytes = checked(dataOffset + dataLength);
            var packet = new byte[packetBytes];

            WriteUInt16Le(packet, 0, sptSize);
            packet[6] = checked((byte)cdb.Length);
            packet[7] = senseSize;
            packet[8] = 1; // SCSI_IOCTL_DATA_IN
            WriteUInt32Le(packet, 12, dataLength);
            WriteUInt32Le(packet, 16, 20);
            WriteUInt64Le(packet, 24, dataOffset);
            WriteUInt32Le(packet, 32, sptSize);
            Array.Copy(cdb, 0, packet, 36, Math.Min(16, cdb.Length));

            IBuffer input;
            using (var writer = new DataWriter())
            {
                writer.WriteBytes(packet);
                input = writer.DetachBuffer();
            }

            var output = new WinBuffer((uint)packetBytes);
            var ioctl = new IOControlCode(
                (ushort)0x0004,
                (ushort)0x0401,
                IOControlAccessMode.ReadWrite,
                IOControlBufferingMethod.Buffered);

            var ok = await device.TrySendIOControlAsync(ioctl, input, output);
            if (!ok)
                throw new IOException("Xbox SCSI pass-through command failed.");

            byte[] bytes;
            using (var reader = DataReader.FromBuffer(output))
            {
                bytes = new byte[output.Length];
                reader.ReadBytes(bytes);
            }

            if (bytes.Length < sptSize)
                throw new IOException("Xbox SCSI pass-through response was truncated.");

            if (bytes[2] != 0)
                throw new IOException("Optical drive returned SCSI status " + bytes[2] + ".");

            var actualOffset = dataOffset;
            if (bytes.Length >= 32)
            {
                var reported = ReadUInt64Le(bytes, 24);
                if (reported >= sptSize &&
                    reported <= (ulong)Math.Max(sptSize, bytes.Length - dataLength))
                    actualOffset = checked((int)reported);
            }

            var actualLength = dataLength;
            if (bytes.Length >= 16)
            {
                var reportedLength = checked((int)ReadUInt32Le(bytes, 12));
                if (reportedLength > 0 && reportedLength <= dataLength)
                    actualLength = reportedLength;
            }

            if (actualOffset + actualLength > bytes.Length)
                throw new IOException("Xbox SCSI response data range is invalid.");

            var result = new byte[actualLength];
            Array.Copy(bytes, actualOffset, result, 0, actualLength);
            return result;
        }

        byte[] GetSector(uint lba)
        {
            if (_cachedSector != null && _cachedLba == lba)
                return _cachedSector;

            var data = ReadSectorAsync(lba).GetAwaiter().GetResult();
            if (data.Length != _sectorSize)
                throw new IOException("Short optical sector read at LBA " + lba + ".");

            _cachedLba = lba;
            _cachedSector = data;
            return data;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
                throw new ArgumentOutOfRangeException();
            if (_position >= _length || count == 0)
                return 0;

            var remaining = (int)Math.Min(count, _length - _position);
            var copied = 0;

            while (copied < remaining)
            {
                var lba = checked((uint)(_position / _sectorSize));
                var within = checked((int)(_position % _sectorSize));
                var sector = GetSector(lba);
                var take = Math.Min(remaining - copied, _sectorSize - within);

                Array.Copy(sector, within, buffer, offset + copied, take);
                copied += take;
                _position += take;
            }

            return copied;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long next;
            switch (origin)
            {
                case SeekOrigin.Begin: next = offset; break;
                case SeekOrigin.Current: next = _position + offset; break;
                case SeekOrigin.End: next = _length + offset; break;
                default: throw new ArgumentOutOfRangeException(nameof(origin));
            }

            if (next < 0)
                throw new IOException("Cannot seek before the beginning of the optical stream.");

            _position = Math.Min(next, _length);
            return _position;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        static uint ReadUInt32Be(byte[] b, int o) =>
            ((uint)b[o] << 24) |
            ((uint)b[o + 1] << 16) |
            ((uint)b[o + 2] << 8) |
            b[o + 3];

        static uint ReadUInt32Le(byte[] b, int o) =>
            b[o] |
            ((uint)b[o + 1] << 8) |
            ((uint)b[o + 2] << 16) |
            ((uint)b[o + 3] << 24);

        static ulong ReadUInt64Le(byte[] b, int o)
        {
            ulong value = 0;
            for (var i = 0; i < 8; i++)
                value |= ((ulong)b[o + i]) << (8 * i);
            return value;
        }

        static void WriteUInt16Le(byte[] data, int offset, int value)
        {
            data[offset] = (byte)(value & 0xFF);
            data[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        static void WriteUInt32Le(byte[] data, int offset, long value)
        {
            for (var i = 0; i < 4; i++)
                data[offset + i] = (byte)((value >> (8 * i)) & 0xFF);
        }

        static void WriteUInt64Le(byte[] data, int offset, long value)
        {
            for (var i = 0; i < 8; i++)
                data[offset + i] = (byte)((value >> (8 * i)) & 0xFF);
        }

        sealed class CapacityInfo
        {
            public uint LastLba;
            public int BlockSize;
        }
    }
}
