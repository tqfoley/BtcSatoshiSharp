using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Security.Cryptography.X509Certificates;

namespace main4
{
    /// <summary>
    /// Pulls one block out of a blk#####.dat file and prints its raw serialized bytes as hex.
    ///
    /// There are two ways to name the block you want, and both walk the same records:
    ///
    ///     [4 bytes magic F9BEB4D9][4 bytes block size, little endian][block bytes]
    ///
    ///   by hash     - give a hash in the order explorers display it (e.g.
    ///                 000000006a625f06636b8bb6ac7b960a8d03705d1ace08b1a19da3fdcc99ddbd) plus the
    ///                 blk file index. Every 80-byte header in that file is hashed until one
    ///                 matches.
    ///   by position - give the blk file index plus the 0-based position of the block inside it,
    ///                 so `0 4` is the fifth record in blk00000.dat. Nothing is hashed while
    ///                 skipping; the records are just counted.
    ///
    /// Either way only the 8-byte record headers (and, hashing aside, the 80-byte block headers)
    /// are read while scanning - block bodies are seeked over - so a 128 MiB file is walked in a
    /// fraction of a second.
    ///
    /// Position is *not* height. These files hold blocks in the order they were written, which
    /// for MainBlockDownload is the order they arrived off the wire, not chain order.
    ///
    /// If xor.dat is present in the directory its key is applied (Bitcoin Core obfuscates
    /// blk*.dat with an 8-byte key indexed by absolute file offset). An all-zero key, which is
    /// what MainBlockDownload writes, means the files are stored plain.
    ///
    /// Usage:
    ///     dotnet run --project src/ConsoleApp -- 000000006a625f06636b8bb6ac7b960a8d03705d1ace08b1a19da3fdcc99ddbd 0
    ///     dotnet run --project src/ConsoleApp -- 0 4
    ///     dotnet run --project src/ConsoleApp -- &lt;hash&gt; 12 --dir C:\btcblock\claudeblocks1
    ///     dotnet run --project src/ConsoleApp -- 12 500 --out block.hex
    ///
    /// Running with no arguments at all uses the debug defaults below, which is what F5 in Visual
    /// Studio gives you.
    ///
    /// The hex goes to stdout and everything else to stderr, so `... &gt; block.hex` gives a clean
    /// file. This is the project's entry point - to run one of the other experiments instead,
    /// rename this method to Main4 and rename that file's Main2/Main3 back to Main.
    /// </summary>
    public class MainBlockHex
    {
        // Handy early blocks to breakpoint on.
        const string Block2Hash = "000000006a625f06636b8bb6ac7b960a8d03705d1ace08b1a19da3fdcc99ddbd";
        const string Block3Hash = "0000000082b5015589a3fdf2d4baff403e6f0be035a5d9742c1cae6295464449";

        // What a no-argument run looks for. Passing any argument ignores all three.
        const string DebugHash = Block3Hash;
        const int DebugFileIndex = 0;
        const int DebugBlockIndex = -1;       // set >= 0 to look up by position instead of by hash

        static readonly byte[] Magic = { 0xF9, 0xBE, 0xB4, 0xD9 };

        /// <summary>Records claiming more than this are treated as garbage, not blocks.</summary>
        const int MaxBlockBytes = 8 * 1024 * 1024;

        const int IndexRecordBytes = 48;      // hash[32] fileNo[4] offset[8] size[4] - see BlockStore

        /// <summary>What genesis carries where a parent hash would go: 32 zero bytes.</summary>
        const string NoParentHash = "0000000000000000000000000000000000000000000000000000000000000000";


        /// <summary>
        /// Empties a directory of files, creating it if it is not there. Subdirectories are left
        /// alone. Returns how many files were deleted.
        /// </summary>
        static int DeleteAllFilesIn(string directory)
        {
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                return 0;
            }

            int deleted = 0;
            foreach (string file in Directory.GetFiles(directory))
            {
                File.Delete(file);
                deleted++;
            }
            return deleted;
        }



        // ------------------------------------------------------------------------------------
        // Entry point
        // ------------------------------------------------------------------------------------

        /// <summary>What to look for and where - filled in from the command line.</summary>
        sealed class Request
        {
            public int FileIndex = -1;

            /// <summary>Display-order hash in by-hash mode, empty in by-position mode.</summary>
            public string Hash = "";

            /// <summary>0-based position in the file in by-position mode, -1 in by-hash mode.</summary>
            public int BlockIndex = -1;

            public string? OutPath;

            /// <summary>blk-format file to append the block to, null when it is not wanted.</summary>
            //public string? AppendPath;

            /// <summary>Hash is a plain string, so "no hash given" is the empty one, not null.</summary>
            public bool ByHash => Hash.Length > 0;
        }

        static int FindIn100ArrayOfBlockRaw(BlockRaw[] array, int count, string hash)
        {
            int index = 0;
            
            foreach (var raw in array)
            {
                if (raw != null && raw.DisplayHash == hash)
                {
                    return index;
                }

                index++;

                if(index >= count)
                {
                    break;
                }
            }

            return -1;
        }


        static int Main(string[] args)
        {
            var req = new Request();

            try
            {
                if (!ParseArgs(args, req))
                {
                    PrintUsage();      // --help
                    return 0;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("argument error: " + ex.Message);
                PrintUsage();
                return 2;
            }

            try
            {
                Request reqBlock3ByHash = new Request();
                reqBlock3ByHash.FileIndex = 0;
                reqBlock3ByHash.Hash = Block3Hash;

                Request reqBlock3ByIndex = new Request();
                reqBlock3ByIndex.FileIndex = 0;
                reqBlock3ByIndex.BlockIndex = 3;


                int scanned;
                int scannedByHash;
                int scannedByIndex;
                BlockRaw? foundByHash = FindBlockByHash(@"C:\btcblock\claudeblocks1", reqBlock3ByHash.FileIndex, reqBlock3ByHash.Hash, out scannedByHash);
                BlockRaw? foundByIndex = FindBlockByPosition(@"C:\btcblock\claudeblocks1", reqBlock3ByIndex.FileIndex, reqBlock3ByIndex.BlockIndex, out scannedByIndex);


                if (foundByHash != foundByIndex)
                {
                    Console.WriteLine("error: foundByHash and foundByIndex do not match");
                }

                if (foundByHash == null || foundByIndex == null)
                {
                    Console.WriteLine("error: block 3 was not found both ways, nothing to summarize");
                }
                else
                {
                    PrintSummary(foundByHash, scannedByHash);
                    PrintSummary(foundByIndex, scannedByIndex);
                }

                DeleteAllFilesIn("C:\\btcblock\\inOrder\\");


                // https://api.blockchair.com/bitcoin/raw/block/33
                string jsonBlock33 = "{\"data\":{\"33\":{\"raw_block\":\"01000000e3f6664d5af37062b934f983ed1033e2011b42c9b04735276c7ccbe5000000001012aaab3e3bffd34055aaa157bf78792d5c18f085635eda7046d89c08a0eabde3c86849ffff001d228c22400101000000010000000000000000000000000000000000000000000000000000000000000000ffffffff0704ffff001d0138ffffffff0100f2052a01000000434104804d71f6a91c908a973cae7ef4363f7689520116b995d6936328de00be56f92baee0dabf3a240e0ed2dce7f374f12cbba7649808528236cb04c558f028dd61edac00000000\",\"decoded_raw_block\":{\"hash\":\"00000000a87073ea3d7af299e02a434598b9c92094afa552e0711afcc0857962\",\"confirmations\":960926,\"height\":33,\"version\":1,\"versionHex\":\"00000001\",\"merkleroot\":\"bdeaa0089cd84670da5e6385f0185c2d7978bf57a1aa5540d3ff3b3eabaa1210\",\"time\":1231603939,\"mediantime\":1231601457,\"nonce\":1076005922,\"bits\":\"1d00ffff\",\"difficulty\":1,\"chainwork\":\"0000000000000000000000000000000000000000000000000000002200220022\",\"nTx\":1,\"previousblockhash\":\"00000000e5cb7c6c273547b0c9421b01e23310ed83f934b96270f35a4d66f6e3\",\"nextblockhash\":\"00000000a73fb23b6c42b18b3253ed29c5d0c80d84624efa12c2cf05c4b4318f\",\"strippedsize\":215,\"size\":215,\"weight\":860,\"tx\":[\"bdeaa0089cd84670da5e6385f0185c2d7978bf57a1aa5540d3ff3b3eabaa1210\"]}}},\"context\":{\"code\":200,\"source\":\"T+R\",\"results\":1,\"state\":960939,\"market_price_usd\":63703,\"cache\":{\"live\":true,\"duration\":120,\"since\":\"2026-08-04 03:37:36\",\"until\":\"2026-08-04 03:39:36\",\"time\":null},\"api\":{\"version\":\"2.0.95-ie\",\"last_major_update\":\"2022-11-07 02:00:00\",\"next_major_update\":\"2023-11-12 02:00:00\",\"documentation\":\"https:\\/\\/blockchair.com\\/api\\/docs\",\"notice\":\"Try out our new API v.3: https:\\/\\/3xpl.com\\/data\"},\"servers\":\"API4,BTC5,BTC5,BTC5\",\"time\":0.006392955780029297,\"render_time\":0.0043070316314697266,\"full_time\":0.010699987411499023,\"request_cost\":1}}";
                List<BlockRaw> g =  ReadBlocksFromJson(jsonBlock33);



                // 30 00000000bc919cfb64f62de736d55cf79e3d535b474ace256b4fbb56073f64db
                // 31 000000009700ff3494f215c412cd8c0ceabf1deb0df03ce39bcfc223b769d3c4
                // 32 00000000e5cb7c6c273547b0c9421b01e23310ed83f934b96270f35a4d66f6e3
                // 33 00000000a87073ea3d7af299e02a434598b9c92094afa552e0711afcc0857962
                // 34 00000000a73fb23b6c42b18b3253ed29c5d0c80d84624efa12c2cf05c4b4318f

                int currentIndex = 0;
                while (currentIndex < 550)
                {

                    foundByIndex = FindBlockByPosition(@"C:\btcblock\mostblocks11_zeroxor", reqBlock3ByIndex.FileIndex, currentIndex, out scannedByIndex);
                    if (foundByIndex!.DisplayHash == "00000000e5cb7c6c273547b0c9421b01e23310ed83f934b96270f35a4d66f6e3" ||
                        foundByIndex!.DisplayHash == "00000000a87073ea3d7af299e02a434598b9c92094afa552e0711afcc0857962" ||
                        foundByIndex!.DisplayHash == "00000000a73fb23b6c42b18b3253ed29c5d0c80d84624efa12c2cf05c4b4318f")
                    {
                        Console.WriteLine(currentIndex + "***  " + foundByIndex.GetPrevBlockHash() + " hash " + foundByIndex.DisplayHash);

                    }
                    else
                    {
                        //Console.WriteLine(currentIndex + " " + foundByIndex.GetPrevBlockHash() + " hash " + foundByIndex.DisplayHash);
                    }
                    currentIndex++;

                }



                string blockDataDirectory = "C:\\btcblock\\mostblocks11_zeroxor";


                BlockRaw? prevFoundByIndex = null;
                currentIndex = 0;
                int written = 0;


                // Blocks arrive in file order, which is arrival order - a block's parent is usually
                // somewhere else entirely. The assembler parks whatever does not connect yet,
                // tracks cumulative work per branch, and only writes a block once it is 50 deep
                // behind the heaviest tip, at which point a competing branch would need 50 blocks
                // of its own to take it back.
                var assembler = new ChainAssembler("C:\\btcblock\\claudeblocks1\\blk00000.dat",
                                                   confirmationDepth: 50, maxPending: 1000);

                while (currentIndex < 2600)
                {
                    foundByIndex = FindBlockByPosition("C:\\btcblock\\mostblocks11_zeroxor", reqBlock3ByIndex.FileIndex, currentIndex, out scannedByIndex);
                    if (foundByIndex == null) break;      // ran off the end of the file

                    assembler.Add(foundByIndex);
                    assembler.Flush();                    // commits whatever just became 50 deep

                    currentIndex++;
                }

                // Height 33 was never downloaded, so the chain in the file stops at 32. The pasted
                // API response holds it - hand it to the assembler the same way a block read off
                // disk would be, and the parked blocks behind it connect up on their own.
                //foreach (BlockRaw imported in ReadBlocksFromJson(BlockRaw33))
                {
                  //  assembler.Add(imported);
                }
                //assembler.Flush();

                int writtenWhileStreaming = assembler.Written;

                // Nothing further can arrive to outweigh the tip, so the last 50 are safe to commit.
                assembler.FlushAll();
                written = assembler.Written;

                Console.WriteLine("read " + currentIndex + " blocks, wrote " + written
                                  + " (" + writtenWhileStreaming + " of them " + assembler.ConfirmationDepth
                                  + "-deep during the scan, "
                                  + (written - writtenWhileStreaming) + " on the final flush)");
                Console.WriteLine("  best tip    : height " + assembler.BestTipHeight + " " + assembler.BestTipHash);
                Console.WriteLine("  duplicates " + assembler.Duplicates + ", still waiting on a parent "
                                  + assembler.Pending + ", evicted " + assembler.Evicted
                                  + ", deep reorgs " + assembler.DeepReorgs);

                // The greedy pass above can only keep a block when it happens to sit right after
                // its parent in the file. This one indexes the whole file by parent hash first and
                // then follows the links, so it recovers the blocks that pass had to drop. It
                // writes straight into inOrder2, which the verification loop below reads.
                ChainOrderResult ordered = WriteChainOrdered(blockDataDirectory, reqBlock3ByIndex.FileIndex,
                                                             "C:\\btcblock\\inOrder2\\blk00000.dat", "", 1600);
                Console.WriteLine("reordered " + ordered.BlocksWritten + " of " + ordered.BlocksInFile
                                  + " blocks, " + ordered.Unreachable + " unreachable, "
                                  + ordered.ForkedParents + " forked parents");
                Console.WriteLine("  from " + ordered.StartHash);
                Console.WriteLine("  to   " + ordered.EndHash);


                int currentIndex2 = 0;
                prevFoundByIndex = null;
                while (currentIndex2 < 300)
                {

                    foundByIndex = FindBlockByPosition("C:\\btcblock\\inOrder2", 0, currentIndex2, out scannedByIndex);
                    if (foundByIndex == null) break;      // file holds fewer blocks than this

                    if (prevFoundByIndex == null)
                    {
                        // first block, nothing to compare to
                    }
                    else
                    {
                        if (prevFoundByIndex.DisplayHash != foundByIndex.GetPrevBlockHash())
                        {
                            // 21 hash 000000006f016342d1275be946166cff975c8b27542de70a7113ac6d1ef3294f
                            // 22 hash 0000000098b58d427a10c860335a21c1a9a7639e96c3d6f1a03d8c8c885b5e3b
                            // 23 hash 000000000cd339982e556dfffa9de94744a4135c53eeef15b7bcc9bdeb9c2182

                            string h = foundByIndex.GetPrevBlockHash();
                            throw new Exception("bad");
                        }
                    }
                    prevFoundByIndex = foundByIndex;
                    currentIndex2++;
                }


                    // block 500 0000000047560030cea942ff993f9c5464dd6499e7118d189c56ca57a465bcb7

                    int scannedByIndex2=2;
                BlockRaw? tqfFoundByIndex = FindBlockByPosition("C:\\btcblock\\inOrder2\\", 0, 500, out scannedByIndex2);



                BlockRaw? found;

                if (req.ByHash)
                {
                    found = FindBlockByHash(blockDataDirectory, req.FileIndex, req.Hash, out scanned);
                }
                else
                {
                    found = FindBlockByPosition(blockDataDirectory, req.FileIndex, req.BlockIndex, out scanned);
                }

                if (found == null)
                {
                    ReportMiss(req, scanned, blockDataDirectory);
                    return 1;
                }

                PrintSummary(found, scanned);

                //if (req.AppendPath != null)
                //{
                  //  long writtenAt = AppendBlockToFile(req.AppendPath, found);
                    //Console.Error.WriteLine("appended    : " + (found.Size + 8) + " bytes to " + req.AppendPath
                  //                          + " at offset " + writtenAt);
                //}

                string hex = Convert.ToHexString(found.Raw).ToLowerInvariant();
                if (req.OutPath != null)
                {
                    File.WriteAllText(req.OutPath, hex);
                    Console.Error.WriteLine("wrote " + hex.Length + " hex chars to " + req.OutPath);
                }
                else
                {
                    Console.Out.WriteLine(hex);
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                return 1;
            }
        }

        /// <summary>
        /// Returns false when the caller should just print usage and stop (--help, or nothing to
        /// go on). Throws with a message when an argument is wrong.
        /// </summary>
        static bool ParseArgs(string[] args, Request req)
        {
            if (args.Length == 0)
            {
                req.FileIndex = DebugFileIndex;
                req.BlockIndex = DebugBlockIndex;
                if (DebugBlockIndex < 0)
                {
                    req.Hash = DebugHash;      // no debug position given, so look the hash up
                }
                Console.Error.WriteLine("no arguments - using the built-in debug defaults (--help for usage)");
                return true;
            }

            var positional = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string Next(string name)
                {
                    if (++i >= args.Length) throw new ArgumentException(name + " needs a value");
                    return args[i];
                }

                switch (a)
                {
                    case "-h":
                    case "--help": return false;
                    //case "--dir": blockDataDirectory = Next("--dir"); break;
                    case "--out": req.OutPath = Next("--out"); break;
                    //case "--append": req.AppendPath = Next("--append"); break;
                    case "--file": req.FileIndex = ParseFileIndex(Next("--file")); break;
                    case "--hash": req.Hash = NormalizeHash(Next("--hash")); break;
                    case "--block": req.BlockIndex = ParseBlockIndex(Next("--block")); break;
                    default:
                        if (a.StartsWith("-")) throw new ArgumentException("unknown option " + a);
                        positional.Add(a);
                        break;
                }
            }

            // <hash> <fileIndex>   or   <fileIndex> <blockIndex>  - a 64-hex-character first
            // argument is the only thing that tells the two apart.
            if (positional.Count > 2)
                throw new ArgumentException("unexpected argument " + positional[2]);

            if (positional.Count > 0)
            {
                if (LooksLikeHash(positional[0]))
                {
                    req.Hash = NormalizeHash(positional[0]);
                    if (positional.Count > 1) req.FileIndex = ParseFileIndex(positional[1]);
                }
                else
                {
                    req.FileIndex = ParseFileIndex(positional[0]);
                    if (positional.Count > 1) req.BlockIndex = ParseBlockIndex(positional[1]);
                }
            }

            if (req.Hash.Length > 0 && req.BlockIndex >= 0)
                throw new ArgumentException("give a block hash or a block position, not both");
            if (req.Hash.Length == 0 && req.BlockIndex < 0)
                throw new ArgumentException("no block named - pass a hash, or a file index plus a block position");
            if (req.FileIndex < 0)
                throw new ArgumentException("no blk file index - which file should be searched?");

            return true;
        }

        static bool LooksLikeHash(string text)
        {
            string s = text.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            if (s.Length != 64) return false;
            foreach (char c in s)
                if (!Uri.IsHexDigit(c)) return false;
            return true;
        }

        static int ParseFileIndex(string text)
        {
            // Accept 12, 00012 or blk00012.dat - all name the same file.
            string s = text;
            if (s.StartsWith("blk", StringComparison.OrdinalIgnoreCase)) s = s.Substring(3);
            if (s.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            if (!int.TryParse(s, out int index) || index < 0)
                throw new ArgumentException("'" + text + "' is not a blk file index");
            return index;
        }

        static int ParseBlockIndex(string text)
        {
            if (!int.TryParse(text, out int index) || index < 0)
                throw new ArgumentException("'" + text + "' is not a block position (0 is the first block in the file)");
            return index;
        }

        static string NormalizeHash(string text)
        {
            string s = text.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            if (s.Length != 64)
                throw new ArgumentException("a block hash is 64 hex characters, got " + s.Length);
            foreach (char c in s)
                if (!Uri.IsHexDigit(c)) throw new ArgumentException("'" + text + "' is not hex");
            return s.ToLowerInvariant();
        }

        static void PrintUsage()
        {
            Console.Error.WriteLine(@"
Print the raw hex of one block from a blk#####.dat file. Name the block either way:

  <hash> <fileIndex>          by hash: 64 hex chars in explorer (display) order, then the
                              blk file to search - 0 means blk00000.dat
  <fileIndex> <blockIndex>    by position: the blk file, then the 0-based position of the
                              block inside it - `0 4` is the fifth record in blk00000.dat

Position is the order blocks were written to the file, which is not chain height.

  --hash <hash>               same as the positional hash
  --file <index>              same as the positional blk file index
  --block <index>             same as the positional block position
  --dir <path>                directory holding the blk files (default " +  @")
  --out <file>                write the hex here instead of stdout
  --append <file>             append the block to this blk-format file, magic bytes and size
                              field and all, creating it if it does not exist

Examples:
  dotnet run --project src/ConsoleApp -- 000000006a625f06636b8bb6ac7b960a8d03705d1ace08b1a19da3fdcc99ddbd 0
  dotnet run --project src/ConsoleApp -- 0 4
  dotnet run --project src/ConsoleApp -- 12 500 --out block.hex");
        }

        static void PrintSummary(BlockRaw b, int scanned)
        {
            Console.Error.WriteLine("file        : " + b.Path);
            Console.Error.WriteLine("position    : " + b.BlockIndex + " (0-based, write order not height)");
            Console.Error.WriteLine("record at   : " + b.Offset + " (block bytes start at " + (b.Offset + 8) + ")");
            Console.Error.WriteLine("block bytes : " + b.Size);
            Console.Error.WriteLine("hash        : " + b.DisplayHash);
            Console.Error.WriteLine("prev block  : " + b.GetPrevBlockHash());
            Console.Error.WriteLine("merkle root : " + ToDisplayHex(b.Raw.AsSpan(36, 32).ToArray()));
            Console.Error.WriteLine("version     : 0x" + BinaryPrimitives.ReadUInt32LittleEndian(b.Raw.AsSpan(0, 4)).ToString("x8"));
            Console.Error.WriteLine("time        : " + DateTimeOffset.FromUnixTimeSeconds(
                BinaryPrimitives.ReadUInt32LittleEndian(b.Raw.AsSpan(68, 4))).UtcDateTime.ToString("u"));
            Console.Error.WriteLine("bits        : 0x" + BinaryPrimitives.ReadUInt32LittleEndian(b.Raw.AsSpan(72, 4)).ToString("x8"));
            Console.Error.WriteLine("nonce       : " + BinaryPrimitives.ReadUInt32LittleEndian(b.Raw.AsSpan(76, 4)));
            int pos = 80;
            Console.Error.WriteLine("transactions: " + ReadVarInt(b.Raw, ref pos));
            Console.Error.WriteLine("scanned     : " + scanned + " blocks to find it");
        }

        static void ReportMiss(Request req, int scanned, string blockDataDirectory)
        {
            string path = BlkFilePath(blockDataDirectory, req.FileIndex);

            if (req.ByHash)
            {
                Console.Error.WriteLine("not found: " + req.Hash);
                Console.Error.WriteLine("scanned " + scanned + " blocks in " + path);
                ReportIndexHint(blockDataDirectory, req.Hash, req.FileIndex);
            }
            else
            {
                string range = "";
                if (scanned > 0)
                {
                    range = " (0 to " + (scanned - 1) + ")";
                }
                Console.Error.WriteLine("no block at position " + req.BlockIndex + " - " + path
                                        + " holds " + scanned + " blocks" + range);
            }
        }

        // ------------------------------------------------------------------------------------
        // The search
        // ------------------------------------------------------------------------------------

        public sealed class BlockRaw : IEquatable<BlockRaw>
        {
            /// <summary>Full path of the blk file the block was found in.</summary>
            public string Path = "";

            /// <summary>0-based position of the block within that file, in write order.</summary>
            public int BlockIndex;

            /// <summary>Offset of the 4 magic bytes, so the block bytes themselves start 8 later.</summary>
            public long Offset;

            /// <summary>Length of the serialized block, i.e. the size field of the record.</summary>
            public int Size;

            /// <summary>The serialized block: 80-byte header, tx count varint, transactions.</summary>
            public byte[] Raw = Array.Empty<byte>();

            /// <summary>The hash in the reversed order explorers show.</summary>
            public string DisplayHash = "";

            public string previousHash = "";

            /// <summary>
            /// The parent block's hash, read out of bytes 4..35 of the header and flipped into the
            /// same reversed order DisplayHash uses. So for two blocks in chain order:
            ///
            ///     child.GetPrevBlockHash() == parent.DisplayHash
            ///
            /// Genesis has no parent and returns 64 zeros.
            /// </summary>
            public string GetPrevBlockHash()
            {
                return ToDisplayHex(GetPrevBlockHashBytes());
            }

            /// <summary>
            /// The same 32 bytes in the little-endian order they are actually stored in on disk -
            /// use this one when comparing against raw header bytes rather than display strings.
            /// </summary>
            public byte[] GetPrevBlockHashBytes()
            {
                if (Raw.Length < 80)
                {
                    throw new InvalidOperationException("block is only " + Raw.Length
                                                        + " bytes, too short to hold an 80 byte header");
                }

                previousHash = ToDisplayHex((Raw.AsSpan(4, 32).ToArray()));
                return Raw.AsSpan(4, 32).ToArray();
            }

            /// <summary>
            /// Value equality: two locations are equal when they name the same record in the same
            /// file and carry the same bytes. This is what lets a by-hash result and a by-position
            /// result be compared with == and != - without it they are two separate objects and
            /// every comparison reports a mismatch even when they found the same block.
            /// </summary>
            public bool Equals(BlockRaw? other)
            {
                if (other is null) return false;
                if (ReferenceEquals(this, other)) return true;

                if (BlockIndex != other.BlockIndex) return false;
                if (Offset != other.Offset) return false;
                if (Size != other.Size) return false;
                if (!string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase)) return false;
                if (!string.Equals(DisplayHash, other.DisplayHash, StringComparison.Ordinal)) return false;

                return Raw.AsSpan().SequenceEqual(other.Raw);
            }

            public override bool Equals(object? obj)
            {
                return Equals(obj as BlockRaw);
            }

            public override int GetHashCode()
            {
                // Deliberately skips Raw - hashing megabytes to look something up is not worth it,
                // and the offset alone already separates blocks within a file.
                return HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(Path),
                                        BlockIndex, Offset, Size, DisplayHash);
            }

            /// <summary>Null-safe, so `found == null` still means what it looks like.</summary>
            public static bool operator ==(BlockRaw? left, BlockRaw? right)
            {
                if (left is null) return right is null;
                return left.Equals(right);
            }

            public static bool operator !=(BlockRaw? left, BlockRaw? right)
            {
                return !(left == right);
            }
        }

        // ------------------------------------------------------------------------------------
        // Writing a block back out
        // ------------------------------------------------------------------------------------

        /// <summary>
        /// Appends one block to a blk-format file, laid out exactly the way the reader above
        /// expects to find it:
        ///
        ///     [4 bytes magic F9BEB4D9][4 bytes block size, little endian][block bytes]
        ///
        /// The file (and its directory) is created when it is not there yet. Every record carries
        /// its own magic bytes, so the first block written to a brand new file and the thousandth
        /// appended to an existing one are written identically - there is no separate file header.
        ///
        /// Bytes go down plain, no XOR, which is the same thing as an all-zero xor.dat. Name the
        /// file blk#####.dat and FindBlockByHash / FindBlockByPosition read it straight back.
        ///
        /// Returns the offset the record was written at, i.e. where its magic bytes landed.
        /// </summary>
        public static long AppendBlockToFile(string path, BlockRaw block)
        {
            if (block is null) throw new ArgumentNullException(nameof(block));
            return AppendBlockToFile(path, block.Raw);
        }

        // ------------------------------------------------------------------------------------
        // Importing blocks from a JSON API response
        // ------------------------------------------------------------------------------------

        /// <summary>
        /// Pulls raw blocks out of a Blockchair-shaped response and appends them to a blk-format
        /// file, which is how a block that was never downloaded gets into the data. The shape it
        /// looks for is:
        ///
        ///     { "data": { "&lt;height or hash&gt;": { "raw_block": "&lt;hex&gt;",
        ///                                        "decoded_raw_block": { "hash": "..." } } } }
        ///
        /// Anything else in the response - context, market_price_usd, and so on - is ignored, and
        /// more than one block in "data" is fine.
        ///
        /// Every block is hashed from its own bytes before being written, and refused outright if
        /// that does not match the hash the response claims, so a truncated or edited response
        /// cannot get into the file. Blocks already in the target are skipped, so re-running this
        /// is harmless.
        ///
        /// Returns how many blocks were actually appended.
        /// </summary>
        public static int AppendBlocksFromJson(string json, string outputPath)
        {
            HashSet<string> already = ReadHashesInFile(outputPath);
            int appended = 0;

            foreach (BlockRaw block in ReadBlocksFromJson(json))
            {
                if (already.Contains(block.DisplayHash))
                {
                    Console.Error.WriteLine("already present, skipping " + block.DisplayHash);
                    continue;
                }

                AppendBlockToFile(outputPath, block.Raw);
                already.Add(block.DisplayHash);
                appended++;
            }

            return appended;
        }

        /// <summary>
        /// The parsing half of the importer: turns a response into BlockRaw objects without
        /// touching any file, so blocks can be handed straight to a ChainAssembler instead.
        /// Each one is verified against the hash the response claims for it.
        /// </summary>
        public static List<BlockRaw> ReadBlocksFromJson(string json)
        {
            var blocks = new List<BlockRaw>();

            using JsonDocument doc = JsonDocument.Parse(json);

            JsonElement data;
            if (!doc.RootElement.TryGetProperty("data", out data))
                throw new InvalidDataException("no \"data\" object in this response");
            if (data.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("\"data\" is not an object keyed by height or hash");

            foreach (JsonProperty entry in data.EnumerateObject())
            {
                JsonElement rawElement;
                if (!entry.Value.TryGetProperty("raw_block", out rawElement))
                {
                    Console.Error.WriteLine("skipping \"" + entry.Name + "\": no raw_block in it");
                    continue;
                }

                byte[] raw = Convert.FromHexString(rawElement.GetString() ?? "");
                if (raw.Length < 81)
                    throw new InvalidDataException("\"" + entry.Name + "\" is only " + raw.Length
                                                   + " bytes, too short to be a block");

                string hash = ToDisplayHex(DoubleSha256(raw, 0, 80));

                // When the response says what the hash should be, hold it to that.
                JsonElement decoded;
                if (entry.Value.TryGetProperty("decoded_raw_block", out decoded))
                {
                    JsonElement claimed;
                    if (decoded.TryGetProperty("hash", out claimed))
                    {
                        string claimedHash = claimed.GetString() ?? "";
                        if (!string.Equals(claimedHash, hash, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException("\"" + entry.Name + "\" hashes to " + hash
                                                           + " but the response calls it " + claimedHash
                                                           + " - the raw_block does not match its own metadata");
                        }
                    }
                }

                blocks.Add(new BlockRaw
                {
                    Path = "json:" + entry.Name,     // it came from a response, not a blk file
                    BlockIndex = -1,
                    Offset = -1,
                    Size = raw.Length,
                    Raw = raw,
                    DisplayHash = hash,
                });

                Console.Error.WriteLine("read " + entry.Name + " " + hash + " (" + raw.Length
                                        + " bytes, parent " + ToDisplayHex(raw.AsSpan(4, 32).ToArray()) + ")");
            }

            return blocks;
        }

        /// <summary>
        /// The hashes of every block already in a blk-format file. Empty when the file does not
        /// exist yet. Only the 80 byte headers are read.
        /// </summary>
        static HashSet<string> ReadHashesInFile(string path)
        {
            var hashes = new HashSet<string>(StringComparer.Ordinal);
            if (!File.Exists(path)) return hashes;

            string directory = Path.GetDirectoryName(path) ?? ".";
            byte[] key = ReadXorKey(directory);

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20);
            byte[] header80 = new byte[80];

            foreach (var record in EnumerateRecords(fs, key))
            {
                ReadBlockHeader(fs, record.Offset, key, header80);
                hashes.Add(ToDisplayHex(DoubleSha256(header80, 0, 80)));
            }

            return hashes;
        }

        // ------------------------------------------------------------------------------------
        // Streaming assembly of the most-work chain
        // ------------------------------------------------------------------------------------

        /// <summary>
        /// Takes blocks in whatever order they turn up and writes out the most-work chain, holding
        /// each block back until it is ConfirmationDepth deep so a later, heavier branch cannot
        /// invalidate what has already gone to disk.
        ///
        /// Three things have to be tracked to decide "most work", and a rolling buffer of recent
        /// blocks cannot do any of them:
        ///
        ///   - a block's parent is usually nowhere near it in an arrival-ordered file, so blocks
        ///     that do not connect yet are parked by the parent they are waiting for, and get
        ///     connected later when that parent shows up (cascading, since connecting one block
        ///     can release a whole run of blocks waiting behind it),
        ///   - work is not height. It comes from the header's compact "bits" target, and the chain
        ///     with the most cumulative work wins - which is not always the longest one,
        ///   - the winner can only be known in hindsight, hence the depth lag.
        ///
        /// Writes go through AppendBlockToFile, so the output is an ordinary blk-format file.
        /// </summary>
        public sealed class ChainAssembler
        {
            /// <summary>One block that has found its parent, and what the chain to it weighs.</summary>
            sealed class Node
            {
                public string Hash = "";
                public int Height;                  // distance from the root, not true chain height
                public BigInteger ChainWork;
                public Node? Parent;
                public BlockRaw? Block;             // dropped once written, to keep memory flat
                public bool Written;
            }

            readonly string _outputPath;
            readonly int _confirmationDepth;
            readonly int _maxPending;

            readonly Dictionary<string, Node> _byHash = new Dictionary<string, Node>(StringComparer.Ordinal);

            /// <summary>Blocks whose parent has not turned up yet, keyed by that missing parent.</summary>
            readonly Dictionary<string, List<BlockRaw>> _waitingOnParent =
                new Dictionary<string, List<BlockRaw>>(StringComparer.Ordinal);

            /// <summary>Arrival order of the parked blocks, so the oldest can be dropped first.</summary>
            readonly Queue<string> _parkedOrder = new Queue<string>();

            Node? _bestTip;
            Node? _lastWritten;
            bool _stalled;

            /// <summary>How far behind the tip a block has to be before it is committed.</summary>
            public int ConfirmationDepth => _confirmationDepth;

            public int Written { get; private set; }
            public int Duplicates { get; private set; }
            public int Evicted { get; private set; }
            public int DeepReorgs { get; private set; }
            public int Pending { get; private set; }

            public int BestTipHeight
            {
                get
                {
                    if (_bestTip == null) return -1;
                    return _bestTip.Height;
                }
            }

            public string BestTipHash
            {
                get
                {
                    if (_bestTip == null) return "";
                    return _bestTip.Hash;
                }
            }

            /// <param name="outputPath">blk-format file the committed chain is appended to.</param>
            /// <param name="confirmationDepth">
            /// How far behind the tip a block has to be before it is written. 50 is the value the
            /// caller wanted: a competing branch would have to arrive 50 blocks long to undo it.
            /// </param>
            /// <param name="maxPending">
            /// Cap on blocks parked waiting for a parent. Past this the oldest are dropped, which
            /// is the bounded-memory version of a rolling buffer.
            /// </param>
            public ChainAssembler(string outputPath, int confirmationDepth, int maxPending)
            {
                if (confirmationDepth < 0) throw new ArgumentOutOfRangeException(nameof(confirmationDepth));
                if (maxPending < 1) throw new ArgumentOutOfRangeException(nameof(maxPending));

                _outputPath = outputPath;
                _confirmationDepth = confirmationDepth;
                _maxPending = maxPending;
            }

            /// <summary>
            /// Offers one block to the assembler. It is connected if its parent is already known,
            /// parked if not, and ignored if it has been seen before.
            /// </summary>
            public void Add(BlockRaw block)
            {
                if (block is null) throw new ArgumentNullException(nameof(block));

                string hash = block.DisplayHash;
                if (_byHash.ContainsKey(hash))
                {
                    Duplicates++;
                    return;
                }

                string prev = block.GetPrevBlockHash();

                if (prev == NoParentHash)
                {
                    ConnectDescendants(Attach(block, null));      // genesis: the root of everything
                    return;
                }

                Node? parent;
                if (_byHash.TryGetValue(prev, out parent))
                {
                    ConnectDescendants(Attach(block, parent));
                    return;
                }

                Park(block, prev);
            }

            /// <summary>
            /// Writes everything on the best chain that is now at least ConfirmationDepth behind
            /// the tip. Cheap to call after every Add.
            /// </summary>
            public void Flush()
            {
                if (_bestTip == null) return;
                WriteUpTo(_bestTip.Height - _confirmationDepth);
            }

            /// <summary>
            /// Commits the rest of the best chain, including the last ConfirmationDepth blocks.
            /// Only correct once the input is exhausted - there is nothing left that could arrive
            /// and outweigh the tip.
            /// </summary>
            public void FlushAll()
            {
                if (_bestTip == null) return;
                WriteUpTo(_bestTip.Height);
            }

            Node Attach(BlockRaw block, Node? parent)
            {
                var node = new Node
                {
                    Hash = block.DisplayHash,
                    Parent = parent,
                    Block = block,
                };

                if (parent == null)
                {
                    node.Height = 0;
                    node.ChainWork = BlockWork(block.Raw);
                }
                else
                {
                    node.Height = parent.Height + 1;
                    node.ChainWork = parent.ChainWork + BlockWork(block.Raw);
                }

                _byHash[node.Hash] = node;

                // A child always outweighs its parent, so "heaviest node seen" is the best tip -
                // no separate list of tips to maintain.
                if (_bestTip == null || node.ChainWork > _bestTip.ChainWork)
                {
                    _bestTip = node;
                }
                return node;
            }

            /// <summary>
            /// Releases every parked block that was waiting on this one, then everything waiting
            /// on those, and so on - one arrival can reconnect a long run of blocks.
            /// </summary>
            void ConnectDescendants(Node parent)
            {
                var ready = new Queue<Node>();
                ready.Enqueue(parent);

                while (ready.Count > 0)
                {
                    Node current = ready.Dequeue();

                    List<BlockRaw>? children;
                    if (!_waitingOnParent.TryGetValue(current.Hash, out children)) continue;
                    _waitingOnParent.Remove(current.Hash);

                    foreach (BlockRaw child in children)
                    {
                        Pending--;
                        if (_byHash.ContainsKey(child.DisplayHash))
                        {
                            Duplicates++;
                            continue;
                        }
                        ready.Enqueue(Attach(child, current));
                    }
                }
            }

            void Park(BlockRaw block, string prevHash)
            {
                List<BlockRaw>? waiting;
                if (!_waitingOnParent.TryGetValue(prevHash, out waiting))
                {
                    waiting = new List<BlockRaw>();
                    _waitingOnParent[prevHash] = waiting;
                }

                waiting.Add(block);
                _parkedOrder.Enqueue(prevHash);
                Pending++;

                while (Pending > _maxPending && _parkedOrder.Count > 0)
                {
                    string oldest = _parkedOrder.Dequeue();

                    List<BlockRaw>? victims;
                    if (!_waitingOnParent.TryGetValue(oldest, out victims)) continue;
                    if (victims.Count == 0)
                    {
                        _waitingOnParent.Remove(oldest);
                        continue;
                    }

                    victims.RemoveAt(0);
                    Pending--;
                    Evicted++;
                    if (victims.Count == 0) _waitingOnParent.Remove(oldest);
                }
            }

            /// <summary>
            /// Writes the best chain down to targetHeight, in order, starting from wherever the
            /// last write left off.
            /// </summary>
            void WriteUpTo(int targetHeight)
            {
                if (_stalled) return;
                if (_bestTip == null || targetHeight < 0) return;

                // Back up from the tip to the deepest block we are willing to commit to...
                Node? node = _bestTip;
                while (node != null && node.Height > targetHeight) node = node.Parent;

                // ...then keep backing up to the last block already on disk, stacking the ones in
                // between so they can be written parent-first.
                var path = new Stack<Node>();
                while (node != null && !node.Written)
                {
                    path.Push(node);
                    node = node.Parent;
                }

                // node is now the deepest already-written ancestor. If that is not the block we
                // last wrote, the winning chain no longer descends from the output file - a reorg
                // deeper than ConfirmationDepth, which is the case this design bets against. The
                // file cannot be unwritten, so stop rather than append a broken link onto it.
                if (node != null && node != _lastWritten)
                {
                    DeepReorgs++;
                    _stalled = true;
                    Console.WriteLine("deep reorg past the committed tip " + _lastWritten!.Hash
                                      + " - stopping writes, the output holds everything up to there");
                    return;
                }

                while (path.Count > 0)
                {
                    Node write = path.Pop();
                    AppendBlockToFile(_outputPath, write.Block!.Raw);

                    write.Written = true;
                    write.Block = null;          // the bytes are on disk now, let them go
                    _lastWritten = write;
                    Written++;
                }
            }
        }

        /// <summary>
        /// How much work a block adds to its chain: 2^256 / (target + 1), the same measure Core
        /// uses to compare branches. The target is unpacked from the compact 4-byte "bits" field
        /// at offset 72 of the header - top byte is the exponent, low three the mantissa.
        /// </summary>
        public static BigInteger BlockWork(byte[] rawBlock)
        {
            if (rawBlock.Length < 80) throw new ArgumentException("not a block header", nameof(rawBlock));

            uint bits = BinaryPrimitives.ReadUInt32LittleEndian(rawBlock.AsSpan(72, 4));
            int exponent = (int)(bits >> 24);
            uint mantissa = bits & 0x007FFFFF;

            BigInteger target;
            if (exponent <= 3)
            {
                target = new BigInteger(mantissa) >> (8 * (3 - exponent));
            }
            else
            {
                target = new BigInteger(mantissa) << (8 * (exponent - 3));
            }

            if (target <= BigInteger.Zero) return BigInteger.Zero;
            return (BigInteger.One << 256) / (target + BigInteger.One);
        }

        /// <summary>What a reorder pass did, so the caller can see whether it got everything.</summary>
        public sealed class ChainOrderResult
        {
            /// <summary>Records found in the source file.</summary>
            public int BlocksInFile;

            /// <summary>Blocks written to the output, in chain order.</summary>
            public int BlocksWritten;

            /// <summary>Blocks in the file that the walk never reached (gaps, or forks).</summary>
            public int Unreachable;

            /// <summary>Parents that had more than one child - a fork. The first one seen wins.</summary>
            public int ForkedParents;

            /// <summary>Hash the walk started from, and the one it ran out of children at.</summary>
            public string StartHash = "";
            public string EndHash = "";
        }

        /// <summary>
        /// Rewrites a blk file into chain order, which is the fix for broken prev-hash links: the
        /// records in a downloaded file are in the order blocks arrived off the wire, so a block's
        /// child is usually nowhere near it.
        ///
        /// Pass one hashes every header and indexes the records by their *parent* hash, so the
        /// child of any block is a dictionary lookup rather than another scan. Pass two starts at
        /// startDisplayHash - or at the block with no parent, i.e. genesis, when that is empty -
        /// and follows the links, copying each block to outputPath as it goes. What comes out has
        /// every record's PrevBlockHash equal to the hash of the record before it, by construction.
        ///
        /// outputPath is overwritten. maxBlocks caps the walk; pass 0 for the whole chain.
        ///
        /// Blocks the walk never reaches are counted in Unreachable and left behind - those are
        /// the ones whose parent is not in this file (it is in another blk file, or was never
        /// downloaded), plus the losing side of any fork.
        /// </summary>
        public static ChainOrderResult WriteChainOrdered(string directory, int fileIndex, string outputPath,
                                                         string startDisplayHash, int maxBlocks)
        {
            string path = BlkFilePath(directory, fileIndex);
            if (!File.Exists(path))
                throw new FileNotFoundException("no such block file: " + path, path);

            byte[] key = ReadXorKey(directory);
            var result = new ChainOrderResult();

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20);

            // Pass 1: index by parent hash. Only the 80 byte headers are read here.
            var childOfParent = new Dictionary<string, ChainEntry>(StringComparer.Ordinal);
            ChainEntry? start = null;
            byte[] header80 = new byte[80];

            foreach (var record in EnumerateRecords(fs, key))
            {
                result.BlocksInFile++;

                ReadBlockHeader(fs, record.Offset, key, header80);
                var entry = new ChainEntry
                {
                    Hash = ToDisplayHex(DoubleSha256(header80, 0, 80)),
                    PrevHash = ToDisplayHex(header80.AsSpan(4, 32).ToArray()),
                    Offset = record.Offset,
                    Size = record.Size,
                };

                if (!childOfParent.TryAdd(entry.PrevHash, entry))
                {
                    result.ForkedParents++;      // two blocks claim the same parent, keep the first
                }

                if (start != null) continue;
                if (startDisplayHash.Length == 0 && entry.PrevHash == NoParentHash) start = entry;
                if (startDisplayHash.Length > 0 && entry.Hash == startDisplayHash) start = entry;
            }

            if (start == null)
            {
                if (startDisplayHash.Length > 0)
                    throw new InvalidOperationException("block " + startDisplayHash + " is not in " + path);
                throw new InvalidOperationException(path + " holds no block without a parent, so there is "
                                                    + "nothing to start from - name a start hash instead");
            }

            // Pass 2: follow the links, copying each block out as it is reached.
            using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);

            result.StartHash = start.Hash;
            ChainEntry current = start;

            while (true)
            {
                WriteRecord(outFs, ReadBlockBytes(fs, current.Offset, current.Size, key));
                result.BlocksWritten++;
                result.EndHash = current.Hash;

                if (maxBlocks > 0 && result.BlocksWritten >= maxBlocks) break;
                if (!childOfParent.TryGetValue(current.Hash, out ChainEntry? next)) break;
                current = next;
            }

            outFs.Flush();
            result.Unreachable = result.BlocksInFile - result.BlocksWritten;
            return result;
        }

        /// <summary>One record's place in the file plus the two hashes that chain it.</summary>
        sealed class ChainEntry
        {
            public string Hash = "";
            public string PrevHash = "";
            public long Offset;
            public int Size;
        }

        /// <summary>Same, for raw block bytes that did not come from a BlockRaw.</summary>
        public static long AppendBlockToFile(string path, byte[] rawBlock)
        {
            if (rawBlock is null) throw new ArgumentNullException(nameof(rawBlock));
            if (rawBlock.Length < 81)
                throw new ArgumentException("a block is at least 81 bytes - an 80 byte header plus a "
                                            + "transaction count - and this is " + rawBlock.Length, nameof(rawBlock));
            if (rawBlock.Length > MaxBlockBytes)
                throw new ArgumentException("block is " + rawBlock.Length + " bytes, past the "
                                            + MaxBlockBytes + " the reader will accept back", nameof(rawBlock));

            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);      // no-op when it is already there
            }

            // FileMode.Append creates the file when it is missing and always seeks to the end.
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 1 << 16);
            long offset = fs.Position;

            WriteRecord(fs, rawBlock);
            fs.Flush();

            return offset;
        }

        /// <summary>
        /// Writes one [magic][size][block] record at the stream's current position. The single
        /// definition of the on-disk layout that everything writing a blk file goes through.
        /// </summary>
        static void WriteRecord(FileStream fs, byte[] rawBlock)
        {
            Span<byte> recordHeader = stackalloc byte[8];
            Magic.CopyTo(recordHeader);
            BinaryPrimitives.WriteUInt32LittleEndian(recordHeader.Slice(4, 4), (uint)rawBlock.Length);

            fs.Write(recordHeader);
            fs.Write(rawBlock, 0, rawBlock.Length);
        }

        /// <summary>
        /// Walks one blk file looking for the block with this display-order hash. Returns null if
        /// the file does not hold it; blocksScanned says how many records were checked.
        /// </summary>
        public static BlockRaw? FindBlockByHash(string directory, int fileIndex, string displayHash, out int blocksScanned)
        {
            byte[] want = ReverseCopy(Convert.FromHexString(NormalizeHash(displayHash)));
            return ScanBlkFile(directory, fileIndex, want, -1, out blocksScanned);
        }

        /// <summary>
        /// Takes the blockIndex'th block in the file, counting record by record from 0. Returns
        /// null if the file holds fewer blocks than that; blocksScanned is then the total.
        /// </summary>
        public static BlockRaw? FindBlockByPosition(string directory, int fileIndex, int blockIndex, out int blocksScanned)
        {
            if (blockIndex < 0) throw new ArgumentOutOfRangeException(nameof(blockIndex), "block position starts at 0");
            return ScanBlkFile(directory, fileIndex, null, blockIndex, out blocksScanned);
        }

        /// <summary>
        /// One pass over a blk file's records. Exactly one of wantHash (internal byte order) and
        /// wantIndex (0-based position, -1 when unused) selects the block.
        /// </summary>
        static BlockRaw? ScanBlkFile(string directory, int fileIndex, byte[]? wantHash, int wantIndex, out int blocksScanned)
        {
            blocksScanned = 0;

            string path = BlkFilePath(directory, fileIndex);
            if (!File.Exists(path))
                throw new FileNotFoundException("no such block file: " + path, path);

            byte[] key = ReadXorKey(directory);

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20);
            byte[] header80 = new byte[80];

            foreach (var record in EnumerateRecords(fs, key))
            {
                int index = blocksScanned++;
                bool match;

                if (wantHash != null)
                {
                    ReadBlockHeader(fs, record.Offset, key, header80);
                    match = DoubleSha256(header80, 0, 80).AsSpan().SequenceEqual(wantHash);
                }
                else
                {
                    match = index == wantIndex;      // by position, so nothing to hash on the way
                }

                if (match)
                {
                    byte[] raw = ReadBlockBytes(fs, record.Offset, record.Size, key);

                    return new BlockRaw
                    {
                        Path = path,
                        BlockIndex = index,
                        Offset = record.Offset,
                        Size = record.Size,
                        Raw = raw,
                        DisplayHash = ToDisplayHex(DoubleSha256(raw, 0, 80)),
                    };
                }
            }

            return null;
        }

        /// <summary>
        /// Walks the records of an open blk file, handing back the offset of each one's magic
        /// bytes and the size of the block that follows. A run of zeros ends the walk (Core
        /// pre-allocates its files), and magic bytes that are not really a record header - which
        /// transaction data can contain - are resynced past.
        ///
        /// The enumerator repositions the stream itself on every step, so callers are free to
        /// seek around inside the loop body.
        /// </summary>
        static IEnumerable<(long Offset, int Size)> EnumerateRecords(FileStream fs, byte[] key)
        {
            long length = fs.Length;
            byte[] recordHeader = new byte[8];
            long offset = 0;

            while (offset + 8 <= length)
            {
                fs.Position = offset;
                fs.ReadExactly(recordHeader, 0, 8);
                Deobfuscate(recordHeader, 0, 8, offset, key);

                if (!HasMagic(recordHeader))
                {
                    if (IsAllZero(recordHeader)) break;

                    offset = ResyncToMagic(fs, offset + 1, key);
                    if (offset < 0) break;
                    continue;
                }

                int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(recordHeader.AsSpan(4, 4));
                long blockStart = offset + 8;

                if (size < 81 || size > MaxBlockBytes || blockStart + size > length)
                {
                    offset = ResyncToMagic(fs, offset + 1, key);
                    if (offset < 0) break;
                    continue;
                }

                yield return (offset, size);
                offset = blockStart + size;
            }
        }

        /// <summary>Reads the 80 byte header of the record whose magic bytes are at recordOffset.</summary>
        static void ReadBlockHeader(FileStream fs, long recordOffset, byte[] key, byte[] header80)
        {
            long blockStart = recordOffset + 8;
            fs.Position = blockStart;
            fs.ReadExactly(header80, 0, 80);
            Deobfuscate(header80, 0, 80, blockStart, key);
        }

        /// <summary>Reads the whole block of the record whose magic bytes are at recordOffset.</summary>
        static byte[] ReadBlockBytes(FileStream fs, long recordOffset, int size, byte[] key)
        {
            long blockStart = recordOffset + 8;
            byte[] raw = new byte[size];
            fs.Position = blockStart;
            fs.ReadExactly(raw, 0, size);
            Deobfuscate(raw, 0, size, blockStart, key);
            return raw;
        }

        /// <summary>Scans forward for the next magic bytes. Returns -1 at end of file.</summary>
        static long ResyncToMagic(FileStream fs, long from, byte[] key)
        {
            const int ChunkBytes = 1 << 20;
            byte[] buf = new byte[ChunkBytes];
            long length = fs.Length;

            while (from + 4 <= length)
            {
                int want = (int)Math.Min(ChunkBytes, length - from);
                fs.Position = from;
                fs.ReadExactly(buf, 0, want);
                Deobfuscate(buf, 0, want, from, key);

                for (int i = 0; i + 4 <= want; i++)
                {
                    if (buf[i] == Magic[0] && buf[i + 1] == Magic[1] && buf[i + 2] == Magic[2] && buf[i + 3] == Magic[3])
                        return from + i;
                }

                // Overlap by 3 so a magic straddling the chunk boundary is not missed.
                from += want - 3;
            }
            return -1;
        }

        /// <summary>
        /// Best-effort "did you mean" when the block is not in the file that was asked for: the
        /// downloader writes a blocks.index next to the .dat files mapping hash -> file number.
        /// </summary>
        static void ReportIndexHint(string directory, string displayHash, int triedFileIndex)
        {
            string indexPath = Path.Combine(directory, "blocks.index");
            if (!File.Exists(indexPath)) return;

            byte[] want = ReverseCopy(Convert.FromHexString(displayHash));

            try
            {
                using var fs = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20);
                byte[] rec = new byte[IndexRecordBytes];
                long usable = fs.Length - (fs.Length % IndexRecordBytes);

                for (long at = 0; at < usable; at += IndexRecordBytes)
                {
                    fs.ReadExactly(rec, 0, IndexRecordBytes);
                    if (!rec.AsSpan(0, 32).SequenceEqual(want)) continue;

                    int fileNo = (int)BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(32, 4));
                    Console.Error.WriteLine("blocks.index says it is in blk" + fileNo.ToString("D5") + ".dat at offset "
                                            + BinaryPrimitives.ReadInt64LittleEndian(rec.AsSpan(36, 8))
                                            + " - re-run with index " + fileNo + " instead of " + triedFileIndex);
                    return;
                }
                Console.Error.WriteLine("blocks.index does not list this block either - it was never downloaded");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("could not read " + indexPath + ": " + ex.Message);
            }
        }

        // ------------------------------------------------------------------------------------
        // Helpers - kept local so this file stands on its own
        // ------------------------------------------------------------------------------------

        static string BlkFilePath(string directory, int fileIndex) =>
            Path.Combine(directory, "blk" + fileIndex.ToString("D5") + ".dat");

        /// <summary>
        /// Core's xor.dat: eight bytes applied to the file by absolute offset. An all-zero key is
        /// returned as an empty array so the XOR turns into a no-op.
        /// </summary>
        static byte[] ReadXorKey(string directory)
        {
            string path = Path.Combine(directory, "xor.dat");
            if (!File.Exists(path)) return Array.Empty<byte>();

            byte[] key = File.ReadAllBytes(path);
            if (key.Length != 8)
            {
                Console.Error.WriteLine("ignoring " + path + ": expected 8 bytes, found " + key.Length);
                return Array.Empty<byte>();
            }
            if (IsAllZero(key)) return Array.Empty<byte>();
            return key;
        }

        static void Deobfuscate(byte[] buffer, int start, int count, long fileOffset, byte[] key)
        {
            if (key.Length == 0) return;
            for (int i = 0; i < count; i++)
                buffer[start + i] ^= key[(int)((fileOffset + i) & 7)];
        }

        static bool HasMagic(byte[] buf) =>
            buf[0] == Magic[0] && buf[1] == Magic[1] && buf[2] == Magic[2] && buf[3] == Magic[3];

        static bool IsAllZero(byte[] buf)
        {
            foreach (byte b in buf) if (b != 0) return false;
            return true;
        }

        static byte[] DoubleSha256(byte[] data, int offset, int count)
        {
            Span<byte> first = stackalloc byte[32];
            SHA256.HashData(data.AsSpan(offset, count), first);
            byte[] second = new byte[32];
            SHA256.HashData(first, second);
            return second;
        }

        /// <summary>Internal (little endian) hash bytes to the reversed hex explorers display.</summary>
        static string ToDisplayHex(byte[] hash) => Convert.ToHexString(ReverseCopy(hash)).ToLowerInvariant();

        static byte[] ReverseCopy(byte[] input)
        {
            byte[] copy = (byte[])input.Clone();
            Array.Reverse(copy);
            return copy;
        }

        static ulong ReadVarInt(byte[] data, ref int pos)
        {
            byte prefix = data[pos++];
            switch (prefix)
            {
                case 0xFD:
                    ulong a = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2)); pos += 2; return a;
                case 0xFE:
                    ulong b = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4)); pos += 4; return b;
                case 0xFF:
                    ulong c = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos, 8)); pos += 8; return c;
                default:
                    return prefix;
            }
        }
    }
}
