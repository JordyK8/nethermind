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

using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Facade;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.Db.Blooms;
using Nethermind.Int256;
using Nethermind.KeyStore;
using Nethermind.Specs;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using Nethermind.Wallet;
using Newtonsoft.Json;
using NSubstitute;

namespace Nethermind.JsonRpc.Test.Modules
{
    public class TestRpcBlockchain : TestBlockchain
    {
        public IEthModule EthModule { get; private set; }
        public IBlockchainBridge Bridge { get; private set; }
        public ITxSender TxSender { get; private set; }
        public ILogFinder LogFinder { get; private set; }
        public IKeyStore KeyStore { get; } = new MemKeyStore(TestItem.PrivateKeys);
        public IWallet TestWallet { get; } = new DevKeyStoreWallet(new MemKeyStore(TestItem.PrivateKeys), LimboLogs.Instance);

        protected TestRpcBlockchain(SealEngineType sealEngineType)
            : base(sealEngineType)
        {
        }
 
        public static Builder ForTest(SealEngineType sealEngineType)
        {
            return new Builder(sealEngineType);
        }

        public class Builder
        {
            public Builder(SealEngineType sealEngineType)
            {
                _blockchain = new TestRpcBlockchain(sealEngineType);
            }
            
            private TestRpcBlockchain _blockchain;
            
            public Builder WithBlockchainBridge(IBlockchainBridge blockchainBridge)
            {
                _blockchain.Bridge = blockchainBridge;
                return this;
            }
            
            public Builder WithBlockFinder(IBlockFinder blockFinder)
            {
                _blockchain.BlockFinder = blockFinder;
                return this;
            }
            
            public Builder WithTxSender(ITxSender txSender)
            {
                _blockchain.TxSender = txSender;
                return this;
            }

            public Builder WithGenesisBlockBuilder(BlockBuilder blockBuilder)
            {
                _blockchain.GenesisBlockBuilder = blockBuilder;
                return this;
            }
            
            public async Task<TestRpcBlockchain> Build(ISpecProvider specProvider = null, UInt256? initialValues = null)
            {
                return (TestRpcBlockchain)(await _blockchain.Build(specProvider, initialValues));
            }
        }

        protected override async Task<TestBlockchain> Build(ISpecProvider specProvider = null, UInt256? initialValues = null)
        {
            BloomStorage bloomStorage = new BloomStorage(new BloomConfig(), new MemDb(), new InMemoryDictionaryFileStoreFactory());
            specProvider ??= MainnetSpecProvider.Instance;
            await base.Build(specProvider, initialValues);
            IFilterStore filterStore = new FilterStore();
            IFilterManager filterManager = new FilterManager(filterStore, BlockProcessor, TxPool, LimboLogs.Instance);

            ReceiptsRecovery receiptsRecovery = new ReceiptsRecovery(new EthereumEcdsa(specProvider.ChainId, LimboLogs.Instance), specProvider);
            LogFinder = new LogFinder(BlockTree, ReceiptStorage, bloomStorage, LimboLogs.Instance, receiptsRecovery);
            
            ReadOnlyTxProcessingEnv processingEnv = new ReadOnlyTxProcessingEnv(
                new ReadOnlyDbProvider(DbProvider, false),
                new ReadOnlyBlockTree(BlockTree),
                SpecProvider,
                LimboLogs.Instance);
            
            Bridge ??= new BlockchainBridge(processingEnv, TxPool, ReceiptStorage, filterStore, filterManager, EthereumEcdsa, Timestamper, LogFinder, false, false);
            BlockFinder ??= BlockTree;
            
            ITxSigner txSigner = new WalletTxSigner(TestWallet, specProvider?.ChainId ?? 0);
            ITxSealer txSealer0 = new TxSealer(txSigner, Timestamper);
            ITxSealer txSealer1 = new NonceReservingTxSealer(txSigner, Timestamper, TxPool);
            TxSender ??= new TxPoolSender(TxPool, txSealer0, txSealer1);
            
            EthModule = new EthModule(
                new JsonRpcConfig(),
                Bridge,
                BlockFinder,
                StateReader,
                TxPool,
                TxSender,
                TestWallet,
                LimboLogs.Instance);
            
            return this;
        }

        public string TestEthRpc(string method, params string[] parameters)
        {
            return RpcTest.TestSerializedRequest(EthModuleFactory.Converters, EthModule, method, parameters);
        }

        public string TestSerializedRequest<T>(T module, string method, params string[] parameters) where T : class, IModule
        {
            return RpcTest.TestSerializedRequest(new JsonConverter[0], module, method, parameters);
        }
    }
}
