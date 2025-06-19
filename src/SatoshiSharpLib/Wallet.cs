using System;

namespace SatoshiSharpLib
{
    public class WalletTransactionSpend
    {
        public required string SourceAddress { get; set; }
        public required string DestinationAddress { get; set; }
        public required UInt64 AmountSats { get; set; }
        public required Wallet SourceWallet { get; set; }
        public required Wallet DestinationWallet { get; set; }
    }

    public class WalletTransaction
    {
        public required string TransactionHash { get; set; }
        public required UInt64 BlockNumber { get; set; }
        public required List<WalletTransactionSpend> Spends { get; set; }
    }

    public class Wallet
    {
        public Wallet()
        {
            AddressHex = string.Empty;
            AddressBase58 = string.Empty;
            Transactions = new List<WalletTransaction>();
        }
        
        public required string AddressHex { get; set; }
        public required string AddressBase58 { get; set; }

        public required List<WalletTransaction> Transactions { get; set; }

        public UInt64 TotalSpentSats { get; set; }
        public UInt64 CurrentSpendableSats { get; set; }
        public UInt64 TotalReceivedSats { get; set; }

        public UInt64 TotalSentTransactionsCount { get; set; }
        public UInt64 TotalReceivedTransactionsCount { get; set; }

        public UInt64 TotalUniqueSentTransactionsCount { get; set; } // hard to calculate need to search transactions
        public UInt64 TotalUniqueReceivedTransactionsCount { get; set; } // hard to calculate need to search transactions
    }
} 