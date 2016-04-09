using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTDB.Buffer;
using BTDB.KVDBLayer.BTree;
using BTDB.ODBLayer;
using BTDB.StreamLayer;

namespace BTDB.KVDBLayer
{
    public class KeyValueDB : IKeyValueDB, IHaveSubDB
    {
        const int MaxValueSizeInlineInMemory = 7;
        const int EndOfIndexFileMarker = 0x1234DEAD;
        IBTreeRootNode _lastCommited;
        IBTreeRootNode _nextRoot;
        KeyValueDBTransaction _writingTransaction;
        readonly Queue<TaskCompletionSource<IKeyValueDBTransaction>> _writeWaitingQueue = new Queue<TaskCompletionSource<IKeyValueDBTransaction>>();
        readonly object _writeLock = new object();
        uint _fileIdWithTransactionLog;
        uint _fileIdWithPreviousTransactionLog;
        IFileCollectionFile _fileWithTransactionLog;
        AbstractBufferedWriter _writerWithTransactionLog;
        static readonly byte[] MagicStartOfTransaction = { (byte)'t', (byte)'R' };
        internal readonly long MaxTrLogFileSize;
        readonly ICompressionStrategy _compression;
        readonly CompactorScheduler _compactorScheduler;
        readonly HashSet<IBTreeRootNode> _usedBTreeRootNodes = new HashSet<IBTreeRootNode>(ReferenceEqualityComparer<IBTreeRootNode>.Instance);
        readonly object _usedBTreeRootNodesLock = new object();
        readonly IFileCollectionWithFileInfos _fileCollection;
        readonly Dictionary<long, object> _subDBs = new Dictionary<long, object>();

        public KeyValueDB(IFileCollection fileCollection)
            : this(fileCollection, new SnappyCompressionStrategy())
        {
        }

        public KeyValueDB(IFileCollection fileCollection, ICompressionStrategy compression, uint fileSplitSize = int.MaxValue)
        {
            if (fileCollection == null) throw new ArgumentNullException(nameof(fileCollection));
            if (compression == null) throw new ArgumentNullException(nameof(compression));
            if (fileSplitSize < 1024 || fileSplitSize > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(fileSplitSize), "Allowed range 1024 - 2G");
            MaxTrLogFileSize = fileSplitSize;
            _compression = compression;
            DurableTransactions = false;
            _fileCollection = new FileCollectionWithFileInfos(fileCollection);
            _lastCommited = new BTreeRoot(0);
            LoadInfoAboutFiles();
            _compactorScheduler = new CompactorScheduler(token => new Compactor(this, token).Run());
            _compactorScheduler.AdviceRunning();
        }

        void LoadInfoAboutFiles()
        {
            long latestGeneration = -1;
            uint lastestTrLogFileId = 0;
            var keyIndexes = new List<KeyValuePair<uint, long>>();
            foreach (var fileInfo in _fileCollection.FileInfos)
            {
                var trLog = fileInfo.Value as IFileTransactionLog;
                if (trLog != null)
                {
                    if (trLog.Generation > latestGeneration)
                    {
                        latestGeneration = trLog.Generation;
                        lastestTrLogFileId = fileInfo.Key;
                    }
                    continue;
                }
                var keyIndex = fileInfo.Value as IKeyIndex;
                if (keyIndex == null) continue;
                keyIndexes.Add(new KeyValuePair<uint, long>(fileInfo.Key, keyIndex.Generation));
            }
            if (keyIndexes.Count > 1)
                keyIndexes.Sort((l, r) => Comparer<long>.Default.Compare(l.Value, r.Value));
            var firstTrLogId = LinkTransactionLogFileIds(lastestTrLogFileId);
            var firstTrLogOffset = 0u;
            var hasKeyIndex = false;
            while (keyIndexes.Count > 0)
            {
                var keyIndex = keyIndexes[keyIndexes.Count - 1];
                keyIndexes.RemoveAt(keyIndexes.Count - 1);
                var info = (IKeyIndex)_fileCollection.FileInfoByIdx(keyIndex.Key);
                _nextRoot = LastCommited.NewTransactionRoot();
                if (LoadKeyIndex(keyIndex.Key, info))
                {
                    _lastCommited = _nextRoot;
                    _nextRoot = null;
                    firstTrLogId = info.TrLogFileId;
                    firstTrLogOffset = info.TrLogOffset;
                    hasKeyIndex = true;
                    break;
                }
                _fileCollection.MakeIdxUnknown(keyIndex.Key);
            }
            while (keyIndexes.Count > 0)
            {
                var keyIndex = keyIndexes[keyIndexes.Count - 1];
                keyIndexes.RemoveAt(keyIndexes.Count - 1);
                _fileCollection.MakeIdxUnknown(keyIndex.Key);
            }
            LoadTransactionLogs(firstTrLogId, firstTrLogOffset);
            if (lastestTrLogFileId != firstTrLogId && firstTrLogId != 0 || !hasKeyIndex && _fileCollection.FileInfos.Any(p => p.Value.SubDBId == 0))
            {
                CreateIndexFile(CancellationToken.None);
            }
            new Compactor(this, CancellationToken.None).FastStartCleanUp();
            _fileCollection.DeleteAllUnknownFiles();
        }

        internal void CreateIndexFile(CancellationToken cancellation)
        {
            var idxFileId = CreateKeyIndexFile(LastCommited, cancellation);
            MarkAsUnknown(_fileCollection.FileInfos.Where(p => p.Value.FileType == KVFileType.KeyIndex && p.Key != idxFileId).Select(p => p.Key));
        }

        bool LoadKeyIndex(uint fileId, IKeyIndex info)
        {
            try
            {
                var reader = FileCollection.GetFile(fileId).GetExclusiveReader();
                FileKeyIndex.SkipHeader(reader);
                var keyCount = info.KeyValueCount;
                _nextRoot.TrLogFileId = info.TrLogFileId;
                _nextRoot.TrLogOffset = info.TrLogOffset;
                _nextRoot.CommitUlong = info.CommitUlong;
                _nextRoot.BuildTree(keyCount, () =>
                    {
                        var keyLength = reader.ReadVInt32();
                        var key = ByteBuffer.NewAsync(new byte[Math.Abs(keyLength)]);
                        reader.ReadBlock(key);
                        if (keyLength < 0)
                        {
                            _compression.DecompressKey(ref key);
                        }
                        return new BTreeLeafMember
                            {
                                Key = key.ToByteArray(),
                                ValueFileId = reader.ReadVUInt32(),
                                ValueOfs = reader.ReadVUInt32(),
                                ValueSize = reader.ReadVInt32()
                            };
                    });
                if (reader.Eof) return true;
                if (reader.ReadInt32()==EndOfIndexFileMarker) return true;
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        void LoadTransactionLogs(uint firstTrLogId, uint firstTrLogOffset)
        {
            while (firstTrLogId != 0 && firstTrLogId != uint.MaxValue)
            {
                _fileIdWithTransactionLog = 0;
                if (LoadTransactionLog(firstTrLogId, firstTrLogOffset))
                {
                    _fileIdWithTransactionLog = firstTrLogId;
                }
                firstTrLogOffset = 0;
                _fileIdWithPreviousTransactionLog = firstTrLogId;
                var fileInfo = _fileCollection.FileInfoByIdx(firstTrLogId);
                if (fileInfo == null)
                    return;
                firstTrLogId = ((IFileTransactionLog)fileInfo).NextFileId;
            }
        }

        // Return true if it is suitable for continuing writing new transactions
        bool LoadTransactionLog(uint fileId, uint logOffset)
        {
            var inlineValueBuf = new byte[MaxValueSizeInlineInMemory];
            var stack = new List<NodeIdxPair>();
            var collectionFile = FileCollection.GetFile(fileId);
            var reader = collectionFile.GetExclusiveReader();
            try
            {
                if (logOffset == 0)
                {
                    FileTransactionLog.SkipHeader(reader);
                }
                else
                {
                    reader.SkipBlock(logOffset);
                }
                if (reader.Eof) return true;
                var afterTemporaryEnd = false;
                while (!reader.Eof)
                {
                    var command = (KVCommandType)reader.ReadUInt8();
                    if (command==0 && afterTemporaryEnd)
                    {
                        collectionFile.SetSize(reader.GetCurrentPosition()-1);
                        return true;
                    }
                    afterTemporaryEnd = false;
                    switch (command & KVCommandType.CommandMask)
                    {
                        case KVCommandType.CreateOrUpdateDeprecated:
                        case KVCommandType.CreateOrUpdate:
                            {
                                if (_nextRoot == null) return false;
                                var keyLen = reader.ReadVInt32();
                                var valueLen = reader.ReadVInt32();
                                var key = new byte[keyLen];
                                reader.ReadBlock(key);
                                var keyBuf = ByteBuffer.NewAsync(key);
                                if ((command & KVCommandType.FirstParamCompressed) != 0)
                                {
                                    _compression.DecompressKey(ref keyBuf);
                                }
                                var ctx = new CreateOrUpdateCtx
                                {
                                    KeyPrefix = BitArrayManipulation.EmptyByteArray,
                                    Key = keyBuf,
                                    ValueFileId = fileId,
                                    ValueOfs = (uint)reader.GetCurrentPosition(),
                                    ValueSize = (command & KVCommandType.SecondParamCompressed) != 0 ? -valueLen : valueLen
                                };
                                if (valueLen <= MaxValueSizeInlineInMemory && (command & KVCommandType.SecondParamCompressed) == 0)
                                {
                                    reader.ReadBlock(inlineValueBuf, 0, valueLen);
                                    StoreValueInlineInMemory(ByteBuffer.NewSync(inlineValueBuf, 0, valueLen), out ctx.ValueOfs, out ctx.ValueSize);
                                    ctx.ValueFileId = 0;
                                }
                                else
                                {
                                    reader.SkipBlock(valueLen);
                                }
                                _nextRoot.CreateOrUpdate(ctx);
                            }
                            break;
                        case KVCommandType.EraseOne:
                            {
                                if (_nextRoot == null) return false;
                                var keyLen = reader.ReadVInt32();
                                var key = new byte[keyLen];
                                reader.ReadBlock(key);
                                var keyBuf = ByteBuffer.NewAsync(key);
                                if ((command & KVCommandType.FirstParamCompressed) != 0)
                                {
                                    _compression.DecompressKey(ref keyBuf);
                                }
                                long keyIndex;
                                var findResult = _nextRoot.FindKey(stack, out keyIndex, BitArrayManipulation.EmptyByteArray, keyBuf);
                                if (findResult == FindResult.Exact)
                                    _nextRoot.EraseRange(keyIndex, keyIndex);
                            }
                            break;
                        case KVCommandType.EraseRange:
                            {
                                if (_nextRoot == null) return false;
                                var keyLen1 = reader.ReadVInt32();
                                var keyLen2 = reader.ReadVInt32();
                                var key = new byte[keyLen1];
                                reader.ReadBlock(key);
                                var keyBuf = ByteBuffer.NewAsync(key);
                                if ((command & KVCommandType.FirstParamCompressed) != 0)
                                {
                                    _compression.DecompressKey(ref keyBuf);
                                }
                                long keyIndex1;
                                var findResult = _nextRoot.FindKey(stack, out keyIndex1, BitArrayManipulation.EmptyByteArray, keyBuf);
                                if (findResult == FindResult.Previous) keyIndex1++;
                                key = new byte[keyLen2];
                                reader.ReadBlock(key);
                                keyBuf = ByteBuffer.NewAsync(key);
                                if ((command & KVCommandType.SecondParamCompressed) != 0)
                                {
                                    _compression.DecompressKey(ref keyBuf);
                                }
                                long keyIndex2;
                                findResult = _nextRoot.FindKey(stack, out keyIndex2, BitArrayManipulation.EmptyByteArray, keyBuf);
                                if (findResult == FindResult.Next) keyIndex2--;
                                _nextRoot.EraseRange(keyIndex1, keyIndex2);
                            }
                            break;
                        case KVCommandType.TransactionStart:
                            if (!reader.CheckMagic(MagicStartOfTransaction))
                                return false;
                            _nextRoot = LastCommited.NewTransactionRoot();
                            break;
                        case KVCommandType.CommitWithDeltaUlong:
                            unchecked // overflow is expected in case commitUlong is decreasing but that should be rare
                            {
                                _nextRoot.CommitUlong += reader.ReadVUInt64();
                            }
                            goto case KVCommandType.Commit;
                        case KVCommandType.Commit:
                            _nextRoot.TrLogFileId = fileId;
                            _nextRoot.TrLogOffset = (uint)reader.GetCurrentPosition();
                            _lastCommited = _nextRoot;
                            _nextRoot = null;
                            break;
                        case KVCommandType.Rollback:
                            _nextRoot = null;
                            break;
                        case KVCommandType.EndOfFile:
                            collectionFile.SetSize(reader.GetCurrentPosition());
                            collectionFile.Truncate();
                            return false;
                        case KVCommandType.TemporaryEndOfFile:
                            afterTemporaryEnd = true;
                            break;
                        default:
                            _nextRoot = null;
                            return false;
                    }
                }
                return afterTemporaryEnd;
            }
            catch (EndOfStreamException)
            {
                _nextRoot = null;
                return false;
            }
        }

        void StoreValueInlineInMemory(ByteBuffer value, out uint valueOfs, out int valueSize)
        {
            var inlineValueBuf = value.Buffer;
            var valueLen = value.Length;
            var ofs = value.Offset;
            switch (valueLen)
            {
                case 0:
                    valueOfs = 0;
                    valueSize = 0;
                    break;
                case 1:
                    valueOfs = 0;
                    valueSize = 0x1000000 | (inlineValueBuf[ofs] << 16);
                    break;
                case 2:
                    valueOfs = 0;
                    valueSize = 0x2000000 | (inlineValueBuf[ofs] << 16) | (inlineValueBuf[ofs + 1] << 8);
                    break;
                case 3:
                    valueOfs = 0;
                    valueSize = 0x3000000 | (inlineValueBuf[ofs] << 16) | (inlineValueBuf[ofs + 1] << 8) | inlineValueBuf[ofs + 2];
                    break;
                case 4:
                    valueOfs = inlineValueBuf[ofs + 3];
                    valueSize = 0x4000000 | (inlineValueBuf[ofs] << 16) | (inlineValueBuf[ofs + 1] << 8) | inlineValueBuf[ofs + 2];
                    break;
                case 5:
                    valueOfs = inlineValueBuf[ofs + 3] | ((uint)inlineValueBuf[ofs + 4] << 8);
                    valueSize = 0x5000000 | (inlineValueBuf[ofs] << 16) | (inlineValueBuf[ofs + 1] << 8) | inlineValueBuf[ofs + 2];
                    break;
                case 6:
                    valueOfs = inlineValueBuf[ofs + 3] | ((uint)inlineValueBuf[ofs + 4] << 8) | ((uint)inlineValueBuf[ofs + 5] << 16);
                    valueSize = 0x6000000 | (inlineValueBuf[ofs] << 16) | (inlineValueBuf[ofs + 1] << 8) | inlineValueBuf[ofs + 2];
                    break;
                case 7:
                    valueOfs = inlineValueBuf[ofs + 3] | ((uint)inlineValueBuf[ofs + 4] << 8) | ((uint)inlineValueBuf[ofs + 5] << 16) | (((uint)inlineValueBuf[ofs + 6]) << 24);
                    valueSize = 0x7000000 | (inlineValueBuf[ofs] << 16) | (inlineValueBuf[ofs + 1] << 8) | inlineValueBuf[ofs + 2];
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        uint LinkTransactionLogFileIds(uint lastestTrLogFileId)
        {
            var nextId = 0u;
            var currentId = lastestTrLogFileId;
            while (currentId != 0)
            {
                var fileInfo = _fileCollection.FileInfoByIdx(currentId);
                if (fileInfo == null)
                {
                    break;
                }
                var fileTransactionLog = fileInfo as IFileTransactionLog;
                if (fileTransactionLog == null) break;
                fileTransactionLog.NextFileId = nextId;
                nextId = currentId;
                currentId = fileTransactionLog.PreviousFileId;
            }
            return nextId;
        }

        public void Dispose()
        {
            if (_compactorScheduler != null) _compactorScheduler.Dispose();
            lock (_writeLock)
            {
                if (_writingTransaction != null) throw new BTDBException("Cannot dispose KeyValueDB when writting transaction still running");
                while (_writeWaitingQueue.Count > 0)
                {
                    _writeWaitingQueue.Dequeue().TrySetCanceled();
                }
            }
            if (_writerWithTransactionLog != null)
            {
                _writerWithTransactionLog.WriteUInt8((byte)KVCommandType.TemporaryEndOfFile);
                _writerWithTransactionLog.FlushBuffer();
                _fileWithTransactionLog.HardFlush();
                _fileWithTransactionLog.Truncate();
            }
        }

        public bool DurableTransactions { get; set; }

        internal IBTreeRootNode LastCommited => _lastCommited;

        internal IFileCollectionWithFileInfos FileCollection => _fileCollection;

        public IKeyValueDBTransaction StartTransaction()
        {
            return new KeyValueDBTransaction(this, LastCommited, false, false);
        }

        public IKeyValueDBTransaction StartReadOnlyTransaction()
        {
            return new KeyValueDBTransaction(this, LastCommited, false, true);
        }

        public Task<IKeyValueDBTransaction> StartWritingTransaction()
        {
            lock (_writeLock)
            {
                var tcs = new TaskCompletionSource<IKeyValueDBTransaction>();
                if (_writingTransaction == null)
                {
                    NewWrittingTransactionUnsafe(tcs);
                }
                else
                {
                    _writeWaitingQueue.Enqueue(tcs);
                }
                return tcs.Task;
            }
        }

        public string CalcStats()
        {
            var sb = new StringBuilder("KeyValueCount:" + LastCommited.CalcKeyCount() + Environment.NewLine
                                       + "FileCount:" + FileCollection.GetCount() + Environment.NewLine
                                       + "FileGeneration:" + FileCollection.LastFileGeneration + Environment.NewLine);
            foreach (var file in _fileCollection.FileInfos)
            {
                sb.AppendFormat("{0} Size:{1} Type:{2} Gen:{3}{4}", file.Key, FileCollection.GetSize(file.Key),
                                file.Value.FileType, file.Value.Generation, Environment.NewLine);
            }
            return sb.ToString();
        }

        internal IBTreeRootNode MakeWrittableTransaction(KeyValueDBTransaction keyValueDBTransaction, IBTreeRootNode btreeRoot)
        {
            lock (_writeLock)
            {
                if (_writingTransaction != null) throw new BTDBTransactionRetryException("Another writting transaction already running");
                if (LastCommited != btreeRoot) throw new BTDBTransactionRetryException("Another writting transaction already finished");
                _writingTransaction = keyValueDBTransaction;
                return btreeRoot.NewTransactionRoot();
            }
        }

        internal void CommitWrittingTransaction(IBTreeRootNode btreeRoot)
        {
            var deltaUlong = unchecked (btreeRoot.CommitUlong - _lastCommited.CommitUlong);
            if (deltaUlong != 0)
            {
                _writerWithTransactionLog.WriteUInt8((byte)KVCommandType.CommitWithDeltaUlong);
                _writerWithTransactionLog.WriteVUInt64(deltaUlong);
            }
            else
            {
                _writerWithTransactionLog.WriteUInt8((byte)KVCommandType.Commit);
            }
            _writerWithTransactionLog.FlushBuffer();
            UpdateTransactionLogInBTreeRoot(btreeRoot);
            if (DurableTransactions)
                _fileWithTransactionLog.HardFlush();
            lock (_writeLock)
            {
                _writingTransaction = null;
                _lastCommited = btreeRoot;
                TryDequeWaiterForWrittingTransaction();
            }
        }

        void UpdateTransactionLogInBTreeRoot(IBTreeRootNode btreeRoot)
        {
            if (btreeRoot.TrLogFileId != _fileIdWithTransactionLog && btreeRoot.TrLogFileId != 0)
            {
                _compactorScheduler.AdviceRunning();
            }
            btreeRoot.TrLogFileId = _fileIdWithTransactionLog;
            if (_writerWithTransactionLog != null)
            {
                btreeRoot.TrLogOffset = (uint)_writerWithTransactionLog.GetCurrentPosition();
            }
            else
            {
                btreeRoot.TrLogOffset = 0;
            }
        }

        void TryDequeWaiterForWrittingTransaction()
        {
            if (_writeWaitingQueue.Count == 0) return;
            var tcs = _writeWaitingQueue.Dequeue();
            NewWrittingTransactionUnsafe(tcs);
        }

        void NewWrittingTransactionUnsafe(TaskCompletionSource<IKeyValueDBTransaction> tcs)
        {
            var newTransactionRoot = LastCommited.NewTransactionRoot();
            _writingTransaction = new KeyValueDBTransaction(this, newTransactionRoot, true, false);
            tcs.SetResult(_writingTransaction);
        }

        internal void RevertWrittingTransaction(bool nothingWrittenToTransactionLog)
        {
            if (!nothingWrittenToTransactionLog)
            {
                _writerWithTransactionLog.WriteUInt8((byte)KVCommandType.Rollback);
                var newRoot = _lastCommited.CloneRoot();
                UpdateTransactionLogInBTreeRoot(newRoot);
                lock (_writeLock)
                {
                    _writingTransaction = null;
                    _lastCommited = newRoot;
                    TryDequeWaiterForWrittingTransaction();
                }
            }
            else
            {
                lock (_writeLock)
                {
                    _writingTransaction = null;
                    TryDequeWaiterForWrittingTransaction();
                }
            }
        }

        internal void WriteStartTransaction()
        {
            if (_fileIdWithTransactionLog == 0)
            {
                WriteStartOfNewTransactionLogFile();
            }
            else
            {
                if (_writerWithTransactionLog == null)
                {
                    _fileWithTransactionLog = FileCollection.GetFile(_fileIdWithTransactionLog);
                    _writerWithTransactionLog = _fileWithTransactionLog.GetAppenderWriter();
                }
                if (_writerWithTransactionLog.GetCurrentPosition() > MaxTrLogFileSize)
                {
                    WriteStartOfNewTransactionLogFile();
                }
            }
            _writerWithTransactionLog.WriteUInt8((byte)KVCommandType.TransactionStart);
            _writerWithTransactionLog.WriteByteArrayRaw(MagicStartOfTransaction);
        }

        void WriteStartOfNewTransactionLogFile()
        {
            if (_writerWithTransactionLog != null)
            {
                _writerWithTransactionLog.WriteUInt8((byte)KVCommandType.EndOfFile);
                _writerWithTransactionLog.FlushBuffer();
                _fileWithTransactionLog.HardFlush();
                _fileWithTransactionLog.Truncate();
                _fileIdWithPreviousTransactionLog = _fileIdWithTransactionLog;
            }
            _fileWithTransactionLog = FileCollection.AddFile("trl");
            _fileIdWithTransactionLog = _fileWithTransactionLog.Index;
            var transactionLog = new FileTransactionLog(FileCollection.NextGeneration(), _fileIdWithPreviousTransactionLog);
            _writerWithTransactionLog = _fileWithTransactionLog.GetAppenderWriter();
            transactionLog.WriteHeader(_writerWithTransactionLog);
            FileCollection.SetInfo(_fileIdWithTransactionLog, transactionLog);
        }

        public void WriteCreateOrUpdateCommand(byte[] prefix, ByteBuffer key, ByteBuffer value, out uint valueFileId, out uint valueOfs, out int valueSize)
        {
            var command = KVCommandType.CreateOrUpdate;
            if (_compression.ShouldTryToCompressKey(prefix.Length + key.Length))
            {
                if (prefix.Length != 0)
                {
                    var fullkey = new byte[prefix.Length + key.Length];
                    Array.Copy(prefix, 0, fullkey, 0, prefix.Length);
                    Array.Copy(key.Buffer, prefix.Length + key.Offset, fullkey, prefix.Length, key.Length);
                    prefix = BitArrayManipulation.EmptyByteArray;
                    key = ByteBuffer.NewAsync(fullkey);
                }
                if (_compression.CompressKey(ref key))
                {
                    command |= KVCommandType.FirstParamCompressed;
                }
            }
            valueSize = value.Length;
            if (_compression.CompressValue(ref value))
            {
                command |= KVCommandType.SecondParamCompressed;
                valueSize = -value.Length;
            }
            if (_writerWithTransactionLog.GetCurrentPosition() + prefix.Length + key.Length + 16 > MaxTrLogFileSize)
            {
                WriteStartOfNewTransactionLogFile();
            }
            _writerWithTransactionLog.WriteUInt8((byte)command);
            _writerWithTransactionLog.WriteVInt32(prefix.Length + key.Length);
            _writerWithTransactionLog.WriteVInt32(value.Length);
            _writerWithTransactionLog.WriteBlock(prefix);
            _writerWithTransactionLog.WriteBlock(key);
            if (valueSize != 0)
            {
                if (valueSize > 0 && valueSize < MaxValueSizeInlineInMemory)
                {
                    StoreValueInlineInMemory(value, out valueOfs, out valueSize);
                    valueFileId = 0;
                }
                else
                {
                    valueFileId = _fileIdWithTransactionLog;
                    valueOfs = (uint)_writerWithTransactionLog.GetCurrentPosition();
                }
                _writerWithTransactionLog.WriteBlock(value);
            }
            else
            {
                valueFileId = 0;
                valueOfs = 0;
            }
        }

        public uint CalcValueSize(uint valueFileId, uint valueOfs, int valueSize)
        {
            if (valueSize == 0) return 0;
            if (valueFileId == 0)
            {
                return (uint) (valueSize >> 24);
            }
            return (uint) Math.Abs(valueSize);
        }

        public ByteBuffer ReadValue(uint valueFileId, uint valueOfs, int valueSize)
        {
            if (valueSize == 0) return ByteBuffer.NewEmpty();
            if (valueFileId == 0)
            {
                var len = valueSize >> 24;
                var buf = new byte[len];
                switch (len)
                {
                    case 7:
                        buf[6] = (byte)(valueOfs >> 24);
                        goto case 6;
                    case 6:
                        buf[5] = (byte)(valueOfs >> 16);
                        goto case 5;
                    case 5:
                        buf[4] = (byte)(valueOfs >> 8);
                        goto case 4;
                    case 4:
                        buf[3] = (byte)valueOfs;
                        goto case 3;
                    case 3:
                        buf[2] = (byte)valueSize;
                        goto case 2;
                    case 2:
                        buf[1] = (byte)(valueSize >> 8);
                        goto case 1;
                    case 1:
                        buf[0] = (byte)(valueSize >> 16);
                        break;
                    default:
                        throw new BTDBException("Corrupted DB");
                }
                return ByteBuffer.NewAsync(buf);
            }
            var compressed = false;
            if (valueSize < 0)
            {
                compressed = true;
                valueSize = -valueSize;
            }
            var result = ByteBuffer.NewAsync(new byte[valueSize]);
            var file = FileCollection.GetFile(valueFileId);
            file.RandomRead(result.Buffer, 0, valueSize, valueOfs);
            if (compressed)
                _compression.DecompressValue(ref result);
            return result;
        }

        public void WriteEraseOneCommand(ByteBuffer key)
        {
            var command = KVCommandType.EraseOne;
            if (_compression.ShouldTryToCompressKey(key.Length))
            {
                if (_compression.CompressKey(ref key))
                {
                    command |= KVCommandType.FirstParamCompressed;
                }
            }
            if (_writerWithTransactionLog.GetCurrentPosition() > MaxTrLogFileSize)
            {
                WriteStartOfNewTransactionLogFile();
            }
            _writerWithTransactionLog.WriteUInt8((byte)command);
            _writerWithTransactionLog.WriteVInt32(key.Length);
            _writerWithTransactionLog.WriteBlock(key);
        }

        public void WriteEraseRangeCommand(ByteBuffer firstKey, ByteBuffer secondKey)
        {
            var command = KVCommandType.EraseRange;
            if (_compression.ShouldTryToCompressKey(firstKey.Length))
            {
                if (_compression.CompressKey(ref firstKey))
                {
                    command |= KVCommandType.FirstParamCompressed;
                }
            }
            if (_compression.ShouldTryToCompressKey(secondKey.Length))
            {
                if (_compression.CompressKey(ref secondKey))
                {
                    command |= KVCommandType.SecondParamCompressed;
                }
            }
            if (_writerWithTransactionLog.GetCurrentPosition() > MaxTrLogFileSize)
            {
                WriteStartOfNewTransactionLogFile();
            }
            _writerWithTransactionLog.WriteUInt8((byte)command);
            _writerWithTransactionLog.WriteVInt32(firstKey.Length);
            _writerWithTransactionLog.WriteVInt32(secondKey.Length);
            _writerWithTransactionLog.WriteBlock(firstKey);
            _writerWithTransactionLog.WriteBlock(secondKey);
        }

        uint CreateKeyIndexFile(IBTreeRootNode root, CancellationToken cancellation)
        {
            var file = FileCollection.AddFile("kvi");
            var writer = file.GetAppenderWriter();
            var keyCount = root.CalcKeyCount();
            var keyIndex = new FileKeyIndex(FileCollection.NextGeneration(), root.TrLogFileId, root.TrLogOffset, keyCount, root.CommitUlong);
            keyIndex.WriteHeader(writer);
            if (keyCount > 0)
            {
                var stack = new List<NodeIdxPair>();
                root.FillStackByIndex(stack, 0);
                do
                {
                    cancellation.ThrowIfCancellationRequested();
                    var nodeIdxPair = stack[stack.Count - 1];
                    var memberValue = ((IBTreeLeafNode)nodeIdxPair.Node).GetMemberValue(nodeIdxPair.Idx);
                    var key = ((IBTreeLeafNode)nodeIdxPair.Node).GetKey(nodeIdxPair.Idx);
                    var keyCompressed = false;
                    if (_compression.ShouldTryToCompressKey(key.Length))
                    {
                        keyCompressed = _compression.CompressKey(ref key);
                    }
                    writer.WriteVInt32(keyCompressed ? -key.Length : key.Length);
                    writer.WriteBlock(key);
                    writer.WriteVUInt32(memberValue.ValueFileId);
                    writer.WriteVUInt32(memberValue.ValueOfs);
                    writer.WriteVInt32(memberValue.ValueSize);
                } while (root.FindNextKey(stack));
            }
            writer.FlushBuffer();
            file.HardFlush();
            writer.WriteInt32(EndOfIndexFileMarker);
            writer.FlushBuffer();
            file.HardFlush();
            file.Truncate();
            FileCollection.SetInfo(file.Index, keyIndex);
            return file.Index;
        }

        internal void Compact()
        {
            _compactorScheduler.AdviceRunning();
        }

        internal bool ContainsValuesAndDoesNotTouchGeneration(uint fileId, long dontTouchGeneration)
        {
            var info = FileCollection.FileInfoByIdx(fileId);
            if (info == null) return false;
            if (info.Generation >= dontTouchGeneration) return false;
            return (info.FileType == KVFileType.TransactionLog || info.FileType == KVFileType.PureValues);
        }

        internal AbstractBufferedWriter StartPureValuesFile(out uint fileId)
        {
            var fId = FileCollection.AddFile("pvl");
            fileId = fId.Index;
            var pureValues = new FilePureValues(FileCollection.NextGeneration());
            var writer = fId.GetAppenderWriter();
            FileCollection.SetInfo(fId.Index, pureValues);
            pureValues.WriteHeader(writer);
            return writer;
        }

        internal long AtomicallyChangeBTree(Action<IBTreeRootNode> action)
        {
            using (var tr = StartWritingTransaction().Result)
            {
                var newRoot = (tr as KeyValueDBTransaction).BtreeRoot;
                action(newRoot);
                lock (_writeLock)
                {
                    _lastCommited = newRoot;
                }
                return newRoot.TransactionId;
            }
        }

        internal void MarkAsUnknown(IEnumerable<uint> fileIds)
        {
            foreach (var fileId in fileIds)
            {
                _fileCollection.MakeIdxUnknown(fileId);
            }
        }

        internal long GetGeneration(uint fileId)
        {
            if (fileId == 0) return -1;
            var fileInfo = FileCollection.FileInfoByIdx(fileId);
            if (fileInfo == null)
            {
                throw new ArgumentOutOfRangeException(nameof(fileId));
            }
            return fileInfo.Generation;
        }

        internal void StartedUsingBTreeRoot(IBTreeRootNode btreeRoot)
        {
            lock (_usedBTreeRootNodesLock)
            {
                var uses = btreeRoot.UseCount;
                uses++;
                btreeRoot.UseCount = uses;
                if (uses == 1)
                {
                    _usedBTreeRootNodes.Add(btreeRoot);
                }
            }
        }

        internal void FinishedUsingBTreeRoot(IBTreeRootNode btreeRoot)
        {
            lock (_usedBTreeRootNodesLock)
            {
                var uses = btreeRoot.UseCount;
                uses--;
                btreeRoot.UseCount = uses;
                if (uses == 0)
                {
                    _usedBTreeRootNodes.Remove(btreeRoot);
                    Monitor.PulseAll(_usedBTreeRootNodesLock);
                }
            }
        }

        internal void WaitForFinishingTransactionsBefore(long transactionId, CancellationToken cancellation)
        {
            lock (_usedBTreeRootNodesLock)
            {
                while (true)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var oldStillRuns = false;
                    foreach (var usedTransaction in _usedBTreeRootNodes)
                    {
                        if (usedTransaction.TransactionId - transactionId >= 0) continue;
                        oldStillRuns = true;
                        break;
                    }
                    if (!oldStillRuns) return;
                    Monitor.Wait(_usedBTreeRootNodesLock, 100);
                }
            }
        }

        internal ulong DistanceFromLastKeyIndex(IBTreeRootNode root)
        {
            var keyIndex = FileCollection.FileInfos.Where(p => p.Value.FileType == KVFileType.KeyIndex).Select(p => (IKeyIndex)p.Value).FirstOrDefault();
            if (keyIndex == null)
            {
                if (FileCollection.FileInfos.Count(p => p.Value.SubDBId == 0) > 1) return ulong.MaxValue;
                return root.TrLogOffset;
            }
            if (root.TrLogFileId != keyIndex.TrLogFileId) return ulong.MaxValue;
            return root.TrLogOffset - keyIndex.TrLogOffset;
        }

        public T GetSubDB<T>(long id) where T : class
        {
            object subDB;
            if (_subDBs.TryGetValue(id, out subDB))
            {
                if (!(subDB is T)) throw new ArgumentException($"SubDB of id {id} is not type {typeof (T).FullName}");
                return (T)subDB;
            }
            if (typeof(T) == typeof(IChunkStorage))
            {
                subDB = new ChunkStorageInKV(id, _fileCollection, MaxTrLogFileSize);
            }
            _subDBs.Add(id, subDB);
            return (T)subDB;
        }
    }
}
