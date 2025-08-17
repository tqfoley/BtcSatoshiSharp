using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Utilities.Encoders;
using System.Text.Json;

using SatoshiSharpLib;
using static SatoshiSharpLib.Helpers;

namespace main
{
    public class SatoshiSharp
    {
        public static byte[] OP_HASH160(byte[] input)
        {
            // 1. SHA-256
            byte[] sha256Hash = SHA256.Create().ComputeHash(input);

            // 2. RIPEMD-160
            //using (var ripemd160 = new RIPEMD160())
            //{
            //    byte[] ripemd160Hash = ripemd160.ComputeHash(sha256Hash);
            //    return ripemd160Hash;
            //}
            return null;
        }

        public static byte[] OP_HASH160(string input)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(input);
            return OP_HASH160(bytes);
        }

        static void SaveDataWithOutKeyMaxBytes(byte[] key, string inputPath, int maxBytes)
        {
            if (!File.Exists(inputPath))
            {
                Console.WriteLine("File not found.");
                return;
            }

            try
            {
                using (FileStream fs = new FileStream(inputPath, FileMode.Open, FileAccess.Read))
                {
                    // Read only the first 10,000 bytes (or less if file is smaller)
                   
                    byte[] buffer = new byte[Math.Min(maxBytes, (int)fs.Length)];
                    fs.Read(buffer, 0, buffer.Length);

                    // XOR encryption
                    for (int i = 0; i < buffer.Length; i++)
                    {
                        buffer[i] ^= key[i % key.Length];
                    }

                    // Save with "_xored" before extension
                    string dir = Path.GetDirectoryName(inputPath);
                    string name = Path.GetFileNameWithoutExtension(inputPath);
                    string ext = Path.GetExtension(inputPath);
                    string outputPath = Path.Combine(dir, $"{name}_xored{ext}");

                    File.WriteAllBytes(outputPath, buffer);

                    Console.WriteLine($"Processed first {buffer.Length} bytes and saved as: {outputPath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }
        public class Settings
        {
            // Changed to instance properties for JSON deserialization
            public string Version { get; set; } = string.Empty;
            public string BlockChainDataDirectory { get; set; } = string.Empty;
            public LoggingSettings Logging { get; set; } = new();
        }

        public class LoggingSettings
        {
            public string FilePath { get; set; } = string.Empty;
        }

        public static class SettingsReader
        {

            public static Settings ReadSettings(string filePath = "settings.txt")
            {
                try
                {
                    if (!File.Exists(filePath))
                        throw new FileNotFoundException($"Settings file not found: {filePath}");

                    string jsonContent = File.ReadAllText(filePath);

                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        AllowTrailingCommas = true,
                        ReadCommentHandling = JsonCommentHandling.Skip
                    };

                    var settings = JsonSerializer.Deserialize<Settings>(jsonContent, options)
                        ?? throw new InvalidDataException("Failed to deserialize settings");

                    return settings;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Error reading settings from {filePath}: {ex.Message}", ex);
                }
            }
        }

        static void Main(string[] args)
        {
            Console.WriteLine("Read Settings");
            var settings = SettingsReader.ReadSettings(Path.Combine(Helpers.GetParentDirectory(".", 5), "settings.json"));
            Console.WriteLine($"Block Chain data: {settings.BlockChainDataDirectory}" );

            Wallet wallet = new Wallet( new WalletAddress(0,0,0,0));// { AddressBase58 = "", AddressHex = "", Transactions = new List<WalletTransaction>()};
            

              string scriptHex = "410496b538e853519c726a2c91e61ec11600ae1390813a627c66fb8be7947be63c52da7589379515d4e0a604f8141781e62294721166bf621e73a82cbf2342c858eeac";
            //second bitcoin transaction     41   0496b538e853519c726a2c91e61ec11600ae1390813a627c66fb8be7947be63c52da7589379515d4e0a604f8141781e62294721166bf621e73a82cbf2342c858ee  ac   reward 50 BTC
            //410496B538E853519C726A2C91E61EC11600AE1390813A627C66FB8BE7947BE63C52DA7589379515D4E0A604F8141781E62294721166BF621E73A82CBF2342C858EEAC}
            //ScriptPubKey: 410496B538E853519C726A2C91E61EC11600AE1390813A627C66FB8BE7947BE63C52DA7589379515D4E0A604F8141781E62294721166BF621E73A82CBF2342C858EEAC}
            //0496b538e853519c726a2c91e61ec11600ae1390813a627c66fb8be7947be63c52da7589379515d4e0a604f8141781e62294721166bf621e73a82cbf2342c858ee
            // to 12c6DSiU4Rq3P4ZxziKxzrL5LmMBrzjrJX =
            //                             0011  9b098e2e980a229e139a9ed01a469e518e6f2690afe11c


            /*
             * OP_DUP OPHASH160 <119B098E2E980A229E139A9ED01A469E518E6F26> OP_EQUALVERIFY OP_CHECKSIG
               becomes --> 12c6DSiU4Rq3P4ZxziKxzrL5LmMBrzjrJX
             */


            /*
  1 - Public ECDSA Key
0496B538E853519C726A2C91E61EC11600AE1390813A627C66FB8BE7947BE63C52DA7589379515D4E0A604F8141781E62294721166BF621E73A82CBF2342C858EE
2 - SHA-256 hash of 1
6527751DD9B3C2E5A2EE74DB57531AE419C786F5B54C165D21CDDDF04735281F
3 - RIPEMD-160 Hash of 2
119B098E2E980A229E139A9ED01A469E518E6F26
4 - Adding network bytes to 3
00119B098E2E980A229E139A9ED01A469E518E6F26
5 - SHA-256 hash of 4
D304D9060026D2C5AED09B330B85A8FF10926AC432C7A7AEE384E47B2FA1A670
6 - SHA-256 hash of 5
90AFE11C54D3BF6BACD6BF92A3F46EECBE9316DC1AF9287791A25D340E67F535
7 - First four bytes of 6
90AFE11C
8 - Adding 7 at the end of 4
00119B098E2E980A229E139A9ED01A469E518E6F2690AFE11C
9 - Base58 encoding of 8
12c6DSiU4Rq3P4ZxziKxzrL5LmMBrzjrJX
             */


            // my transaction send 732 in python                                01976a91491c794eb0d1b7760639b7c5a863521b09c31d4de88ac00000000
            //base58.b58decode_int('1EHp4zm1T2yUyjEmW3H4qauTJiVEXan3Uf'))[2:-8] = 0091C794EB0D1B7760639B7C5A863521B09C31D4DE    remove last eight 8D97C3A4

            /*{
                byte[] addressbytes = Helpers.Base58Decode("1EHp4zm1T2yUyjEmW3H4qauTJiVEXan3Uf");
                string hexAddressWithExtra = Helpers.ByteArrayToHexString(addressbytes);
                Console.WriteLine(hexAddressWithExtra.Substring(2, hexAddressWithExtra.Length - (2 + 8)));
            }

            {
                string addressOriginalBtc = "12c6DSiU4Rq3P4ZxziKxzrL5LmMBrzjrJX";
                byte[] addressbytes = Helpers.Base58Decode(addressOriginalBtc);
                string hexAddressWithExtra = Helpers.ByteArrayToHexString(addressbytes);
                Console.WriteLine(hexAddressWithExtra.Substring(2, hexAddressWithExtra.Length - (2 + 8))); //119B098E2E980A229E139A9ED01A469E518E6F26
                Console.WriteLine(hexAddressWithExtra); // 00119B098E2E980A229E139A9ED01A469E518E6F2690AFE11C   version in front plus 8 checksum
                Console.WriteLine(hexAddressWithExtra); 
            }*/



            {
                string firstzero = "04678AFDB0FE5548271967F1A67130B7105CD6A828E03909A67962E0EA1F61DEB649F6BC3F4CEF38C4F35504E51EC112DE5C384DF7BA0B8D578A4C702B6BF11D5F";
                string pubKeyHex = //"0496b538e853519c726a2c91e61ec11600ae1390813a627c66fb8be7947be63c52da7589379515d4e0a604f8141781e62294721166bf621e73a82cbf2342c858ee";
                                   "0496B538E853519C726A2C91E61EC11600AE1390813A627C66FB8BE7947BE63C52DA7589379515D4E0A604F8141781E62294721166BF621E73A82CBF2342C858EE";
                //pubKeyHex = firstzero.ToLower();
                string third =     "047211A824F55B505228E4C3D5194C1FCFAA15A456ABDF37F9B9D97A4040AFC073DEE6C89064984F03385237D92167C13E236446B417AB79A0FCAE412AE3316B77";
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
                Console.WriteLine(Helpers.ByteArrayToHexString(checksum).Substring(0,8));

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
                //                                                  00119B098E2E980A229E139A9ED01A469E518E6F26 90AFE11C  this is correct
                // not sure what this is                            00119B098E2E980A229E139A9ED01A469E518E6F268D97C3A4 WRONG
                Console.WriteLine(j);




                Console.WriteLine("Bitcoin Address: ");
            }

            Console.WriteLine("here");

            //string ripe160Hash = RIPEMD160Hash.Compute(Helpers.PrintHexPreview(sha256Hash2)); //Helpers.PrintHexPreview(sha256Hash));
            //Console.WriteLine((ripe160Hash));

            // 3. Add version byte (0x00 for mainnet)
            /*byte[] versionedPayload = new byte[1 + ripe160Hash.Length];
            versionedPayload[0] = 0x00;
            Buffer.BlockCopy(ripe160Hash, 0, versionedPayload, 1, ripe160Hash.Length);

            // 4. Double SHA256 for checksum
            byte[] checksum = SHA256.Create().ComputeHash(SHA256.Create().ComputeHash(versionedPayload)).Take(4).ToArray();

            // 5. Concatenate versionedPayload + checksum
            byte[] finalPayload = versionedPayload.Concat(checksum).ToArray();

            // 6. Base58Check encode
            string address = Helpers.Base58Encode(finalPayload);

            /*Console.WriteLine($"Bitcoin address: {address}");
                        string filePath = "..\\..\\xor.dat"; // Replace with your actual path

            try
            {
                byte[] bytes = ReadFirstNBytes(filePath, 8);
                Console.WriteLine("First 100 bytes in hex:");
                PrintHex(bytes);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading file: {ex.Message}");
            }*/

            BlockReader bdf = new BlockReader();

            //byte[] data = new byte[8] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
            //File.WriteAllBytes(Path.Combine(Helpers.GetParentDirectory(".", 5), settings.BlockChainDataDirectory, "xorZero.dat"), data);

            byte[] key = new byte[] { 0, 0, 0, 0, 0, 0, 0, 0}; //{142, 205, 168, 81, 211, 222, 85, 154 };

            if (key[0] != 0 || key[1] != 0 ||
                key[2] != 0 || key[3] != 0 ||
                key[4] != 0 || key[5] != 0 ||
                key[6] != 0 || key[7] != 0)
            {
                throw new Exception("use all zero! for xor data!");
            }

            //{ 0x22, 0x6B, 0x64, 0x3B, 0x1C, 0xE5, 0x63, 0x68 }; // different for everyone
            // to do read file in btcblockdata
            string xorPath = Path.Combine(Helpers.GetParentDirectory(".", 5), settings.BlockChainDataDirectory, "xor.dat");
            byte[] xorData = File.ReadAllBytes(xorPath);
            if(key[0] != xorData[0] ||                key[1] != xorData[1] ||
                key[2] != xorData[2] ||                key[3] != xorData[3] ||
                key[4] != xorData[4] ||                key[5] != xorData[5] ||
                key[6] != xorData[6] ||                key[7] != xorData[7])
            {
                SaveDataWithOutKeyMaxBytes(key,                     Path.Combine(Helpers.GetParentDirectory(".", 5), settings.BlockChainDataDirectory, "blk00000.dat"),                    500000);
                SaveDataWithOutKeyMaxBytes(key, Path.Combine(Helpers.GetParentDirectory(".", 5), settings.BlockChainDataDirectory, "blk00001.dat"),                    500000);
                SaveDataWithOutKeyMaxBytes(key, Path.Combine(Helpers.GetParentDirectory(".", 5), settings.BlockChainDataDirectory, "blk00002.dat"),                    500000);
                throw new Exception("bad xor data");
            }


            string path = Path.Combine(Helpers.GetParentDirectory(".", 5), settings.BlockChainDataDirectory, "blk00000.dat");
            
            StateWallets.Wallets =
            [
                new Wallet(new WalletAddress(0, 0, 0, 0)), // add the reward wallet
            ];


            Block lastBlock = null;
            if (bdf.blocksInDataFile.Count != 0)
            {
                lastBlock = bdf.blocksInDataFile.Last();

            }
            bdf.ReadBlkDataFile(path, key, lastBlock, limit:1289);

            Console.WriteLine(Helpers.ReverseHexString(Helpers.ByteArrayToHexString(bdf.blocksInDataFile[0].header.PrevBlockHash)));
            Console.WriteLine(Helpers.ReverseHexString(Helpers.ByteArrayToHexString(bdf.blocksInDataFile[1].header.PrevBlockHash)));
            Console.WriteLine(Helpers.ReverseHexString(Helpers.ByteArrayToHexString(bdf.blocksInDataFile[2].header.PrevBlockHash)));
            Console.WriteLine(Helpers.ReverseHexString(Helpers.ByteArrayToHexString(bdf.blocksInDataFile[3].header.PrevBlockHash)));
            Console.WriteLine(Helpers.ReverseHexString(Helpers.ByteArrayToHexString(bdf.blocksInDataFile[4].header.PrevBlockHash)));
            Console.WriteLine(Helpers.ReverseHexString(Helpers.ByteArrayToHexString(bdf.blocksInDataFile[5].header.PrevBlockHash)));


            //PrintHexPreview(bdf.data, 300);

            var v = Helpers.BitcoinBase58AddressToHexString("12c6DSiU4Rq3P4ZxziKxzrL5LmMBrzjrJX");

            var b  = Helpers.BitcoinBase58AddressToHexString("1EHp4zm1T2yUyjEmW3H4qauTJiVEXan3Uf");

            Console.WriteLine(v);

            //string filePath = "..\\..\\..\\..\\..\\btcblockdata\\blk00000.dat"; // path to bitcoin blockchain data
            //string hexString = File.ReadAllText(filePath).Trim();
            /*byte[] bytes = File.ReadAllBytes(path);
            string hexString = BitConverter.ToString(bytes).Replace("-", "");



            byte[] byteArray = new byte[hexString.Length / 2];
            Console.WriteLine("byte size of entire block:" + (hexString.Length / 2));
            for (int i = 0; i < byteArray.Length-1; i++)
            {
                string hexByte = hexString.Substring(i * 2, 2);
                byteArray[i] = Convert.ToByte(hexByte, 16);
            }

            // Example: Print bytes
            Console.WriteLine(BitConverter.ToString(byteArray));*/

            //var block = Header.Parse(byteArray);
            //Console.WriteLine(block);
            








            Console.WriteLine("End\n");
        }
    }
}


