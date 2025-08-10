using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Utilities.Encoders;
using SatoshiSharpLib;
using System.Security.Cryptography;

namespace SatoshiSharpTest;

public class SatoshiSharpTest
{
    [Fact]
    public void Address0()
    {
        Wallet w = new Wallet(new WalletAddress(0,0,0,0));
        Assert.True( w.Address.getHex().Contains("00000000000000000000"));
    }

    [Fact]
    public void Block0()
    {
        BlockReader bdf = new BlockReader();
        byte[] key = new byte[] { 0x22, 0x6B, 0x64, 0x3B, 0x1C, 0xE5, 0x63, 0x68 };
        
        StateWallets.Wallets =
        [
            new Wallet(new WalletAddress(0, 0, 0, 0)), // add the reward wallet
        ];
        string path = Path.Combine(Helpers.GetParentDirectory(".", 5), "btcblockdata", "blk00000.dat");
        //string path = "..\\..\\..\\..\\..\\btcblockdata\\blk00000.dat";
        bdf.ReadBlkDataFile(path, key, limit: 100);
        Assert.Equal("0000000000000000000000000000000000000000000000000000000000000000", Helpers.ReverseHexString(Helpers.ByteArrayToHexString(bdf.blocksInDataFile[0].header.PrevBlock)));
    }

    [Fact]
    public void BlockHeader1()
    {
        BlockReader bdf = new BlockReader();
        byte[] key = new byte[] { 0x22, 0x6B, 0x64, 0x3B, 0x1C, 0xE5, 0x63, 0x68 };
        
        StateWallets.Wallets =
        [
            new Wallet(new WalletAddress(0, 0, 0, 0)), // add the reward wallet
        ];
        
        string path = Path.Combine(Helpers.GetParentDirectory(".", 5), "btcblockdata", "blk00000.dat");
            
        //string path = "..\\..\\..\\..\\..\\btcblockdata\\blk00000.dat";
        bdf.ReadBlkDataFile(path, key, limit: 100);
        Assert.Equal("00000000839A8E6886AB5951D76F411475428AFC90947EE320161BBF18EB6048", Helpers.ReverseHexString(Helpers.ByteArrayToHexString(bdf.blocksInDataFile[2].header.PrevBlock)));
    }

    [Fact]
    public void Address1()
    {
        string pubKeyHex = "0496b538e853519c726a2c91e61ec11600ae1390813a627c66fb8be7947be63c52da7589379515d4e0a604f8141781e62294721166bf621e73a82cbf2342c858ee";
        byte[] pubKeyBytes = Hex.Decode(pubKeyHex);

        // Step 1: SHA-256
        SHA256 sha256 = SHA256.Create();
        byte[] sha256Hash = sha256.ComputeHash(pubKeyBytes);

        // Step 2: RIPEMD-160
        RipeMD160Digest ripemd160 = new RipeMD160Digest();
        ripemd160.BlockUpdate(sha256Hash, 0, sha256Hash.Length);
        byte[] ripemdHash = new byte[ripemd160.GetDigestSize()];
        ripemd160.DoFinal(ripemdHash, 0);

        // Step 3: Add version byte (0x00 for mainnet)
        byte[] versionedPayload = new byte[ripemdHash.Length + 1];
        versionedPayload[0] = 0x00;
        Array.Copy(ripemdHash, 0, versionedPayload, 1, ripemdHash.Length);

        // Step 4: Double SHA-256 for checksum
        byte[] checksum = sha256.ComputeHash(sha256.ComputeHash(versionedPayload)); 

        // Step 5: Base58Check encode
        string mainPartOfAddress = Helpers.ByteArrayToHexString(ripemdHash);
        string importantPartOfChecksum = Helpers.ByteArrayToHexString(checksum).Substring(0, 8);

        string fullAddressInHex = "00" + mainPartOfAddress + importantPartOfChecksum;
        string final = Helpers.Base58Encode(Helpers.HexToBytes(fullAddressInHex));
        Assert.Equal("12c6DSiU4Rq3P4ZxziKxzrL5LmMBrzjrJX", final);
    }
}
