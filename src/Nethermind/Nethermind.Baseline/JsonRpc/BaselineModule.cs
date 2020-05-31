//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Abstractions;
using System.Threading.Tasks;
using System.Timers;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Facade;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.TxPool;

namespace Nethermind.Baseline.JsonRpc
{
    public class BaselineModule : IBaselineModule
    {
        private const int TruncationLength = 5;
        
        private readonly IAbiEncoder _abiEncoder;
        private readonly IFileSystem _fileSystem;
        private readonly IDb _baselineDb;
        private readonly ILogger _logger;
        private readonly ITxPoolBridge _txPoolBridge;

        private ConcurrentDictionary<Address, BaselineTree> _baselineTrees;
        private BaselineMetadata _metadata;

        private Timer _timer;

        public BaselineModule(ITxPoolBridge txPoolBridge, IAbiEncoder abiEncoder, IFileSystem fileSystem, IDb baselineDb, ILogManager logManager)
        {
            _abiEncoder = abiEncoder ?? throw new ArgumentNullException(nameof(abiEncoder));
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _baselineDb = baselineDb ?? throw new ArgumentNullException(nameof(baselineDb));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _txPoolBridge = txPoolBridge ?? throw new ArgumentNullException(nameof(txPoolBridge));
            
            _metadata = LoadMetadata();
            _baselineTrees = InitTrees();
            _timer = InitTimer();
        }

        private Timer InitTimer()
        {
            Timer timer = new Timer();
            timer.Interval = 1000;
            timer.Elapsed += TimerOnElapsed;
            timer.AutoReset = false;
            return timer;
        }

        private ConcurrentDictionary<Address, BaselineTree> InitTrees()
        {
            ConcurrentDictionary<Address, BaselineTree> trees = new ConcurrentDictionary<Address, BaselineTree>();
            foreach (Address trackedTree in _metadata.TrackedTrees)
            {
                TryAddTree(trackedTree);
            }

            return trees;
        }
        
        private BaselineMetadata LoadMetadata()
        {
            byte[] serializedMetadata = _baselineDb[new byte[0]];
            return serializedMetadata == null
                ? new BaselineMetadata()
                : new BaselineMetadata(Rlp.DecodeArray<Address>(new RlpStream(serializedMetadata)));
        }

        private bool TryAddTree(Address trackedTree)
        {
            ShaBaselineTree tree = new ShaBaselineTree(_baselineDb, trackedTree.Bytes, TruncationLength);
            return _baselineTrees.TryAdd(trackedTree, tree);
        }

        private void TimerOnElapsed(object sender, ElapsedEventArgs e)
        {
            _timer.Enabled = true;
        }

        public Task<ResultWrapper<Keccak>> baseline_insertLeaf(Address address, Address contractAddress, Keccak hash)
        {
            if (hash == Keccak.Zero)
            {
                return Task.FromResult(ResultWrapper<Keccak>.Fail("Cannot insert zero hash", ErrorCodes.InvalidInput));
            }
            
            var txData = _abiEncoder.Encode(AbiEncodingStyle.IncludeSignature, ContractMerkleTree.InsertLeafAbiSig, hash);

            Transaction tx = new Transaction();
            tx.Value = 0;
            tx.Data = txData;
            tx.To = contractAddress;
            tx.SenderAddress = address;
            tx.GasLimit = 1000000;
            tx.GasPrice = 0.GWei();

            Keccak txHash = _txPoolBridge.SendTransaction(tx, TxHandlingOptions.ManagedNonce);
            return Task.FromResult(ResultWrapper<Keccak>.Success(txHash));
        }

        public Task<ResultWrapper<Keccak>> baseline_insertLeaves(Address address, Address contractAddress, params Keccak[] hashes)
        {
            for (int i = 0; i < hashes.Length; i++)
            {
                if (hashes[i] == Keccak.Zero)
                {
                    return Task.FromResult(ResultWrapper<Keccak>.Fail("Cannot insert zero hash", ErrorCodes.InvalidInput));
                }
            }

            var txData = _abiEncoder.Encode(AbiEncodingStyle.IncludeSignature, ContractMerkleTree.InsertLeavesAbiSig, new object[] {hashes});

            Transaction tx = new Transaction();

            tx.Value = 0;
            tx.Data = txData;
            tx.To = contractAddress;
            tx.SenderAddress = address;
            tx.GasLimit = 1000000;
            tx.GasPrice = 0.GWei();

            Keccak txHash = _txPoolBridge.SendTransaction(tx, TxHandlingOptions.ManagedNonce);

            return Task.FromResult(ResultWrapper<Keccak>.Success(txHash));
        }

        /// <summary>
        /// We retrieve the line 3 from here (bytecode) 
        /// 
        /// ======= MerkleTreeSHA.sol:MerkleTreeSHA =======
        /// Binary: 
        /// 608060405234801561001057600080fd5b5061080980610(...)
        /// </summary>
        /// <param name="contract"></param>
        /// <returns></returns>
        private async Task<byte[]> GetContractBytecode(string contract)
        {
            string[] contractBytecode = await _fileSystem.File.ReadAllLinesAsync($"contracts/{contract}.bin");
            if (contractBytecode.Length < 4)
            {
                throw new IOException("Bytecode not found");
            }

            if (_logger.IsInfo) _logger.Info($"Loading bytecode of {contractBytecode[1]}");
            return Bytes.FromHexString(contractBytecode[3]);
        }

        public async Task<ResultWrapper<Keccak>> baseline_deploy(Address address, string contractType)
        {
            byte[] bytecode;
            try
            {
                bytecode = await GetContractBytecode(contractType);
            }
            catch (IOException)
            {
                return ResultWrapper<Keccak>.Fail($"{contractType} bytecode could not be loaded.", ErrorCodes.ResourceUnavailable);
            }

            Transaction tx = new Transaction();
            tx.Value = 0;
            tx.Init = bytecode;
            tx.GasLimit = 2000000;
            tx.GasPrice = 20.GWei();
            tx.SenderAddress = address;

            Keccak txHash = _txPoolBridge.SendTransaction(tx, TxHandlingOptions.ManagedNonce);

            _logger.Info($"Sent transaction at price {tx.GasPrice} to {tx.SenderAddress}");
            _logger.Info($"Contract {contractType} has been deployed");

            return ResultWrapper<Keccak>.Success(txHash);
        }

        public Task<ResultWrapper<BaselineTreeNode[]>> baseline_getSiblings(Address contractAddress, long leafIndex)
        {
            if (leafIndex > MerkleTree.MaxLeafIndex || leafIndex < 0L)
            {
                return Task.FromResult(ResultWrapper<BaselineTreeNode[]>.Fail($"{leafIndex} is not a valid leaf index", ErrorCodes.InvalidInput));
            }

            bool isTracked = _baselineTrees.TryGetValue(contractAddress, out BaselineTree? tree);
            if (!isTracked)
            {
                return Task.FromResult(ResultWrapper<BaselineTreeNode[]>.Fail($"{contractAddress} tree is not tracked", ErrorCodes.InvalidInput));
            }

            return Task.FromResult(ResultWrapper<BaselineTreeNode[]>.Success(tree!.GetProof((uint) leafIndex)));
        }

        public Task<ResultWrapper<bool>> baseline_track(Address contractAddress)
        {
            // can potentially warn user if tree is not deployed at the address

            if (_baselineTrees.TryAdd(contractAddress, new ShaBaselineTree(_baselineDb, contractAddress.Bytes)))
            {
                return Task.FromResult(ResultWrapper<bool>.Success(true));
            }

            return Task.FromResult(ResultWrapper<bool>.Fail($"{contractAddress} is already tracked", ErrorCodes.InvalidInput));
        }
    }
}