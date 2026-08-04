# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
dotnet build                      # builds all three projects (net8.0)
dotnet test                       # runs the xUnit suite
dotnet test --filter "FullyQualifiedName~Address1"   # single test
dotnet test --logger "console;verbosity=detailed"    # see the heavy Console.WriteLine tracing
```

There is no linter or formatter configured. `Nullable` and `ImplicitUsings` are enabled everywhere, so the build emits ~31 nullability warnings; they are pre-existing noise, not something a change introduced.

## Code style

**Never use the ternary conditional operator (`cond ? a : b`).** Always write it as a plain `if`/`else`, even when that costs extra lines or means declaring the variable on its own first:

```csharp
// NO - do not write this
BlockLocation? found = req.ByHash
    ? FindBlockByHash(req.Directory, req.FileIndex, req.Hash!, out scanned)
    : FindBlockByPosition(req.Directory, req.FileIndex, req.BlockIndex, out scanned);

// YES
BlockLocation? found;
if (req.ByHash)
{
    found = FindBlockByHash(req.Directory, req.FileIndex, req.Hash!, out scanned);
}
else
{
    found = FindBlockByPosition(req.Directory, req.FileIndex, req.BlockIndex, out scanned);
}
```

This applies to every use, including short ones buried inside a `return`, a string concatenation, or an argument list — hoist those into an `if` above the statement.

## Running requires blockchain data that is not in the repo

`btcblockdata/` is gitignored, and nothing in the build produces it. Consequences on a fresh clone:

- **`Block0` and `BlockHeader1` fail** with `System.Exception : bad` (BlockReader.cs:55 swallows the `FileNotFoundException`). They look for `<repo-root>/btcblockdata/blk00000.dat`. `Address0` and `Address1` pass — they need no data. Treat 2-passed/2-failed as the expected baseline unless you have supplied data files.
- **The console app cannot run as-is.** `Program.cs:123` resolves settings via `Helpers.GetParentDirectory(".", 4)`, which is arithmetic relative to the *build output* directory (`src/ConsoleApp/bin/Debug/net8.0`) and lands on `src/settings.json` — but the repo's `settings.json` is at the root. `dotnet run` from the repo root is worse: the walk goes above `C:\` and throws `NullReferenceException`. Any fix here means reconciling that path arithmetic with how the app is actually launched (Visual Studio sets cwd to the output dir; `dotnet run` does not).

`settings.json` points `BlockChainDataDirectory` at an absolute path (`C:\btcblock\...`). `Program.cs` branches on whether the value contains `:\` to decide between using it verbatim and resolving it 5 levels up from the output dir.

## Architecture

Three projects: `SatoshiSharpLib` (all logic), `ConsoleApp` (a scratchpad `Main` plus the `ClaudeCode` byte-pattern search utilities), `SatoshiSharpTest` (xUnit). `ConsoleApp` is not a stable CLI — it is a long experiment buffer full of commented-out probes and hard-coded hashes for specific early blocks.

**The parse pipeline is a single method: `BlockReader.ReadBlkDataFile`.** It XORs the whole file into memory with the 8-byte key, then loops: magic bytes `F9BEB4D9` → block size → 80-byte header (`Block.Header.Parse`) → varint tx count → N transactions (`Transaction.readTransactionBytes`). After each block it re-serializes every transaction and recomputes the merkle root, comparing against the header. Validation is not a separate pass and there are no `Validate*()` methods — everything happens inline and **failures `throw`** rather than flagging. `header.Valid` is set but nothing reads it.

**Blocks must arrive in chain order.** The reader threads a `prevBlock` through the loop and throws `"error prev hash"` when a block's `PrevBlockHash` doesn't match the previous block's computed hash. Raw `blk*.dat` files from Bitcoin Core are *not* height-ordered, which is why the code reads files named `blk00000_ordered.dat` — a pre-sorted variant produced outside this repo.

**Endianness is the main source of confusion.** Bitcoin stores hashes little-endian on disk; explorers display them reversed. The codebase converts constantly:
- `Block.ThirtyTwoByteClass` wraps 32-byte fields and its `ToString()` **reverses** to explorer order (lowercase hex). Implicit conversions to/from `byte[]` mean a raw array assigned to `PrevBlockHash`/`MerkleRoot` silently becomes this type.
- `Helpers.ByteArrayToHexString` does *not* reverse; `Helpers.GetStringReverseHexBytes` and `Helpers.ReverseHexString` do.
- `Header.Hash` is a **string**, not bytes. `SerializeBlockHeader` round-trips hashes through hex strings to flip them back to little-endian (flagged with a TODO in the source).

**Wallet tracking is early-stage and only works for the genesis era.** `StateWallets.Wallets` is a static `List<Wallet>` with linear lookup, initialized by the caller before reading. `Helpers.readSignedSpend` assumes every output is bare P2PK — it strips one leading byte and one trailing byte (`scriptHex.Substring(2, len - 4)`) to get the pubkey, so it breaks on P2PKH and anything later. Value is also hard-coded to 50 at the call site (`Transaction.cs:222`), not taken from the output. There are two transaction types: `Transaction` (real parsing) and `TransactionDELETEME` in `Wallet.cs` (the wallet-side placeholder).

**RIPEMD-160 exists twice.** `Ripemd160.cs` has a hand-rolled `RIPEMD160Managed`, but live code paths use BouncyCastle's `RipeMD160Digest`. The dependency is `Portable.BouncyCastle` 1.9.0 (not `BouncyCastle.Cryptography`, despite what the README says).

## Landmines when editing BlockReader

- **Hard-coded absolute path**: when `BlockNumber > 191` it dumps the chain to `C:\btcblock\mostblocks11_zeroxor\hashes.txt` (BlockReader.cs:291). This will throw on any machine without that directory.
- **Sanity limits tuned to early blocks**: throws if `BlockSize > 266222` or `TransactionCount > 870`. Both are arbitrary and will reject modern blocks.
- Console noise (`"sdf"`, `"das"`, `"sg"`, `"two inputs"`) and comparisons against specific block-193 hashes are debugging breakpoint bait, not logic.

## The README is aspirational, not accurate

`README.md` documents an API that does not exist: `BlockHeader` (the real type is nested `Block.Header`), `block.transactions` (it's `Transactions`), `block.Height` (it's `header.BlockNumber`), `header.ValidateHash()`, `block.ValidateMerkleRoot()`, and `Helpers.PublicKeyToBitcoinAddress()`. It was written by an LLM in commits `8013e58`/`a315506` without being checked against the source. Verify against the code before relying on any snippet from it — and prefer fixing the README over writing code to match it.
