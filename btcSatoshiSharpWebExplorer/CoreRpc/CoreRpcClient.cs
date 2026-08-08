using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace btcSatoshiSharpWebExplorer.CoreRpc
{
    /// <summary>
    /// Speaks Bitcoin Core's JSON-RPC over HTTP. Every call is a POST of
    ///
    ///     {"jsonrpc": "1.0", "id": "...", "method": "getblock", "params": [...]}
    ///
    /// to the node, and the reply is always
    ///
    ///     {"result": ..., "error": ..., "id": ...}
    ///
    /// with exactly one of result and error filled in. Core answers HTTP 500 when error is set,
    /// so the status code is read after the body rather than before it - the body is the useful
    /// part either way.
    ///
    /// Returns JsonElement rather than mapped classes on purpose: the explorer hands most of what
    /// it gets straight back out, and Core adds fields between versions. Mapping them would mean
    /// silently dropping anything new.
    /// </summary>
    public sealed class CoreRpcClient
    {
        readonly HttpClient _http;
        readonly CoreRpcOptions _options;
        readonly ILogger<CoreRpcClient> _log;

        public CoreRpcClient(HttpClient http, IOptions<CoreRpcOptions> options, ILogger<CoreRpcClient> log)
        {
            _http = http;
            _options = options.Value;
            _log = log;

            _http.BaseAddress = new Uri(_options.Url);
            _http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
            _http.DefaultRequestHeaders.Authorization = BuildAuthHeader();
        }

        /// <summary>
        /// rpcuser/rpcpassword when they are configured, otherwise the .cookie file Core writes
        /// into its data directory at startup.
        /// </summary>
        AuthenticationHeaderValue BuildAuthHeader()
        {
            string credentials;

            if (_options.RpcUser.Length > 0)
            {
                credentials = _options.RpcUser + ":" + _options.RpcPassword;
            }
            else
            {
                if (!File.Exists(_options.CookieFilePath))
                {
                    throw new InvalidOperationException(
                        "No RPC credentials. Either set BitcoinCore:RpcUser and BitcoinCore:RpcPassword, or " +
                        "point BitcoinCore:CookieFilePath at Core's .cookie file - looked for it at " +
                        _options.CookieFilePath + " and it is not there. The node writes that file when it " +
                        "starts, so this also means the node may not be running.");
                }

                // The cookie file holds "__cookie__:<password>" on one line, already in the shape
                // basic auth wants.
                credentials = File.ReadAllText(_options.CookieFilePath).Trim();
                _log.LogInformation("authenticating with the cookie file at {path}", _options.CookieFilePath);
            }

            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
            return new AuthenticationHeaderValue("Basic", encoded);
        }

        /// <summary>
        /// Calls one method and hands back whatever is in "result". Throws CoreRpcException when
        /// the node reports an error, HttpRequestException when it cannot be reached at all.
        /// </summary>
        public async Task<JsonElement> CallAsync(string method, params object[] parameters)
        {
            var request = new
            {
                jsonrpc = "1.0",
                id = "btcSatoshiSharpWebExplorer",
                method = method,
                @params = parameters
            };

            string body = JsonSerializer.Serialize(request);
            _log.LogDebug("rpc -> {method} {body}", method, body);

            using var content = new StringContent(body, Encoding.UTF8, "text/plain");
            using HttpResponseMessage response = await _http.PostAsync("", content);

            string text = await response.Content.ReadAsStringAsync();

            // A node that is up but unhappy answers 500 with a JSON-RPC error in the body, which is
            // more useful than the status code. Only give up on the body when there isn't one.
            if (text.Length == 0)
            {
                response.EnsureSuccessStatusCode();
                throw new CoreRpcException(0, "the node returned an empty body for " + method);
            }

            using JsonDocument document = JsonDocument.Parse(text);
            JsonElement root = document.RootElement;

            JsonElement error;
            if (root.TryGetProperty("error", out error) && error.ValueKind != JsonValueKind.Null)
            {
                int code = 0;
                JsonElement codeElement;
                if (error.TryGetProperty("code", out codeElement))
                {
                    code = codeElement.GetInt32();
                }

                string message = "the node reported an error with no message";
                JsonElement messageElement;
                if (error.TryGetProperty("message", out messageElement))
                {
                    message = messageElement.GetString() ?? message;
                }

                throw new CoreRpcException(code, message);
            }

            JsonElement result;
            if (!root.TryGetProperty("result", out result))
            {
                throw new CoreRpcException(0, "the node's reply to " + method + " had no result and no error");
            }

            // The JsonDocument owns the memory the element points into and is about to be
            // disposed, so hand back a detached copy rather than a dangling one.
            return result.Clone();
        }

        // ---------------------------------------------------------------------------------
        // The handful of calls the explorer actually makes
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// getblock. Verbosity 0 is the raw block as a hex string, 1 is the block with its
        /// transactions listed as txids only, 2 is the block with every transaction decoded.
        /// </summary>
        public Task<JsonElement> GetBlockAsync(string blockHash, int verbosity)
        {
            return CallAsync("getblock", blockHash, verbosity);
        }

        /// <summary>getblockhash - the only way to turn a height into a hash.</summary>
        public Task<JsonElement> GetBlockHashAsync(int height)
        {
            return CallAsync("getblockhash", height);
        }

        /// <summary>
        /// getrawtransaction, decoded.
        ///
        /// Without a blockHash this needs the node to be running with -txindex=1, otherwise it only
        /// finds transactions still in the mempool and anything else comes back as error -5. Pass
        /// the block hash when it is known and the node reads that block directly, no index needed.
        /// </summary>
        public Task<JsonElement> GetRawTransactionAsync(string txid, string? blockHash)
        {
            if (blockHash == null)
            {
                return CallAsync("getrawtransaction", txid, true);
            }
            return CallAsync("getrawtransaction", txid, true, blockHash);
        }

        /// <summary>getblockchaininfo - chain name, height, verification progress.</summary>
        public Task<JsonElement> GetBlockchainInfoAsync()
        {
            return CallAsync("getblockchaininfo");
        }

        /// <summary>
        /// Turns whatever the user typed into a block hash: a 64 character hex string is already
        /// one, a plain number is a height and gets looked up.
        /// </summary>
        public async Task<string> ResolveBlockHashAsync(string blockOrHeight)
        {
            string text = blockOrHeight.Trim();

            if (LooksLikeHash(text))
            {
                return text.ToLowerInvariant();
            }

            int height;
            if (!int.TryParse(text, out height) || height < 0)
            {
                throw new CoreRpcException(-8, "'" + blockOrHeight + "' is neither a 64 character block hash nor a height");
            }

            JsonElement hash = await GetBlockHashAsync(height);
            return hash.GetString() ?? "";
        }

        public static bool LooksLikeHash(string text)
        {
            if (text.Length != 64)
            {
                return false;
            }

            foreach (char c in text)
            {
                if (!Uri.IsHexDigit(c))
                {
                    return false;
                }
            }
            return true;
        }
    }
}
