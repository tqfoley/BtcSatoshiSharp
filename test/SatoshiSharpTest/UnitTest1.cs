using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Utilities.Encoders;
using SatoshiSharpLib;
using System.Security.Cryptography;

namespace SatoshiSharpTest;

public class SatoshiSharpTest
{
    [Fact]
    public void Test1()
    {
        Wallet w = new Wallet(new WalletAddress(0, 0, 0, 0));
        
        //Assert.Equal("", w.AddressHex);
    }

    [Fact]
    public void Block0()
    {
        BlockReader bdf = new BlockReader();
        byte[] key = new byte[] { 0x22, 0x6B, 0x64, 0x3B, 0x1C, 0xE5, 0x63, 0x68 };
        List<Wallet> wallets = new List<Wallet>();
        string path = "..\\..\\..\\..\\..\\btcblockdata\\blk00000.dat";
        bdf.ReadBlkDataFile( path, key, limit: 100);
        Assert.Equal("0000000000000000000000000000000000000000000000000000000000000000", Helpers.ReverseHexString(Helpers.ByteArrayToHexString(bdf.blocksInDataFile[0].header.PrevBlock)));
    }

    [Fact]
    public void BlockHeader1()
    {
        BlockReader bdf = new BlockReader();
        byte[] key = new byte[] { 0x22, 0x6B, 0x64, 0x3B, 0x1C, 0xE5, 0x63, 0x68 };
        List<Wallet> wallets = new List<Wallet>();
        string path = "..\\..\\..\\..\\..\\btcblockdata\\blk00000.dat";
        bdf.ReadBlkDataFile( path, key, limit: 100);
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

    [Fact]
    public void Address2()
    {
        string firstzero = "04678AFDB0FE5548271967F1A67130B7105CD6A828E03909A67962E0EA1F61DEB649F6BC3F4CEF38C4F35504E51EC112DE5C384DF7BA0B8D578A4C702B6BF11D5F";
        string pubKeyHex = //"0496b538e853519c726a2c91e61ec11600ae1390813a627c66fb8be7947be63c52da7589379515d4e0a604f8141781e62294721166bf621e73a82cbf2342c858ee";
                           "0496B538E853519C726A2C91E61EC11600AE1390813A627C66FB8BE7947BE63C52DA7589379515D4E0A604F8141781E62294721166BF621E73A82CBF2342C858EE";
        //pubKeyHex = firstzero.ToLower();
        string third = "047211A824F55B505228E4C3D5194C1FCFAA15A456ABDF37F9B9D97A4040AFC073DEE6C89064984F03385237D92167C13E236446B417AB79A0FCAE412AE3316B77";
        //pubKeyHex = third;
        byte[] pubKeyBytes = Hex.Decode(pubKeyHex);

        // Step 1: SHA-256
        SHA256 sha256 = SHA256.Create();
        byte[] sha256Hash = sha256.ComputeHash(pubKeyBytes);

        // Step 2: RIPEMD-160
        RipeMD160Digest ripemd160 = new RipeMD160Digest();
        ripemd160.BlockUpdate(sha256Hash, 0, sha256Hash.Length);
        byte[] ripemdHash = new byte[ripemd160.GetDigestSize()];
        ripemd160.DoFinal(ripemdHash, 0);
        Console.WriteLine(Helpers.ByteArrayToHexString(ripemdHash)); // got to here I think  119B098E2E980A229E139A9ED01A469E518E6F26

        // Step 3: Add version byte (0x00 for mainnet)
        byte[] versionedPayload = new byte[ripemdHash.Length + 1];
        versionedPayload[0] = 0x00;
        Array.Copy(ripemdHash, 0, versionedPayload, 1, ripemdHash.Length);

        Console.WriteLine(Helpers.ByteArrayToHexString(versionedPayload));

        // Step 4: Double SHA-256 for checksum
        byte[] checksum = sha256.ComputeHash(sha256.ComputeHash(versionedPayload));  // expected checksum 90AFE11C

        // Step 5: Base58Check encode
        Console.WriteLine(Helpers.ByteArrayToHexString(checksum).Substring(0, 8));

        string importantPartOfChecksum = Helpers.ByteArrayToHexString(checksum).Substring(0, 8);

        string addressOriginalBtc = "12c6DSiU4Rq3P4ZxziKxzrL5LmMBrzjrJX";
        Console.WriteLine("Bitcoin Address: " + addressOriginalBtc);
        byte[] addressbytes = Helpers.Base58Decode(addressOriginalBtc);
        string hexAddressWithExtra = Helpers.ByteArrayToHexString(addressbytes);
        Console.WriteLine(hexAddressWithExtra.Substring(2, hexAddressWithExtra.Length - (2 + 8))); //119B098E2E980A229E139A9ED01A469E518E6F26

        string mainPartOfAddress = hexAddressWithExtra.Substring(2, hexAddressWithExtra.Length - (2 + 8));

        byte[] f = Helpers.HexToBytes(Helpers.ByteArrayToHexString(versionedPayload) + importantPartOfChecksum);// "00119B098E2E980A229E139A9ED01A469E518E6F2690AFE11C");
        string myaddress = Helpers.Base58Encode(f);

        if (myaddress != addressOriginalBtc)
            throw new Exception("bad");

        string fullAddressInHex = "00" + mainPartOfAddress + importantPartOfChecksum;
        Console.WriteLine(fullAddressInHex);

        string j = Helpers.Base58Encode(Helpers.HexToBytes(fullAddressInHex));

        Assert.Equal("12c6DSiU4Rq3P4ZxziKxzrL5LmMBrzjrJX", j);
    }

    [Fact]
    public void Wallet2()
    {
        
        //WalletAddress = new()
        /*string myaddress = Helpers.Base58Encode(Helpers.HexToBytes("0062E907B15CBF27D5425399EBF6F0FB50EBB88F18C29B7D93"));

        string myaddress2 = Helpers.Base58Encode(f);
        byte[] g = Helpers.Base58Decode(myaddress2); //           01234567890123456789012345678901234567890123456789
        string hexstring2 = Helpers.ByteArrayToHexString(g); // "0062E907B15CBF27D5425399EBF6F0FB50EBB88F18C29B7D93"
        string base58 = Helpers.Base58Encode(Helpers.HexToBytes(hexstring2));

        string k = base58;

        WalletAddress g55 = new WalletAddress(hexstring2);

        string eee = g55.getHex();
        */
        //string eee2 = g55.getBase58();
    }

}
