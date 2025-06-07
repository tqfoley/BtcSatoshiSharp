using System.Text;

using Org.BouncyCastle.Math;
using Org.BouncyCastle.Crypto.Digests;

namespace SatoshiSharpLib
{

    public class Helpers // the majority of this class generated with ChatGPT
    {

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
