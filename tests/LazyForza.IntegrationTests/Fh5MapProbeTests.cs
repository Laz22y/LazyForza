using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LazyForza.Fh5MapProbe;

namespace LazyForza.IntegrationTests;

[TestClass]
public sealed class Fh5MapProbeTests
{
    [DataTestMethod]
    [DataRow(Fh5DataOutParser.ShortPacketLength)]
    [DataRow(Fh5DataOutParser.PaddedPacketLength)]
    public void ParserAcceptsHorizonDashLengthsAndCriticalOffsets(int packetLength)
    {
        var packet = BuildPacket(packetLength);

        var parsed = Fh5DataOutParser.TryParse(packet, out var frame, out var error);

        Assert.IsTrue(parsed, error);
        Assert.IsNotNull(frame);
        Assert.AreEqual(packetLength, frame.PacketLength);
        Assert.AreEqual(123456u, frame.TimestampMs);
        Assert.AreEqual(4123, frame.CarOrdinal);
        Assert.AreEqual(11, frame.CarCategory);
        Assert.AreEqual(0x10203040u, frame.HorizonUnknown1);
        Assert.AreEqual(0x50607080u, frame.HorizonUnknown2);
        Assert.AreEqual(101.25f, frame.PositionX);
        Assert.AreEqual(202.5f, frame.PositionY);
        Assert.AreEqual(-303.75f, frame.PositionZ);
        Assert.AreEqual(5d, frame.VelocityMagnitudeMps, 1e-6);
        Assert.AreEqual(0d, frame.SpeedDeltaMps, 1e-6);
    }

    [TestMethod]
    public void ParserRejectsMotorsportDashLength()
    {
        var parsed = Fh5DataOutParser.TryParse(new byte[311], out _, out var error);

        Assert.IsFalse(parsed);
        StringAssert.Contains(error, "323");
        StringAssert.Contains(error, "324");
    }

    [TestMethod]
    public void ParserRejectsMisalignedDashUsingPhysicsCheck()
    {
        var packet = BuildPacket(Fh5DataOutParser.PaddedPacketLength);
        WriteFloat(packet, 256, 50f);

        var parsed = Fh5DataOutParser.TryParse(packet, out _, out var error);

        Assert.IsFalse(parsed);
        StringAssert.Contains(error, "Velocity");
    }

    [TestMethod]
    public async Task CapturePackageContainsRawFramesMarkersAndManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lazyforza-fh5-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, $"test{CapturePackageWriter.Extension}");
            var packet = BuildPacket(Fh5DataOutParser.PaddedPacketLength);
            Assert.IsTrue(Fh5DataOutParser.TryParse(packet, out var frame, out var error), error);
            Assert.IsNotNull(frame);
            using (var writer = new CapturePackageWriter(path))
            {
                writer.WriteRawPacket(DateTimeOffset.UnixEpoch, packet);
                writer.WriteFrame(1, DateTimeOffset.UnixEpoch, frame);
                var marker = new Fh5CoordinateMarker(
                    Guid.NewGuid(),
                    "入口,出生点",
                    DateTimeOffset.UnixEpoch,
                    frame.PositionX,
                    frame.PositionY,
                    frame.PositionZ,
                    0.05,
                    60,
                    0);
                var manifest = new Fh5CaptureManifest(
                    CapturePackageWriter.SchemaVersion,
                    "test",
                    "Forza Horizon 5",
                    Fh5MapRegion.Mexico.ToString(),
                    "主地图 · 墨西哥",
                    "第 1 轮",
                    "test",
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch.AddMinutes(1),
                    "0.0.0.0",
                    2299,
                    1,
                    1,
                    0,
                    1,
                    new Dictionary<int, long> { [packet.Length] = 1 },
                    new Dictionary<string, long>(),
                    new Fh5CoordinateBounds(101, 102, 202, 203, -304, -303),
                    0,
                    1,
                    "raw",
                    "csv",
                    "layout");

                await writer.CompleteAsync(manifest, [marker]);
            }

            using var archive = ZipFile.OpenRead(path);
            CollectionAssert.AreEquivalent(
                new[] { "raw-packets.bin", "frames.csv", "markers.csv", "manifest.json" },
                archive.Entries.Select(entry => entry.FullName).ToArray());
            var rawEntry = archive.GetEntry("raw-packets.bin");
            Assert.IsNotNull(rawEntry);
            await using var raw = rawEntry.Open();
            var magic = new byte[8];
            await raw.ReadExactlyAsync(magic);
            CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("LF5RAW01"), magic);
            var markersEntry = archive.GetEntry("markers.csv");
            Assert.IsNotNull(markersEntry);
            using var markerReader = new StreamReader(markersEntry.Open());
            StringAssert.Contains(await markerReader.ReadToEndAsync(), "\"入口,出生点\"");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CaptureSessionReceivesUdpCreatesMarkerAndSavesPackage()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lazyforza-fh5-session-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, $"loopback{CapturePackageWriter.Extension}");
            var port = AvailableUdpPort();
            var settings = new Fh5CaptureSettings(
                Fh5MapRegion.Mexico,
                "主地图 · 墨西哥",
                "回环测试",
                "127.0.0.1",
                port,
                path,
                DateTimeOffset.UtcNow);
            await using var session = new Fh5MapCaptureSession(settings);
            using var sender = new UdpClient();
            var packet = BuildPacket(Fh5DataOutParser.PaddedPacketLength);
            for (var index = 0; index < 12; index++)
            {
                await sender.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, port));
                await Task.Delay(12);
            }
            var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
            while (session.Snapshot().ValidPackets < 12 && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(20);

            var marker = session.CaptureMarker("回环地标");
            await session.StopAndSaveAsync("UDP loopback");

            Assert.AreEqual(12, session.Snapshot().ValidPackets);
            Assert.AreEqual(12, marker.SampleCount);
            Assert.IsTrue(File.Exists(path));
            using var archive = ZipFile.OpenRead(path);
            Assert.IsNotNull(archive.GetEntry("manifest.json"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task PausedPacketsDoNotExpandActiveDrivingCoordinateBounds()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lazyforza-fh5-paused-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, $"paused{CapturePackageWriter.Extension}");
            var port = AvailableUdpPort();
            var settings = new Fh5CaptureSettings(
                Fh5MapRegion.HotWheelsPark,
                "风火轮地图 · Hot Wheels Park",
                "暂停过滤测试",
                "127.0.0.1",
                port,
                path,
                DateTimeOffset.UtcNow);
            await using var session = new Fh5MapCaptureSession(settings);
            using var sender = new UdpClient();
            for (var index = 0; index < 8; index++)
                await sender.SendAsync(
                    BuildPacket(Fh5DataOutParser.PaddedPacketLength),
                    new IPEndPoint(IPAddress.Loopback, port));
            for (var index = 0; index < 8; index++)
                await sender.SendAsync(
                    BuildPacket(
                        Fh5DataOutParser.PaddedPacketLength,
                        isRaceOn: 0,
                        positionX: 50_000,
                        positionY: 60_000,
                        positionZ: 70_000),
                    new IPEndPoint(IPAddress.Loopback, port));

            var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
            while (session.Snapshot().ValidPackets < 16 && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(20);

            var snapshot = session.Snapshot();
            Assert.AreEqual(16, snapshot.ValidPackets);
            Assert.AreEqual(8, snapshot.ActiveDrivingPackets);
            Assert.IsNotNull(snapshot.ActiveCoordinateBounds);
            Assert.AreEqual(101.25, snapshot.ActiveCoordinateBounds.MaximumX, 0.001);
            Assert.AreEqual(202.5, snapshot.ActiveCoordinateBounds.MaximumY, 0.001);
            Assert.AreEqual(-303.75, snapshot.ActiveCoordinateBounds.MaximumZ, 0.001);
            await session.StopAndSaveAsync("paused packets excluded");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] BuildPacket(
        int length,
        int isRaceOn = 1,
        float positionX = 101.25f,
        float positionY = 202.5f,
        float positionZ = -303.75f)
    {
        var packet = new byte[length];
        WriteInt32(packet, 0, isRaceOn);
        WriteUInt32(packet, 4, 123456);
        WriteFloat(packet, 8, 8000);
        WriteFloat(packet, 16, 3500);
        WriteFloat(packet, 32, 3);
        WriteFloat(packet, 36, 4);
        WriteFloat(packet, 40, 0);
        WriteFloat(packet, 56, 0.25f);
        WriteFloat(packet, 60, -0.1f);
        WriteFloat(packet, 64, 0.05f);
        WriteInt32(packet, 212, 4123);
        WriteInt32(packet, 216, 5);
        WriteInt32(packet, 220, 850);
        WriteInt32(packet, 224, 2);
        WriteInt32(packet, 228, 8);
        WriteInt32(packet, 232, 11);
        WriteUInt32(packet, 236, 0x10203040);
        WriteUInt32(packet, 240, 0x50607080);
        WriteFloat(packet, 244, positionX);
        WriteFloat(packet, 248, positionY);
        WriteFloat(packet, 252, positionZ);
        WriteFloat(packet, 256, 5);
        WriteFloat(packet, 292, 1500);
        WriteFloat(packet, 304, 12.5f);
        WriteFloat(packet, 308, 44.5f);
        WriteUInt16(packet, 312, 2);
        packet[314] = 3;
        packet[315] = 200;
        packet[316] = 10;
        packet[319] = 4;
        packet[320] = unchecked((byte)(sbyte)-20);
        return packet;
    }

    private static void WriteInt32(byte[] value, int offset, int data) =>
        BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(offset), data);

    private static void WriteUInt32(byte[] value, int offset, uint data) =>
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(offset), data);

    private static void WriteUInt16(byte[] value, int offset, ushort data) =>
        BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(offset), data);

    private static void WriteFloat(byte[] value, int offset, float data) =>
        WriteInt32(value, offset, BitConverter.SingleToInt32Bits(data));

    private static int AvailableUdpPort()
    {
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)client.Client.LocalEndPoint!).Port;
    }
}
