# btcSatoshiSharpWebExplorer

A read-only Bitcoin block explorer that keeps no data of its own. Every request becomes one or two
JSON-RPC calls to a Bitcoin Core node, and the answer is either rendered as a page or handed back
as the node's own JSON.

Nothing here reads `blk*.dat` or the RocksDB store that `ConsoleApp` builds - this project talks to
a node and nothing else.

## Running it

The node needs to be reachable and synced far enough to hold what you ask for.

```bash
dotnet run --project btcSatoshiSharpWebExplorer
```

Then open the URL it prints (`http://localhost:5217` by default).

## Pointing it at a node

Configuration lives under `BitcoinCore` in `appsettings.json`. There are two ways to authenticate,
and the default needs no setup at all.

**Cookie file (default).** Core writes a `.cookie` file into its data directory every time it
starts, holding `__cookie__:<random password>`. Leave `RpcUser` empty and the explorer reads it.
Only works when the explorer runs on the same machine as the node.

```json
"BitcoinCore": {
  "Url": "http://127.0.0.1:8332/",
  "RpcUser": "",
  "CookieFilePath": "C:\\Users\\you\\AppData\\Roaming\\Bitcoin\\.cookie"
}
```

**rpcuser / rpcpassword.** Needed to reach a node on another machine. **Do not put the password in
`appsettings.json`** - that file is committed. Use environment variables or user-secrets:

```bash
# PowerShell
$env:BitcoinCore__RpcUser = "me"
$env:BitcoinCore__RpcPassword = "the password from bitcoin.conf"
dotnet run --project btcSatoshiSharpWebExplorer

# or, kept out of the shell history and off disk in the repo
dotnet user-secrets set "BitcoinCore:RpcPassword" "..." --project btcSatoshiSharpWebExplorer
```

Testnet nodes listen on 18332 instead of 8332.

## What it serves

| Route | What it does |
| --- | --- |
| `POST /` | Bitcoin Core's own JSON-RPC shape, forwarded to the node |
| `GET /block/<hash or height>` | block page |
| `GET /tx/<txid>[?block=<hash>]` | transaction page |
| `GET /api/block/<hash or height>[?verbosity=0\|1\|2]` | the node's `getblock` JSON |
| `GET /api/tx/<txid>[?block=<hash>]` | the node's `getrawtransaction` JSON |
| `GET /search?q=...` | works out what was typed and redirects |
| `GET /health` | whether the node is answering, and `getblockchaininfo` if it is |

The JSON-RPC endpoint takes exactly what `bitcoin-cli` sends:

```bash
curl --data-binary '{"jsonrpc":"1.0","id":"curltest","method":"getblock","params":["<hash>",1]}' \
  -H 'content-type: text/plain;' http://localhost:5217/
```

and answers in the same `{"result": ..., "error": ..., "id": ...}` shape, so anything that speaks
to Core speaks to this. `id` comes back as whatever you sent - a string stays a string, a number
stays a number.

## Two things that will bite

**`/tx/<txid>` needs `-txindex=1`** unless the explorer can tell Core which block to look in.
Without the index, Core only finds transactions still in the mempool and everything else comes
back as error `-5`. Reaching a transaction by clicking through from its block page works either
way, because the block hash travels in the `?block=` query string. Turning on `-txindex=1` means
restarting the node and waiting for it to build the index.

**Only read-only methods are forwarded.** The explorer holds credentials the caller does not have,
so an unfiltered proxy would put `sendtoaddress` and `stop` one method name away from the internet.
The allowlist is `allowedMethods` in `Program.cs`; anything else gets `403` and never reaches the
node. Add to it deliberately.

Beyond that, this has no authentication of its own and assumes whoever can reach it is allowed to.
Do not put it on a public address without something in front of it.
