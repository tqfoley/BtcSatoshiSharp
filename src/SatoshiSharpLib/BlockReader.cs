
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
        public byte[] MagicBytes { 
            get; 
            private set; } // 4 bytes
        public uint BlockSize { 
            get; 
            private set; } // 4 bytes

        public List<Block> blocksInDataFile = new List<Block>();

        public int ReadBlkDataFile(string FilePath, byte[] key, Block prevBlock, int? limit=null)
        {
            if (key.Length != 8)
            {
                throw new ArgumentException("Key must be exactly 8 bytes long.");
            }

            int totalBytes = 0;
            int currentTotalBytesDebug = 0;

            byte[] dataSpecificToMyKey = null;
            try
            {
                dataSpecificToMyKey = File.ReadAllBytes(FilePath);
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

            if (limit == null)
            {
                limit = int.MaxValue;
            }
            int lastFilePosition = 0;
            int countNumberTimesLoopingBelowDEBUG = 0;

            int totalNumberBlocksReadIncludingInvalid = 0;
            using (MemoryStream ms = new MemoryStream(data))
            using (BinaryReader reader = new BinaryReader(ms))
            {
                // read entire block data file about 100 megabytes
                while (ms.Position < ms.Length && limit-- > 0)
                {
                    countNumberTimesLoopingBelowDEBUG++;
                    MagicBytes = reader.ReadBytes(4);

                    byte[] expectedMagic = new byte[] { 0xF9, 0xBE, 0xB4, 0xD9 };

                    for (int i = 0; i < 4; i++)
                    {
                        if (MagicBytes[i] != expectedMagic[i])
                        {
                            throw new InvalidDataException("Invalid magic bytes: not a valid Bitcoin block.");
                        }
                    }

                    //Helpers.PrintHexData(reader, 4);
                    BlockSize = reader.ReadUInt32();
                    currentBytesRead += (int)BlockSize + 4 + 4; // 4 bytes for magic number and 4 bytes for block size 
                    currentTotalBytesDebug += currentBytesRead;

                    if (BlockSize != 215 && BlockSize != 216)
                    {
                        Console.WriteLine("not 215 bytes instead " + BlockSize);
                    }
                    //Helpers.PrintHexData(reader, (int)BlockSize + 8);

                    if (BlockSize > 266222)
                    {
                        throw new InvalidDataException("block size bigger than 266222.");
                    }

                    Console.WriteLine("lastFilePosition " + lastFilePosition);
                    lastFilePosition += currentBytesRead;
                    using (MemoryStream ms2 = new MemoryStream(reader.ReadBytes((int)BlockSize)))
                    using (BinaryReader reader2 = new BinaryReader(ms2))
                    {
                        totalNumberBlocksReadIncludingInvalid++;

                        List<byte[]> transationsListAsBytes = new List<byte[]>();

                        Block newCurrentBlock = new Block();
                        {
                            newCurrentBlock.header = Block.Header.Parse(reader2.ReadBytes(80));
                            
                            newCurrentBlock.header.TransactionCount = Helpers.ReadVarInt(reader2);

                            if (prevBlock == null)
                            {
                                newCurrentBlock.header.BlockNumber = 0;
                            }
                            else
                            {
                                newCurrentBlock.header.BlockNumber = prevBlock.header.BlockNumber + 1;
                            }

                            if (newCurrentBlock.header.TransactionCount > 870)
                            {
                                throw new InvalidDataException("TransactionCount bigger than 870.");
                            }

                            newCurrentBlock.header.Hash = Block.Header.CalculateBlockHash(newCurrentBlock.header);
                            if (prevBlock != null)
                            {
                                if (Block.Header.CalculateBlockHash(prevBlock.header) != prevBlock.header.Hash)
                                {
                                    Console.WriteLine("hash prev bad mismatch invalid");
                                    //throw new Exception("bad");
                                }
                                //if (Block.Header.CalculateBlockHash(newCurrentBlock.header.PrevBlock.header) != newCurrentBlock.header.PrevBlock.header.Hash)
                                {
                                    //throw new Exception("bad");
                                }
                                if (newCurrentBlock.header.PrevBlockHash.ToString() == prevBlock.header.Hash)
                                {
                                    newCurrentBlock.header.PrevBlock = prevBlock; // assume its valid but if not set prev block back to null
                                }
                            }

                            // read transactions
                            for (ulong i = 0; i < newCurrentBlock.header.TransactionCount; i++)
                            {

                                Transaction t = new Transaction();
                                if(newCurrentBlock.header.TransactionCount > 1) 
                                {
                                    Console.WriteLine("TransactionCount > 1");
                                }
                                if (newCurrentBlock.header.TransactionCount > 2)
                                {
                                    Console.WriteLine("TransactionCount > 2");
                                }

                                Transaction transaction = t.readTransactionBytes(StateWallets.Wallets, reader2, newCurrentBlock.header.BlockNumber);

                                newCurrentBlock.Transactions.Add(transaction);
                            }


                        }

                        int blockNumberFORDEBUGGING = newCurrentBlock.header.BlockNumber;
                        // CALC MERK ROOT

                        //string block1 = "010000006fe28c0ab6f1b372c1a6a246ae63f74f931e8365e15a089c68d6190000000000982051fd1e4ba744bbbe680e1fee14677ba1a3c3540bf7b1cdb606e857233e0e61bc6649ffff001d01e362990101000000010000000000000000000000000000000000000000000000000000000000000000ffffffff0704ffff001d0104ffffffff0100f2052a0100000043410496b538e853519c726a2c91e61ec11600ae1390813a627c66fb8be7947be63c52da7589379515d4e0a604f8141781e62294721166bf621e73a82cbf2342c858eeac00000000";
                        //string block1Trans = block1.Substring(80);
                        //transationsListAsBytes.Add(Helpers.HexToBytes(block1Trans));
                        // Convert the hex string to bytes

                        //byte[] transBytes = newCurrentBlock.Transactions[0].SerializeTransaction(); delete todo to do
                        //string transHex = Helpers.ByteArrayToHexString(transBytes);
                        //int lengthTrans = transHex.Length; ;
                        //byte[] transactionBytes = Block.HexToBytes(transHex);

                        var transactions = new List<byte[]>();
                        foreach(var t in newCurrentBlock.Transactions)
                        {
                            transactions.Add(t.SerializeTransaction());
                        }

                        // Calculate the Merkle root
                        Block.ThirtyTwoByteClass merkleRoot = Block.CalculateMerkleRoot(transactions);

                        //block 0 4a5e1e4baab89f3a32518a88c31bc87f618f76673e2cc77ab2127b7afdeda33b
                        //Block 1 0e3e2357e806b6cdb1f70b54c3a3a17b6714ee1f0e68bebb44a74b1efd512098
                        //Console.WriteLine("Calculated Merkle Root: " + Helpers.GetStringReverseHexBytes(merkleRoot));


                        string expectedMerkleRootOfBothTrans = "7dac2c5666815c17a3b36427de37bb9d2e2c5ccec3f8633eb91a4205cb4c10ff";
                        if (Helpers.GetStringReverseHexBytes(merkleRoot).ToLower() ==  expectedMerkleRootOfBothTrans)
                        {
                            Console.WriteLine("sdf");
                        }

                        string expectedMerkle193 =
                        "10470e2c3c443863ea2e84684f7f6021539f518d3246ad97125d348ea1a75964";
                        if (Helpers.GetStringReverseHexBytes(merkleRoot).ToLower() == expectedMerkle193)
                        {
                            Console.WriteLine("expectedMerkle193");
                            Console.WriteLine("totalNumberBlocksReadIncludingInvalid " + totalNumberBlocksReadIncludingInvalid);
                        }

                        if (Helpers.GetStringReverseHexBytes(merkleRoot).ToLower() != newCurrentBlock.header.GetMerkleRootAsString().ToLower())
                        {
                            byte[] trans2Bytes = newCurrentBlock.Transactions[1].SerializeTransaction();
                            string trans2Hex = Helpers.ByteArrayToHexString(trans2Bytes);
                            throw new Exception("merkle root in header issue");
                        }
                        //10470e2c3c443863ea2e84684f7f6021539f518d3246ad97125d348ea1a75964
                        // End CALC MERK ROOT

                        if (limit<10)
                        {
                            Console.WriteLine("sg");
                        }
                        blocksInDataFile.Add(newCurrentBlock);
                        //StateWallets.PrintAllWalletsOrdered();

                        //Console.WriteLine("Number blocksInDataFile:" + blocksInDataFile.Count);

                        /*BitcoinBlockHeader bbh =
                        new BitcoinBlockHeader
                        {
                            Bits = newBlock.header.Bits,
                            MerkleRoot = newBlock.header.GetMerkleRootAsString(),
                            Nonce = newBlock.header.Nonce,
                            PrevBlockHash = newBlock.header.GetPrevBlockHashAsString(),
                            Timestamp = newBlock.header.Timestamp,
                            Version = (int)newBlock.header.Version
                        };*/

                        /*var genesisBlock = new BitcoinBlockHeader
                        {
                            Version = 1,
                            PrevBlockHash = "0000000000000000000000000000000000000000000000000000000000000000",
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

                        //Console.WriteLine("    hash " + Helpers.GetStringReverseHexBytes((newCurrentBlock.header.PrevBlockHash)));
                        //Console.WriteLine("prevhash " + prevHash);

                        newCurrentBlock.header.ConsoleWrite();
                        // 193 merkle root 10470e2c3c443863ea2e84684f7f6021539f518d3246ad97125d348ea1a75964
                        //192 16293da6d4078f636b691448b57f96d5af32d7ca3fb15a20cc74845b224a44bd
                        string test = newCurrentBlock.header.MerkleRoot.ToString();
                        if (newCurrentBlock.header.BlockNumber > 191)
                        {
                            //Console.WriteLine("block 192 https://www.blockchain.com/explorer/blocks/btc/00000000520bf3614f3f3f312491bcce9ae820cfcf8393cf1e7aecb0db4932ab");

                            List<string> toFile = new List<string>();
                            int c = 0;
                            Block curr = newCurrentBlock;
                            while(curr != null)
                            {
                                toFile.Add(c++.ToString("0000.0") + "  " + Block.Header.CalculateBlockHash(curr.header));

                                toFile.Add("t       " + (DateTimeOffset.FromUnixTimeSeconds(curr.header.Timestamp).UtcDateTime));

                                toFile.Add("p       " + curr.header.PrevBlockHash);

                                curr = curr.header.PrevBlock;

                            }
                            File.WriteAllLines("C:\\btcblock\\mostblocks11_zeroxor\\hashes.txt", toFile);
                        }

                        if (prevBlock != null)
                        {
                            if (Block.Header.CalculateBlockHash(prevBlock.header) != newCurrentBlock.header.PrevBlockHash.ToString())
                            {

                                Console.WriteLine("    hash " + newCurrentBlock.header.PrevBlockHash.ToString());// Helpers.GetStringReverseHexBytes((newCurrentBlock.header.PrevBlockHash)));
                                Console.WriteLine("prevhash " + Block.Header.CalculateBlockHash(prevBlock.header));
                                throw new Exception("error prev hash");
                                
                                // some block invalid
                                newCurrentBlock.header.Valid = false;
                                Console.WriteLine("totalNumberBlocksReadIncludingInvalid " + totalNumberBlocksReadIncludingInvalid);
                            }
                            else
                            {
                                newCurrentBlock.header.Valid = true;
                                prevBlock = newCurrentBlock;
                                Console.WriteLine("totalNumberBlocksReadIncludingInvalid " + totalNumberBlocksReadIncludingInvalid);
                            }
                        }
                        else
                        {
                            // make the first block work
                            newCurrentBlock.header.Valid = true;
                            prevBlock = newCurrentBlock;
                        }

                        //Console.WriteLine("hash: " + prevHash.Substring(prevHash.Length - 6));
                    }// end reading block, Last thing increement block index

                    if ((int)BlockSize > 1000)
                    {
                        Console.WriteLine("large block size!");
                        Console.WriteLine("totalNumberBlocksReadIncludingInvalid " + totalNumberBlocksReadIncludingInvalid);
                    }
                    //Console.WriteLine(blockNumber + " totalBytes: " + totalBytes + "block size (-8 to match) : " + ((int)BlockSize) + " now " + currentBytesRead + " diff " + (totalBytes - currentBytesRead).ToString());

                    if (ms.Position == ms.Length)
                    {
                        exactBytesAccountedForNoExtra = true;
                        Console.WriteLine("Finished reading block!"); // this is for the entire ~100 meg file
                    }


                } // end while satement


            }// end reafing block data file

            if (!exactBytesAccountedForNoExtra && limit == null)
            {
                Console.WriteLine("unexpected block file size");
                throw new Exception("unexpected block file size");
            }
            if (limit > 0)
            {
                if (!exactBytesAccountedForNoExtra)
                {
                    throw new Exception("error exactBytesAccountedForNoExtra is flase");
                }
            }

            return 0;
        }
    }

}