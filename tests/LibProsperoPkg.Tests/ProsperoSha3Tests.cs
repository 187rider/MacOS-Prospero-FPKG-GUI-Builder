using System;
using System.Text;
using LibProsperoPkg.Util;
using Xunit;

namespace LibProsperoPkg.Tests;

public class ProsperoSha3Tests
{
    [Fact]
    public void Sha3_256_EmptyString_MatchesNistVector()
    {
        // NIST SHA3-256 test vector for empty string:
        // a7ffc6f8bf1ed76651c14756a061d662f580ff4de43b49fa82d80a4b80f8434a
        byte[] expected = Convert.FromHexString("a7ffc6f8bf1ed76651c14756a061d662f580ff4de43b49fa82d80a4b80f8434a");
        byte[] actual = Crypto.Sha3_256(Array.Empty<byte>());
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Sha3_256_KnownAsciiVector_MatchesNistVector()
    {
        // NIST SHA3-256 test vector for "The quick brown fox jumps over the lazy dog":
        // 69070dda01975c8c120c3aada1b282394e7f032fa9cf32f4cb2259a0897dfc04
        byte[] input = Encoding.ASCII.GetBytes("The quick brown fox jumps over the lazy dog");
        byte[] expected = Convert.FromHexString("69070dda01975c8c120c3aada1b282394e7f032fa9cf32f4cb2259a0897dfc04");
        byte[] actual = Crypto.Sha3_256(input);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ComputeKeys_SucceedsOnCurrentPlatformWithoutException()
    {
        // Tests PS5 PFS mount key derivation which failed previously on macOS
        string contentId = "EP0002-PPSA02177_00-TH12RTHEGAME0001";
        string passcode = "00000000000000000000000000000000";
        byte[] ekpfs = Crypto.ComputeKeys(contentId, passcode, 1, useSha3: true);

        Assert.NotNull(ekpfs);
        Assert.Equal(32, ekpfs.Length);
    }
}
