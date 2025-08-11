
using System;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using static SatoshiSharpLib.Block;



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
            string prevHash = "";

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

                        /*BitcoinBlockHeader bbh =
                        new BitcoinBlockHeader
                        {
                            Bits = newBlock.header.Bits,
                            MerkleRoot = newBlock.header.GetMerkleRootAsString(),
                            Nonce = newBlock.header.Nonce,
                            PreviousBlockHash = newBlock.header.GetPrevBlockHashAsString(),
                            Timestamp = newBlock.header.Timestamp,
                            Version = (int)newBlock.header.Version
                        };*/

                        /*var genesisBlock = new BitcoinBlockHeader
                        {
                            Version = 1,
                            PreviousBlockHash = "0000000000000000000000000000000000000000000000000000000000000000",
                            MerkleRoot =        "4a5e1e4baab89f3a32518a88c31bc87f618f76673e2cc77ab2127b7afdeda33b",
                            Timestamp = 1231006505, // 2009-01-03 18:15:05 UTC
                            Bits = 0x1d00ffff,
                            Nonce = 2083236893
                        };*/

                        //if(bbh.Bits != genesisBlock.Bits)
                        {
                            //Console.WriteLine("dsf BAD");
                        }

                        // Expected Genesis Block hash: 000000000019d6689c085ae165831e934ff763ae46a2a6c172b3f1b60a8ce26f

                        if (prevHash != "")
                        {
                            bool prevHashExpected = newBlock.header.PrevBlockHash.Length == Helpers.HexToBytes(Helpers.ReverseHexString(prevHash)).Length &&
                                newBlock.header.PrevBlockHash.Zip(Helpers.HexToBytes(Helpers.ReverseHexString(prevHash)), (a, b) => a == b).All(x => x);
                            if (!prevHashExpected)
                            {
                                throw new Exception("error prev hash");
                            }
                        }
                        prevHash = Block.Header.CalculateBlockHash(newBlock.header);

                        // test claude
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
        }
    }

}