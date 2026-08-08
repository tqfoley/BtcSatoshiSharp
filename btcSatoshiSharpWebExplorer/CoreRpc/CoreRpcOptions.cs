namespace btcSatoshiSharpWebExplorer.CoreRpc
{
    /// <summary>
    /// Where the Bitcoin Core node is and how to authenticate to it. Bound from the "BitcoinCore"
    /// section of appsettings.json, and overridable by environment variables the usual way
    /// (BitcoinCore__RpcPassword=... and so on).
    ///
    /// Core offers two ways in and this supports both:
    ///
    ///   cookie - the default. Core writes a .cookie file into its data directory each time it
    ///            starts, holding "__cookie__:&lt;random password&gt;". Nothing to configure, but the
    ///            explorer has to be able to read that file, so it only works on the same machine.
    ///   user   - rpcuser / rpcpassword (or rpcauth) in bitcoin.conf. Works remotely.
    ///
    /// Leave RpcUser empty and the cookie file is used.
    /// </summary>
    public sealed class CoreRpcOptions
    {
        public const string SectionName = "BitcoinCore";

        /// <summary>Base URL of the node's RPC interface. 8332 mainnet, 18332 testnet.</summary>
        public string Url { get; set; } = "http://127.0.0.1:8332/";

        /// <summary>rpcuser from bitcoin.conf. Empty means use the cookie file instead.</summary>
        public string RpcUser { get; set; } = "";

        public string RpcPassword { get; set; } = "";

        /// <summary>
        /// Path to Core's .cookie file, used when RpcUser is empty. The default is where Core puts
        /// it on Windows; on Linux it is ~/.bitcoin/.cookie.
        /// </summary>
        public string CookieFilePath { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bitcoin", ".cookie");

        /// <summary>How long to wait on one RPC call. A cold getblock can take a moment.</summary>
        public int TimeoutSeconds { get; set; } = 30;
    }
}
