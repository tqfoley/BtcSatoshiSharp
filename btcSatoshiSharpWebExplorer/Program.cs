using System.Text;
using System.Text.Json;
using btcSatoshiSharpWebExplorer.CoreRpc;
using btcSatoshiSharpWebExplorer.Web;

// A read-only Bitcoin block explorer that keeps no data of its own: every request turns into one
// or two JSON-RPC calls to a Bitcoin Core node and the answer is rendered or passed straight back.
//
//   POST /                        Core's own JSON-RPC shape, proxied to the node
//   GET  /block/<hash|height>     HTML block page
//   GET  /tx/<txid>[?block=hash]  HTML transaction page
//   GET  /api/block/<hash|height> the node's getblock JSON
//   GET  /api/tx/<txid>           the node's getrawtransaction JSON
//   GET  /search?q=...            works out what was typed and redirects
//   GET  /health                  whether the node is reachable
//
// Configure the node in appsettings.json under "BitcoinCore", or with environment variables:
//   BitcoinCore__Url, BitcoinCore__RpcUser, BitcoinCore__RpcPassword

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<CoreRpcOptions>(builder.Configuration.GetSection(CoreRpcOptions.SectionName));
builder.Services.AddHttpClient<CoreRpcClient>();

var app = builder.Build();

// ---------------------------------------------------------------------------------------
// Shared helpers
// ---------------------------------------------------------------------------------------

static IResult Html(string markup, int status = 200)
{
    return Results.Content(markup, "text/html; charset=utf-8", Encoding.UTF8, status);
}

// Turns an exception into a page. The three cases read differently to whoever is looking:
// the node does not have it, the node is not reachable, or the node refused the request.
static IResult HtmlForException(Exception ex, string what)
{
    if (ex is CoreRpcException rpc)
    {
        if (rpc.IsNotFound)
        {
            return Html(HtmlPages.Error("Not found", what + " is not something the node knows about.",
                                        "Core said: " + rpc.Message), 404);
        }
        return Html(HtmlPages.Error("The node refused that", rpc.Message,
                                    "RPC error code " + rpc.Code), 400);
    }

    if (ex is HttpRequestException || ex is TaskCanceledException || ex is InvalidOperationException)
    {
        return Html(HtmlPages.Error("Cannot reach the node",
                                    "The explorer could not talk to Bitcoin Core.",
                                    ex.Message), 502);
    }

    return Html(HtmlPages.Error("Something went wrong", ex.Message, null), 500);
}

// The same three cases, as JSON rather than a page.
static IResult JsonForException(Exception ex)
{
    if (ex is CoreRpcException rpc)
    {
        int status = 400;
        if (rpc.IsNotFound)
        {
            status = 404;
        }
        return Results.Json(new { error = new { code = rpc.Code, message = rpc.Message } }, statusCode: status);
    }

    return Results.Json(new { error = new { code = 0, message = ex.Message } }, statusCode: 502);
}

// ---------------------------------------------------------------------------------------
// JSON-RPC passthrough - the endpoint that makes this look like a node
// ---------------------------------------------------------------------------------------

// Takes exactly what bitcoin-cli sends:
//   {"jsonrpc":"1.0","id":"curltest","method":"getblock","params":["<hash>",1]}
// and answers in the same shape, so anything that speaks to Core speaks to this.
//
// Only read-only methods are allowed through. The node is reached with credentials the caller
// does not have, so forwarding everything would hand the internet a wallet - sendtoaddress and
// stop are one method name away otherwise.
var allowedMethods = new HashSet<string>(StringComparer.Ordinal)
{
    "getblock",
    "getblockhash",
    "getblockheader",
    "getblockcount",
    "getbestblockhash",
    "getblockchaininfo",
    "getrawtransaction",
    "getchaintips",
    "getdifficulty",
    "gettxoutproof",
    "getmempoolinfo",
    "getblockstats",
    "decoderawtransaction",
};

app.MapPost("/", async (HttpRequest request, CoreRpcClient rpc) =>
{
    string body;
    using (var reader = new StreamReader(request.Body, Encoding.UTF8))
    {
        body = await reader.ReadToEndAsync();
    }

    string id = "null";
    try
    {
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;

        JsonElement idElement;
        if (root.TryGetProperty("id", out idElement))
        {
            id = idElement.GetRawText();
        }

        JsonElement methodElement;
        if (!root.TryGetProperty("method", out methodElement) || methodElement.ValueKind != JsonValueKind.String)
        {
            return Results.Json(new { result = (object?)null, error = new { code = -32600, message = "no method in the request" } });
        }

        string method = methodElement.GetString() ?? "";
        if (!allowedMethods.Contains(method))
        {
            return Results.Json(new
            {
                result = (object?)null,
                error = new { code = -32601, message = "method '" + method + "' is not one this explorer will forward" }
            }, statusCode: 403);
        }

        var parameters = new List<object>();
        JsonElement paramsElement;
        if (root.TryGetProperty("params", out paramsElement) && paramsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement p in paramsElement.EnumerateArray())
            {
                parameters.Add(JsonElementToObject(p));
            }
        }

        JsonElement result = await rpc.CallAsync(method, parameters.ToArray());

        // Rebuilt by hand rather than serialized from an anonymous type, so result and id keep
        // exactly the JSON they had - a height stays a number, an id stays whatever it was.
        string json = "{\"result\":" + result.GetRawText() + ",\"error\":null,\"id\":" + id + "}";
        return Results.Content(json, "application/json; charset=utf-8");
    }
    catch (JsonException)
    {
        return Results.Json(new { result = (object?)null, error = new { code = -32700, message = "the request body is not JSON" } },
                            statusCode: 400);
    }
    catch (CoreRpcException ex)
    {
        string json = "{\"result\":null,\"error\":{\"code\":" + ex.Code + ",\"message\":"
                      + JsonSerializer.Serialize(ex.Message) + "},\"id\":" + id + "}";
        return Results.Content(json, "application/json; charset=utf-8", Encoding.UTF8, 500);
    }
    catch (Exception ex)
    {
        return JsonForException(ex);
    }
});

// JSON values arrive as JsonElement but CallAsync serializes plain objects, so they have to come
// back to CLR types on the way through. Anything structured is passed on as raw JSON.
static object JsonElementToObject(JsonElement element)
{
    switch (element.ValueKind)
    {
        case JsonValueKind.String:
            return element.GetString() ?? "";
        case JsonValueKind.True:
            return true;
        case JsonValueKind.False:
            return false;
        case JsonValueKind.Number:
            int whole;
            if (element.TryGetInt32(out whole))
            {
                return whole;
            }
            return element.GetDouble();
        default:
            return JsonSerializer.Deserialize<object>(element.GetRawText()) ?? "";
    }
}

// ---------------------------------------------------------------------------------------
// HTML pages
// ---------------------------------------------------------------------------------------

app.MapGet("/", async (CoreRpcClient rpc) =>
{
    // The node is asked for its chain and height only so the front page can say whether it is
    // actually reachable. A node that is down should not make the search box disappear.
    string? summary = null;
    try
    {
        JsonElement info = await rpc.GetBlockchainInfoAsync();

        string chain = "";
        JsonElement chainElement;
        if (info.TryGetProperty("chain", out chainElement))
        {
            chain = chainElement.GetString() ?? "";
        }

        string blocks = "";
        JsonElement blocksElement;
        if (info.TryGetProperty("blocks", out blocksElement))
        {
            blocks = blocksElement.ToString();
        }

        summary = "connected to a " + chain + " node at height " + blocks;
    }
    catch (Exception ex)
    {
        summary = "the node is not answering: " + ex.Message;
    }

    return Html(HtmlPages.Home(summary));
});

app.MapGet("/block/{id}", async (string id, CoreRpcClient rpc) =>
{
    try
    {
        string hash = await rpc.ResolveBlockHashAsync(id);
        JsonElement block = await rpc.GetBlockAsync(hash, 1);
        return Html(HtmlPages.BlockPage(block));
    }
    catch (Exception ex)
    {
        return HtmlForException(ex, "Block '" + id + "'");
    }
});

app.MapGet("/tx/{txid}", async (string txid, string? block, CoreRpcClient rpc) =>
{
    try
    {
        if (!CoreRpcClient.LooksLikeHash(txid))
        {
            return Html(HtmlPages.Error("Not a txid", "'" + txid + "' is not 64 hex characters.", null), 400);
        }

        JsonElement tx = await rpc.GetRawTransactionAsync(txid, block);
        return Html(HtmlPages.TransactionPage(tx));
    }
    catch (CoreRpcException ex) when (ex.IsNotFound && block == null)
    {
        // The overwhelmingly common cause, and the one nobody guesses on their own.
        return Html(HtmlPages.Error("Transaction not found",
            "The node could not find that transaction.",
            "Without -txindex=1 Core can only look up transactions in the mempool. Either restart the " +
            "node with -txindex=1 and let it rebuild, or reach this transaction from its block page so " +
            "the explorer can tell Core which block to read."), 404);
    }
    catch (Exception ex)
    {
        return HtmlForException(ex, "Transaction '" + txid + "'");
    }
});

// Works out what was typed and sends the browser to the right page.
app.MapGet("/search", (string? q) =>
{
    string query = (q ?? "").Trim();
    if (query.Length == 0)
    {
        return Results.Redirect("/");
    }

    // A height is short and numeric; a hash is 64 hex characters. A block hash starts with a run
    // of zeros because of the proof of work, which a txid has no reason to - so a 64 character
    // string starting 0000 is treated as a block and anything else as a transaction. Both pages
    // handle being wrong by showing a not-found rather than misleading anyone.
    int height;
    if (int.TryParse(query, out height) && height >= 0)
    {
        return Results.Redirect("/block/" + height);
    }

    if (CoreRpcClient.LooksLikeHash(query))
    {
        if (query.StartsWith("0000", StringComparison.Ordinal))
        {
            return Results.Redirect("/block/" + query.ToLowerInvariant());
        }
        return Results.Redirect("/tx/" + query.ToLowerInvariant());
    }

    return Html(HtmlPages.Error("Cannot tell what that is",
        "'" + query + "' is not a height, a block hash, or a txid.",
        "Heights are plain numbers. Hashes and txids are 64 hex characters."), 400);
});

// ---------------------------------------------------------------------------------------
// JSON convenience routes - the same data the pages use, unrendered
// ---------------------------------------------------------------------------------------

app.MapGet("/api/block/{id}", async (string id, int? verbosity, CoreRpcClient rpc) =>
{
    try
    {
        int level = 1;
        if (verbosity.HasValue)
        {
            level = verbosity.Value;
        }

        string hash = await rpc.ResolveBlockHashAsync(id);
        JsonElement block = await rpc.GetBlockAsync(hash, level);
        return Results.Content(block.GetRawText(), "application/json; charset=utf-8");
    }
    catch (Exception ex)
    {
        return JsonForException(ex);
    }
});

app.MapGet("/api/tx/{txid}", async (string txid, string? block, CoreRpcClient rpc) =>
{
    try
    {
        JsonElement tx = await rpc.GetRawTransactionAsync(txid, block);
        return Results.Content(tx.GetRawText(), "application/json; charset=utf-8");
    }
    catch (Exception ex)
    {
        return JsonForException(ex);
    }
});

app.MapGet("/health", async (CoreRpcClient rpc) =>
{
    try
    {
        JsonElement info = await rpc.GetBlockchainInfoAsync();
        return Results.Json(new { ok = true, node = JsonSerializer.Deserialize<object>(info.GetRawText()) });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, message = ex.Message }, statusCode: 503);
    }
});

app.Run();
