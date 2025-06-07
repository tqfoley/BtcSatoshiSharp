using System;

namespace SatoshiSharpLib
{
    public class WalletTransactionSpend
    {
        public string sourceAddress;
        public string destinationAddress;
        public UInt64 amountSats;
        public Wallet sourceWallet;
        public Wallet destinationWallet;
    }

    public class WalletTransaction
    {
        public string transactionHash;
        public UInt64 blockNumber;
        public List<WalletTransactionSpend> spends;
    }

    public class Wallet
    {
        public int g;
        public string AddressHex;
        public string AddressBase58;

        public UInt64 totalSpentSats;
        public UInt64 currentSpendableSats;
        public UInt64 totalReceivedSats;

        public UInt64 totalSentTransactionsCount;
        public UInt64 totalReceivedTransactionsCount;

        public UInt64 totalUniqueSentTransactionsCount; // hard to calculate need to search transactions
        public UInt64 totalUniqueReceivedTransactionsCount; // hard to calculate need to search transactions

        public List<WalletTransaction> transactions;

    }
}