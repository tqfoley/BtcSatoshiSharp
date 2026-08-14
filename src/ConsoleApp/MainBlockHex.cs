using Microsoft.Data.Sqlite;
using Org.BouncyCastle.Utilities;
using Org.BouncyCastle.Utilities.Encoders;
using RocksDbSharp;
using SatoshiSharpLib;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Drawing;
using System.Numerics;
using System.Runtime.ConstrainedExecution;
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
    /// 

    public class MainBlockHex
    {

        public static string btcBlocksDirectory = "C:\\btcblock\\notclaudeblocks";//"/Users/trevorfoley/Documents/blocks"; //"C:\\btcblock\\claudeblocks"

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

                if (index >= count)
                {
                    break;
                }
            }

            return -1;
        }


        public static int MainBlockHex2(string[] args)
        {
            bool rocksDbLoaded = false;
            if (false)
            {
                Console.WriteLine("rocksdb not loaded");

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
                    /*Request reqBlock3ByHash = new Request();
                    reqBlock3ByHash.FileIndex = 0;
                    reqBlock3ByHash.Hash = Block3Hash;

                    Request reqBlock3ByIndex = new Request();
                    reqBlock3ByIndex.FileIndex = 0;
                    reqBlock3ByIndex.BlockIndex = 3;


                    int scanned;
                    int scannedByHash;
                    int scannedByIndex;
                    BlockRaw? foundByHash = FindBlockByHash(btcBlocksDirectory, reqBlock3ByHash.FileIndex, reqBlock3ByHash.Hash, out scannedByHash);
                    BlockRaw? foundByIndex = FindBlockByPosition(btcBlocksDirectory, reqBlock3ByIndex.FileIndex, reqBlock3ByIndex.BlockIndex, out scannedByIndex);


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
                    }*/


                    //PrintTimestampExtremes(@"C:\btcblock\claudeblocks\blk00001.dat", 10);
                    //PrintTimestampExtremes(@"C:\btcblock\claudeblocks\blk00002.dat", 10);
                    //PrintTimestampExtremes(@"C:\btcblock\claudeblocks\blk00003.dat", 10);
                    //PrintTimestampExtremes(@"C:\btcblock\claudeblocks\blk00004.dat", 10);
                    int MAXBLKDATFILE = 41; // block to read      1 for 100000    22 for 200000

                    // read all blk file and print the highest timestamp and lowest for each file and print it out
                    PrintTimestampRangePerFile(btcBlocksDirectory, MAXBLKDATFILE);


                    // order ALL .dat files by timestamp
                    if (false)
                    {
                        var files = new List<string>();
                        foreach (string file in Directory.EnumerateFiles(btcBlocksDirectory, "blk*.dat"))
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
                        //DeleteAllFilesIn("C:\\btcblock\\inOrder\\");
                    }
                    /*

                    //claude's fake block 33 raw date 01000000e3f6664d5af37062b934f983ed1033e2011b42c9b04735276c7ccbe50000000033c56986d991564d8f2e5d6b3b98105c882a5b108738d0994407de8b72935ac4efc86849ffff001df9649d460101000000010000000000000000000000000000000000000000000000000000000000000000ffffffff1d04ffff001d12414c5433332f464f524b2d464958545552450400000000ffffffff0100f2052a01000000434104804d71f6a91c908a973cae7ef4363f7689520116b995d6936328de00be56f92baee0dabf3a240e0ed2dce7f374f12cbba7649808528236cb04c558f028dd61edac00000000
                    //claude's fake block 33 hash    0000000096a151f27d9cd2d706b6b8e16ba43e7e290bbb77f9eff8fe1d20c66c parent  00000000e5cb7c6c273547b0c9421b01e23310ed83f934b96270f35a4d66f6e3   â† identical to real block 33 time    1231603951(12s after the real block) nonce   1184720121 bits    1d00ffff(unchanged â€” difficulty is consensus -fixed in this epoch)
                    //01234567 123456789012345678901234567890123456789012345678901234567890123 123456789012345678901234567890123456789012345678901234567890123                                                                                                                            01234567890123456789012345678901234567890123
                    string fakeBlock33 = "{\"data\":{\"33\":{\"raw_block\":\"01000000e3f6664d5af37062b934f983ed1033e2011b42c9b04735276c7ccbe50000000033c56986d991564d8f2e5d6b3b98105c882a5b108738d0994407de8b72935ac4efc86849ffff001df9649d460101000000010000000000000000000000000000000000000000000000000000000000000000ffffffff1d04ffff001d12414c5433332f464f524b2d464958545552450400000000ffffffff0100f2052a01000000434104804d71f6a91c908a973cae7ef4363f7689520116b995d6936328de00be56f92baee0dabf3a240e0ed2dce7f374f12cbba7649808528236cb04c558f028dd61edac00000000\",\"decoded_raw_block\":{\"hash\":\"0000000096a151f27d9cd2d706b6b8e16ba43e7e290bbb77f9eff8fe1d20c66c\",\"confirmations\":-1,\"height\":33,\"version\":1,\"versionHex\":\"00000001\",\"merkleroot\":\"c45a93728bde074499d03887105b2a885c10983b6b5d2e8f4d5691d98669c533\",\"time\":1231603951,\"mediantime\":1231601457,\"nonce\":1184720121,\"bits\":\"1d00ffff\",\"difficulty\":1,\"chainwork\":\"0000000000000000000000000000000000000000000000000000002200220022\",\"previousblockhash\":\"00000000e5cb7c6c273547b0c9421b01e23310ed83f934b96270f35a4d66f6e3\",\"strippedsize\":237,\"size\":237,\"weight\":948,\"nTx\":1,\"tx\":[\"c45a93728bde074499d03887105b2a885c10983b6b5d2e8f4d5691d98669c533\"]}}},\"context\":{\"code\":200,\"source\":\"SYNTHETIC\",\"results\":1,\"state\":960939,\"market_price_usd\":63703,\"cache\":{\"live\":false,\"duration\":120,\"since\":\"2026-08-04 03:37:36\",\"until\":\"2026-08-04 03:39:36\",\"time\":null},\"api\":{\"version\":\"2.0.95-ie\",\"last_major_update\":\"2022-11-07 02:00:00\",\"next_major_update\":\"2023-11-12 02:00:00\",\"documentation\":\"https://blockchair.com/api/docs\",\"notice\":\"SYNTHETIC FIXTURE - not a historical block and not served by any explorer. Locally mined competitor to block 33 for fork / stale-tip detection testing.\"},\"servers\":\"SYNTHETIC\",\"time\":0.006392955780029297,\"render_time\":0.0043070316314697266,\"full_time\":0.010699987411499023,\"request_cost\":1}}";
                    string jsonBlock33 = "{\"data\":{\"33\":{\"raw_block\":\"01000000e3f6664d5af37062b934f983ed1033e2011b42c9b04735276c7ccbe5000000001012aaab3e3bffd34055aaa157bf78792d5c18f085635eda7046d89c08a0eabde3c86849ffff001d228c22400101000000010000000000000000000000000000000000000000000000000000000000000000ffffffff0704ffff001d0138ffffffff0100f2052a01000000434104804d71f6a91c908a973cae7ef4363f7689520116b995d6936328de00be56f92baee0dabf3a240e0ed2dce7f374f12cbba7649808528236cb04c558f028dd61edac00000000\",\"decoded_raw_block\":{\"hash\":\"00000000a87073ea3d7af299e02a434598b9c92094afa552e0711afcc0857962\",\"confirmations\":960926,\"height\":33,\"version\":1,\"versionHex\":\"00000001\",\"merkleroot\":\"bdeaa0089cd84670da5e6385f0185c2d7978bf57a1aa5540d3ff3b3eabaa1210\",\"time\":1231603939,\"mediantime\":1231601457,\"nonce\":1076005922,\"bits\":\"1d00ffff\",\"difficulty\":1,\"chainwork\":\"0000000000000000000000000000000000000000000000000000002200220022\",\"nTx\":1,\"previousblockhash\":\"00000000e5cb7c6c273547b0c9421b01e23310ed83f934b96270f35a4d66f6e3\",\"nextblockhash\":\"00000000a73fb23b6c42b18b3253ed29c5d0c80d84624efa12c2cf05c4b4318f\",\"strippedsize\":215,\"size\":215,\"weight\":860,\"tx\":[\"bdeaa0089cd84670da5e6385f0185c2d7978bf57a1aa5540d3ff3b3eabaa1210\"]}}},\"context\":{\"code\":200,\"source\":\"T+R\",\"results\":1,\"state\":960939,\"market_price_usd\":63703,\"cache\":{\"live\":true,\"duration\":120,\"since\":\"2026-08-04 03:37:36\",\"until\":\"2026-08-04 03:39:36\",\"time\":null},\"api\":{\"version\":\"2.0.95-ie\",\"last_major_update\":\"2022-11-07 02:00:00\",\"next_major_update\":\"2023-11-12 02:00:00\",\"documentation\":\"https:\\/\\/blockchair.com\\/api\\/docs\",\"notice\":\"Try out our new API v.3: https:\\/\\/3xpl.com\\/data\"},\"servers\":\"API4,BTC5,BTC5,BTC5\",\"time\":0.006392955780029297,\"render_time\":0.0043070316314697266,\"full_time\":0.010699987411499023,\"request_cost\":1}}";
                    // https://api.blockchair.com/bitcoin/raw/block/33
                    List<BlockRaw> missingBlock33 = ReadBlocksFromJson(jsonBlock33);

                    string jsonBlock32 = "{\"data\":{\"32\":{\"raw_block\":\"01000000c4d369b723c2cf9be33cf00deb1dbfea0c8ccd12c415f29434ff009700000000c9c0fd0ae7b7973c42fc9e3dddc967b6e309570b720ff15414c08365f005992be3c56849ffff001d08e1c00d0101000000010000000000000000000000000000000000000000000000000000000000000000ffffffff0704ffff001d0136ffffffff0100f2052a01000000434104b949980bb46aee11510519b4af0dfcc3cc7464b3ede15f184b7c8126a98bf6d6e698eaf16b938814174a002ba24daa03e59a7c0927248517b581c09ec70f216eac00000000\",\"decoded_raw_block\":{\"hash\":\"00000000e5cb7c6c273547b0c9421b01e23310ed83f934b96270f35a4d66f6e3\",\"confirmations\":961020,\"height\":32,\"version\":1,\"versionHex\":\"00000001\",\"merkleroot\":\"2b9905f06583c01454f10f720b5709e3b667c9dd3d9efc423c97b7e70afdc0c9\",\"time\":1231603171,\"mediantime\":1231570573,\"nonce\":230744328,\"bits\":\"1d00ffff\",\"difficulty\":1,\"chainwork\":\"0000000000000000000000000000000000000000000000000000002100210021\",\"nTx\":1,\"previousblockhash\":\"000000009700ff3494f215c412cd8c0ceabf1deb0df03ce39bcfc223b769d3c4\",\"nextblockhash\":\"00000000a87073ea3d7af299e02a434598b9c92094afa552e0711afcc0857962\",\"strippedsize\":215,\"size\":215,\"weight\":860,\"tx\":[\"2b9905f06583c01454f10f720b5709e3b667c9dd3d9efc423c97b7e70afdc0c9\"]}}},\"context\":{\"code\":200,\"source\":\"T+R\",\"results\":1,\"state\":961051,\"market_price_usd\":64049,\"cache\":{\"live\":true,\"duration\":120,\"since\":\"2026-08-04 17:57:58\",\"until\":\"2026-08-04 17:59:58\",\"time\":null},\"api\":{\"version\":\"2.0.95-ie\",\"last_major_update\":\"2022-11-07 02:00:00\",\"next_major_update\":\"2023-11-12 02:00:00\",\"documentation\":\"https:\\/\\/blockchair.com\\/api\\/docs\",\"notice\":\"Try out our new API v.3: https:\\/\\/3xpl.com\\/data\"},\"servers\":\"API4,BTC5,BTC5,BTC5\",\"time\":0.01161813735961914,\"render_time\":0.0032088756561279297,\"full_time\":0.01482701301574707,\"request_cost\":1}}";
                    List<BlockRaw> missingBlock32 = ReadBlocksFromJson(jsonBlock32);

                    string jsonBlock34 = "{\"data\":{\"34\":{\"raw_block\":\"01000000627985c0fc1a71e052a5af9420c9b99845432ae099f27a3dea7370a80000000074549b3151d6dd4ce77419d01710921b3211ed3280bf2e3af2c1f1a820063b2272ca6849ffff001d2243c0240101000000010000000000000000000000000000000000000000000000000000000000000000ffffffff0704ffff001d0147ffffffff0100f2052a01000000434104180bfa57bff462c7641fa0b91efe29344a77086b073cd9c5f769cb2393acc151a4e7377eaabacc39f5b2bd2cd4bcb5ed1855939619e491c79c0bb5793d4edbf3ac00000000\",\"decoded_raw_block\":{\"hash\":\"00000000a73fb23b6c42b18b3253ed29c5d0c80d84624efa12c2cf05c4b4318f\",\"confirmations\":961018,\"height\":34,\"version\":1,\"versionHex\":\"00000001\",\"merkleroot\":\"223b0620a8f1c1f23a2ebf8032ed11321b921017d01974e74cddd651319b5474\",\"time\":1231604338,\"mediantime\":1231601503,\"nonce\":616579874,\"bits\":\"1d00ffff\",\"difficulty\":1,\"chainwork\":\"0000000000000000000000000000000000000000000000000000002300230023\",\"nTx\":1,\"previousblockhash\":\"00000000a87073ea3d7af299e02a434598b9c92094afa552e0711afcc0857962\",\"nextblockhash\":\"00000000b572a465b4e816420d47a16274557b3573b7924b64808a82c7322d9b\",\"strippedsize\":215,\"size\":215,\"weight\":860,\"tx\":[\"223b0620a8f1c1f23a2ebf8032ed11321b921017d01974e74cddd651319b5474\"]}}},\"context\":{\"code\":200,\"source\":\"T+R\",\"results\":1,\"state\":961051,\"market_price_usd\":64049,\"cache\":{\"live\":true,\"duration\":120,\"since\":\"2026-08-04 17:59:02\",\"until\":\"2026-08-04 18:01:02\",\"time\":null},\"api\":{\"version\":\"2.0.95-ie\",\"last_major_update\":\"2022-11-07 02:00:00\",\"next_major_update\":\"2023-11-12 02:00:00\",\"documentation\":\"https:\\/\\/blockchair.com\\/api\\/docs\",\"notice\":\"Try out our new API v.3: https:\\/\\/3xpl.com\\/data\"},\"servers\":\"API4,BTC5,BTC5,BTC5\",\"time\":0.009490013122558594,\"render_time\":0.003835916519165039,\"full_time\":0.013325929641723633,\"request_cost\":1}}";
                    List<BlockRaw> missingBlock34 = ReadBlocksFromJson(jsonBlock34);

                    List<BlockRaw> missingBlocks = new List<BlockRaw>();
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

                        foundByIndex = FindBlockByPosition(btcBlocksDirectory, 0, currentIndex, out scannedByIndex);
                        if (foundByIndex!.DisplayHash == "00000000e5cb7c6c273547b0c9421b01e23310ed83f934b96270f35a4d66f6e3" ||
                            foundByIndex!.DisplayHash == "00000000a87073ea3d7af299e02a434598b9c92094afa552e0711afcc0857962" ||
                            foundByIndex!.DisplayHash == "00000000a73fb23b6c42b18b3253ed29c5d0c80d84624efa12c2cf05c4b4318f")
                        {
                            Console.WriteLine(currentIndex + "***  " + foundByIndex.GetUnixTime() + " " + foundByIndex.GetPrevBlockHash().Substring(30) + " hash " + foundByIndex.DisplayHash.Substring(30));
                            if (missingBlock32.First()! == foundByIndex!)
                            {
                                Console.WriteLine("match block 32");

                            }
                        }
                        else
                        {
                            if (prevHash != foundByIndex.GetPrevBlockHash())
                            {
                                Console.WriteLine(currentIndex + "     " + foundByIndex.GetUnixTime() + " " + foundByIndex.GetPrevBlockHash().Substring(30) + " hash " + foundByIndex.DisplayHash.Substring(30));
                            }


                        }
                        prevHash = foundByIndex.DisplayHash;
                        currentIndex++;

                    }

                    */


                    if (false)
                    {
                        // assumes blocks in order

                        int currentIndex2 = 0;
                        BlockRaw prevFoundByIndex3 = null;
                        while (currentIndex2 < 10)
                        {

                            var foundByIndex2 = FindBlockByPosition(btcBlocksDirectory, 0, currentIndex2);
                            if (foundByIndex2 == null) break;      // file holds fewer blocks than this


                            string h = foundByIndex2.GetPrevBlockHash().Substring(40);
                            Console.WriteLine(currentIndex2 + "     " + h + " " + foundByIndex2.DisplayHash.Substring(40));

                            currentIndex2++;
                        }
                    }






                    // Read Blocks into memory even the forked blocks from ALL Block Files 
                    // Read Blocks into memory even the forked blocks from ALL Block Files 
                    // Read Blocks into memory even the forked blocks from ALL Block Files 
                    // Read Blocks into memory even the forked blocks from ALL Block Files 
                    // Read Blocks into memory even the forked blocks from ALL Block Files 
                    // Read Blocks into memory even the forked blocks from ALL Block Files 

                    // 65 seconds for 250 blk####.dat files

                    List<MyRawBlock<BlockRaw>> rawBlocks = new List<MyRawBlock<BlockRaw>>();
                    var readClock = Stopwatch.StartNew();

                    List<BlockRaw> allBlocks = new List<BlockRaw>();
                    int blkFile = 0;
                    while (blkFile < MAXBLKDATFILE) // limit Blk files to MAXBLKDATFILE for testing
                    {
                        List<BlockRaw> blocksInFile = ReadAllBlocks(btcBlocksDirectory, blkFile);
                        allBlocks.AddRange(blocksInFile);
                        blkFile++;
                    }
                    //List<BlockRaw> allBlocksOneFile = ReadAllBlocks(btcBlocksDirectory, 0); // only blk00000.dat
                    Console.WriteLine("Loaded " + allBlocks.Count + " blocks, read in "
                                      + readClock.Elapsed.TotalSeconds.ToString("F1") + "s");




                    // read the headers.dat file

                    // read the headers.dat file
                    // read the headers.dat file
                    // read the headers.dat file
                    // read the headers.dat file
                    // read the headers.dat file
                    // read the headers.dat file                    // read the headers.dat file
                    List<HeaderRecord> headers = ReadHeadersFile(btcBlocksDirectory);

                    HeaderRecord[] headersArray = headers.ToArray();


                    //block 333222 hash 00000000000000000220f06a0e8d4591e93829be148fa51062f1c3ac228d1b68
                    //block 305822 hash 00000000000000005c61c7d3af58fee0cb3b5746c150d4cb904797b7f2b0e19f

                    var myheader = headers.Where(x => x.Height == 305822).FirstOrDefault();
                    if (myheader == null)
                    {
                        Console.WriteLine("no header at height 961111 - headers.dat stops at height "
                                          + (headers.Count - 1));
                    }
                    else
                    {
                        Console.WriteLine("height " + myheader.Height + " hash " + myheader.GetDisplayHash());
                    }




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

                    SetNextLinks(state);



                    MyBlock<BlockRaw>? currentBlock = state.blockZero;
                    int myHeaderIndex = 0;
                    while (currentBlock != null && myHeaderIndex < state.byHash.Count - 1)
                    {
                        //Console.WriteLine("height " + currentBlock.height + " " + currentBlock.hash);
                        //if (prevhash != null && prevhash != currentBlock.prevHash)
                        {
                            //  Console.WriteLine("error: prevhash " + prevhash + " does not match currentBlock.prevHash " + currentBlock.prevHash);
                            //throw new Exception("prevhash mismatch");
                        }
                        //prevhash = currentBlock.hash;

                        currentBlock = currentBlock.nextLink;

                        if (headersArray[myHeaderIndex].GetDisplayHash() != currentBlock!.prevHash)
                        {

                        }
                        myHeaderIndex++;



                    }


                    int deleted = PruneShortForks(state, 3);



                    //MyBlock<BlockRaw>? 
                    currentBlock = state.blockZero;
                    string? prevhash = null;

                    MyBlock<BlockRaw>? blockAtHeight119221 = null;


                    // 133 MB * 3900 = 518700 MB = 518 GB
                    // 4 megs theoritical max block size 4*960000 = 3840000 MB = 3.84 TB
                    // sha 256 hash 32 * 960000 = 30720000 bytes = 30 MB

                    // block 961111 hash 00000000000000000000f34b4d14ebd20a90621dd8287de069a9c5b2333d2ba3
                    //height 961111 hash 00000000000000000000f34b4d14ebd20a90621dd8287de069a9c5b2333d2ba3

                    // stoped at blk00017

                    while (currentBlock != null)
                    {
                        //Console.WriteLine("height " + currentBlock.height + " " + currentBlock.hash);
                        if (prevhash != null && prevhash != currentBlock.prevHash)
                        {
                            Console.WriteLine("error: prevhash " + prevhash + " does not match currentBlock.prevHash " + currentBlock.prevHash);
                            throw new Exception("prevhash mismatch");
                        }
                        prevhash = currentBlock.hash;
                        currentBlock = currentBlock.nextLink;
                        if (currentBlock != null && currentBlock.height == 119221)
                        {
                            blockAtHeight119221 = currentBlock;
                        }
                    }




                    MyBlock<BlockRaw>? currBlock = state.blockZero;
                    while (currBlock != null)
                    {
                        if (currBlock.data.Size > 550)
                        {
                            //Console.WriteLine("large blockdata at height " + currBlock.height + " size " + currBlock.data.Size);
                        }
                        //Console.WriteLine(currBlock.hash + " -> " + currBlock.prevHash);
                        currBlock = currBlock.nextLink;
                    }


                    //get block with height 119221

                    Block parsedblockAtHeight119221 = ParseBlock(blockAtHeight119221!.data!, 119221);

                    //ReportState(state);
                    foreach (var t in parsedblockAtHeight119221.Transactions)
                    {
                        // Not Convert.ToHexString(t.Hash) - that prints the bytes in the order they are
                        // stored, which is this string backwards. GetHashAsString does the reversing.
                        Console.WriteLine("tx hash: " + t.GetHashAsString());
                    }


                    List<Transaction> allTransactions = new List<Transaction>();


                    if (false)
                    {
                        // get transactions instead of block data, memory intensive but useful for analysis
                        var parseClock = Stopwatch.StartNew();
                        List<Block> parsedChain = new List<Block>();

                        long totalTransactions = 0;
                        long totalInputs = 0;
                        long totalOutputs = 0;
                        ulong totalOutputSats = 0;
                        int merkleMismatches = 0;


                        MyBlock<BlockRaw>? atBlock = state.blockZero;
                        int count = 0;
                        while (atBlock != null)
                        {
                            Block parsed = ParseBlock(atBlock.data, atBlock.height);
                            parsedChain.Add(parsed);

                            foreach (Transaction tx in parsed.Transactions)
                            {
                                allTransactions.Add(tx);
                            }

                            totalTransactions += parsed.Transactions.Count;
                            foreach (Transaction tx in parsed.Transactions)
                            {
                                totalInputs += tx.Inputs.Count;
                                totalOutputs += tx.Outputs.Count;
                                foreach (Transaction.TxOutput output in tx.Outputs)
                                {
                                    totalOutputSats += output.Value;
                                }
                            }

                            // Counted rather than thrown on: one bad block should not take the whole run
                            // down when 119,000 others are fine.
                            if (!MerkleRootMatches(parsed))
                            {
                                merkleMismatches++;
                                if (merkleMismatches <= 5)
                                {
                                    Console.WriteLine("merkle mismatch at height " + parsed.header.BlockNumber
                                                      + " " + parsed.header.Hash);
                                }
                            }

                            atBlock = atBlock.nextLink;
                            if (count++ % 20000 == 0)
                            {
                                Console.WriteLine(count + " parsed height " + parsed.header.BlockNumber + " " + parsed.header.Hash);
                            }
                        }
                        parseClock.Stop();



                        Console.WriteLine("parsed " + parsedChain.Count + " blocks in "
                                          + parseClock.Elapsed.TotalSeconds.ToString("F1") + "s");
                        Console.WriteLine("  transactions : " + totalTransactions);
                        Console.WriteLine("  inputs       : " + totalInputs);
                        Console.WriteLine("  outputs      : " + totalOutputs);
                        Console.WriteLine("  output value : " + (totalOutputSats / 100000000.0).ToString("F8") + " BTC");
                        Console.WriteLine("  merkle roots : " + (parsedChain.Count - merkleMismatches)
                                          + " of " + parsedChain.Count + " match");

                        var g = allTransactions.Last();
                    }


                    if (false)
                    {
                        // 119221 block 2nd transaction 382f663b0554c5986b295eec475166592c3c638e61afe7d7a2ea2100935ba3a6  
                        byte[] myhash = Convert.FromHexString("382f663b0554c5986b295eec475166592c3c638e61afe7d7a2ea2100935ba3a6");
                        Array.Reverse(myhash);

                        foreach (var t in allTransactions)
                        {
                            if (t.Hash.AsSpan().SequenceEqual(myhash))
                            {
                                Console.WriteLine("found transaction " + t.GetHashAsString());
                                Console.WriteLine("  inputs: " + t.Inputs.Count);
                                foreach (var input in t.Inputs)
                                {
                                    //Console.WriteLine("    input prev tx: " + input.PrevTxHash + " index: " + input.PrevTxIndex);
                                }
                                Console.WriteLine("  outputs: " + t.Outputs.Count);
                                foreach (var output in t.Outputs)
                                {
                                    Console.WriteLine("    output value: " + output.Value + " script: " + Convert.ToHexString(output.ScriptPubKey).ToLowerInvariant());
                                }
                            }
                        }
                    }

                    if (true)
                    {
                        // 159920 block 2nd transaction c22f79ba86968a5285225008b2740f074f44f44ef27b8efb61ecff09e9eb4f6d  
                        byte[] myhash = Convert.FromHexString("c22f79ba86968a5285225008b2740f074f44f44ef27b8efb61ecff09e9eb4f6d");
                        Array.Reverse(myhash);

                        foreach (var t in allTransactions)
                        {
                            if (t.Hash.AsSpan().SequenceEqual(myhash))
                            {
                                Console.WriteLine("found transaction " + t.GetHashAsString());
                                Console.WriteLine("  inputs: " + t.Inputs.Count);
                                foreach (var input in t.Inputs)
                                {
                                    //Console.WriteLine("    input prev tx: " + input.PrevTxHash + " index: " + input.PrevTxIndex);
                                }
                                Console.WriteLine("  outputs: " + t.Outputs.Count);
                                foreach (var output in t.Outputs)
                                {
                                    Console.WriteLine("    output value: " + output.Value + " script: " + Convert.ToHexString(output.ScriptPubKey).ToLowerInvariant());
                                }
                            }
                        }
                    }



                    Console.WriteLine("total transactions: " + allTransactions.Count);


                    /*
                     *         public class MyBlock<TData>
        {
            public string hash;
            public string prevHash;
            public TData data;
            public MyBlock<TData>? prevLink;   // null on the first block of a chain
            public MyBlock<TData>? nextLink;   // filled in by SetNextLinks, null before that and at a tip
            public int height;                 // 0 at the root, parent + 1 everywhere else

        }
                     */

                    // Copy the chain out in fixed-size runs and file each run in its own rocksdb
                    // store. The copy is what makes a run addressable at all: SaveBlocksToRocksDb
                    // takes a root and follows nextLink to the end, so handing it a slice means
                    // building a chain of its own whose last node has a null nextLink. Only the
                    // links are new - every copy points at the BlockRaw the original already held,
                    // so this costs one small object per block and not a second copy of the bytes.
                    //
                    // One pointer walks the whole chain once. Written out a run at a time it went
                    // back to the root for each one, so every run added re-walked everything below
                    // it; here f simply carries on from wherever the previous run stopped.
                    const int segmentBlocks = 50000;

                    MyBlock<BlockRaw>? f = state.blockZero;
                    int segment = 0;
                    int totalCopied = 0;

                    while (f != null && segment < 4)
                    {
                        segment++;

                        MyBlock<BlockRaw>? head = null;
                        MyBlock<BlockRaw>? tail = null;
                        int copied = 0;

                        // Counted, not measured against a height boundary, so a run holds exactly
                        // segmentBlocks blocks whatever the heights in it happen to be.
                        while (f != null && copied < segmentBlocks)
                        {
                            MyBlock<BlockRaw> copy = new MyBlock<BlockRaw>
                            {
                                hash = f.hash,
                                prevHash = f.prevHash,

                                // Carried over rather than left at its default: height is what the
                                // rocksdb 'h' keys are built from, so a chain of copies that all
                                // say zero files every block under height 0, each overwriting the
                                // last, and the height index ends up holding one entry.
                                height = f.height,

                                data = f.data,
                                prevLink = tail,
                                nextLink = null,
                            };

                            if (tail == null)
                            {
                                head = copy;
                            }
                            else
                            {
                                tail.nextLink = copy;
                            }

                            tail = copy;
                            copied++;
                            f = f.nextLink;
                        }

                        totalCopied += copied;

                        // The outer loop only runs while f is non-null, so the inner one always
                        // copies at least one block and head is set by the time we reach here.
                        string rocksDbPath = "C:\\btcblock\\rocksdb\\blocks" + segment;
                        Console.WriteLine("segment " + segment + ": " + copied + " blocks, heights "
                                          + head!.height + " to " + tail!.height + " -> " + rocksDbPath);

                        var rocksClock = Stopwatch.StartNew();
                        SaveBlocksToRocksDb(rocksDbPath, head);
                        rocksClock.Stop();

                        Console.WriteLine("  wrote in   : " + rocksClock.Elapsed.TotalSeconds.ToString("F1") + "s");

                        // Reopen it and pull one block back, to show the store stands on its own.
                        // Asked for by this run's own first height rather than by 1: only these
                        // heights were written here, so on every store past the first, height 1
                        // comes back null and the check would quietly pass by doing nothing.
                        int firstHeight = head.height;
                        byte[]? firstFromDb = ReadBlockFromRocksDb(rocksDbPath, firstHeight);
                        if (firstFromDb != null)
                        {
                            Console.WriteLine("  reopened   : height " + firstHeight + " is "
                                              + ToDisplayHex(DoubleSha256(firstFromDb, 0, 80))
                                              + " (" + firstFromDb.Length + " bytes)");
                        }
                        else
                        {
                            Console.WriteLine("  reopened   : height " + firstHeight + " is not in the store");
                        }
                    }

                    if (segment == 0)
                    {
                        Console.WriteLine("nothing to copy - the chain is empty");
                    }
                    else
                    {
                        Console.WriteLine("wrote " + totalCopied + " blocks across " + segment
                                          + " stores of up to " + segmentBlocks + " blocks each");
                    }


                    return 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("error: " + ex.Message);
                    return 1;
                }
            }
            //else for rocksdb
            if (false)
            {
                // load rocksdb
                //
                // The branch above earns this one: it walked the blk files, worked out which
                // blocks form the longest chain and in what order, and wrote that out. So there is
                // no scanning, no chain to rebuild and nothing to sort here - the blocks come back
                // in height order because that is how they were filed.
                // One store per run the branch above wrote - blocks1, blocks2, and so on, each
                // holding up to 50,000 blocks in height order. Read back in the same order they
                // were written they concatenate into one chain, which is what makes the link
                // check further down worth running: it is then testing the seams between the
                // stores as well as the blocks inside each one.
                string rocksDbBase = "C:\\btcblock\\rocksdb\\blocks";

                // Counted first, and on its own: this is Directory.Exists until one is missing,
                // so however many runs were written is however many get read - there is no count
                // here to keep in step with the writer. Opening them is the expensive part and
                // that happens below, once this knows how many there are to open.
                int stores = 0;
                while (Directory.Exists(rocksDbBase + (stores + 1)))
                {
                    stores++;
                }

                if (stores == 0)
                {
                    Console.Error.WriteLine("no stores found - looked for " + rocksDbBase + "1");
                    Console.Error.WriteLine("set rocksDbLoaded to false to build them from the blk files first");
                    return 1;
                }

                List<BlockRaw> loaded;
                try
                {
                    var loadClock = Stopwatch.StartNew();

                    // One store per thread. They are separate databases in separate directories
                    // sharing no handle, and LoadBlocksFromRocksDb keeps everything it touches
                    // local - its list, its options, its RocksDb and its iterator are all created
                    // inside the call. So the only thing crossing threads is the array each
                    // result is dropped into, and every task owns one slot of it.
                    //
                    // Worth doing because most of the cost is not the disk: every block is
                    // re-hashed on the way out to check it against the hash it is filed under,
                    // which is CPU work that scales rather than queueing behind one disk head.
                    var perStore = new List<BlockRaw>[stores];
                    var summary = new string[stores];

                    Parallel.For(0, stores, i =>
                    {
                        string storePath = rocksDbBase + (i + 1);
                        List<BlockRaw> fromStore = LoadBlocksFromRocksDb(storePath);
                        perStore[i] = fromStore;

                        // Built here, printed below in store order. Writing to the console from
                        // inside the loop would put the lines down in whatever order the threads
                        // happened to finish, which reads like the stores are out of order.
                        if (fromStore.Count == 0)
                        {
                            summary[i] = "  " + Path.GetFileName(storePath) + " : empty";
                        }
                        else
                        {
                            summary[i] = "  " + Path.GetFileName(storePath) + " : " + fromStore.Count
                                         + " blocks, heights " + fromStore[0].BlockIndex
                                         + " to " + fromStore[fromStore.Count - 1].BlockIndex;
                        }
                    });

                    loadClock.Stop();

                    foreach (string line in summary)
                    {
                        Console.WriteLine(line);
                    }

                    // Joined back up in store order, which is height order - the threads finish in
                    // whatever order they like but nothing reads perStore until they are all done.
                    // Sized up front so the list does not copy its whole array every time it grows
                    // past a couple of hundred thousand entries.
                    int total = 0;
                    foreach (List<BlockRaw> fromStore in perStore)
                    {
                        total += fromStore.Count;
                    }

                    loaded = new List<BlockRaw>(total);
                    foreach (List<BlockRaw> fromStore in perStore)
                    {
                        loaded.AddRange(fromStore);
                    }

                    Console.WriteLine("rocksdb loaded: " + loaded.Count + " blocks from " + stores
                                      + " stores in " + loadClock.Elapsed.TotalSeconds.ToString("F2") + "s");
                }
                catch (AggregateException ex)
                {
                    // Parallel.For collects everything that threw and hands it back in one of
                    // these, whose own Message is just "One or more errors occurred" - the part
                    // worth reading is inside it.
                    Console.Error.WriteLine("could not load the stores:");
                    foreach (Exception inner in ex.Flatten().InnerExceptions)
                    {
                        Console.Error.WriteLine("  " + inner.Message);
                    }
                    Console.Error.WriteLine("set rocksDbLoaded to false to build them from the blk files first");
                    return 1;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("could not load the stores: " + ex.Message);
                    Console.Error.WriteLine("set rocksDbLoaded to false to build them from the blk files first");
                    return 1;
                }

                if (loaded.Count == 0)
                {
                    Console.Error.WriteLine("the store is empty - nothing to work with");
                    return 1;
                }

                Console.WriteLine("  first      : height " + loaded[0].BlockIndex + " " + loaded[0].DisplayHash);
                Console.WriteLine("  last       : height " + loaded[loaded.Count - 1].BlockIndex
                                  + " " + loaded[loaded.Count - 1].DisplayHash);

                // The blocks were stored in chain order, so this is checking the store kept it -
                // every block's parent should be the one before it, with no gap in the heights.
                int brokenLinks = 0;
                for (int i = 1; i < loaded.Count; i++)
                {
                    if (loaded[i].GetPrevBlockHash() != loaded[i - 1].DisplayHash)
                    {
                        brokenLinks++;
                        if (brokenLinks <= 5)
                        {
                            Console.Error.WriteLine("  break at height " + loaded[i].BlockIndex
                                                    + ": parent is " + loaded[i].GetPrevBlockHash()
                                                    + ", previous block is " + loaded[i - 1].DisplayHash);
                        }
                    }
                }

                if (brokenLinks == 0)
                {
                    Console.WriteLine("  chain      : all " + (loaded.Count - 1) + " links hold");
                }
                else
                {
                    Console.WriteLine("  chain      : " + brokenLinks + " broken links");
                }

                // Parse them, the same way the blk-file branch does.
                var parseFromDbClock = Stopwatch.StartNew();
                long loadedTransactions = 0;
                long loadedInputs = 0;
                long loadedOutputs = 0;
                ulong loadedOutputSats = 0;
                int loadedMerkleMismatches = 0;

                foreach (BlockRaw raw in loaded)
                {
                    Block parsedFromDb = ParseBlock(raw, raw.BlockIndex);

                    loadedTransactions += parsedFromDb.Transactions.Count;
                    foreach (Transaction tx in parsedFromDb.Transactions)
                    {
                        loadedInputs += tx.Inputs.Count;
                        loadedOutputs += tx.Outputs.Count;
                        foreach (Transaction.TxOutput output in tx.Outputs)
                        {
                            loadedOutputSats += output.Value;
                        }
                    }

                    if (!MerkleRootMatches(parsedFromDb))
                    {
                        loadedMerkleMismatches++;
                    }
                }
                parseFromDbClock.Stop();

                Console.WriteLine("  parsed in  : " + parseFromDbClock.Elapsed.TotalSeconds.ToString("F1") + "s");
                Console.WriteLine("  transactions: " + loadedTransactions);
                Console.WriteLine("  inputs      : " + loadedInputs + ", outputs " + loadedOutputs);
                Console.WriteLine("  output value: " + (loadedOutputSats / 100000000.0).ToString("F8") + " BTC");
                Console.WriteLine("  merkle roots: " + (loaded.Count - loadedMerkleMismatches)
                                  + " of " + loaded.Count + " match");


                BlockRaw? prevBlock = null;
                foreach (var l in loaded)
                {
                    //Console.WriteLine("height " + currentBlock.height + " " + currentBlock.hash);
                    if (prevBlock != null && prevBlock.DisplayHash != l.previousHash)
                    {
                        Console.WriteLine("error: ");
                        throw new Exception("prevhash mismatch");
                    }
                    prevBlock = l;
                }

                // block 199999 hash = 00000000000003a20def7a05a77361b9657ff954b2f2080e135ea6f5970da215

                string g = loaded.Last().DisplayHash;


                // save transactions
            }

            if (true)
            {
                if(false)
                {
                    int MAXBLKDATFILE = 91; // 91 good for 10 (250,000)

                    List<MyRawBlock<BlockRaw>> rawBlocks = new List<MyRawBlock<BlockRaw>>();
                    var readClock = Stopwatch.StartNew();

                    List<BlockRaw> allBlocks = new List<BlockRaw>();
                    int blkFile = 0;
                    while (blkFile < MAXBLKDATFILE) // limit Blk files to MAXBLKDATFILE for testing
                    {
                        List<BlockRaw> blocksInFile = ReadAllBlocks(btcBlocksDirectory, blkFile);
                        allBlocks.AddRange(blocksInFile);
                        blkFile++;
                    }
                    //List<BlockRaw> allBlocksOneFile = ReadAllBlocks(btcBlocksDirectory, 0); // only blk00000.dat
                    Console.WriteLine("Loaded " + allBlocks.Count + " blocks, read in "
                                      + readClock.Elapsed.TotalSeconds.ToString("F1") + "s");




                    // read the headers.dat file

                    // read the headers.dat file
                    // read the headers.dat file
                    // read the headers.dat file
                    // read the headers.dat file
                    // read the headers.dat file
                    // read the headers.dat file                    // read the headers.dat file
                    List<HeaderRecord> headers = ReadHeadersFile(btcBlocksDirectory);

                    HeaderRecord[] headersArray = headers.ToArray();


                    //block 333222 hash 00000000000000000220f06a0e8d4591e93829be148fa51062f1c3ac228d1b68
                    //block 305822 hash 00000000000000005c61c7d3af58fee0cb3b5746c150d4cb904797b7f2b0e19f

                    var myheader = headers.Where(x => x.Height == 305822).FirstOrDefault();
                    if (myheader == null)
                    {
                        Console.WriteLine("no header at height 961111 - headers.dat stops at height "
                                          + (headers.Count - 1));
                    }
                    else
                    {
                        Console.WriteLine("height " + myheader.Height + " hash " + myheader.GetDisplayHash());
                    }




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

                    SetNextLinks(state);




                    List<Transaction> allTransactions = new List<Transaction>();

                    // get transactions instead of block data, memory intensive but useful for analysis
                    var parseClock = Stopwatch.StartNew();
                    List<Block> parsedChain = new List<Block>();

                    long totalTransactions = 0;
                    long totalInputs = 0;
                    long totalOutputs = 0;
                    ulong totalOutputSats = 0;
                    int merkleMismatches = 0;


                    MyBlock<BlockRaw>? atBlock = state.blockZero;
                    int count = 0;
                    while (atBlock != null)
                    {
                        Block parsed = ParseBlock(atBlock.data, atBlock.height);
                        parsedChain.Add(parsed);

                        foreach (Transaction tx in parsed.Transactions)
                        {
                            allTransactions.Add(tx);
                        }

                        totalTransactions += parsed.Transactions.Count;
                        foreach (Transaction tx in parsed.Transactions)
                        {
                            totalInputs += tx.Inputs.Count;
                            totalOutputs += tx.Outputs.Count;
                            foreach (Transaction.TxOutput output in tx.Outputs)
                            {
                                totalOutputSats += output.Value;
                            }
                        }

                        // Counted rather than thrown on: one bad block should not take the whole run
                        // down when 119,000 others are fine.
                        if (!MerkleRootMatches(parsed))
                        {
                            merkleMismatches++;
                            if (merkleMismatches <= 5)
                            {
                                Console.WriteLine("merkle mismatch at height " + parsed.header.BlockNumber
                                                  + " " + parsed.header.Hash);
                            }
                        }

                        atBlock = atBlock.nextLink;
                        if (count++ % 12500 == 0)
                        {
                            Console.WriteLine(count + " parsed height " + parsed.header.BlockNumber + " " + parsed.header.Hash);
                        }
                    }
                    parseClock.Stop();



                    Console.WriteLine("parsed " + parsedChain.Count + " blocks in "
                                      + parseClock.Elapsed.TotalSeconds.ToString("F1") + "s");
                    Console.WriteLine("  transactions : " + totalTransactions);
                    Console.WriteLine("  inputs       : " + totalInputs);
                    Console.WriteLine("  outputs      : " + totalOutputs);
                    Console.WriteLine("  output value : " + (totalOutputSats / 100000000.0).ToString("F8") + " BTC");
                    Console.WriteLine("  merkle roots : " + (parsedChain.Count - merkleMismatches)
                                      + " of " + parsedChain.Count + " match");

                    var g = allTransactions.Last();













                    Console.WriteLine("total transactions: " + allTransactions.Count);

                    const int segmentBlocks = 25000;

                    MyBlock<BlockRaw>? f = state.blockZero;
                    int segment = 0;
                    int totalCopied = 0;

                    while (f != null && segment < 11)
                    {
                        segment++;

                        MyBlock<BlockRaw>? head = null;
                        MyBlock<BlockRaw>? tail = null;
                        int copied = 0;

                        // Counted, not measured against a height boundary, so a run holds exactly
                        // segmentBlocks blocks whatever the heights in it happen to be.
                        while (f != null && copied < segmentBlocks)
                        {
                            MyBlock<BlockRaw> copy = new MyBlock<BlockRaw>
                            {
                                hash = f.hash,
                                prevHash = f.prevHash,

                                // Carried over rather than left at its default: height is what the
                                // rocksdb 'h' keys are built from, so a chain of copies that all
                                // say zero files every block under height 0, each overwriting the
                                // last, and the height index ends up holding one entry.
                                height = f.height,

                                data = f.data,
                                prevLink = tail,
                                nextLink = null,
                            };

                            if (tail == null)
                            {
                                head = copy;
                            }
                            else
                            {
                                tail.nextLink = copy;
                            }

                            tail = copy;
                            copied++;
                            f = f.nextLink;
                        }

                        totalCopied += copied;

                        // The outer loop only runs while f is non-null, so the inner one always
                        // copies at least one block and head is set by the time we reach here.
                        string rocksDbPath = "C:\\btcblock\\rocksdb\\blocks" + segment;
                        Console.WriteLine("segment " + segment + ": " + copied + " blocks, heights "
                                          + head!.height + " to " + tail!.height + " -> " + rocksDbPath);

                        var rocksClock = Stopwatch.StartNew();
                        SaveBlocksToRocksDb(rocksDbPath, head);
                        rocksClock.Stop();


                    }



                }

                if(false)
                {


                    string rocksDbBase2 = "C:\\btcblock\\rocksdb\\blocksFirst10";

                    var loadedBlocks = LoadBlocks(0, 249999, rocksDbBase2);

                    //var loadedBlocks2 = LoadBlocks(200000, 249999, rocksDbBase2);


                    List<AddressBalance>? walletsFromList2 = null;

                    // load all transactions from ~6 gigabyte  sqllite file transactions_all.db
                    //
                    // The file the last run left behind - SaveAllTransactionsToSqlite writes it at the
                    // end of the balance walk - read back whole. What comes out is not what went in:
                    // the database holds a txid, a locator and one row per side of every transaction,
                    // with the address and value already resolved on both sides, and no scripts, no
                    // outpoints and no block bytes. So this rebuilds StoredTransaction rather than
                    // Transaction, which is the honest shape of what is in there.
                    //
                    // Worth having because resolving inputs is the expensive half of everything below.
                    // An input names an outpoint and no address, so attributing one means carrying the
                    // UTXO set of the whole chain and looking it up - which the run that wrote this
                    // file already did, once, and filed the answers. Reading them back is a single
                    // sequential pass over the file with nothing held but the result.
                    //const int dbHeights = 50000;//200000;

                    string allTxDbPath = "C:\\btcblock\\rocksdb\\transactions_all.db";

                    //List<StoredTransaction> storedTransactions =
                    //LoadAllTransactionsFromSqlite(allTxDbPath, dbHeights);

                    // 7 million transactions in first 200,000 blocks

                    //int64 9x10^18  all sats is 21x10^ 14 (6+8)

                    // And the balances straight off them, which is the third way this file arrives at
                    // that table: once from the blocks, once from the collected transactions, and now
                    // once from the database. CompareAddressBalances further down holds this against
                    // the block walk - the same several million addresses, reached by routines with no
                    // code in common, is what says the database is a faithful copy of the chain.
                    //
                    // null rather than a path because the CSV is another 350 MB and the two already
                    // written say the same thing; pass a path to have it out.
                    //walletsFromList2 = CollectAddressBalancesFromStoredTransactions(storedTransactions,dbHeights, null);

                    // silk road  1933phfhK3ZgFQNLGSDXvqCn32k2buXY8a
                    // mount gox 1FeexV6bAHb8ybZjqQMjJrcCrHGW9sb6uF

                    List<Transaction> allTransactions = new List<Transaction>();

                    int count = 0;

                    foreach (BlockRaw raw in loadedBlocks)
                    {
                        Block parsed = ParseBlock(raw, raw.BlockIndex);

                        // Moved out of the inner loop, where it added the block's whole list once per
                        // transaction in it - so a block of n transactions put n*n of them in here.
                        // Anything counting balances off the result credited every output n times.
                        allTransactions.AddRange(parsed.Transactions);

                        foreach (Transaction tx in parsed.Transactions)
                        {
                            if (count++ % 500000 == 0)
                            {
                                Console.WriteLine(tx.GetHashAsString());
                                //tx.
                            }

                        }
                    }

                    Console.WriteLine("allTransactions " + allTransactions.Count);

                    // 5000 transactions per block  1,000,000 blocks is 5 billion transactions at 500 bytes each, which is 2.5 TB of data. 


                    List<KeyValuePair<int, simpleTransaction[]>> simpleTransactionsList = new List<KeyValuePair<int, simpleTransaction[]>>();
                    int[] simpleTransactionsListCounts = new int[MAXLISTSsimpleTransactionsList];

                    int i = 0;
                    while (i < MAXLISTSsimpleTransactionsList)
                    {
                        simpleTransaction[] simpleTransactionsMillion = new simpleTransaction[MAXSIZEsimpleTransactionsList];
                        simpleTransactionsList.Add(new KeyValuePair<int, simpleTransaction[]>(i, simpleTransactionsMillion));
                        simpleTransactionsListCounts[i] = 0;
                        i++;
                    }

                    Dictionary<long, string> lookupAddress = new Dictionary<long, string>(1 << 22);


                    // The balances out of allTransactions rather than out of the blocks. Nothing is
                    // parsed again - the list above already holds every transaction, in chain order,
                    // each with its height stamped on it by ParseBlock on the way past.
                    //
                    // What this way costs is memory rather than time. The block walk further down
                    // holds one parsed block at a time and lets it go; this holds every transaction
                    // of 200,000 blocks at once, inputs, outputs, scripts and all, on top of the raw
                    // block bytes that `loadedBlocks` is already keeping. If it runs out of room, the block
                    // walk arrives at the same table without the list.
                    //
                    // Declared out here so the comparison at the end of the block walk can still find
                    // it, and null when this has been switched off, which is what that check is for.
                    List<AddressBalance>? walletsFromList = null;
                    {
                        const int listHeights = 250000;

                        string listCsvPath = "C:\\btcblock\\rocksdb\\address_balances_from_list.csv";

                        var listClock = Stopwatch.StartNew();
                        walletsFromList = CollectAddressBalancesFromTransactions(allTransactions, listHeights,
                                                                                listCsvPath, simpleTransactionsList,
                                                                                simpleTransactionsListCounts, lookupAddress);
                        listClock.Stop();

                        Console.WriteLine("  total        : " + listClock.Elapsed.TotalSeconds.ToString("F1")
                                          + "s including the sort and the file");
                    }


                    var t3 = simpleTransactionsList.ToArray();
                    var s = simpleTransactionsListCounts.ToArray();

                    for (int i2 = 0; i2 < MAXLISTSsimpleTransactionsList; i2++)
                    {
                        simpleTransaction.saveToDisk("C:\\btcblock\\rocksdb\\simpletx\\OneToTen", i2, simpleTransactionsList[i2].Value, simpleTransactionsListCounts[i2]);
                    }


                    saveLookupAddress(toSortedLookup(lookupAddress), "C:\\btcblock\\rocksdb\\addresslookup10.dat");


                    SortedList<long, string> lookupAddress2 = loadLookupAddress("C:\\btcblock\\rocksdb\\addresslookup10.dat");

                }
                
                if(true)
                { 
                    List<KeyValuePair<int, simpleTransaction[]>> simpleTransactionsList = new List<KeyValuePair<int, simpleTransaction[]>>();
                    //int[] simpleTransactionsListCounts = new int[MAXLISTSsimpleTransactionsList];

                    for (int i2 = 0; i2 < MAXLISTSsimpleTransactionsList; i2++)
                    {
                        simpleTransactionsList.Add(new KeyValuePair<int, simpleTransaction[]>(i2,
                            simpleTransaction.loadFromDisk("C:\\btcblock\\rocksdb\\simpletx\\OneToTen", i2)));
                    }

                    simpleTransaction g = new simpleTransaction();
                    long h = g.shrinkStringAddress("1L7i2sEamwB6SzZzn9JQf2Rz5XB4AcAMfP");




                    SortedList<long, string> lookupAddress2 = loadLookupAddress("C:\\btcblock\\rocksdb\\simpletx\\addresslookup10.dat");

                    // The shrunk key back to the string it came from, for printing only.
                    //
                    // A miss is not a bug and not rare. The table holds what addLookupAddress was
                    // handed while the records were being built, which is the addresses that could
                    // be read off a script - so an end of a payment that was never named, or one
                    // whose records came from a run before the table existed, has nothing to find
                    // here. The number is still the identity either way, so a miss prints it.
                    //
                    // Zero is left to fall through to that on purpose. It reads as "no address at
                    // all" - a coinbase's From, a script nobody can name - and also as the burn
                    // address 1111111111111111111114oLvT2, whose hash160 is twenty zero bytes.
                    // Nothing here can tell those apart, so it says so rather than picking one.
                    string nameOf(long shrunk)
                    {
                        if (lookupAddress2.TryGetValue(shrunk, out string? name))
                        {
                            return name;
                        }

                        if (shrunk == 0)
                        {
                            return "0 (no address, or the burn address - not told apart)";
                        }

                        return shrunk + " (not in the lookup)";
                    }






                    // Every slot of every array is a record, so there is no count to walk to the
                    // way the save side needed simpleTransactionsListCounts: loadFromDisk sizes the
                    // array from the file length, and a file holds exactly what was written to it.
                    // Worth saying up front whether the address being looked for is one the table
                    // knows, because a scan that finds nothing has two very different causes and
                    // this tells them apart: an address with no payments in these two files, or an
                    // address whose key was never built the same way these records' keys were.
                    Console.WriteLine("scanning for 1L7i2sEamwB6SzZzn9JQf2Rz5XB4AcAMfP -> " + h
                                      + (lookupAddress2.ContainsKey(h) ? ", in the lookup"
                                                                       : ", NOT in the lookup")
                                      + " (" + lookupAddress2.Count + " addresses)");

                    int found = 0;
                    long sentTotal = 0;
                    long receivedTotal = 0;

                    foreach (KeyValuePair<int, simpleTransaction[]> file in simpleTransactionsList)
                    {
                        simpleTransaction[] records = file.Value;

                        for (int r = 0; r < records.Length; r++)
                        {
                            simpleTransaction t = records[r];

                            // One record is one From-to-To payment, so this address can be on
                            // either end of it - and on both, which is what a payment back to
                            // itself looks like once the change output is written down this way.
                            bool sent = t.From == h;
                            bool received = t.To == h;

                            if (!sent && !received)
                            {
                                continue;
                            }

                            int height;
                            long amount = t.splitAMountAndBLock(t.AmountAndBlock, out height);

                            string direction;
                            long other;
                            if (sent && received)
                            {
                                direction = "self    ";
                                other = h;
                            }
                            else if (sent)
                            {
                                direction = "sent    ";
                                other = t.To;          // the end that is not this address
                                sentTotal += amount;
                            }
                            else
                            {
                                direction = "received";
                                other = t.From;
                                receivedTotal += amount;
                            }

                            // Satoshis to BTC as decimal rather than double: eight places of a
                            // hundred-millionth is past what double holds exactly, and a balance
                            // printed a satoshi out is the kind of wrong that gets believed.
                            Console.WriteLine("  file " + file.Key + " record " + r.ToString("D7")
                                              + "  block " + height.ToString("D6")
                                              + "  " + direction
                                              + " " + (amount / 100000000m).ToString("F8") + " BTC"
                                              + "  " + (sent && received ? "with " : sent ? "to   " : "from ")
                                              + nameOf(other));
                            found++;
                        }
                    }

                    Console.WriteLine(found + " records: " + (receivedTotal / 100000000m).ToString("F8")
                                      + " BTC in, " + (sentTotal / 100000000m).ToString("F8")
                                      + " BTC out, balance "
                                      + ((receivedTotal - sentTotal) / 100000000m).ToString("F8") + " BTC");

                    // Every payment in both files applied to a running balance per address. Same
                    // walk as the scan above with the filter taken off: To is credited, From is
                    // debited, and what is left against a key is what that address holds across
                    // the records loaded here.
                    //
                    // A Dictionary rather than the SortedList this file's lookup table uses,
                    // because this one is built by accumulation rather than by lookup - the same
                    // busy address is hit thousands of times - and an O(1) hash beats a binary
                    // search followed by an O(n) array shift every time a new address turns up.
                    // Sorted order is wanted once, at the end, and is paid for there instead.
                    var balances = new Dictionary<long, long>(1 << 20);

                    long payments = 0;

                    foreach (KeyValuePair<int, simpleTransaction[]> file in simpleTransactionsList)
                    {
                        foreach (simpleTransaction t in file.Value)
                        {
                            int atHeight;
                            long moved = t.splitAMountAndBLock(t.AmountAndBlock, out atHeight);

                            // GetValueRefOrAddDefault rather than TryGetValue and then an assign:
                            // one hash of the key rather than two, on a loop that runs twice per
                            // record over every record in the set.
                            //
                            // The ref it hands back is into the dictionary's own storage, so it
                            // dies the moment anything adds another key - which is what the very
                            // next statement does. Hence each one used and finished with before
                            // the other is asked for, rather than both taken up front.
                            ref long credit = ref System.Runtime.InteropServices.CollectionsMarshal
                                                    .GetValueRefOrAddDefault(balances, t.To, out _);
                            credit += moved;

                            ref long debit = ref System.Runtime.InteropServices.CollectionsMarshal
                                                   .GetValueRefOrAddDefault(balances, t.From, out _);
                            debit -= moved;

                            payments++;
                        }
                    }

                    Console.WriteLine();
                    Console.WriteLine(payments + " payments over " + balances.Count + " addresses");

                    // The same address counted two different ways: the scan above filtered for h
                    // and totalled what it saw, this one totalled everything and then looked h up.
                    // They can only disagree if one of them is wrong, so it is worth one line to
                    // find that out rather than trusting both.
                    long fromScan = receivedTotal - sentTotal;
                    long fromTable = balances.TryGetValue(h, out long held) ? held : 0;

                    Console.WriteLine("  cross check on " + h + ": scan says "
                                      + (fromScan / 100000000m).ToString("F8") + ", table says "
                                      + (fromTable / 100000000m).ToString("F8")
                                      + (fromScan == fromTable ? "   (agree)" : "   <-- DISAGREE"));

                    // Key zero is not a wallet and is taken out before the figures below. A
                    // coinbase has no From, so every block's reward debits zero and what collects
                    // there is the amount minted rather than anything anybody holds - with whatever
                    // the burn address 1111111111111111111114oLvT2 was paid mixed in, since the two
                    // share this key. Printed on its own line because it is worth seeing, not
                    // because it means what the other rows mean.
                    if (balances.Remove(0, out long notAWallet))
                    {
                        Console.WriteLine("  key 0, minted and burned together, not a wallet: "
                                          + (notAWallet / 100000000m).ToString("F8") + " BTC");
                    }

                    // Negative balances are expected and are not an arithmetic fault: only two of
                    // the seven files are loaded, so an address whose incoming payments live in one
                    // of the other five is caught spending money it was never seen receiving. The
                    // count is how much of this set is being read through that hole.
                    int negative = 0;
                    long positiveTotal = 0;

                    foreach (long balance in balances.Values)
                    {
                        if (balance < 0)
                        {
                            negative++;
                        }
                        else
                        {
                            positiveTotal += balance;
                        }
                    }

                    Console.WriteLine("  " + negative + " addresses negative (funded by a file not"
                                      + " loaded here), " + (balances.Count - negative)
                                      + " at or above zero holding "
                                      + (positiveTotal / 100000000m).ToString("F8") + " BTC");

                    // One full sort of the table to answer one question. Cheaper than it sounds
                    // next to the walk that built it, and simpler than carrying a heap of the top
                    // twenty five through the accumulation - which is the change to make if this
                    // ever runs over all seven files rather than two.
                    const int TopWallets = 25;

                    Console.WriteLine();
                    Console.WriteLine("top " + TopWallets + " by balance:");

                    foreach (KeyValuePair<long, long> wallet in balances
                                                               .OrderByDescending(w => w.Value)
                                                               .Take(TopWallets))
                    {
                        Console.WriteLine("  " + (wallet.Value / 100000000m).ToString("F8").PadLeft(18)
                                          + " BTC  " + nameOf(wallet.Key));
                    }

                    
                }
            }




            //https://www.blockchain.com/explorer/addresses/BTC/1GoLDuG1MMwvAhSzwJigNyThnmAzt98RCW
            // address with 400 transactions 1GoLDuG1MMwvAhSzwJigNyThnmAzt98RCW, 2/26/2014, 23:45:38 last transaction hash 300cafc07578688e5a31fc0051e20696a3e29a351a2c144576920163eb44823c
            return 0;
        }

        public const int MAXLISTSsimpleTransactionsList = 10;
        public const int MAXSIZEsimpleTransactionsList = 15000000;  // about 350 megs



        /// <summary>
        /// A transaction cut down to twenty four bytes: who paid, who was paid, how much, and
        /// where in the chain it happened.
        ///
        /// What that buys is the whole of the early chain in memory at once. The database this
        /// shrinks from holds thirty million address rows and takes about three gigabytes to load;
        /// the same rows at twenty four bytes each are seven hundred megabytes on disk. What it
        /// costs is everything the bytes no longer say - no txid, no script, no locator back to
        /// the block, and an address truncated to eight bytes that cannot be turned back into a
        /// string.
        ///
        /// In memory it is not twenty four bytes, because this is a class: thirty million of them
        /// is thirty million objects at sixteen bytes of header plus the twenty four of fields,
        /// and an array of eight byte references pointing at them - about 1.4 GB rather than the
        /// 700 MB on disk. As a struct it would be the disk figure exactly, the array would be one
        /// allocation the collector never looks inside, and saveToDisk could hand the whole array
        /// to the file in one call through MemoryMarshal instead of packing it a record at a time.
        /// Changing that is a one word edit here and nothing at the call sites, since every method
        /// below already treats a record as a value.
        ///
        /// So this is an index to answer questions with, not a record to rebuild anything from:
        /// which addresses touched each other, how much moved and when. Anything else means going
        /// back to the database or the blocks.
        ///
        /// One instance is one payment - one From to one To - not one Bitcoin transaction, which
        /// has as many inputs and outputs as it likes. How a transaction with three inputs and two
        /// outputs turns into these is the caller's decision and not one this class makes: six
        /// records for every from-to pair, or five with one end left at zero. The chain itself does
        /// not say which input paid which output.
        /// </summary>
        public class simpleTransaction
        {
            public Int64 From;
            public Int64 AmountAndBlock; // amount in satoshis upper 40 bits, block height in the smallest 24 bits
            public Int64 To;

            /// <summary>Bits at the bottom of AmountAndBlock holding the height. 24 of them reach
            /// block 16,777,215, which at ten minutes a block is somewhere past the year 2330.</summary>
            public const int BlockHeightBits = 2; // 24 ,  adds to 64 with below

            /// <summary>Bits above those holding the amount.</summary>
            public const int AmountBits = 61; // 40 , adds to 64 with above

            /// <summary>The highest block this can pack: 16,777,215.</summary>
            public const int MaxBlockHeight = (1 << BlockHeightBits) - 1;

            /// <summary>The largest amount this can pack: 1,099,511,627,775 satoshis, which is
            /// 10,995.11627775 BTC. Not the ~5,500 the field comment guessed - that is 2^39, and
            /// the field is 2^40 wide - and not nearly the whole of what the chain contains. See
            /// computeAmountAndBlock.</summary>
            public const Int64 MaxAmountSatoshis = (1L << AmountBits) - 1;

            /// <summary>What one of these occupies on disk, and what its three fields occupy in
            /// memory. The file format is the fields, in order, and nothing else.</summary>
            public const int RecordBytes = 24;

            const Int64 BlockHeightMask = (1L << BlockHeightBits) - 1;

            /// <summary>Where saveToDisk and loadFromDisk put their files. The one knob either of
            /// them has - fileIndex only names the file inside it.</summary>

            /// <summary>
            /// An address shrunk to eight bytes, given as a string.
            ///
            /// Two forms are taken, told apart by length rather than by guesswork: 40 or 50
            /// characters is hex - the hash160 on its own, or the whole version-hash-checksum
            /// payload - and anything else is a base58 address, which runs 26 to 35 characters and
            /// so can never be either of those lengths. A base58 string has its checksum verified
            /// on the way through, which costs two SHA-256s per call; hex is not checked because
            /// there is nothing in it to check.
            /// </summary>
            public Int64 shrinkStringAddress(string addressHexString)
            {
                if (addressHexString == null)
                {
                    throw new ArgumentNullException(nameof(addressHexString));
                }

                if (addressHexString.Length == 40 || addressHexString.Length == 50)
                {
                    return shrinkByteAddress(Convert.FromHexString(addressHexString));
                }

                return shrinkByteAddress(Base58CheckDecode(addressHexString));
            }

            /// <summary>
            /// An address shrunk to eight bytes: the LAST eight of its hash160, read big endian so
            /// the number prints as the tail of the hash reads. Which end is not arbitrary and is
            /// the whole substance of this method - see below.
            ///
            /// Truncating a hash needs no hashing of its own, and the theory says any eight bytes
            /// of a hash160 are as good as any other eight: it is already uniform, and the birthday
            /// bound over 64 bits makes a collision anywhere in 6.6 million addresses about a one
            /// in a million event. The theory is wrong about this chain, and the ends of the hash
            /// are not interchangeable. Counted over the 6,576,582 addresses of the first 200,000
            /// blocks:
            ///
            ///     first 8 bytes       101 collisions
            ///     middle 8 bytes       30
            ///     last 8 bytes         12
            ///
            /// Because a fair share of those addresses were never hashed from anything. They are
            /// vanity and message addresses - 11ConsecteturAdipiscingE1itYQHEPM,
            /// 11EtchabLockdotcomXXXXXXXXXXzmeuk, 1111111111111111111114oLvT2 - where somebody
            /// picked the base58 string and let the bytes fall out of it. Base58 is a big endian
            /// numeral system, so fixing the front of the string fixes the front of the payload:
            /// every address beginning "11Consectetur" shares its leading bytes by construction,
            /// and seven of them share all eight. The filler at the end is where those addresses
            /// differ, which is why the tail is the eight bytes to take.
            ///
            /// The twelve that still collide are mostly the same thing from the other end. One is
            /// not: 175tWpb8K1S7NmH4Zx6rewF9WQrcZv2456 and 37muSN5ZrukVTvyVh3mT5Zc5ew9L9CBare are
            /// the same hash160 - 42bd6b9eeb1da01504fefe014e16415246c0f66f - under two version
            /// bytes, one paying a public key hash and one paying a script hash. No truncation can
            /// separate those, because nothing but the version byte differs, and the version byte
            /// is not mixed in here: a bare hash160 arrives without one, and mixing it would make
            /// the 20 byte form disagree with the 21 and 25 byte forms of the same address.
            ///
            /// It is a one way trip. The other twelve bytes are gone, so nothing here can be turned
            /// back into an address - keep the strings elsewhere if they are wanted.
            ///
            /// Half of these are negative, which is nothing to worry about: the number is an
            /// identity and never an amount. Zero is worth knowing about. It reads naturally as "no
            /// address at all" - a coinbase's From, a script nobody can name - and no address
            /// reaches it by chance, but one address in this data reaches it on purpose:
            /// 1111111111111111111114oLvT2, whose hash160 is twenty zero bytes, is the burn address
            /// everybody's unspendable coins go to. It appears in the first 200,000 blocks. So a
            /// zero either means nothing was there or means that address, and anything that has to
            /// tell them apart needs a flag of its own.
            ///
            /// Takes the hash160 on its own (20 bytes), with its version byte (21), or the whole
            /// base58 payload with the checksum still on it (25).
            /// </summary>
            public Int64 shrinkByteAddress(byte[] addressBytes)
            {
                if (addressBytes == null)
                {
                    throw new ArgumentNullException(nameof(addressBytes));
                }

                int hash160;
                if (addressBytes.Length == 20)
                {
                    hash160 = 0;
                }
                else if (addressBytes.Length == 21 || addressBytes.Length == 25)
                {
                    hash160 = 1;
                }
                else
                {
                    throw new ArgumentException("an address is 20 bytes of hash160, 21 with its"
                                                + " version byte or 25 with the checksum as well,"
                                                + " and this is " + addressBytes.Length,
                                                nameof(addressBytes));
                }

                // The last eight bytes of the hash160, not the last eight of what was passed in -
                // on a 25 byte payload those would be four bytes of hash and the four byte
                // checksum, which is a different key for the same address depending on which form
                // it arrived in.
                return BinaryPrimitives.ReadInt64BigEndian(addressBytes.AsSpan(hash160 + 12, 8));
            }

            /// <summary>
            /// The two halves of AmountAndBlock taken back apart: the height out of the bottom 24
            /// bits, the amount returned out of the top 40.
            ///
            /// Shifted unsigned rather than arithmetic. A value this class packed never has bit 63
            /// set, so the two agree on anything computeAmountAndBlock made - but on a field that
            /// was never packed, or was read off a corrupted file, an arithmetic shift hands back a
            /// negative amount and a signed one hands back a large positive number that is at least
            /// obviously wrong.
            /// </summary>
            public Int64 splitAMountAndBLock(Int64 combinedAmountAndBlock, out int blockHeight)
            {
                blockHeight = (int)(combinedAmountAndBlock & BlockHeightMask);
                return combinedAmountAndBlock >>> BlockHeightBits;
            }

            /// <summary>
            /// The amount and the height packed into one Int64, the inverse of splitAMountAndBLock.
            ///
            /// The limits are exact and both were one out as first written. `2 &lt;&lt; 23` is 2^24,
            /// which is one past the largest height 24 bits hold, so a height of exactly 16,777,216
            /// went through and spilled into bit 24 - into the bottom of the amount. `2 &lt;&lt; 39`
            /// is 2^40 and let one satoshi too many into a 40 bit field the same way. Hence
            /// MaxBlockHeight and MaxAmountSatoshis, and hence &gt; rather than &gt;=.
            ///
            /// The amount limit is the real constraint on this format, and 40 bits is not enough
            /// for the chain it is being pointed at. It reaches 10,995.11627775 BTC; the first
            /// 200,000 blocks contain 30,506 address rows above that, 15,269 of them outputs, and
            /// the largest single output in the range is the 500,000 BTC moved in block 153,509 -
            /// 50,000,000,000,000 satoshis, which needs 46 bits. Bitcoin's whole supply needs 51.
            /// So this throws on about one row in a thousand of that database, which is a choice to
            /// make rather than a bug: widen the amount to 46 bits and the height to 18 (which then
            /// only reaches block 262,143), keep 40/24 and hold the outliers somewhere else, or
            /// move to a wider record.
            /// </summary>
            public Int64 computeAmountAndBlock(Int64 amountSatoshis, int blockHeight)
            {
                if (blockHeight > MaxBlockHeight)
                {
                    throw new Exception("block height too high: " + blockHeight + " needs more than "
                                        + BlockHeightBits + " bits, which stop at " + MaxBlockHeight);
                }
                if (amountSatoshis > MaxAmountSatoshis) // supports up to ~10,995 BTC
                {
                    throw new Exception("Amount too high: " + amountSatoshis + " satoshis needs more"
                                        + " than " + AmountBits + " bits, which stop at "
                                        + MaxAmountSatoshis + " (" + MaxAmountSatoshis / 100000000
                                        + " BTC)");
                }

                // Neither is allowed to be negative, and not only because the arithmetic below
                // would carry the sign bits into the other field. A negative height or a negative
                // amount is a bug upstream, and one that silently packs is one found much later.
                if (blockHeight < 0)
                {
                    throw new Exception("block height is negative: " + blockHeight);
                }
                if (amountSatoshis < 0)
                {
                    throw new Exception("Amount is negative: " + amountSatoshis + " satoshis");
                }

                return (amountSatoshis << BlockHeightBits) | (Int64)blockHeight;
            }

            /// <summary>
            /// An array of these written to one file, as the fields and nothing else: From,
            /// AmountAndBlock, To, each eight bytes little endian, twenty four bytes a record,
            /// repeated. No header, no count, no padding - the file's length divided by twenty four
            /// is how many are in it, which is what loadFromDisk relies on.
            ///
            /// fileIndex only names the file. What it segments is the caller's business - one per
            /// blk file, one per fifty thousand blocks, one per run - and nothing here reads it
            /// back out of the contents, so a file loaded under the wrong index still loads.
            ///
            /// maxIndex is a count rather than an index, despite the name: it is one past the last
            /// record, so array.Length writes the whole array and 0 writes an empty file. The array
            /// is allocated at its full size and filled from the front, so every slot at or above
            /// maxIndex is a null the caller never assigned. Writing those is a
            /// NullReferenceException at best and a file full of zero records at worst, and it is
            /// the whole reason this takes the parameter.
            ///
            /// The file is opened FileMode.Create, so a run that writes fewer records than the last
            /// one leaves a shorter file rather than the tail of the old one behind the new ones.
            ///
            /// Written through one buffer of four thousand records rather than a BinaryWriter call
            /// per field, which is three system calls per record against one per hundred kilobytes.
            /// </summary>
            public static void saveToDisk(string savePath, int fileIndex, simpleTransaction[] array, int maxIndex)
            {
                if (array == null)
                {
                    throw new ArgumentNullException(nameof(array));
                }

                if (maxIndex < 0 || maxIndex > array.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(maxIndex), maxIndex,
                                                          "there are " + array.Length + " slots to"
                                                          + " write from, so a count outside 0 to"
                                                          + " that is the caller's count and the"
                                                          + " caller's array disagreeing");
                }

                Directory.CreateDirectory(savePath);
                string path = PathForIndex(savePath, fileIndex);

                byte[] buffer = new byte[RecordBytes * 4096];
                int at = 0;

                using var file = new FileStream(path, FileMode.Create, FileAccess.Write,
                                                FileShare.None, 1 << 20);

                for (int i = 0; i < maxIndex; i++)
                {
                    simpleTransaction record = array[i];

                    // A null below maxIndex is the same disagreement as an out of range maxIndex,
                    // only found one record at a time. Worth saying so: the alternative is the
                    // NullReferenceException the next line throws on its own, which names neither
                    // the slot nor the count it was supposed to be under.
                    if (record == null)
                    {
                        throw new InvalidDataException("slot " + i + " of the " + maxIndex
                                                       + " to write was never filled in");
                    }

                    BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(at, 8), record.From);
                    BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(at + 8, 8), record.AmountAndBlock);
                    BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(at + 16, 8), record.To);
                    at += RecordBytes;

                    if (at == buffer.Length)
                    {
                        file.Write(buffer, 0, at);
                        at = 0;
                    }
                }

                if (at > 0)
                {
                    file.Write(buffer, 0, at);
                }
            }

            /// <summary>
            /// The file back as an array, in the order it was written.
            ///
            /// A file whose length is not a whole number of records is a truncated write or another
            /// format altogether, and either way nothing after the break can be read - so it throws
            /// rather than returning the records up to it.
            ///
            /// ReadExactly rather than Read: a FileStream is allowed to hand back fewer bytes than
            /// were asked for, and a loop that assumes otherwise reads a shifted file and finds
            /// nothing wrong with it.
            /// </summary>
            public static simpleTransaction[] loadFromDisk(string loadPath, int fileIndex)
            {
                string path = PathForIndex(loadPath, fileIndex);
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException("there is no transaction file at " + path, path);
                }

                using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                FileShare.Read, 1 << 20);

                long length = file.Length;
                if (length % RecordBytes != 0)
                {
                    throw new InvalidDataException(path + " is " + length + " bytes, which is not a"
                                                   + " whole number of " + RecordBytes + " byte"
                                                   + " records - it is truncated, or it is not one"
                                                   + " of these files");
                }

                var loaded = new simpleTransaction[length / RecordBytes];

                byte[] buffer = new byte[RecordBytes * 4096];
                int done = 0;

                while (done < loaded.Length)
                {
                    int want = loaded.Length - done;
                    if (want > 4096)
                    {
                        want = 4096;
                    }

                    file.ReadExactly(buffer, 0, want * RecordBytes);

                    for (int i = 0; i < want; i++)
                    {
                        int at = i * RecordBytes;

                        var record = new simpleTransaction();
                        record.From = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(at, 8));
                        record.AmountAndBlock = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(at + 8, 8));
                        record.To = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(at + 16, 8));

                        loaded[done + i] = record;
                    }

                    done += want;
                }

                return loaded;
            }

            /// <summary>The file one index names, five digits so they sort the way they are
            /// numbered - the same shape as the blk files this all came out of.</summary>
            static string PathForIndex(string path, int fileIndex)
            {
                return Path.Combine(path, "simpletx" + fileIndex.ToString("D5") + ".dat");
            }
        }



        /// <summary>Blocks in one rocksdb store, fixed by the writer that fills them.</summary>
        const int BlocksPerStore = 25000;

        /// <summary>What the stores are called under the base directory, numbered from 1.</summary>
        const string StorePrefix = "blocks";

        /// <summary>Where the stores live when the caller does not say.</summary>
        const string DefaultStoreDirectory = "C:\\btcblock\\rocksdb";

        /// <summary>
        /// A run of blocks out of the rocksdb stores, in height order, as the same BlockRaw
        /// objects a blk file produces.
        ///
        /// The stores are written a fixed 50,000 blocks at a time from height 0 up, so a height
        /// says which store holds it and no index is needed to find out:
        ///
        ///     blocks1  0       to 49999
        ///     blocks2  50000   to 99999
        ///     blocks3  100000  to 149999
        ///     blocks4  150000  to 199999
        ///
        /// which is why the range has to land on those boundaries - a store is opened whole or
        /// not at all. Asking for less than everything is the point of saying so: the first store
        /// is 14 MB and the fourth is 1.9 GB, and a caller that only wants the early chain has no
        /// reason to wait for the rest of it.
        ///
        /// That mapping is an assumption about the data rather than something the store records:
        /// the writer counts blocks, not heights, so a chain with a gap in it would put store 3
        /// somewhere other than height 100000. Everything downstream - the UTXO set, the balances,
        /// the from-addresses in the SQLite index - is silently wrong rather than noisily broken if
        /// the blocks are not exactly the run they claim to be, so this ends by checking that what
        /// came back is the range that was asked for and that every block's parent is the block
        /// before it. A break in either throws rather than returning something that looks fine.
        /// </summary>
        /// <param name="startHeight">First height wanted. Must be the first height of a store, so
        /// a multiple of 50000.</param>
        /// <param name="lastHeight">Last height wanted, inclusive. Must be the last height of a
        /// store, so one less than a multiple of 50000 - 49999, 99999, and so on.</param>
        /// <param name="baseDirectory">Directory the numbered stores sit in. Empty for the usual
        /// place.</param>
        public static List<BlockRaw> LoadBlocks(int startHeight, int lastHeight, string baseDirectory)
        {
            if (startHeight < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(startHeight),
                    "startHeight cannot be negative - got " + startHeight);
            }

            if (startHeight % BlocksPerStore != 0)
            {
                throw new ArgumentException("startHeight must be the first height of a store, so a"
                    + " multiple of " + BlocksPerStore + " - got " + startHeight, nameof(startHeight));
            }

            if ((lastHeight + 1) % BlocksPerStore != 0)
            {
                throw new ArgumentException("lastHeight must be the last height of a store, so one"
                    + " less than a multiple of " + BlocksPerStore + " (" + (BlocksPerStore - 1)
                    + ", " + (BlocksPerStore * 2 - 1) + ", and so on) - got " + lastHeight,
                    nameof(lastHeight));
            }

            if (lastHeight < startHeight)
            {
                throw new ArgumentException("lastHeight " + lastHeight + " is below startHeight "
                    + startHeight, nameof(lastHeight));
            }

            string directory = baseDirectory;
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = DefaultStoreDirectory;
            }

            // The whole of the height-to-store mapping, and the reason the two arguments have to
            // land on store boundaries.
            int firstStore = startHeight / BlocksPerStore + 1;
            int lastStore = (lastHeight + 1) / BlocksPerStore;
            int stores = lastStore - firstStore + 1;
            int expected = lastHeight - startHeight + 1;

            // Named up front so the loop below and the error path agree on where a store lives.
            var storePaths = new string[stores];
            for (int i = 0; i < stores; i++)
            {
                storePaths[i] = Path.Combine(directory, StorePrefix + (firstStore + i));
            }

            // Checked before any of them is opened, so a range that reaches past what was written
            // says which store is missing instead of half-loading and failing the count at the
            // end. The writer stops at four stores, so anything above height 199999 lands here.
            foreach (string storePath in storePaths)
            {
                if (!Directory.Exists(storePath))
                {
                    throw new DirectoryNotFoundException("no rocksdb store at " + storePath
                        + " - heights " + startHeight + " to " + lastHeight + " need stores "
                        + firstStore + " to " + lastStore);
                }
            }

            Console.WriteLine("rocksdb      : heights " + startHeight + " to " + lastHeight
                              + " from " + stores + " store(s), " + StorePrefix + firstStore
                              + " to " + StorePrefix + lastStore);

            var loadClock = Stopwatch.StartNew();

            // One store per thread. They are separate databases in separate directories sharing
            // no handle, and LoadBlocksFromRocksDb keeps everything it touches local - its list,
            // its options, its RocksDb and its iterator are all created inside the call. So the
            // only thing crossing threads is the array each result is dropped into, and every task
            // owns one slot of it.
            //
            // Worth doing because most of the cost is not the disk: every block is re-hashed on
            // the way out to check it against the hash it is filed under, which is CPU work that
            // scales rather than queueing behind one disk head.
            var perStore = new List<BlockRaw>[stores];
            var summary = new string[stores];

            try
            {
                Parallel.For(0, stores, i =>
                {
                    List<BlockRaw> fromStore = LoadBlocksFromRocksDb(storePaths[i]);
                    perStore[i] = fromStore;

                    // Built here, printed below in store order. Writing to the console from inside
                    // the loop would put the lines down in whatever order the threads happened to
                    // finish, which reads like the stores are out of order.
                    if (fromStore.Count == 0)
                    {
                        summary[i] = "  " + Path.GetFileName(storePaths[i]) + " : empty";
                    }
                    else
                    {
                        summary[i] = "  " + Path.GetFileName(storePaths[i]) + " : " + fromStore.Count
                                     + " blocks, heights " + fromStore[0].BlockIndex
                                     + " to " + fromStore[fromStore.Count - 1].BlockIndex;
                    }
                });
            }
            catch (AggregateException ex)
            {
                // Parallel.For collects everything that threw and hands it back in one of these,
                // whose own Message is just "One or more errors occurred" - the part worth reading
                // is inside it. Printed and then rethrown as it stands, so the caller decides what
                // a failed load means and nothing arrives back looking like an empty chain.
                Console.Error.WriteLine("could not load the stores:");
                foreach (Exception inner in ex.Flatten().InnerExceptions)
                {
                    Console.Error.WriteLine("  " + inner.Message);
                }
                throw;
            }

            loadClock.Stop();

            foreach (string line in summary)
            {
                Console.WriteLine(line);
            }

            // Joined in store order, which is height order - the threads finish in whatever order
            // they like but nothing reads perStore until they are all done. Sized up front so the
            // list does not copy its whole array every time it grows past a couple of hundred
            // thousand entries.
            var loaded = new List<BlockRaw>(expected);
            foreach (List<BlockRaw> fromStore in perStore)
            {
                loaded.AddRange(fromStore);
            }

            Console.WriteLine("rocksdb loaded: " + loaded.Count + " blocks from " + stores
                              + " store(s) in " + loadClock.Elapsed.TotalSeconds.ToString("F2") + "s");

            // What was asked for is what came back. A store written short, or one holding heights
            // other than the ones its number implies, is caught here rather than three hours later
            // in a balance that does not reconcile.
            if (loaded.Count != expected)
            {
                throw new InvalidDataException("asked for heights " + startHeight + " to " + lastHeight
                    + ", which is " + expected + " blocks, and the stores hold " + loaded.Count);
            }

            if (loaded[0].BlockIndex != startHeight || loaded[loaded.Count - 1].BlockIndex != lastHeight)
            {
                throw new InvalidDataException("asked for heights " + startHeight + " to " + lastHeight
                    + " and got " + loaded[0].BlockIndex + " to " + loaded[loaded.Count - 1].BlockIndex);
            }

            // And they are a chain, in order, with nothing missing between them - each block's
            // parent being the one before it is the property everything downstream is built on,
            // and the join across stores is where it would break.
            int brokenLinks = 0;
            for (int i = 1; i < loaded.Count; i++)
            {
                if (loaded[i].GetPrevBlockHash() != loaded[i - 1].DisplayHash)
                {
                    brokenLinks++;
                    if (brokenLinks <= 5)
                    {
                        Console.Error.WriteLine("  break at height " + loaded[i].BlockIndex
                                                + ": parent is " + loaded[i].GetPrevBlockHash()
                                                + ", previous block is " + loaded[i - 1].DisplayHash);
                    }
                }
            }

            if (brokenLinks > 0)
            {
                // Thrown rather than warned about. A run with a break in it still walks, still
                // parses and still produces balances - wrong ones, with nothing about them to say
                // so. Turn this into a Console.Error line if a broken run is ever worth having.
                throw new InvalidDataException(brokenLinks + " blocks do not follow the block before"
                    + " them - the run from " + startHeight + " to " + lastHeight + " is not a chain");
            }

            Console.WriteLine("  chain      : all " + (loaded.Count - 1) + " links hold");
            Console.WriteLine("  first      : height " + loaded[0].BlockIndex + " " + loaded[0].DisplayHash);
            Console.WriteLine("  last       : height " + loaded[loaded.Count - 1].BlockIndex
                              + " " + loaded[loaded.Count - 1].DisplayHash);

            return loaded;
        }


        //junk

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
  --dir <path>                directory holding the blk files (default " + @")
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

            public string timestamp = ""; //68â€“71	e3c86849 Timestamp   Reverses to 0x4968c8e3 = 1,231,603,939 = Jan 10, 2009 16:12:19 UTC

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

        /// <summary>
        /// Walks every blk#####.dat in a directory and prints one line per file: how many blocks
        /// it holds, its lowest and highest header timestamp, and the span between them. The
        /// lowest and highest across all the files together follow at the end.
        ///
        /// Only the 8-byte record headers and the 80-byte block headers are read - block bodies
        /// are seeked over and nothing is hashed - so a directory of 128 MiB files is walked in
        /// about the time it takes to read them off disk.
        ///
        /// The ranges will overlap between files, and the earliest block in a file is usually not
        /// the first one in it: MainBlockDownload writes blocks in the order they arrive off the
        /// wire, which is not height order, and a header timestamp is the miner's own clock
        /// rather than a verified time.
        /// </summary>
        public static void PrintTimestampRangePerFile(string directory, int MAXBLKDATFILE)
        {
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException("no such directory: " + directory);

            byte[] key = ReadXorKey(directory);

            var paths = new List<string>();
            foreach (string file in Directory.EnumerateFiles(directory, "blk*.dat"))
            {
                // A three-character extension in the pattern is treated as a PREFIX match on
                // Windows, so "*.dat" also hands back blk00000.database. Check the real ending.
                if (!file.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)) continue;
                paths.Add(file);
            }

            // The indexes are zero padded to five digits, so ordinal order is numeric order.
            paths.Sort(StringComparer.OrdinalIgnoreCase);

            Console.WriteLine();
            Console.WriteLine("timestamp range per file in " + directory);

            if (paths.Count == 0)
            {
                Console.WriteLine("  no blk*.dat files here");
                return;
            }

            Console.WriteLine("  " + "file".PadRight(14) + " " + "blocks".PadLeft(7)
                              + "  " + "lowest".PadRight(20) + "  " + "highest".PadRight(20) + "  span");

            long totalBlocks = 0;
            uint overallLowest = uint.MaxValue;
            uint overallHighest = uint.MinValue;


            int count = 0;
            foreach (string path in paths)
            {
                List<TimestampedRecord> records = ReadTimestampedRecords(path, key, withHashes: false);
                totalBlocks += records.Count;

                if (records.Count == 0)
                {
                    Console.WriteLine("  " + Path.GetFileName(path).PadRight(14) + " " + "0".PadLeft(7) + "  empty");
                    continue;
                }

                uint lowest = records[0].UnixTime;
                uint highest = records[0].UnixTime;
                foreach (TimestampedRecord r in records)
                {
                    if (r.UnixTime < lowest) lowest = r.UnixTime;
                    if (r.UnixTime > highest) highest = r.UnixTime;
                }

                if (lowest < overallLowest) overallLowest = lowest;
                if (highest > overallHighest) overallHighest = highest;

                Console.WriteLine("  " + Path.GetFileName(path).PadRight(14)
                                  + " " + records.Count.ToString().PadLeft(7)
                                  + "  " + FormatUnixTime(lowest).PadRight(20)
                                  + "  " + FormatUnixTime(highest).PadRight(20)
                                  + "  " + FormatGap((long)highest - lowest));

                if (count++ > MAXBLKDATFILE)
                    break;

            }

            Console.WriteLine();
            Console.WriteLine("  " + paths.Count + " files, " + totalBlocks + " blocks");
            if (totalBlocks > 0)
            {
                Console.WriteLine("  lowest  " + FormatUnixTime(overallLowest) + "  (unix " + overallLowest + ")");
                Console.WriteLine("  highest " + FormatUnixTime(overallHighest) + "  (unix " + overallHighest + ")");
                Console.WriteLine("  span    " + FormatGap((long)overallHighest - overallLowest));
            }
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

        /// <summary>A header timestamp as "2009-01-10 16:12:19Z".</summary>
        static string FormatUnixTime(uint unixTime) =>
            DateTimeOffset.FromUnixTimeSeconds(unixTime).UtcDateTime.ToString("u");

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
            //Everything that is commentary about the run â€” the field dump at lines 592â€“605, "not found", the reorder summary at 1512 â€” goes to Console.Error so it stays on the terminal and never contaminates that redirected file. Same convention as curl, dd, or ffmpeg: stdout carries data, stderr carries progress and diagnostics. Both interleave normally when nobody redirects, so you lose nothing in interactive use.
            //Two other properties that matter for the line you asked about:
            //-Console.Error is auto - flushed, Console.Out is not necessarily.If the process throws partway through a reorder, the "rewrote N blocks" messages already emitted are guaranteed to have surfaced.
            //- It's a status line, not a result. The caller gets sorted.Count as the return value at line 1515; the text is for a human watching.
            //That said, this file isn't consistent about it. The --help text (566), the argument-error message (157), and the "no arguments" notice (464) are also on stderr, which is right, but the tables at 1353â€“1384 and the summaries at 375â€“394 use Console.WriteLine â€” stdout â€” even though they're equally diagnostic.And lines 1488 / 1498("go", "write fake block33") are breakpoint bait on stdout.If you ever pipe the hex output for real, those would need moving to stderr.
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
        public static BlockRaw? FindBlockByHash(string directory, int fileIndex, string displayHash)
        {
            byte[] want = ReverseCopy(Convert.FromHexString(NormalizeHash(displayHash)));
            return ScanBlkFile(directory, fileIndex, want, -1);
        }

        /// <summary>
        /// Takes the blockIndex'th block in the file, counting record by record from 0. Returns
        /// null if the file holds fewer blocks than that; blocksScanned is then the total.
        /// </summary>
        public static BlockRaw? FindBlockByPosition(string directory, int fileIndex, int blockIndex)
        {
            if (blockIndex < 0) throw new ArgumentOutOfRangeException(nameof(blockIndex), "block position starts at 0");
            return ScanBlkFile(directory, fileIndex, null, blockIndex);
        }

        /// <summary>
        /// One pass over a blk file's records. Exactly one of wantHash (internal byte order) and
        /// wantIndex (0-based position, -1 when unused) selects the block.
        /// </summary>
        static BlockRaw? ScanBlkFile(string directory, int fileIndex, byte[]? wantHash, int wantIndex)
        {
            int blocksScanned = 0;

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

        // ------------------------------------------------------------------------------------
        // Raw bytes -> a parsed SatoshiSharpLib.Block
        // ------------------------------------------------------------------------------------

        /// <summary>
        /// Turns the bytes of one block into a Block with its header and Transactions filled in.
        ///
        /// The layout after the 80 byte header is a varint transaction count and then that many
        /// transactions back to back, each of them:
        ///
        ///     version[4] inCount[varint] (txid[32] vout[4] scriptSig[varint+n] sequence[4])*
        ///                outCount[varint] (value[8] scriptPubKey[varint+n])* lockTime[4]
        ///
        /// blockNumber is written to header.BlockNumber - pass the height, since nothing in the
        /// bytes themselves says where the block sits in the chain.
        ///
        /// Note this does NOT go through Transaction.readTransactionBytes. That one calls
        /// Helpers.readSignedSpend on every output, which assumes bare P2PK (it strips one leading
        /// and one trailing byte to get a pubkey), prints three lines per output, and throws on a
        /// script shorter than two bytes. Fine for the genesis era it was written for; over a whole
        /// file it is both wrong and unusably slow. Use that path when the wallet tracking is what
        /// you are after - this one when you want the transactions.
        /// </summary>
        public static Block ParseBlock(BlockRaw raw, int blockNumber)
        {
            if (raw is null) throw new ArgumentNullException(nameof(raw));

            Block block = new Block();
            block.header = Block.Header.Parse(raw.Raw);        // reads the first 80 bytes
            block.header.BlockNumber = blockNumber;
            block.header.Hash = raw.DisplayHash;               // already computed when it was read

            int pos = 80;
            ulong txCount = ReadVarInt(raw.Raw, ref pos);
            block.header.TransactionCount = txCount;

            for (ulong i = 0; i < txCount; i++)
            {
                Transaction tx = ReadTransaction(raw.Raw, ref pos);

                // Stamped here rather than at the call site that collects them: a transaction
                // knows nothing about where it came from once it is out of the block, and this is
                // the last point where both are in hand. Every caller of ParseBlock gets it.
                tx.BlockHeight = blockNumber;

                block.Transactions.Add(tx);
            }

            // The record's size field already said where this block ends, so landing anywhere else
            // means the walk went wrong. Without this a short read surfaces much later as a merkle
            // mismatch, with nothing to say which transaction lost the thread.
            if (pos != raw.Raw.Length)
            {
                throw new InvalidDataException("block " + raw.DisplayHash + ": " + txCount
                                               + " transactions ran to byte " + pos + " of " + raw.Raw.Length);
            }
            //block 1 transaction hash 0e3e2357e806b6cdb1f70b54c3a3a17b6714ee1f0e68bebb44a74b1efd512098
            return block;
        }

        /// <summary>
        /// Reads one transaction starting at pos and leaves pos on the byte after it.
        /// </summary>
        static Transaction ReadTransaction(byte[] data, ref int pos)
        {
            var tx = new Transaction();

            int start = pos;                                   // kept so the txid can be hashed

            tx.Version = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));
            pos += 4;

            // BIP144: a real transaction always has at least one input, so a zero where the input
            // count belongs is the segwit marker, with the flag byte behind it. Nothing in this
            // data is segwit - it activated at height 481,824 - but MainBlockDownload asks peers
            // for witness serialization, so a modern block would otherwise parse into nonsense.
            bool hasWitness = false;
            if (data[pos] == 0x00)
            {
                hasWitness = true;
                pos += 2;                                      // marker 0x00, flag 0x01
            }

            ulong inputCount = ReadVarInt(data, ref pos);
            for (ulong i = 0; i < inputCount; i++)
            {
                var input = new Transaction.TxInput();

                input.TxId = data.AsSpan(pos, 32).ToArray();
                pos += 32;
                input.Vout = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));
                pos += 4;

                ulong scriptLength = ReadVarInt(data, ref pos);
                input.ScriptSig = data.AsSpan(pos, (int)scriptLength).ToArray();
                pos += (int)scriptLength;

                input.Sequence = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));
                pos += 4;

                tx.Inputs.Add(input);
            }

            ulong outputCount = ReadVarInt(data, ref pos);
            for (ulong i = 0; i < outputCount; i++)
            {
                var output = new Transaction.TxOutput();

                output.Value = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos, 8));
                pos += 8;

                ulong scriptLength = ReadVarInt(data, ref pos);
                output.ScriptPubKey = data.AsSpan(pos, (int)scriptLength).ToArray();
                pos += (int)scriptLength;

                tx.Outputs.Add(output);
            }

            if (hasWitness)
            {
                // Stepped over rather than kept: Transaction has nowhere to put witness data, and
                // it is not part of the txid, so dropping it leaves the merkle root correct.
                for (ulong i = 0; i < inputCount; i++)
                {
                    ulong items = ReadVarInt(data, ref pos);
                    for (ulong j = 0; j < items; j++)
                    {
                        ulong length = ReadVarInt(data, ref pos);
                        pos += (int)length;
                    }
                }
            }

            tx.LockTime = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));
            pos += 4;

            // Without a witness the bytes just walked over ARE the stripped serialization, so the
            // txid is a hash of that slice - no need to build the transaction back up again. With
            // one they are not: the txid has to skip the marker, flag and witness stacks, which is
            // exactly what SerializeTransaction leaves out.
            if (hasWitness)
            {
                tx.Hash = Transaction.ComputeHash(tx.SerializeTransaction());
            }
            else
            {
                tx.Hash = Transaction.ComputeHash(data.AsSpan(start, pos - start));
            }

            // The span the walk just covered. start and pos are the only two numbers needed to
            // find this transaction in the block again, and this is the one place that has both -
            // ParseBlock sees pos move but never learns where each transaction began.
            tx.ByteOffset = start;
            tx.ByteLength = pos - start;

            return tx;
        }

        /// <summary>
        /// Re-serializes every transaction, rebuilds the merkle root from them and compares it to
        /// the one in the header. This is the check that the transaction walk read exactly the
        /// right bytes: any input or output whose length was misread changes a txid, and a changed
        /// txid changes the root.
        /// </summary>
        public static bool MerkleRootMatches(Block block)
        {
            if (block.Transactions.Count == 0) return false;

            var serialized = new List<byte[]>(block.Transactions.Count);
            foreach (Transaction tx in block.Transactions)
            {
                serialized.Add(tx.SerializeTransaction());
            }

            byte[] computed = Block.CalculateMerkleRoot(serialized);

            return string.Equals(Helpers.GetStringReverseHexBytes(computed),
                                 block.header.GetMerkleRootAsString(),
                                 StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------------------------
        // RocksDB block store
        // ------------------------------------------------------------------------------------

        // Key layout. Every key carries a one byte prefix so the different kinds share one
        // keyspace without colliding, which is the same shape Bitcoin Core's block index uses.
        //
        //   'b' + hash[32]     -> the serialized block
        //   'h' + height[4]    -> the hash of the block at that height
        //   'T'                -> the hash of the tip
        //   'M'                -> count[4] and tip height[4]
        //
        // Hashes are stored in internal (little endian) order, matching Transaction.Hash and
        // TxInput.TxId rather than the reversed form explorers show.
        const byte BlockPrefix = (byte)'b';
        const byte HeightPrefix = (byte)'h';

        static readonly byte[] TipKey = { (byte)'T' };
        static readonly byte[] MetaKey = { (byte)'M' };

        /// <summary>Blocks per write batch - a batch is held in memory until it is written.</summary>
        const int BlocksPerBatch = 2000;

        static byte[] BlockKey(byte[] internalHash)
        {
            byte[] key = new byte[33];
            key[0] = BlockPrefix;
            internalHash.CopyTo(key, 1);
            return key;
        }

        /// <summary>
        /// Height keys are BIG endian on purpose. RocksDB orders keys by their bytes, so a big
        /// endian height means iterating the 'h' keys walks the chain from genesis upwards -
        /// little endian would interleave heights 1, 256, 512 and so on.
        /// </summary>
        static byte[] HeightKey(int height)
        {
            byte[] key = new byte[5];
            key[0] = HeightPrefix;
            BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(1, 4), height);
            return key;
        }

        /// <summary>The block's hash as the 32 bytes it is stored as, not the display string.</summary>
        static byte[] InternalHashBytes(BlockRaw raw)
        {
            return ReverseCopy(Convert.FromHexString(raw.DisplayHash));
        }

        /// <summary>
        /// Writes every block on a chain into a RocksDB database, keyed by hash with a height
        /// index beside it, and reads a sample back to check the round trip. Creates the database
        /// when it is not there; re-running overwrites the same keys rather than duplicating them,
        /// so it is safe to run twice.
        ///
        /// Pass the root of the chain - ChainState.blockZero - and it follows nextLink to the tip.
        ///
        /// Returns how many blocks were written.
        /// </summary>
        public static int SaveBlocksToRocksDb(string dbPath, MyBlock<BlockRaw>? chainStart, int verifyEvery = 5000)
        {
            Directory.CreateDirectory(dbPath);

            var options = new DbOptions().SetCreateIfMissing(true);

            int written = 0;
            int verified = 0;
            int roundTripFailures = 0;
            int tipHeight = -1;
            byte[] tipHash = Array.Empty<byte>();

            using (RocksDb db = RocksDb.Open(options, dbPath))
            {
                var batch = new WriteBatch();
                int inBatch = 0;

                MyBlock<BlockRaw>? at = chainStart;
                while (at != null)
                {
                    byte[] hash = InternalHashBytes(at.data);

                    batch.Put(BlockKey(hash), at.data.Raw);
                    batch.Put(HeightKey(at.height), hash);

                    tipHash = hash;
                    tipHeight = at.height;
                    written++;
                    inBatch++;

                    // Whole chain in one batch would be the entire file plus overhead sitting in
                    // memory before a single byte reached disk.
                    if (inBatch >= BlocksPerBatch)
                    {
                        db.Write(batch);
                        batch.Dispose();
                        batch = new WriteBatch();
                        inBatch = 0;
                    }

                    at = at.nextLink;
                }

                if (inBatch > 0)
                {
                    db.Write(batch);
                }
                batch.Dispose();

                if (written > 0)
                {
                    byte[] meta = new byte[8];
                    BinaryPrimitives.WriteInt32LittleEndian(meta.AsSpan(0, 4), written);
                    BinaryPrimitives.WriteInt32LittleEndian(meta.AsSpan(4, 4), tipHeight);

                    db.Put(TipKey, tipHash);
                    db.Put(MetaKey, meta);
                }

                // Read a sample back while the database is still open. Bytes in equals bytes out,
                // and the height index has to lead to the same block the hash key does.
                if (verifyEvery > 0)
                {
                    MyBlock<BlockRaw>? check = chainStart;
                    while (check != null)
                    {
                        if (check.height % verifyEvery == 0)
                        {
                            verified++;

                            byte[]? hashAtHeight = db.Get(HeightKey(check.height));
                            byte[] expectedHash = InternalHashBytes(check.data);

                            bool good = hashAtHeight != null
                                        && hashAtHeight.AsSpan().SequenceEqual(expectedHash);
                            if (good)
                            {
                                byte[]? stored = db.Get(BlockKey(hashAtHeight!));
                                good = stored != null && stored.AsSpan().SequenceEqual(check.data.Raw);
                            }

                            if (!good)
                            {
                                roundTripFailures++;
                                Console.Error.WriteLine("rocksdb round trip failed at height " + check.height);
                            }
                        }
                        check = check.nextLink;
                    }
                }
            }

            Console.WriteLine("rocksdb      : " + written + " blocks written to " + dbPath);
            Console.WriteLine("  tip        : height " + tipHeight + " " + ToDisplayHex(tipHash));
            Console.WriteLine("  round trip : " + (verified - roundTripFailures) + " of " + verified + " sampled blocks match");

            return written;
        }

        /// <summary>
        /// Reads every block back out of the store, in height order, as the same BlockRaw objects
        /// ReadAllBlocks hands back - so anything that works on blocks read from a blk file works
        /// on these unchanged.
        ///
        /// The height index is walked with an iterator rather than by asking for height 0, then 1,
        /// and so on: keys come back in order for free, and a gap in the heights is skipped rather
        /// than ending the walk. Offset is -1 on these, since a row in a database has no offset in
        /// a file, and BlockIndex holds the height.
        ///
        /// Every block is re-hashed and checked against the hash it is filed under. A block whose
        /// bytes rotted on disk would otherwise be handed back wearing the hash it used to have.
        /// </summary>
        public static List<BlockRaw> LoadBlocksFromRocksDb(string dbPath)
        {
            if (!Directory.Exists(dbPath))
            {
                throw new DirectoryNotFoundException("no rocksdb store at " + dbPath
                                                     + " - nothing has been saved there yet");
            }

            var blocks = new List<BlockRaw>();
            var options = new DbOptions().SetCreateIfMissing(false);
            int damaged = 0;

            using (RocksDb db = RocksDb.Open(options, dbPath))
            {
                using (Iterator it = db.NewIterator())
                {
                    byte[] prefix = { HeightPrefix };
                    it.Seek(prefix);

                    while (it.Valid())
                    {
                        byte[] key = it.Key();
                        if (key.Length != 5 || key[0] != HeightPrefix)
                        {
                            break;                       // past the last 'h' key
                        }

                        int height = BinaryPrimitives.ReadInt32BigEndian(key.AsSpan(1, 4));
                        byte[] indexedHash = it.Value();

                        byte[]? raw = db.Get(BlockKey(indexedHash));
                        if (raw == null)
                        {
                            damaged++;
                            Console.Error.WriteLine("height " + height + " is indexed but its block is not there");
                            it.Next();
                            continue;
                        }

                        byte[] actualHash = DoubleSha256(raw, 0, 80);
                        if (!actualHash.AsSpan().SequenceEqual(indexedHash))
                        {
                            damaged++;
                            Console.Error.WriteLine("height " + height + " does not hash to the hash it is filed under");
                        }

                        var block = new BlockRaw
                        {
                            Path = "rocksdb:" + dbPath,
                            BlockIndex = height,         // height, not a position in a file
                            Offset = -1,
                            Size = raw.Length,
                            Raw = raw,
                            DisplayHash = ToDisplayHex(actualHash),
                        };
                        block.SetHeaderFields();
                        blocks.Add(block);

                        it.Next();
                    }
                }
            }

            if (damaged > 0)
            {
                Console.Error.WriteLine(damaged + " blocks in the store are damaged");
            }

            return blocks;
        }

        /// <summary>
        /// Pulls one block back out of the store by height, as the raw serialized bytes. Null when
        /// the database does not hold that height. Opens the database for the one read, so this is
        /// for spot checks rather than a loop - a loop should open it once itself.
        /// </summary>
        public static byte[]? ReadBlockFromRocksDb(string dbPath, int height)
        {
            var options = new DbOptions().SetCreateIfMissing(false);

            using (RocksDb db = RocksDb.Open(options, dbPath))
            {
                byte[]? hash = db.Get(HeightKey(height));
                if (hash == null)
                {
                    return null;
                }
                return db.Get(BlockKey(hash));
            }
        }

        // ------------------------------------------------------------------------------------
        // End to end check of a blk file
        // ------------------------------------------------------------------------------------

        /// <summary>
        /// Blocks whose height on mainnet is not in doubt, used to check that the heights the
        /// harness works out are real Bitcoin heights and not just a counter that starts somewhere.
        /// Display order, the way an explorer shows them.
        /// </summary>
        static readonly Dictionary<int, string> KnownMainnetHeights = new Dictionary<int, string>
        {
            [0] = "000000000019d6689c085ae165831e934ff763ae46a2a6c172b3f1b60a8ce26f",
            [1] = "00000000839a8e6886ab5951d76f411475428afc90947ee320161bbf18eb6048",
            [32] = "00000000e5cb7c6c273547b0c9421b01e23310ed83f934b96270f35a4d66f6e3",
            [33] = "00000000a87073ea3d7af299e02a434598b9c92094afa552e0711afcc0857962",
            [34] = "00000000a73fb23b6c42b18b3253ed29c5d0c80d84624efa12c2cf05c4b4318f",
        };

        /// <summary>
        /// Reads one blk file, builds the longest chain out of it, parses every block on that chain
        /// and checks the result. Prints a report; returns false if any check failed.
        ///
        /// What each step actually proves:
        ///   - ReadAllBlocks and CountBlocksInFile agree on how many blocks the file holds, so the
        ///     bulk read is not quietly stopping early,
        ///   - the chain links up - every block's prevHash is the block before it, walking forwards
        ///     from blockZero, and that walk ends on possibleTip,
        ///   - the heights are real Bitcoin heights, checked against hashes known from mainnet,
        ///   - the transaction walk is byte exact: every merkle root is rebuilt from the parsed
        ///     transactions and compared to the one in the header, which no misread script length
        ///     can survive.
        ///
        /// Blocks are fed to the harness under their full 64 character hashes rather than a
        /// shortened tail, so ChainState.rootPrevHash has to be the 64 zero string for genesis to
        /// be recognised as a root instead of parking forever on a parent nobody has.
        ///
        /// traceChainBuild leaves the harness's per block tracing on. It is off by default because
        /// a full file is over a hundred thousand lines of it.
        /// </summary>
        public static bool VerifyBlockFile(string directory, int fileIndex, bool traceChainBuild = false)
        {
            bool allPassed = true;

            // 1. Read the file, and check the two readers agree.
            var clock = Stopwatch.StartNew();
            List<BlockRaw> all = ReadAllBlocks(directory, fileIndex);
            clock.Stop();

            int counted = CountBlocksInFile(directory, fileIndex);

            Console.WriteLine(BlkFilePath(directory, fileIndex));
            Console.WriteLine("  read         : " + all.Count + " blocks in "
                              + clock.Elapsed.TotalSeconds.ToString("F2") + "s");
            if (counted != all.Count)
            {
                Console.WriteLine("  FAILED       : CountBlocksInFile says " + counted
                                  + ", ReadAllBlocks says " + all.Count);
                allPassed = false;
            }

            // 2. Build the longest chain. Genesis has 64 zeros where a parent would go.
            var state = new ChainState<BlockRaw>();
            state.rootPrevHash = NoParentHash;

            TextWriter console = Console.Out;
            if (!traceChainBuild)
            {
                Console.SetOut(TextWriter.Null);
            }
            clock.Restart();
            try
            {
                foreach (BlockRaw raw in all)
                {
                    BuildLongestChain(new MyRawBlock<BlockRaw>
                    {
                        hash = raw.DisplayHash,
                        prevHash = raw.GetPrevBlockHash(),
                        data = raw
                    }, state);
                }
                SetNextLinks(state);
            }
            finally
            {
                Console.SetOut(console);
            }
            clock.Stop();

            if (state.blockZero == null || state.possibleTip == null)
            {
                Console.WriteLine("  FAILED       : nothing linked up - no blockZero or no tip");
                return false;
            }

            Console.WriteLine("  chain built  : " + clock.Elapsed.TotalSeconds.ToString("F2") + "s, "
                              + state.waitingOnParent.Count + " still parked");
            Console.WriteLine("  blockZero    : " + state.blockZero.hash + " at height " + state.blockZero.height);
            Console.WriteLine("  possibleTip  : " + state.possibleTip.hash + " at height " + state.possibleTip.height);
            Console.WriteLine("  cache        : " + RecentCacheStats<BlockRaw>() + " of parent lookups");

            // 3. Walk it forwards, checking the links and the known heights as we go, and parse
            //    each block on the way past so the file is only walked once.
            clock.Restart();

            int walked = 0;
            int merkleMismatches = 0;
            int heightsChecked = 0;
            int singleTxBlocks = 0;
            int txHashMismatches = 0;
            long totalTransactions = 0;
            long totalInputs = 0;
            long totalOutputs = 0;
            ulong totalOutputSats = 0;
            string? previousHash = null;
            Block? genesis = null;

            MyBlock<BlockRaw>? at = state.blockZero;
            while (at != null)
            {
                if (previousHash != null && at.prevHash != previousHash)
                {
                    Console.WriteLine("  FAILED       : height " + at.height + " names parent "
                                      + at.prevHash + ", but the block before it is " + previousHash);
                    allPassed = false;
                }
                previousHash = at.hash;

                string knownHash;
                if (KnownMainnetHeights.TryGetValue(at.height, out knownHash))
                {
                    heightsChecked++;
                    if (at.hash != knownHash)
                    {
                        Console.WriteLine("  FAILED       : height " + at.height + " is " + at.hash
                                          + ", mainnet has " + knownHash);
                        allPassed = false;
                    }
                }

                Block parsed = ParseBlock(at.data, at.height);
                if (at.height == 0)
                {
                    genesis = parsed;
                }

                totalTransactions += parsed.Transactions.Count;
                foreach (Transaction tx in parsed.Transactions)
                {
                    totalInputs += tx.Inputs.Count;
                    totalOutputs += tx.Outputs.Count;
                    foreach (Transaction.TxOutput output in tx.Outputs)
                    {
                        totalOutputSats += output.Value;
                    }
                }

                // A block holding one transaction has that transaction's txid as its merkle root,
                // so this checks the Hash the parser worked out against something the header
                // already committed to - and most of the early chain is one-transaction blocks.
                if (parsed.Transactions.Count == 1)
                {
                    singleTxBlocks++;
                    if (!string.Equals(ToDisplayHex(parsed.Transactions[0].Hash),
                                       parsed.header.GetMerkleRootAsString(),
                                       StringComparison.OrdinalIgnoreCase))
                    {
                        txHashMismatches++;
                        if (txHashMismatches <= 5)
                        {
                            Console.WriteLine("  txid bad     : height " + parsed.header.BlockNumber
                                              + " " + ToDisplayHex(parsed.Transactions[0].Hash));
                        }
                    }
                }

                // Counted rather than thrown on, so one bad block does not hide the state of the
                // other hundred thousand.
                if (!MerkleRootMatches(parsed))
                {
                    merkleMismatches++;
                    if (merkleMismatches <= 5)
                    {
                        Console.WriteLine("  merkle bad   : height " + parsed.header.BlockNumber
                                          + " " + parsed.header.Hash);
                    }
                }

                walked++;
                at = at.nextLink;
            }
            clock.Stop();

            if (previousHash != state.possibleTip.hash)
            {
                Console.WriteLine("  FAILED       : the walk from blockZero ended on " + previousHash
                                  + ", not on the tip");
                allPassed = false;
            }

            Console.WriteLine("  walked       : " + walked + " blocks parsed in "
                              + clock.Elapsed.TotalSeconds.ToString("F2") + "s");
            Console.WriteLine("  transactions : " + totalTransactions);
            Console.WriteLine("  inputs       : " + totalInputs + ", outputs " + totalOutputs);
            Console.WriteLine("  output value : " + (totalOutputSats / 100000000.0).ToString("F8") + " BTC");
            Console.WriteLine("  heights      : " + heightsChecked + " checked against mainnet");

            if (merkleMismatches == 0)
            {
                Console.WriteLine("  merkle roots : all " + walked + " match");
            }
            else
            {
                Console.WriteLine("  merkle roots : " + merkleMismatches + " of " + walked + " DO NOT match");
                allPassed = false;
            }

            if (txHashMismatches == 0)
            {
                Console.WriteLine("  txids        : all " + singleTxBlocks
                                  + " one-transaction blocks agree with their merkle root");
            }
            else
            {
                Console.WriteLine("  txids        : " + txHashMismatches + " of " + singleTxBlocks
                                  + " DO NOT match their merkle root");
                allPassed = false;
            }

            // 4. Genesis is worth its own look: one coinbase paying 50 BTC, carrying the headline.
            if (genesis != null)
            {
                Transaction coinbase = genesis.Transactions[0];
                string scriptText = System.Text.Encoding.ASCII.GetString(coinbase.Inputs[0].ScriptSig);
                bool headline = scriptText.Contains("Chancellor on brink of second bailout for banks");

                // The genesis coinbase txid, which every explorer agrees on. It is also the one
                // txid that is NOT spendable, but that does not change what it hashes to.
                const string GenesisTxId = "4a5e1e4baab89f3a32518a88c31bc87f618f76673e2cc77ab2127b7afdeda33b";
                string coinbaseTxId = ToDisplayHex(coinbase.Hash);

                Console.WriteLine("  genesis      : " + genesis.Transactions.Count + " tx, "
                                  + coinbase.Outputs[0].Value + " sats, headline present " + headline);
                Console.WriteLine("  genesis txid : " + coinbaseTxId);

                if (coinbase.Outputs[0].Value != 5000000000 || !headline)
                {
                    Console.WriteLine("  FAILED       : genesis coinbase is not what it should be");
                    allPassed = false;
                }

                if (coinbaseTxId != GenesisTxId)
                {
                    Console.WriteLine("  FAILED       : genesis txid should be " + GenesisTxId);
                    allPassed = false;
                }
            }

            string result = "FAILURES above";
            if (allPassed)
            {
                result = "all checks passed";
            }
            Console.WriteLine("  result       : " + result);

            return allPassed;
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
        // Addresses - turning an output script into the address an explorer would show
        // ------------------------------------------------------------------------------------

        /// <summary>SHA-256 then RIPEMD-160, the hash every base58 address is built on.</summary>
        static void Hash160(ReadOnlySpan<byte> data, Span<byte> result20)
        {
            Span<byte> sha = stackalloc byte[32];
            SHA256.HashData(data, sha);

            // BouncyCastle rather than the hand-rolled RIPEMD160Managed in the library: .NET
            // dropped RIPEMD-160 after .NET Framework, and this is the digest the live code paths
            // in this repo already use. It wants arrays, so this is the one copy that stays.
            var ripemd = new Org.BouncyCastle.Crypto.Digests.RipeMD160Digest();
            ripemd.BlockUpdate(sha.ToArray(), 0, 32);
            byte[] digest = new byte[20];
            ripemd.DoFinal(digest, 0);
            digest.CopyTo(result20);
        }

        const string Base58Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

        /// <summary>
        /// Base58, as long division over a stack buffer.
        ///
        /// Helpers.Base58Encode produces the same string, but does it with repeated
        /// BigInteger.DivideAndRemainder - a fresh BigInteger allocated per digit, about 34 of them
        /// per address. Measured at ~49 microseconds a call, which made deriving addresses 82% of
        /// the time spent building the index and dwarfed both parsing the blocks and writing the
        /// rows. This allocates nothing but the string it returns.
        /// </summary>
        public static string Base58Encode(ReadOnlySpan<byte> input)
        {
            int zeroes = 0;
            while (zeroes < input.Length && input[zeroes] == 0) zeroes++;

            // log(256)/log(58) is a shade under 1.37, so this is always enough room.
            int size = (input.Length - zeroes) * 138 / 100 + 1;
            Span<byte> buffer = stackalloc byte[size];
            buffer.Clear();

            int length = 0;
            for (int i = zeroes; i < input.Length; i++)
            {
                int carry = input[i];
                int digits = 0;
                for (int k = size - 1; (carry != 0 || digits < length) && k >= 0; k--, digits++)
                {
                    carry += 256 * buffer[k];
                    buffer[k] = (byte)(carry % 58);
                    carry /= 58;
                }
                length = digits;
            }

            int start = size - length;
            Span<char> chars = stackalloc char[zeroes + length];
            for (int i = 0; i < zeroes; i++) chars[i] = '1';
            for (int i = 0; i < length; i++) chars[zeroes + i] = Base58Alphabet[buffer[start + i]];
            return new string(chars);
        }

        /// <summary>
        /// Base58Check: a version byte, the payload, and the first four bytes of the double SHA-256
        /// of both as a checksum, all base58 encoded.
        /// </summary>
        static string Base58Check(byte version, ReadOnlySpan<byte> payload)
        {
            Span<byte> full = stackalloc byte[payload.Length + 5];
            full[0] = version;
            payload.CopyTo(full.Slice(1));

            Span<byte> once = stackalloc byte[32];
            Span<byte> twice = stackalloc byte[32];
            SHA256.HashData(full.Slice(0, payload.Length + 1), once);
            SHA256.HashData(once, twice);
            twice.Slice(0, 4).CopyTo(full.Slice(payload.Length + 1));

            return Base58Encode(full);
        }

        /// <summary>
        /// The bytes a Base58Check string encodes - version, payload and the four checksum bytes -
        /// with the checksum verified. The inverse of Base58Check above, and long multiplication
        /// where that is long division, over the same kind of stack buffer.
        ///
        /// The checksum is what makes this worth having over a plain decode: an address with a
        /// character wrong decodes perfectly well into 25 bytes that are simply not the address
        /// anybody meant, and nothing downstream would ever notice. Two SHA-256s per call say so
        /// at the door instead.
        /// </summary>
        static byte[] Base58CheckDecode(string text)
        {
            // Leading '1's are leading zero bytes - the same convention Base58Encode writes them
            // out with - and carry no value, so they are counted here and put back at the end.
            int zeroes = 0;
            while (zeroes < text.Length && text[zeroes] == '1') zeroes++;

            // log(58)/log(256) is a shade over 0.732, so this is always enough room.
            int size = (text.Length - zeroes) * 733 / 1000 + 1;
            Span<byte> buffer = stackalloc byte[size];
            buffer.Clear();

            int length = 0;
            for (int i = zeroes; i < text.Length; i++)
            {
                int digit = Base58Alphabet.IndexOf(text[i]);
                if (digit < 0)
                {
                    throw new FormatException("'" + text[i] + "' is not a base58 character, so '"
                                              + text + "' is not an address");
                }

                int carry = digit;
                int digits = 0;
                for (int k = size - 1; (carry != 0 || digits < length) && k >= 0; k--, digits++)
                {
                    carry += 58 * buffer[k];
                    buffer[k] = (byte)(carry % 256);
                    carry /= 256;
                }
                length = digits;
            }

            byte[] decoded = new byte[zeroes + length];
            buffer.Slice(size - length, length).CopyTo(decoded.AsSpan(zeroes));

            if (decoded.Length < 5)
            {
                throw new FormatException("'" + text + "' decodes to " + decoded.Length
                                          + " bytes, which is not long enough to hold a checksum");
            }

            Span<byte> once = stackalloc byte[32];
            Span<byte> twice = stackalloc byte[32];
            SHA256.HashData(decoded.AsSpan(0, decoded.Length - 4), once);
            SHA256.HashData(once, twice);

            for (int i = 0; i < 4; i++)
            {
                if (twice[i] != decoded[decoded.Length - 4 + i])
                {
                    throw new FormatException("the checksum on '" + text + "' does not match its"
                                              + " contents, so it is not an address anybody issued"
                                              + " - a character of it is wrong");
                }
            }

            return decoded;
        }

        /// <summary>
        /// The address an output pays to, or null when the script does not name one.
        ///
        /// Only the forms that existed in the era this data covers are recognised, which is the
        /// whole of the standard set up to 2012:
        ///
        ///   P2PKH  OP_DUP OP_HASH160 &lt;20&gt; OP_EQUALVERIFY OP_CHECKSIG   -> version 0x00, a '1' address
        ///   P2SH   OP_HASH160 &lt;20&gt; OP_EQUAL                            -> version 0x05, a '3' address
        ///   P2PK   &lt;65 or 33 byte pubkey&gt; OP_CHECKSIG                  -> version 0x00, hashing the key
        ///
        /// P2PK is the odd one: the script commits to a public key and not to any hash, so it has
        /// no address in it at all. The one returned is the address of that key, which is what
        /// explorers show for these outputs and what makes the early chain searchable by address.
        ///
        /// Everything else - bare multisig, OP_RETURN, the outright malformed, and every segwit
        /// and taproot form from later on - returns null rather than a guess. Callers are expected
        /// to keep the row and leave the address empty, so the gap stays visible.
        /// </summary>
        public static string? ScriptToAddress(byte[] script)
        {
            // P2PKH first: by the era this data reaches it is most of every block, so the cheapest
            // test wants to be the one that matches most often.
            if (script.Length == 25 && script[0] == 0x76 && script[1] == 0xA9 && script[2] == 0x14
                && script[23] == 0x88 && script[24] == 0xAC)
            {
                return Base58Check(0x00, script.AsSpan(3, 20));
            }

            if (script.Length == 23 && script[0] == 0xA9 && script[1] == 0x14 && script[22] == 0x87)
            {
                return Base58Check(0x05, script.AsSpan(2, 20));
            }

            if (script.Length == 67 && script[0] == 0x41 && script[66] == 0xAC)
            {
                Span<byte> keyHash = stackalloc byte[20];
                Hash160(script.AsSpan(1, 65), keyHash);
                return Base58Check(0x00, keyHash);
            }

            if (script.Length == 35 && script[0] == 0x21 && script[34] == 0xAC)
            {
                Span<byte> keyHash = stackalloc byte[20];
                Hash160(script.AsSpan(1, 33), keyHash);
                return Base58Check(0x00, keyHash);
            }

            return null;
        }

        // ------------------------------------------------------------------------------------
        // Block reward addresses
        // ------------------------------------------------------------------------------------

        /// <summary>What one address was paid by the block rewards it appears in.</summary>
        public sealed class CoinbaseReward
        {
            /// <summary>The address a coinbase output pays.</summary>
            public string Address = "";

            /// <summary>How many blocks' rewards it was paid out of. Counted once per block even
            /// when a coinbase splits the reward over two outputs that land on it.</summary>
            public int Blocks;

            /// <summary>Total satoshis those blocks paid it - subsidy and fees together, since a
            /// coinbase output is one number and does not separate them.</summary>
            public ulong Value;

            /// <summary>Height of the first block that paid it.</summary>
            public int FirstHeight = -1;

            /// <summary>Height of the last block that paid it, equal to FirstHeight when only one
            /// block ever did.</summary>
            public int LastHeight = -1;
        }

        /// <summary>
        /// Every address the block reward was paid to across a run of blocks, with what each was
        /// paid and how many blocks paid it.
        ///
        /// The reward is the first transaction of a block and nothing else - a coinbase, which
        /// spends the all-zero outpoint because the coins it pays out are new rather than someone
        /// else's. So this reads the transaction count varint at byte 80, parses exactly one
        /// transaction, and moves on to the next block. Over 200,000 blocks the transactions left
        /// unparsed are almost all of the bytes, which is why this is worth doing here rather than
        /// calling ParseBlock and taking Transactions[0].
        ///
        /// What "the 50 BTC reward" amounts to needs some care:
        ///
        ///   - The subsidy halves every 210,000 blocks, so every block below that height carries
        ///     one of 50 BTC and the whole of the first 200,000 qualifies. Nothing here filters on
        ///     the amount, and nothing needs to.
        ///   - A coinbase output pays the subsidy PLUS the fees of its block, so once blocks stop
        ///     being empty the values run over 50 BTC. What comes back is what was actually paid,
        ///     not 50 x Blocks.
        ///   - A miner may claim less than it is owed, and a few did - block 124724 is the well
        ///     known one, a satoshi short. Those come out just under.
        ///   - A coinbase may have several outputs. Each is credited to its own address, and the
        ///     block counts once against each distinct address in it.
        ///
        /// An output whose script names no address - ScriptToAddress returns null for anything
        /// that is not P2PKH, P2SH or P2PK - is counted and reported rather than guessed at.
        ///
        /// Two things in this range look like errors in the output and are not. Genesis pays
        /// 1A1zP1eP5QGefi2DMPTfTL5SLmv7DivfNa 50 BTC that Bitcoin Core never puts in the UTXO set,
        /// so that reward exists in the data and can never be spent. And the BIP30 pairs at
        /// heights 91722/91880 and 91812/91842 carry byte-identical coinbases, where the later
        /// block's outputs overwrote the earlier block's and destroyed 50 BTC each time. Both
        /// halves of a pair are counted here - this is what the chain paid, not what survived.
        ///
        /// Comes back sorted by total paid, largest first.
        /// </summary>
        /// <param name="blocks">Blocks in height order. Only the coinbase of each is read.</param>
        /// <param name="heightLimit">Stop before this height, so 200000 means the first 200,000
        /// blocks: heights 0 to 199999. Blocks at or above it are skipped.</param>
        /// <param name="csvPath">Where to write the full list, or null to only return it.</param>
        public static List<CoinbaseReward> CollectCoinbaseAddresses(List<BlockRaw> blocks, int heightLimit,
                                                                   string? csvPath)
        {
            var byAddress = new Dictionary<string, CoinbaseReward>();

            // Cleared per block rather than allocated per block. It is here for the one job of
            // stopping a coinbase that pays the same address from two outputs - which pools do -
            // from counting its block twice.
            var seenInBlock = new HashSet<string>();

            int blocksRead = 0;
            int outputs = 0;
            int outputsWithNoAddress = 0;
            ulong paid = 0;

            foreach (BlockRaw raw in blocks)
            {
                if (raw.BlockIndex >= heightLimit)
                {
                    continue;
                }

                int pos = 80;
                ulong txCount = ReadVarInt(raw.Raw, ref pos);
                if (txCount == 0)
                {
                    throw new InvalidDataException("block " + raw.BlockIndex + " has no coinbase");
                }

                Transaction coinbase = ReadTransaction(raw.Raw, ref pos);
                blocksRead++;
                seenInBlock.Clear();

                foreach (Transaction.TxOutput output in coinbase.Outputs)
                {
                    outputs++;
                    paid += output.Value;

                    string? address = ScriptToAddress(output.ScriptPubKey);
                    if (address == null)
                    {
                        outputsWithNoAddress++;
                        continue;
                    }

                    CoinbaseReward? reward;
                    if (!byAddress.TryGetValue(address, out reward))
                    {
                        reward = new CoinbaseReward();
                        reward.Address = address;

                        // First and last are read off the walk rather than compared, which is
                        // only right because the blocks arrive in height order.
                        reward.FirstHeight = raw.BlockIndex;
                        byAddress.Add(address, reward);
                    }

                    reward.Value += output.Value;
                    reward.LastHeight = raw.BlockIndex;

                    if (seenInBlock.Add(address))
                    {
                        reward.Blocks++;
                    }
                }
            }

            var list = new List<CoinbaseReward>(byAddress.Count);
            foreach (KeyValuePair<string, CoinbaseReward> entry in byAddress)
            {
                list.Add(entry.Value);
            }

            // Biggest earner first, address order inside a tie so that two runs over the same
            // blocks produce the same file - a dictionary hands its entries back in an order that
            // is its own business and can differ between runs.
            list.Sort((a, b) =>
            {
                if (a.Value != b.Value)
                {
                    return b.Value.CompareTo(a.Value);
                }
                return string.CompareOrdinal(a.Address, b.Address);
            });

            Console.WriteLine("block reward addresses below height " + heightLimit + ":");
            Console.WriteLine("  blocks       : " + blocksRead);
            Console.WriteLine("  addresses    : " + list.Count);
            Console.WriteLine("  outputs      : " + outputs + " coinbase outputs");
            Console.WriteLine("  paid         : " + (paid / 100000000.0).ToString("F8") + " BTC");
            if (outputsWithNoAddress > 0)
            {
                Console.WriteLine("  no address   : " + outputsWithNoAddress
                                  + " coinbase outputs pay a script with no address in it");
            }

            int show = 20;
            if (list.Count < show)
            {
                show = list.Count;
            }

            for (int i = 0; i < show; i++)
            {
                CoinbaseReward reward = list[i];
                Console.WriteLine("  " + reward.Address.PadRight(35)
                                  + reward.Blocks.ToString().PadLeft(7) + " blocks "
                                  + (reward.Value / 100000000.0).ToString("F8").PadLeft(18) + " BTC"
                                  + "  heights " + reward.FirstHeight + " to " + reward.LastHeight);
            }

            if (csvPath != null)
            {
                string? directory = Path.GetDirectoryName(csvPath);
                if (directory != null)
                {
                    Directory.CreateDirectory(directory);
                }

                using (var writer = new StreamWriter(csvPath, false))
                {
                    writer.WriteLine("address,blocks,satoshis,btc,firstHeight,lastHeight");
                    foreach (CoinbaseReward reward in list)
                    {
                        // Invariant culture on the BTC column only: every other column is an
                        // integer and formats the same anywhere, but F8 on a machine set to a
                        // comma decimal separator would put a second comma inside the row.
                        string btc = (reward.Value / 100000000.0)
                            .ToString("F8", System.Globalization.CultureInfo.InvariantCulture);

                        writer.WriteLine(reward.Address + "," + reward.Blocks + "," + reward.Value
                                         + "," + btc + "," + reward.FirstHeight + "," + reward.LastHeight);
                    }
                }

                Console.WriteLine("  written      : " + csvPath);
            }

            return list;
        }

        // ------------------------------------------------------------------------------------
        // Address balances
        // ------------------------------------------------------------------------------------

        /// <summary>Where one address stands after every transaction that touched it.</summary>
        public sealed class AddressBalance
        {
            /// <summary>The address itself. This string is the one shared instance for it - the
            /// UTXO set points at this same object rather than keeping a copy of its own.</summary>
            public string Address = "";

            /// <summary>Satoshis paid to it minus satoshis spent from it. Signed because the type
            /// has to be able to represent a walk that went wrong; over a run that starts at
            /// height 0 it cannot legitimately go below zero, and any that does is reported.</summary>
            public long Balance;

            /// <summary>Height of the first block holding a transaction that touched it.</summary>
            public int FirstHeight = -1;

            /// <summary>Height of the last one.</summary>
            public int LastHeight = -1;

            /// <summary>How many transactions touched it, counted once per transaction however
            /// many of that transaction's inputs and outputs land on the address.</summary>
            public int Transactions;

            /// <summary>The ordinal of the last transaction counted against it, which is how
            /// Transactions stays one-per-transaction: a second touch from the same transaction
            /// finds its own number here and adds nothing. Cheaper than a per-transaction set of
            /// addresses, and it costs eight bytes on a record that already exists.</summary>
            internal long LastTransactionSeen = -1;
        }

        /// <summary>
        /// The balance of every address that appears anywhere in a run of blocks, with the range
        /// of heights it was active over and how many transactions touched it.
        ///
        /// The two sides are the same two the SQLite index files as direction 1 and direction 0,
        /// and are derived here exactly as they are there:
        ///
        ///   credit  an output, address straight out of ScriptToAddress(output.ScriptPubKey).
        ///           Coinbase outputs are outputs like any other, so the mining rewards are in
        ///           these totals without needing anything said about them - the 50 BTC a block
        ///           pays lands on the miner's address the same way a payment does.
        ///   debit   an input, which names no address at all. The address being spent belongs to
        ///           the output the input's outpoint points at, in some earlier block, so the only
        ///           way to have it is to have carried that output along - which is what `unspent`
        ///           is. Outputs go in, inputs take them back out, so it stays the size of the
        ///           UTXO set rather than growing to every output ever made.
        ///
        /// Blocks must therefore arrive in height order, and the run has to start at height 0 for
        /// the balances to mean anything: an input whose outpoint was never seen cannot be
        /// subtracted from anybody, so a run starting higher up silently leaves balances too big.
        /// The count of those is reported, and the reconciliation at the end is only printed when
        /// it is zero.
        ///
        /// Two categories of coin sit outside the totals, both counted rather than hidden:
        /// outputs whose script names no address (bare multisig, OP_RETURN, the malformed - see
        /// ScriptToAddress), and inputs that spend one of those. Neither can be attributed to an
        /// address, so neither moves a balance, and both ends stay consistent because such an
        /// output still goes into the UTXO set - carrying a null address - so spending it is
        /// recognised as unattributable rather than mistaken for an outpoint never seen.
        ///
        /// Balances here are what the arithmetic of the chain says, which is not always what an
        /// address can actually spend. Genesis pays 50 BTC to a coinbase Bitcoin Core never puts
        /// in its UTXO set, and the BIP30 pairs at heights 91722/91880 and 91812/91842 pay two
        /// byte-identical coinbases where the later overwrote the earlier. Those coins are counted
        /// as received here and are gone in reality.
        ///
        /// Comes back sorted by balance, largest first.
        /// </summary>
        /// <param name="blocks">Blocks in height order, starting at height 0.</param>
        /// <param name="heightLimit">Stop before this height, so 200000 means the first 200,000
        /// blocks: heights 0 to 199999.</param>
        /// <param name="csvPath">Where to write the full table, or null to only return it.</param>
        public static List<AddressBalance> CollectAddressBalances(List<BlockRaw> blocks, int heightLimit,
                                                                  string? csvPath)
        {
            var balances = new Dictionary<string, AddressBalance>();

            // The UTXO set, and the reason inputs can be attributed at all. Keyed by a struct so
            // the entries live in the dictionary's own storage and cost no per-outpoint
            // allocation - at height 200,000 there are a few million of them.
            var unspent = new Dictionary<OutPoint, UnspentOutput>();

            // Numbered rather than compared: every transaction gets the next number, and an
            // address records the number of the last one it was counted against.
            long transactionOrdinal = 0;

            int blocksRead = 0;
            int transactions = 0;
            int coinbaseInputs = 0;
            int unresolvedInputs = 0;
            int outputsWithNoAddress = 0;
            int spentWithNoAddress = 0;

            // Kept for the reconciliation at the end. Everything a coinbase pays is new money;
            // everything a normal transaction leaves behind is a fee, which comes back as part of
            // some coinbase in the same block and so is already inside `mined`.
            ulong mined = 0;
            ulong fees = 0;

            var clock = Stopwatch.StartNew();

            foreach (BlockRaw raw in blocks)
            {
                if (raw.BlockIndex >= heightLimit)
                {
                    continue;
                }

                Block parsed = ParseBlock(raw, raw.BlockIndex);
                blocksRead++;

                if (raw.BlockIndex % 10000 == 0)
                    Console.WriteLine(raw.BlockIndex);

                foreach (Transaction tx in parsed.Transactions)
                {
                    transactionOrdinal++;
                    transactions++;

                    bool coinbase = false;
                    ulong spent = 0;
                    ulong created = 0;

                    // Inputs before outputs. A transaction cannot spend its own outputs - that
                    // would need its txid inside itself - but a later transaction in the same
                    // block routinely spends an earlier one's, which is why the UTXO set has to
                    // be updated transaction by transaction rather than block by block.
                    for (int n = 0; n < tx.Inputs.Count; n++)
                    {
                        Transaction.TxInput input = tx.Inputs[n];

                        // A coinbase spends nothing and names the all-zero outpoint. There is no
                        // address on the other end of it to debit.
                        if (IsAllZero(input.TxId))
                        {
                            coinbase = true;
                            coinbaseInputs++;
                            continue;
                        }

                        var spending = new OutPoint(input.TxId, input.Vout);

                        UnspentOutput previous;
                        if (!unspent.TryGetValue(spending, out previous))
                        {
                            unresolvedInputs++;
                            continue;
                        }

                        // Out of the set the moment it is spent. This is what keeps the dictionary
                        // the size of the unspent set instead of the size of every output the
                        // chain has ever made.
                        unspent.Remove(spending);
                        spent += previous.Value;

                        if (previous.Address == null)
                        {
                            // The value is known - it is the output's own - but there is no
                            // address to take it off, so it only counts towards the fee above.
                            spentWithNoAddress++;
                            continue;
                        }

                        AddressBalance from = Touch(balances, previous.Address, raw.BlockIndex,
                                                    transactionOrdinal);
                        from.Balance -= (long)previous.Value;
                    }

                    for (int n = 0; n < tx.Outputs.Count; n++)
                    {
                        Transaction.TxOutput output = tx.Outputs[n];
                        created += output.Value;

                        string? address = ScriptToAddress(output.ScriptPubKey);
                        if (address == null)
                        {
                            outputsWithNoAddress++;

                            // Still goes in. Whoever spends it later needs to find it here, or the
                            // spend would be counted as an outpoint this walk never saw and the
                            // two ends of the report would stop agreeing.
                            unspent[new OutPoint(tx.Hash, (uint)n)] =
                                new UnspentOutput { Address = null, Value = output.Value };
                            continue;
                        }

                        AddressBalance to = Touch(balances, address, raw.BlockIndex, transactionOrdinal);
                        to.Balance += (long)output.Value;

                        // to.Address, not `address`. They are equal strings but only one of them
                        // is the instance already held in the table, and pointing the UTXO entry
                        // at that one keeps a few million duplicate 34-character strings out of
                        // memory for as long as the outputs stay unspent.
                        unspent[new OutPoint(tx.Hash, (uint)n)] =
                            new UnspentOutput { Address = to.Address, Value = output.Value };
                    }

                    if (coinbase)
                    {
                        mined += created;
                    }
                    else if (spent >= created)
                    {
                        // What went in and did not come out. Only meaningful when every input
                        // resolved, which is why the line it feeds is suppressed otherwise - and
                        // the guard is there for the same reason: an input that resolved to
                        // nothing leaves `spent` short of `created`, and on unsigned arithmetic
                        // that subtraction does not go negative, it wraps.
                        fees += spent - created;
                    }
                }
            }

            clock.Stop();

            var list = new List<AddressBalance>(balances.Count);
            long balanceTotal = 0;
            int negative = 0;

            foreach (KeyValuePair<string, AddressBalance> entry in balances)
            {
                list.Add(entry.Value);
                balanceTotal += entry.Value.Balance;
                if (entry.Value.Balance < 0)
                {
                    negative++;
                }
            }

            // Richest first, address order inside a tie so two runs over the same blocks produce
            // the same file - a dictionary hands its entries back in an order that is its own
            // business and need not be the same twice.
            list.Sort((a, b) =>
            {
                if (a.Balance != b.Balance)
                {
                    return b.Balance.CompareTo(a.Balance);
                }
                return string.CompareOrdinal(a.Address, b.Address);
            });

            // The part of the UTXO set that belongs to nobody nameable. It is the whole of the
            // difference between what was mined and what the balances add up to, which is what
            // makes the check below exact rather than approximate.
            ulong unspentWithNoAddress = 0;
            foreach (KeyValuePair<OutPoint, UnspentOutput> entry in unspent)
            {
                if (entry.Value.Address == null)
                {
                    unspentWithNoAddress += entry.Value.Value;
                }
            }

            Console.WriteLine("address balances below height " + heightLimit + ":");
            Console.WriteLine("  blocks       : " + blocksRead + " in "
                              + clock.Elapsed.TotalSeconds.ToString("F1") + "s");
            Console.WriteLine("  transactions : " + transactions);
            Console.WriteLine("  addresses    : " + list.Count);
            Console.WriteLine("  coinbase in  : " + coinbaseInputs + " (no address to debit)");
            Console.WriteLine("  mined        : " + (mined / 100000000.0).ToString("F8")
                              + " BTC paid out by coinbases, fees included");
            Console.WriteLine("  balances     : " + (balanceTotal / 100000000.0).ToString("F8") + " BTC held");
            Console.WriteLine("  utxo set     : " + unspent.Count + " unspent outputs");

            if (outputsWithNoAddress > 0)
            {
                Console.WriteLine("  no address   : " + outputsWithNoAddress
                                  + " outputs pay a script with no address in it, "
                                  + spentWithNoAddress + " of them later spent");
            }

            if (unresolvedInputs > 0)
            {
                Console.WriteLine("  unresolved   : " + unresolvedInputs
                                  + " inputs spend an output this walk never saw - the balances"
                                  + " above them are too high and the run did not start at 0");
            }
            else
            {
                // Every satoshi ever mined is either sitting in the UTXO set or was burnt as a
                // fee and mined again inside some later coinbase. So with nothing unresolved:
                //
                //     mined - fees == balances + the unspent outputs with no address
                //
                // A mismatch means the walk lost an output somewhere, which nothing else here
                // would report - balances that are simply wrong still look like balances.
                long expected = (long)mined - (long)fees;
                long actual = balanceTotal + (long)unspentWithNoAddress;

                Console.WriteLine("  fees         : " + (fees / 100000000.0).ToString("F8")
                                  + " BTC spent but not paid out, re-mined inside the coinbases");
                Console.WriteLine("  unattributed : " + (unspentWithNoAddress / 100000000.0).ToString("F8")
                                  + " BTC unspent in scripts with no address");

                if (expected == actual)
                {
                    Console.WriteLine("  reconciles   : mined - fees == balances + unattributed");
                }
                else
                {
                    Console.WriteLine("  MISMATCH     : mined - fees is " + expected
                                      + " sats, balances + unattributed is " + actual
                                      + ", off by " + (expected - actual));
                }
            }

            if (negative > 0)
            {
                Console.WriteLine("  negative     : " + negative
                                  + " addresses spent more than they were paid, which cannot happen"
                                  + " on a walk that started at height 0");
            }

            int show = 20;
            if (list.Count < show)
            {
                show = list.Count;
            }

            for (int i = 0; i < show; i++)
            {
                AddressBalance held = list[i];
                Console.WriteLine("  " + held.Address.PadRight(35)
                                  + (held.Balance / 100000000.0).ToString("F8").PadLeft(18) + " BTC"
                                  + held.Transactions.ToString().PadLeft(8) + " txs"
                                  + "  heights " + held.FirstHeight + " to " + held.LastHeight);
            }

            if (csvPath != null)
            {
                string? directory = Path.GetDirectoryName(csvPath);
                if (directory != null)
                {
                    Directory.CreateDirectory(directory);
                }

                // Balance in satoshis: it is what the chain actually counts in, it is exact, and
                // an integer column cannot be mangled by whatever decimal separator the machine
                // that opens the file happens to use.
                using (var writer = new StreamWriter(csvPath, false))
                {
                    writer.WriteLine("address,balance,firstHeight,lastHeight,transactions");
                    foreach (AddressBalance held in list)
                    {
                        writer.WriteLine(held.Address + "," + held.Balance + "," + held.FirstHeight
                                         + "," + held.LastHeight + "," + held.Transactions);
                    }
                }

                Console.WriteLine("  written      : " + csvPath + " (balance in satoshis)");
            }

            return list;
        }

        /// <summary>
        /// The record for an address, made on first sight, with the heights and the transaction
        /// count brought up to date. Returns it for the caller to move the balance on - which the
        /// caller does rather than this, because the two sides move it in opposite directions.
        /// </summary>
        static AddressBalance Touch(Dictionary<string, AddressBalance> balances, string address,
                                    int height, long transactionOrdinal)
        {
            AddressBalance? held;
            if (!balances.TryGetValue(address, out held))
            {
                held = new AddressBalance();
                held.Address = address;

                // First and last are taken straight off the walk rather than compared against
                // what is there, which is only right because the blocks arrive in height order.
                held.FirstHeight = height;
                balances.Add(address, held);
            }

            held.LastHeight = height;

            if (held.LastTransactionSeen != transactionOrdinal)
            {
                held.LastTransactionSeen = transactionOrdinal;
                held.Transactions++;
            }

            return held;
        }

        static long countCollisions = 0;
        public static void addLookupAddress(long shrunkAddress, string address, Dictionary<long, string> lookupAddress)
        {
            //SortedList<long, string> addressLookup = new SortedList<long, string>();
            //addressLookup.Add(1234567890123L, "bc1q...");
            
            //simpleTransaction f2 = new simpleTransaction();
            //long h3 = f2.shrinkStringAddress("12cbQLTFMXRnSzktFkuoG3eHoMeFtpTu3S");
            //lookupAddress.Add(3325248302790856497L, "12cbQLTFMXRnSzktFkuoG3eHoMeFtpTu3S");
            //lookupAddress.Add(-6697027017390134739L, "1Q2TWHE3GMdB6BZKafqwxXtWAWgFt5Jvm3");
            //lookupAddress.TryGetValue(3325248302790856497L, out string? addr3);
            //lookupAddress.TryGetValue(-6697027017390134739L, out string? addr4);

            // preferred — no exception
            if (lookupAddress.TryGetValue(shrunkAddress, out string? addr2))
            {
                if (false)//addr2 != address)
                {
                    simpleTransaction f = new simpleTransaction();
                    long h =  f.shrinkStringAddress(addr2);
                    long j = f.shrinkStringAddress(address);

                    Console.WriteLine(addr2);
                    Console.WriteLine(address);
                    countCollisions++;
                    Console.WriteLine("countCollisions " + countCollisions);
                    //throw new Exception("Shrunk address collision");
                }
                //if(addr2 != address)
                //{
                //  Console.WriteLine(addr2 + " "  + address);
                //throw new Exception("Shrunk address collision");
                //}

            }
            else
            {
                lookupAddress.Add(shrunkAddress, address);
            }

        }

        /// <summary>
        /// The eight bytes every lookupAddress file starts with. Not a checksum and not a version
        /// negotiation - it is only there so that a path typo pointed at one of the other .dat
        /// files in that directory fails on the first read rather than reading a block record as a
        /// key and a length and then allocating whatever the length happened to be.
        /// </summary>
        public const string LookupAddressMagic = "LKUPADR1";


        /// <summary>
        /// The lookup table as a SortedList, built once from the Dictionary the walk accumulates
        /// into. What saveLookupAddress and every by-key query want is sorted order; what the walk
        /// wants is a cheap insert, and those are not the same structure.
        ///
        /// Accumulating straight into the SortedList is what this exists to avoid, and the cost of
        /// it is not a constant factor. An Add at a random key binary-searches in O(log n) and then
        /// shifts every key above it along by one - and every value with it, since a SortedList is
        /// two parallel arrays - so filling one with n addresses that arrive in no particular order
        /// moves on the order of n^2/2 elements in total. Measured on this machine, over random
        /// keys:
        ///
        ///     125,000 addresses     1.4s        4x the addresses,
        ///     250,000               5.7s        16x the time - which is
        ///     500,000              23.2s        what quadratic looks like
        ///   1,000,000              87.5s
        ///
        /// The 6.5 million distinct addresses of the first 200,000 blocks are another forty times
        /// the last of those, so somewhere around an hour, all of it spent in Array.Copy. The same
        /// 6.5 million into a Dictionary and through here is a second or two: the Dictionary insert
        /// is O(1), the sort is one O(n log n) pass over the keys, and the Adds below arrive in
        /// ascending order, so each one binary-searches to the end of the array and shifts nothing.
        ///
        /// That last part is the whole reason this sorts the keys first rather than handing the
        /// Dictionary's own enumeration order to the SortedList - doing that would be the quadratic
        /// fill again, just moved.
        /// </summary>
        public static SortedList<long, string> toSortedLookup(Dictionary<long, string> lookupAddress)
        {
            if (lookupAddress == null)
            {
                throw new ArgumentNullException(nameof(lookupAddress));
            }

            long[] keys = new long[lookupAddress.Count];
            lookupAddress.Keys.CopyTo(keys, 0);
            Array.Sort(keys);

            var sorted = new SortedList<long, string>(keys.Length);
            for (int i = 0; i < keys.Length; i++)
            {
                sorted.Add(keys[i], lookupAddress[keys[i]]);
            }

            return sorted;
        }

        /// <summary>
        /// The whole table to one file: the magic above, then the entry count as four bytes little
        /// endian, then one record per entry -
        ///
        ///     [8 bytes key, little endian][2 bytes UTF-8 length, little endian][that many bytes]
        ///
        /// Records are variable width, which is why the count is in the header: loadLookupAddress
        /// cannot divide the file length by a record size the way loadFromDisk does, and it wants
        /// the count up front anyway to size the SortedList once instead of growing it.
        ///
        /// Written in the order SortedList already keeps, which is ascending key. That is what
        /// makes the load cheap - see loadLookupAddress - so the loop walks by index rather than
        /// foreach over a copy of the pairs.
        ///
        /// Two bytes for the length reaches 65,535, which no address comes near (bech32 stops at
        /// 90 characters, base58 at 35). It throws rather than truncating if something else ends up
        /// in here, because a silently clipped address reads back as a valid-looking different one.
        ///
        /// FileMode.Create, so a run with fewer entries than the last leaves a shorter file rather
        /// than the tail of the old one behind the new ones - and the count in the header would
        /// disagree with that tail in any case.
        /// </summary>
        public static void saveLookupAddress(SortedList<long, string> lookupAddress, string path)
        {
            if (lookupAddress == null)
            {
                throw new ArgumentNullException(nameof(lookupAddress));
            }

            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);      // no-op when it is already there
            }

            using var file = new FileStream(path, FileMode.Create, FileAccess.Write,
                                            FileShare.None, 1 << 20);

            // One megabyte holds the largest record this format allows (10 + 65,535) many times
            // over, so the flush below always leaves room for the record that triggered it and
            // nothing has to handle a record split across two buffers.
            byte[] buffer = new byte[1 << 20];

            System.Text.Encoding.ASCII.GetBytes(LookupAddressMagic, buffer.AsSpan(0, 8));
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8, 4), lookupAddress.Count);
            int at = 12;

            for (int i = 0; i < lookupAddress.Count; i++)
            {
                long key = lookupAddress.GetKeyAtIndex(i);
                string address = lookupAddress.GetValueAtIndex(i);

                if (address == null)
                {
                    throw new InvalidDataException("key " + key + " is in the table with a null"
                                                   + " address - addLookupAddress never puts one"
                                                   + " there, so something else did");
                }

                int bytes = System.Text.Encoding.UTF8.GetByteCount(address);
                if (bytes > ushort.MaxValue)
                {
                    throw new InvalidDataException("the address under key " + key + " is " + bytes
                                                   + " UTF-8 bytes, past the " + ushort.MaxValue
                                                   + " this format's length field holds");
                }

                if (at + 10 + bytes > buffer.Length)
                {
                    file.Write(buffer, 0, at);
                    at = 0;
                }

                BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(at, 8), key);
                BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(at + 8, 2), (ushort)bytes);
                System.Text.Encoding.UTF8.GetBytes(address, buffer.AsSpan(at + 10, bytes));
                at += 10 + bytes;
            }

            if (at > 0)
            {
                file.Write(buffer, 0, at);
            }
        }

        /// <summary>
        /// The table back, in the same order and with the same contents saveLookupAddress wrote.
        ///
        /// The capacity comes out of the header so the two backing arrays are allocated once rather
        /// than doubling their way up to a few million entries, and the keys arrive in ascending
        /// order, so every Add binary-searches to the end of the array and copies nothing down.
        /// That is the cheap path through SortedList.Add and the reason nothing has to be sorted
        /// afterwards; keys out of order would still load, only each one shifting the tail of both
        /// arrays along, which is what turns the load into an O(n^2) walk.
        ///
        /// So the order is checked rather than assumed. An out of order or duplicate key means the
        /// file was written by something other than saveLookupAddress, and what Add would do about
        /// a duplicate is throw anyway, with a message about a key rather than about the file.
        ///
        /// ReadExactly rather than Read throughout: a FileStream is allowed to hand back fewer
        /// bytes than were asked for, and a loop that assumes otherwise reads a shifted file and
        /// finds nothing wrong with it.
        /// </summary>
        public static SortedList<long, string> loadLookupAddress(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("there is no lookupAddress file at " + path, path);
            }

            using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                                            FileShare.Read, 1 << 20);

            byte[] header = new byte[12];
            file.ReadExactly(header, 0, 12);

            string magic = System.Text.Encoding.ASCII.GetString(header, 0, 8);
            if (magic != LookupAddressMagic)
            {
                throw new InvalidDataException(path + " starts with \"" + magic + "\" rather than \""
                                               + LookupAddressMagic + "\" - it is not one of these"
                                               + " files");
            }

            int count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, 4));

            // The smallest a record can be is the ten byte fixed part with an empty address, so a
            // count that could not fit in what is left of the file is a corrupt header - worth
            // catching before it is handed to the constructor below as a capacity to allocate.
            if (count < 0 || (long)count * 10 > file.Length - 12)
            {
                throw new InvalidDataException(path + " claims " + count + " entries, which will"
                                               + " not fit in its " + file.Length + " bytes - the"
                                               + " header is corrupt or the file is truncated");
            }

            var loaded = new SortedList<long, string>(count);

            byte[] record = new byte[ushort.MaxValue];
            long previousKey = 0;

            for (int i = 0; i < count; i++)
            {
                file.ReadExactly(record, 0, 10);

                long key = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(0, 8));
                int bytes = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(8, 2));

                if (i > 0 && key <= previousKey)
                {
                    throw new InvalidDataException("entry " + i + " of " + path + " has key " + key
                                                   + " after key " + previousKey + " - these are"
                                                   + " written in ascending order, so the file is"
                                                   + " not one of these or it is damaged");
                }
                previousKey = key;

                file.ReadExactly(record, 0, bytes);
                loaded.Add(key, System.Text.Encoding.UTF8.GetString(record, 0, bytes));
            }

            return loaded;
        }

        public static void addToSimpleTransactionsList(simpleTransaction toAdd, 
            List<KeyValuePair<int, simpleTransaction[]>> simpleTransactionsList, int[] simpleTransactionsListCounts)
        {

            // https://www.blockchain.com/explorer/addresses/btc/1BcSvC5fS4KN3ywRgHPxymoUkAfvxUL2PH
            //1BcSvC5fS4KN3ywRgHPxymoUkAfvxUL2PH adress got 0 btc sent to it and thats only transaction


            // The first list with room left in it. What decides that is how full list h is, which
            // is simpleTransactionsListCounts[h] - not simpleTransactionsList.Count, which is how
            // many lists there are and stays at MAXLISTSsimpleTransactionsList for good.
            int index = 0;
            while (index < MAXLISTSsimpleTransactionsList)
            {
                if (simpleTransactionsListCounts[index] < MAXSIZEsimpleTransactionsList)
                {
                    break;
                }
                index++;
            }

            // Falling off the end of that loop is every list full, and the only way h reaches
            // MAXLISTSsimpleTransactionsList - the break above leaves it on a list with room.
            if (index == MAXLISTSsimpleTransactionsList)
            {
                throw new Exception("too many simple transactions: all "
                                    + MAXLISTSsimpleTransactionsList + " lists of "
                                    + MAXSIZEsimpleTransactionsList + " are full");
            }

            // List h, and the count for list h. Both indexed the same way, which is the whole
            // point - the list is built in key order, so simpleTransactionsList[h].Key is h.
            simpleTransactionsList[index].Value[simpleTransactionsListCounts[index]] = toAdd;
            simpleTransactionsListCounts[index]++;
        }


        /// <summary>
        /// The same table as CollectAddressBalances, built from a flat list of transactions
        /// instead of from the blocks - for when the transactions have already been collected and
        /// there is no reason to go back over the block bytes a second time.
        ///
        /// Written out again rather than sharing an inner loop with the block version on purpose.
        /// Two independent walks that agree on several million balances is worth something; two
        /// calls into one shared routine only ever prove that the routine is deterministic.
        /// CompareAddressBalances below is what makes that worth having.
        ///
        /// What it needs of the list is exactly what a chain gives it:
        ///
        ///   - every transaction once, and
        ///   - in chain order, blocks by height and transactions in the order their block carries
        ///     them, because an input can only be attributed after the output it spends has been
        ///     seen - including when that output was made by an earlier transaction in the same
        ///     block, which is legal and common.
        ///
        /// Height comes off Transaction.BlockHeight, which ParseBlock stamps on the way past. A
        /// transaction on its own does not otherwise know where it came from, so a list built by
        /// anything that does not go through ParseBlock will have zeros there and every balance
        /// will claim to have been first seen at height 0.
        ///
        /// A list that repeats transactions is the failure this is most likely to meet, and it is
        /// silent: the same output credited twice inflates a balance, and the input that spends it
        /// only finds it once. So outputs whose outpoint is already in the set are counted and
        /// reported. Over the first 200,000 blocks that count should be exactly 2 - the BIP30
        /// coinbases at heights 91880 and 91842, each a byte-identical repeat of an earlier block's
        /// and each overwriting an unspent output that then ceased to exist. Anything above 2 is
        /// the list, not the chain.
        ///
        /// Every rule about what a balance means is the same as the block version - read the
        /// comment on CollectAddressBalances for the mining rewards, the unattributable scripts
        /// and the reconciliation.
        ///
        /// The walk itself is two sides per transaction. The sending address is the one that owns
        /// the output an input spends, and it is only knowable by looking that output up in the
        /// unspent set; the receiving address is the one an output's script names, and it is
        /// right there in the transaction. Both sides can come up empty and the loops below say
        /// where: a coinbase input has no sender because the satoshis are new, an input naming an
        /// output this walk never saw has a sender that cannot be identified from here, and a
        /// script ScriptToAddress cannot read has no address on either side of it. Satoshis with
        /// no address are still counted - into the mined, fee and unattributed totals - so that
        /// the reconciliation at the end covers every satoshi the walk touched, not only the ones
        /// that landed on a name.
        /// </summary>
        /// <param name="transactions">Every transaction once, in chain order.</param>
        /// <param name="heightLimit">Stop before this height, so 200000 means the first 200,000
        /// blocks: heights 0 to 199999.</param>
        /// <param name="csvPath">Where to write the full table, or null to only return it.</param>
        public static List<AddressBalance> CollectAddressBalancesFromTransactions(
            List<Transaction> transactions, int heightLimit, string? csvPath,
            List<KeyValuePair<int, simpleTransaction[]>> simpleTransactionsList, int[] simpleTransactionsListCounts, Dictionary<long, string> lookupAddress
            )
        {



            // shrinkStringAddress is a base58 long division and two SHA-256 every time it is
            // called, about a microsecond, and the calls repeat far more than they differ. The
            // same address is shrunk once for every record it appears in; a transaction with n
            // senders and m receivers emits n*m records over n+m distinct strings, and a busy
            // address comes back thousands of times across the walk. Cached, a repeat is one hash
            // of the string.
            //
            // Keyed on the string rather than on the script it came from, because that is what the
            // call sites hold and because two different scripts that name the same address should
            // land on the same key - which is the point of the key.
            var shrunkCache = new Dictionary<string, long>(1 << 22);
            var shrinker = new simpleTransaction();

            long Shrink(string address)
            {
                if (shrunkCache.TryGetValue(address, out long already))
                {
                    return already;
                }

                long shrunk = shrinker.shrinkStringAddress(address);
                shrunkCache.Add(address, shrunk);
                return shrunk;
            }

            var balances = new Dictionary<string, AddressBalance>();
            var unspent = new Dictionary<OutPoint, UnspentOutput>();

            long transactionOrdinal = 0;

            int walked = 0;
            int coinbaseInputs = 0;
            int unresolvedInputs = 0;
            int outputsWithNoAddress = 0;
            int spentWithNoAddress = 0;
            ulong satoshisPaidToNoAddress = 0;
            ulong satoshisSentFromNoAddress = 0;
            int duplicateOutpoints = 0;
            int lastHeight = -1;
            int outOfOrder = 0;

            ulong mined = 0;
            ulong fees = 0;

            var clock = Stopwatch.StartNew();

            Int64 count = 0;
            foreach (Transaction tx in transactions)
            {
                if (tx.BlockHeight >= heightLimit)
                {
                    continue;
                }

                // Order is the one thing about this list that cannot be checked after the fact -
                // a balance built out of order is just wrong, with nothing about it to say so. A
                // height that goes backwards is proof of it; a repeated height is normal, since a
                // block holds many transactions.
                if (tx.BlockHeight < lastHeight)
                {
                    outOfOrder++;
                }
                lastHeight = tx.BlockHeight;

                transactionOrdinal++;
                walked++;

                bool coinbase = false;

                // The two sides of one transaction. Satoshis leave the addresses that own the
                // outputs this transaction spends, and arrive at the addresses named by the
                // outputs it makes. Neither side is guaranteed to have an address on it, so these
                // two totals are kept apart from what could actually be debited and credited.
                ulong satoshisSentBySenders = 0;
                ulong satoshisPaidToReceivers = 0;

                if (count++ % 150000 == 0)
                    Console.WriteLine(count + " of 7 million first 200000 blocks" + "end: " + transactions.Count) ;


                List<string> sentOut = new List<string>();
                List<Int64>  amountsOut = new List<Int64>();
                List<string> gotIn = new List<string>();
                List<Int64> amountsIn = new List<Int64>();


                // ---- the sending side: which address the satoshis are leaving ----------------
                //
                // An input carries neither an address nor an amount. All it names is the output
                // it spends, by txid and index - the sending address and the number of satoshis
                // being sent are both read back out of that earlier output, which is the whole
                // reason this walk keeps an unspent set. Three kinds of input have no sender to
                // debit, and each is counted where it turns up below:
                //
                //   - a coinbase input, which spends nothing because the satoshis are new,
                //   - an input naming an output this walk never saw, leaving both the sending
                //     address and the amount unknowable from here, and
                //   - an input spending an output whose script named no address.
                for (int n = 0; n < tx.Inputs.Count; n++)
                {
                    Transaction.TxInput input = tx.Inputs[n];

                    // NO SENDING ADDRESS - newly mined satoshis, out of nobody's balance.
                    if (IsAllZero(input.TxId))
                    {
                        coinbase = true;
                        coinbaseInputs++;
                        continue;
                    }

                    var outputBeingSpent = new OutPoint(input.TxId, input.Vout);

                    UnspentOutput sourceOutput;
                    if (!unspent.TryGetValue(outputBeingSpent, out sourceOutput))
                    {
                        // NO SENDING ADDRESS - the output being spent was never seen, so nothing
                        // here knows whose it was or how much it held.
                        unresolvedInputs++;
                        continue;
                    }

                    // Whoever it belonged to, the satoshis are on the move: the output is spent
                    // and off the unspent set for good.
                    unspent.Remove(outputBeingSpent);

                    // THE ADDRESS SENDING THE SATOSHIS OUT, and how many it is sending.
                    string? sendingAddress = sourceOutput.Address;
                    ulong satoshisSent = sourceOutput.Value;

                    sentOut.Add(sendingAddress!);
                    amountsOut.Add((Int64)satoshisSent);

                    satoshisSentBySenders += satoshisSent;

                    // NO SENDING ADDRESS - the amount is known, but the script it sat in named
                    // no address, so there is no balance to take it out of.
                    if (sendingAddress == null)
                    {
                        spentWithNoAddress++;
                        satoshisSentFromNoAddress += satoshisSent;
                        continue;
                    }

                    // sendingAddress is down satoshisSent.
                    AddressBalance sender = Touch(balances, sendingAddress, tx.BlockHeight,
                                                  transactionOrdinal);
                    sender.Balance -= (long)satoshisSent;
                }

                // ---- the receiving side: which address the satoshis are going to -------------
                //
                // An output does carry its amount, and its script usually names who may spend it
                // next, which is the address receiving the satoshis. Not always: a script
                // ScriptToAddress cannot read - an OP_RETURN, a non-standard script, a form it
                // does not cover - leaves satoshis with no receiver to credit. They are real
                // satoshis either way, and still spendable by whoever holds the key, so they go
                // into the unspent set under a null address rather than being dropped - and an
                // input spending them later comes back out of it as a sender with no address.
                for (int n = 0; n < tx.Outputs.Count; n++)
                {
                    Transaction.TxOutput output = tx.Outputs[n];

                    // THE ADDRESS GETTING THE SATOSHIS, and how many it is getting.
                    string? receivingAddress = ScriptToAddress(output.ScriptPubKey);
                    ulong satoshisReceived = output.Value;

                    if (receivingAddress != null)
                    {
                        gotIn.Add(receivingAddress);
                        amountsIn.Add((Int64)satoshisReceived);
                    }

                    satoshisPaidToReceivers += satoshisReceived;

                    if (receivingAddress == null)
                    {
                        // NO RECEIVING ADDRESS - nothing to credit, only to count.
                        outputsWithNoAddress++;
                        satoshisPaidToNoAddress += satoshisReceived;
                    }

                    // The output is spendable from here on, and it is what will tell a later
                    // input who its sender was: the receiving address on this side is the sending
                    // address on that one, whenever something comes along and spends it.
                    var outputBeingMade = new OutPoint(tx.Hash, (uint)n);
                    var nowUnspent = new UnspentOutput
                    {
                        Address = receivingAddress,
                        Value = satoshisReceived
                    };

                    // TryAdd first because it is one lookup and the answer is yes almost every
                    // time. The overwrite on the other branch is deliberate and is what Bitcoin
                    // Core itself did with the BIP30 duplicates - the newer output wins and the
                    // older one is gone.
                    if (!unspent.TryAdd(outputBeingMade, nowUnspent))
                    {
                        duplicateOutpoints++;
                        unspent[outputBeingMade] = nowUnspent;
                    }

                    if (receivingAddress == null)
                    {
                        continue;
                    }

                    // receivingAddress is up satoshisReceived.
                    AddressBalance receiver = Touch(balances, receivingAddress, tx.BlockHeight,
                                                    transactionOrdinal);
                    receiver.Balance += (long)satoshisReceived;
                }

                if (gotIn.Count == 1 && sentOut.Count == 0)
                {
                    //byte[] zeroAddress = new byte[8];
                    //zeroAddress[0] = 0; zeroAddress[1] = 0; zeroAddress[2] = 0; zeroAddress[3] = 0;
                    //zeroAddress[4] = 0; zeroAddress[5] = 0; zeroAddress[6] = 0; zeroAddress[7] = 0;

                    List<simpleTransaction> trans = new List<simpleTransaction>();

                    simpleTransaction t = new simpleTransaction();
                    t.From = 0;
                    t.AmountAndBlock = t.computeAmountAndBlock(amountsIn.FirstOrDefault(), 0);
                    if (amountsIn.FirstOrDefault() > ((Int64)2) << simpleTransaction.AmountBits)
                    {
                        throw new Exception("Bad");
                    }
                    t.To = Shrink(gotIn.FirstOrDefault()!);
                    
                    addLookupAddress(t.To, gotIn.FirstOrDefault()!, lookupAddress);
                    
                    trans.Add(t);


                    foreach (var t2 in trans)
                    {
                        
                        addToSimpleTransactionsList(t2, simpleTransactionsList, simpleTransactionsListCounts);
                        //simpleTransactionsList.First().Value[simpleTransactionsListCounts[0]] = t2;
                        //simpleTransactionsListCounts[0]++;
                    }
                }
                else if (gotIn.Count == 1 && sentOut.Count == 1)
                {
                    if (amountsIn.FirstOrDefault() < ((Int64)2) << simpleTransaction.AmountBits)
                    {

                        if (sentOut.FirstOrDefault() == null)
                        {
                            // m-ca4ab30f8a7cb85b4ac824a090a51f72
                            //https://blockchair.com/bitcoin/transaction/23b397edccd3740a74adb603c9756370fafcde9bcc4483eb271ecad09a94dd63

                            // unknown here
                            //https://www.blockchain.com/explorer/transactions/btc/23b397edccd3740a74adb603c9756370fafcde9bcc4483eb271ecad09a94dd63
                        }
                        else {
                            List<simpleTransaction> trans = new List<simpleTransaction>();

                            simpleTransaction t = new simpleTransaction();
                            t.From = Shrink(sentOut.FirstOrDefault()!);
                            t.AmountAndBlock = t.computeAmountAndBlock(amountsIn.FirstOrDefault(), 0);

                            if (amountsIn.FirstOrDefault() > ((Int64)2) << simpleTransaction.AmountBits)
                            {
                                throw new Exception("Bad");
                            }
                            t.To = Shrink(gotIn.FirstOrDefault()!);


                            addLookupAddress(t.To, gotIn.FirstOrDefault()!, lookupAddress);
                            addLookupAddress(t.From, sentOut.FirstOrDefault()!, lookupAddress);

                            trans.Add(t);


                            foreach (var t2 in trans)
                            {
                                addToSimpleTransactionsList(t2, simpleTransactionsList, simpleTransactionsListCounts);
                                //simpleTransactionsList.First().Value[simpleTransactionsListCounts[0]] = t2;
                                //simpleTransactionsListCounts[0]++;
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine("big transfer single in single out " + simpleTransactionsListCounts[0]);
                        // 11k transfer https://www.blockchain.com/explorer/transactions/btc/1aae9d58e8826aa65ce985061a78642ec2d920e8a8bb1679aae35f7d496d25b4
                    }

                }
                else if (gotIn.Count == 1 && sentOut.Count >= 2)
                {
                    if (amountsIn.FirstOrDefault() < ((Int64)2) << simpleTransaction.AmountBits)
                    {
                        var x = sentOut.ToArray();
                        var y = amountsOut.ToArray();
                        //https://www.blockchain.com/explorer/transactions/btc/4d6edbeb62735d45ff1565385a8b0045f066055c9425e21540ea7a8060f08bf2
                        for (int i = 0; i < sentOut.Count; i++)
                        {

                            if (y[i] > ((Int64)2) << simpleTransaction.AmountBits)
                            {
                                // https://www.blockchain.com/explorer/transactions/btc/1aae9d58e8826aa65ce985061a78642ec2d920e8a8bb1679aae35f7d496d25b4
                                throw new Exception("Bad");
                            }
                            simpleTransaction t = new simpleTransaction();

                            if (x[i] == null)
                            {
                                // unknown in and then unknown out
                                //https://www.blockchain.com/explorer/addresses/btc/1BGTiCvuU1ocR5QFbqRfmRkjSF6BVdyeP5


                                //https://www.blockchain.com/explorer/transactions/btc/4f1433d6433d3ce8a877519ba9ddc310dbee96dba939aca0dbef0176a3563436
                                Console.WriteLine("weird https://www.blockchain.com/explorer/transactions/btc/4f1433d6433d3ce8a877519ba9ddc310dbee96dba939aca0dbef0176a3563436");
                            }
                            else 
                            { 
                                t.From = Shrink(x[i]);
                                t.AmountAndBlock = t.computeAmountAndBlock(y[i], 0);
                                t.To = Shrink(gotIn.FirstOrDefault()!);


                                addLookupAddress(t.To, gotIn.FirstOrDefault()!, lookupAddress); // redundant adds
                                addLookupAddress(t.From, x[i], lookupAddress);


                                // https://www.blockchain.com/explorer/transactions/btc/a3b0e9e7cddbbe78270fa4182a7675ff00b92872d8df7d14265a2b1e379a9d33
                                addToSimpleTransactionsList(t, simpleTransactionsList, simpleTransactionsListCounts);
                                //simpleTransactionsList.First().Value[simpleTransactionsListCounts[0]] = t;
                                //simpleTransactionsListCounts[0]++;
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine("big transfer single in" + simpleTransactionsListCounts[0]);
                        // 11k transfer https://www.blockchain.com/explorer/transactions/btc/1aae9d58e8826aa65ce985061a78642ec2d920e8a8bb1679aae35f7d496d25b4
                    }
                }
                else if (gotIn.Count >= 2 && sentOut.Count == 1)
                {
                    if (amountsOut.FirstOrDefault() < ((Int64)2) << simpleTransaction.AmountBits)
                    {
                        var x = gotIn.ToArray();
                        var y = amountsIn.ToArray();


                        if (sentOut.FirstOrDefault() == null)
                        {
                            //https://blockchair.com/bitcoin/transaction/81b0bb7be25a496cb12ed5acf834cbaebb8e5dfaffc9c996f33fe24f0f54c883
                            // s-1428b6578b0073fcd6871a28b99bf95b
                            Console.WriteLine("weird address");

                        }
                        else
                        {
                            for (int i = 0; i < gotIn.Count; i++)
                            {

                                if (y[i] > ((Int64)2) << simpleTransaction.AmountBits)
                                {
                                    throw new Exception("Bad");
                                }
                                simpleTransaction t = new simpleTransaction();

                                t.From = Shrink(sentOut.FirstOrDefault());
                                t.AmountAndBlock = t.computeAmountAndBlock(y[i], 0);
                                t.To = Shrink(x[i]);

                                addLookupAddress(t.From, sentOut.FirstOrDefault()!, lookupAddress); // redundant adds
                                addLookupAddress(t.To, x[i], lookupAddress);



                                addToSimpleTransactionsList(t, simpleTransactionsList, simpleTransactionsListCounts);
                                //simpleTransactionsList.First().Value[simpleTransactionsListCounts[0]] = t;
                                //simpleTransactionsListCounts[0]++;

                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine("big transfer single out");
                        // 11k transfer https://www.blockchain.com/explorer/transactions/btc/1aae9d58e8826aa65ce985061a78642ec2d920e8a8bb1679aae35f7d496d25b4
                    }
                }

                else if (gotIn.Count >= 2 && sentOut.Count >= 2)
                {
                    var senders = sentOut.ToArray();
                    var sent = amountsOut.ToArray();
                    var receivers = gotIn.ToArray();
                    var received = amountsIn.ToArray();

                    Int64 totalSent = 0;
                    for (int i = 0; i < sent.Length; i++)
                    {
                        totalSent += sent[i];
                    }

                    // The weights are shares of totalSent, so a total of zero has no shares to
                    // take. It means two inputs that between them spent nothing, which the chain
                    // does not contain and which would divide by zero below.
                    if (totalSent <= 0)
                    {
                        throw new Exception("two senders spending " + totalSent
                                            + " satoshis between them");
                    }

                    for (int j = 0; j < receivers.Length; j++)
                    {
                        // received[j] * sent[i] overflows Int64 long before either factor does:
                        // the largest output on the chain is 5e13 satoshis and a pooled input side
                        // can reach 2.1e15, whose product needs about a hundred bits. Int128 holds
                        // that intermediate exactly, and the quotient is back under 2.1e15, so the
                        // cast back out of it cannot truncate.
                        Int64 attributed = 0;
                        for (int i = 0; i < senders.Length; i++)
                        {
                            // Integer division rounds every share down, so the shares sum to as
                            // much as one satoshi per sender short of received[j]. The last sender
                            // is handed the remainder rather than its own quotient, which is what
                            // makes one receiver's records add up to exactly what it received.
                            Int64 share;
                            if (i == senders.Length - 1)
                            {
                                share = received[j] - attributed;
                            }
                            else
                            {
                                share = (Int64)((Int128)received[j] * sent[i] / totalSent);
                            }
                            attributed += share;

                            simpleTransaction t = new simpleTransaction();

                            if (senders[i] == null )//&& (i==90 || i==89))
                            {
                                // doesnt happen much
                                Console.WriteLine("look into 90th" +  "https://www.blockchain.com/explorer/addresses/btc/1LYvWcThP3kkiSvbd3ZYVvQiMhNpMVHHnj");
                            }
                            else
                            {
                                t.From = Shrink(senders[i]);
                                t.AmountAndBlock = t.computeAmountAndBlock(share, 0);
                                t.To = Shrink(receivers[j]);

                                addLookupAddress(t.From, senders[i], lookupAddress); // redundant adds
                                addLookupAddress(t.To, receivers[j], lookupAddress); // redundant adds
                                

                                // https://www.blockchain.com/explorer/transactions/btc/28204cad1d7fc1d199e8ef4fa22f182de6258a3eaafe1bbe56ebdcacd3069a5f
                                addToSimpleTransactionsList(t, simpleTransactionsList, simpleTransactionsListCounts);
                                //simpleTransactionsList.First().Value[simpleTransactionsListCounts[0]] = t;
                                //simpleTransactionsListCounts[0]++;
                            }
                        }
                    }
                }

                if (coinbase)
                {
                    // Nothing was sent to it, so everything it pays out is new: the block subsidy
                    // plus the fees every other transaction in the block left behind.
                    mined += satoshisPaidToReceivers;
                }
                else if (satoshisSentBySenders >= satoshisPaidToReceivers)
                {
                    // More left the senders than reached a receiver. The difference is the fee -
                    // it goes to no address here, and comes back out of this block's coinbase.
                    fees += satoshisSentBySenders - satoshisPaidToReceivers;
                }
            }

            clock.Stop();

            var list = new List<AddressBalance>(balances.Count);
            long balanceTotal = 0;
            int negative = 0;

            foreach (KeyValuePair<string, AddressBalance> entry in balances)
            {
                list.Add(entry.Value);
                balanceTotal += entry.Value.Balance;
                if (entry.Value.Balance < 0)
                {
                    negative++;
                }
            }

            list.Sort((a, b) =>
            {
                if (a.Balance != b.Balance)
                {
                    return b.Balance.CompareTo(a.Balance);
                }
                return string.CompareOrdinal(a.Address, b.Address);
            });

            ulong unspentWithNoAddress = 0;
            foreach (KeyValuePair<OutPoint, UnspentOutput> entry in unspent)
            {
                if (entry.Value.Address == null)
                {
                    unspentWithNoAddress += entry.Value.Value;
                }
            }

            Console.WriteLine("address balances from " + transactions.Count
                              + " collected transactions, below height " + heightLimit + ":");
            Console.WriteLine("  transactions : " + walked + " walked in "
                              + clock.Elapsed.TotalSeconds.ToString("F1") + "s");
            Console.WriteLine("  addresses    : " + list.Count);
            Console.WriteLine("  coinbase in  : " + coinbaseInputs
                              + " inputs mint new satoshis, so there is no sending address");
            Console.WriteLine("  mined        : " + (mined / 100000000.0).ToString("F8")
                              + " BTC paid out by coinbases, fees included");
            Console.WriteLine("  balances     : " + (balanceTotal / 100000000.0).ToString("F8") + " BTC held");
            Console.WriteLine("  utxo set     : " + unspent.Count + " unspent outputs");

            if (outputsWithNoAddress > 0)
            {
                Console.WriteLine("  no receiver  : " + outputsWithNoAddress
                                  + " outputs pay a script with no address in it, holding "
                                  + (satoshisPaidToNoAddress / 100000000.0).ToString("F8") + " BTC");
            }

            if (spentWithNoAddress > 0)
            {
                Console.WriteLine("  no sender    : " + spentWithNoAddress
                                  + " of those were later spent, sending "
                                  + (satoshisSentFromNoAddress / 100000000.0).ToString("F8")
                                  + " BTC out of no address");
            }

            if (duplicateOutpoints > 2)
            {
                Console.WriteLine("  DUPLICATES   : " + duplicateOutpoints
                                  + " outputs were made on an outpoint already in the set - the"
                                  + " chain accounts for 2 of those below height 200000, so this"
                                  + " list repeats transactions and every number here is wrong");
            }
            else if (duplicateOutpoints > 0)
            {
                Console.WriteLine("  duplicates   : " + duplicateOutpoints
                                  + " outpoints made twice, which is the BIP30 pair and expected");
            }

            if (outOfOrder > 0)
            {
                Console.WriteLine("  OUT OF ORDER : " + outOfOrder
                                  + " transactions arrived at a lower height than the one before"
                                  + " them - the list is not in chain order and the balances are wrong");
            }

            if (unresolvedInputs > 0)
            {
                Console.WriteLine("  unresolved   : " + unresolvedInputs
                                  + " inputs spend an output this walk never saw, so neither the"
                                  + " sending address nor the amount is known - the balances"
                                  + " above them are too high and the list did not start at 0");
            }
            else
            {
                long expected = (long)mined - (long)fees;
                long actual = balanceTotal + (long)unspentWithNoAddress;

                Console.WriteLine("  fees         : " + (fees / 100000000.0).ToString("F8")
                                  + " BTC spent but not paid out, re-mined inside the coinbases");
                Console.WriteLine("  unattributed : " + (unspentWithNoAddress / 100000000.0).ToString("F8")
                                  + " BTC unspent in scripts with no address");

                if (expected == actual)
                {
                    Console.WriteLine("  reconciles   : mined - fees == balances + unattributed");
                }
                else
                {
                    Console.WriteLine("  MISMATCH     : mined - fees is " + expected
                                      + " sats, balances + unattributed is " + actual
                                      + ", off by " + (expected - actual));
                }
            }

            if (negative > 0)
            {
                Console.WriteLine("  negative     : " + negative
                                  + " addresses spent more than they were paid, which cannot happen"
                                  + " on a list that started at height 0");
            }

            int show = 20;
            if (list.Count < show)
            {
                show = list.Count;
            }

            for (int i = 0; i < show; i++)
            {
                AddressBalance held = list[i];
                Console.WriteLine("  " + held.Address.PadRight(35)
                                  + (held.Balance / 100000000.0).ToString("F8").PadLeft(18) + " BTC"
                                  + held.Transactions.ToString().PadLeft(8) + " txs"
                                  + "  heights " + held.FirstHeight + " to " + held.LastHeight);
            }

            if (false)
            {
                string? directory = Path.GetDirectoryName(csvPath);
                if (directory != null)
                {
                    Directory.CreateDirectory(directory);
                }

                using (var writer = new StreamWriter(csvPath, false))
                {
                    writer.WriteLine("address,balance,firstHeight,lastHeight,transactions");
                    foreach (AddressBalance held in list)
                    {
                        writer.WriteLine(held.Address + "," + held.Balance + "," + held.FirstHeight
                                         + "," + held.LastHeight + "," + held.Transactions);
                    }
                }

                Console.WriteLine("  written      : " + csvPath + " (balance in satoshis)");
            }


            if (false)
            {

                //Save ALL Transactions ToS qlite
                //
                // Every transaction this walk saw, in one database, with the address on either
                // side of it - the same tx and txaddr tables SaveTransactionsToSqlite builds per
                // 50,000 block segment, except that this is the whole run in a single file.
                //
                // It walks the list again rather than filing rows as the balances were built,
                // because resolving an input consumes the output it points at: the set above no
                // longer holds anything that was spent along the way, so from here the only way
                // to know whose an input was is to build the set a second time.
                string allTxDbPath = "C:\\btcblock\\rocksdb\\transactions_all.db";

                // Which is the other reason to let this one go first. It is a few million entries
                // and nothing needs it now - the reconciliation above is done - so dropping the
                // reference leaves the second walk building its set in that room rather than
                // beside it. Clear() would keep the storage; a new dictionary hands it back.
                unspent = new Dictionary<OutPoint, UnspentOutput>();

                SaveAllTransactionsToSqlite(allTxDbPath, transactions, heightLimit);
            }

            return list;
        }

        /// <summary>
        /// Holds two balance tables against each other and says whether they are the same table.
        ///
        /// This is the reason the list version is written out separately. Both walks apply the
        /// same rules to the same chain, so every address in one has to appear in the other with
        /// the same balance, the same two heights and the same transaction count. If they do, the
        /// list was fed in whole and in order and both implementations are doing what they say.
        /// If they do not, the differences say where to look - a balance out by exactly the value
        /// of one output is a different problem from a first-seen height of 0.
        ///
        /// Returns the number of differences, so 0 means the two agree.
        /// </summary>
        public static int CompareAddressBalances(List<AddressBalance> left, string leftName,
                                                 List<AddressBalance> right, string rightName)
        {
            var byAddress = new Dictionary<string, AddressBalance>(left.Count);
            foreach (AddressBalance held in left)
            {
                byAddress[held.Address] = held;
            }

            int differences = 0;
            int missing = 0;
            int shown = 0;

            Console.WriteLine("comparing " + leftName + " (" + left.Count + " addresses) against "
                              + rightName + " (" + right.Count + " addresses):");

            foreach (AddressBalance held in right)
            {
                AddressBalance? other;
                if (!byAddress.TryGetValue(held.Address, out other))
                {
                    missing++;
                    if (shown < 10)
                    {
                        shown++;
                        Console.WriteLine("  only in " + rightName + " : " + held.Address
                                          + " " + held.Balance + " sats");
                    }
                    continue;
                }

                // Seen, so what is left in the dictionary at the end is whatever only the left
                // side had.
                byAddress.Remove(held.Address);

                if (other.Balance == held.Balance && other.FirstHeight == held.FirstHeight
                    && other.LastHeight == held.LastHeight && other.Transactions == held.Transactions)
                {
                    continue;
                }

                differences++;
                if (shown < 10)
                {
                    shown++;
                    Console.WriteLine("  differs      : " + held.Address);
                    Console.WriteLine("    " + leftName.PadRight(12) + " balance " + other.Balance
                                      + " heights " + other.FirstHeight + "-" + other.LastHeight
                                      + " txs " + other.Transactions);
                    Console.WriteLine("    " + rightName.PadRight(12) + " balance " + held.Balance
                                      + " heights " + held.FirstHeight + "-" + held.LastHeight
                                      + " txs " + held.Transactions);
                }
            }

            foreach (KeyValuePair<string, AddressBalance> entry in byAddress)
            {
                missing++;
                if (shown < 10)
                {
                    shown++;
                    Console.WriteLine("  only in " + leftName + " : " + entry.Key
                                      + " " + entry.Value.Balance + " sats");
                }
            }

            differences += missing;

            if (differences == 0)
            {
                Console.WriteLine("  identical    : two independent walks, same " + left.Count
                                  + " addresses, same balances");
            }
            else
            {
                Console.WriteLine("  differences  : " + differences + " in total, " + missing
                                  + " of them an address one walk has and the other does not");
            }

            return differences;
        }

        // ------------------------------------------------------------------------------------
        // RocksDB transaction index
        // ------------------------------------------------------------------------------------

        //   't' + txid[32]   -> height[4] offset[4] length[4]
        //   'M'              -> how many records were written
        //
        // The transactions themselves are not copied in here. Every one of them is already on
        // disk inside the block that carries it, so a store of the bytes would be a second copy
        // of the block store wearing different keys. What is not already anywhere is *where* a
        // given txid lives, and that is twelve bytes: the height of its block, and the span it
        // occupies in that block's serialization.
        //
        // Reading one back is this store for the locator, the block store for the block, then a
        // slice - which is the same trade Bitcoin Core makes with -txindex.
        const byte TxPrefix = (byte)'t';

        /// <summary>Bytes in a locator: height[4] offset[4] length[4], all little endian.</summary>
        const int TxLocatorBytes = 12;

        /// <summary>Transactions per write batch. They are small, so this can be far bigger than
        /// the block equivalent before a batch is worth anything in memory.</summary>
        const int TransactionsPerBatch = 20000;

        static byte[] TxKey(byte[] internalTxid)
        {
            byte[] key = new byte[33];
            key[0] = TxPrefix;
            internalTxid.CopyTo(key, 1);
            return key;
        }

        /// <summary>Hashes are already uniformly random, so the first four bytes make a fine key.</summary>
        sealed class ByteArrayComparer : IEqualityComparer<byte[]>
        {
            public bool Equals(byte[]? x, byte[]? y)
            {
                return BytesEqual(x, y);
            }

            public int GetHashCode(byte[] obj)
            {
                if (obj.Length >= 4) return BinaryPrimitives.ReadInt32LittleEndian(obj.AsSpan(0, 4));
                return obj.Length;
            }
        }

        /// <summary>
        /// Files every transaction in a run of blocks into its own RocksDB store, keyed by txid,
        /// with the block height and the span the transaction occupies in that block as the value.
        ///
        /// The blocks are parsed here rather than taken already parsed, because the offsets are
        /// what this is really after and only the parse knows them - ReadTransaction records the
        /// span it walked, and nothing upstream of it does.
        ///
        /// Duplicate txids are real, and this stretch of the chain has them: before BIP30 was
        /// enforced a coinbase could repeat an earlier one byte for byte, so blocks 91842 and
        /// 91880 carry transactions with the same txids as 91812 and 91722. The later record wins
        /// the key, which is the same answer the chain itself gives, and the count is reported.
        ///
        /// Returns how many transactions were written - not how many keys the store ends up with,
        /// which is lower by exactly the duplicate count.
        /// </summary>
        public static int SaveTransactionsToRocksDb(string dbPath, List<BlockRaw> blocks, int verifyEvery = 5000)
        {
            Directory.CreateDirectory(dbPath);

            var options = new DbOptions().SetCreateIfMissing(true);

            int written = 0;
            int duplicates = 0;
            var seen = new HashSet<byte[]>(new ByteArrayComparer());

            // Sampled on the way past and checked once everything is written. Holding the block
            // itself rather than a copy of the bytes - it is already in memory either way.
            var samples = new List<(byte[] Txid, BlockRaw Block, int Height, int Offset, int Length)>();

            using (RocksDb db = RocksDb.Open(options, dbPath))
            {
                var batch = new WriteBatch();
                int inBatch = 0;

                foreach (BlockRaw raw in blocks)
                {
                    Block parsed = ParseBlock(raw, raw.BlockIndex);

                    foreach (Transaction tx in parsed.Transactions)
                    {
                        byte[] locator = new byte[TxLocatorBytes];
                        BinaryPrimitives.WriteInt32LittleEndian(locator.AsSpan(0, 4), tx.BlockHeight);
                        BinaryPrimitives.WriteInt32LittleEndian(locator.AsSpan(4, 4), tx.ByteOffset);
                        BinaryPrimitives.WriteInt32LittleEndian(locator.AsSpan(8, 4), tx.ByteLength);

                        batch.Put(TxKey(tx.Hash), locator);

                        if (!seen.Add(tx.Hash))
                        {
                            duplicates++;
                            Console.WriteLine("  duplicate txid at height " + tx.BlockHeight
                                              + ": " + tx.GetHashAsString());
                        }

                        if (verifyEvery > 0 && written % verifyEvery == 0)
                        {
                            samples.Add((tx.Hash, raw, tx.BlockHeight, tx.ByteOffset, tx.ByteLength));
                        }

                        written++;
                        inBatch++;

                        if (inBatch >= TransactionsPerBatch)
                        {
                            db.Write(batch);
                            batch.Dispose();
                            batch = new WriteBatch();
                            inBatch = 0;
                        }
                    }
                }

                if (inBatch > 0)
                {
                    db.Write(batch);
                }
                batch.Dispose();

                byte[] meta = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(meta, written);
                db.Put(MetaKey, meta);

                int roundTripFailures = 0;
                foreach (var sample in samples)
                {
                    byte[]? stored = db.Get(TxKey(sample.Txid));
                    if (stored == null || stored.Length != TxLocatorBytes)
                    {
                        roundTripFailures++;
                        Console.Error.WriteLine("txid " + ToDisplayHex(sample.Txid) + " is not in the store");
                        continue;
                    }

                    int height = BinaryPrimitives.ReadInt32LittleEndian(stored.AsSpan(0, 4));
                    int offset = BinaryPrimitives.ReadInt32LittleEndian(stored.AsSpan(4, 4));
                    int length = BinaryPrimitives.ReadInt32LittleEndian(stored.AsSpan(8, 4));

                    // A duplicate txid legitimately reads back as the later block's locator, so
                    // only the ones that were not repeated are held to the exact values written.
                    if (height != sample.Height || offset != sample.Offset || length != sample.Length)
                    {
                        roundTripFailures++;
                        Console.Error.WriteLine("locator for " + ToDisplayHex(sample.Txid) + " came back as "
                                                + height + "/" + offset + "/" + length + ", wrote "
                                                + sample.Height + "/" + sample.Offset + "/" + sample.Length);
                        continue;
                    }

                    if (offset < 0 || length < 1 || offset + length > sample.Block.Raw.Length)
                    {
                        roundTripFailures++;
                        Console.Error.WriteLine("locator for " + ToDisplayHex(sample.Txid)
                                                + " points outside its " + sample.Block.Raw.Length + " byte block");
                        continue;
                    }

                    // Cut the transaction back out and check it is the one the key names. Only
                    // sound without a witness: the txid skips the marker, flag and witness stacks,
                    // so on a segwit transaction the slice hashes to the wtxid instead. Byte 4 of
                    // the slice is the segwit marker when there is one - nothing below height
                    // 481,824 has it, but this store does not have to know that.
                    ReadOnlySpan<byte> slice = sample.Block.Raw.AsSpan(offset, length);
                    if (slice.Length > 4 && slice[4] != 0x00)
                    {
                        byte[] hashed = DoubleSha256(sample.Block.Raw, offset, length);
                        if (!hashed.AsSpan().SequenceEqual(sample.Txid))
                        {
                            roundTripFailures++;
                            Console.Error.WriteLine("the bytes at the locator for " + ToDisplayHex(sample.Txid)
                                                    + " hash to " + ToDisplayHex(hashed));
                        }
                    }
                }

                Console.WriteLine("transactions : " + written + " written to " + dbPath);
                if (duplicates > 0)
                {
                    Console.WriteLine("  duplicates : " + duplicates + ", so the store holds "
                                      + (written - duplicates) + " keys");
                }
                Console.WriteLine("  round trip : " + (samples.Count - roundTripFailures)
                                  + " of " + samples.Count + " sampled transactions match");
            }

            return written;
        }


        /// <summary>
        /// Looks a txid up in a transaction store and cuts the transaction out of the block bytes
        /// handed in. The block has to be the one at the height the locator names - this does not
        /// open the block store, so the caller decides where the block comes from.
        ///
        /// Null when the store does not hold that txid. Opens the database for the one read, so
        /// this is for spot checks rather than a loop.
        /// </summary>
        public static byte[]? ReadTransactionFromRocksDb(string dbPath, byte[] internalTxid, BlockRaw block)
        {
            if (!Directory.Exists(dbPath))
            {
                throw new DirectoryNotFoundException("no rocksdb store at " + dbPath
                                                     + " - nothing has been saved there yet");
            }

            var options = new DbOptions().SetCreateIfMissing(false);

            using (RocksDb db = RocksDb.Open(options, dbPath))
            {
                byte[]? stored = db.Get(TxKey(internalTxid));
                if (stored == null || stored.Length != TxLocatorBytes) return null;

                int height = BinaryPrimitives.ReadInt32LittleEndian(stored.AsSpan(0, 4));
                int offset = BinaryPrimitives.ReadInt32LittleEndian(stored.AsSpan(4, 4));
                int length = BinaryPrimitives.ReadInt32LittleEndian(stored.AsSpan(8, 4));

                if (height != block.BlockIndex)
                {
                    throw new ArgumentException("that transaction is in the block at height " + height
                                                + ", not the one at " + block.BlockIndex, nameof(block));
                }
                if (offset < 0 || length < 1 || offset + length > block.Raw.Length)
                {
                    throw new InvalidDataException("the locator points outside the block it names");
                }

                return block.Raw.AsSpan(offset, length).ToArray();
            }
        }

        // ------------------------------------------------------------------------------------
        // SQLite transaction index, searchable by address
        // ------------------------------------------------------------------------------------

        /// <summary>What an unspent output pays and to whom, held until something spends it.</summary>
        public struct UnspentOutput
        {
            public string? Address;
            public ulong Value;
        }

        /// <summary>
        /// An outpoint as a dictionary key, held as four longs and an int rather than a 36 byte
        /// array. A struct key lives inside the dictionary's own storage, so the UTXO set costs no
        /// per-entry allocation at all - which matters when every output in the chain puts one in
        /// and every input takes one out.
        ///
        /// Hashing off the first eight bytes of the txid is enough: it is already a hash.
        /// </summary>
        public readonly struct OutPoint : IEquatable<OutPoint>
        {
            readonly ulong _a, _b, _c, _d;
            readonly uint _vout;

            public OutPoint(ReadOnlySpan<byte> txid32, uint vout)
            {
                _a = BinaryPrimitives.ReadUInt64LittleEndian(txid32.Slice(0, 8));
                _b = BinaryPrimitives.ReadUInt64LittleEndian(txid32.Slice(8, 8));
                _c = BinaryPrimitives.ReadUInt64LittleEndian(txid32.Slice(16, 8));
                _d = BinaryPrimitives.ReadUInt64LittleEndian(txid32.Slice(24, 8));
                _vout = vout;
            }

            public bool Equals(OutPoint other)
            {
                return _a == other._a && _b == other._b && _c == other._c
                       && _d == other._d && _vout == other._vout;
            }

            public override bool Equals(object? obj)
            {
                if (obj is OutPoint other) return Equals(other);
                return false;
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(_a, _vout);
            }
        }

        static void Execute(SqliteConnection conn, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// A prepared insert with its parameters held onto, so the hot loop sets values and
        /// executes rather than rebuilding a command per row.
        ///
        /// Deliberately one row per execute. Multi-row inserts - the usual advice for a bulk load
        /// - were measured here and made it worse, monotonically: on 670 blocks of blk00060 the
        /// same work took 10.8s at one row a statement, 10.7s at four, 11.8s at sixteen and 17.9s
        /// at sixty-four. Microsoft.Data.Sqlite binds parameters by name, so a statement with 64
        /// tuples pays for 384 named bindings per execute and gives back only the statement steps
        /// it saved. The win in this method came from elsewhere - see Base58Encode.
        /// </summary>
        sealed class PreparedInsert : IDisposable
        {
            readonly SqliteCommand _command;
            readonly SqliteParameter[] _params;

            public PreparedInsert(SqliteConnection conn, SqliteTransaction work, string table,
                                  string[] columns, SqliteType[] types)
            {
                _params = new SqliteParameter[columns.Length];

                var text = new System.Text.StringBuilder();
                text.Append("insert into ").Append(table).Append(" (")
                    .Append(string.Join(", ", columns)).Append(") values (");

                _command = conn.CreateCommand();
                _command.Transaction = work;
                for (int c = 0; c < columns.Length; c++)
                {
                    if (c > 0) text.Append(',');
                    string name = "$c" + c;
                    text.Append(name);
                    _params[c] = _command.Parameters.Add(name, types[c]);
                }
                text.Append(')');

                _command.CommandText = text.ToString();
                _command.Prepare();
            }

            public void Set(int column, long value)
            {
                _params[column].Value = value;
            }

            public void Set(int column, byte[] value)
            {
                _params[column].Value = value;
            }

            public void SetText(int column, string? value)
            {
                if (value == null)
                {
                    _params[column].Value = DBNull.Value;
                }
                else
                {
                    _params[column].Value = value;
                }
            }

            public void SetNull(int column)
            {
                _params[column].Value = DBNull.Value;
            }

            public void Write()
            {
                _command.ExecuteNonQuery();
            }

            public void Dispose()
            {
                _command.Dispose();
            }
        }

        /// <summary>
        /// Writes a run of blocks into a SQLite database as transactions plus the addresses on
        /// either side of them, indexed so either side can be searched.
        ///
        ///   tx      one row per transaction: txid, height, and its span in the block
        ///   txaddr  one row per address a transaction touches, direction 0 for the address it
        ///           came FROM and 1 for an address it went TO, with the value in satoshis
        ///
        /// The to-address is in the output script and falls straight out of it. The from-address
        /// does not exist anywhere in the transaction: an input names an outpoint, and the address
        /// being spent lives in the output that outpoint points at, in some earlier block. So the
        /// only way to fill it in is to carry the unspent outputs along as the chain is walked -
        /// which is what `unspent` is, and why it has to be passed in rather than built per call.
        /// Blocks must arrive in height order across calls or an input will look up an outpoint
        /// that has not been seen yet.
        ///
        /// Passing the same dictionary through every segment in turn makes it a real UTXO set:
        /// outputs go in, inputs take them back out, so it stays the size of the unspent set
        /// rather than growing to every output ever made.
        ///
        /// An input whose outpoint is not in there gets its row with a null address rather than
        /// being dropped - that is what the start of a run that did not begin at height 0 looks
        /// like, and the count is reported so it cannot pass unnoticed.
        ///
        /// Returns how many transactions were written.
        /// </summary>
        public static int SaveTransactionsToSqlite(string dbPath, List<BlockRaw> blocks,
                                                   Dictionary<OutPoint, UnspentOutput> unspent)
        {
            string? directory = Path.GetDirectoryName(dbPath);
            if (directory != null) Directory.CreateDirectory(directory);

            // Rebuilt from scratch: the tables have no key to update against, so a second run over
            // an existing file would file every transaction twice.
            if (File.Exists(dbPath))
            {
                Console.WriteLine("  replacing the existing " + Path.GetFileName(dbPath));
                File.Delete(dbPath);
            }

            int transactions = 0;
            int addressRows = 0;
            int coinbaseInputs = 0;
            int unresolvedInputs = 0;
            int scriptsWithNoAddress = 0;

            using var conn = new SqliteConnection("Data Source=" + dbPath);
            conn.Open();

            // This is a derived index - everything in it can be rebuilt from the blocks - so
            // durability is worth nothing here and speed is worth a lot.
            Execute(conn, "pragma journal_mode = off");
            Execute(conn, "pragma synchronous = off");
            Execute(conn, "pragma temp_store = memory");
            Execute(conn, "pragma cache_size = -200000");

            Execute(conn, @"create table tx (
                                txid   blob    not null,
                                height integer not null,
                                offset integer not null,
                                length integer not null)");

            Execute(conn, @"create table txaddr (
                                txid      blob    not null,
                                height    integer not null,
                                direction integer not null,
                                n         integer not null,
                                address   text,
                                value     integer)");

            using (var work = conn.BeginTransaction())
            {
                using var txRows = new PreparedInsert(conn, work, "tx",
                    new[] { "txid", "height", "offset", "length" },
                    new[] { SqliteType.Blob, SqliteType.Integer, SqliteType.Integer, SqliteType.Integer });

                using var addrRows = new PreparedInsert(conn, work, "txaddr",
                    new[] { "txid", "height", "direction", "n", "address", "value" },
                    new[] { SqliteType.Blob, SqliteType.Integer, SqliteType.Integer,
                            SqliteType.Integer, SqliteType.Text, SqliteType.Integer });

                foreach (BlockRaw raw in blocks)
                {
                    Block parsed = ParseBlock(raw, raw.BlockIndex);

                    foreach (Transaction tx in parsed.Transactions)
                    {
                        txRows.Set(0, tx.Hash);
                        txRows.Set(1, tx.BlockHeight);
                        txRows.Set(2, tx.ByteOffset);
                        txRows.Set(3, tx.ByteLength);
                        txRows.Write();
                        transactions++;

                        // Inputs first, so an output this transaction creates cannot be mistaken
                        // for one of the outputs it spends.
                        for (int n = 0; n < tx.Inputs.Count; n++)
                        {
                            Transaction.TxInput input = tx.Inputs[n];

                            // A coinbase spends nothing and names the all-zero outpoint. There is
                            // no from-address to look up, so it gets no row.
                            if (IsAllZero(input.TxId))
                            {
                                coinbaseInputs++;
                                continue;
                            }

                            var spending = new OutPoint(input.TxId, input.Vout);

                            addrRows.Set(0, tx.Hash);
                            addrRows.Set(1, tx.BlockHeight);
                            addrRows.Set(2, 0);
                            addrRows.Set(3, n);

                            UnspentOutput prev;
                            if (unspent.TryGetValue(spending, out prev))
                            {
                                addrRows.SetText(4, prev.Address);
                                addrRows.Set(5, (long)prev.Value);

                                // Spent now, so it leaves the set. This is what keeps the
                                // dictionary the size of the UTXO set instead of the size of
                                // every output the chain has ever made.
                                unspent.Remove(spending);
                            }
                            else
                            {
                                unresolvedInputs++;
                                addrRows.SetNull(4);
                                addrRows.SetNull(5);
                            }

                            addrRows.Write();
                            addressRows++;
                        }

                        for (int n = 0; n < tx.Outputs.Count; n++)
                        {
                            Transaction.TxOutput output = tx.Outputs[n];
                            string? address = ScriptToAddress(output.ScriptPubKey);
                            if (address == null) scriptsWithNoAddress++;

                            addrRows.Set(0, tx.Hash);
                            addrRows.Set(1, tx.BlockHeight);
                            addrRows.Set(2, 1);
                            addrRows.Set(3, n);
                            addrRows.SetText(4, address);
                            addrRows.Set(5, (long)output.Value);
                            addrRows.Write();
                            addressRows++;

                            unspent[new OutPoint(tx.Hash, (uint)n)] =
                                new UnspentOutput { Address = address, Value = output.Value };
                        }
                    }
                }
                work.Commit();
            }

            // Built after the rows are in. Maintaining an index through several million inserts
            // costs far more than building it once over the finished table.
            //
            // Partial, so there is genuinely an index on the from-address and another on the
            // to-address rather than one covering both: a lookup by direction only walks the rows
            // of that direction, and each index is a fraction of the size of the combined one.
            Execute(conn, "create index ix_txaddr_from on txaddr(address) where direction = 0");
            Execute(conn, "create index ix_txaddr_to   on txaddr(address) where direction = 1");
            Execute(conn, "create index ix_txaddr_txid on txaddr(txid)");
            Execute(conn, "create index ix_tx_txid     on tx(txid)");

            Console.WriteLine("  transactions : " + transactions + ", address rows " + addressRows);
            Console.WriteLine("  coinbase in  : " + coinbaseInputs + " (no from-address to look up)");
            if (unresolvedInputs > 0)
            {
                Console.WriteLine("  unresolved   : " + unresolvedInputs
                                  + " inputs spend an output this walk never saw");
            }
            if (scriptsWithNoAddress > 0)
            {
                Console.WriteLine("  no address   : " + scriptsWithNoAddress
                                  + " outputs pay a script with no address in it");
            }
            Console.WriteLine("  utxo set     : " + unspent.Count + " unspent outputs carried forward");

            return transactions;
        }

        /// <summary>
        /// The whole chain's transactions in one SQLite file, from a list of transactions rather
        /// than from the blocks - the same two tables and the same four indexes SaveTransactionsToSqlite
        /// builds, so a query written against the segmented databases runs here unchanged.
        ///
        /// The difference between the two is what carries the UTXO set. SaveTransactionsToSqlite
        /// is called once per 50,000 block segment and is handed the set from outside, because an
        /// input in segment 4 spends an output made in segment 1 and only a set that has been
        /// walking along since height 0 still knows whose it was. This does the whole run in one
        /// call, so it owns its set and builds it as it goes.
        ///
        /// Which is also why it is a second walk when it follows the balance table: resolving an
        /// input consumes the output it points at, so by the time a balance walk has finished, the
        /// set it used no longer holds the outputs that were spent along the way and cannot answer
        /// the same questions twice. The only way to have them again is to walk again.
        ///
        /// Wants the same list the balance walk wants - every transaction once, in chain order,
        /// with Transaction.BlockHeight stamped by ParseBlock. Anything out of order shows up as
        /// unresolved inputs rather than as an error.
        ///
        /// Returns how many transactions were written.
        /// </summary>
        /// <param name="dbPath">The file to build. Deleted first if it exists.</param>
        /// <param name="transactions">Every transaction once, in chain order.</param>
        /// <param name="heightLimit">Stop before this height, so 200000 means the first 200,000
        /// blocks: heights 0 to 199999.</param>
        public static int SaveAllTransactionsToSqlite(string dbPath, List<Transaction> transactions,
                                                      int heightLimit)
        {
            string? directory = Path.GetDirectoryName(dbPath);
            if (directory != null) Directory.CreateDirectory(directory);

            // Rebuilt from scratch: the tables have no key to update against, so a second run over
            // an existing file would file every transaction twice.
            if (File.Exists(dbPath))
            {
                Console.WriteLine("  replacing the existing " + Path.GetFileName(dbPath));
                File.Delete(dbPath);
            }

            // Its own, because this call is the whole chain rather than one segment of it.
            var unspent = new Dictionary<OutPoint, UnspentOutput>();

            int written = 0;
            int addressRows = 0;
            int coinbaseInputs = 0;
            int unresolvedInputs = 0;
            int scriptsWithNoAddress = 0;

            var clock = Stopwatch.StartNew();

            using var conn = new SqliteConnection("Data Source=" + dbPath);
            conn.Open();

            // A derived index - everything in it can be rebuilt from the blocks - so durability is
            // worth nothing here and speed is worth a lot.
            Execute(conn, "pragma journal_mode = off");
            Execute(conn, "pragma synchronous = off");
            Execute(conn, "pragma temp_store = memory");
            Execute(conn, "pragma cache_size = -200000");

            Execute(conn, @"create table tx (
                                txid   blob    not null,
                                height integer not null,
                                offset integer not null,
                                length integer not null)");

            Execute(conn, @"create table txaddr (
                                txid      blob    not null,
                                height    integer not null,
                                direction integer not null,
                                n         integer not null,
                                address   text,
                                value     integer)");

            using (var work = conn.BeginTransaction())
            {
                using var txRows = new PreparedInsert(conn, work, "tx",
                    new[] { "txid", "height", "offset", "length" },
                    new[] { SqliteType.Blob, SqliteType.Integer, SqliteType.Integer, SqliteType.Integer });

                using var addrRows = new PreparedInsert(conn, work, "txaddr",
                    new[] { "txid", "height", "direction", "n", "address", "value" },
                    new[] { SqliteType.Blob, SqliteType.Integer, SqliteType.Integer,
                            SqliteType.Integer, SqliteType.Text, SqliteType.Integer });

                foreach (Transaction tx in transactions)
                {
                    if (tx.BlockHeight >= heightLimit)
                    {
                        continue;
                    }

                    // The txid goes in as the 32 bytes it is stored in on disk, little endian,
                    // which is the order the rocksdb index and the segmented databases use too.
                    // A query holding the hash an explorer shows has to reverse it first.
                    txRows.Set(0, tx.Hash);
                    txRows.Set(1, tx.BlockHeight);
                    txRows.Set(2, tx.ByteOffset);
                    txRows.Set(3, tx.ByteLength);
                    txRows.Write();
                    written++;

                    // One line per half million, because this is a long enough job that a console
                    // saying nothing for an hour is indistinguishable from a hung one.
                    if (written % 500000 == 0)
                    {
                        Console.WriteLine("  " + written + " transactions, height " + tx.BlockHeight
                                          + ", " + clock.Elapsed.TotalSeconds.ToString("F0") + "s");
                    }

                    // Inputs first, so an output this transaction creates cannot be taken for one
                    // of the outputs it spends.
                    for (int n = 0; n < tx.Inputs.Count; n++)
                    {
                        Transaction.TxInput input = tx.Inputs[n];

                        // A coinbase spends nothing and names the all-zero outpoint. There is no
                        // from-address to look up, so it gets no row.
                        if (IsAllZero(input.TxId))
                        {
                            coinbaseInputs++;
                            continue;
                        }

                        var spending = new OutPoint(input.TxId, input.Vout);

                        addrRows.Set(0, tx.Hash);
                        addrRows.Set(1, tx.BlockHeight);
                        addrRows.Set(2, 0);
                        addrRows.Set(3, n);

                        UnspentOutput previous;
                        if (unspent.TryGetValue(spending, out previous))
                        {
                            addrRows.SetText(4, previous.Address);
                            addrRows.Set(5, (long)previous.Value);

                            // Spent now, so it leaves the set. This is what keeps the dictionary
                            // the size of the UTXO set instead of the size of every output the
                            // chain has ever made.
                            unspent.Remove(spending);
                        }
                        else
                        {
                            unresolvedInputs++;
                            addrRows.SetNull(4);
                            addrRows.SetNull(5);
                        }

                        addrRows.Write();
                        addressRows++;
                    }

                    for (int n = 0; n < tx.Outputs.Count; n++)
                    {
                        Transaction.TxOutput output = tx.Outputs[n];
                        string? address = ScriptToAddress(output.ScriptPubKey);
                        if (address == null) scriptsWithNoAddress++;

                        addrRows.Set(0, tx.Hash);
                        addrRows.Set(1, tx.BlockHeight);
                        addrRows.Set(2, 1);
                        addrRows.Set(3, n);
                        addrRows.SetText(4, address);
                        addrRows.Set(5, (long)output.Value);
                        addrRows.Write();
                        addressRows++;

                        unspent[new OutPoint(tx.Hash, (uint)n)] =
                            new UnspentOutput { Address = address, Value = output.Value };
                    }
                }

                work.Commit();
            }

            // Built after the rows are in. Maintaining an index through several million inserts
            // costs far more than building it once over the finished table - and over the whole
            // chain rather than a segment of it, this is the part of the call that takes the time.
            Console.WriteLine("  indexing     : " + addressRows + " address rows, "
                              + clock.Elapsed.TotalSeconds.ToString("F0") + "s so far");

            // Partial, so there is genuinely an index on the from-address and another on the
            // to-address rather than one covering both: a lookup by direction only walks the rows
            // of that direction, and each index is a fraction of the size of the combined one.
            Execute(conn, "create index ix_txaddr_from on txaddr(address) where direction = 0");
            Execute(conn, "create index ix_txaddr_to   on txaddr(address) where direction = 1");
            Execute(conn, "create index ix_txaddr_txid on txaddr(txid)");
            Execute(conn, "create index ix_tx_txid     on tx(txid)");

            clock.Stop();

            Console.WriteLine("  transactions : " + written + ", address rows " + addressRows
                              + ", in " + clock.Elapsed.TotalSeconds.ToString("F1") + "s");
            Console.WriteLine("  coinbase in  : " + coinbaseInputs + " (no from-address to look up)");
            if (unresolvedInputs > 0)
            {
                Console.WriteLine("  unresolved   : " + unresolvedInputs
                                  + " inputs spend an output this walk never saw - either the list"
                                  + " does not start at height 0 or it is not in chain order");
            }
            if (scriptsWithNoAddress > 0)
            {
                Console.WriteLine("  no address   : " + scriptsWithNoAddress
                                  + " outputs pay a script with no address in it");
            }
            Console.WriteLine("  utxo set     : " + unspent.Count + " unspent outputs at the end");

            var built = new FileInfo(dbPath);
            Console.WriteLine("  written      : " + dbPath + ", "
                              + (built.Length / 1024.0 / 1024.0).ToString("F0") + " MB");

            return written;
        }

        // ------------------------------------------------------------------------------------
        // SQLite transaction index, read back
        // ------------------------------------------------------------------------------------

        /// <summary>One side of one transaction, the way the txaddr table holds it: an output
        /// paying an address, or an input spending one.
        ///
        /// A struct, and deliberately. A whole-chain database has thirty million of these rows in
        /// it, and as a class that is thirty million object headers - most of a gigabyte spent
        /// before any of the four fields is counted, and thirty million more objects for the
        /// collector to walk. In an array of structs they cost their fields and the array.</summary>
        public readonly struct StoredTxAddress
        {
            /// <summary>Whose side this is, or null where the database has none: an output whose
            /// script names no address, or an input the writer could not resolve. Value tells
            /// those two apart - see below.
            ///
            /// Every row naming the same address shares one string instance, so the addresses of
            /// the whole chain cost one copy each rather than one per row.</summary>
            public readonly string? Address;

            /// <summary>Satoshis on this side, or -1 where the row holds null, which the writer
            /// files for an input whose output it never saw. So a null address with a value is an
            /// output with no address in its script, or an input spending one; a null address with
            /// -1 is an input nothing at all could be said about.</summary>
            public readonly long Value;

            /// <summary>Which input, or which output, counted from 0 within its own side.</summary>
            public readonly int N;

            /// <summary>True for an output - direction 1 in the table - false for an input.</summary>
            public readonly bool IsOutput;

            public StoredTxAddress(string? address, long value, int n, bool isOutput)
            {
                Address = address;
                Value = value;
                N = n;
                IsOutput = isOutput;
            }
        }

        /// <summary>
        /// One transaction as the database holds it, which is less than a <see cref="Transaction"/>
        /// in one way and more in another.
        ///
        /// Less, because nothing in the file can rebuild one. There are no scripts, no outpoints,
        /// no sequence numbers and no bytes - only the txid, the locator saying where the bytes
        /// are in the block that carries them, and one row per side.
        ///
        /// More, because the address on an input is not in the transaction at all. It belongs to
        /// the output being spent, made in some earlier block, and the writer did that lookup on
        /// the way in with the UTXO set it was carrying anyway. Reading it back therefore needs no
        /// set and no earlier blocks: a row says who paid whom on its own.
        /// </summary>
        public sealed class StoredTransaction
        {
            /// <summary>The txid, little endian, the order it is stored in on disk and the order
            /// Transaction.Hash uses. Explorers show it reversed - GetDisplayTxid does that.</summary>
            public byte[] Txid = Array.Empty<byte>();

            /// <summary>The height of the block that carries it.</summary>
            public int Height;

            /// <summary>Where its bytes start inside that block's serialization, counted from byte
            /// 0 of the block - Transaction.ByteOffset, filed unchanged.</summary>
            public int Offset;

            /// <summary>How many bytes it occupies there. With Offset and the block this cuts the
            /// transaction back out of the chain, which is the only way to reach anything the
            /// database does not itself hold.</summary>
            public int Length;

            /// <summary>Inputs first, then outputs, in the order the writer filed them - which is
            /// the order they appear in the transaction, each side numbered by N.</summary>
            public StoredTxAddress[] Addresses = Array.Empty<StoredTxAddress>();

            /// <summary>The txid the way an explorer shows it.</summary>
            public string GetDisplayTxid()
            {
                return ToDisplayHex(Txid);
            }

            /// <summary>
            /// Whether this is its block's coinbase, which the rows answer without being asked: a
            /// coinbase spends the all-zero outpoint and the writer files no row for it, while
            /// every other input gets a row whether or not its address could be resolved. So a
            /// transaction with no input row is a coinbase, and nothing else is.
            /// </summary>
            public bool IsCoinbase()
            {
                foreach (StoredTxAddress side in Addresses)
                {
                    if (!side.IsOutput)
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        /// <summary>
        /// Every transaction in the database, in the order it was written, which is chain order.
        ///
        /// The two tables are joined by position rather than by SQL, and that is the whole trick
        /// of this method. SaveAllTransactionsToSqlite filled both in one pass - a transaction's
        /// row into tx, then that transaction's rows into txaddr, then the next transaction - so
        /// reading both in rowid order walks the same transactions in the same sequence, and a
        /// transaction's address rows are exactly the run of txaddr rows sitting under its txid.
        /// Both statements are plain scans (EXPLAIN QUERY PLAN says SCAN tx and SCAN txaddr with
        /// no sort - "order by rowid" on a rowid table is the order a scan comes back in anyway),
        /// so six gigabytes are read once, sequentially, and no index is touched.
        ///
        /// Joining them in SQL is the obvious way to write this and is the thing to avoid: seven
        /// million index lookups scattered through a file far larger than memory, against one pass
        /// that never seeks.
        ///
        /// The txid is compared on every row rather than trusted, because a positional join fails
        /// silently. If the two orders ever disagree the run boundaries land in the wrong places
        /// and every transaction after that point carries somebody else's addresses, with nothing
        /// about the result looking wrong. A transaction coming out with no rows at all is that
        /// failure - a transaction with no outputs is not valid and cannot be in here - so it
        /// throws rather than handing back a chain that quietly means nothing.
        ///
        /// Streamed, so a caller that only wants to count something never holds the chain;
        /// LoadAllTransactionsFromSqlite below is the one that costs memory. Address strings are
        /// pooled across the whole read, so the pool is held for as long as the enumeration runs -
        /// a few million strings against the thirty million rows that share them.
        /// </summary>
        /// <param name="dbPath">The file SaveAllTransactionsToSqlite wrote.</param>
        /// <param name="heightLimit">Stop before this height, so 200000 means the first 200,000
        /// blocks: heights 0 to 199999.</param>
        public static IEnumerable<StoredTransaction> StreamAllTransactionsFromSqlite(string dbPath,
                                                                                     int heightLimit)
        {
            if (!File.Exists(dbPath))
            {
                throw new FileNotFoundException("there is no transaction database at " + dbPath, dbPath);
            }

            // Read-only, and a connection each. SQLite will step two statements over one
            // connection quite happily, but two connections keep the scans sharing nothing at all,
            // and read-only says outright that a load cannot be what corrupts the file.
            string connectionText = "Data Source=" + dbPath + ";Mode=ReadOnly";

            using var txConn = new SqliteConnection(connectionText);
            using var addrConn = new SqliteConnection(connectionText);
            txConn.Open();
            addrConn.Open();

            using var txCmd = txConn.CreateCommand();
            txCmd.CommandText = "select txid, height, offset, length from tx"
                                + " where height < $limit order by rowid";
            txCmd.Parameters.AddWithValue("$limit", heightLimit);

            using var addrCmd = addrConn.CreateCommand();
            addrCmd.CommandText = "select txid, direction, n, address, value from txaddr"
                                  + " where height < $limit order by rowid";
            addrCmd.Parameters.AddWithValue("$limit", heightLimit);

            using var txRows = txCmd.ExecuteReader();
            using var addrRows = addrCmd.ExecuteReader();

            // The txid of the address row waiting to be attached, read into a buffer that is kept
            // rather than allocated per row - thirty million 32 byte arrays that live long enough
            // to be compared once and dropped is work worth not doing.
            byte[] pendingTxid = new byte[32];
            bool pending = addrRows.Read();
            if (pending)
            {
                addrRows.GetBytes(0, 0, pendingTxid, 0, 32);
            }

            // One instance per distinct address, handed to every row that names it.
            var pool = new Dictionary<string, string>();

            // Refilled per transaction and copied out at the size it ended up, so the arrays that
            // are kept are exact and this list's spare capacity is the only slack in the read.
            var sides = new List<StoredTxAddress>(8);

            while (txRows.Read())
            {
                byte[] txid = new byte[32];
                txRows.GetBytes(0, 0, txid, 0, 32);

                var stored = new StoredTransaction();
                stored.Txid = txid;
                stored.Height = txRows.GetInt32(1);
                stored.Offset = txRows.GetInt32(2);
                stored.Length = txRows.GetInt32(3);

                sides.Clear();

                while (pending && SameTxid(pendingTxid, txid))
                {
                    long direction = addrRows.GetInt64(1);
                    int n = addrRows.GetInt32(2);

                    string? address = null;
                    if (!addrRows.IsDBNull(3))
                    {
                        string read = addrRows.GetString(3);

                        string? shared;
                        if (pool.TryGetValue(read, out shared))
                        {
                            address = shared;
                        }
                        else
                        {
                            pool.Add(read, read);
                            address = read;
                        }
                    }

                    long value = -1;
                    if (!addrRows.IsDBNull(4))
                    {
                        value = addrRows.GetInt64(4);
                    }

                    bool isOutput = false;
                    if (direction == 1)
                    {
                        isOutput = true;
                    }

                    sides.Add(new StoredTxAddress(address, value, n, isOutput));

                    pending = addrRows.Read();
                    if (pending)
                    {
                        addrRows.GetBytes(0, 0, pendingTxid, 0, 32);
                    }
                }

                if (sides.Count == 0)
                {
                    throw new InvalidDataException(
                        "the transaction " + ToDisplayHex(txid) + " at height " + stored.Height
                        + " has no rows in txaddr. Every transaction has at least one output and"
                        + " every output was filed, so the two tables are no longer being read in"
                        + " the same order and everything past this point would carry another"
                        + " transaction's addresses");
                }

                stored.Addresses = sides.ToArray();
                yield return stored;
            }

            // Rows nobody claimed. With the scans aligned there are none - both end on the same
            // transaction - so any at all are the same failure as above, found from the other end.
            long orphaned = 0;
            while (pending)
            {
                orphaned++;
                pending = addrRows.Read();
            }

            if (orphaned > 0)
            {
                throw new InvalidDataException(orphaned + " rows in txaddr belong to no transaction"
                                               + " in tx below height " + heightLimit
                                               + ", so the two tables do not describe the same run");
            }
        }

        /// <summary>Whether two 32 byte txids are the same. Compared whole rather than by the
        /// first eight bytes: this is what keeps the positional join honest, so it is the one
        /// place where a shortcut would defeat the check it is there to make.</summary>
        static bool SameTxid(byte[] left, byte[] right)
        {
            return left.AsSpan().SequenceEqual(right);
        }

        /// <summary>
        /// The database in memory, in one list, in chain order.
        ///
        /// What this costs is worth knowing before calling it. A whole-chain file - the first
        /// 200,000 blocks are about six gigabytes of it - is roughly seven million transactions
        /// and thirty million address rows, and holding all of that lands somewhere around three
        /// gigabytes of managed heap: a StoredTransaction and a 32 byte txid each, an array of
        /// sides each, twenty four bytes per side, and one shared string per distinct address. The
        /// summary prints what it actually took, which is the number to trust over that estimate.
        ///
        /// The alternative is StreamAllTransactionsFromSqlite, which hands them over one at a time
        /// and holds nothing. Anything that walks the chain once - balances, a search for an
        /// address, counting - wants the stream. This is for the second and third pass, where
        /// paying the read once beats reading six gigabytes off the disk again.
        /// </summary>
        /// <param name="dbPath">The file SaveAllTransactionsToSqlite wrote.</param>
        /// <param name="heightLimit">Stop before this height, so 200000 means the first 200,000
        /// blocks: heights 0 to 199999.</param>
        public static List<StoredTransaction> LoadAllTransactionsFromSqlite(string dbPath, int heightLimit)
        {
            var file = new FileInfo(dbPath);
            Console.WriteLine("loading transactions from " + dbPath + ", "
                              + (file.Length / 1024.0 / 1024.0 / 1024.0).ToString("F2") + " GB:");

            long beforeHeap = GC.GetTotalMemory(false);
            var clock = Stopwatch.StartNew();

            // Seven million is where this ends up on a whole-chain file, and growing a list into
            // that copies its way through fourteen million entries on the doublings. A million up
            // front costs eight megabytes and skips most of it.
            var loaded = new List<StoredTransaction>(1024 * 1024);

            long inputRows = 0;
            long outputRows = 0;
            long coinbases = 0;
            long unresolvedInputs = 0;
            long outputsWithNoAddress = 0;
            long spentWithNoAddress = 0;
            int firstHeight = -1;
            int lastHeight = -1;
            int outOfOrder = 0;

            foreach (StoredTransaction tx in StreamAllTransactionsFromSqlite(dbPath, heightLimit))
            {
                if (firstHeight < 0)
                {
                    firstHeight = tx.Height;
                }

                // The order everything downstream depends on, checked while it is free. A height
                // that goes backwards says the file was not written by a walk in chain order, and
                // a balance built from it would be wrong with nothing about it to say so.
                if (tx.Height < lastHeight)
                {
                    outOfOrder++;
                }
                lastHeight = tx.Height;

                bool coinbase = true;
                foreach (StoredTxAddress side in tx.Addresses)
                {
                    if (side.IsOutput)
                    {
                        outputRows++;
                        if (side.Address == null)
                        {
                            outputsWithNoAddress++;
                        }
                        continue;
                    }

                    coinbase = false;
                    inputRows++;

                    if (side.Value < 0)
                    {
                        unresolvedInputs++;
                    }
                    else if (side.Address == null)
                    {
                        spentWithNoAddress++;
                    }
                }

                if (coinbase)
                {
                    coinbases++;
                }

                loaded.Add(tx);

                // Same cadence the writer prints at, for the same reason: this is long enough that
                // a console saying nothing is indistinguishable from a hung one.
                if (loaded.Count % 500000 == 0)
                {
                    Console.WriteLine("  " + loaded.Count + " transactions, height " + tx.Height
                                      + ", " + clock.Elapsed.TotalSeconds.ToString("F0") + "s");
                }
            }

            clock.Stop();

            long heap = GC.GetTotalMemory(false) - beforeHeap;

            Console.WriteLine("  transactions : " + loaded.Count + " in "
                              + clock.Elapsed.TotalSeconds.ToString("F1") + "s");
            Console.WriteLine("  heights      : " + firstHeight + " to " + lastHeight
                              + ", " + coinbases + " coinbases");
            Console.WriteLine("  address rows : " + (inputRows + outputRows) + ", "
                              + inputRows + " spending and " + outputRows + " paying");
            Console.WriteLine("  held         : " + (heap / 1024.0 / 1024.0 / 1024.0).ToString("F2")
                              + " GB of managed heap");

            if (outputsWithNoAddress > 0)
            {
                Console.WriteLine("  no address   : " + outputsWithNoAddress
                                  + " outputs pay a script with no address in it, "
                                  + spentWithNoAddress + " of them later spent");
            }

            if (unresolvedInputs > 0)
            {
                Console.WriteLine("  unresolved   : " + unresolvedInputs
                                  + " inputs have no address in the file - the run that wrote it"
                                  + " never saw the output they spend, so nothing reading it back"
                                  + " can attribute them either");
            }

            if (outOfOrder > 0)
            {
                Console.WriteLine("  OUT OF ORDER : " + outOfOrder
                                  + " transactions came back at a lower height than the one before"
                                  + " them - the file was not written in chain order");
            }

            if (firstHeight > 0)
            {
                Console.WriteLine("  PARTIAL      : the file starts at height " + firstHeight
                                  + " rather than 0, so balances taken off it are missing whatever"
                                  + " those blocks paid");
            }

            return loaded;
        }

        /// <summary>
        /// The same address table as CollectAddressBalances and CollectAddressBalancesFromTransactions,
        /// built out of the database instead of out of the chain.
        ///
        /// This is the cheap one, and it is the reason the database is worth writing. Both of the
        /// others carry a UTXO set - every unspent output in the chain, held so that an input can
        /// be told whose coins it moves - because an input names an outpoint and nothing else. The
        /// writer had that set in hand and put the answer in the row, so this walk needs no set, no
        /// outpoints and no lookup at all: a direction 0 row already says which address is being
        /// debited and by how much, and a direction 1 row says who is being paid.
        ///
        /// The rules are otherwise the ones CollectAddressBalances documents at length - mining
        /// rewards are outputs like any other, scripts with no address are counted rather than
        /// hidden, and the reconciliation at the end says mined less fees equals what the balances
        /// hold plus what sits in scripts nobody can name.
        ///
        /// What it cannot do is check itself the way the other two do. They rebuild every address
        /// from the block bytes; this inherits whatever the writer resolved, and would repeat the
        /// writer's mistakes without noticing. CompareAddressBalances against the block walk is
        /// what makes it trustworthy - two tables built from the same chain by routines sharing no
        /// code, agreeing on several million addresses.
        /// </summary>
        /// <param name="transactions">Every transaction once, in chain order, out of the file.</param>
        /// <param name="heightLimit">Stop before this height, so 200000 means heights 0 to 199999.</param>
        /// <param name="csvPath">Where to write the full table, or null to only return it. The
        /// whole chain is a 350 MB file, so this is worth passing null for while iterating.</param>
        public static List<AddressBalance> CollectAddressBalancesFromStoredTransactions(
            List<StoredTransaction> transactions, int heightLimit, string? csvPath)
        {
            var balances = new Dictionary<string, AddressBalance>();

            long transactionOrdinal = 0;
            int walked = 0;
            int unresolvedInputs = 0;
            int lastHeight = -1;
            int outOfOrder = 0;

            ulong mined = 0;
            ulong fees = 0;

            // The two ends of what nobody can be credited with: paid into scripts with no address
            // in them, and taken back out again by something spending one. What is left is still
            // sitting there, and is the term that makes the reconciliation add up.
            ulong paidToNoAddress = 0;
            ulong spentFromNoAddress = 0;

            var clock = Stopwatch.StartNew();

            foreach (StoredTransaction tx in transactions)
            {
                if (tx.Height >= heightLimit)
                {
                    continue;
                }

                if (tx.Height < lastHeight)
                {
                    outOfOrder++;
                }
                lastHeight = tx.Height;

                transactionOrdinal++;
                walked++;

                bool coinbase = true;
                ulong spent = 0;
                ulong created = 0;

                foreach (StoredTxAddress side in tx.Addresses)
                {
                    if (!side.IsOutput)
                    {
                        // An input, and the address on it belongs to the output being spent -
                        // looked up when the file was written, which is the whole saving here.
                        coinbase = false;

                        if (side.Value < 0)
                        {
                            unresolvedInputs++;
                            continue;
                        }

                        spent += (ulong)side.Value;

                        if (side.Address == null)
                        {
                            spentFromNoAddress += (ulong)side.Value;
                            continue;
                        }

                        AddressBalance from = Touch(balances, side.Address, tx.Height,
                                                    transactionOrdinal);
                        from.Balance -= side.Value;
                        continue;
                    }

                    created += (ulong)side.Value;

                    if (side.Address == null)
                    {
                        paidToNoAddress += (ulong)side.Value;
                        continue;
                    }

                    AddressBalance to = Touch(balances, side.Address, tx.Height, transactionOrdinal);
                    to.Balance += side.Value;
                }

                // A coinbase pays out the subsidy and the fees of every other transaction in its
                // block together, so what it creates is the block's whole payment. Everything else
                // spends more than it creates, and the difference is the fee that comes back in
                // that coinbase.
                if (coinbase)
                {
                    mined += created;
                }
                else if (spent >= created)
                {
                    fees += spent - created;
                }
            }

            clock.Stop();

            var list = new List<AddressBalance>(balances.Count);
            long balanceTotal = 0;
            int negative = 0;

            foreach (KeyValuePair<string, AddressBalance> entry in balances)
            {
                list.Add(entry.Value);
                balanceTotal += entry.Value.Balance;
                if (entry.Value.Balance < 0)
                {
                    negative++;
                }
            }

            list.Sort((a, b) =>
            {
                if (a.Balance != b.Balance)
                {
                    return b.Balance.CompareTo(a.Balance);
                }
                return string.CompareOrdinal(a.Address, b.Address);
            });

            Console.WriteLine("address balances from " + transactions.Count
                              + " stored transactions, below height " + heightLimit + ":");
            Console.WriteLine("  transactions : " + walked + " walked in "
                              + clock.Elapsed.TotalSeconds.ToString("F1") + "s");
            Console.WriteLine("  addresses    : " + list.Count);
            Console.WriteLine("  mined        : " + (mined / 100000000.0).ToString("F8")
                              + " BTC paid out by coinbases, fees included");
            Console.WriteLine("  balances     : " + (balanceTotal / 100000000.0).ToString("F8") + " BTC held");

            if (outOfOrder > 0)
            {
                Console.WriteLine("  OUT OF ORDER : " + outOfOrder
                                  + " transactions arrived at a lower height than the one before"
                                  + " them - the file is not in chain order and the balances are wrong");
            }

            if (unresolvedInputs > 0)
            {
                Console.WriteLine("  unresolved   : " + unresolvedInputs
                                  + " inputs have no address in the file, so the balances above"
                                  + " them are too high by whatever those inputs spent");
            }
            else
            {
                ulong unattributed = paidToNoAddress - spentFromNoAddress;

                long expected = (long)mined - (long)fees;
                long actual = balanceTotal + (long)unattributed;

                Console.WriteLine("  fees         : " + (fees / 100000000.0).ToString("F8")
                                  + " BTC spent but not paid out, re-mined inside the coinbases");
                Console.WriteLine("  unattributed : " + (unattributed / 100000000.0).ToString("F8")
                                  + " BTC unspent in scripts with no address");

                if (expected == actual)
                {
                    Console.WriteLine("  reconciles   : mined - fees == balances + unattributed");
                }
                else
                {
                    Console.WriteLine("  MISMATCH     : mined - fees is " + expected
                                      + " sats, balances + unattributed is " + actual
                                      + ", off by " + (expected - actual));
                }
            }

            if (negative > 0)
            {
                Console.WriteLine("  negative     : " + negative
                                  + " addresses spent more than they were paid, which cannot happen"
                                  + " on a file that started at height 0");
            }

            int show = 20;
            if (list.Count < show)
            {
                show = list.Count;
            }

            for (int i = 0; i < show; i++)
            {
                AddressBalance held = list[i];
                Console.WriteLine("  " + held.Address.PadRight(35)
                                  + (held.Balance / 100000000.0).ToString("F8").PadLeft(18) + " BTC"
                                  + held.Transactions.ToString().PadLeft(8) + " txs"
                                  + "  heights " + held.FirstHeight + " to " + held.LastHeight);
            }

            if (csvPath != null)
            {
                string? directory = Path.GetDirectoryName(csvPath);
                if (directory != null)
                {
                    Directory.CreateDirectory(directory);
                }

                using (var writer = new StreamWriter(csvPath, false))
                {
                    writer.WriteLine("address,balance,firstHeight,lastHeight,transactions");
                    foreach (AddressBalance held in list)
                    {
                        writer.WriteLine(held.Address + "," + held.Balance + "," + held.FirstHeight
                                         + "," + held.LastHeight + "," + held.Transactions);
                    }
                }

                Console.WriteLine("  written      : " + csvPath + " (balance in satoshis)");
            }

            return list;
        }

        // ------------------------------------------------------------------------------------
        // headers.dat - the header chain MainBlockDownload caches, in height order
        // ------------------------------------------------------------------------------------

        /// <summary>Bytes in a block header. Fixed by the protocol, and headers.dat is nothing else.</summary>
        const int HeaderBytes = 80;

        /// <summary>One header out of headers.dat, at the height its position in the file gives it.</summary>
        public sealed class HeaderRecord
        {
            /// <summary>Position in the file, which is the height - the file is written in order.</summary>
            public int Height;

            /// <summary>The 80 header bytes exactly as they sit in the file.</summary>
            public byte[] Raw = Array.Empty<byte>();

            /// <summary>The header's hash, little endian, the order it is stored in on disk.</summary>
            public byte[] HashBytes = Array.Empty<byte>();

            /// <summary>
            /// The hash in the reversed order explorers show. Built on demand rather than held as
            /// a field: a million of these strings is another 150 MB, and most callers want the
            /// hash for only a handful of the headers they read.
            /// </summary>
            public string GetDisplayHash()
            {
                return ToDisplayHex(HashBytes);
            }

            /// <summary>The parent's hash in display order, from bytes 4..35 of the header.</summary>
            public string GetPrevBlockHash()
            {
                return ToDisplayHex(GetPrevBlockHashBytes());
            }

            /// <summary>The same 32 bytes as stored, for comparing against raw header bytes.</summary>
            public byte[] GetPrevBlockHashBytes()
            {
                return Raw.AsSpan(4, 32).ToArray();
            }

            /// <summary>The miner's timestamp in unix seconds, from bytes 68..71.</summary>
            public uint GetUnixTime()
            {
                return BinaryPrimitives.ReadUInt32LittleEndian(Raw.AsSpan(68, 4));
            }

            public DateTime GetTimeUtc()
            {
                return DateTimeOffset.FromUnixTimeSeconds(GetUnixTime()).UtcDateTime;
            }
        }

        /// <summary>
        /// Reads headers.dat out of a blk file directory and prints what it covers.
        ///
        /// The file is the header chain MainBlockDownload builds while it syncs: 80 bytes per
        /// header, one after another, with no magic bytes, no size fields and nothing else in
        /// between - so its length is exactly 80 times the number of headers in it. It is never
        /// XORed, unlike the blk files beside it, so xor.dat does not come into this.
        ///
        /// What makes it worth reading is the order. Headers go in as the chain is walked, tip
        /// first never happening, so record i IS the block at height i - which the blk files
        /// cannot tell you, since they hold blocks in the order the peers answered. This is where
        /// a height for a block read out of a blk file comes from: match its hash against these.
        ///
        /// Every header names its parent, so the file checks itself. Header i's bytes 4..35 have
        /// to be header i-1's hash; where that fails the file was torn or truncated and every
        /// height past the break would be wrong, so the read stops there and says so.
        ///
        /// Costs memory the same way ReadAllBlocks does - about 200 bytes a header, so roughly
        /// 190 MB for a million of them. Pass maxHeaders to read only the start of the chain.
        /// </summary>
        public static List<HeaderRecord> ReadHeadersFile(string directory, int maxHeaders = int.MaxValue)
        {
            if (maxHeaders < 1)
                throw new ArgumentOutOfRangeException(nameof(maxHeaders), "ask for at least one header");

            string path = Path.Combine(directory, "headers.dat");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("no headers.dat in " + directory
                                                + " - MainBlockDownload writes it while it syncs headers", path);
            }

            var clock = Stopwatch.StartNew();
            var headers = new List<HeaderRecord>();

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);

            long usable = fs.Length - (fs.Length % HeaderBytes);
            if (usable != fs.Length)
            {
                Console.Error.WriteLine("headers.dat: ignoring a trailing partial header of "
                                        + (fs.Length - usable) + " bytes");
            }

            bool broken = false;
            byte[] previousHash = Array.Empty<byte>();

            for (long off = 0; off < usable && headers.Count < maxHeaders; off += HeaderBytes)
            {
                byte[] raw = new byte[HeaderBytes];
                fs.ReadExactly(raw, 0, HeaderBytes);

                if (headers.Count > 0 && !previousHash.AsSpan().SequenceEqual(raw.AsSpan(4, 32)))
                {
                    Console.Error.WriteLine("headers.dat: chain breaks at height " + headers.Count
                                            + " - stopping there, the file holds "
                                            + (usable / HeaderBytes) + " headers");
                    broken = true;
                    break;
                }

                byte[] hash = DoubleSha256(raw, 0, HeaderBytes);
                headers.Add(new HeaderRecord { Height = headers.Count, Raw = raw, HashBytes = hash });
                previousHash = hash;
            }

            clock.Stop();

            Console.WriteLine();
            Console.WriteLine("headers.dat: " + headers.Count + " headers, read in "
                              + clock.Elapsed.TotalSeconds.ToString("F1") + "s");

            if (headers.Count == 0)
            {
                Console.WriteLine("  the file is empty");
                return headers;
            }

            PrintHeaderLine("first", headers[0]);
            PrintHeaderLine("tip  ", headers[headers.Count - 1]);

            long onDisk = usable / HeaderBytes;
            if (!broken && headers.Count < onDisk)
            {
                Console.WriteLine("  stopped at maxHeaders - the file holds " + onDisk);
            }

            return headers;
        }

        static void PrintHeaderLine(string label, HeaderRecord header)
        {
            Console.WriteLine("  " + label + " height " + header.Height.ToString().PadLeft(7)
                              + "  " + FormatUnixTime(header.GetUnixTime())
                              + "  " + header.GetDisplayHash());
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
    

    
        // walletsFromList2 is the table the caller built somewhere else - off the database, or off
        // a collected list - and is a parameter because the comparison at the bottom of this
        // method wants it. It was left behind when this method was split out of the one above and
        // stopped compiling; null means there is nothing to compare against, which is what the
        // check down there is for.
        static int DeleteLater(List<BlockRaw> loadedBlocks, List<Transaction> allTransactions,
                               List<AddressBalance>? walletsFromList2)
        {

            if (false)
            {
                // load rocksdb blocks
                //
                // The branch above earns this one: it walked the blk files, worked out which
                // blocks form the longest chain and in what order, and wrote that out. So there is
                // no scanning, no chain to rebuild and nothing to sort here - the blocks come back
                // in height order because that is how they were filed.
                // One store per run the branch above wrote - blocks1, blocks2, and so on, each
                // holding up to 50,000 blocks in height order. Read back in the same order they
                // were written they concatenate into one chain, which is what makes the link
                // check further down worth running: it is then testing the seams between the
                // stores as well as the blocks inside each one.
                string rocksDbBase = "C:\\btcblock\\rocksdb\\blocks";

                // Counted first, and on its own: this is Directory.Exists until one is missing,
                // so however many runs were written is however many get read - there is no count
                // here to keep in step with the writer. Opening them is the expensive part and
                // that happens below, once this knows how many there are to open.
                int stores = 0;
                while (Directory.Exists(rocksDbBase + (stores + 1)))
                {
                    stores++;
                }

                if (stores == 0)
                {
                    Console.Error.WriteLine("no stores found - looked for " + rocksDbBase + "1");
                    Console.Error.WriteLine("set rocksDbloaded2 to false to build them from the blk files first");
                    return 1;
                }

                List<BlockRaw> loaded2;
                try
                {
                    var loadClock = Stopwatch.StartNew();

                    // One store per thread. They are separate databases in separate directories
                    // sharing no handle, and LoadBlocksFromRocksDb keeps everything it touches
                    // local - its list, its options, its RocksDb and its iterator are all created
                    // inside the call. So the only thing crossing threads is the array each
                    // result is dropped into, and every task owns one slot of it.
                    //
                    // Worth doing because most of the cost is not the disk: every block is
                    // re-hashed on the way out to check it against the hash it is filed under,
                    // which is CPU work that scales rather than queueing behind one disk head.
                    var perStore = new List<BlockRaw>[stores];
                    var summary = new string[stores];

                    Parallel.For(0, stores, i =>
                    {
                        string storePath = rocksDbBase + (i + 1);
                        List<BlockRaw> fromStore = LoadBlocksFromRocksDb(storePath);
                        perStore[i] = fromStore;

                        // Built here, printed below in store order. Writing to the console from
                        // inside the loop would put the lines down in whatever order the threads
                        // happened to finish, which reads like the stores are out of order.
                        if (fromStore.Count == 0)
                        {
                            summary[i] = "  " + Path.GetFileName(storePath) + " : empty";
                        }
                        else
                        {
                            summary[i] = "  " + Path.GetFileName(storePath) + " : " + fromStore.Count
                                         + " blocks, heights " + fromStore[0].BlockIndex
                                         + " to " + fromStore[fromStore.Count - 1].BlockIndex;
                        }
                    });

                    loadClock.Stop();

                    foreach (string line in summary)
                    {
                        Console.WriteLine(line);
                    }

                    // Joined back up in store order, which is height order - the threads finish in
                    // whatever order they like but nothing reads perStore until they are all done.
                    // Sized up front so the list does not copy its whole array every time it grows
                    // past a couple of hundred thousand entries.
                    int total = 0;
                    foreach (List<BlockRaw> fromStore in perStore)
                    {
                        total += fromStore.Count;
                    }

                    loaded2 = new List<BlockRaw>(total);
                    foreach (List<BlockRaw> fromStore in perStore)
                    {
                        loaded2.AddRange(fromStore);
                    }

                    Console.WriteLine("rocksdb loaded2: " + loaded2.Count + " blocks from " + stores
                                      + " stores in " + loadClock.Elapsed.TotalSeconds.ToString("F2") + "s");
                }
                catch (AggregateException ex)
                {
                    // Parallel.For collects everything that threw and hands it back in one of
                    // these, whose own Message is just "One or more errors occurred" - the part
                    // worth reading is inside it.
                    Console.Error.WriteLine("could not load the stores:");
                    foreach (Exception inner in ex.Flatten().InnerExceptions)
                    {
                        Console.Error.WriteLine("  " + inner.Message);
                    }
                    Console.Error.WriteLine("set rocksDbloaded2 to false to build them from the blk files first");
                    return 1;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("could not load the stores: " + ex.Message);
                    Console.Error.WriteLine("set rocksDbloaded2 to false to build them from the blk files first");
                    return 1;
                }

                if (loaded2.Count == 0)
                {
                    Console.Error.WriteLine("the store is empty - nothing to work with");
                    return 1;
                }

                Console.WriteLine("  first      : height " + loaded2[0].BlockIndex + " " + loaded2[0].DisplayHash);
                Console.WriteLine("  last       : height " + loaded2[loaded2.Count - 1].BlockIndex
                                  + " " + loaded2[loaded2.Count - 1].DisplayHash);

                // The blocks were stored in chain order, so this is checking the store kept it -
                // every block's parent should be the one before it, with no gap in the heights.
                int brokenLinks = 0;
                for (int i = 1; i < loaded2.Count; i++)
                {
                    if (loaded2[i].GetPrevBlockHash() != loaded2[i - 1].DisplayHash)
                    {
                        brokenLinks++;
                        if (brokenLinks <= 5)
                        {
                            Console.Error.WriteLine("  break at height " + loaded2[i].BlockIndex
                                                    + ": parent is " + loaded2[i].GetPrevBlockHash()
                                                    + ", previous block is " + loaded2[i - 1].DisplayHash);
                        }
                    }
                }


                if (brokenLinks == 0)
                {
                    Console.WriteLine("  chain      : all " + (loaded2.Count - 1) + " links hold");
                }
                else
                {
                    Console.WriteLine("  chain      : " + brokenLinks + " broken links");
                }
            }

            // write code here to use the - to — txaddr direction 1, one row per output, address straight out of ScriptToAddress(output.ScriptPubKey) (MainBlockHex.cs:4372).
            // -from — direction 0, one row per non-coinbase input, address taken from the unspent entry the input's outpoint points at (MainBlockHex.cs:4347).
            // also include the mining rewards

            //  write code here to keep the balance of everywallet that sees transaction in the first 200,000 blocks, and write it to a csv file with columns: address, balance, first seen height, last seen height, number of transactions seen.  The balance is the sum of all outputs to that address minus the sum of all inputs from that address.  The first seen height is the height of the first block that contains a transaction that has an output to that address or an input from that address.  The last seen height is the height of the last block that contains a transaction that has an output to that address or an input from that address.  The number of transactions seen is the number of transactions that have an output to that address or an input from that address.
            if (false)
            {
                const int balanceHeights = 200000;

                // Beside the stores the blocks came out of, like everything else this file
                // writes.
                string balanceCsvPath = "C:\\btcblock\\rocksdb\\address_balances.csv";

                // Both sides of every transaction and the mining rewards with them - a
                // coinbase output is an output, so the 50 BTC a block pays is credited to the
                // miner by the same code that credits a payment. The reconciliation it prints
                // at the end is the thing to read first: with nothing unresolved, what was
                // mined less what was burnt as fees has to equal what the balances hold, and
                // if it does then no output went missing anywhere in the walk.
                var walletClock = Stopwatch.StartNew();
                List<AddressBalance> wallets = CollectAddressBalances(loadedBlocks, balanceHeights, balanceCsvPath);
                walletClock.Stop();

                // How much of that table is one payment that never moved again. Through this
                // era most of it is: an address was a single transaction's worth of coins and
                // then nothing.
                int untouchedSince = 0;
                foreach (AddressBalance held in wallets)
                {
                    if (held.Transactions == 1)
                    {
                        untouchedSince++;
                    }
                }

                Console.WriteLine("  one tx only  : " + untouchedSince + " addresses were touched exactly once");
                Console.WriteLine("  walked in    : " + walletClock.Elapsed.TotalSeconds.ToString("F1") + "s");

                // Two independent walks over the same chain, one off the blocks and one off
                // the collected transactions. They have to come to the same table, and where
                // they do not the differences say which of them to distrust.
                if (walletsFromList2 != null)
                {
                    CompareAddressBalances(walletsFromList2, "from list", wallets, "from blocks");
                }
            }


            if (false)
            {
                // generate a list of all the wallet addresses that get the 50 BTC block reward in
                // the first 200,000 blocks
                //
                // 200,000 is short of the first halving at 210,000, so every block in the range
                // still carries a 50 BTC subsidy and there is nothing to filter on but height.
                // If the stores hold fewer blocks than that the list simply covers what is there,
                // and the block count it prints says how many it actually read.
                {
                    const int rewardHeights = 200000;

                    // Beside the stores the blocks came out of. Every other absolute path in this
                    // file points into C:\btcblock, so this one does too rather than inventing a
                    // second place for output to live.
                    string rewardCsvPath = "C:\\btcblock\\rocksdb\\coinbase_addresses.csv";

                    var rewardClock = Stopwatch.StartNew();
                    List<CoinbaseReward> rewards = CollectCoinbaseAddresses(loadedBlocks, rewardHeights, rewardCsvPath);
                    rewardClock.Stop();

                    // The shape of early mining in one number: through this range most addresses
                    // are somebody who found a single block and was never seen again.
                    int oneBlockOnly = 0;
                    foreach (CoinbaseReward reward in rewards)
                    {
                        if (reward.Blocks == 1)
                        {
                            oneBlockOnly++;
                        }
                    }

                    Console.WriteLine("  one block    : " + oneBlockOnly + " addresses were paid exactly once");
                    Console.WriteLine("  read in      : " + rewardClock.Elapsed.TotalSeconds.ToString("F1") + "s");
                }
            }


            if (false)
            {

                // use sqllite to save the transactions with an index on from address and to address

                // save the transactions from the first 50,000 blocks into C:\\btcblock\\rocksdb\\transactions1
                // save the transactions from the second 50,000 blocks into C:\\btcblock\\rocksdb\\transactions2 and so on

                string txDbBase = "C:\\btcblock\\rocksdb\\transactions";

                // Cut the same way the block stores were: 50,000 blocks each, in order, so
                // transactions1.db covers the same heights as blocks1.
                //
                // ".db" on the end because a SQLite database is a file while a rocksdb store is a
                // directory, and everything else under this folder is the latter - a bare
                // "transactions1" would be a file sitting where this codebase expects a store.
                const int sqliteSegmentBlocks = 50000;

                // Carried across every segment, which is what makes the from-address work: an
                // input in segment 4 routinely spends an output made in segment 1, and only a set
                // that has been walking along since height 0 still knows who owned it. Outputs go
                // in and spends take them out, so this stays the size of the UTXO set at whatever
                // height the walk has reached, not a record of every output ever made.
                //
                // Keyed by a struct, so the entries live in the dictionary's own storage and the
                // set costs no per-outpoint allocation.
                var unspent = new Dictionary<OutPoint, UnspentOutput>();

                int sqliteStore = 0;
                long sqliteTotal = 0;
                var sqliteClock = Stopwatch.StartNew();

                for (int start = 0; start < loadedBlocks.Count; start += sqliteSegmentBlocks)
                {
                    sqliteStore++;
                    int take = Math.Min(sqliteSegmentBlocks, loadedBlocks.Count - start);
                    List<BlockRaw> segmentBlocks = loadedBlocks.GetRange(start, take);

                    string sqlitePath = txDbBase + sqliteStore + ".db";
                    Console.WriteLine("transactions " + sqliteStore + ": blocks " + segmentBlocks[0].BlockIndex
                                      + " to " + segmentBlocks[take - 1].BlockIndex + " -> " + sqlitePath);

                    var oneDbClock = Stopwatch.StartNew();
                    sqliteTotal += SaveTransactionsToSqlite(sqlitePath, segmentBlocks, unspent);
                    oneDbClock.Stop();

                    Console.WriteLine("  wrote in     : " + oneDbClock.Elapsed.TotalSeconds.ToString("F1") + "s");
                }

                sqliteClock.Stop();
                Console.WriteLine("indexed " + sqliteTotal + " transactions across " + sqliteStore
                                  + " databases in " + sqliteClock.Elapsed.TotalSeconds.ToString("F1") + "s");


            }

            return 0;
        }

    }
}
