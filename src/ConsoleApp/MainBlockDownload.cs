using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace main3
{
    /// <summary>
    /// Standalone Bitcoin P2P block downloader.
    ///
    /// Speaks the raw Bitcoin wire protocol to public mainnet nodes (or to a node you name with
    /// --node) and writes every block it receives straight into blk00000.dat / blk00001.dat / ...
    /// using Bitcoin Core's on-disk record layout:
    ///
    ///     [4 bytes magic F9BEB4D9][4 bytes block size, little endian][block bytes]
    ///
    /// The files are written UNOBFUSCATED - no XOR is applied, which is the same thing as Core's
    /// xor.dat holding eight zero bytes. An all-zero xor.dat is dropped into the output directory
    /// so the rest of this repo (which reads xor.dat and insists it is zeroed) is happy.
    ///
    /// Blocks are written in the order they arrive off the wire, NOT in height order - many peers
    /// are downloading different parts of the chain at once, which is what makes it fast. Sorting
    /// and fork-trimming is left to a later pass; every block written is also recorded in
    /// blocks.index (48 bytes per block: hash[32], fileNo[4], offset[8], size[4]) so that later
    /// pass can seek straight to a block instead of rescanning the .dat files.
    ///
    /// Scope note: the P2P protocol only lets you fetch blocks whose hashes you already know, and
    /// `getheaders` only ever walks a peer's *active* chain. So this downloads the main chain plus
    /// whatever stale blocks happen to be announced while it is running near the tip. Historical
    /// orphans are not discoverable this way - if you have their hashes from elsewhere, drop them
    /// in a text file (one display-order hash per line) and pass --extra to fetch them too.
    ///
    /// Usage:
    ///     dotnet run --project src/ConsoleApp -- --out C:\btcblock\raw
    ///     dotnet run --project src/ConsoleApp -- --out C:\btcblock\raw --stop 200000 --peers 16
    ///     dotnet run --project src/ConsoleApp -- --out C:\btcblock\raw --node 127.0.0.1:8333
    ///
    /// Ctrl+C is a clean stop; re-running the same command resumes where it left off.
    /// </summary>
    public class MainBlockDownload
    {
        // ------------------------------------------------------------------------------------
        // Options
        // ------------------------------------------------------------------------------------

        public sealed class Options
        {
            /// <summary>Directory the blk*.dat files are written to.</summary>
            public string OutputDirectory = "C:\\btcblock\\claudeblocks";

            /// <summary>How many peers to download from at once. This is the main speed dial.</summary>
            public int PeerCount = 12;

            /// <summary>Blocks requested from a single peer before waiting for replies.</summary>
            public int InFlightPerPeer = 16;

            /// <summary>First height to download (headers below this are still synced).</summary>
            public int StartHeight = 0;

            /// <summary>Last height to download. Default is the chain tip.</summary>
            public int StopHeight = int.MaxValue;

            /// <summary>Roll over to the next blk file past this size. Core uses 128 MiB.</summary>
            public long MaxBlockFileBytes = 0x8000000;

            /// <summary>Ask for MSG_WITNESS_BLOCK so post-segwit blocks match Core's serialization.</summary>
            public bool RequestWitness = true;

            /// <summary>Verify the 4-byte message checksum on every message.</summary>
            public bool VerifyChecksums = true;

            /// <summary>Explicit nodes to use instead of the DNS seeds.</summary>
            public List<IPEndPoint> FixedNodes = new List<IPEndPoint>();

            /// <summary>Extra block hashes to fetch (display order hex), e.g. known orphans.</summary>
            public List<string> ExtraBlockHashes = new List<string>();

            public TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

            /// <summary>Per-message deadline. A peer that goes quiet is dropped and its blocks requeued.</summary>
            public TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

            /// <summary>
            /// How many peers may fail on the same block before it is given up on. Stops a block
            /// no peer will serve from cycling the retry lane forever.
            /// </summary>
            public int MaxAttemptsPerBlock = 10;
        }

        // ------------------------------------------------------------------------------------
        // Protocol constants
        // ------------------------------------------------------------------------------------

        static readonly byte[] Magic = { 0xF9, 0xBE, 0xB4, 0xD9 };

        const int ProtocolVersion = 70016;
        const int DefaultPort = 8333;
        const int MaxMessageBytes = 32 * 1024 * 1024;
        const int HeadersPerMessage = 2000;
        const string UserAgent = "/SatoshiSharp:0.1/";

        const ulong NodeNetwork = 1;          // serves the full chain
        const ulong NodeWitness = 8;          // serves witness data

        const uint MsgBlock = 2;
        const uint MsgWitnessBlock = 2 | (1u << 30);

        static readonly string[] DnsSeeds =
        {
            "seed.bitcoin.sipa.be",
            "dnsseed.bluematt.me",
            "seed.bitcoinstats.com",
            "seed.bitcoin.jonasschnelli.org",
            "seed.btc.petertodd.net",
            "seed.bitcoin.sprovoost.nl",
            "dnsseed.emzy.de",
            "seed.bitcoin.wiz.biz",
            "seed.mainnet.achownodes.xyz",
        };

        // Genesis is hardcoded: peers will not always serve block 0, and seeding the header chain
        // with it avoids the ambiguity of sending getheaders with an empty locator.
        const string GenesisBlockHex =
            "0100000000000000000000000000000000000000000000000000000000000000000000003BA3EDFD7A7B12B27AC7" +
            "2C3E67768F617FC81BC3888A51323A9FB8AA4B1E5E4A29AB5F49FFFF001D1DAC2B7C0101000000010000000000000" +
            "000000000000000000000000000000000000000000000000000FFFFFFFF4D04FFFF001D0104455468652054696D65" +
            "732030332F4A616E2F32303039204368616E63656C6C6F72206F6E206272696E6B206F66207365636F6E642062616" +
            "96C6F757420666F722062616E6B73FFFFFFFF0100F2052A01000000434104678AFDB0FE5548271967F1A67130B710" +
            "5CD6A828E03909A67962E0EA1F61DEB649F6BC3F4CEF38C4F35504E51EC112DE5C384DF7BA0B8D578A4C702B6BF11" +
            "D5FAC00000000";

        const string GenesisHashDisplay = "000000000019d6689c085ae165831e934ff763ae46a2a6c172b3f1b60a8ce26f";

        // ------------------------------------------------------------------------------------
        // Instance state
        // ------------------------------------------------------------------------------------

        readonly Options _opt;
        readonly ulong _nonce;

        readonly ConcurrentQueue<byte[]> _work = new ConcurrentQueue<byte[]>();          // block hashes still to fetch

        /// <summary>
        /// Blocks handed back by a peer that died mid-batch. Workers drain this before _work, so a
        /// dropped batch is retried within seconds. Appending them to _work instead would put them
        /// behind every block not yet dispatched - a retry deferred until the end of the chain,
        /// which never arrives if the run is stopped early, and that is how gaps got left behind.
        /// </summary>
        readonly ConcurrentQueue<byte[]> _retry = new ConcurrentQueue<byte[]>();

        /// <summary>How many times each block has been handed back. Only failures land here.</summary>
        readonly ConcurrentDictionary<byte[], int> _attempts =
            new ConcurrentDictionary<byte[], int>(new ByteArrayComparer());

        /// <summary>Blocks given up on after too many attempts, named in the closing summary.</summary>
        readonly ConcurrentBag<byte[]> _abandoned = new ConcurrentBag<byte[]>();
        readonly ConcurrentQueue<IPEndPoint> _addrs = new ConcurrentQueue<IPEndPoint>(); // peers still to try
        readonly ConcurrentDictionary<string, byte> _knownAddrs = new ConcurrentDictionary<string, byte>();
        readonly BlockingCollection<KeyValuePair<byte[], byte[]>> _toWrite =
            new BlockingCollection<KeyValuePair<byte[], byte[]>>(boundedCapacity: 64);

        BlockStore _store = null!;
        long _remaining;
        long _retried;
        long _blocksWritten;
        long _bytesWritten;
        int _livePeers;

        public MainBlockDownload(Options options)
        {
            _opt = options;
            _nonce = (ulong)Environment.TickCount64 ^ ((ulong)Environment.ProcessId << 32);
        }

        // ------------------------------------------------------------------------------------
        // Entry point
        // ------------------------------------------------------------------------------------

        static async Task<int> Main3(string[] args)
        {
            var opt = new Options();
            try
            {
                if (!ParseArgs(args, opt))
                {
                    PrintUsage();
                    return 0;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("argument error: " + ex.Message);
                PrintUsage();
                return 2;
            }

            if (opt.OutputDirectory.Length == 0)
                opt.OutputDirectory = ResolveDefaultOutputDirectory();

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;                       // let the writer flush instead of dying
                Console.WriteLine("\nstopping - flushing to disk...");
                cts.Cancel();
            };

            try
            {
                await new MainBlockDownload(opt).RunAsync(cts.Token);
                return 0;
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("fatal: " + ex);
                return 1;
            }
        }

        static bool ParseArgs(string[] args, Options opt)
        {
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
                    case "--out": opt.OutputDirectory = Next("--out"); break;
                    case "--peers": opt.PeerCount = int.Parse(Next("--peers")); break;
                    case "--inflight": opt.InFlightPerPeer = int.Parse(Next("--inflight")); break;
                    case "--start": opt.StartHeight = int.Parse(Next("--start")); break;
                    case "--stop": opt.StopHeight = int.Parse(Next("--stop")); break;
                    case "--max-file-mb": opt.MaxBlockFileBytes = long.Parse(Next("--max-file-mb")) * 1024 * 1024; break;
                    case "--max-attempts": opt.MaxAttemptsPerBlock = int.Parse(Next("--max-attempts")); break;
                    case "--no-witness": opt.RequestWitness = false; break;
                    case "--no-checksum": opt.VerifyChecksums = false; break;
                    case "--node": opt.FixedNodes.Add(ParseEndPoint(Next("--node"))); break;
                    case "--extra": opt.ExtraBlockHashes.AddRange(ReadHashList(Next("--extra"))); break;
                    default: throw new ArgumentException("unknown option " + a);
                }
            }

            if (opt.PeerCount < 1) throw new ArgumentException("--peers must be >= 1");
            if (opt.MaxAttemptsPerBlock < 1) throw new ArgumentException("--max-attempts must be >= 1");
            if (opt.InFlightPerPeer < 1) throw new ArgumentException("--inflight must be >= 1");
            if (opt.StopHeight < opt.StartHeight) throw new ArgumentException("--stop is below --start");
            return true;
        }

        static void PrintUsage()
        {
            Console.WriteLine(@"
Bitcoin block downloader - writes un-XORed blk*.dat files.

  --out <dir>          output directory (default: BlockChainDataDirectory from settings.json)
  --peers <n>          concurrent peer connections            (default 12)
  --inflight <n>       blocks requested per peer at a time    (default 16)
  --start <height>     first height to download               (default 0)
  --stop <height>      last height to download                (default chain tip)
  --node <host:port>   use this node instead of the DNS seeds (repeatable)
  --extra <file>       text file of extra block hashes to fetch, one per line
  --max-attempts <n>   peers that may fail on one block before giving up (default 10)
  --max-file-mb <n>    blk file rollover size in MiB          (default 128)
  --no-witness         request stripped (non-witness) blocks
  --no-checksum        skip per-message checksum verification

The output directory must be empty or previously written by this tool - it will
refuse to touch a directory holding blk*.dat files it did not create.");
        }

        static IPEndPoint ParseEndPoint(string text)
        {
            int colon = text.LastIndexOf(':');
            if (colon > 0 && text.IndexOf(':') == colon)   // host:port, not a bare IPv6 literal
                return new IPEndPoint(ResolveOne(text.Substring(0, colon)), int.Parse(text.Substring(colon + 1)));
            return new IPEndPoint(ResolveOne(text), DefaultPort);
        }

        static IPAddress ResolveOne(string host)
        {
            if (IPAddress.TryParse(host, out var ip)) return ip;
            var found = Dns.GetHostAddresses(host);
            if (found.Length == 0) throw new ArgumentException("cannot resolve " + host);
            return found[0];
        }

        static List<string> ReadHashList(string path)
        {
            var list = new List<string>();
            foreach (var line in File.ReadAllLines(path))
            {
                string s = line.Trim();
                if (s.Length == 64) list.Add(s);
                else if (s.Length != 0 && !s.StartsWith("#")) Console.WriteLine("skipping malformed hash line: " + s);
            }
            return list;
        }

        /// <summary>
        /// Finds settings.json by walking up from the build output (rather than the brittle fixed
        /// parent count Program.cs uses) and takes BlockChainDataDirectory from it.
        /// </summary>
        static string ResolveDefaultOutputDirectory()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "settings.json");
                if (!File.Exists(candidate)) continue;

                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(candidate));
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (!prop.NameEquals("BlockChainDataDirectory")) continue;
                        string value = prop.Value.GetString() ?? "";
                        if (value.Length == 0) break;
                        if (Path.IsPathRooted(value)) return value;
                        return Path.Combine(dir.FullName, value);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("could not read " + candidate + ": " + ex.Message);
                }
                break;
            }
            return Path.Combine(Directory.GetCurrentDirectory(), "btcblockdata");
        }

        // ------------------------------------------------------------------------------------
        // Main flow
        // ------------------------------------------------------------------------------------

        public async Task RunAsync(CancellationToken ct)
        {
            Console.WriteLine("output directory : " + _opt.OutputDirectory);
            Console.WriteLine("peers            : " + _opt.PeerCount + " x " + _opt.InFlightPerPeer + " blocks in flight");
            string witness = "no (stripped)";
            if (_opt.RequestWitness) witness = "yes";
            Console.WriteLine("witness data     : " + witness);
            Console.WriteLine("xor              : none (xor.dat written as eight zero bytes)");

            Directory.CreateDirectory(_opt.OutputDirectory);

            // Constructed before anything is written: it refuses to run against a directory that
            // holds blk*.dat files it did not create, so we never scribble on a real Core datadir.
            _store = new BlockStore(_opt.OutputDirectory, _opt.MaxBlockFileBytes);
            Console.WriteLine("already on disk  : " + _store.Have.Count + " blocks");

            File.WriteAllBytes(Path.Combine(_opt.OutputDirectory, "xor.dat"), new byte[8]);

            using var failFast = CancellationTokenSource.CreateLinkedTokenSource(ct);
            CancellationToken token = failFast.Token;

            SeedAddresses();

            // 1. Header sync - gives us every block hash in height order.
            var chain = new HeaderChain(Path.Combine(_opt.OutputDirectory, "headers.dat"));
            SeedGenesis(chain);
            Console.WriteLine("cached headers   : " + (chain.Tip + 1));
            await SyncHeadersAsync(chain, token);
            Console.WriteLine("header tip       : " + chain.Tip + " " + ToDisplayHex(chain.TipHash));

            // 2. Build the work list. Order here only decides what gets *asked for* first; blocks
            //    land in the .dat files in whatever order the peers answer.
            int stop = Math.Min(_opt.StopHeight, chain.Tip);
            var genesisBlock = HexToBytes(GenesisBlockHex);
            var genesisHash = DoubleSha256(genesisBlock, 0, 80);

            for (int h = Math.Max(0, _opt.StartHeight); h <= stop; h++)
            {
                byte[] hash = chain.Hashes[h];
                if (_store.Have.Contains(hash)) continue;
                if (h == 0) { _store.Write(genesisHash, genesisBlock); continue; }   // peers do not reliably serve genesis
                _work.Enqueue(hash);
            }
            foreach (string display in _opt.ExtraBlockHashes)
            {
                byte[] hash = ReverseCopy(HexToBytes(display));
                if (!_store.Have.Contains(hash)) _work.Enqueue(hash);
            }

            _remaining = _work.Count;
            Console.WriteLine("blocks to fetch  : " + _remaining);
            if (_remaining == 0)
            {
                _store.Dispose();
                Console.WriteLine("nothing to do");
                return;
            }

            // 3. Writer thread owns the files; peers only hand it finished blocks.
            var writer = Task.Factory.StartNew(() => WriterLoop(failFast), TaskCreationOptions.LongRunning);
            var progress = ReportProgressAsync(token);

            var workers = new Task[_opt.PeerCount];
            for (int i = 0; i < workers.Length; i++)
            {
                int id = i;
                workers[i] = Task.Run(() => DownloadWorkerAsync(id, token));
            }

            try
            {
                await Task.WhenAll(workers);
            }
            finally
            {
                _toWrite.CompleteAdding();
                await writer;
                _store.Dispose();
            }

            await progress;

            Console.WriteLine();
            Console.WriteLine("wrote " + _blocksWritten + " blocks, " + (_bytesWritten / 1024.0 / 1024.0).ToString("F1") + " MB");
            Console.WriteLine("retried " + Interlocked.Read(ref _retried) + " blocks off the retry lane");
            Console.WriteLine("index: " + Path.Combine(_opt.OutputDirectory, "blocks.index"));

            // Do not report a clean finish while blocks are still queued. Silence here is exactly
            // how a run with holes in it used to look like a run that worked.
            int unfetched = _work.Count + _retry.Count;
            if (unfetched > 0)
            {
                Console.WriteLine();
                Console.WriteLine("INCOMPLETE: " + unfetched + " blocks were never fetched ("
                                  + _retry.Count + " of them waiting on a retry)");
                Console.WriteLine("re-run the same command to pick them up - they queue ahead of everything else");
            }

            if (!_abandoned.IsEmpty)
            {
                Console.WriteLine();
                Console.WriteLine("gave up on " + _abandoned.Count + " blocks after "
                                  + _opt.MaxAttemptsPerBlock + " attempts each:");
                foreach (byte[] hash in _abandoned) Console.WriteLine("  " + ToDisplayHex(hash));
            }
        }

        void SeedGenesis(HeaderChain chain)
        {
            if (chain.Tip >= 0) return;
            byte[] genesisHeader = HexToBytes(GenesisBlockHex).AsSpan(0, 80).ToArray();
            byte[] hash = DoubleSha256(genesisHeader, 0, 80);
            if (ToDisplayHex(hash) != GenesisHashDisplay)
                throw new InvalidDataException("hardcoded genesis header does not hash to the genesis block hash");
            chain.Append(genesisHeader, hash);
        }

        void SeedAddresses()
        {
            if (_opt.FixedNodes.Count > 0)
            {
                foreach (var ep in _opt.FixedNodes) AddAddress(ep);
                Console.WriteLine("using " + _opt.FixedNodes.Count + " fixed node(s)");
                return;
            }

            int before = _knownAddrs.Count;
            foreach (string seed in DnsSeeds)
            {
                try
                {
                    foreach (var ip in Dns.GetHostAddresses(seed))
                        AddAddress(new IPEndPoint(ip, DefaultPort));
                }
                catch (Exception ex)
                {
                    Console.WriteLine("dns seed " + seed + " failed: " + ex.Message);
                }
            }

            int count = _knownAddrs.Count - before;
            Console.WriteLine("dns seeds        : " + count + " peer addresses");
            if (count == 0)
                throw new InvalidOperationException("no peers found - check your network, or pass --node host:port");
        }

        void AddAddress(IPEndPoint ep)
        {
            if (_knownAddrs.TryAdd(ep.ToString(), 0)) _addrs.Enqueue(ep);
        }

        // ------------------------------------------------------------------------------------
        // Header sync
        // ------------------------------------------------------------------------------------

        async Task SyncHeadersAsync(HeaderChain chain, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();

            while (!ct.IsCancellationRequested && chain.Tip < _opt.StopHeight)
            {
                Peer? peer = await ConnectAsync(requireWitness: false, ct);
                if (peer == null)
                {
                    await Task.Delay(1000, ct);
                    continue;
                }

                try
                {
                    using (peer)
                    {
                        while (!ct.IsCancellationRequested && chain.Tip < _opt.StopHeight)
                        {
                            await peer.SendAsync("getheaders", BuildGetHeaders(chain), ct);

                            byte[] payload;
                            while (true)
                            {
                                var msg = await peer.ReceiveAsync(ct);
                                if (await HandleCommonAsync(peer, msg.Command, msg.Payload, ct)) continue;
                                if (msg.Command == "headers") { payload = msg.Payload; break; }
                            }

                            int added = ApplyHeaders(chain, payload);
                            if (added == 0) { chain.Flush(); return; }          // peer has nothing past our tip

                            if (chain.Tip % 50000 < added)
                                Console.WriteLine("  headers " + chain.Tip + " (" + sw.Elapsed.TotalSeconds.ToString("F0") + "s)");
                        }
                        chain.Flush();
                        return;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    chain.Flush();
                    Console.WriteLine("  header peer " + peer.Endpoint + " failed: " + ex.Message + " - retrying");
                }
            }
            chain.Flush();
        }

        byte[] BuildGetHeaders(HeaderChain chain)
        {
            var locator = chain.BuildLocator();
            var ms = new MemoryStream();
            WriteUInt32LE(ms, ProtocolVersion);
            WriteVarInt(ms, (ulong)locator.Count);
            foreach (var h in locator) ms.Write(h, 0, 32);
            ms.Write(new byte[32], 0, 32);          // hash_stop = 0 -> give me as many as you can
            return ms.ToArray();
        }

        static int ApplyHeaders(HeaderChain chain, byte[] payload)
        {
            int pos = 0;
            ulong count = ReadVarInt(payload, ref pos);
            if (count > HeadersPerMessage) throw new InvalidDataException("peer sent " + count + " headers");

            int added = 0;
            for (ulong i = 0; i < count; i++)
            {
                if (pos + 80 > payload.Length) throw new InvalidDataException("truncated headers message");
                byte[] raw = new byte[80];
                Buffer.BlockCopy(payload, pos, raw, 0, 80);
                pos += 80;
                ReadVarInt(payload, ref pos);       // tx count, always 0 in a headers message

                byte[] hash = DoubleSha256(raw, 0, 80);
                if (chain.Contains(hash)) continue;

                byte[] prev = new byte[32];
                Buffer.BlockCopy(raw, 4, prev, 0, 32);

                if (!ByteArrayComparer.Equal(chain.TipHash, prev))
                {
                    // The peer is answering from a fork point; rewind to it if we know it. If we
                    // have never heard of the parent the peer is off in the weeds - drop it and
                    // let the caller pick another one rather than silently stopping at this tip.
                    if (!chain.TryGetHeight(prev, out int forkHeight))
                        throw new InvalidDataException("peer sent a header that does not connect to our chain");
                    chain.TruncateTo(forkHeight);
                }

                chain.Append(raw, hash);
                added++;
            }
            return added;
        }

        // ------------------------------------------------------------------------------------
        // Block download
        // ------------------------------------------------------------------------------------

        async Task DownloadWorkerAsync(int id, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && Interlocked.Read(ref _remaining) > 0)
            {
                Peer? peer = null;
                var inFlight = new List<byte[]>();

                try
                {
                    peer = await ConnectAsync(_opt.RequestWitness, ct);
                    if (peer == null)
                    {
                        await Task.Delay(1000, ct);
                        continue;
                    }

                    Interlocked.Increment(ref _livePeers);
                    try
                    {
                        int idleTicks = 0;
                        while (!ct.IsCancellationRequested)
                        {
                            // Retries first: those blocks have already cost one peer's worth of
                            // waiting, and every other block in _work is still untried.
                            var fresh = new List<byte[]>();
                            while (inFlight.Count + fresh.Count < _opt.InFlightPerPeer)
                            {
                                byte[]? next;
                                if (!_retry.TryDequeue(out next) && !_work.TryDequeue(out next)) break;
                                fresh.Add(next);
                            }

                            if (fresh.Count > 0)
                            {
                                await peer.SendAsync("getdata", BuildGetData(fresh), ct);
                                inFlight.AddRange(fresh);
                            }

                            if (inFlight.Count == 0)
                            {
                                if (Interlocked.Read(ref _remaining) <= 0) break;      // everything is downloaded

                                // The queue is momentarily empty but another peer may still time
                                // out and hand its blocks back. Hold the connection open and wait
                                // rather than churning through a fresh TCP handshake every 250ms.
                                if (++idleTicks > 100) break;                          // ~20s idle, let it go
                                await Task.Delay(200, ct);
                                continue;
                            }
                            idleTicks = 0;

                            var msg = await peer.ReceiveAsync(ct);
                            if (await HandleCommonAsync(peer, msg.Command, msg.Payload, ct)) continue;

                            if (msg.Command == "block")
                            {
                                if (msg.Payload.Length < 81) throw new InvalidDataException("runt block message");
                                byte[] hash = DoubleSha256(msg.Payload, 0, 80);

                                int at = inFlight.FindIndex(h => ByteArrayComparer.Equal(h, hash));
                                if (at < 0) continue;          // not one of ours - ignore it
                                inFlight.RemoveAt(at);

                                // Blocks when the writer is behind, which is the backpressure that
                                // keeps memory bounded no matter how many peers are running.
                                _toWrite.Add(new KeyValuePair<byte[], byte[]>(hash, msg.Payload), ct);
                            }
                            else if (msg.Command == "notfound")
                            {
                                throw new InvalidDataException("peer does not have a block we asked for");
                            }
                        }
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _livePeers);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("peer " + id + ": " + ex.Message);
                }
                finally
                {
                    peer?.Dispose();
                    foreach (var h in inFlight) Requeue(h);         // hand unfinished work back
                }

                if (_work.IsEmpty && _retry.IsEmpty && Interlocked.Read(ref _remaining) > 0)
                    await Task.Delay(250, ct);                      // someone else is still finishing
            }
        }

        /// <summary>
        /// Puts a block back in the queue after the peer holding it went away. It goes on the
        /// retry lane so the next free peer picks it up immediately.
        ///
        /// A block nobody will serve - a bad --extra hash, say - would otherwise bounce around the
        /// lane forever, killing a peer each time and leaving _remaining stuck above zero so the
        /// run never ends. After MaxAttemptsPerBlock tries it is given up on instead, and the
        /// closing summary names it.
        /// </summary>
        void Requeue(byte[] hash)
        {
            int attempts = _attempts.AddOrUpdate(hash, 1, (_, previous) => previous + 1);

            if (attempts >= _opt.MaxAttemptsPerBlock)
            {
                _abandoned.Add(hash);
                Interlocked.Decrement(ref _remaining);      // or the workers would never exit
                Console.WriteLine("giving up on " + ToDisplayHex(hash) + " after " + attempts + " attempts");
                return;
            }

            Interlocked.Increment(ref _retried);
            _retry.Enqueue(hash);
        }

        byte[] BuildGetData(List<byte[]> hashes)
        {
            uint type = MsgBlock;
            if (_opt.RequestWitness) type = MsgWitnessBlock;
            var ms = new MemoryStream();
            WriteVarInt(ms, (ulong)hashes.Count);
            foreach (var h in hashes)
            {
                WriteUInt32LE(ms, type);
                ms.Write(h, 0, 32);
            }
            return ms.ToArray();
        }

        void WriterLoop(CancellationTokenSource failFast)
        {
            try
            {
                foreach (var item in _toWrite.GetConsumingEnumerable(failFast.Token))
                {
                    _store.Write(item.Key, item.Value);
                    Interlocked.Increment(ref _blocksWritten);
                    Interlocked.Add(ref _bytesWritten, item.Value.Length);
                    Interlocked.Decrement(ref _remaining);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // Disk full, permissions, whatever - stop the peers instead of letting them block
                // forever on a queue nobody is draining.
                Console.WriteLine("write failed: " + ex.Message);
                failFast.Cancel();
            }
        }

        async Task ReportProgressAsync(CancellationToken ct)
        {
            long lastBytes = 0, lastBlocks = 0;
            var sw = Stopwatch.StartNew();

            while (!ct.IsCancellationRequested && Interlocked.Read(ref _remaining) > 0)
            {
                try { await Task.Delay(2000, ct); }
                catch (OperationCanceledException) { break; }

                long bytes = Interlocked.Read(ref _bytesWritten);
                long blocks = Interlocked.Read(ref _blocksWritten);
                double secs = sw.Elapsed.TotalSeconds;
                sw.Restart();

                double mbPerSec = (bytes - lastBytes) / 1024.0 / 1024.0 / secs;
                double blocksPerSec = (blocks - lastBlocks) / secs;
                lastBytes = bytes;
                lastBlocks = blocks;

                string eta = "--:--:--";
                if (blocksPerSec > 0.01)
                {
                    eta = TimeSpan.FromSeconds(Interlocked.Read(ref _remaining) / blocksPerSec).ToString(@"hh\:mm\:ss");
                }

                string retryLane = "";
                if (!_retry.IsEmpty) retryLane = "  " + _retry.Count + " retrying";

                Console.WriteLine(blocks + " blocks  " + (bytes / 1024.0 / 1024.0 / 1024.0).ToString("F2") + " GB  "
                                  + mbPerSec.ToString("F1") + " MB/s  " + blocksPerSec.ToString("F0") + " blk/s  "
                                  + _livePeers + " peers  " + Interlocked.Read(ref _remaining) + " left" + retryLane
                                  + "  eta " + eta);
            }
        }

        // ------------------------------------------------------------------------------------
        // Peer connection management
        // ------------------------------------------------------------------------------------

        async Task<Peer?> ConnectAsync(bool requireWitness, CancellationToken ct)
        {
            for (int attempt = 0; attempt < 40 && !ct.IsCancellationRequested; attempt++)
            {
                if (!_addrs.TryDequeue(out var ep))
                {
                    // Only fixed nodes were given, or we burned through the seeds: recycle them.
                    if (_opt.FixedNodes.Count > 0)
                    {
                        foreach (var fixedEp in _opt.FixedNodes) _addrs.Enqueue(fixedEp);
                        await Task.Delay(1000, ct);
                        continue;
                    }
                    return null;
                }

                Peer? peer = null;
                try
                {
                    peer = await Peer.ConnectAsync(ep, _opt, _nonce, ct);
                    await peer.HandshakeAsync(ct);

                    if ((peer.Services & NodeNetwork) == 0)
                        throw new InvalidOperationException("peer is not a full node");
                    if (requireWitness && (peer.Services & NodeWitness) == 0)
                        throw new InvalidOperationException("peer does not serve witness data");

                    await peer.SendAsync("getaddr", Array.Empty<byte>(), ct);   // keep the address pool topped up
                    _addrs.Enqueue(ep);                                          // a good peer is worth reusing
                    return peer;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    peer?.Dispose();
                    throw;
                }
                catch (Exception ex)
                {
                    peer?.Dispose();
                    Debug.WriteLine("connect " + ep + " failed: " + ex.Message);
                }
            }
            return null;
        }

        /// <summary>
        /// Handles the housekeeping messages every peer sends. Returns true when the message was
        /// dealt with and the caller should just read the next one.
        /// </summary>
        async Task<bool> HandleCommonAsync(Peer peer, string command, byte[] payload, CancellationToken ct)
        {
            switch (command)
            {
                case "ping":
                    await peer.SendAsync("pong", payload, ct);   // echo the nonce back or we get dropped
                    return true;

                case "addr":
                    try
                    {
                        int pos = 0;
                        ulong count = ReadVarInt(payload, ref pos);
                        for (ulong i = 0; i < count && pos + 30 <= payload.Length; i++)
                        {
                            pos += 4;                                            // timestamp
                            ulong services = BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(pos, 8));
                            pos += 8;
                            var ipBytes = payload.AsSpan(pos, 16).ToArray();
                            pos += 16;
                            int port = (payload[pos] << 8) | payload[pos + 1];
                            pos += 2;

                            if ((services & NodeNetwork) == 0 || port == 0) continue;
                            AddAddress(new IPEndPoint(new IPAddress(ipBytes), port));
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("bad addr message: " + ex.Message);
                    }
                    return true;

                // Nothing here changes what we do, but peers send them constantly.
                case "inv":
                case "addrv2":
                case "sendaddrv2":
                case "sendheaders":
                case "sendcmpct":
                case "cmpctblock":
                case "feefilter":
                case "wtxidrelay":
                case "pong":
                case "tx":
                case "getheaders":
                case "getdata":
                case "alert":
                    return true;

                default:
                    return false;
            }
        }

        // ------------------------------------------------------------------------------------
        // Peer - one TCP connection speaking the Bitcoin wire protocol
        // ------------------------------------------------------------------------------------

        sealed class Peer : IDisposable
        {
            readonly Socket _socket;
            readonly NetworkStream _stream;
            readonly Options _opt;
            readonly ulong _nonce;
            readonly byte[] _headerBuf = new byte[24];

            public IPEndPoint Endpoint { get; }
            public ulong Services { get; private set; }
            public string PeerUserAgent { get; private set; } = "";

            Peer(Socket socket, IPEndPoint ep, Options opt, ulong nonce)
            {
                _socket = socket;
                _stream = new NetworkStream(socket, ownsSocket: false);
                _opt = opt;
                _nonce = nonce;
                Endpoint = ep;
            }

            public static async Task<Peer> ConnectAsync(IPEndPoint ep, Options opt, ulong nonce, CancellationToken ct)
            {
                var socket = new Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(opt.ConnectTimeout);
                    await socket.ConnectAsync(ep, timeout.Token);
                    return new Peer(socket, ep, opt, nonce);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }

            public async Task HandshakeAsync(CancellationToken ct)
            {
                await SendAsync("version", BuildVersionPayload(Endpoint, _nonce), ct);

                bool gotVersion = false, gotVerack = false;
                while (!gotVersion || !gotVerack)
                {
                    var msg = await ReceiveAsync(ct);
                    switch (msg.Command)
                    {
                        case "version":
                            ParseVersion(msg.Payload);
                            await SendAsync("verack", Array.Empty<byte>(), ct);
                            gotVersion = true;
                            break;
                        case "verack":
                            gotVerack = true;
                            break;
                        case "ping":
                            await SendAsync("pong", msg.Payload, ct);
                            break;
                        case "reject":
                            throw new InvalidOperationException("peer rejected the handshake");
                    }
                }
            }

            void ParseVersion(byte[] payload)
            {
                if (payload.Length < 80) throw new InvalidDataException("short version message");
                Services = BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(4, 8));

                int pos = 4 + 8 + 8 + 26 + 26 + 8;                 // version..nonce
                if (pos >= payload.Length) return;                 // no user agent, nothing else we need
                ulong len = ReadVarInt(payload, ref pos);
                if (len <= 256 && pos + (int)len <= payload.Length)
                    PeerUserAgent = Encoding.ASCII.GetString(payload, pos, (int)len);
            }

            public async Task SendAsync(string command, byte[] payload, CancellationToken ct)
            {
                byte[] frame = new byte[24 + payload.Length];
                Magic.CopyTo(frame, 0);
                Encoding.ASCII.GetBytes(command).CopyTo(frame, 4);   // rest of the 12 bytes stays zero
                BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(16, 4), (uint)payload.Length);
                DoubleSha256(payload, 0, payload.Length).AsSpan(0, 4).CopyTo(frame.AsSpan(20, 4));
                payload.CopyTo(frame, 24);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_opt.ReadTimeout);
                await _stream.WriteAsync(frame, timeout.Token);
            }

            public async Task<(string Command, byte[] Payload)> ReceiveAsync(CancellationToken ct)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_opt.ReadTimeout);

                await _stream.ReadExactlyAsync(_headerBuf, 0, 24, timeout.Token);

                for (int i = 0; i < 4; i++)
                    if (_headerBuf[i] != Magic[i]) throw new InvalidDataException("bad magic from " + Endpoint);

                string command = Encoding.ASCII.GetString(_headerBuf, 4, 12).TrimEnd('\0');
                uint length = BinaryPrimitives.ReadUInt32LittleEndian(_headerBuf.AsSpan(16, 4));
                if (length > MaxMessageBytes) throw new InvalidDataException(command + " message is " + length + " bytes");

                byte[] payload = Array.Empty<byte>();
                if (length > 0)
                {
                    payload = new byte[length];
                    await _stream.ReadExactlyAsync(payload, 0, (int)length, timeout.Token);
                }

                if (_opt.VerifyChecksums)
                {
                    byte[] sum = DoubleSha256(payload, 0, payload.Length);
                    for (int i = 0; i < 4; i++)
                        if (sum[i] != _headerBuf[20 + i]) throw new InvalidDataException("checksum mismatch on " + command);
                }

                return (command, payload);
            }

            static byte[] BuildVersionPayload(IPEndPoint remote, ulong nonce)
            {
                var ms = new MemoryStream();
                WriteUInt32LE(ms, ProtocolVersion);
                WriteUInt64LE(ms, 0);                                            // we serve nothing
                WriteUInt64LE(ms, (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                WriteNetAddr(ms, remote, NodeNetwork);                           // addr_recv
                WriteNetAddr(ms, null, 0);                                       // addr_from
                WriteUInt64LE(ms, nonce);
                WriteVarString(ms, UserAgent);
                WriteUInt32LE(ms, 0);                                            // start_height
                ms.WriteByte(0);                                                 // relay=false: no loose transactions
                return ms.ToArray();
            }

            static void WriteNetAddr(Stream s, IPEndPoint? ep, ulong services)
            {
                WriteUInt64LE(s, services);
                byte[] ip = new byte[16];
                if (ep != null)
                {
                    byte[] raw = ep.Address.GetAddressBytes();
                    if (raw.Length == 4) { ip[10] = 0xFF; ip[11] = 0xFF; raw.CopyTo(ip, 12); }   // IPv4-mapped
                    else raw.CopyTo(ip, 0);
                }
                s.Write(ip, 0, 16);
                int port = ep?.Port ?? 0;
                s.WriteByte((byte)(port >> 8));                                  // port is big endian
                s.WriteByte((byte)port);
            }

            public void Dispose()
            {
                try { _stream.Dispose(); } catch { }
                try { _socket.Dispose(); } catch { }
            }
        }

        // ------------------------------------------------------------------------------------
        // Header chain - hashes in height order, backed by a headers.dat cache
        // ------------------------------------------------------------------------------------

        sealed class HeaderChain
        {
            public readonly List<byte[]> Hashes = new List<byte[]>();
            readonly Dictionary<byte[], int> _heightByHash = new Dictionary<byte[], int>(new ByteArrayComparer());
            readonly FileStream _cache;

            public HeaderChain(string cachePath)
            {
                _cache = new FileStream(cachePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1 << 20);

                long usable = _cache.Length - (_cache.Length % 80);
                byte[] buf = new byte[80];
                _cache.Position = 0;
                for (long off = 0; off < usable; off += 80)
                {
                    _cache.ReadExactly(buf, 0, 80);
                    byte[] raw = (byte[])buf.Clone();
                    byte[] hash = DoubleSha256(raw, 0, 80);

                    if (Hashes.Count > 0)
                    {
                        byte[] prev = new byte[32];
                        Buffer.BlockCopy(raw, 4, prev, 0, 32);
                        if (!ByteArrayComparer.Equal(TipHash, prev))
                        {
                            Console.WriteLine("headers.dat breaks its chain at height " + Hashes.Count + " - truncating there");
                            break;
                        }
                    }
                    AddInternal(hash);
                }

                _cache.SetLength((long)Hashes.Count * 80);
                _cache.Position = _cache.Length;
            }

            public int Tip => Hashes.Count - 1;
            public byte[] TipHash => Hashes[Hashes.Count - 1];
            public bool Contains(byte[] hash) => _heightByHash.ContainsKey(hash);
            public bool TryGetHeight(byte[] hash, out int height) => _heightByHash.TryGetValue(hash, out height);

            void AddInternal(byte[] hash)
            {
                _heightByHash[hash] = Hashes.Count;
                Hashes.Add(hash);
            }

            public void Append(byte[] raw80, byte[] hash)
            {
                AddInternal(hash);
                _cache.Write(raw80, 0, 80);
            }

            public void TruncateTo(int height)
            {
                for (int h = Tip; h > height; h--) _heightByHash.Remove(Hashes[h]);
                Hashes.RemoveRange(height + 1, Tip - height);
                _cache.Flush();
                _cache.SetLength((long)Hashes.Count * 80);
                _cache.Position = _cache.Length;
            }

            /// <summary>Dense near the tip, exponentially sparse going back - the standard locator.</summary>
            public List<byte[]> BuildLocator()
            {
                var locator = new List<byte[]>();
                int step = 1;
                for (int h = Tip; h >= 0; h -= step)
                {
                    locator.Add(Hashes[h]);
                    if (locator.Count > 10) step *= 2;
                }
                if (!ByteArrayComparer.Equal(locator[locator.Count - 1], Hashes[0])) locator.Add(Hashes[0]);
                return locator;
            }

            public void Flush() => _cache.Flush();
        }

        // ------------------------------------------------------------------------------------
        // Block store - the blk*.dat files plus a hash -> (file, offset) index
        // ------------------------------------------------------------------------------------

        sealed class BlockStore : IDisposable
        {
            const int IndexRecordBytes = 48;      // hash[32] fileNo[4] offset[8] size[4]

            readonly string _dir;
            readonly long _maxFileBytes;
            readonly FileStream _index;
            FileStream _blk = null!;
            int _fileNo;
            long _offset;
            int _sinceFlush;

            public HashSet<byte[]> Have { get; } = new HashSet<byte[]>(new ByteArrayComparer());

            public BlockStore(string dir, long maxFileBytes)
            {
                _dir = dir;
                _maxFileBytes = maxFileBytes;

                string indexPath = Path.Combine(dir, "blocks.index");
                bool hasIndex = File.Exists(indexPath);
                bool hasBlkFiles = Directory.GetFiles(dir, "blk*.dat").Length > 0;

                if (hasBlkFiles && !hasIndex)
                {
                    throw new InvalidOperationException(
                        "'" + dir + "' already holds blk*.dat files but no blocks.index, so it was not written by " +
                        "this downloader (a Bitcoin Core datadir, perhaps). Refusing to touch it - pick an empty " +
                        "directory with --out.");
                }

                _index = new FileStream(indexPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1 << 16);
                LoadIndex();
                OpenCurrentFile();
            }

            /// <summary>
            /// Reads the index, dropping any trailing records whose block data did not make it to
            /// disk, then trims the .dat files back to exactly what the index accounts for.
            /// </summary>
            void LoadIndex()
            {
                long usable = _index.Length - (_index.Length % IndexRecordBytes);
                var records = new List<(byte[] Hash, int FileNo, long End)>((int)(usable / IndexRecordBytes));
                var fileLengths = new Dictionary<int, long>();

                byte[] rec = new byte[IndexRecordBytes];
                _index.Position = 0;
                for (long off = 0; off < usable; off += IndexRecordBytes)
                {
                    _index.ReadExactly(rec, 0, IndexRecordBytes);
                    byte[] hash = rec.AsSpan(0, 32).ToArray();
                    int fileNo = (int)BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(32, 4));
                    long offset = BinaryPrimitives.ReadInt64LittleEndian(rec.AsSpan(36, 8));
                    uint size = BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(44, 4));
                    records.Add((hash, fileNo, offset + 8 + size));
                }

                long ActualLength(int fileNo)
                {
                    if (!fileLengths.TryGetValue(fileNo, out long len))
                    {
                        var fi = new FileInfo(FilePath(fileNo));
                        len = 0;
                        if (fi.Exists) len = fi.Length;
                        fileLengths[fileNo] = len;
                    }
                    return len;
                }

                // A crash can leave index entries pointing past the end of the data. Drop those.
                while (records.Count > 0 && ActualLength(records[records.Count - 1].FileNo) < records[records.Count - 1].End)
                {
                    Console.WriteLine("dropping index entry with no block data: " + ToDisplayHex(records[records.Count - 1].Hash));
                    records.RemoveAt(records.Count - 1);
                }

                foreach (var r in records) Have.Add(r.Hash);

                _index.SetLength((long)records.Count * IndexRecordBytes);
                _index.Position = _index.Length;

                if (records.Count == 0)
                {
                    _fileNo = 0;
                    _offset = 0;
                }
                else
                {
                    var last = records[records.Count - 1];
                    _fileNo = last.FileNo;
                    _offset = last.End;
                }

                // Anything on disk past the last indexed byte was never accounted for - cut it off,
                // otherwise the .dat files would end up with a torn record in the middle.
                TrimDataFiles();
            }

            void TrimDataFiles()
            {
                string current = FilePath(_fileNo);
                if (File.Exists(current))
                {
                    using var fs = new FileStream(current, FileMode.Open, FileAccess.Write);
                    if (fs.Length > _offset)
                    {
                        Console.WriteLine("trimming " + Path.GetFileName(current) + " from " + fs.Length + " to " + _offset + " bytes");
                        fs.SetLength(_offset);
                    }
                }

                for (int n = _fileNo + 1; File.Exists(FilePath(n)); n++)
                {
                    Console.WriteLine("removing unindexed " + Path.GetFileName(FilePath(n)));
                    File.Delete(FilePath(n));
                }
            }

            string FilePath(int fileNo) => Path.Combine(_dir, "blk" + fileNo.ToString("D5") + ".dat");

            void OpenCurrentFile()
            {
                _blk = new FileStream(FilePath(_fileNo), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 1 << 20);
                _blk.Position = _offset;
            }

            public void Write(byte[] hash, byte[] block)
            {
                if (!Have.Add(hash)) return;                    // already have it

                if (_offset > 0 && _offset + 8 + block.Length > _maxFileBytes)
                {
                    _blk.Flush();
                    _blk.Dispose();
                    _fileNo++;
                    _offset = 0;
                    OpenCurrentFile();
                }

                Span<byte> recordHeader = stackalloc byte[8];
                Magic.CopyTo(recordHeader);
                BinaryPrimitives.WriteUInt32LittleEndian(recordHeader.Slice(4, 4), (uint)block.Length);
                _blk.Write(recordHeader);
                _blk.Write(block, 0, block.Length);

                Span<byte> rec = stackalloc byte[IndexRecordBytes];
                hash.CopyTo(rec);
                BinaryPrimitives.WriteUInt32LittleEndian(rec.Slice(32, 4), (uint)_fileNo);
                BinaryPrimitives.WriteInt64LittleEndian(rec.Slice(36, 8), _offset);
                BinaryPrimitives.WriteUInt32LittleEndian(rec.Slice(44, 4), (uint)block.Length);
                _index.Write(rec);

                _offset += 8 + block.Length;

                if (++_sinceFlush >= 200)
                {
                    _sinceFlush = 0;
                    _blk.Flush();            // data first, so the index never outruns it
                    _index.Flush();
                }
            }

            public void Dispose()
            {
                try { _blk.Flush(); _blk.Dispose(); } catch { }
                try { _index.Flush(); _index.Dispose(); } catch { }
            }
        }

        // ------------------------------------------------------------------------------------
        // Small helpers - kept local so this file stands on its own
        // ------------------------------------------------------------------------------------

        sealed class ByteArrayComparer : IEqualityComparer<byte[]>
        {
            public bool Equals(byte[]? x, byte[]? y) => Equal(x, y);

            public int GetHashCode(byte[] obj)
            {
                if (obj.Length >= 4) return BinaryPrimitives.ReadInt32LittleEndian(obj.AsSpan(0, 4));
                return obj.Length;
            }

            public static bool Equal(byte[]? a, byte[]? b)
            {
                if (ReferenceEquals(a, b)) return true;
                if (a == null || b == null) return false;
                return a.AsSpan().SequenceEqual(b);
            }
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

        static byte[] HexToBytes(string hex) => Convert.FromHexString(hex);

        static void WriteUInt32LE(Stream s, uint value)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, value);
            s.Write(b);
        }

        static void WriteUInt64LE(Stream s, ulong value)
        {
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(b, value);
            s.Write(b);
        }

        static void WriteVarInt(Stream s, ulong value)
        {
            if (value < 0xFD)
            {
                s.WriteByte((byte)value);
            }
            else if (value <= 0xFFFF)
            {
                Span<byte> b = stackalloc byte[2];
                BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)value);
                s.WriteByte(0xFD);
                s.Write(b);
            }
            else if (value <= 0xFFFFFFFF)
            {
                s.WriteByte(0xFE);
                WriteUInt32LE(s, (uint)value);
            }
            else
            {
                s.WriteByte(0xFF);
                WriteUInt64LE(s, value);
            }
        }

        static void WriteVarString(Stream s, string text)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            WriteVarInt(s, (ulong)bytes.Length);
            s.Write(bytes, 0, bytes.Length);
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
