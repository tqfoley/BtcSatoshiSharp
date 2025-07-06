using System;

namespace SatoshiSharpLib
{
    public class WalletAddress
    {
        public UInt64 first { set; get; } // store in uint64 bit for speed
        public UInt64 second { set; get; }
        public UInt64 third { set; get; }
        public UInt64 last { set; get; }

        public string getBase58()
        {
            return Helpers.Base58Encode(Helpers.HexToBytes(getHex()));
        }

        public string getHex()
        {
            string lastString = last.ToString("X8");
            if (lastString.Length < 3) {
                lastString.PadRight(14, '0');
            }
            string ret = first.ToString("X8").PadLeft(16, '0') + second.ToString("X8").PadLeft(16, '0') + third.ToString("X8").PadLeft(16, '0') +
                lastString.PadLeft(16, '0');
            return ret.Substring(0,50);
        }

        public static string UlongToHexString(ulong value)
        {
            return value.ToString("X8").ToLower(); // 8 hex digits, lowercase
        }

        public WalletAddress(UInt64 f, UInt64 s, UInt64 t, UInt64 l)
        {
            first = (UInt64)f;
            second = (UInt64)s;
            third = (UInt64)t;
            last = (UInt64)l;
        }

        public void SetWalletAddress(string hex)
        {

        }

        public WalletAddress(string hexOrBase58)
        {
            string hex;
            if (hexOrBase58.Length != 50)
            {
                hex = Helpers.BitcoinBase58AddressToHexString(hexOrBase58);
            }
            else
            {
                hex = hexOrBase58;
            }

            first = Convert.ToUInt64(hex.Substring(0,16), 16);
            second = Convert.ToUInt64(hex.Substring(16, 16), 16);
            third = Convert.ToUInt64(hex.Substring(32, 16), 16);
            string lastString = hex.Substring(48, hex.Length - 48);
            lastString = lastString.PadRight(16, '0');
            last = Convert.ToUInt64(lastString, 16);
        }
    }

    //public class address trevor 
    public class Spend
    {
        //public required string SourceAddress { get; set; }
        //public required string DestinationAddress { get; set; }
        //public WalletAddress Source { get; set; }
        //public WalletAddress Destination { get; set; }
        public  UInt64 AmountSats { get; set; }
        public  Wallet SourceWallet { get; set; }
        public  Wallet DestinationWallet { get; set; }

        public Spend(WalletAddress source, WalletAddress destination, UInt64 sats)
        {
            SourceWallet = StateWallets.getWallet(source);
            DestinationWallet = StateWallets.getWallet(destination);

            DestinationWallet.TotalReceivedSats += sats;
            DestinationWallet.TotalReceivedTransactionsCount += 1;
            DestinationWallet.CurrentSpendableSats += sats;

            SourceWallet.TotalSpentSats += sats;
            SourceWallet.TotalSentTransactionsCount += 1;
            SourceWallet.CurrentSpendableSats -= sats;

            var spends = new List<Spend>();
            spends.Add(this);

            SourceWallet.addTransaction(new Transaction { BlockNumber =0, Spends = spends});
            //todo DestinationWallet.addTransaction(new Transaction { BlockNumber = 0,  = spends });

            AmountSats = sats;
        }
    }


    public class Transaction
    {
        //public required string TransactionHash { get; set; }
        public required UInt64 BlockNumber { get; set; }
        public required List<Spend> Spends { get; set; }
    }

    public class StateWallets
    {
        public static List<Wallet> Wallets;
        public static int blockNumber { set; get; }
        public static UInt64 totalSats;

        public static Wallet getWallet(WalletAddress wa)
        {
            foreach (Wallet w in Wallets)
            {
                if (w.Address.first == wa.first && w.Address.second == wa.second)
                {
                    if (w.Address.third == wa.third && w.Address.last == wa.last)
                    {
                        return w;
                    }
                }
            }
            Console.WriteLine("wallet not found");
            Console.WriteLine("wallet not found");
            Console.WriteLine("wallet not found");
            return null;
        }

        public static void PrintAllWalletsOrdered()
        {
            foreach (Wallet w in Wallets.OrderBy(x => x.CurrentSpendableSats).ToList())
            {
                Console.WriteLine(w.Address.getHex());
                Console.WriteLine("  " + w.CurrentSpendableSats / Helpers.SatsInBTC);
            }
        }
    }

    public class Wallet
    {
        public Wallet(WalletAddress wa)
        {
            Address = wa;
            Transactions = new List<Transaction>();
        }

        public void addTransaction(Transaction transaction)
        {

        }

        public void addTransactions(List<Transaction> transactions)
        {

        }

        //public required string AddressHex { get; set; }
        //public required string AddressBase58 { get; set; }

        public WalletAddress Address { get; set; }

        public List<Transaction> Transactions { get; set; }

        public UInt64 TotalSpentSats { get; set; }
        public UInt64 CurrentSpendableSats { get; set; }
        public UInt64 TotalReceivedSats { get; set; }

        public UInt64 TotalSentTransactionsCount { get; set; }
        public UInt64 TotalReceivedTransactionsCount { get; set; }

        public UInt64 TotalUniqueSentTransactionsCount { get; set; } // hard to calculate need to search transactions
        public UInt64 TotalUniqueReceivedTransactionsCount { get; set; } // hard to calculate need to search transactions
    }
} 