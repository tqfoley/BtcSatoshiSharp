
using Org.BouncyCastle.Utilities;
using System.Text;
using System.Security.Cryptography;

namespace SatoshiSharpLib
{

    public class Block
    {
        public int BlockNumber = -1;

        public Header header { get; 
            set; } = new Header();

        public List<Transaction> Transactions { get; 
            set; } = new List<Transaction>();

        public Block PreviousBlock { get; 
            set; }


        public class ThirtyTwoByteClass
        {
            public readonly byte[] ThirtyTwoBytes;

            public readonly int Length = 32;

            public ThirtyTwoByteClass(byte[] value)
            {
                if (value?.Length != 32)
                {
                    throw new ArgumentException("MerkleRoot must be exactly 32 bytes");
                }    
                ThirtyTwoBytes = value ?? throw new ArgumentNullException(nameof(value));
            }

            public byte[] Value => ThirtyTwoBytes;


            public int GetLength()
            {
                return 32;
            }

            public override string ToString()
            {

                return Helpers.GetStringReverseHexBytes(ThirtyTwoBytes); // Using your extension method
            }

            // Implicit conversion operators for convenience
            public static implicit operator byte[](ThirtyTwoByteClass merkleRoot) => merkleRoot.ThirtyTwoBytes;
            public static implicit operator ThirtyTwoByteClass(byte[] bytes) => new ThirtyTwoByteClass(bytes);
        }

        public class Header
        {
            public uint Version { get; set; }
            public ThirtyTwoByteClass PrevBlockHash { get; set; }
            //public byte[] MerkleRoot { get; set; }  // 32 bytes
            public ThirtyTwoByteClass MerkleRoot { get; set; }
            public uint Timestamp { get; set; }
            public uint Bits { get; set; }
            public uint Nonce { get; set; }
            public ulong TransactionCount { get; set; }


            public static string CalculateBlockHash(Block.Header blockHeader)
            {
                // Convert block header to bytes
                byte[] headerBytes = SerializeBlockHeader(blockHeader);

                // Bitcoin uses double SHA-256 (SHA-256 of SHA-256)
                using (SHA256 sha256 = SHA256.Create())
                {
                    byte[] firstHash = sha256.ComputeHash(headerBytes);
                    byte[] secondHash = sha256.ComputeHash(firstHash);

                    // Reverse bytes for little-endian display (Bitcoin convention)
                    Array.Reverse(secondHash);

                    // Convert to hexadecimal string
                    return BitConverter.ToString(secondHash).Replace("-", "").ToLower();
                }
            }

            public static byte[] SerializeBlockHeader(Block.Header header)
            {
                var bytes = new byte[80]; // Bitcoin block header is always 80 bytes
                int offset = 0;

                // Version (4 bytes, little-endian)
                Helpers.WriteUInt32LE(bytes, offset, (uint)header.Version);
                offset += 4;

                // Previous block hash (32 bytes, little-endian)
                byte[] prevHashBytes = Helpers.HexToBytes(header.GetPrevBlockHashAsString()); // toDo trevor to do don't convert to string and back
                Array.Reverse(prevHashBytes); // Convert to little-endian
                Array.Copy(prevHashBytes, 0, bytes, offset, 32);
                offset += 32;

                // Merkle root (32 bytes, little-endian)
                byte[] merkleBytes = Helpers.HexToBytes(header.GetMerkleRootAsString()); // toDo trevor to do don't convert to string and back
                Array.Reverse(merkleBytes); // Convert to little-endian
                Array.Copy(merkleBytes, 0, bytes, offset, 32);
                offset += 32;

                // Timestamp (4 bytes, little-endian)
                Helpers.WriteUInt32LE(bytes, offset, (uint)header.Timestamp);
                offset += 4;

                // Bits (4 bytes, little-endian)
                Helpers.WriteUInt32LE(bytes, offset, header.Bits);
                offset += 4;

                // Nonce (4 bytes, little-endian)
                Helpers.WriteUInt32LE(bytes, offset, header.Nonce);

                string j = Helpers.ByteArrayToHexString(bytes);

                return bytes;
            }

            public string ReverseRemoveDashAndToLower(byte[] a)
            {
                string b = BitConverter.ToString(a).Replace("-", "");
                string c = Helpers.ReverseHexString(b).ToLower();
                return c;
            }

            public void ConsoleWrite()
            {
                Console.WriteLine("Version: " + Version);
                Console.WriteLine("PrevBlockHash: " + Helpers.GetStringReverseHexBytes(PrevBlockHash));
                Console.WriteLine("------------------------------------------------------");
                Console.WriteLine("MerkleRoot: " + Helpers.GetStringReverseHexBytes(MerkleRoot));
                Console.WriteLine("Timestamp: " + Timestamp);
                Console.WriteLine("Bits: " + Bits);
                Console.WriteLine("Nonce: " + Nonce);
                Console.WriteLine("TransactionCount: " + TransactionCount);
            }

            public override string ToString()
            {
                string prevBlockWithZerosAtEnd = BitConverter.ToString(PrevBlockHash).Replace("-", "");
                string prevBlockMatchExplorers = Helpers.ReverseHexString(prevBlockWithZerosAtEnd).ToLower();
                string hexBits = Bits.ToString("X8");



                return $"Version: {Version}\n" +
                       //$"Previous Block: {BitConverter.ToString(PrevBlock).Replace("-", "")}\n" +
                       $"Previous Block: {prevBlockMatchExplorers}\n" +
                       $"---------------------------------------------------\n" +
                       $"Merkle Root: {ReverseRemoveDashAndToLower(MerkleRoot)}\n" +
                       $"Timestamp: {Timestamp} ({UnixTimeStampToDateTime(Timestamp)})\n" +
                       $"Bits: {hexBits}\n" +
                       $"Nonce: {Nonce}\n" +
                       $"TransactionCount: {TransactionCount}";
            }

            private static DateTime UnixTimeStampToDateTime(uint unixTime)
            {
                return DateTimeOffset.FromUnixTimeSeconds(unixTime).UtcDateTime;
            }

            public static Header Parse(byte[] blockData)
            {
                using (MemoryStream ms = new MemoryStream(blockData))
                using (BinaryReader reader = new BinaryReader(ms))
                {
                    Header header = new Header();
                    header = new Header
                    {
                        Version = reader.ReadUInt32(),         // 4 bytes
                        PrevBlockHash = reader.ReadBytes(32),      // 32 bytes
                        MerkleRoot = reader.ReadBytes(32),     // 32 bytes
                        Timestamp = reader.ReadUInt32(),       // 4 bytes
                        Bits = reader.ReadUInt32(),            // 4 bytes
                        Nonce = reader.ReadUInt32(),            // 4 bytes
                    };

                    if(Helpers.GetStringReverseHexBytes(header.MerkleRoot).ToLower().Contains("e506a3cf0cf2e190e8c88fd45646b8a5f95e69c7cb0cd0b6de4bef5f4dad".ToLower()))
                    {
                        Console.WriteLine("das");
                    }

                    Console.WriteLine(header);
                    return header;
                }
            }

            public string GetMerkleRootAsString()
            {
                return Helpers.ReverseHexString(
                    Helpers.ByteArrayToHexString(MerkleRoot));
            }
            public string GetPrevBlockHashAsString()
            {
                return Helpers.ReverseHexString(Helpers.ByteArrayToHexString(PrevBlockHash));
                //return Encoding.UTF8.GetString(PrevBlockHash);
            }

            /*private static ulong ReadVarInt(BinaryReader reader)
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
            }*/
        }

        public static byte[] DoubleSha256(byte[] data)
        {
            using (var sha256 = SHA256.Create())
            {
                var firstHash = sha256.ComputeHash(data);
                return sha256.ComputeHash(firstHash);
            }
        }

        public static byte[] CalculateMerkleRoot(List<byte[]> transactions)
        {
            if (transactions == null || transactions.Count == 0)
                throw new ArgumentException("Transactions list cannot be null or empty");

            // Step 1: Calculate SHA256 hash twice for each transaction (Bitcoin uses double SHA256)
            var hashes = new List<byte[]>();
            foreach (var tx in transactions)
            {
                hashes.Add(DoubleSha256(tx));
            }

            // Step 2: Build the Merkle tree by repeatedly hashing pairs
            while (hashes.Count > 1)
            {
                var nextLevel = new List<byte[]>();

                for (int i = 0; i < hashes.Count; i += 2)
                {
                    byte[] left = hashes[i];
                    byte[] right;

                    // If odd number of hashes, duplicate the last one (Bitcoin protocol rule)
                    if (i + 1 < hashes.Count)
                        right = hashes[i + 1];
                    else
                        right = hashes[i]; // Duplicate the last hash

                    // Concatenate left + right and hash
                    var combined = new byte[left.Length + right.Length];
                    Array.Copy(left, 0, combined, 0, left.Length);
                    Array.Copy(right, 0, combined, left.Length, right.Length);

                    nextLevel.Add(DoubleSha256(combined));
                }

                hashes = nextLevel;
            }

            return hashes[0]; // The final hash is the Merkle root
        }

        public static string BytesToHex(byte[] bytes)
        {
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        public static byte[] HexToBytes(string hex)
        {
            if (hex.Length % 2 != 0)
                throw new ArgumentException("Hex string must have even length");

            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < hex.Length; i += 2)
            {
                bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
            }
            return bytes;
        }


    }
}