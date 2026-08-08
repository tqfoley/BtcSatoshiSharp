namespace btcSatoshiSharpWebExplorer.CoreRpc
{
    /// <summary>
    /// An error the node itself reported - the HTTP call succeeded and came back holding
    /// {"result": null, "error": {"code": -5, "message": "Block not found"}}.
    ///
    /// Code is Core's own RPC error code, not an HTTP status. The ones worth recognising:
    ///
    ///   -5   invalid address or key, which is also what a missing block or transaction returns
    ///   -8   invalid parameter
    ///   -32601 no such method
    ///   -28  still starting up and loading the block index
    ///
    /// A node that cannot be reached at all throws HttpRequestException instead, which is a
    /// different problem and reads differently to the user.
    /// </summary>
    public sealed class CoreRpcException : Exception
    {
        public int Code { get; }

        public CoreRpcException(int code, string message) : base(message)
        {
            Code = code;
        }

        /// <summary>
        /// True when the node is saying "I do not have that", rather than "you asked wrongly".
        /// Worth separating because it is a 404 to a browser, not a 500.
        /// </summary>
        public bool IsNotFound
        {
            get
            {
                if (Code == -5)
                {
                    return true;
                }
                return false;
            }
        }
    }
}
