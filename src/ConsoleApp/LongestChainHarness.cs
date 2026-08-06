using SatoshiSharpLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ConsoleApp
{
    public class LongestChainHarness
    {
        public class MyRawBlock<TData>
        {
            public string hash;
            public string prevHash;
            public TData data;

        }

        public class MyBlock<TData>
        {
            public string hash;
            public string prevHash;
            public TData data;
            public MyBlock<TData>? prevLink;   // null on the first block of a chain
            public MyBlock<TData>? nextLink;   // filled in by SetNextLinks, null before that and at a tip
            public int height;                 // 0 at the root, parent + 1 everywhere else

        }


        /// <summary>
        /// Everything carried between calls to BuildLongestChain. Blocks go in one at a time, in
        /// whatever order they turn up, and nothing is ever discarded: a shorter fork is only ever
        /// one block away from being the longest chain, so every branch stays linked and alive.
        /// </summary>
        public class ChainState<TData>
        {
            // Every block that has been linked to a parent, forks included.
            public Dictionary<string, MyBlock<TData>> byHash = new Dictionary<string, MyBlock<TData>>();

            // Blocks that showed up before their parent did, keyed by the parent they are waiting
            // on. Each one is attached the moment that parent arrives.
            public Dictionary<string, List<MyRawBlock<TData>>> waitingOnParent = new Dictionary<string, List<MyRawBlock<TData>>>();

            // Tip of the longest chain known right now. Follow prevLink from here to walk it back.
            //
            // "possible" because it is only the best answer for the blocks seen so far - a branch
            // being kept is one block away from taking it, and blocks arriving out of order mean a
            // taller branch may still be sitting in waitingOnParent. It is settled only once the
            // input is exhausted.
            public MyBlock<TData>? possibleTip;

            // The block the chain is anchored on: the first root linked, i.e. the one whose
            // prevHash was rootPrevHash. Genesis, for real chain data.
            //
            // possibleTip moves constantly and blockZero does not, so this is the end to walk
            // nextLink from to read the chain forwards - and it saves walking prevLink from the tip
            // just to find where the chain starts.
            public MyBlock<TData>? blockZero;

            // The prevHash value that means "this block starts a chain" - without it there is no
            // way to tell a root from a block whose parent simply has not arrived yet. Bitcoin
            // uses 32 zero bytes.
            public string rootPrevHash = "000000000000000000000000";
        }


        /// <summary>
        /// The last RecentCapacity blocks attached to the chain, newest last, so a block whose
        /// parent arrived just before it can be linked without going to byHash.
        ///
        /// Static, as asked for - and static fields on a generic type are per constructed type, so
        /// ChainState&lt;BlockRaw&gt; and ChainState&lt;byte[]&gt; get one ring each rather than
        /// sharing. Two chains of the SAME TData would still share one, which is what Owner is
        /// for: the ring is emptied whenever it is handed a different ChainState, so a block from
        /// one chain can never be returned as a parent in another.
        /// </summary>
        static class RecentBlocks<TData>
        {
            public const int RecentCapacity = 20;

            public static readonly MyBlock<TData>?[] Ring = new MyBlock<TData>?[RecentCapacity];

            /// <summary>Slot the next block goes in; the newest block is the one before it.</summary>
            public static int Next;

            /// <summary>Filled slots, so a part-full ring is not searched past its end.</summary>
            public static int Count;

            /// <summary>Whose blocks are in the ring right now.</summary>
            public static ChainState<TData>? Owner;

            /// <summary>Parents found in the ring, and parents that had to go to byHash.</summary>
            public static long Hits;
            public static long Misses;
        }


        /// <summary>
        /// Looks for a block by hash among the last few attached. Null means "not in the cache",
        /// which says nothing about whether the chain holds it - the caller falls back to byHash.
        ///
        /// Searched newest first: a chain arriving roughly in order hits on the first compare.
        /// </summary>
        public static MyBlock<TData>? FindRecent<TData>(string hash, ChainState<TData> state)
        {
            if (!ReferenceEquals(RecentBlocks<TData>.Owner, state))
            {
                return null;      // the ring belongs to a different chain
            }

            MyBlock<TData>?[] ring = RecentBlocks<TData>.Ring;
            int at = RecentBlocks<TData>.Next;

            for (int i = 0; i < RecentBlocks<TData>.Count; i++)
            {
                at--;
                if (at < 0)
                {
                    at = ring.Length - 1;
                }

                MyBlock<TData>? candidate = ring[at];
                if (candidate != null && candidate.hash == hash)
                {
                    return candidate;
                }
            }

            return null;
        }


        /// <summary>
        /// Adds a freshly attached block to the ring, dropping the oldest once it is full.
        /// </summary>
        static void RememberRecent<TData>(MyBlock<TData> block, ChainState<TData> state)
        {
            if (!ReferenceEquals(RecentBlocks<TData>.Owner, state))
            {
                ResetRecent(state);
            }

            RecentBlocks<TData>.Ring[RecentBlocks<TData>.Next] = block;
            RecentBlocks<TData>.Next = (RecentBlocks<TData>.Next + 1) % RecentBlocks<TData>.RecentCapacity;

            if (RecentBlocks<TData>.Count < RecentBlocks<TData>.RecentCapacity)
            {
                RecentBlocks<TData>.Count++;
            }
        }


        /// <summary>
        /// Empties the ring and points it at this chain. Call it after anything that deletes blocks
        /// from byHash - a cached block that has since been pruned would otherwise be handed back
        /// as a parent and linked to, putting a block back on a branch that was just thrown away.
        /// </summary>
        public static void ResetRecent<TData>(ChainState<TData> state)
        {
            Array.Clear(RecentBlocks<TData>.Ring);
            RecentBlocks<TData>.Next = 0;
            RecentBlocks<TData>.Count = 0;
            RecentBlocks<TData>.Owner = state;
        }


        /// <summary>How often the ring answered the parent lookup, as "hits/total (pct)".</summary>
        public static string RecentCacheStats<TData>()
        {
            long hits = RecentBlocks<TData>.Hits;
            long total = hits + RecentBlocks<TData>.Misses;
            if (total == 0)
            {
                return "0/0";
            }
            return hits + "/" + total + " (" + (100.0 * hits / total).ToString("F1") + "%)";
        }


        /// <summary>
        /// Feeds one raw block into the state and returns the tip of the longest chain as it
        /// stands afterwards (null while nothing has linked up yet).
        ///
        /// Three things can happen to the block:
        ///   linked   - its parent is held (or it is a root), so it becomes a MyBlock and may
        ///              release blocks that were waiting on it.
        ///   parked   - its parent has not arrived, so it waits in waitingOnParent.
        ///   ignored  - already held.
        ///
        /// "Longest" is block count, not accumulated work - these test blocks carry no difficulty.
        /// A tie leaves the tip where it is, so the branch that got there first keeps it.
        /// </summary>
        public static int countMisses = 0;
        public static MyBlock<TData>? BuildLongestChain<TData>(MyRawBlock<TData> next, ChainState<TData> state)
        {
            if (state.byHash.ContainsKey(next.hash))
            {
                Console.WriteLine($"  {next.hash} already held, ignored");
                return state.possibleTip;
            }

            MyBlock<TData>? parent = null;
            if (next.prevHash != state.rootPrevHash)
            {
                // The last 20 attached first. A block that turned up right behind its parent is
                // answered here; anything else falls through to the full lookup below.
                parent = FindRecent(next.prevHash, state);
                if (parent != null)
                {
                    RecentBlocks<TData>.Hits++;
                }
                else
                {
                    countMisses++;
                    // Counted here rather than in the park below, so a miss means "the ring did not
                    // answer it" whether byHash went on to or not - that is the number that says
                    // whether 20 slots is the right size.
                    RecentBlocks<TData>.Misses++;

                    if (!state.byHash.TryGetValue(next.prevHash, out parent))
                    {
                        List<MyRawBlock<TData>>? waiting;
                        if (!state.waitingOnParent.TryGetValue(next.prevHash, out waiting))
                        {
                            waiting = new List<MyRawBlock<TData>>();
                            state.waitingOnParent[next.prevHash] = waiting;
                        }
                        waiting.Add(next);
                        if (countMisses > 2900)
                        {
                            Console.WriteLine($"  {next.hash} parked, waiting on {next.prevHash}");
                        }
                        return state.possibleTip;
                    }
                }
            }

            AttachBlock(next, parent, state);
            return state.possibleTip;
        }


        /// <summary>
        /// Turns a raw block whose parent is known into a linked MyBlock, then does the same for
        /// anything that was waiting on it, and anything waiting on those. Runs off a queue rather
        /// than recursing - a long parked run would otherwise be a deep stack.
        /// </summary>
        public static void AttachBlock<TData>(MyRawBlock<TData> raw, MyBlock<TData>? parent, ChainState<TData> state)
        {
            Queue<(MyRawBlock<TData> raw, MyBlock<TData>? parent)> pending = new Queue<(MyRawBlock<TData> raw, MyBlock<TData>? parent)>();
            pending.Enqueue((raw, parent));

            while (pending.Count > 0)
            {
                var item = pending.Dequeue();
                if (state.byHash.ContainsKey(item.raw.hash))
                {
                    continue;
                }

                // A root is height 0, the way Bitcoin numbers genesis - so height is the number of
                // blocks BEFORE this one, and the chain to it is height + 1 blocks long.
                int height = 0;
                if (item.parent != null)
                {
                    height = item.parent.height + 1;
                }

                MyBlock<TData> block = new MyBlock<TData>
                {
                    hash = item.raw.hash,
                    prevHash = item.raw.prevHash,
                    data = item.raw.data,
                    prevLink = item.parent,
                    height = height
                };
                state.byHash[block.hash] = block;
                RememberRecent(block, state);

                // A block with no parent is a root - it said rootPrevHash rather than naming a
                // parent nobody has. First one wins: a second root is a branch that shares no
                // ancestor with this chain, and moving the anchor onto it would leave blockZero
                // and possibleTip in two disconnected trees.
                if (item.parent == null && state.blockZero == null)
                {
                    state.blockZero = block;
                    Console.WriteLine($"  {block.hash} is blockZero, the chain is anchored here");
                }

                // Strictly longer takes the tip. Equal length changes nothing, which is what keeps
                // the chain from flapping between two branches of the same length.
                if (state.possibleTip == null || block.height > state.possibleTip.height)
                {
                    ReportNewTip(block, state);
                    state.possibleTip = block;
                }
                else
                {
                    Console.WriteLine($"  {block.hash} linked at height {block.height}, fork kept, tip stays {state.possibleTip.hash} (height {state.possibleTip.height})");
                }

                // Whatever was parked on this block can be linked now.
                List<MyRawBlock<TData>>? released;
                if (state.waitingOnParent.TryGetValue(block.hash, out released))
                {
                    state.waitingOnParent.Remove(block.hash);
                    foreach (var child in released)
                    {
                        pending.Enqueue((child, block));
                    }
                }
            }
        }


        /// <summary>
        /// Says how the tip moved: a first block, an extension of the current tip, or a reorg onto
        /// a branch that was being kept around until it overtook.
        /// </summary>
        public static void ReportNewTip<TData>(MyBlock<TData> newTip, ChainState<TData> state)
        {
            if (state.possibleTip == null)
            {
                //Console.WriteLine($"  {newTip.hash} linked at height {newTip.height}, chain starts here");
                return;
            }

            if (newTip.prevLink == state.possibleTip)
            {
                //Console.WriteLine($"  {newTip.hash} linked at height {newTip.height}, extends tip");
                return;
            }

            MyBlock<TData>? forkPoint = FindForkPoint(state.possibleTip, newTip);
            string forkHash = "(no common ancestor)";
            if (forkPoint != null)
            {
                forkHash = forkPoint.hash;
            }
            //Console.WriteLine($"  {newTip.hash} linked at height {newTip.height}, REORG off {state.possibleTip.hash} (height {state.possibleTip.height}), forked at {forkHash}");
        }


        /// <summary>
        /// Last block the two branches share. Walks the taller one down to the shorter one's
        /// height, then steps both back together. Null when they do not share a root at all.
        /// </summary>
        public static MyBlock<TData>? FindForkPoint<TData>(MyBlock<TData> left, MyBlock<TData> right)
        {
            MyBlock<TData>? a = left;
            MyBlock<TData>? b = right;

            while (a != null && b != null && a.height > b.height)
            {
                a = a.prevLink;
            }
            while (a != null && b != null && b.height > a.height)
            {
                b = b.prevLink;
            }
            while (a != null && b != null && a != b)
            {
                a = a.prevLink;
                b = b.prevLink;
            }

            return a;
        }


        /// <summary>
        /// The chain ending at a block, root first. prevLink runs the other way.
        /// </summary>
        public static List<MyBlock<TData>> ChainToList<TData>(MyBlock<TData> tip)
        {
            List<MyBlock<TData>> chain = new List<MyBlock<TData>>();
            MyBlock<TData>? walk = tip;
            while (walk != null)
            {
                chain.Add(walk);
                walk = walk.prevLink;
            }
            chain.Reverse();
            return chain;
        }


        /// <summary>
        /// How many blocks are in the chain ending at this one, the block itself included, so 1 at
        /// a root. Counted by following prevLink rather than reading height, which makes it a check
        /// on the two agreeing - for anything this harness linked, length is always height + 1.
        ///
        /// A count and a height are not the same number: a root is 1 block long and sits at height
        /// 0, the way Bitcoin numbers genesis.
        /// </summary>
        public static int ChainLength<TData>(MyBlock<TData>? tip)
        {
            int length = 0;
            MyBlock<TData>? walk = tip;
            while (walk != null)
            {
                length++;
                walk = walk.prevLink;
            }
            return length;
        }


        /// <summary>
        /// Length of the longest chain being held, 0 while nothing has linked up yet. Forks are not
        /// counted - pass their tip to the overload above for a branch length.
        /// </summary>
        public static int ChainLength<TData>(ChainState<TData> state)
        {
            return ChainLength(state.possibleTip);
        }


        /// <summary>
        /// Every block nothing else builds on - one per branch being kept, longest chain included.
        /// </summary>
        public static List<MyBlock<TData>> FindTips<TData>(ChainState<TData> state)
        {
            HashSet<string> hasChild = new HashSet<string>();
            foreach (var block in state.byHash.Values)
            {
                if (block.prevLink != null)
                {
                    hasChild.Add(block.prevLink.hash);
                }
            }

            List<MyBlock<TData>> tips = new List<MyBlock<TData>>();
            foreach (var block in state.byHash.Values)
            {
                if (!hasChild.Contains(block.hash))
                {
                    tips.Add(block);
                }
            }
            tips.Sort((x, y) => y.height.CompareTo(x.height));
            return tips;
        }


        /// <summary>
        /// Deletes every fork branch shorter than minForkLength. A branch's length is counted from
        /// the fork point, not from the root: a two block branch off the longest chain has length 2
        /// whether it split at height 3 or at height 300000.
        ///
        /// The longest chain is never touched. Neither is a block that a branch long enough to keep
        /// also runs through, so two forks sharing a run only lose the blocks past the point where
        /// they part company.
        ///
        /// Returns how many blocks went, linked and parked together - a block parked on something
        /// that just got deleted goes with it, because the parent it is waiting on is never coming.
        /// </summary>
        public static int PruneShortForks<TData>(ChainState<TData> state, int minForkLength)
        {
            if (state.possibleTip == null)
            {
                return 0;
            }

            HashSet<string> onLongestChain = new HashSet<string>();
            foreach (var block in ChainToList(state.possibleTip))
            {
                onLongestChain.Add(block.hash);
            }

            // Each branch walked back to where it left the longest chain. A block shared by two
            // forks turns up in both segments, which is what protects it below.
            List<List<MyBlock<TData>>> segments = new List<List<MyBlock<TData>>>();
            foreach (var branchTip in FindTips(state))
            {
                if (onLongestChain.Contains(branchTip.hash))
                {
                    continue;
                }
                segments.Add(ForkSegment(branchTip, onLongestChain));
            }

            HashSet<string> keep = new HashSet<string>();
            foreach (var segment in segments)
            {
                if (segment.Count >= minForkLength)
                {
                    foreach (var block in segment)
                    {
                        keep.Add(block.hash);
                    }
                }
            }

            List<string> removed = new List<string>();
            foreach (var segment in segments)
            {
                if (segment.Count >= minForkLength)
                {
                    continue;
                }

                foreach (var block in segment)
                {
                    if (keep.Contains(block.hash))
                    {
                        continue;
                    }
                    if (!state.byHash.Remove(block.hash))
                    {
                        continue;   // already went with an earlier segment
                    }
                    removed.Add(block.hash);
                    Console.WriteLine($"  {block.hash} deleted, fork is {segment.Count} long, under {minForkLength}");
                }
            }

            int parked = DropParkedUnder(state, removed);

            // The ring can still be holding blocks that just went out of byHash. Linking a later
            // block onto one of those would resurrect a branch that was deliberately thrown away,
            // so empty it rather than trying to pick the dead entries out.
            ResetRecent(state);

            // blockZero survives every prune while the chain runs back to it, since the longest
            // chain is never touched. It only goes when the chain that outgrew it came off a
            // different root, and then the anchor it names no longer exists.
            if (state.blockZero != null && !state.byHash.ContainsKey(state.blockZero.hash))
            {
                state.blockZero = null;
            }

            return removed.Count + parked;
        }


        /// <summary>
        /// A branch tip walked back to - but not including - the first block the longest chain also
        /// holds. A branch that shares no ancestor with it runs all the way back to its own root.
        /// </summary>
        public static List<MyBlock<TData>> ForkSegment<TData>(MyBlock<TData> branchTip, HashSet<string> onLongestChain)
        {
            List<MyBlock<TData>> segment = new List<MyBlock<TData>>();
            MyBlock<TData>? walk = branchTip;
            while (walk != null && !onLongestChain.Contains(walk.hash))
            {
                segment.Add(walk);
                walk = walk.prevLink;
            }
            return segment;
        }


        /// <summary>
        /// Points every block at the block that follows it. prevLink is the link the chain is
        /// actually built on and one block can have several children, so nextLink is a derived
        /// view: each block gets the child that leads to the tallest tip under it, which makes a
        /// nextLink walk from the root the longest chain, read forwards. Tips get null.
        ///
        /// Rebuilt from scratch every time rather than patched, because a delete can leave a
        /// nextLink pointing at a block that is no longer held.
        /// </summary>
        public static void SetNextLinks<TData>(ChainState<TData> state)
        {
            foreach (var block in state.byHash.Values)
            {
                block.nextLink = null;
            }

            // FindTips hands them back tallest first, so the first branch to claim a run of blocks
            // is the tallest one through them, and a shorter branch below cannot take it back.
            foreach (var branchTip in FindTips(state))
            {
                MyBlock<TData> walk = branchTip;
                while (walk.prevLink != null)
                {
                    MyBlock<TData> parent = walk.prevLink;
                    if (parent.nextLink != null)
                    {
                        break;   // a taller branch already runs through here, and so through everything above
                    }
                    parent.nextLink = walk;
                    walk = parent;
                }
            }
        }


        /// <summary>
        /// Clears out blocks parked on a hash that has just been deleted, then blocks parked on
        /// those, and so on down. Without this they sit in waitingOnParent for a parent that no
        /// longer exists and can never be linked.
        /// </summary>
        public static int DropParkedUnder<TData>(ChainState<TData> state, List<string> removedHashes)
        {
            int dropped = 0;
            Queue<string> gone = new Queue<string>(removedHashes);

            while (gone.Count > 0)
            {
                string hash = gone.Dequeue();

                List<MyRawBlock<TData>>? orphans;
                if (!state.waitingOnParent.TryGetValue(hash, out orphans))
                {
                    continue;
                }
                state.waitingOnParent.Remove(hash);

                foreach (var orphan in orphans)
                {
                    dropped++;
                    Console.WriteLine($"  {orphan.hash} deleted, was parked on deleted {hash}");
                    gone.Enqueue(orphan.hash);
                }
            }

            return dropped;
        }


        /// <summary>
        /// Payload text for the tracing below. byte[] still prints as hex the way it always did;
        /// anything else is left to its own ToString.
        /// </summary>
        public static string DescribeData<TData>(TData data)
        {
            object? boxed = data;
            if (boxed == null)
            {
                return "(null)";
            }

            byte[]? bytes = boxed as byte[];
            if (bytes != null)
            {
                return BitConverter.ToString(bytes);
            }

            string? text = boxed.ToString();
            if (text == null)
            {
                return "(null)";
            }
            return text;
        }



        /// <summary>
        /// The longest chain, every branch still being kept, and anything still parked.
        /// </summary>
        public static void ReportState<TData>(ChainState<TData> state)
        {
            if (state.possibleTip == null)
            {
                Console.WriteLine("no chain");
                return;
            }

            List<MyBlock<TData>> longestChain = ChainToList(state.possibleTip);
            Console.WriteLine();
            string blockZeroHash = "(none linked)";
            if (state.blockZero != null)
            {
                blockZeroHash = state.blockZero.hash;
            }
            Console.WriteLine($"Longest chain is {ChainLength(state)} blocks, blockZero {blockZeroHash}, possible tip {state.possibleTip.hash}");
            Console.WriteLine($"Recent-block cache answered {RecentCacheStats<TData>()} parent lookups");
            foreach (var block in longestChain)
            {
                string prevLinkHash = "(none)";
                if (block.prevLink != null)
                {
                    prevLinkHash = block.prevLink.hash;
                }
                string nextLinkHash = "(none)";
                if (block.nextLink != null)
                {
                    nextLinkHash = block.nextLink.hash;
                }
                Console.WriteLine($"  {block.height}: Hash={block.hash}, PrevHash={block.prevHash}, PrevLink={prevLinkHash}, NextLink={nextLinkHash}, Data={DescribeData(block.data)}");
            }

            Console.WriteLine();
            Console.WriteLine("Branches still held:");
            foreach (var branchTip in FindTips(state))
            {
                string names = string.Join(" -> ", ChainToList(branchTip).Select(b => b.hash));
                string marker = "";
                if (branchTip == state.possibleTip)
                {
                    marker = "  <- longest";
                }
                Console.WriteLine($"  tip {branchTip.hash} (height {branchTip.height}): {names}{marker}");
            }

            foreach (var waiting in state.waitingOnParent)
            {
                foreach (var parked in waiting.Value)
                {
                    Console.WriteLine($"  parked: {parked.hash} still waiting on {waiting.Key}");
                }
            }
        }




        class Program
        {
            static async Task<int> Main5(string[] args)
            {
                Console.WriteLine("Longest Chain Harness");

                List<MyRawBlock<byte[]>> rawBlocks = new List<MyRawBlock<byte[]>>();
                rawBlocks.Add(new MyRawBlock<byte[]> { hash = "A", prevHash = "0", data = new byte[] { 0x01 } });
                rawBlocks.Add(new MyRawBlock<byte[]> { hash = "B", prevHash = "A", data = new byte[] { 0x02 } });
                rawBlocks.Add(new MyRawBlock<byte[]> { hash = "CFork1", prevHash = "B", data = new byte[] { 0x03 } });
                rawBlocks.Add(new MyRawBlock<byte[]> { hash = "D2", prevHash = "CFork1", data = new byte[] { 0x04 } });
                rawBlocks.Add(new MyRawBlock<byte[]> { hash = "C", prevHash = "B", data = new byte[] { 0x05 } });
                rawBlocks.Add(new MyRawBlock<byte[]> { hash = "D", prevHash = "C", data = new byte[] { 0x06 } });
                rawBlocks.Add(new MyRawBlock<byte[]> { hash = "E", prevHash = "D", data = new byte[] { 0x07 } });
                rawBlocks.Add(new MyRawBlock<byte[]> { hash = "G", prevHash = "F", data = new byte[] { 0x08 } });  // arrives before its parent
                rawBlocks.Add(new MyRawBlock<byte[]> { hash = "F", prevHash = "E", data = new byte[] { 0x09 } });

                ChainState<byte[]> state = new ChainState<byte[]>();

                foreach (var rawBlock in rawBlocks)
                {
                    Console.WriteLine($"Raw Block: Hash={rawBlock.hash}, PrevHash={rawBlock.prevHash}, Data={DescribeData(rawBlock.data)}");
                    BuildLongestChain(rawBlock, state);
                }

                int deleted = PruneShortForks(state, 3);

                SetNextLinks(state);

                // blockZero is where the chain starts, so a nextLink walk from it reads the longest
                // chain forwards - no walking prevLink back from the tip to find the first block.
                MyBlock<byte[]>? currBlock = state.blockZero;
                while (currBlock != null)
                {
                    Console.WriteLine(currBlock.hash + " -> " + currBlock.prevHash);
                    currBlock = currBlock.nextLink;
                }


                ReportState(state);




                return 0;

            }
        }
    }
}
