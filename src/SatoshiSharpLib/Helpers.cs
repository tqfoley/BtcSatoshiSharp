using System.Text;
using System.Security.Cryptography;

using Org.BouncyCastle.Math;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Utilities.Encoders;

namespace SatoshiSharpLib
{

    public class Helpers // the majority of this class generated with ChatGPT
    {

        public static string GetParentDirectory(string path, int numberParents = 1)
        {
            string g = Directory.GetParent(path).FullName;
            for (int i = 1; i < numberParents; i++)
            {
                g = Directory.GetParent(g).FullName;
            }
            return g;
        }

        public static List<string> GetAllFiles(string path)
        {
            List<string> ret = new List<string>();
            try
            {
                Console.WriteLine("TREVOR trevor all files");
                string[] files = Directory.GetFiles(path);

                foreach (string file in files)
                {
                    ret.Add(file);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}\n");
            }
            return ret;
        }

        public static void readSignedSpend(int blockNumber, byte[] script, ulong valueSats, List<Wallet> stateWallets)
        {
            List<Spend> ret = new List<Spend>();

            WalletAddress destinationAddress = new WalletAddress(0, 0, 0, 0);

            string scriptHex = ByteArrayToHexString(script);

            string scriptHexTest = "410496b538e853519c726a2c91e61ec11600ae1390813a627c66fb8be7947be63c52da7589379515d4e0a604f8141781e62294721166bf621e73a82cbf2342c858eeac";


            byte[] pubKeyBytes = Hex.Decode(scriptHex.Substring(2, scriptHex.Length - 4));

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

            //string addressOriginalBtc = "12c6DSiU4Rq3P4ZxziKxzrL5LmMBrzjrJX";
            //Console.WriteLine("Bitcoin Address: " + addressOriginalBtc);
            //byte[] addressbytes = Helpers.Base58Decode(addressOriginalBtc);
            //string hexAddressWithExtra = Helpers.ByteArrayToHexString(addressbytes);
            //Console.WriteLine(hexAddressWithExtra.Substring(2, hexAddressWithExtra.Length - (2 + 8))); //119B098E2E980A229E139A9ED01A469E518E6F26
            //string mainPartOfAddress = hexAddressWithExtra.Substring(2, hexAddressWithExtra.Length - (2 + 8));

            byte[] f = Helpers.HexToBytes(Helpers.ByteArrayToHexString(versionedPayload) + importantPartOfChecksum);// "00119B098E2E980A229E139A9ED01A469E518E6F2690AFE11C");


            //WalletAddress = new()
            string myaddress = Helpers.Base58Encode(f);

            string myaddress2 = Helpers.Base58Encode(f);
            byte[] g = Helpers.Base58Decode(myaddress2); //           01234567890123456789012345678901234567890123456789
            string hexstring2 = Helpers.ByteArrayToHexString(g); // "0062E907B15CBF27D5425399EBF6F0FB50EBB88F18C29B7D93"
            string base58 = Helpers.Base58Encode(Helpers.HexToBytes(hexstring2));

            string k = base58;

            WalletAddress destinationWallet = new WalletAddress(hexstring2);

            //string eee = g55.getHex();

            //string eee2 = g55.getBase58();

            var dest = StateWallets.getWallet(destinationWallet);

            if (dest == null)
            {
                stateWallets.Add(new Wallet(destinationWallet));
            }

            //WalletTransaction trevor
            Transaction t = new Transaction { BlockNumber = 0, Spends = new List<Spend>() };

            Spend s = new Spend(new WalletAddress(0, 0, 0, 0), destinationWallet, valueSats);

            s.DestinationWallet = dest;

        }

        public static string ReverseHexString(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length % 2 != 0)
                throw new ArgumentException("Hex string must have an even number of characters.");

            int byteCount = hex.Length / 2;
            string[] bytePairs = new string[byteCount];

            // Break into byte-sized chunks
            for (int i = 0; i < byteCount; i++)
            {
                bytePairs[i] = hex.Substring(i * 2, 2);
            }

            // Reverse the byte order
            Array.Reverse(bytePairs);

            // Join into final hex string
            return string.Join("", bytePairs);
        }

        public static string ByteArrayToHexString(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return string.Empty;

            return BitConverter.ToString(bytes).Replace("-", "");
        }

        // Helper: Hex to byte[]
        public static byte[] HexToBytes(string hex)
        {
            return Enumerable.Range(0, hex.Length / 2)
                             .Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16))
                             .ToArray();
        }

        public static string Base58Encode(byte[] input)
        {
            const string alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
            BigInteger intData = new BigInteger(1, input);

            StringBuilder result = new StringBuilder();
            while (intData.CompareTo(BigInteger.Zero) > 0)
            {
                BigInteger[] divRem = intData.DivideAndRemainder(BigInteger.ValueOf(58));
                intData = divRem[0];
                int remainder = divRem[1].IntValue;
                result.Insert(0, alphabet[remainder]);
            }

            // Add '1' for each leading 0 byte
            foreach (byte b in input)
            {
                if (b == 0x00)
                    result.Insert(0, '1');
                else
                    break;
            }

            return result.ToString();
        }

        public static string PrintHexPreview(byte[] data)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(data.Length * 2);
            foreach (byte b in data)
            {
                sb.AppendFormat("{0:x2}", b);
            }
            //console.WriteLine(sb.ToString());
            return sb.ToString();
        }

        public static void PrintHexPreview(byte[] data, int maxBytes)
        {
            int count = Math.Min(maxBytes, data.Length);
            Console.WriteLine($"Displaying first {count} bytes in hex:");

            for (int i = 0; i < count; i++)
            {
                Console.Write($"{data[i]:X2} ");

                // Add a newline every 16 bytes for readability
                if ((i + 1) % 16 == 0)
                {
                    Console.WriteLine();
                }
            }

            if (count % 16 != 0)
            {
                Console.WriteLine(); // Final newline if needed
            }
        }

        public static byte[] ReadFirstNBytes(string filePath, int count)
        {
            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                byte[] buffer = new byte[count];
                int bytesRead = fs.Read(buffer, 0, count);
                if (bytesRead < count)
                {
                    Array.Resize(ref buffer, bytesRead);
                }
                return buffer;
            }
        }

        public static void PrintHex(byte[] bytes)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                Console.Write($"{bytes[i]:X2} ");
                if ((i + 1) % 16 == 0)
                    Console.WriteLine();
            }
            if (bytes.Length % 16 != 0)
                Console.WriteLine();
        }
        
        public static void PrintHexData(BinaryReader br, int maxBytes)
        {
            byte[] a = br.ReadBytes(maxBytes); // 0x10

            for (int i = 0; i < maxBytes; i++)
            {
                Console.Write($"{a[i]:X2} ");

                // Add a newline every 16 bytes for readability
                if ((i + 1) % 32 == 0)
                {
                    Console.WriteLine();
                }
            }

            if (maxBytes % 32 != 0)
            {
                Console.WriteLine(); // Final newline if needed
            }

            br.BaseStream.Seek(-1 * maxBytes, SeekOrigin.Current);
        }

        public static void PrintHexData(byte[] data, int maxBytes)
        {
            int count = Math.Min(maxBytes, data.Length);
            Console.WriteLine($"Displaying first {count} bytes in hex:");

            for (int i = 0; i < count; i++)
            {
                Console.Write($"{data[i]:X2} ");

                // Add a newline every 16 bytes for readability
                if ((i + 1) % 16 == 0)
                {
                    Console.WriteLine();
                }
            }

            if (count % 16 != 0)
            {
                Console.WriteLine(); // Final newline if needed
            }
        }

        public static ulong ReadVarInt(BinaryReader reader)
        {
            byte prefix = reader.ReadByte();

            if (prefix < 0xfd)
            {
                return prefix;
            }
            else if (prefix == 0xfd)
            {
                return reader.ReadUInt16(); // 2 bytes
            }
            else if (prefix == 0xfe)
            {
                return reader.ReadUInt32(); // 4 bytes
            }
            else if (prefix == 0xff)
            {
                return reader.ReadUInt64(); // 8 bytes
            }
            else
            {
                throw new Exception("Invalid VarInt prefix");
            }
        }

        private const string Base58Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

        public static byte[] Base58Decode(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                throw new ArgumentNullException(nameof(input));

            // Convert base58 string to byte array
            var bytes = new List<byte> { 0 };

            foreach (char c in input)
            {
                int carry = Base58Alphabet.IndexOf(c);
                if (carry < 0)
                    throw new FormatException($"Invalid Base58 character '{c}'");

                for (int i = 0; i < bytes.Count; ++i)
                {
                    carry += bytes[i] * 58;
                    bytes[i] = (byte)(carry & 0xFF);  // mod 256
                    carry >>= 8;                      // divide by 256
                }

                while (carry > 0)
                {
                    bytes.Add((byte)(carry & 0xFF));
                    carry >>= 8;
                }
            }

            // Deal with leading zeros
            int leadingZeroCount = input.TakeWhile(c => c == '1').Count();
            var leadingZeros = Enumerable.Repeat((byte)0, leadingZeroCount);

            // Since the result is little-endian, reverse it
            return leadingZeros.Concat(bytes.AsEnumerable().Reverse()).ToArray();
        }

        public static string BitcoinBase58AddressToHexString(string address)
        {
            var decoded = Base58Decode(address);
            return BitConverter.ToString(decoded).Replace("-", "").ToLower();
        }
    }

}
