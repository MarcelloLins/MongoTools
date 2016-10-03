using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Builders;
using MongoToolsLib.SimpleHelpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MongoToolsLib
{
    public class CopyHandler
    {
        class CopyInfo
        {
            public MongoDatabase   SourceDatabase   { get; set; }
            public MongoDatabase   TargetDatabase   { get; set; }
            public string          SourceCollection { get; set; }
            public string          TargetCollection { get; set; }
            public int             BatchSize        { get; set; }
            public bool            CopyIndexes      { get; set; } 
            public bool            DropCollections  { get; set; }
            public bool            SkipCount        { get; set; }
            public FlexibleOptions Options          { get; set; } 
        }

        static IEnumerable<Tuple<MongoDatabase,MongoDatabase>> ListDatabases (MongoServer sourceServer, MongoServer targetServer, List<string> sourceDatabases, List<string> targetDatabases)
        {
            if (sourceDatabases == null)
                yield break;
            if (targetDatabases == null || targetDatabases.Count == 0)
                targetDatabases = null;

            // check if we are on the same server!
            bool sameServer = ServersAreEqual (sourceServer, targetServer);

            // prepare available databases list
            var databases = sourceServer.GetDatabaseNames ().ToList ();
            var availableDatabases = new HashSet<string> (databases, StringComparer.Ordinal);            

            // create mappings
            if (targetDatabases == null)
            {
                for (int i = 0; i < sourceDatabases.Count; i++)
                {
                    string k = sourceDatabases[i];
                    if (k.IndexOf ('=') > 0)
                    {
                        var split = k.Split ('=');
                        k = split[0];
                        var db = availableDatabases.Contains (k) ? k : databases.FirstOrDefault (name => k.Equals (name, StringComparison.OrdinalIgnoreCase));
                        // check if database was found
                        if (String.IsNullOrEmpty (db) || String.IsNullOrEmpty (split[1])) continue;
                        yield return Tuple.Create (sourceServer.GetDatabase (db), targetServer.GetDatabase (split[1]));
                    }
                    else
                    {
                        foreach (var db in databases.Where (name => SharedMethods.WildcardIsMatch (k, name, true)))
                        {
                            yield return Tuple.Create (sourceServer.GetDatabase (db), targetServer.GetDatabase (db));
                        }
                    }                    
                }
            }
            else
            {
                // match
                for (int i = 0; i < sourceDatabases.Count; i++)
                {
                    string k = sourceDatabases[i];
                    var db = availableDatabases.Contains (k) ? k : databases.FirstOrDefault (name => k.Equals (name, StringComparison.OrdinalIgnoreCase));
                    // check if database was found
                    if (String.IsNullOrEmpty (db) || String.IsNullOrEmpty (targetDatabases[i])) continue;
                    yield return Tuple.Create (sourceServer.GetDatabase (db), targetServer.GetDatabase (targetDatabases[i]));
                }                
            }            
        }
 
        private static bool ServersAreEqual (MongoServer sourceServer, MongoServer targetServer)
        {
            // check if we are on the same server!            
            try
            {
                // check using get ip endpoint of the primary server and port
                if (sourceServer.Primary != null && targetServer.Primary != null)
                    return sourceServer.Primary.GetIPEndPoint ().Address.ToString () == targetServer.Primary.GetIPEndPoint ().Address.ToString () &&
                        sourceServer.Primary.Address.Port == targetServer.Primary.Address.Port;
            }
            catch {}

            try
            {
                // fallback to comparing the host names and port
                if (sourceServer.Instance != null && targetServer.Instance != null)
                    return sourceServer.Instance.Address.Host.Equals (targetServer.Instance.Address.Host, StringComparison.OrdinalIgnoreCase) &&
                           sourceServer.Instance.Address.Port == targetServer.Instance.Address.Port;
            }
            catch {}

            return false;
        }

        static IEnumerable<Tuple<string,string>> ListCollections (MongoDatabase sourceServer, List<string> collections)
        {            
            if (collections == null || collections.Count == 0)
            {
                foreach (var c in sourceServer.GetCollectionNames ())
                    yield return Tuple.Create (c, c);
            }
            else
            {
                var list = sourceServer.GetCollectionNames ().ToList ();
                var hashOrdinal = new HashSet<string> (list, StringComparer.Ordinal);
                foreach (var c in collections)
                {
                    if (hashOrdinal.Contains (c))
                    {
                        yield return Tuple.Create (c, c);
                    }
                    else if (c.IndexOf ('=') > 0)
                    {
                        var split = c.Split ('=');
                        var k = split[0];
                        var col = hashOrdinal.Contains (k) ? k : list.FirstOrDefault (name => k.Equals (name, StringComparison.OrdinalIgnoreCase));
                        if (!String.IsNullOrEmpty (col) && !String.IsNullOrEmpty (split[1]))
                            yield return Tuple.Create (col, split[1]);
                    }
                    else
                    {
                        foreach (var col in list.Where (name => SharedMethods.WildcardIsMatch (c, name, true)))
                            yield return Tuple.Create (col, col);
                    }
                }                
            }   
        }

        /// <summary>
        /// Migrates data and indexes of all collections of a certain database, to another
        /// </summary>
        /// <param name="sourceServer">Source mongodb server  - Where the data will come from.</param>
        /// <param name="targetServer">Target mongodb server - Where the data will go to.</param>
        /// <param name="sourceDatabases">The source databases.</param>
        /// <param name="targetDatabases">The target databases.</param>
        /// <param name="collections">The collections.</param>
        /// <param name="insertBatchSize">Size (in records) of the chunk of data that will be inserted per batch.</param>
        /// <param name="copyIndexes">True if the indexes should be copied aswell, false otherwise.</param>
        /// <param name="dropCollections">The drop collections.</param>
        /// <param name="threads">The threads.</param>
        public static void DatabaseCopy (MongoServer sourceServer, MongoServer targetServer, List<string> sourceDatabases, List<string> targetDatabases, List<string> collections, string targetCollection, int insertBatchSize = -1, bool copyIndexes = true, bool dropCollections = false, bool skipCount = false, int threads = 1, FlexibleOptions options = null)
        {
            if (threads <= 1)
                threads = 1;

            // check if we are on the same server!
            bool sameServer = ServersAreEqual (sourceServer, targetServer);

            // Validating whether we received multiple matches (or collection names) when the "target collection" has value
            var databases           = ListDatabases(sourceServer, targetServer, sourceDatabases, targetDatabases);
            var matchingCollections = ListCollections(databases.First().Item1, collections).ToList();

            if (matchingCollections != null && matchingCollections.Count > 1 && !String.IsNullOrWhiteSpace(targetCollection))
            {
                // Error. In order to specify a "TargetCollection" there should be only one collection matching the mask or received as argument (as it's source)
                NLog.LogManager.GetLogger ("DatabaseCopy").Error ("In order to specify a 'TargetCollection' there should be only one collection matching the mask or received as argument (as it's source)");
                return;
            }

            // create our thread manager and start producing tasks...
            using (var mgr = new MongoToolsLib.SimpleHelpers.ParallelTasks<CopyInfo> (0, threads, 1000, CollectionCopy))
            {
                // list databases
                foreach (var db in ListDatabases (sourceServer, targetServer, sourceDatabases, targetDatabases))
                {
                    foreach (var col in ListCollections (db.Item1, collections))
                    {
                        // sanity checks
                        if (sameServer && db.Item1 == db.Item2 && col.Item1 == col.Item2)
                        {
                            NLog.LogManager.GetLogger ("DatabaseCopy").Warn ("Skiping collection, since it would be copied to itself! Database: {0}, Collection: {1}", db.Item1, col.Item1);
                            continue;                       
                        }

                        // process task
                        mgr.AddTask (new CopyInfo
                        {
                            SourceDatabase   = db.Item1,
                            TargetDatabase   = db.Item2,
                            SourceCollection = col.Item1,
                            TargetCollection = col.Item2,
                            BatchSize        = insertBatchSize,
                            CopyIndexes      = copyIndexes,
                            DropCollections  = dropCollections,
                            Options          = options,
                            SkipCount        = skipCount
                        });
                    }
                }
                mgr.CloseAndWait ();
            }
        }

        static void CollectionCopy (CopyInfo item)
        {
            SharedMethods.CopyCollection (item.SourceDatabase, item.TargetDatabase, item.SourceCollection, item.TargetCollection, item.BatchSize, item.CopyIndexes, item.DropCollections, item.SkipCount, item.Options);
        }
    }
}
