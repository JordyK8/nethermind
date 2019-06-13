﻿/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Encoding;
using Nethermind.KeyStore;
using Nethermind.Logging;
using Nethermind.Runner.Config;
using Nethermind.Wallet;

namespace Nethermind.Runner.Runners
{
    public class HiveRunner : IRunner
    {
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IBlockTree _blockTree;
        private readonly IBlockchainProcessor _blockchainProcessor;
        private readonly HiveWallet _wallet;
        private readonly ILogger _logger;
        private readonly IConfigProvider _configurationProvider;

        public HiveRunner(
            IBlockTree blockTree,
            IBlockchainProcessor blockchainProcessor,
            HiveWallet wallet,
            IJsonSerializer jsonSerializer,
            IConfigProvider configurationProvider,
            ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _jsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));
            _blockchainProcessor = blockchainProcessor ?? throw new ArgumentNullException(nameof(blockchainProcessor));
            _configurationProvider = configurationProvider ?? throw new ArgumentNullException(nameof(configurationProvider));
        }

        public Task Start()
        {
            _logger.Info("Ethereum");
            var hiveConfig = _configurationProvider.GetConfig<IHiveConfig>();
            _blockchainProcessor.Start();
            ListEnvironmentVariables();
            InitializeKeys(hiveConfig.KeysDir);
            InitializeChain(hiveConfig.ChainFile);
            InitializeBlocks(hiveConfig.BlocksDir);
            _logger.Info("Ethereum initialization completed");
            return Task.CompletedTask;
        }

        private void ListEnvironmentVariables()
        {
// # This script assumes the following environment variables:
// #  - HIVE_BOOTNODE       enode URL of the remote bootstrap node
// #  - HIVE_TESTNET        whether testnet nonces (2^20) are needed
// #  - HIVE_NODETYPE       sync and pruning selector (archive, full, light)
// #  - HIVE_FORK_HOMESTEAD block number of the DAO hard-fork transition
// #  - HIVE_FORK_DAO_BLOCK block number of the DAO hard-fork transition
// #  - HIVE_FORK_DAO_VOTE  whether the node support (or opposes) the DAO fork
// #  - HIVE_MINER          address to credit with mining rewards (single thread)
// #  - HIVE_MINER_EXTRA    extra-data field to set for newly minted blocks
            string[] variableNames = new[] {"HIVE_BOOTNODE", "HIVE_TESTNET", "HIVE_NODETYPE", "HIVE_FORK_HOMESTEAD", "HIVE_FORK_DAO_BLOCK", "HIVE_FORK_DAO_VOTE", "HIVE_MINER", "HIVE_MINER_EXTRA"};
            foreach (string variableName in variableNames)
            {
                if(_logger.IsInfo) _logger.Info($"{variableName}: {Environment.GetEnvironmentVariable(variableName)}");
            }
        }

        public async Task StopAsync()
        {
            await Task.CompletedTask;
        }

        private void InitializeChain(string chainFile)
        {
            if (!File.Exists(chainFile))
            {
                if (_logger.IsInfo) _logger.Info($"Chain file does not exist: {chainFile}, skipping");
                return;
            }

            var chainFileContent = File.ReadAllBytes(chainFile);
            var context = new Rlp.DecoderContext(chainFileContent);
            var blocks = new List<Block>();
            
            if (_logger.IsInfo) _logger.Info($"Loading blocks from {chainFile}");
            while (context.ReadNumberOfItemsRemaining() > 0)
            {
                context.PeekNextItem();
                Block block = Rlp.Decode<Block>(context);
                if (_logger.IsInfo) _logger.Info($"Reading a chain.rlp block {block.ToString(Block.Format.Short)}");
                blocks.Add(block);
            }

            for (int i = 0; i < blocks.Count; i++)
            {
                Block block = blocks[i];
                if (_logger.IsInfo) _logger.Info($"Processing a chain.rlp block {block.ToString(Block.Format.Short)}");
                ProcessBlock(block);
            }
        }

        private void InitializeBlocks(string blocksDir)
        {
            if (!Directory.Exists(blocksDir))
            {
                if (_logger.IsInfo) _logger.Info($"Blocks dir does not exist: {blocksDir}, skipping");
                return;
            }

            if (_logger.IsInfo) _logger.Info($"Loading blocks from {blocksDir}");
            var files = Directory.GetFiles(blocksDir).OrderBy(x => x).ToArray();
            var blocks = files.Select(x => new {File = x, Block = DecodeBlock(x)}).OrderBy(x => x.Block.Header.Number).ToArray();
            foreach (var block in blocks)
            {
                if (_logger.IsInfo) _logger.Info($"Processing block file: {block.File} - {block.Block.ToString(Block.Format.Short)}");
                ProcessBlock(block.Block);
            }
        }

        private Block DecodeBlock(string file)
        {
            var fileContent = File.ReadAllBytes(file);
            var blockRlp = new Rlp(fileContent);

            return Rlp.Decode<Block>(blockRlp);
        }

        private void ProcessBlock(Block block)
        {
            try
            {
                _blockTree.SuggestBlock(block);
            }
            catch (InvalidBlockException e)
            {
                _logger.Error($"Invalid block: {block.Hash}, ignoring", e);
            }
        }

        private void InitializeKeys(string keysDir)
        {
            if (!Directory.Exists(keysDir))
            {
                if (_logger.IsInfo) _logger.Info($"Keys dir does not exist: {keysDir}, skipping");
                return;
            }

            if (_logger.IsInfo) _logger.Info($"Loading keys from {keysDir}");
            var files = Directory.GetFiles(keysDir);
            foreach (var file in files)
            {
                if (_logger.IsInfo) _logger.Info($"Processing key file: {file}");
                var fileContent = File.ReadAllText(file);
                var keyStoreItem = _jsonSerializer.Deserialize<KeyStoreItem>(fileContent);
                _wallet.Add(new Address(keyStoreItem.Address));
            }
        }
    }
}