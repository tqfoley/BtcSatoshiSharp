using Org.BouncyCastle.Utilities;
using Org.BouncyCastle.Utilities.Encoders;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Drawing;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Tasks.Dataflow;

// The longest-chain harness lives in ConsoleApp.LongestChainHarness, and MyRawBlock / MyBlock /
// ChainState are nested inside it. `using static` pulls the nested types and the static methods in
// unqualified, so BuildLongestChain(...) and MyRawBlock<byte[]> below read the same here as they do
// over there.
using static ConsoleApp.LongestChainHarness;

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
                BlockRaw? foundByHash = FindBlockByHash(@"C:\btcblock\claudeblocks", reqBlock3ByHash.FileIndex, reqBlock3ByHash.Hash, out scannedByHash);
                BlockRaw? foundByIndex = FindBlockByPosition(@"C:\btcblock\claudeblocks", reqBlock3ByIndex.FileIndex, reqBlock3ByIndex.BlockIndex, out scannedByIndex);


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


                //PrintTimestampExtremes(@"C:\btcblock\claudeblocks\blk00001.dat", 10);
                //PrintTimestampExtremes(@"C:\btcblock\claudeblocks\blk00002.dat", 10);
                //PrintTimestampExtremes(@"C:\btcblock\claudeblocks\blk00003.dat", 10);
                //PrintTimestampExtremes(@"C:\btcblock\claudeblocks\blk00004.dat", 10);


                // order ALL .dat files by timestamp
                if (false)
                {
                    var files = new List<string>();
                    foreach (string file in Directory.EnumerateFiles(@"C:\btcblock\claudeblocks", "blk*.dat"))
                    {
                        // A three-character extension in the pattern is treated as a PREFIX match on Windows,
                        // so "*.dat" also hands back blk00000.database. Check the real ending.
                        if (!file.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)) continue;
                        files.Add(file);
                    }
                    foreach (string file in files)
                    {
                        Console.WriteLine("found blk file: " + file);
                        SortBlockFileByTimestamp(file);
                    }
                }






                if (false)
                {
                    DeleteAllFilesIn("C:\\btcblock\\inOrder\\");
                }


                //claude's fake block 33 raw date 01000000e3f6664d5af37062b934f983ed1033e2011b42c9b04735276c7ccbe50000000033c56986d991564d8f2e5d6b3b98105c882a5b108738d0994407de8b72935ac4efc86849ffff001df9649d460101000000010000000000000000000000000000000000000000000000000000000000000000ffffffff1d04ffff001d12414c5433332f464f524b2d464958545552450400000000ffffffff0100f2052a01000000434104804d71f6a91c908a973cae7ef4363f7689520116b995d6936328de00be56f92baee0dabf3a240e0ed2dce7f374f12cbba7649808528236cb04c558f028dd61edac00000000
                //claude's fake block 33 hash    0000000096a151f27d9cd2d706b6b8e16ba43e7e290bbb77f9eff8fe1d20c66c parent  00000000e5cb7c6c273547b0c9421b01e23310ed83f934b96270f35a4d66f6e3   ← identical to real block 33 time    1231603951(12s after the real block) nonce   1184720121 bits    1d00ffff(unchanged — difficulty is consensus -fixed in this epoch)
                                                                       //01234567 123456789012345678901234567890123456789012345678901234567890123 123456789012345678901234567890123456789012345678901234567890123                                                                                                                            01234567890123456789012345678901234567890123
                string fakeBlock33 = "{\"data\":{\"33\":{\"raw_block\":\"01000000e3f6664d5af37062b934f983ed1033e2011b42c9b04735276c7ccbe50000000033c56986d991564d8f2e5d6b3b98105c882a5b108738d0994407de8b72935ac4efc86849ffff001df9649d460101000000010000000000000000000000000000000000000000000000000000000000000000ffffffff1d04ffff001d12414c5433332f464f524b2d464958545552450400000000ffffffff0100f2052a01000000434104804d71f6a91c908a973cae7ef4363f7689520116b995d6936328de00be56f92baee0dabf3a240e0ed2dce7f374f12cbba7649808528236cb04c558f028dd61edac00000000\",\"decoded_raw_block\":{\"hash\":\"0000000096a151f27d9cd2d706b6b8e16ba43e7e290bbb77f9eff8fe1d20c66c\",\"confirmations\":-1,\"height\":33,\"version\":1,\"versionHex\":\"00000001\",\"merkleroot\":\"c45a93728bde074499d03887105b2a885c10983b6b5d2e8f4d5691d98669c533\",\"time\":1231603951,\"mediantime\":1231601457,\"nonce\":1184720121,\"bits\":\"1d00ffff\",\"difficulty\":1,\"chainwork\":\"0000000000000000000000000000000000000000000000000000002200220022\",\"previousblockhash\":\"00000000e5cb7c6c273547b0c9421b01e23310ed83f934b96270f35a4d66f6e3\",\"strippedsize\":237,\"size\":237,\"weight\":948,\"nTx\":1,\"tx\":[\"c45a93728bde074499d03887105b2a885c10983b6b5d2e8f4d5691d98669c533\"]}}},\"context\":{\"code\":200,\"source\":\"SYNTHETIC\",\"results\":1,\"state\":960939,\"market_price_usd\":63703,\"cache\":{\"live\":false,\"duration\":120,\"since\":\"2026-08-04 03:37:36\",\"until\":\"2026-08-04 03:39:36\",\"time\":null},\"api\":{\"version\":\"2.0.95-ie\",\"last_major_update\":\"2022-11-07 02:00:00\",\"next_major_update\":\"2023-11-12 02:00:00\",\"documentation\":\"https://blockchair.com/api/docs\",\"notice\":\"SYNTHETIC FIXTURE - not a historical block and not served by any explorer. Locally mined competitor to block 33 for fork / stale-tip detection testing.\"},\"servers\":\"SYNTHETIC\",\"time\":0.006392955780029297,\"render_time\":0.0043070316314697266,\"full_time\":0.010699987411499023,\"request_cost\":1}}";
                string jsonBlock33 = "{\"data\":{\"33\":{\"raw_block\":\"01000000e3f6664d5af37062b934f983ed1033e2011b42c9b04735276c7ccbe5000000001012aaab3e3bffd34055aaa157bf78792d5c18f085635eda7046d89c08a0eabde3c86849ffff001d228c22400101000000010000000000000000000000000000000000000000000000000000000000000000ffffffff0704ffff001d0138ffffffff0100f2052a01000000434104804d71f6a91c908a973cae7ef4363f7689520116b995d6936328de00be56f92baee0dabf3a240e0ed2dce7f374f12cbba7649808528236cb04c558f028dd61edac00000000\",\"decoded_raw_block\":{\"hash\":\"00000000a87073ea3d7af299e02a434598b9c92094afa552e0711afcc0857962\",\"confirmations\":960926,\"height\":33,\"version\":1,\"versionHex\":\"00000001\",\"merkleroot\":\"bdeaa0089cd84670da5e6385f0185c2d7978bf57a1aa5540d3ff3b3eabaa1210\",\"time\":1231603939,\"mediantime\":1231601457,\"nonce\":1076005922,\"bits\":\"1d00ffff\",\"difficulty\":1,\"chainwork\":\"0000000000000000000000000000000000000000000000000000002200220022\",\"nTx\":1,\"previousblockhash\":\"00000000e5cb7c6c273547b0c9421b01e23310ed83f934b96270f35a4d66f6e3\",\"nextblockhash\":\"00000000a73fb23b6c42b18b3253ed29c5d0c80d84624efa12c2cf05c4b4318f\",\"strippedsize\":215,\"size\":215,\"weight\":860,\"tx\":[\"bdeaa0089cd84670da5e6385f0185c2d7978bf57a1aa5540d3ff3b3eabaa1210\"]}}},\"context\":{\"code\":200,\"source\":\"T+R\",\"results\":1,\"state\":960939,\"market_price_usd\":63703,\"cache\":{\"live\":true,\"duration\":120,\"since\":\"2026-08-04 03:37:36\",\"until\":\"2026-08-04 03:39:36\",\"time\":null},\"api\":{\"version\":\"2.0.95-ie\",\"last_major_update\":\"2022-11-07 02:00:00\",\"next_major_update\":\"2023-11-12 02:00:00\",\"documentation\":\"https:\\/\\/blockchair.com\\/api\\/docs\",\"notice\":\"Try out our new API v.3: https:\\/\\/3xpl.com\\/data\"},\"servers\":\"API4,BTC5,BTC5,BTC5\",\"time\":0.006392955780029297,\"render_time\":0.0043070316314697266,\"full_time\":0.010699987411499023,\"request_cost\":1}}";
                // https://api.blockchair.com/bitcoin/raw/block/33
                List<BlockRaw> missingBlock33 =  ReadBlocksFromJson(jsonBlock33);

                string jsonBlock32 = "{\"data\":{\"32\":{\"raw_block\":\"01000000c4d369b723c2cf9be33cf00deb1dbfea0c8ccd12c415f29434ff009700000000c9c0fd0ae7b7973c42fc9e3dddc967b6e309570b720ff15414c08365f005992be3c56849ffff001d08e1c00d0101000000010000000000000000000000000000000000000000000000000000000000000000ffffffff0704ffff001d0136ffffffff0100f2052a01000000434104b949980bb46aee11510519b4af0dfcc3cc7464b3ede15f184b7c8126a98bf6d6e698eaf16b938814174a002ba24daa03e59a7c0927248517b581c09ec70f216eac00000000\",\"decoded_raw_block\":{\"hash\":\"00000000e5cb7c6c273547b0c9421b01e23310ed83f934b96270f35a4d66f6e3\",\"confirmations\":961020,\"height\":32,\"version\":1,\"versionHex\":\"00000001\",\"merkleroot\":\"2b9905f06583c01454f10f720b5709e3b667c9dd3d9efc423c97b7e70afdc0c9\",\"time\":1231603171,\"mediantime\":1231570573,\"nonce\":230744328,\"bits\":\"1d00ffff\",\"difficulty\":1,\"chainwork\":\"0000000000000000000000000000000000000000000000000000002100210021\",\"nTx\":1,\"previousblockhash\":\"000000009700ff3494f215c412cd8c0ceabf1deb0df03ce39bcfc223b769d3c4\",\"nextblockhash\":\"00000000a87073ea3d7af299e02a434598b9c92094afa552e0711afcc0857962\",\"strippedsize\":215,\"size\":215,\"weight\":860,\"tx\":[\"2b9905f06583c01454f10f720b5709e3b667c9dd3d9efc423c97b7e70afdc0c9\"]}}},\"context\":{\"code\":200,\"source\":\"T+R\",\"results\":1,\"state\":961051,\"market_price_usd\":64049,\"cache\":{\"live\":true,\"duration\":120,\"since\":\"2026-08-04 17:57:58\",\"until\":\"2026-08-04 17:59:58\",\"time\":null},\"api\":{\"version\":\"2.0.95-ie\",\"last_major_update\":\"2022-11-07 02:00:00\",\"next_major_update\":\"2023-11-12 02:00:00\",\"documentation\":\"https:\\/\\/blockchair.com\\/api\\/docs\",\"notice\":\"Try out our new API v.3: https:\\/\\/3xpl.com\\/data\"},\"servers\":\"API4,BTC5,BTC5,BTC5\",\"time\":0.01161813735961914,\"render_time\":0.0032088756561279297,\"full_time\":0.01482701301574707,\"request_cost\":1}}";
                List<BlockRaw> missingBlock32 = ReadBlocksFromJson(jsonBlock32);

                string jsonBlock34 = "{\"data\":{\"34\":{\"raw_block\":\"01000000627985c0fc1a71e052a5af9420c9b99845432ae099f27a3dea7370a80000000074549b3151d6dd4ce77419d01710921b3211ed3280bf2e3af2c1f1a820063b2272ca6849ffff001d2243c0240101000000010000000000000000000000000000000000000000000000000000000000000000ffffffff0704ffff001d0147ffffffff0100f2052a01000000434104180bfa57bff462c7641fa0b91efe29344a77086b073cd9c5f769cb2393acc151a4e7377eaabacc39f5b2bd2cd4bcb5ed1855939619e491c79c0bb5793d4edbf3ac00000000\",\"decoded_raw_block\":{\"hash\":\"00000000a73fb23b6c42b18b3253ed29c5d0c80d84624efa12c2cf05c4b4318f\",\"confirmations\":961018,\"height\":34,\"version\":1,\"versionHex\":\"00000001\",\"merkleroot\":\"223b0620a8f1c1f23a2ebf8032ed11321b921017d01974e74cddd651319b5474\",\"time\":1231604338,\"mediantime\":1231601503,\"nonce\":616579874,\"bits\":\"1d00ffff\",\"difficulty\":1,\"chainwork\":\"0000000000000000000000000000000000000000000000000000002300230023\",\"nTx\":1,\"previousblockhash\":\"00000000a87073ea3d7af299e02a434598b9c92094afa552e0711afcc0857962\",\"nextblockhash\":\"00000000b572a465b4e816420d47a16274557b3573b7924b64808a82c7322d9b\",\"strippedsize\":215,\"size\":215,\"weight\":860,\"tx\":[\"223b0620a8f1c1f23a2ebf8032ed11321b921017d01974e74cddd651319b5474\"]}}},\"context\":{\"code\":200,\"source\":\"T+R\",\"results\":1,\"state\":961051,\"market_price_usd\":64049,\"cache\":{\"live\":true,\"duration\":120,\"since\":\"2026-08-04 17:59:02\",\"until\":\"2026-08-04 18:01:02\",\"time\":null},\"api\":{\"version\":\"2.0.95-ie\",\"last_major_update\":\"2022-11-07 02:00:00\",\"next_major_update\":\"2023-11-12 02:00:00\",\"documentation\":\"https:\\/\\/blockchair.com\\/api\\/docs\",\"notice\":\"Try out our new API v.3: https:\\/\\/3xpl.com\\/data\"},\"servers\":\"API4,BTC5,BTC5,BTC5\",\"time\":0.009490013122558594,\"render_time\":0.003835916519165039,\"full_time\":0.013325929641723633,\"request_cost\":1}}";
                List<BlockRaw> missingBlock34 = ReadBlocksFromJson(jsonBlock34);

                List < BlockRaw > missingBlocks = new List<BlockRaw>();
                missingBlocks.AddRange(missingBlock32);
                missingBlocks.AddRange(missingBlock33);
                missingBlocks.AddRange(missingBlock34);

                // 30 00000000bc919cfb64f62de736d55cf79e3d535b474ace256b4fbb56073f64db
                // 31 000000009700ff3494f215c412cd8c0ceabf1deb0df03ce39bcfc223b769d3c4
                // 32 00000000e5cb7c6c273547b0c9421b01e23310ed83f934b96270f35a4d66f6e3
                // 33 00000000a87073ea3d7af299e02a434598b9c92094afa552e0711afcc0857962
                // 34 00000000a73fb23b6c42b18b3253ed29c5d0c80d84624efa12c2cf05c4b4318f

                foreach (var f in missingBlocks)
                {
                    Console.WriteLine("0" + "***  " + f.GetUnixTime() + " " + f.GetPrevBlockHash().Substring(30) + " hash " + f.DisplayHash.Substring(30));

                }


                int currentIndex = 0;
                string prevHash = "";
                while (currentIndex < 111)
                {

                    foundByIndex = FindBlockByPosition(@"C:\btcblock\claudeblocks", 0, currentIndex, out scannedByIndex);
                    if (foundByIndex!.DisplayHash == "00000000e5cb7c6c273547b0c9421b01e23310ed83f934b96270f35a4d66f6e3" ||
                        foundByIndex!.DisplayHash == "00000000a87073ea3d7af299e02a434598b9c92094afa552e0711afcc0857962" ||
                        foundByIndex!.DisplayHash == "00000000a73fb23b6c42b18b3253ed29c5d0c80d84624efa12c2cf05c4b4318f")
                    {
                        Console.WriteLine(currentIndex + "***  " + foundByIndex.GetUnixTime() +  " " + foundByIndex.GetPrevBlockHash().Substring(30) + " hash " + foundByIndex.DisplayHash.Substring(30));
                        if(missingBlock32.First()! == foundByIndex!)
                        {
                            Console.WriteLine("match block 32");

                        }
                    }
                    else
                    {
                        if(prevHash != foundByIndex.GetPrevBlockHash())
                        {
                            Console.WriteLine(currentIndex + "     " + foundByIndex.GetUnixTime() + " " + foundByIndex.GetPrevBlockHash().Substring(30) + " hash " + foundByIndex.DisplayHash.Substring(30));
                        }
                        

                    }
                    prevHash = foundByIndex.DisplayHash;
                    currentIndex++;

                }




                if (true)
                {
                    // assumes blocks in order

                    int currentIndex2 = 0;
                    BlockRaw prevFoundByIndex3 = null;
                    while (currentIndex2 < 10)
                    {

                        foundByIndex = FindBlockByPosition("C:\\btcblock\\claudeblocks", 0, currentIndex2, out scannedByIndex);
                        if (foundByIndex == null) break;      // file holds fewer blocks than this

                       
                                string h = foundByIndex.GetPrevBlockHash().Substring(40); 
                                Console.WriteLine(currentIndex2 + "     " + h  + " " + foundByIndex.DisplayHash.Substring(40));

                        currentIndex2++;
                    }
                }









                Console.WriteLine("Longest Chain Harness");

                List<MyRawBlock<BlockRaw>> rawBlocks = new List<MyRawBlock<BlockRaw>>();

                // One pass over the file for all of them. Asking FindBlockByPosition for block 0,
                // then block 1, and so on re-walks the file from the start every time, which is
                // where the harness was spending its time - and it cannot run off the end here,
                // since the file itself says how many blocks there are.
                var readClock = Stopwatch.StartNew();
                List<BlockRaw> allBlocks = ReadAllBlocks(@"C:\btcblock\claudeblocks", 0);
                Console.WriteLine("blk00000.dat holds " + allBlocks.Count + " blocks, read in "
                                  + readClock.Elapsed.TotalSeconds.ToString("F1") + "s");

                foreach (BlockRaw block in allBlocks)
                {
                    rawBlocks.Add(new MyRawBlock<BlockRaw>
                    {
                        hash = block.DisplayHash.Substring(40),
                        prevHash = block.GetPrevBlockHash().Substring(40),
                        data = block
                    });
                }

                ChainState<BlockRaw> state = new ChainState<BlockRaw>();

                foreach (var rawBlock in rawBlocks)
                {
                    //Console.WriteLine($"Raw Block: Hash={rawBlock.hash}, PrevHash={rawBlock.prevHash}, Data={DescribeData(rawBlock.data)}");
                    BuildLongestChain(rawBlock, state);
                }

                int deleted = PruneShortForks(state, 3);

                SetNextLinks(state);

                MyBlock<BlockRaw>? currentBlock = state.blockZero;
                string? prevhash = null;
                while (currentBlock != null)
                {
                    Console.WriteLine("height " + currentBlock.height + " " + currentBlock.hash);
                    if(prevhash != null && prevhash != currentBlock.prevHash)
                    {
                        Console.WriteLine("error: prevhash " + prevhash + " does not match currentBlock.prevHash " + currentBlock.prevHash);
                        throw new Exception("prevhash mismatch");
                    }
                    prevhash = currentBlock.hash;
                    currentBlock = currentBlock.nextLink;
                }

                MyBlock<BlockRaw>? currBlock = state.blockZero;
                while (currBlock != null)
                {
                    //Console.WriteLine(currBlock.hash + " -> " + currBlock.prevHash);
                    currBlock = currBlock.nextLink;
                }


                ReportState(state);








                /*


                //string blockDataDirectory = "C:\\btcblock\\mostblocks11_zeroxor";
                string blockDataDirectory =           "C:\\btcblock\\claudeblocksRetryLane\\";


                BlockRaw? prevFoundByIndex = null;
                currentIndex = 0;
                int written = 0;


                // Blocks arrive in file order, which is arrival order - a block's parent is usually
                // somewhere else entirely. The assembler parks whatever does not connect yet,
                // tracks cumulative work per branch, and only writes a block once it is 50 deep
                // behind the heaviest tip, at which point a competing branch would need 50 blocks
                // of its own to take it back.
                var assembler = new ChainAssembler("C:\\btcblock\\claudeblocksRetryLane\\blk00000.dat",
                                                   confirmationDepth: 50, maxPending: 1000);

                while (currentIndex < 2600)
                {
                    foundByIndex = FindBlockByPosition("C:\\btcblock\\claudeblocksRetryLane", 0, currentIndex, out scannedByIndex);
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



                    // block 500 0000000047560030cea942ff993f9c5464dd6499e7118d189c56ca57a465bcb7

                    //int scannedByIndex2=2;
               // BlockRaw? tqfFoundByIndex = FindBlockByPosition("C:\\btcblock\\inOrder2\\", 0, 500, out scannedByIndex2);



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
                }*/
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
            Console.Error.WriteLine("time        : " + b.GetTimestamp() + "  (unix " + b.GetUnixTime() + ")");
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

            public string timestamp = ""; //68–71	e3c86849 Timestamp   Reverses to 0x4968c8e3 = 1,231,603,939 = Jan 10, 2009 16:12:19 UTC

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
            /// The header's timestamp in unix seconds, from bytes 68..71. Those four bytes are
            /// little endian on disk, so e3c86849 reads back as 0x4968c8e3 = 1231603939.
            /// </summary>
            public uint GetUnixTime()
            {
                if (Raw.Length < 80)
                {
                    throw new InvalidOperationException("block is only " + Raw.Length
                                                        + " bytes, too short to hold an 80 byte header");
                }

                return BinaryPrimitives.ReadUInt32LittleEndian(Raw.AsSpan(68, 4));
            }

            /// <summary>The same instant as a UTC DateTime: 1231603939 -> 2009-01-10 16:12:19.</summary>
            public DateTime GetTimeUtc()
            {
                return DateTimeOffset.FromUnixTimeSeconds(GetUnixTime()).UtcDateTime;
            }

            /// <summary>
            /// Reads the timestamp out of the header into timestamp and hands it back, the way
            /// GetPrevBlockHashBytes fills previousHash. Format is "2009-01-10 16:12:19Z".
            ///
            /// This is the miner's own clock, not a verified time - it only has to sit above the
            /// median of the last 11 blocks and inside a couple of hours of network time, so it
            /// does not always increase from one block to the next.
            /// </summary>
            public string GetTimestamp()
            {
                timestamp = GetTimeUtc().ToString("u");
                return timestamp;
            }

            /// <summary>
            /// Fills the fields that are just the header read back out - timestamp and
            /// previousHash - so they hold something from the moment the block is built, rather
            /// than staying empty until someone happens to call the matching getter.
            /// </summary>
            public void SetHeaderFields()
            {
                GetTimestamp();
                GetPrevBlockHash();
            }

            /// <summary>
            /// Value equality: two blocks are equal when they are the same block carrying the same
            /// bytes. This is what lets a by-hash result and a by-position result be compared with
            /// == and != - without it they are two separate objects and every comparison reports a
            /// mismatch even when they found the same block.
            ///
            /// Where the block was found - Path, BlockIndex, Offset - is deliberately not compared:
            /// the same block in two different files is still the same block.
            /// </summary>
            public bool Equals(BlockRaw? other)
            {
                if (other is null) return false;

                if (ReferenceEquals(this, other))
                {
                    throw new Exception("ReferenceEquals(this, other) is true");
                    return true;
                }

                //if (BlockIndex != other.BlockIndex) return false;
                //if (Offset != other.Offset) return false;
                //if (!string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase)) return false;

                if (Size != other.Size) return false;
                if (!string.Equals(DisplayHash, other.DisplayHash, StringComparison.Ordinal)) return false;
                if (!string.Equals(GetPrevBlockHash(), other.GetPrevBlockHash(), StringComparison.Ordinal)) return false;

                // Every check above compares the 80-byte header, one way or another - so two blocks
                // can pass all of them and still hold different bytes, because everything past byte
                // 80 is transactions. Only this last comparison covers the whole block.
                if (Raw.Length != other.Raw.Length) return false;
                if (!Raw.AsSpan().SequenceEqual(other.Raw))
                {
                    // Same hash, same length, different bytes. The hash only commits to the header,
                    // so this means the transactions differ: either a witness-serialized copy next
                    // to a stripped one, or one of the two is corrupt. Worth saying out loud rather
                    // than quietly reporting "not equal".
                    //Console.Error.WriteLine("warning: " + DisplayHash + " has the same header in "
                                            //+ Path + " and " + other.Path + " but different body bytes");
                    return false;
                }

                return true;
            }

            public override bool Equals(object? obj)
            {
                return Equals(obj as BlockRaw);
            }

            public override int GetHashCode()
            {
                // Only the fields Equals still compares. Equal objects have to hash the same, so
                // Path / BlockIndex / Offset cannot be in here while Equals ignores them - the same
                // block found in two files is equal, and would otherwise land in two different
                // buckets of a Dictionary or HashSet and never be found again.
                //
                // Raw is skipped on purpose: hashing megabytes to look one block up is not worth
                // it, and DisplayHash already commits to the header.
                return HashCode.Combine(DisplayHash, Size);
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

                var imported = new BlockRaw
                {
                    Path = "json:" + entry.Name,     // it came from a response, not a blk file
                    BlockIndex = -1,
                    Offset = -1,
                    Size = raw.Length,
                    Raw = raw,
                    DisplayHash = hash,
                };
                imported.SetHeaderFields();
                blocks.Add(imported);

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

        /// <summary>Where a record sits in the file, and when its miner said it was made.</summary>
        sealed class TimestampedRecord
        {
            public uint UnixTime;
            public long Offset;
            public int Size;
            public string Hash = "";      // only filled when the caller asked for it
        }

        /// <summary>
        /// One header-only pass over a blk file: where every record sits and when it says it was
        /// made. Block bodies are seeked over. Hashing each header is optional, since it is only
        /// worth the cost when the caller intends to show the hashes.
        /// </summary>
        static List<TimestampedRecord> ReadTimestampedRecords(string path, byte[] key, bool withHashes)
        {
            var records = new List<TimestampedRecord>();

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            byte[] header80 = new byte[80];

            foreach (var record in EnumerateRecords(fs, key))
            {
                ReadBlockHeader(fs, record.Offset, key, header80);

                var entry = new TimestampedRecord
                {
                    UnixTime = BinaryPrimitives.ReadUInt32LittleEndian(header80.AsSpan(68, 4)),
                    Offset = record.Offset,
                    Size = record.Size,
                };

                if (withHashes) entry.Hash = ToDisplayHex(DoubleSha256(header80, 0, 80));
                records.Add(entry);
            }

            return records;
        }

        /// <summary>
        /// Prints the earliest and the latest timestamps in a blk-format file - the smallest and
        /// largest `count` of them, each list in increasing order, with the gap from the entry
        /// before it.
        ///
        /// The gaps are the point. Across the smallest they show how thinly the early chain was
        /// mined; across the largest they show where the file's coverage runs out. A gap can also
        /// come out negative, because a miner's clock only has to beat the median of the last 11
        /// blocks - so timestamps in a perfectly ordered chain still step backwards now and then.
        ///
        /// When the file holds fewer blocks than the two lists would cover, every timestamp is
        /// printed once instead of printing an overlapping head and tail.
        /// </summary>
        public static void PrintTimestampExtremes(string path, int count = 30)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("no such block file: " + path, path);
            if (count < 1)
                throw new ArgumentOutOfRangeException(nameof(count), "ask for at least one timestamp");

            string directory = Path.GetDirectoryName(path) ?? ".";
            byte[] key = ReadXorKey(directory);

            List<TimestampedRecord> records = ReadTimestampedRecords(path, key, withHashes: true);

            Console.WriteLine();
            Console.WriteLine(Path.GetFileName(path) + ": " + records.Count + " blocks");
            if (records.Count == 0) return;

            // Stable, so blocks sharing a timestamp stay in the order the file has them.
            List<TimestampedRecord> byTime = records.OrderBy(r => r.UnixTime).ToList();

            if (byTime.Count <= count * 2)
            {
                PrintTimestampRun("all " + byTime.Count + " timestamps, in order", byTime);
                return;
            }

            PrintTimestampRun(count + " smallest timestamps", byTime.GetRange(0, count));
            PrintTimestampRun(count + " largest timestamps", byTime.GetRange(byTime.Count - count, count));
        }

        static void PrintTimestampRun(string title, List<TimestampedRecord> run)
        {
            Console.WriteLine();
            Console.WriteLine(title);
            Console.WriteLine("   #  timestamp             unix        gap from previous   block");

            for (int i = 0; i < run.Count; i++)
            {
                string gap = "-";
                if (i > 0)
                {
                    gap = FormatGap((long)run[i].UnixTime - run[i - 1].UnixTime);
                }

                Console.WriteLine("  " + (i + 1).ToString().PadLeft(2) + "  "
                                  + DateTimeOffset.FromUnixTimeSeconds(run[i].UnixTime).UtcDateTime.ToString("u") + "  "
                                  + run[i].UnixTime.ToString().PadRight(11)
                                  + " " + gap.PadRight(19)
                                  + run[i].Hash.Substring(30));
            }
        }

        /// <summary>
        /// A signed gap as +d hh:mm:ss. Negative is not an error - it is a miner whose clock ran
        /// behind the block before it.
        /// </summary>
        static string FormatGap(long seconds)
        {
            string sign = "+";
            long absolute = seconds;
            if (seconds < 0)
            {
                sign = "-";
                absolute = -seconds;
            }

            TimeSpan span = TimeSpan.FromSeconds(absolute);
            if (span.Days > 0)
            {
                return sign + span.Days + "d " + span.ToString(@"hh\:mm\:ss");
            }
            return sign + span.ToString(@"hh\:mm\:ss");
        }

        static bool BytesEqual(byte[]? a, byte[]? b)
        {
            if (ReferenceEquals(a, b)) return true;      // both null, or literally the same array
            if (a is null || b is null) return false;
            return a.AsSpan().SequenceEqual(b);
        }

        /// <summary>
        /// Rewrites one blk-format file with its blocks in increasing timestamp order - the same
        /// value BlockRaw.timestamp holds, read straight out of bytes 68..71 of each header.
        ///
        /// The file is read twice and never edited in place: pass one reads only the 80-byte
        /// headers to collect (timestamp, offset, size), pass two copies the blocks into a
        /// temporary file in sorted order, and only once that file is complete does it replace the
        /// original. An interrupted run leaves the original untouched.
        ///
        /// Blocks sharing a timestamp keep the order they were already in, and a file that is
        /// already sorted is left alone rather than rewritten. If the directory has a non-zero
        /// xor.dat, blocks are re-masked at their new offsets, since the key is applied by
        /// absolute file position.
        ///
        /// Returns the number of blocks in the file.
        ///
        /// CAUTION - timestamp order is not chain order. A block's timestamp is the miner's own
        /// clock: it only has to beat the median of the last 11 blocks and stay within about two
        /// hours of network time, so a block can legitimately carry an earlier timestamp than its
        /// parent. Sorting by it gets close to height order and then quietly disagrees in places.
        /// For an exactly ordered file, follow the prev-hash links with WriteChainOrdered.
        /// </summary>
        public static int SortBlockFileByTimestamp(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("no such block file: " + path, path);

            string directory = Path.GetDirectoryName(path) ?? ".";
            byte[] key = ReadXorKey(directory);

            // Pass one: locate every record and read its timestamp. Bodies are seeked over, and
            // the headers are not hashed - nothing here needs the hashes.
            List<TimestampedRecord> records = ReadTimestampedRecords(path, key, withHashes: false);

            if (records.Count == 0) return 0;

            // since we are adding blocks for debugging never assume alreadySorted
            bool alreadySorted = false;
            for (int i = 1; i < records.Count; i++)
            {
                if (records[i].UnixTime < records[i - 1].UnixTime)
                {
                    alreadySorted = false;
                    break;
                }
            }
            alreadySorted = false;
            if (alreadySorted)
            {
                Console.Error.WriteLine(Path.GetFileName(path) + ": already in timestamp order, left alone");
                return records.Count;
            }

            // OrderBy is a stable sort, so blocks sharing a timestamp keep their existing order.
            List<TimestampedRecord> sorted = records.OrderBy(r => r.UnixTime).ToList();

            // Pass two: copy the blocks out in order. Writing to a temporary file rather than over
            // the original means a crash here costs nothing.
            string temp = path + ".sorting.tmp";

            int count = 0;
            using (var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            using (var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                foreach (TimestampedRecord r in sorted)
                {
                    /*if (count++ == 38)
                    {
                        Console.WriteLine("go");
                        byte[] k = ReadBlockBytes(src, r.Offset, r.Size, key);
                        byte[] block33raw = Convert.FromHexString("01000000c4d369b723c2cf9be33cf00deb1dbfea0c8ccd12c415f29434ff009700000000c9c0fd0ae7b7973c42fc9e3dddc967b6e309570b720ff15414c08365f005992be3c56849ffff001d08e1c00d0101000000010000000000000000000000000000000000000000000000000000000000000000ffffffff0704ffff001d0136ffffffff0100f2052a01000000434104b949980bb46aee11510519b4af0dfcc3cc7464b3ede15f184b7c8126a98bf6d6e698eaf16b938814174a002ba24daa03e59a7c0927248517b581c09ec70f216eac00000000");
                        if (BytesEqual(k, block33raw))
                        {
                            throw new Exception("bad");
                        }
                    }
                    if(count == 33)// writes the fake one before real 33
                    {
                        Console.WriteLine("write fake block33");

                        string fake_block_33_is_22_byteslongerthan_real_block33 = "01000000e3f6664d5af37062b934f983ed1033e2011b42c9b04735276c7ccbe50000000033c56986d991564d8f2e5d6b3b98105c882a5b108738d0994407de8b72935ac4efc86849ffff001df9649d460101000000010000000000000000000000000000000000000000000000000000000000000000ffffffff1d04ffff001d12414c5433332f464f524b2d464958545552450400000000ffffffff0100f2052a01000000434104804d71f6a91c908a973cae7ef4363f7689520116b995d6936328de00be56f92baee0dabf3a240e0ed2dce7f374f12cbba7649808528236cb04c558f028dd61edac00000000";
                        WriteRecordWithKey(dst, Convert.FromHexString(fake_block_33_is_22_byteslongerthan_real_block33), key);
                    }*/
                    WriteRecordWithKey(dst, ReadBlockBytes(src, r.Offset, r.Size, key), key);
                    
                    
                    
                    
               
                    
                    //if(count > 1000) // trevor todo remove this limit, just for testing
                    //{ break; }








                }
                dst.Flush();
            }

            File.Move(temp, path, overwrite: true);

            // why user Error here Claude?
            //Everything that is commentary about the run — the field dump at lines 592–605, "not found", the reorder summary at 1512 — goes to Console.Error so it stays on the terminal and never contaminates that redirected file. Same convention as curl, dd, or ffmpeg: stdout carries data, stderr carries progress and diagnostics. Both interleave normally when nobody redirects, so you lose nothing in interactive use.
            //Two other properties that matter for the line you asked about:
            //-Console.Error is auto - flushed, Console.Out is not necessarily.If the process throws partway through a reorder, the "rewrote N blocks" messages already emitted are guaranteed to have surfaced.
            //- It's a status line, not a result. The caller gets sorted.Count as the return value at line 1515; the text is for a human watching.
            //That said, this file isn't consistent about it. The --help text (566), the argument-error message (157), and the "no arguments" notice (464) are also on stderr, which is right, but the tables at 1353–1384 and the summaries at 375–394 use Console.WriteLine — stdout — even though they're equally diagnostic.And lines 1488 / 1498("go", "write fake block33") are breakpoint bait on stdout.If you ever pipe the hex output for real, those would need moving to stderr.
                        Console.Error.WriteLine(Path.GetFileName(path) + ": rewrote " + sorted.Count
                                    + " blocks in timestamp order ("
                                    + DateTimeOffset.FromUnixTimeSeconds(sorted[0].UnixTime).UtcDateTime.ToString("u")
                                    + " to "
                                    + DateTimeOffset.FromUnixTimeSeconds(sorted[sorted.Count - 1].UnixTime).UtcDateTime.ToString("u")
                                    + ")");

            return sorted.Count;
        }

        /// <summary>
        /// Writes one record, re-masking it with the directory's xor key when there is one. The key
        /// is applied by absolute file position, so bytes landing at a new offset have to be
        /// masked for that offset - and since XOR is its own inverse, Deobfuscate does it.
        /// </summary>
        static void WriteRecordWithKey(FileStream fs, byte[] rawBlock, byte[] key)
        {
            if (key.Length == 0)
            {
                WriteRecord(fs, rawBlock);
                return;
            }

            long at = fs.Position;
            byte[] record = new byte[8 + rawBlock.Length];
            Magic.CopyTo(record, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4, 4), (uint)rawBlock.Length);
            rawBlock.CopyTo(record, 8);

            Deobfuscate(record, 0, record.Length, at, key);
            fs.Write(record, 0, record.Length);
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

                    var found = new BlockRaw
                    {
                        Path = path,
                        BlockIndex = index,
                        Offset = record.Offset,
                        Size = record.Size,
                        Raw = raw,
                        DisplayHash = ToDisplayHex(DoubleSha256(raw, 0, 80)),
                    };
                    found.SetHeaderFields();
                    return found;
                }
            }

            return null;
        }

        /// <summary>
        /// Every block in a blk-format file, in file order, read in a single pass.
        ///
        /// This is the bulk alternative to FindBlockByPosition. That one restarts at offset 0 on
        /// every call, so walking a whole file with it costs N*(N+1)/2 record steps - for the
        /// 120,006 blocks in a 128 MiB file, about 7.2 billion, plus a file handle per block. This
        /// walks the record chain once: N steps, one handle. The order is identical, so
        /// ReadAllBlocks(dir, file)[i] is the block FindBlockByPosition(dir, file, i) returns, and
        /// Count is what CountBlocksInFile reports.
        ///
        /// What it costs instead is memory: every block's bytes are held at once, so a full 128 MiB
        /// file means 128 MiB of Raw arrays plus the BlockRaw objects and hash strings around them.
        /// Use EnumerateAllBlocks when only one block at a time is needed.
        /// </summary>
        public static List<BlockRaw> ReadAllBlocks(string path)
        {
            return new List<BlockRaw>(EnumerateAllBlocks(path));
        }

        /// <summary>
        /// The same, for a file named the way FindBlockByHash and FindBlockByPosition name one.
        /// </summary>
        public static List<BlockRaw> ReadAllBlocks(string directory, int fileIndex)
        {
            return ReadAllBlocks(BlkFilePath(directory, fileIndex));
        }

        /// <summary>
        /// Every block in a blk-format file, handed over one at a time as the file is walked, so a
        /// caller that processes and drops each block never holds more than one. The file stays
        /// open for the length of the enumeration - finish it, or dispose the enumerator, before
        /// rewriting the file.
        /// </summary>
        public static IEnumerable<BlockRaw> EnumerateAllBlocks(string path)
        {
            // Checked out here rather than in the iterator below, where it would not run until the
            // first MoveNext and a missing file would surface at the foreach instead of the call.
            if (!File.Exists(path))
                throw new FileNotFoundException("no such block file: " + path, path);

            return EnumerateAllBlocksCore(path);
        }

        /// <summary>The same, by directory and blk file index.</summary>
        public static IEnumerable<BlockRaw> EnumerateAllBlocks(string directory, int fileIndex)
        {
            return EnumerateAllBlocks(BlkFilePath(directory, fileIndex));
        }

        static IEnumerable<BlockRaw> EnumerateAllBlocksCore(string path)
        {
            string directory = Path.GetDirectoryName(path) ?? ".";
            byte[] key = ReadXorKey(directory);

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20);

            int index = 0;
            foreach (var record in EnumerateRecords(fs, key))
            {
                byte[] raw = ReadBlockBytes(fs, record.Offset, record.Size, key);

                var block = new BlockRaw
                {
                    Path = path,
                    BlockIndex = index,
                    Offset = record.Offset,
                    Size = record.Size,
                    Raw = raw,
                    DisplayHash = ToDisplayHex(DoubleSha256(raw, 0, 80)),
                };
                block.SetHeaderFields();

                yield return block;
                index++;
            }
        }

        /// <summary>
        /// How many blocks a blk-format file holds. Only the 8-byte record headers are read - the
        /// blocks themselves are seeked over and nothing is hashed - so a 128 MiB file is counted
        /// in a fraction of a second.
        ///
        /// This is a count of records, which is exactly what FindBlockByPosition indexes into: the
        /// last block in the file sits at position Count - 1. It is not a height, and a block that
        /// appears twice in the file is counted twice.
        ///
        /// A missing file throws rather than counting zero - zero is what an empty file, or one
        /// holding nothing but Core's pre-allocated padding, comes back with.
        /// </summary>
        public static int CountBlocksInFile(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("no such block file: " + path, path);

            string directory = Path.GetDirectoryName(path) ?? ".";
            byte[] key = ReadXorKey(directory);

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20);

            int count = 0;
            foreach (var _ in EnumerateRecords(fs, key))
            {
                count++;
            }
            return count;
        }

        /// <summary>
        /// The same count, for a file named the way FindBlockByHash and FindBlockByPosition name
        /// one: the directory holding the blk files plus the file's index, so 0 is blk00000.dat.
        /// </summary>
        public static int CountBlocksInFile(string directory, int fileIndex)
        {
            return CountBlocksInFile(BlkFilePath(directory, fileIndex));
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
