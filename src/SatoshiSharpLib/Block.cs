
namespace SatoshiSharpLib
{
    public class Block
    {
        int blockNumber = -1;

        public Header header { get; set; } = new Header();

        public List<Transaction> Transactions { get; set; } = new List<Transaction>();

        public class Header
        {
            public uint Version { get; set; }
            public byte[] PrevBlock { get; set; }   // 32 bytes
            public byte[] MerkleRoot { get; set; }  // 32 bytes
            public uint Timestamp { get; set; }
            public uint Bits { get; set; }
            public uint Nonce { get; set; }
            public ulong TransactionCount { get; set; }

            public override string ToString()
            {
                string hexBits = Bits.ToString("X8");
                return $"Version: {Version}\n" +
                       $"Previous Block: {BitConverter.ToString(PrevBlock).Replace("-", "")}\n" +
                       $"Merkle Root: {BitConverter.ToString(MerkleRoot).Replace("-", "")}\n" +
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
                        PrevBlock = reader.ReadBytes(32),      // 32 bytes
                        MerkleRoot = reader.ReadBytes(32),     // 32 bytes
                        Timestamp = reader.ReadUInt32(),       // 4 bytes
                        Bits = reader.ReadUInt32(),            // 4 bytes
                        Nonce = reader.ReadUInt32(),            // 4 bytes
                    };

                    Console.WriteLine(header);
                    return header;
                }
            }

            private static ulong ReadVarInt(BinaryReader reader)
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
        }

        public class Transaction
        {
            public class TxInput
            {
                public byte[] TxId { get; set; } // 32 bytes, little-endian
                public uint Vout { get; set; }
                public byte[] ScriptSig { get; set; }
                public uint Sequence { get; set; }

                public override string ToString()
                {

                    string hexBits = Sequence.ToString("X8");

                    return $"TxID: {BitConverter.ToString(Reverse(TxId)).Replace("-", "")}\n" +
                           $"Vout: {Vout}\n" +
                           $"ScriptSig: {BitConverter.ToString(ScriptSig).Replace("-", "")}\n" +
                           $"Sequence: {Sequence}\n" +
                           $"Sequence: {hexBits}";
                }

                private static byte[] Reverse(byte[] data)
                {
                    var arr = (byte[])data.Clone();
                    Array.Reverse(arr);
                    return arr;
                }

            }

            public class TxOutput
            {
                public ulong Value { get; set; }
                public byte[] ScriptPubKey { get; set; }

                public override string ToString()
                {
                    return $"Value: {Value} sats\n" +
                           $"ScriptPubKey: {BitConverter.ToString(ScriptPubKey).Replace("-", "")}";
                }
            }

            public uint Version { get; set; }
            public List<TxInput> Inputs { get; set; } = new List<TxInput>();
            public List<TxOutput> Outputs { get; set; } = new List<TxOutput>();
            public uint LockTime { get; set; }

            public override string ToString()
            {
                string result = $"Version: {Version}\nInputs: {Inputs.Count}\n";
                for (int i = 0; i < Inputs.Count; i++)
                {
                    result += $"\nInput #{i}:\n{Inputs[i]}\n";
                }

                result += $"\nOutputs: {Outputs.Count}\n";
                for (int i = 0; i < Outputs.Count; i++)
                {
                    result += $"\nOutput #{i}:\n{Outputs[i]}\n";
                }

                result += $"\nLockTime: {LockTime}";
                return result;
            }

            /*
             * GetHash()      = 0x000000000019d6689c085ae165831e934ff763ae46a2a6c172b3f1b60a8ce26f
hashMerkleRoot = 0x4a5e1e4baab89f3a32518a88c31bc87f618f76673e2cc77ab2127b7afdeda33b
txNew.vin[0].scriptSig     = 486604799 4 0x736B6E616220726F662074756F6C69616220646E6F63657320666F206B6E697262206E6F20726F6C6C65636E61684320393030322F6E614A2F33302073656D695420656854
txNew.vout[0].nValue       = 5000000000
txNew.vout[0].scriptPubKey = 0x5F1DF16B2B704C8A578D0BBAF74D385CDE12C11EE50455F3C438EF4C3FBCF649B6DE611FEAE06279A60939E028A8D65C10B73071A6F16719274855FEB0FD8A6704 OP_CHECKSIG
block.nVersion = 1
block.nTime    = 1231006505
block.nBits    = 0x1d00ffff
block.nNonce   = 2083236893

CBlock(hash=000000000019d6, ver=1, hashPrevBlock=00000000000000, hashMerkleRoot=4a5e1e, nTime=1231006505, nBits=1d00ffff, nNonce=2083236893, vtx=1)
CTransaction(hash=4a5e1e, ver=1, vin.size=1, vout.size=1, nLockTime=0)
CTxIn(COutPoint(000000, -1), coinbase 04ffff001d0104455468652054696d65732030332f4a616e2f32303039204368616e63656c6c6f72206f6e206272696e6b206f66207365636f6e64206261696c6f757420666f722062616e6b73)
CTxOut(nValue=50.00000000, scriptPubKey=0x5F1DF16B2B704C8A578D0B)
vMerkleTree: 4a5e1e

             */
            
            public Transaction readTransactionBytes(List<Wallet> wallets, BinaryReader reader, bool printDebug = false)
            {
                //using (MemoryStream ms = new MemoryStream(txBytes))
                //using (BinaryReader reader = new BinaryReader(ms))
                {
                    Transaction tx = new Transaction();
                    tx.Version = reader.ReadUInt32();

                    ulong inputCount = Helpers.ReadVarInt(reader);
                    for (ulong i = 0; i < inputCount; i++)
                    {
                        TxInput input = new TxInput
                        {
                            TxId = reader.ReadBytes(32),
                            Vout = reader.ReadUInt32()
                        };

                        ulong scriptLength = Helpers.ReadVarInt(reader);
                        input.ScriptSig = reader.ReadBytes((int)scriptLength);
                        input.Sequence = reader.ReadUInt32();

                        tx.Inputs.Add(input);
                    }

                    ulong outputCount = Helpers.ReadVarInt(reader);
                    for (ulong i = 0; i < outputCount; i++)
                    {
                        TxOutput output = new TxOutput
                        {
                            Value = reader.ReadUInt64()
                        };

                        ulong scriptLength = Helpers.ReadVarInt(reader);
                        output.ScriptPubKey = reader.ReadBytes((int)scriptLength);

                        Helpers.readSignedSpend(0, output.ScriptPubKey, 50, wallets);

                        tx.Outputs.Add(output);
                    }

                    if (printDebug)
                    {
                        Console.WriteLine(tx);
                    }

                    tx.LockTime = reader.ReadUInt32();

                    return tx;
                }
            }
        }
    }
}