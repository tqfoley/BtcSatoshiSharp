# BtcSatoshiSharp

![.NET](https://img.shields.io/badge/.NET-8.0-blue)
![License](https://img.shields.io/badge/license-GPL--3.0-green)
![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS-lightgrey)

A C# implementation of Bitcoin blockchain parsing and validation functionality.

## Overview

BtcSatoshiSharp is a .NET library and console application for reading and parsing Bitcoin blockchain data files. It provides low-level access to Bitcoin blockchain data with full validation capabilities.

**Key Features:**
- Parse Bitcoin block headers and transactions from raw `.dat` files
- Validate block hashes using double SHA-256
- Verify merkle roots for transaction integrity
- Generate and validate Bitcoin addresses (Base58Check encoding)
- Track wallet balances and transactions
- Support for XOR-encrypted blockchain data files

## Table of Contents

- [Quick Start](#quick-start)
- [Project Structure](#project-structure)
- [Prerequisites](#prerequisites)
- [Installation](#installation)
- [Configuration](#configuration)
- [Usage Examples](#usage-examples)
- [Blockchain Data Setup](#blockchain-data-setup)
- [Key Features](#key-features)
- [API Reference](#api-reference)
- [Dependencies](#dependencies)
- [License](#license)

## Quick Start

```csharp
using SatoshiSharpLib;

// Initialize the block reader
BlockReader reader = new BlockReader();
byte[] xorKey = new byte[8]; // Use all zeros for unencrypted data

// Read blockchain data
reader.ReadBlkDataFile("path/to/blk00000_ordered.dat", xorKey, null, limit: 1000);

// Access parsed blocks
foreach (var block in reader.blocksInDataFile)
{
    Console.WriteLine($"Block Hash: {Helpers.ByteArrayToHexString(block.header.Hash)}");
    Console.WriteLine($"Transaction Count: {block.transactions.Count}");
}
```

## Project Structure

```
BtcSatoshiSharp/
├── src/
│   ├── SatoshiSharpLib/      # Core blockchain parsing library
│   └── ConsoleApp/            # Example console application
├── test/
│   └── SatoshiSharpTest/      # Unit tests (xUnit)
└── settings.json              # Configuration file
```

## Prerequisites

- **.NET 8.0 SDK** or later ([Download](https://dotnet.microsoft.com/download))
- **Bitcoin blockchain data files** in `.dat` format
- Approximately 500+ GB of disk space for full blockchain data

## Installation

### Clone the Repository

```bash
git clone https://github.com/yourusername/BtcSatoshiSharp.git
cd BtcSatoshiSharp
```

### Build the Project

```bash
# Restore dependencies and build
dotnet build

# Run unit tests
dotnet test

# Run the console application
dotnet run --project src/ConsoleApp/ConsoleApp.csproj
```

## Configuration

Create a `settings.json` file in the project root directory:

```json
{
  "BlockChainDataDirectory": "C:/btcblockdata",
  "version": "1.0.0",
  "logging": {
    "filePath": "logs/app.log"
  }
}
```

**Configuration Options:**
- `BlockChainDataDirectory` - Absolute or relative path to blockchain data directory
- `version` - Application version identifier
- `logging.filePath` - Path for application log files

## Usage Examples

### 1. Bitcoin Address Generation

Generate a Bitcoin address from a public key:

```csharp
using SatoshiSharpLib;
using Org.BouncyCastle.Utilities.Encoders;

// Parse hex-encoded public key
string pubKeyHex = "0496b538e853519c726a2c91e61ec11600ae1390813a627c66fb8be7947be63c52...";
byte[] pubKeyBytes = Hex.Decode(pubKeyHex);

// Generate Bitcoin address
string address = Helpers.PublicKeyToBitcoinAddress(pubKeyBytes);
Console.WriteLine($"Bitcoin Address: {address}");
// Output: 12c6DSiU4Rq3P4ZxziKxzrL5LmMBrzjrJX
```

### 2. Reading and Validating Blocks

Parse and validate blockchain data:

```csharp
using SatoshiSharpLib;

BlockReader reader = new BlockReader();
byte[] xorKey = File.ReadAllBytes("path/to/xor.dat");

// Read first 1000 blocks
reader.ReadBlkDataFile("path/to/blk00000_ordered.dat", xorKey, null, limit: 1000);

// Validate each block
foreach (var block in reader.blocksInDataFile)
{
    // Verify block hash meets difficulty target
    bool isValid = block.header.ValidateHash();

    // Verify merkle root
    bool merkleValid = block.ValidateMerkleRoot();

    Console.WriteLine($"Block Height: {block.Height}");
    Console.WriteLine($"Hash Valid: {isValid}, Merkle Valid: {merkleValid}");
}
```

### 3. Analyzing Transactions

Extract transaction information:

```csharp
foreach (var block in reader.blocksInDataFile)
{
    foreach (var transaction in block.transactions)
    {
        Console.WriteLine($"Transaction ID: {Helpers.ByteArrayToHexString(transaction.Hash)}");
        Console.WriteLine($"Inputs: {transaction.Inputs.Count}");
        Console.WriteLine($"Outputs: {transaction.Outputs.Count}");

        // Analyze outputs
        foreach (var output in transaction.Outputs)
        {
            Console.WriteLine($"  Value: {output.Value} satoshis");
            Console.WriteLine($"  Script: {Helpers.ByteArrayToHexString(output.ScriptPubKey)}");
        }
    }
}
```

## Blockchain Data Setup

### Step 1: Obtain Blockchain Data

You can obtain Bitcoin blockchain data files (`.dat` format) by:

1. **Running Bitcoin Core**: Sync a full node and locate the `blocks` directory
   - Windows: `%APPDATA%\Bitcoin\blocks\`
   - Linux: `~/.bitcoin/blocks/`
   - macOS: `~/Library/Application Support/Bitcoin/blocks/`

2. **Download from Archive**: Use blockchain data archives (if available)

### Step 2: Prepare Data Directory

```bash
# Create directory structure
mkdir btcblockdata
cd btcblockdata

# Copy blockchain data files
cp /path/to/bitcoin/blocks/blk*.dat .

# Create XOR key file (8 bytes)
echo -n -e '\x00\x00\x00\x00\x00\x00\x00\x00' > xor.dat
```

### Step 3: XOR Encryption Support

**Important:** Bitcoin Core XORs blockchain data with an 8-byte key stored in `xor.dat` to prevent accidental execution of embedded data.

- **For unencrypted data**: Use 8 zero bytes (`0x00 0x00 0x00 0x00 0x00 0x00 0x00 0x00`)
- **For encrypted data**: Copy the `xor.dat` file from your Bitcoin Core data directory

```csharp
// Read XOR key
byte[] xorKey = File.ReadAllBytes("path/to/xor.dat");

// The library will automatically decrypt data during parsing
reader.ReadBlkDataFile("path/to/blk00000.dat", xorKey, null);
```

## Key Features

### Block Parsing
- **Block Header Validation**: Reads and validates 80-byte Bitcoin block headers
- **Magic Byte Verification**: Ensures data integrity by verifying magic bytes (`0xF9BEB4D9`)
- **Hash Validation**: Calculates and validates block hashes using double SHA-256
- **Merkle Root Verification**: Validates merkle roots to ensure transaction integrity
- **Chain Validation**: Verifies block linkage through previous block hash references

### Bitcoin Address Generation
Implements the complete Bitcoin address generation pipeline:

1. **Public Key** (ECDSA 65-byte format)
2. **SHA-256** hashing
3. **RIPEMD-160** hashing
4. **Version Byte Addition** (0x00 for mainnet)
5. **Checksum Calculation** (first 4 bytes of double SHA-256)
6. **Base58Check Encoding**

```
Public Key → SHA256 → RIPEMD160 → +Version → +Checksum → Base58 → Bitcoin Address
```

### Transaction Processing
- **Transaction Parsing**: Extracts inputs, outputs, and scripts from raw transaction data
- **Script Analysis**: Parses and interprets Bitcoin scripts (scriptPubKey, scriptSig)
- **Coinbase Detection**: Identifies coinbase transactions (block rewards)
- **Balance Tracking**: Monitors wallet balances across transactions
- **UTXO Management**: Tracks unspent transaction outputs

### Cryptographic Operations
- **Double SHA-256**: Bitcoin's standard hashing algorithm
- **RIPEMD-160**: Used in address generation
- **Base58Check**: Bitcoin-specific encoding for addresses
- **Merkle Tree**: Transaction verification using merkle proofs

## API Reference

### Core Classes

#### `BlockReader`
Main class for reading and parsing blockchain data files.

```csharp
public class BlockReader
{
    public List<Block> blocksInDataFile { get; set; }

    public void ReadBlkDataFile(string path, byte[] xorKey, Block lastBlock = null, int limit = int.MaxValue)
}
```

#### `Block`
Represents a complete Bitcoin block with header and transactions.

```csharp
public class Block
{
    public BlockHeader header { get; set; }
    public List<Transaction> transactions { get; set; }
    public int Height { get; set; }

    public bool ValidateMerkleRoot()
}
```

#### `BlockHeader`
Represents the 80-byte Bitcoin block header.

```csharp
public class BlockHeader
{
    public uint Version { get; set; }
    public byte[] PrevBlockHash { get; set; }
    public byte[] MerkleRoot { get; set; }
    public uint Timestamp { get; set; }
    public uint Bits { get; set; }
    public uint Nonce { get; set; }
    public byte[] Hash { get; set; }

    public bool ValidateHash()
}
```

#### `Helpers`
Utility class with cryptographic and encoding functions.

```csharp
public static class Helpers
{
    public static string ByteArrayToHexString(byte[] bytes)
    public static byte[] HexToBytes(string hex)
    public static string Base58Encode(byte[] input)
    public static byte[] Base58Decode(string input)
    public static string PublicKeyToBitcoinAddress(byte[] publicKey)
    public static string BitcoinBase58AddressToHexString(string address)
}
```

### Common Operations

#### Find Byte Patterns in Blockchain Files

```csharp
using main;

// Find specific block hash in blockchain file
string blockHashHex = "000000000019d6689c085ae165831e934ff763ae46a2a6c172b3f1b60a8ce26f";
byte[] pattern = Helpers.HexToBytes(Helpers.ReverseHexString(blockHashHex));

long position = ClaudeCode.FindBytePattern("path/to/blk00000.dat", pattern);
Console.WriteLine($"Block found at position: {position}");
```

## Dependencies

This project uses the following NuGet packages:

- **BouncyCastle.Cryptography** (v2.4.0+) - Cryptographic operations (RIPEMD-160, ECDSA)
- **xUnit** (v2.4.0+) - Unit testing framework
- **System.Text.Json** - JSON configuration parsing (built-in with .NET 8.0)

Install dependencies:

```bash
dotnet restore
```

## Troubleshooting

### Common Issues

**Issue**: `FileNotFoundException` when reading blockchain data
- **Solution**: Verify the path in `settings.json` points to the correct directory
- Ensure `.dat` files exist in the specified directory

**Issue**: `xor.dat` mismatch error
- **Solution**: Make sure the `xor.dat` file matches your Bitcoin Core installation
- For testing, use all zeros: `new byte[8]`

**Issue**: Out of memory when reading large blockchain files
- **Solution**: Use the `limit` parameter to read blocks incrementally
- Process blocks in batches

```csharp
// Read in batches of 10000 blocks
for (int i = 0; i < totalBlocks; i += 10000)
{
    reader.ReadBlkDataFile(path, xorKey, lastBlock, limit: 10000);
    // Process batch...
}
```

## Contributing

Contributions are welcome! Please feel free to submit pull requests or open issues for:

- Bug fixes
- Performance improvements
- Additional Bitcoin protocol features
- Documentation improvements
- Test coverage

## License

This project is licensed under the **GNU General Public License v3.0** (GPL-3.0).

See the [LICENSE](LICENSE) file for full details.

### Key Points:
- ✅ Free to use, modify, and distribute
- ✅ Source code must be made available
- ✅ Modifications must also be GPL-3.0 licensed
- ✅ No warranty provided

For more information, visit: https://www.gnu.org/licenses/gpl-3.0.en.html

---

**Disclaimer**: This software is provided for educational and research purposes. Use at your own risk. The authors are not responsible for any loss of funds or data.