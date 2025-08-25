# BtcSatoshiSharp
C Sharp implementation of some bitcoin functionality

Put blockchain data in a directory labeled as btcblockdata, currently it is best to use XOR data of all zeros so the blockchain data is not altered (to prevent malicious code in blockchain full nodes store the blck data differently and XOR it with the contents of xor.dat which is eight random bytes)
