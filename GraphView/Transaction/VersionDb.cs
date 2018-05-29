﻿
namespace GraphView.Transaction
{
    using System;
    using System.Collections.Generic;
    using System.Threading;

    /// <summary>
    /// Define a delegate type to specify the partition rules.
    /// Which can be re-assigned outside the version db
    /// </summary>
    /// <param name="recordKey">The record key need to be operated</param>
    /// <returns></returns>
    public delegate int PartitionByKeyDelegate(object recordKey);

    // basic part with fields and its own methods
    public abstract partial class VersionDb
    {
        public static readonly long RETURN_ERROR_CODE = -2L;

        /// <summary>
        /// Define a delegate method to specify the partition rules.
        /// </summary>
        public PartitionByKeyDelegate PhysicalPartitionByKey { get; set; }

        /// <summary>
        /// Define the global LogicalParitionByKey function to determine its partition
        /// It's not a method belonging to version table or version db, which shoule be a global partition function
        /// </summary>
        public static PartitionByKeyDelegate LogicalPartitionByKey { get; set; }

        /// <summary>
        /// The default transaction table name
        /// </summary>
        public static readonly string TX_TABLE = "tx_table";

        public static bool Print = true;

        protected readonly Dictionary<string, VersionTable> versionTables;

        /// <summary>
        /// A tx table or a version table is logically partitioned into k partitions, 
        /// each having a dedicated request queue and request processor/visitor, 
        /// for queuing tx requests and processing them toward a specific partition.
        /// Only one thread is designated to process requests in one queue.
        /// 
        /// There may not may not be a 1:1 mapping from logical partitions to physical partitions
        /// of the underlying k-v store hosting tx states. 
        /// When the k-v store is partition transparent, logical partitions are purely to increase
        /// concurrency while not incurring synchronization.   
        /// When the k-v store is partition-aware, logical partitions are 1:1 mapped to k-v partitions. 
        /// As a result, the designated thread of a logical partition exclusively flushes requests toward
        /// one k-v partition, creating potential of batch processing and reducing fan-out. 
        /// </summary>
        protected readonly Queue<TxEntryRequest>[] txEntryRequestQueues;
        protected readonly Queue<TxEntryRequest>[] flushQueues;
        protected readonly VersionDbVisitor[] dbVisitors;

        private readonly int[] queueLatches;

        internal int PartitionCount { get; private set; }

        protected static class StaticRandom
        {
            static int seed = Environment.TickCount;

            static readonly ThreadLocal<Random> random =
                new ThreadLocal<Random>(() => new Random(Interlocked.Increment(ref seed)));

            public static long RandIdentity()
            {
                byte[] buf = new byte[8];
                random.Value.NextBytes(buf);
                long longRand = BitConverter.ToInt64(buf, 0);

                return Math.Abs(longRand);
            }

            public static int Seed()
            {
                return seed;
            }
        }

        public VersionDb(int partitionCount = 4)
        {
            this.versionTables = new Dictionary<string, VersionTable>();

            this.PartitionCount = partitionCount;
            this.txEntryRequestQueues = new Queue<TxEntryRequest>[partitionCount];
            this.flushQueues = new Queue<TxEntryRequest>[partitionCount];
            this.dbVisitors = new VersionDbVisitor[partitionCount];
            this.queueLatches = new int[partitionCount];

            for (int pid = 0; pid < partitionCount; pid++)
            {
                this.txEntryRequestQueues[pid] = new Queue<TxEntryRequest>(1024);
                this.flushQueues[pid] = new Queue<TxEntryRequest>(1024);
                this.queueLatches[pid] = 0;
            }

            this.PhysicalPartitionByKey = key => Math.Abs(key.GetHashCode()) % this.PartitionCount;
        }

        internal virtual TxResourceManager GetResourceManagerByPartitionIndex(int partition)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Enqueue Transcation Entry Requests
        /// </summary>
        /// <param name="txId">The specify txId to partition</param>
        /// <param name="txEntryRequest">The given request</param>
        internal virtual void EnqueueTxEntryRequest(long txId, TxEntryRequest txEntryRequest, int executorPK = 0)
        {
            int pk = this.PhysicalPartitionByKey(txId);

            while (Interlocked.CompareExchange(ref queueLatches[pk], 1, 0) != 0) ;
            Queue<TxEntryRequest> reqQueue = Volatile.Read(ref this.txEntryRequestQueues[pk]);
            reqQueue.Enqueue(txEntryRequest);
            Interlocked.Exchange(ref queueLatches[pk], 0);
        }

        /// <summary>
        /// Move pending requests for a partition of the tx table to the partition's flush queue.
        /// </summary>
        /// <param name="partitionKey">The key of the tx table partition</param>
        protected void DequeueTxEntryRequest(int partitionKey)
        {
            if (this.txEntryRequestQueues[partitionKey].Count > 0)
            {
                while (Interlocked.CompareExchange(ref queueLatches[partitionKey], 1, 0) != 0) ;

                Queue<TxEntryRequest> freeQueue = this.flushQueues[partitionKey];
                this.flushQueues[partitionKey] = this.txEntryRequestQueues[partitionKey];
                this.txEntryRequestQueues[partitionKey] = freeQueue;

                Interlocked.Exchange(ref queueLatches[partitionKey], 0);
            }
        }

        internal void Visit(string tableId, int partitionKey)
        {
            // Here try to flush the tx requests
            if (tableId == VersionDb.TX_TABLE)
            {
                this.DequeueTxEntryRequest(partitionKey);
                Queue<TxEntryRequest> flushQueue = this.flushQueues[partitionKey];
                if (flushQueue.Count == 0)
                {
                    return;
                }

                this.dbVisitors[partitionKey].Invoke(flushQueue);
                flushQueue.Clear();
            }
            else
            {
                VersionTable versionTable = this.GetVersionTable(tableId);
                if (versionTable != null)
                {
                    versionTable.Visit(partitionKey);
                }
            }
        }
    }

    /// <summary>
    /// This part is for DDL operators
    /// </summary>
    public abstract partial class VersionDb
    {
        /// <summary>
        /// Get a list of tableIds which are inside the database
        /// </summary>
        /// <returns></returns>
        internal virtual IEnumerable<string> GetAllTables()
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Get a version table instance by tableId, which has different implementations
        /// in different storages, like loading from meta-data database
        /// </summary>
        /// <returns></returns>
        internal virtual VersionTable GetVersionTable(string tableId)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Create a version table if the table doesn't exist, it will be called when the version
        /// insertion finds that the table with tableId doesn't exist
        /// </summary>
        /// <param name="tableId">The specify tabldId</param>
        /// <param name="redisDbIndex">It's only from redis kv storage</param>
        /// <returns>a version table instance</returns>
        internal virtual VersionTable CreateVersionTable(string tableId, long redisDbIndex = 0)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Delete a version table by tableId, it will be called by sql delete
        /// </summary>
        /// <param name="tableId"></param>
        /// <returns></returns>
        internal virtual bool DeleteTable(string tableId)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Clear the version db, which will delete the meta data and record data
        /// </summary>
        /// <returns></returns>
        internal virtual void Clear()
        {
            throw new NotImplementedException();
        }

		internal virtual void ClearTxTable()
		{
			throw new NotImplementedException();
		}
    }

    /// <summary>
    /// This part is the implemetation of version table interfaces
    /// </summary>
    public abstract partial class VersionDb
    {
        internal void EnqueueVersionEntryRequest(string tableId, VersionEntryRequest req, int execPartition)
        {
            VersionTable versionTable = this.GetVersionTable(tableId);
            if (versionTable == null)
            {
                throw new TransactionException("The specified table does not exists.");
            }

            versionTable.EnqueueVersionEntryRequest(req, execPartition);
        }

        internal IEnumerable<VersionEntry> GetVersionList(string tableId, object recordKey)
        {
            VersionTable versionTable = this.GetVersionTable(tableId);
            if (versionTable == null)
            {
                return null;
            }

            return versionTable.GetVersionList(recordKey);
        }

        /// <summary>
        /// Get the version entries by a batch of keys in a version table
        /// </summary>
        /// <param name="batch">A batch of record keys and version keys</returns>
        internal IDictionary<VersionPrimaryKey, VersionEntry> GetVersionEntryByKey(
            string tableId, IEnumerable<VersionPrimaryKey> batch)
        {
            Dictionary<VersionPrimaryKey, VersionEntry> versionDict =
                new Dictionary<VersionPrimaryKey, VersionEntry>();

            VersionTable versionTable = this.GetVersionTable(tableId);
            if (versionTable == null)
            {
                throw new TransactionException("The specified table does not exists.");
            }
            return versionTable.GetVersionEntryByKey(batch);
        }

        internal VersionEntry ReplaceVersionEntry(string tableId, object recordKey, long versionKey,
            long beginTimestamp, long endTimestamp, long txId, long readTxId, long expectedEndTimestamp)
        {
            VersionTable versionTable = this.GetVersionTable(tableId);
            if (versionTable == null)
            {
                return null;
            }

            return versionTable.ReplaceVersionEntry(recordKey, versionKey,
                beginTimestamp, endTimestamp, txId, readTxId, expectedEndTimestamp);
        }

        internal bool ReplaceWholeVersionEntry(string tableId, object recordKey, long versionKey,
            VersionEntry versionEntry)
        {
            VersionTable versionTable = this.GetVersionTable(tableId);
            if (versionTable == null)
            {
                return false;
            }

            return versionTable.ReplaceWholeVersionEntry(recordKey, versionKey, versionEntry);
        }

        internal bool UploadNewVersionEntry(string tableId, object recordKey, long versionKey, VersionEntry versionEntry)
        {
            VersionTable versionTable = this.GetVersionTable(tableId);
            if (versionTable == null)
            {
                return false;
            }

            return versionTable.UploadNewVersionEntry(recordKey, versionKey, versionEntry);
        }

        internal VersionEntry UpdateVersionMaxCommitTs(string tableId, object recordKey, long versionKey, long commitTime)
        {
            VersionTable versionTable = this.GetVersionTable(tableId);
            if (versionTable == null)
            {
                return null;
            }

            return versionTable.UpdateVersionMaxCommitTs(recordKey, versionKey, commitTime);
        }

        internal VersionEntry GetVersionEntryByKey(string tableId, object recordKey, long versionKey)
        {
            VersionTable versionTable = this.GetVersionTable(tableId);
            if (versionTable == null)
            {
                return null;
            }

            return versionTable.GetVersionEntryByKey(recordKey, versionKey);
        }

        internal bool DeleteVersionEntry(string tableId, object recordKey, long versionKey)
        {
            VersionTable versionTable = this.GetVersionTable(tableId);
            if (versionTable == null)
            {
                return false;
            }

            return versionTable.DeleteVersionEntry(recordKey, versionKey);
        }

        internal IEnumerable<VersionEntry> InitializeAndGetVersionList(string tableId, object recordKey)
        {
            VersionTable versionTable = this.GetVersionTable(tableId);
            if (versionTable == null) 
            {
                return null;
            }

            return versionTable.InitializeAndGetVersionList(recordKey);
        }
    }

    /// <summary>
    /// Transaction related methods
    /// </summary>
    public abstract partial class VersionDb
    {
        /// <summary>
        /// Generate an unique txId in the current transaction table and store the initial states in transaction
        /// table entry
        /// It will return the unique txId
        /// </summary>
        /// <returns>transaction id</returns>
        internal virtual long InsertNewTx(long txId = -1)
        {
            throw new NotImplementedException();
        }

        internal virtual bool RemoveTx(long txId)
        {
            throw new NotImplementedException();
        }

        internal virtual bool RecycleTx(long txId)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Get an TxTableEntey(txId, status, commitTime, commitLowerBound) by TxId
        /// </summary>
        /// <returns>a TxTableEntry</returns>
        internal virtual TxTableEntry GetTxTableEntry(long txId)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Update the transaction's status, set from ongoing => committed or ongoing => aborted
        /// </summary>
        internal virtual void UpdateTxStatus(long txId, TxStatus status)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Try to set the tx's commitTime and return the commitTime
        /// Take the larger value between commitLowerBound and proposedCommitTime as the commit time, and return it.
        /// If there are some errors in the set process, it will return -1
        /// </summary>
        /// <param name="txId"></param>
        /// <param name="proposalTs"></param>
        /// <returns>-1 or commitTime</returns>
        internal virtual long SetAndGetCommitTime(long txId, long proposedCommitTime)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Update the commitLowerBound to push Tx has greater commitTime than the lowerBound
        /// lowerBound should be the value commitTime + 1
        /// </summary>
        /// <param name="lowerBound">The current transaction's commit time + 1</param>
        /// <returns>
        /// -2 means there are some errors during the execution phase
        /// -1 means the txId hasn't gotten its commitTime and set the lowerBound successfully
        /// Other values mean txId has already got the commitTime and it returns tx's commitTime
        /// </returns>
        internal virtual long UpdateCommitLowerBound(long txId, long lowerBound)
        {
            throw new NotImplementedException();
        }
    }
}
