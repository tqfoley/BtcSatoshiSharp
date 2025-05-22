// See https://aka.ms/new-console-template for more information
//Console.WriteLine("Hello, World!");

using SatoshiSharpLib;
using System;


namespace main
{
    public class SatoshiSharp
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Go!");

            Wallet wallet = new Wallet();
            wallet.g = 6;
        
            Console.WriteLine("End\n");
        }
    }
}
