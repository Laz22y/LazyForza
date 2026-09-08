using System.Security.Cryptography;
using System.Text;

namespace LazyForza.Domain;

/// <summary>Local template identity, captured with a lap; not an official game revision.</summary>
public static class LapTrackRevision
{
    public static string Create(TrackTemplate track)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(track.Id.ToByteArray());
        writer.Write(track.UpdatedAt.UtcTicks);
        writer.Write(track.Direction);
        writer.Write((int)track.LayoutKind);
        writer.Write((int)track.TimingKind);
        writer.Write(track.Source);
        writer.Write(track.GameBuild ?? "");
        WriteNumber(track.LengthMeters, 3);
        foreach (var point in track.Points)
        {
            WriteNumber(point.X, 3); WriteNumber(point.Y, 3); WriteNumber(point.Z, 3); WriteNumber(point.S, 3);
            WriteNumber(point.TangentX, 6); WriteNumber(point.TangentZ, 6);
        }
        writer.Flush();
        return "track-v1:" + Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length)));

        // SQLite's numeric-to-text roundtrip can change the last floating-point bits.
        // Millimetre geometry / 1e-6 tangents are stable across storage and interchange.
        void WriteNumber(double value, int decimals)
        {
            var rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
            writer.Write(rounded == 0 ? 0d : rounded);
        }
    }
}
