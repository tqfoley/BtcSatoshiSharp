using System.Net;
using System.Text;
using System.Text.Json;

namespace btcSatoshiSharpWebExplorer.Web
{
    /// <summary>
    /// Server-rendered pages, built as strings. There is no Razor and no client-side framework
    /// here on purpose - the whole site is a handful of read-only views over what the node
    /// returns, and a string builder keeps the JSON shape and the markup next to each other.
    ///
    /// Everything that reaches the page goes through Escape. Values from the node are hex and
    /// numbers in practice, but a coinbase scriptSig holds whatever the miner put there, so it is
    /// treated as hostile.
    /// </summary>
    public static class HtmlPages
    {
        /// <summary>HTML-escapes a value. Never interpolate into a page without this.</summary>
        public static string Escape(string? text)
        {
            if (text == null)
            {
                return "";
            }
            return WebUtility.HtmlEncode(text);
        }

        const string Style = @"
:root { color-scheme: light dark; --fg:#111; --bg:#fff; --muted:#666; --line:#ddd; --accent:#0b6; --code:#f6f6f6; }
@media (prefers-color-scheme: dark) {
  :root { --fg:#e6e6e6; --bg:#121212; --muted:#999; --line:#333; --accent:#3d8; --code:#1c1c1c; }
}
* { box-sizing: border-box; }
body { margin:0; padding:2rem 1rem; background:var(--bg); color:var(--fg);
       font:15px/1.5 ui-sans-serif,system-ui,-apple-system,Segoe UI,Roboto,sans-serif; }
main { max-width: 60rem; margin: 0 auto; }
h1 { font-size:1.3rem; margin:0 0 1.5rem; }
h2 { font-size:1rem; margin:2rem 0 .5rem; color:var(--muted); text-transform:uppercase; letter-spacing:.05em; }
a { color:var(--accent); text-decoration:none; }
a:hover { text-decoration:underline; }
code, .hash { font-family:ui-monospace,SFMono-Regular,Consolas,monospace; font-size:.85em; word-break:break-all; }
table { border-collapse:collapse; width:100%; }
td, th { text-align:left; padding:.4rem .6rem; border-bottom:1px solid var(--line); vertical-align:top; }
th { width:12rem; color:var(--muted); font-weight:normal; }
form { display:flex; gap:.5rem; margin-bottom:2rem; }
input[type=text] { flex:1; padding:.6rem; border:1px solid var(--line); border-radius:4px;
                   background:var(--bg); color:var(--fg); font-family:inherit; }
button { padding:.6rem 1.2rem; border:0; border-radius:4px; background:var(--accent); color:#fff; cursor:pointer; }
.scroll { overflow-x:auto; }
.muted { color:var(--muted); }
.err { padding:1rem; background:var(--code); border-left:3px solid #c33; border-radius:4px; }
pre { background:var(--code); padding:1rem; border-radius:4px; overflow-x:auto; }
.nav { margin-bottom:1.5rem; font-size:.9em; }
";

        static string Shell(string title, string body)
        {
            var sb = new StringBuilder();
            sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
            sb.Append("<title>").Append(Escape(title)).Append("</title>");
            sb.Append("<style>").Append(Style).Append("</style></head><body><main>");
            sb.Append("<div class=\"nav\"><a href=\"/\">satoshisharp explorer</a></div>");
            sb.Append(body);
            sb.Append("</main></body></html>");
            return sb.ToString();
        }

        static string SearchForm()
        {
            return "<form action=\"/search\" method=\"get\">" +
                   "<input type=\"text\" name=\"q\" placeholder=\"block height, block hash, or txid\" autofocus>" +
                   "<button type=\"submit\">Look up</button></form>";
        }

        public static string Home(string? chainSummary)
        {
            var sb = new StringBuilder();
            sb.Append("<h1>Bitcoin block explorer</h1>");
            sb.Append(SearchForm());

            if (chainSummary != null)
            {
                sb.Append("<p class=\"muted\">").Append(Escape(chainSummary)).Append("</p>");
            }

            sb.Append("<h2>Also available</h2><table>");
            sb.Append(Row("JSON-RPC", "<code>POST /</code> &mdash; Bitcoin Core's own request shape, proxied to the node"));
            sb.Append(Row("Block", "<code>/block/&lt;hash or height&gt;</code>"));
            sb.Append(Row("Transaction", "<code>/tx/&lt;txid&gt;</code>"));
            sb.Append("</table>");
            return Shell("satoshisharp explorer", sb.ToString());
        }

        public static string Error(string title, string message, string? detail)
        {
            var sb = new StringBuilder();
            sb.Append("<h1>").Append(Escape(title)).Append("</h1>");
            sb.Append(SearchForm());
            sb.Append("<div class=\"err\">").Append(Escape(message));
            if (detail != null)
            {
                sb.Append("<br><span class=\"muted\">").Append(Escape(detail)).Append("</span>");
            }
            sb.Append("</div>");
            return Shell(title, sb.ToString());
        }

        static string Row(string label, string valueHtml)
        {
            return "<tr><th>" + Escape(label) + "</th><td>" + valueHtml + "</td></tr>";
        }

        /// <summary>A row whose value is text from the node, so it needs escaping.</summary>
        static string TextRow(string label, string? value)
        {
            return Row(label, "<span class=\"hash\">" + Escape(value) + "</span>");
        }

        /// <summary>Reads a property as a display string, or "" when the node did not send it.</summary>
        static string Get(JsonElement obj, string name)
        {
            JsonElement value;
            if (!obj.TryGetProperty(name, out value))
            {
                return "";
            }
            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? "";
            }
            if (value.ValueKind == JsonValueKind.Null)
            {
                return "";
            }
            return value.ToString();
        }

        /// <summary>
        /// The block page, from a getblock verbosity 1 result: the header fields, then the txids.
        /// </summary>
        public static string BlockPage(JsonElement block)
        {
            string hash = Get(block, "hash");
            string height = Get(block, "height");

            var sb = new StringBuilder();
            sb.Append("<h1>Block ").Append(Escape(height)).Append("</h1>");
            sb.Append(SearchForm());

            sb.Append("<h2>Header</h2><div class=\"scroll\"><table>");
            sb.Append(TextRow("Hash", hash));
            sb.Append(TextRow("Height", height));
            sb.Append(TextRow("Confirmations", Get(block, "confirmations")));
            sb.Append(TextRow("Time", FormatUnixTime(Get(block, "time"))));
            sb.Append(TextRow("Median time", FormatUnixTime(Get(block, "mediantime"))));
            sb.Append(TextRow("Merkle root", Get(block, "merkleroot")));
            sb.Append(TextRow("Version", Get(block, "versionHex")));
            sb.Append(TextRow("Bits", Get(block, "bits")));
            sb.Append(TextRow("Difficulty", Get(block, "difficulty")));
            sb.Append(TextRow("Nonce", Get(block, "nonce")));
            sb.Append(TextRow("Chainwork", Get(block, "chainwork")));
            sb.Append(TextRow("Size", Get(block, "size") + " bytes"));
            sb.Append(TextRow("Stripped size", Get(block, "strippedsize") + " bytes"));
            sb.Append(TextRow("Weight", Get(block, "weight")));
            sb.Append(TextRow("Transactions", Get(block, "nTx")));

            string previous = Get(block, "previousblockhash");
            if (previous.Length > 0)
            {
                sb.Append(Row("Previous block",
                    "<a class=\"hash\" href=\"/block/" + Escape(previous) + "\">" + Escape(previous) + "</a>"));
            }

            // Absent on the tip, which is how you know you are looking at it.
            string next = Get(block, "nextblockhash");
            if (next.Length > 0)
            {
                sb.Append(Row("Next block",
                    "<a class=\"hash\" href=\"/block/" + Escape(next) + "\">" + Escape(next) + "</a>"));
            }

            sb.Append("</table></div>");

            JsonElement txs;
            if (block.TryGetProperty("tx", out txs) && txs.ValueKind == JsonValueKind.Array)
            {
                sb.Append("<h2>Transactions (").Append(txs.GetArrayLength()).Append(")</h2>");
                sb.Append("<div class=\"scroll\"><table>");

                int index = 0;
                foreach (JsonElement tx in txs.EnumerateArray())
                {
                    // Verbosity 1 gives txid strings; verbosity 2 gives whole objects.
                    string txid;
                    if (tx.ValueKind == JsonValueKind.String)
                    {
                        txid = tx.GetString() ?? "";
                    }
                    else
                    {
                        txid = Get(tx, "txid");
                    }

                    string label = index.ToString();
                    if (index == 0)
                    {
                        label = "0 (coinbase)";
                    }

                    sb.Append("<tr><th>").Append(Escape(label)).Append("</th><td>")
                      .Append("<a class=\"hash\" href=\"/tx/").Append(Escape(txid)).Append("?block=")
                      .Append(Escape(hash)).Append("\">").Append(Escape(txid)).Append("</a></td></tr>");
                    index++;
                }
                sb.Append("</table></div>");
            }

            sb.Append("<h2>Raw</h2><p><a href=\"/api/block/").Append(Escape(hash))
              .Append("\">this block as JSON</a></p>");

            return Shell("Block " + height, sb.ToString());
        }

        /// <summary>The transaction page, from a getrawtransaction verbose result.</summary>
        public static string TransactionPage(JsonElement tx)
        {
            string txid = Get(tx, "txid");

            var sb = new StringBuilder();
            sb.Append("<h1>Transaction</h1>");
            sb.Append(SearchForm());

            sb.Append("<div class=\"scroll\"><table>");
            sb.Append(TextRow("Txid", txid));
            sb.Append(TextRow("Hash (wtxid)", Get(tx, "hash")));
            sb.Append(TextRow("Version", Get(tx, "version")));
            sb.Append(TextRow("Size", Get(tx, "size") + " bytes"));
            sb.Append(TextRow("Virtual size", Get(tx, "vsize")));
            sb.Append(TextRow("Weight", Get(tx, "weight")));
            sb.Append(TextRow("Locktime", Get(tx, "locktime")));
            sb.Append(TextRow("Confirmations", Get(tx, "confirmations")));
            sb.Append(TextRow("Time", FormatUnixTime(Get(tx, "time"))));

            string blockHash = Get(tx, "blockhash");
            if (blockHash.Length > 0)
            {
                sb.Append(Row("In block",
                    "<a class=\"hash\" href=\"/block/" + Escape(blockHash) + "\">" + Escape(blockHash) + "</a>"));
            }
            sb.Append("</table></div>");

            JsonElement vin;
            if (tx.TryGetProperty("vin", out vin) && vin.ValueKind == JsonValueKind.Array)
            {
                sb.Append("<h2>Inputs (").Append(vin.GetArrayLength()).Append(")</h2>");
                sb.Append("<div class=\"scroll\"><table>");

                foreach (JsonElement input in vin.EnumerateArray())
                {
                    JsonElement coinbase;
                    if (input.TryGetProperty("coinbase", out coinbase))
                    {
                        sb.Append(Row("coinbase", "<span class=\"hash\">"
                                  + Escape(coinbase.GetString()) + "</span>"));
                        continue;
                    }

                    string previousTxid = Get(input, "txid");
                    string vout = Get(input, "vout");
                    sb.Append("<tr><th>output " + Escape(vout) + " of</th><td>")
                      .Append("<a class=\"hash\" href=\"/tx/").Append(Escape(previousTxid)).Append("\">")
                      .Append(Escape(previousTxid)).Append("</a></td></tr>");
                }
                sb.Append("</table></div>");
            }

            JsonElement vout2;
            if (tx.TryGetProperty("vout", out vout2) && vout2.ValueKind == JsonValueKind.Array)
            {
                sb.Append("<h2>Outputs (").Append(vout2.GetArrayLength()).Append(")</h2>");
                sb.Append("<div class=\"scroll\"><table>");

                foreach (JsonElement output in vout2.EnumerateArray())
                {
                    string value = Get(output, "value");
                    string n = Get(output, "n");

                    string destination = "";
                    JsonElement scriptPubKey;
                    if (output.TryGetProperty("scriptPubKey", out scriptPubKey))
                    {
                        destination = Get(scriptPubKey, "address");
                        if (destination.Length == 0)
                        {
                            // Pre-P2PKH outputs have no address at all, only a raw script. Core
                            // still names the type, which is more use than an empty cell.
                            destination = Get(scriptPubKey, "type");
                        }
                    }

                    sb.Append("<tr><th>").Append(Escape(n)).Append("</th><td>")
                      .Append("<strong>").Append(Escape(value)).Append(" BTC</strong> ")
                      .Append("<span class=\"hash muted\">").Append(Escape(destination)).Append("</span>")
                      .Append("</td></tr>");
                }
                sb.Append("</table></div>");
            }

            sb.Append("<h2>Raw</h2><p><a href=\"/api/tx/").Append(Escape(txid))
              .Append("\">this transaction as JSON</a></p>");

            return Shell("Transaction " + txid, sb.ToString());
        }

        /// <summary>A unix timestamp with the UTC time beside it, since the raw number tells nobody much.</summary>
        static string FormatUnixTime(string unixSeconds)
        {
            long seconds;
            if (!long.TryParse(unixSeconds, out seconds))
            {
                return unixSeconds;
            }
            return unixSeconds + "  (" + DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.ToString("u") + ")";
        }
    }
}
