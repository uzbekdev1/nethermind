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
using System.Collections.Generic;
using System.Diagnostics;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Trie.Pruning
{
    /// <summary>
    /// Trie store helps to manage trie commits block by block.
    /// If persistence and pruning are needed they have a chance to execute their behaviour on commits.  
    /// </summary>
    public class TrieStore : ITrieStore, IDisposable
    {
        public TrieStore(IKeyValueStore? keyValueStore, ILogManager? logManager)
            : this(keyValueStore, No.Pruning, Full.Archive, logManager) { }

        public TrieStore(
            IKeyValueStore? keyValueStore,
            IPruningStrategy? pruningStrategy,
            IPersistenceStrategy? persistenceStrategy,
            ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger<TrieStore>() ?? throw new ArgumentNullException(nameof(logManager));
            _keyValueStore = keyValueStore ?? throw new ArgumentNullException(nameof(keyValueStore));
            _pruningStrategy = pruningStrategy ?? throw new ArgumentNullException(nameof(pruningStrategy));
            _persistenceStrategy = persistenceStrategy ?? throw new ArgumentNullException(nameof(persistenceStrategy));
        }

        public long LastPersistedBlockNumber
        {
            get => _lastPersistedBlockNumber;
            private set
            {
                if (value != _lastPersistedBlockNumber)
                {
                    Metrics.LastPersistedBlockNumber = value;
                    _lastPersistedBlockNumber = value;
                    TriePersisted?.Invoke(this, new BlockNumberEventArgs(_lastPersistedBlockNumber));
                }
            }
        }
        
        public int CommittedNodesCount
        {
            get => _committedNodesCount;
            private set
            {
                Metrics.CommittedNodesCount = value;
                _committedNodesCount = value;
            }
        }

        public int PersistedNodesCount
        {
            get => _persistedNodesCount;
            private set
            {
                Metrics.PersistedNodeCount = value;
                _persistedNodesCount = value;
            }
        }

        public int CachedNodesCount
        {
            get
            {
                Metrics.CachedNodesCount = _nodeCache.Count;
                return _nodeCache.Count;
            }
        }

        public void CommitNode(long blockNumber, NodeCommitInfo nodeCommitInfo)
        {
            if (blockNumber < 0) throw new ArgumentOutOfRangeException(nameof(blockNumber));
            EnsureCommitListExistsForBlock(blockNumber);

            if (_logger.IsTrace) _logger.Trace($"Committing {nodeCommitInfo} at {blockNumber}");
            if (!nodeCommitInfo.IsEmptyBlockMarker)
            {
                TrieNode node = nodeCommitInfo.Node!;
                if (node!.Keccak == null)
                {
                    throw new PruningException($"The hash of {node} should be known at the time of committing.");
                }
                
                if (CurrentPackage == null)
                {
                    throw new PruningException($"{nameof(CurrentPackage)} is NULL when committing {node} at {blockNumber}.");
                }
                
                if (node!.LastSeen.HasValue)
                {
                    throw new PruningException($"{nameof(TrieNode.LastSeen)} not set on {node} committed at {blockNumber}.");
                }

                if (IsNodeCached(node.Keccak))
                {
                    TrieNode cachedNodeCopy = FindCachedOrUnknown(node.Keccak);
                    if (!ReferenceEquals(cachedNodeCopy, node))
                    {
                        if (_logger.IsTrace) _logger.Trace($"Replacing {node} with its cached copy {cachedNodeCopy}.");
                        if (!nodeCommitInfo.IsRoot)
                        {
                            nodeCommitInfo.NodeParent!.ReplaceChildRef(nodeCommitInfo.ChildPositionAtParent, cachedNodeCopy);
                        }

                        node = cachedNodeCopy;
                        Metrics.ReplacedNodesCount++;
                    }
                }
                else
                {
                    SaveInCache(node);
                }

                node.LastSeen = blockNumber;
                CommittedNodesCount++;
            }
        }

        public void FinishBlockCommit(TrieType trieType, long blockNumber, TrieNode? root)
        {
            if (blockNumber < 0) throw new ArgumentOutOfRangeException(nameof(blockNumber));
            EnsureCommitListExistsForBlock(blockNumber);
            
            if (trieType == TrieType.State) // storage tries happen before state commits
            {
                if (_logger.IsTrace) _logger.Trace($"Enqueued blocks {_commitSetQueue.Count}");
                BlockCommitSet set = CurrentPackage;
                if (set != null)
                {
                    set.Root = root;
                    if (_logger.IsTrace) _logger.Trace(
                        $"Current root (block {blockNumber}): {set.Root}, block {set.BlockNumber}");
                    if (_logger.IsTrace) _logger.Trace(
                        $"Incrementing refs from block {blockNumber} root {set.Root?.ToString() ?? "NULL"} ");

                    set.Seal();

                    TryRemovingOldBlock();
                }
                
                CurrentPackage = null;
            }
        }

        public event EventHandler<BlockNumberEventArgs>? TriePersisted;

        public void Flush()
        {
            if (_logger.IsDebug)
                _logger.Debug($"Flushing trie cache - in memory {_nodeCache.Count} " +
                              $"| commit count {CommittedNodesCount} " +
                              $"| save count {PersistedNodesCount}");

            CurrentPackage?.Seal();
            while (TryRemovingOldBlock())
            {
            }

            if (_logger.IsDebug)
                _logger.Debug($"Flushed trie cache - in memory {_nodeCache.Count} " +
                              $"| commit count {CommittedNodesCount} " +
                              $"| save count {PersistedNodesCount}");
        }

        public byte[]? LoadRlp(Keccak keccak, bool allowCaching)
        {
            byte[] rlp = null;
            if (allowCaching)
            {
                // TODO: static NodeCache in PatriciaTrie stays for now to simplify the PR
                rlp = PatriciaTree.NodeCache.Get(keccak);
            }

            if (rlp is null)
            {
                rlp = _keyValueStore[keccak.Bytes];
                if (rlp == null)
                {
                    throw new TrieException($"Node {keccak} is missing from the DB");
                }

                Metrics.LoadedFromDbNodesCount++;
                PatriciaTree.NodeCache.Set(keccak, rlp);
            }
            else
            {
                Metrics.LoadedFromRlpCacheNodesCount++;
            }

            return rlp;
        }

        public bool IsNodeCached(Keccak hash) => _nodeCache.ContainsKey(hash);

        public TrieNode FindCachedOrUnknown(Keccak hash)
        {
            bool isMissing = !_nodeCache.TryGetValue(hash, out TrieNode trieNode);
            if (isMissing)
            {
                trieNode = new TrieNode(NodeType.Unknown, hash);
                if (_logger.IsTrace) _logger.Trace($"Creating new node {trieNode}");
                _nodeCache.TryAdd(trieNode.Keccak!, trieNode);
            }
            else
            {
                Metrics.LoadedFromCacheNodesCount++;
            }

            return trieNode;
        }

        public void Dump()
        {
            if (_logger.IsTrace)
            {
                _logger.Trace($"Trie node cache ({_nodeCache.Count})");
                // return;
                foreach (KeyValuePair<Keccak, TrieNode> keyValuePair in _nodeCache)
                {
                    _logger.Trace($"  {keyValuePair.Value}");
                }
            }
        }

        public void Prune(long blockNumber)
        {
            // TODO: cannot prune nodes that are younger than max reorg
            // TODO: cannot prune nodes that are younger than last persisted
            
            List<Keccak> toRemove = new List<Keccak>(); // TODO: resettable
            foreach ((Keccak key, TrieNode value) in _nodeCache)
            {
                if (value.IsPersisted)
                {
                    if (_logger.IsTrace) _logger.Trace($"Removing persisted {value} from memory.");
                    toRemove.Add(key);
                    Metrics.PrunedPersistedNodesCount++;
                }
                else if (HasBeenRemoved(value, blockNumber))
                {
                    if (_logger.IsTrace) _logger.Trace($"Removing {value} from memory (no longer referenced).");
                    toRemove.Add(key);
                    Metrics.PrunedTransientNodesCount++;
                }
            }

            foreach (Keccak keccak in toRemove)
            {
                _nodeCache.Remove(keccak);
            }

            Metrics.CachedNodesCount = _nodeCache.Count;
        }

        public void ClearCache()
        {
            _nodeCache.Clear();
        }

        #region Private

        private readonly IKeyValueStore _keyValueStore;

        private Dictionary<Keccak, TrieNode> _nodeCache = new Dictionary<Keccak, TrieNode>();

        private readonly IPruningStrategy _pruningStrategy;

        private readonly IPersistenceStrategy _persistenceStrategy;

        private readonly ILogger _logger;

        private LinkedList<BlockCommitSet> _commitSetQueue = new LinkedList<BlockCommitSet>();

        private int _committedNodesCount;

        private int _persistedNodesCount;
        
        private long _lastPersistedBlockNumber;

        private BlockCommitSet? CurrentPackage { get; set; }

        private bool IsCurrentListSealed => CurrentPackage == null || CurrentPackage.IsSealed;

        private long OldestKeptBlockNumber => _commitSetQueue.First!.Value.BlockNumber;

        private long NewestKeptBlockNumber { get; set; }

        private void CreateCommitList(long blockNumber)
        {
            if (_logger.IsDebug) _logger.Debug($"Beginning new {nameof(BlockCommitSet)} - {blockNumber}");

            // TODO: this throws on reorgs, does it not? let us recreate it in test
            Debug.Assert(CurrentPackage == null || blockNumber == CurrentPackage.BlockNumber + 1,
                "Newly begun block is not a successor of the last one");
            Debug.Assert(IsCurrentListSealed, "Not sealed when beginning new block");

            BlockCommitSet commitSet = new BlockCommitSet(blockNumber);
            _commitSetQueue.AddLast(commitSet);
            NewestKeptBlockNumber = Math.Max(blockNumber, NewestKeptBlockNumber);

            // TODO: memory should be taken from the cache now
            while (_pruningStrategy.ShouldPrune(OldestKeptBlockNumber, NewestKeptBlockNumber, 0))
            {
                TryRemovingOldBlock();
            }

            CurrentPackage = commitSet;
            Debug.Assert(ReferenceEquals(CurrentPackage, commitSet),
                $"Current {nameof(BlockCommitSet)} is not same as the new package just after adding");
        }

        internal bool TryRemovingOldBlock()
        {
            BlockCommitSet? blockCommit = _commitSetQueue.First?.Value;
            bool hasAnySealedBlockInQueue = blockCommit != null && blockCommit.IsSealed;
            if (hasAnySealedBlockInQueue)
            {
                RemoveOldBlock(blockCommit);
            }

            return hasAnySealedBlockInQueue;
        }

        private void SaveInCache(TrieNode node)
        {
            Debug.Assert(node.Keccak != null, "Cannot store in cache nodes without resolved key.");
            _nodeCache[node.Keccak!] = node;
            Metrics.CachedNodesCount = _nodeCache.Count;
        }

        private void RemoveOldBlock(BlockCommitSet commitSet)
        {
            _commitSetQueue.RemoveFirst();

            if (_logger.IsDebug)
                _logger.Debug($"Start pruning {nameof(BlockCommitSet)} - {commitSet.BlockNumber}");

            Debug.Assert(commitSet != null && commitSet.IsSealed,
                $"Invalid {nameof(commitSet)} - {commitSet} received for pruning.");

            bool shouldPersistSnapshot = _persistenceStrategy.ShouldPersist(commitSet.BlockNumber);
            if (shouldPersistSnapshot)
            {
                Persist(commitSet);
            }

            if (_logger.IsDebug)
                _logger.Debug($"End pruning {nameof(BlockCommitSet)} - {commitSet.BlockNumber}");

            Dump();
        }

        private void Persist(BlockCommitSet commitSet)
        {
            if (_logger.IsDebug) _logger.Debug($"Persisting from root {commitSet.Root} in {commitSet.BlockNumber}");

            Stopwatch stopwatch = Stopwatch.StartNew();
            commitSet.Root?.PersistRecursively(tn => Persist(tn, commitSet.BlockNumber), this, _logger);
            stopwatch.Stop();
            Metrics.SnapshotPersistenceTime = stopwatch.ElapsedMilliseconds;

            stopwatch.Restart();
            Prune(commitSet.BlockNumber); // TODO: need to prune independently
            stopwatch.Stop();
            Metrics.PruningTime = stopwatch.ElapsedMilliseconds;
            
            if (_logger.IsDebug) _logger.Debug($"Persisted trie from {commitSet.Root} in {commitSet.BlockNumber}");
            LastPersistedBlockNumber = commitSet.BlockNumber;
        }

        private void Persist(TrieNode currentNode, long blockNumber)
        {
            if (currentNode == null)
            {
                throw new ArgumentNullException(nameof(currentNode));
            }

            if (currentNode.Keccak != null)
            {
                Debug.Assert(currentNode.LastSeen.HasValue, $"Cannot persist a dangling node (without {(nameof(TrieNode.LastSeen))} value set).");
                // Note that the LastSeen value here can be 'in the future' (greater than block number
                // if we replaced a newly added node with an older copy and updated the LastSeen value.
                // Here we reach it from the old root so it appears to be out of place but it is correct as we need
                // to prevent it from being removed from cache and also want to have it persisted.

                if (_logger.IsTrace) _logger.Trace($"Persisting {nameof(TrieNode)} {currentNode} in snapshot {blockNumber}.");
                _keyValueStore[currentNode.Keccak.Bytes] = currentNode.FullRlp;
                currentNode.IsPersisted = true;
                currentNode.LastSeen = blockNumber;

                PersistedNodesCount++;
            }
            else
            {
                Debug.Assert(currentNode.FullRlp != null && currentNode.FullRlp.Length < 32,
                    "We only expect persistence call without Keccak for the nodes that are kept inside the parent RLP (less than 32 bytes).");
            }
        }

        private static bool HasBeenRemoved(TrieNode node, long blockNumber)
        {
            Debug.Assert(node.LastSeen.HasValue, $"Any node that is cache should have {nameof(TrieNode.LastSeen)} set.");
            return node.LastSeen < blockNumber;
        }
        
        private void EnsureCommitListExistsForBlock(long blockNumber)
        {
            if (CurrentPackage is null)
            {
                CreateCommitList(blockNumber);
            }
        }

        #endregion

        public void Dispose()
        {
            
        }
    }
}