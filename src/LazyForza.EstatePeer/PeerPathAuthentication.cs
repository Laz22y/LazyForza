using System.Security.Cryptography;
using System.Text;

namespace LazyForza.EstatePeer;

internal static class PeerPathAuthentication
{
    // The receipt nonce is a secret in v2, never a plaintext wire identifier as in v1.
    internal static byte[] Key(PeerReceipt receipt, string purpose) => HMACSHA256.HashData(
        Convert.FromHexString(receipt.Nonce), Encoding.UTF8.GetBytes($"LFZ/{purpose}/{receipt.RoomId:N}/{receipt.Generation}"));

    internal static byte[] Identifier(PeerReceipt receipt) => Key(receipt, "path-id")[..16];
}
