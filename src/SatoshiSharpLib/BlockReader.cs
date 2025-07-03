
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

        public byte[] ReadBlkDataFile(string FilePathSpecificToMyKey, byte[] key, int? limit=null)
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

                                Block.Transaction transaction = t.readTransactionBytes(StateWallets.Wallets, reader2);

                                

                                newBlock.Transactions.Add(transaction);


                                

                            }
                        }
                        blocksInDataFile.Add(newBlock);

                        //Console.WriteLine("Number blocksInDataFile:" + blocksInDataFile.Count);
                    }


                    Console.WriteLine("totalBytes: " + totalBytes + " now " + currentBytesRead + " diff " + (totalBytes - currentBytesRead).ToString());

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

            return data;
        }
    }
}