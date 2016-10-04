using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Builders;
using MongoToolsLib.SimpleHelpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NLog;
using System.Threading;

namespace MongoToolsLib
{
    public class SharedMethods
    {
        static HashSet<string> valid_wt_compressors = new HashSet<string> (StringComparer.OrdinalIgnoreCase) { "", "zlib", "snappy" };
        /// <summary>
        /// Copies a certain collection from one database to the other, including Indexes
        /// </summary>
        /// <param name="sourceDatabase"></param>
        /// <param name="targetDatabase"></param>
        /// <param name="buffer"></param>
        /// <param name="sourceCollection"></param>
        /// <param name="insertBatchSize"></param>
        public static void CopyCollection (MongoDatabase sourceDatabase, MongoDatabase targetDatabase, string sourceCollectionName, string targetCollectionName = "", int insertBatchSize = -1, bool copyIndexes = false, bool dropCollections = false, bool skipCount = false, bool eraseObjectId = false, FlexibleOptions options = null)
        {
            var logger = NLog.LogManager.GetLogger ("CopyCollection");
            try
            {
                if (options == null) options = new FlexibleOptions ();

                BsonDocument last = null;
                // Resets Counter
                long count = 0, lastCount = 0;
                int loop = 0;

                // Reaching Collections
                var sourceCollection = sourceDatabase.GetCollection (sourceCollectionName);
                var targetCollection = targetDatabase.GetCollection (String.IsNullOrEmpty (targetCollectionName) ? sourceCollectionName : targetCollectionName);

                // Skipping System Collections - For Safety Reasons
                if (sourceCollection.FullName.IndexOf ("system.", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    sourceCollection.Database.Name.Equals ("system", StringComparison.OrdinalIgnoreCase) ||
                    sourceCollection.Database.Name.Equals ("local", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (!sourceCollection.Exists ())
                {
                    logger.Warn ("{0}.{1} - Collection not found ", sourceDatabase.Name, sourceCollectionName);
                    return;
                }

                logger.Debug ("{2} - {0}.{1} - Start collection copy.", sourceDatabase.Name, sourceCollectionName, Thread.CurrentThread.ManagedThreadId);

                // get total records in source
                long total = 1000000;
                if (!skipCount)
                {
                    total = sourceCollection.Count ();
                }

                logger.Debug ("{2} - {0}.{1} - Total Records Found: {3}", sourceDatabase.Name, sourceCollectionName, Thread.CurrentThread.ManagedThreadId, total);

                // check stats
                try
                {
                    // check if collection is capped collection
                    // since this is a special type of collection, we will skip it
                    if (sourceCollection.IsCapped ())
                    {
                        logger.Warn ("{0}.{1} - Found Capped collection (feature not implemented) - Make sure you manually run the ConverToCapped command on this collection after this copy ends", sourceDatabase.Name, sourceCollectionName);
                    }

                    // check if batch size is set to auto
                    if (insertBatchSize < 1)
                    {
                        var stats = sourceCollection.GetStats ();
                        // older mongodb vertions < 1.8, has a 4mb limit for batch insert
                        insertBatchSize = ((4 * 1024 * 1024) / (int)stats.AverageObjectSize) + 1;
                        // also benchmarks didn't show any benefit for batches larger than 100...
                        if (insertBatchSize > 200)
                            insertBatchSize = 200;

                        logger.Debug ("{0}.{1} - Insert batch size: {2}", sourceDatabase.Name, sourceCollection.Name, insertBatchSize);
                    }
                }
                catch (Exception ex)
                {
                    logger.Warn (ex, "{0}.{1} - Failed to get collection statistics... continuing any way...", sourceDatabase.Name, sourceCollection.Name);
                }

                // sanity check
                if (insertBatchSize <= 0) insertBatchSize = 100;

                // Checking for the need to drop the collection before adding data to it
                if (targetCollection.Exists ())
                {
                    long targetCount = targetCollection.Count ();
                    if (options.Get ("skip-existing", false) && targetCount > 0)
                    {
                        logger.Info ("{0}.{1} - Collection found in target database, skipping... [flag 'skip-existing']", sourceDatabase.Name, sourceCollectionName);
                        return;
                    }

                    if (options.Get ("resume", false))
                    {
                        last = targetCollection.Find (null).SetSortOrder (SortBy.Descending ("_id")).SetFields ("_id").SetLimit (1).FirstOrDefault ();
                        if (last != null)
                        {
                            logger.Debug ("{0}.{1} - Resuming collection copy, last _id: {2}", sourceDatabase.Name, sourceCollectionName, last["_id"]);
                            count = targetCount;
                        }
                    }

                    if (options.Get ("if-smaller", false) && targetCount >= total)
                    {
                        logger.Debug ("{0}.{1} - Collection of same size or larger, skipping... [flag 'if-smaller']", sourceDatabase.Name, sourceCollectionName, last["_id"]);
                        return;
                    }

                    // if the collection is empty and we have collection options, drop it
                    if (HasCollectionCreationOptions (options) && targetCount == 0)
                        dropCollections = true;

                    // check if we should drop the collection
                    if (dropCollections && last == null)
                    {
                        try
                        {
                            targetCollection.Drop ();
                            logger.Debug ("{0}.{1} - Target collection droped: {2}.{3}.", sourceDatabase.Name, sourceCollectionName, targetDatabase.Name, targetCollection.Name);
                        }
                        catch (Exception ex)
                        {
                            logger.Error (ex, "{0}.{1} - Failed to drop target collection {2}.{3}, aborting collection copy...", sourceDatabase.Name, sourceCollectionName, targetDatabase.Name, targetCollection.Name);
                            return;
                        }
                    }
                }

                // try to create the collection
                CreateCollection (sourceCollection, targetCollection, options);

                // index creation
                if (options.Get ("copy-indexes-before", false) && options.Get("copy-indexes", false))
                {
                    CreateIndexes (sourceCollection, targetCollection, options);
                }

                // check for lazy copy options
                int waitTime = options.Get ("lazy-wait", -1);

                // Local Buffer
                List<BsonDocument> buffer = new List<BsonDocument> (insertBatchSize);

                var timer = System.Diagnostics.Stopwatch.StartNew ();

                // Running Copy
                foreach (BsonDocument i in SafeQuery (sourceCollection, "_id", null, last))
                {
                    // Feedback and Local Buffer
                    count++;

                    buffer.Add (i);

                    // Dumping data to database every 'X' records
                    if (buffer.Count >= insertBatchSize)
                    {
                        try
                        {
                            // Erasing the object ids of the buffer
                            if (eraseObjectId)
                            {
                                buffer.ForEach (t =>
                                {
                                    t["_id"] = ObjectId.GenerateNewId();
                                });
                            }

                            targetCollection.SafeInsertBatch (buffer, 3, true, true);
                            if (loop++ % 150 == 0)
                            {
                                logger.Debug ("{7} - {0}.{1} - batch size: {2}, progress: {3} / {4} ({5}), rate: {6}/h ", sourceDatabase.Name, sourceCollection.Name, insertBatchSize, count.ToString ("N0"), total.ToString ("N0"), ((double)count / total).ToString ("0.0%"), ((count - lastCount) / timer.Elapsed.TotalHours).ToString ("N1"), Thread.CurrentThread.ManagedThreadId);
                                lastCount = count;
                                timer.Restart ();
                            }
                            if (waitTime > -1)
                            {
                                System.Threading.Thread.Sleep (waitTime);
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.Error (ex);
                            System.Threading.Thread.Sleep (1000);
                            // try again, but whithout try catch to hide the exception this time...
                            targetCollection.SafeInsertBatch (buffer, 3, true, true);
                        }
                        buffer.Clear ();
                    }
                }

                // Copying Remaining of Local Buffer
                if (buffer.Count > 0)
                {
                    try
                    {
                        targetCollection.SafeInsertBatch (buffer, 3, true, true);
                        logger.Debug ("{6} - {0}.{1} - batch size: {2}, progress: {3} / {4} ({5}) ", sourceDatabase.Name, sourceCollection.Name, insertBatchSize, count, total, ((double)count / total).ToString ("0.0%"), Thread.CurrentThread.ManagedThreadId);
                    }
                    catch (Exception ex)
                    {
                        logger.Error (ex);
                    }
                    buffer.Clear ();
                }

                // Checkign for the need to copy indexes aswell
                if (copyIndexes && !options.Get ("copy-indexes-before", false))
                {
                    CreateIndexes (sourceCollection, targetCollection, options);
                }

                logger.Info ("{2} - {0}.{1} - Collection copy completed.", sourceDatabase.Name, sourceCollectionName, Thread.CurrentThread.ManagedThreadId);
            }
            catch (Exception ex)
            {
                logger.Error (ex, "{2} - {0}.{1} - Error copying collection ", sourceDatabase.Name, sourceCollectionName ?? "", Thread.CurrentThread.ManagedThreadId);
                return;
            }
        }

        private static bool HasCollectionCreationOptions (FlexibleOptions options)
        {
            return (options.HasOption ("collection-wt-block-compressor") && valid_wt_compressors.Contains (options.Get ("collection-wt-block-compressor", "invalid"))) ||
                   (!String.IsNullOrEmpty (options.Get ("collection-wt-allocation")));
        }

        private static void CreateCollection (MongoCollection<BsonDocument> sourceCollection, MongoCollection<BsonDocument> targetCollection, FlexibleOptions options)
        {
            if (targetCollection.Exists ())
                return;

            List<string> config = new List<string> ();
            if (!String.IsNullOrWhiteSpace (options.Get ("collection-wt-configString")))
            {
                config.AddRange (options.Get ("collection-wt-configString", "").Split (',').Select (i => i.Trim ()).Where (i => !String.IsNullOrEmpty (i)));
            }

            if (options.HasOption ("collection-wt-block-compressor") && valid_wt_compressors.Contains (options.Get ("collection-wt-block-compressor", "invalid")))
            {
                config.RemoveAll (i => i.StartsWith ("block_compressor=", StringComparison.OrdinalIgnoreCase));
                config.Add ("block_compressor=" + options.Get ("collection-wt-block-compressor", "").ToLowerInvariant ());
            }

            if (!String.IsNullOrWhiteSpace (options.Get ("collection-wt-allocation")))
            {
                // Mongodb version 3.0.4 defaults to: "allocation_size=4KB,internal_page_max=4KB,leaf_page_max=32KB,leaf_value_max=1MB"
                if (options.Get ("collection-wt-allocation") == "2x")
                {
                    config.RemoveAll (i =>
                        i.StartsWith ("allocation_size=", StringComparison.OrdinalIgnoreCase) ||
                        i.StartsWith ("leaf_page_max=", StringComparison.OrdinalIgnoreCase) ||
                        i.StartsWith ("internal_page_max=", StringComparison.OrdinalIgnoreCase));
                    config.Add ("allocation_size=8KB");
                    config.Add ("leaf_page_max=64KB");
                    config.Add ("internal_page_max=8KB");
                }
                else if (options.Get ("collection-wt-allocation") == "4x")
                {
                    config.RemoveAll (i =>
                        i.StartsWith ("allocation_size=", StringComparison.OrdinalIgnoreCase) ||
                        i.StartsWith ("leaf_page_max=", StringComparison.OrdinalIgnoreCase) ||
                        i.StartsWith ("internal_page_max=", StringComparison.OrdinalIgnoreCase));
                    config.Add ("allocation_size=16KB");
                    config.Add ("leaf_page_max=64KB");
                    config.Add ("internal_page_max=16KB");
                }
                else if (options.Get ("collection-wt-allocation") == "8x")
                {
                    config.RemoveAll (i =>
                        i.StartsWith ("allocation_size=", StringComparison.OrdinalIgnoreCase) ||
                        i.StartsWith ("leaf_page_max=", StringComparison.OrdinalIgnoreCase) ||
                        i.StartsWith ("internal_page_max=", StringComparison.OrdinalIgnoreCase));
                    config.Add ("allocation_size=32KB");
                    config.Add ("leaf_page_max=128KB");
                    config.Add ("internal_page_max=32KB");
                }
            }

            // apply configuration
            if (config.Count > 0)
            {
                try
                {
                    var storageEngineDoc = new BsonDocument ("wiredTiger", new BsonDocument ("configString", String.Join (",", config)));
                    targetCollection.Database.CreateCollection (targetCollection.Name, CollectionOptions.SetStorageEngineOptions (storageEngineDoc));
                }
                catch (Exception ex)
                {
                    NLog.LogManager.GetLogger ("CreateCollection").Error (ex);
                }
            }
        }

        private static void CreateIndexes (MongoCollection<BsonDocument> sourceCollection, MongoCollection<BsonDocument> targetCollection, FlexibleOptions options)
        {
            if (options == null) options = new FlexibleOptions ();
            var logger = NLog.LogManager.GetLogger ("CreateIndexes");
            logger.Debug ("{2} - {0}.{1} - Start index creation", sourceCollection.Database.Name, sourceCollection.Name, Thread.CurrentThread.ManagedThreadId);

            var command = new CommandDocument ();
            command.Add ("createIndexes", targetCollection.Name);
            var indexList = new BsonArray ();
            command.Add ("indexes", indexList);

            // Copying Indexes - If Any
            foreach (IndexInfo idx in sourceCollection.GetIndexes ().ToList ())
            {
                // Skipping "_id_" default index - Since Every mongodb Collection has it
                if (idx.Name == "_id_")
                {
                    continue;
                }

                // Recreating Index Options based on the current index options
                var opts = IndexOptions.SetBackground (idx.IsBackground || options.Get ("indexes-background", false))
                               .SetSparse (idx.IsSparse || options.Get ("indexes-sparse", false))
                               .SetUnique (idx.IsUnique).SetName (idx.Name).SetDropDups (idx.DroppedDups);

                if (idx.TimeToLive < TimeSpan.MaxValue)
                {
                    opts.SetTimeToLive (idx.TimeToLive);
                }

                // Adding Index
                try
                {
                    if (targetCollection.Database.Server.BuildInfo.Version.Major < 2 && targetCollection.Database.Server.BuildInfo.Version.MajorRevision < 6)
                    {
                        logger.Debug ("{2} - {0}.{1} - Creating index: {2}", sourceCollection.Database.Name, sourceCollection, idx.Name, Thread.CurrentThread.ManagedThreadId);
                        targetCollection.CreateIndex (idx.Key, opts);
                    }
                    else
                    {
                        logger.Debug ("{2} - {0}.{1} - Prepare index creation: {2}", sourceCollection.Database.Name, sourceCollection, idx.Name, Thread.CurrentThread.ManagedThreadId);
                        // removes the namespace to allow mongodb to generate the correct one...
                        var doc = idx.RawDocument;
                        doc.Remove ("ns");
                        if (options.Get ("indexes-background", false))
                            doc["background"] = true;
                        if (options.Get ("indexes-sparse", false))
                            doc["sparse"] = true;
                        indexList.Add (doc);
                    }
                }
                catch (Exception ex)
                {
                    // check for timeout exception that may occur if the collection is large...
                    if (ex is System.IO.IOException || ex is System.Net.Sockets.SocketException || (ex.InnerException != null && ex.InnerException is System.Net.Sockets.SocketException))
                    {
                        logger.Warn ("{3} - {0}.{1} - Timeout creating index {2}, this may occur in large collections. You should check manually after a while.", sourceCollection.Database.Name, sourceCollection.Name, idx.Name, Thread.CurrentThread.ManagedThreadId);
                        // wait for index creation....
                        for (var i = 0; i < 30; i++)
                        {
                            System.Threading.Thread.Sleep (10000);
                            try
                            {
                                if (targetCollection.IndexExists (idx.Name))
                                    break;
                            }
                            catch
                            {
                            }
                        }
                    }
                    else
                    {
                        logger.Error (ex, "{0}.{1} - Error creating index {2}" + idx.Name);
                    }
                    
                    logger.Warn ("{3} - {0}.{1} - Index details: {2}", sourceCollection.Database.Name, sourceCollection.Name, idx.RawDocument.ToJson (), Thread.CurrentThread.ManagedThreadId);
                }
            }

            if (indexList.Count > 0)
            {
                try
                {
                    logger.Debug ("{3} - {0}.{1} - Creating {2} indexes", sourceCollection.Database.Name, sourceCollection, indexList.Count, Thread.CurrentThread.ManagedThreadId);
                    targetCollection.Database.RunCommand (command);
                }
                catch (Exception ex)
                {
                    // check for timeout exception that may occur if the collection is large...
                    if (ex is System.IO.IOException || ex is System.Net.Sockets.SocketException || (ex.InnerException != null && ex.InnerException is System.Net.Sockets.SocketException))
                    {
                        logger.Warn ("{3} - {0}.{1} - Timeout creating {2} indexes, this may occur in large collections. You should check manually after a while.", sourceCollection.Database.Name, sourceCollection.Name, indexList.Count, Thread.CurrentThread.ManagedThreadId);
                        logger.Warn ("{3} - {0}.{1} - Index details: {2}", sourceCollection.Database.Name, sourceCollection.Name, command.ToJson (), Thread.CurrentThread.ManagedThreadId);
                    }
                    else
                    {
                        logger.Error (ex, "{2} - {0}.{1} - Error creating indexes", sourceCollection.Database.Name, sourceCollection.Name, Thread.CurrentThread.ManagedThreadId);
                        logger.Error ("{3} - {0}.{1} - Index details: {2}", sourceCollection.Database.Name, sourceCollection.Name, command.ToJson (), Thread.CurrentThread.ManagedThreadId);
                    }
                }
            }

            logger.Debug ("{2} - {0}.{1} - Index creation completed", sourceCollection.Database.Name, sourceCollection, Thread.CurrentThread.ManagedThreadId);
        }

        private static IEnumerable<BsonDocument> SafeQuery (MongoCollection<BsonDocument> sourceCollection, string indexField = "_id", IMongoQuery query = null, BsonDocument last = null)
        {
            var logger = NLog.LogManager.GetLogger ("Query");
            int errorCount = 0;
            bool running = true;
            IEnumerator<BsonDocument> cursor;

            if (String.IsNullOrEmpty (indexField))
                indexField = "_id";

            while (running)
            {
                // prepare query
                IMongoQuery finalQuery = null;
                if (last != null && last.Contains (indexField))
                {
                    finalQuery = query != null ? Query.And (query, Query.GT (indexField, last[indexField])) : Query.GT (indexField, last[indexField]);
                }
                else
                {
                    finalQuery = query;
                }

                // prepare cursor
                cursor = sourceCollection.Find (finalQuery).SetSortOrder (indexField).GetEnumerator ();

                // execute
                while (running)
                {
                    try
                    {
                        if (!cursor.MoveNext ())
                        {
                            running = false;
                            break;
                        }
                        last = cursor.Current;
                        errorCount = 0;
                    }
                    catch (Exception ex)
                    {
                        logger.Warn ("{0} - {1}.. try {0} of {1}. Last id: {2}", sourceCollection, ex.Message, errorCount, 5, (last != null && last.Contains (indexField)) ? last[indexField].ToString () : "");
                        // if 5 consecutive errors... throw
                        if (errorCount++ > 4)
                            throw;
                        // else lets wait a while before retrying...
                        System.Threading.Thread.Sleep (2500);
                        break;
                    }

                    // if we got here, return the document
                    yield return last;
                }
            }
        }

        public static string WildcardToRegex (string pattern, bool anchorOnStart = false)
        {
            if (pattern == null)
                return String.Empty;
            pattern = System.Text.RegularExpressions.Regex.Escape (pattern).Replace (@"\*", ".*").Replace (@"\?", ".");
            return (anchorOnStart ? "^" : "") + pattern + "$";
        }

        public static bool HasWildcard (string pattern)
        {
            if (pattern == null)
                return false;
            return (pattern.IndexOf ('*') >= 0 || pattern.IndexOf ('?') >= 0);
        }

        public static bool WildcardIsMatch (string pattern, string input, bool ignoreCase = true)
        {
            if (input == pattern)
                return true;
            if (String.IsNullOrWhiteSpace (pattern) || String.IsNullOrWhiteSpace (input))
                return false;
            if (!HasWildcard (pattern))
                return input.Equals (pattern, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            return System.Text.RegularExpressions.Regex.IsMatch (input, WildcardToRegex (pattern, true), ignoreCase ? System.Text.RegularExpressions.RegexOptions.IgnoreCase : System.Text.RegularExpressions.RegexOptions.None);
        }
    }
}
