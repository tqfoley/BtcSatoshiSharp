
using System;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;
using System.Linq;



namespace SatoshiSharpLib
{
    public class BlockReader
    {
        public BlockReader()
        {
        }

        public byte[] data = null;
        public byte[] MagicBytes { get; private set; }       // 4 bytes
        public uint BlockSize { get; private set; }          // 4 bytes

        public List<Block> blocksInDataFile = new List<Block>();

        public int ReadBlkDataFile(string FilePathSpecificToMyKey, byte[] key, int blockNumberOffset, int? limit=null)
        {
            if (key.Length != 8)
            {
                throw new ArgumentException("Key must be exactly 8 bytes long.");
            }

            int totalBytes = 0;

            byte[] dataSpecificToMyKey = null;
            try
            {
                dataSpecificToMyKey = File.ReadAllBytes(FilePathSpecificToMyKey);
                Console.WriteLine($"Read {dataSpecificToMyKey.Length} bytes from file.");
                totalBytes = dataSpecificToMyKey.Length;

                // Example: print first 16 bytes
                for (int i = 0; i < Math.Min(16, dataSpecificToMyKey.Length); i++)
                {
                    //Console.Write($"{dataSpecificToMyKey[i]:X2} ");
                }
            }
            catch (IOException ex)
            {
                Console.WriteLine("Error reading file: " + ex.Message);
                throw new Exception("bad");
            }


            data = new byte[dataSpecificToMyKey.Length];

            for (int i = 0; i < dataSpecificToMyKey.Length; i++)
            {
                data[i] = (byte)(dataSpecificToMyKey[i] ^ key[i % 8]);
            }



            int currentBytesRead = 0;
            bool exactBytesAccountedForNoExtra = false;

            //BlockDataFile data = new BlockDataFile();

            if (limit == null)
            {
                limit = int.MaxValue;
            }

            int blockIndexInDataFile = 0;
            int blockNumber = 0;

            using (MemoryStream ms = new MemoryStream(data))
            using (BinaryReader reader = new BinaryReader(ms))
            {

                //fix loop here to do todo trevor
                while (ms.Position < ms.Length && limit-- > 0)
                    //for (int j = 0; j < 4; j++)
                {
                    //Magic Bytes (4 bytes)
                    MagicBytes = reader.ReadBytes(4);

                    byte[] expectedMagic = new byte[] { 0xF9, 0xBE, 0xB4, 0xD9 };

                    for (int i = 0; i < 4; i++)
                    {
                        if (MagicBytes[i] != expectedMagic[i])
                        {
                            throw new InvalidDataException("Invalid magic bytes: not a valid Bitcoin block.");
                        }
                    }

                    // Block Size (4 bytes)
                    //Helpers.PrintHexData(reader, 4);
                    BlockSize = reader.ReadUInt32();
                    currentBytesRead += (int)BlockSize + 4 + 4; // 4 bytes for magic number and 4 bytes for block size 

                    if (BlockSize != 215 && BlockSize != 216)
                    {
                        Console.WriteLine("not 215 bytes");
                    }
                    //Helpers.PrintHexData(reader, (int)BlockSize + 8);

                    if (BlockSize > 266222)
                    {
                        throw new InvalidDataException("block size bigger than 266222.");
                    }

                    using (MemoryStream ms2 = new MemoryStream(reader.ReadBytes((int)BlockSize)))
                    using (BinaryReader reader2 = new BinaryReader(ms2))
                    {
                        //Helpers.PrintHexData(reader2, (int)BlockSize);
                        //loop here to do todo trevor
                        Block newBlock = new Block();
                        {
                            newBlock.header = Block.Header.Parse(reader2.ReadBytes(80));
                            //reader2.ReadBytes(80);

                            ulong TransactionCount = Helpers.ReadVarInt(reader2);  // In some cases 2 bytes like values 0 or 1


                            if (TransactionCount > 870)
                            {
                                throw new InvalidDataException("TransactionCount bigger than 870.");
                            }

                            for (ulong i = 0; i < TransactionCount; i++)
                            {

                                Block.Transaction t = new Block.Transaction();
                                if(TransactionCount > 1) 
                                {
                                    Console.WriteLine("TransactionCount > 1");
                                }

                                blockNumber = blockIndexInDataFile + blockNumberOffset;
                                Block.Transaction transaction = t.readTransactionBytes(StateWallets.Wallets, reader2, blockNumber);
                                
                                

                                newBlock.Transactions.Add(transaction);


                                

                            }
                        }
                        if(limit<10)
                        {
                            Console.WriteLine("sg");
                        }
                        blockIndexInDataFile++;
                        blocksInDataFile.Add(newBlock);
                        if(blocksInDataFile.Count != blockIndexInDataFile)
                        {
                            throw new Exception("bad: f(blocksInDataFile.Count != blockNumberInDataFile)");
                        }
                        //StateWallets.PrintAllWalletsOrdered();

                        //Console.WriteLine("Number blocksInDataFile:" + blocksInDataFile.Count);

                        BitcoinBlockHeader bbh =
                        new BitcoinBlockHeader
                        {
                            Bits = newBlock.header.Bits,
                            MerkleRoot = newBlock.header.GetMerkleRootAsString(),
                            Nonce = newBlock.header.Nonce,
                            PreviousBlockHash = newBlock.header.GetPrevBlockHashAsString(),
                            Timestamp = newBlock.header.Timestamp,
                            Version = (int)newBlock.header.Version
                        };

                        var genesisBlock = new BitcoinBlockHeader
                        {
                            Version = 1,
                            PreviousBlockHash = "0000000000000000000000000000000000000000000000000000000000000000",
                            MerkleRoot =        "4a5e1e4baab89f3a32518a88c31bc87f618f76673e2cc77ab2127b7afdeda33b",
                            Timestamp = 1231006505, // 2009-01-03 18:15:05 UTC
                            Bits = 0x1d00ffff,
                            Nonce = 2083236893
                        };

                        if(bbh.Bits != genesisBlock.Bits)
                        {
                            Console.WriteLine("dsf BAD");
                        }

                        // Expected Genesis Block hash: 000000000019d6689c085ae165831e934ff763ae46a2a6c172b3f1b60a8ce26f

                        // test claude
                        string bh = BlockReader.GetBlockHash(bbh);

                        //Transactions have merkle roots
                        //BitcoinMerkleRootCalculatorClaude.CalculateMerkleRoot(new List<string> { "4a5e1e4baab89f3a32518a88c31bc87f618f76673e2cc77ab2127b7afdeda33b" });
                        //4a5e1e4baab89f3a32518a88c31bc87f618f76673e2cc77ab2127b7afdeda33b correct for first block single tansaction

                        // end test claude

                    }




                    if (blockNumber > 94)
                    {
                        Console.WriteLine("block 95 and larger");

                    }
                    if((int)BlockSize > 230) // first block is big due to message
                    {
                        Console.WriteLine("expect block 0 or 170 or 180 blockumber " + blockNumber);
                    }
                    Console.WriteLine(blockNumber + " totalBytes: " + totalBytes + "block size (-8 to match) : " + ((int)BlockSize) + " now " + currentBytesRead + " diff " + (totalBytes - currentBytesRead).ToString());

                    if (ms.Position == ms.Length)
                    {
                        exactBytesAccountedForNoExtra = true;
                        Console.WriteLine("Finished reading block!");
                    }
                }

            }

            if (limit > 0)
            {
                if (!exactBytesAccountedForNoExtra)
                {
                    throw new Exception("error exactBytesAccountedForNoExtra is flase");
                }
            }

            return blockIndexInDataFile + blockNumberOffset;
            //return data;
        }


        public static string GetBlockHash(BitcoinBlockHeader header)
        {
             return BitcoinBlockHashCalculator.CalculateBlockHash(header);
            //return string.Equals(calculatedHash, expectedHash, StringComparison.OrdinalIgnoreCase);
        }
    }

    // from claude BELOW
    public class BitcoinBlockHashCalculator
    {
        public static void Main()
        {
            // Example: Calculate hash for Bitcoin Genesis Block
            var genesisBlock = new BitcoinBlockHeader
            {
                Version = 1,
                PreviousBlockHash = "0000000000000000000000000000000000000000000000000000000000000000",
                MerkleRoot = "4a5e1e4baab89f3a32518a88c31bc87f618f76673e2cc77ab2127b7afdeda33b",
                Timestamp = 1231006505, // 2009-01-03 18:15:05 UTC
                Bits = 0x1d00ffff,
                Nonce = 2083236893
            };

            string blockHash = CalculateBlockHash(genesisBlock);
            Console.WriteLine($"Block Hash: {blockHash}");

            // Expected Genesis Block hash: 000000000019d6689c085ae165831e934ff763ae46a2a6c172b3f1b60a8ce26f
        }

        public static string CalculateBlockHash(BitcoinBlockHeader blockHeader)
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

        private static byte[] SerializeBlockHeader(BitcoinBlockHeader header)
        {
            var bytes = new byte[80]; // Bitcoin block header is always 80 bytes
            int offset = 0;

            // Version (4 bytes, little-endian)
            WriteUInt32LE(bytes, offset, (uint)header.Version);
            offset += 4;

            // Previous block hash (32 bytes, little-endian)
            byte[] prevHashBytes = HexStringToByteArray(header.PreviousBlockHash);
            Array.Reverse(prevHashBytes); // Convert to little-endian
            Array.Copy(prevHashBytes, 0, bytes, offset, 32);
            offset += 32;

            // Merkle root (32 bytes, little-endian)
            byte[] merkleBytes = HexStringToByteArray(header.MerkleRoot);
            Array.Reverse(merkleBytes); // Convert to little-endian
            Array.Copy(merkleBytes, 0, bytes, offset, 32);
            offset += 32;

            // Timestamp (4 bytes, little-endian)
            WriteUInt32LE(bytes, offset, (uint)header.Timestamp);
            offset += 4;

            // Bits (4 bytes, little-endian)
            WriteUInt32LE(bytes, offset, header.Bits);
            offset += 4;

            // Nonce (4 bytes, little-endian)
            WriteUInt32LE(bytes, offset, header.Nonce);

            return bytes;
        }

        private static void WriteUInt32LE(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static byte[] HexStringToByteArray(string hex)
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

    public class BitcoinBlockHeader
    {
        public int Version { get; set; }
        public string PreviousBlockHash { get; set; }
        public string MerkleRoot { get; set; }
        public long Timestamp { get; set; }
        public uint Bits { get; set; }
        public uint Nonce { get; set; }
    }


}