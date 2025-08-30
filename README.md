# BtcSatoshiSharp

A C# implementation of Bitcoin blockchain parsing and validation functionality.

## Overview

BtcSatoshiSharp is a .NET library and console application for reading and parsing Bitcoin blockchain data files. It provides functionality for:

- Parsing Bitcoin block headers and transactions
- Validating block hashes and merkle roots
- Bitcoin address generation and validation
- Wallet management functionality

## Project Structure

- **src/SatoshiSharpLib** - Core library containing blockchain parsing logic
- **src/ConsoleApp** - Console application demonstrating library usage
- **test/SatoshiSharpTest** - Unit tests using xUnit

## Prerequisites

- .NET 8.0 SDK or later
- Bitcoin blockchain data files (blk*.dat format)

## Building

```bash
# Build the solution
dotnet build

# Run tests
dotnet test

# Run the console application
dotnet run --project src/ConsoleApp/ConsoleApp.csproj
```

## Configuration

Create a `settings.json` file in the project root:

```json
{
  "BlockChainDataDirectory": "path/to/blockchain/data",
  "version": "1.0.0",
  "logging": {
    "filePath": "logs/app.log"
  }
}
```

## Blockchain Data Setup

1. Create a directory for blockchain data (e.g., `btcblockdata`)
2. Place Bitcoin blockchain `.dat` files in this directory
3. Create an `xor.dat` file with 8 bytes for XOR encryption (use all zeros for unencrypted data)

**Note:** For security, full nodes typically XOR blockchain data with random bytes to prevent potential malicious code execution from blockchain data. This implementation supports XOR decryption with an 8-byte key.

## Key Features

### Block Parsing
- Reads and validates Bitcoin block headers (80 bytes)
- Verifies magic bytes (0xF9BEB4D9)
- Calculates and validates block hashes using double SHA-256
- Validates merkle roots for transaction verification

### Bitcoin Address Generation
- Implements standard Bitcoin address generation:
  - Public Key → SHA-256 → RIPEMD-160 → Base58Check encoding
- Supports mainnet addresses (version byte 0x00)

### Transaction Processing
- Parses transaction inputs and outputs
- Validates transaction structure
- Tracks wallet balances

## Dependencies

- **BouncyCastle** - Cryptographic operations
- **xUnit** - Unit testing framework
- **System.Text.Json** - Configuration parsing