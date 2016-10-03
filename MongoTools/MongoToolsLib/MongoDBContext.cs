using MongoDB.Driver;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MongoToolsLib
{
    public class MongoDbContext
    {
        /// <remarks>
        /// 
        /// ** Connection String **
        /// format example:
        /// 
        /// mongodb://[username:password@]host1[:port1][,host2[:port2],...[,hostN[:portN]]][/[database][?options]]
        /// 
        /// ** OPTIONS **
        /// * w
        /// -1 : The driver will not acknowledge write operations and will suppress all network or socket errors.
        /// 0 : The driver will not acknowledge write operations, but will pass or handle any network and socket errors that it receives to the client.
        /// 1 : Provides basic acknowledgment of write operations, a standalone mongod instance, or the primary for replica sets, acknowledge all write operations
        /// majority
        /// n
        /// tags
        /// 
        /// * ssl
        /// true: Initiate the connection with SSL.
        /// false: Initiate the connection without SSL.
        /// The default value is false.
        /// 
        /// * connectTimeoutMS
        /// The time in milliseconds to attempt a connection before timing out. 
        /// The default is never to timeout, though different drivers might vary. See the driver documentation.
        /// 
        /// * socketTimeoutMS
        /// The time in milliseconds to attempt a send or receive on a socket before the attempt times out. 
        /// The default is never to timeout, though different drivers might vary. See the driver documentation.
        /// 
        /// * journal 
        /// true / false
        /// 
        /// * readPreference
        /// primaryPreferred - will try to read from primary (but if primary is offline, will read from the secondary nodes)
        /// secondaryPreferred - will try to read from a secondary node (but if offline, will read from the primary node)
        /// (OBS.: All writes go to the Primary)
        /// 
        /// </remarks>

        private static string _globalConnectionString = String.Empty;

        private static Lazy<MongoClient> _mongoDbInstance = new Lazy<MongoClient> (OpenConnection);

        private static MongoServer _server = null;

        /// <summary>
        /// Configures the global connection string for a MongoDB instance/cluster.
        /// </summary>
        /// <param name="login">The login.</param>
        /// <param name="password">The password.</param>
        /// <param name="addresses">List of addresses. Format: host1[:port1][,host2[:port2],...[,hostN[:portN]]]</param>
        /// <param name="safeMode">The safe mode. True to receive write confirmation from mongo.</param>
        /// <param name="readOnSecondary">The read on secondary. True to direct read operations to cluster secondary nodes (secondaryPreferred), else try to read from primary (primaryPreferred).</param>
        /// <param name="connectionTimeoutMilliseconds">The time to attempt a connection before timing out.</param>
        /// <param name="socketTimeoutMilliseconds">The time to attempt a send or receive on a socket before the attempt times out.</param>
        /// <param name="databaseName">Database name required to authenticate against a specific database [optional].</param>
        /// <param name="readPreferenceTags">The read preference tags. List of a comma-separated list of colon-separated key-value pairs. Ex.: { {dc:ny,rack:1}, { dc:ny } } </param>
        public static void Configure (string login, string password, string addresses, bool safeMode = true, bool readOnSecondary = false, int connectionTimeoutMilliseconds = 30000, int socketTimeoutMilliseconds = 90000, string databaseName = null, IEnumerable<string> readPreferenceTags = null)
        {
            // prepares the connection string
            string connectionString = BuildConnectionString (login, password, safeMode, readOnSecondary, addresses, connectionTimeoutMilliseconds, socketTimeoutMilliseconds, databaseName, readPreferenceTags);
            // set the new connection string
            Configure (connectionString);
        }

        /// <summary>
        /// Configures the global connection string for a MongoDB instance/cluster.
        /// </summary>
        /// <param name="connectionString">The connection string builder.</param>
        public static void Configure (MongoUrlBuilder connectionString)
        {
            if (connectionString.ConnectTimeout.TotalSeconds < 30) connectionString.ConnectTimeout = TimeSpan.FromSeconds (30);
            if (connectionString.SocketTimeout.TotalSeconds < 90) connectionString.SocketTimeout = TimeSpan.FromSeconds (90);
            connectionString.MaxConnectionIdleTime = TimeSpan.FromSeconds (30);
            // set the new connection string
            Configure (connectionString.ToString ());
        }

        /// <summary>
        /// Configures the global connection string for a MongoDB instance/cluster.
        /// </summary>
        /// <param name="connectionString">The connection string.</param>
        public static void Configure (string connectionString)
        {
            // if there is any change, reconnect
            if (_globalConnectionString != connectionString)
            {
                _globalConnectionString = connectionString;
                Dispose ();
            }
        }

        /// <summary>
        /// Releases MongoDb resources.<para/>
        /// Obs.: the last Connection String is kept, so if any request is made, the resources will be recreated and the connection opened. 
        /// </summary>
        public static void Dispose ()
        {
            _server = null;
            _mongoDbInstance = new Lazy<MongoClient> (OpenConnection);
        }

        /// <summary>
        /// Forcefully closes all connections from the connection pool.<para/>
        /// Not recommended, since can cause some connection instability issues.
        /// </summary>
        public static void ForceDisconnect ()
        {
            if (_server != null)
            {
                _server.Disconnect ();
            }
        }

        /// <summary>
        /// Builds the connection string for MongoDB.
        /// </summary>
        /// <param name="login">The login.</param>
        /// <param name="password">The password.</param>
        /// <param name="addresses">List of addresses. Format: host1[:port1][,host2[:port2],...[,hostN[:portN]]]</param>
        /// <param name="databaseName">Database name required to authenticate against a specific database [optional].</param>
        /// <returns></returns>
        public static string BuildConnectionString (string login, string password, string addresses, string databaseName = null)
        {
            return BuildConnectionString (login, password, true, false, addresses, 30000, 90000, databaseName);
        }

        static string[] urlPrefix = new string[] { "mongodb://" };

        /// <summary>
        /// Builds the connection string for MongoDB.
        /// </summary>
        /// <param name="login">The login.</param>
        /// <param name="password">The password.</param>
        /// <param name="safeMode">The safe mode. True to receive write confirmation from mongo.</param>
        /// <param name="readOnSecondary">The read on secondary. True to direct read operations to cluster secondary nodes (secondaryPreferred), else try to read from primary (primaryPreferred).</param>
        /// <param name="addresses">List of addresses. Format: host1[:port1][,host2[:port2],...[,hostN[:portN]]]</param>
        /// <param name="connectionTimeoutMilliseconds">The time to attempt a connection before timing out.</param>
        /// <param name="socketTimeoutMilliseconds">The time to attempt a send or receive on a socket before the attempt times out.</param>
        /// <param name="databaseName">Database name required to authenticate against a specific database [optional].</param>
        /// <param name="readPreferenceTags">The read preference tags. List of a comma-separated list of colon-separated key-value pairs. Ex.: { {dc:ny,rack:1}, { dc:ny } } </param>
        public static string BuildConnectionString (string login, string password, bool safeMode, bool readOnSecondary, string addresses, int connectionTimeoutMilliseconds, int socketTimeoutMilliseconds, string databaseName = null, IEnumerable<string> readPreferenceTags = null)
        {
            var cb = new MongoDB.Driver.MongoUrlBuilder ("mongodb://" + addresses.Replace ("mongodb://", ""));
            cb.Username = login;
            cb.Password = password;
            cb.ConnectionMode = MongoDB.Driver.ConnectionMode.Automatic;
            cb.DatabaseName   = databaseName;
            cb.W = safeMode ? WriteConcern.W1.W : WriteConcern.Unacknowledged.W;

            if (connectionTimeoutMilliseconds < 15000) connectionTimeoutMilliseconds = 15000;
            if (socketTimeoutMilliseconds < 15000) socketTimeoutMilliseconds = 15000;
            cb.ConnectTimeout = TimeSpan.FromMilliseconds (connectionTimeoutMilliseconds);
            cb.SocketTimeout = TimeSpan.FromMilliseconds (socketTimeoutMilliseconds);

            cb.MaxConnectionIdleTime = TimeSpan.FromSeconds (30);

            cb.ReadPreference = new ReadPreference ();
            cb.ReadPreference.ReadPreferenceMode = readOnSecondary ? ReadPreferenceMode.SecondaryPreferred : ReadPreferenceMode.PrimaryPreferred;

            if (readPreferenceTags != null)
            {
                foreach (var tag in readPreferenceTags.Where (i => i != null && i.IndexOf (':') > 0).Select (i => i.Split (':')).Select (i => new ReplicaSetTag (i[0], i[1])))
                    cb.ReadPreference.AddTagSet (new ReplicaSetTagSet ().Add (tag));
            }

            // generate final connection string
            return cb.ToString ();
        }

        private static MongoClient OpenConnection ()
        {
            // TODO: set serialization options (a design decision to use UtcNow or Local time zone).
            // Its is recommended to use UtcNow as the DateTime default in the database, and only convert to local whenever the data is displayed to the user.
            // To use Local time zone, uncomment the line bellow:

            // MongoDB.Bson.Serialization.BsonSerializer.RegisterSerializer (typeof (DateTime), new MongoDB.Bson.Serialization.Serializers.DateTimeSerializer (MongoDB.Bson.Serialization.Options.DateTimeSerializationOptions.LocalInstance));

            // create mongo client
            MongoClient client = new MongoClient (_globalConnectionString);
            return client;
        }

        public static MongoClient Client
        {
            get { return _mongoDbInstance.Value; }
        }

        public static MongoServer Server
        {
            get
            {
                if (_server == null)
                    _server = Client.GetServer ();
                return _server;
            }
        }

        /// <summary>
        /// Gets the MongoDb server instance.
        /// </summary>
        /// <returns></returns>
        public static MongoServer GetServer ()
        {
            return Client.GetServer ();
        }

        /// <summary>
        /// Gets the MongoDb server instance using the provided connection string.
        /// </summary>
        /// <param name="connectionString">The connection string.</param>
        /// <returns></returns>
        public static MongoServer GetServer (MongoUrlBuilder connectionString)
        {
            if (connectionString.ConnectTimeout.TotalSeconds < 30) connectionString.ConnectTimeout = TimeSpan.FromSeconds (30);
            if (connectionString.SocketTimeout.TotalSeconds < 90) connectionString.SocketTimeout = TimeSpan.FromSeconds (90);
            connectionString.MaxConnectionIdleTime = TimeSpan.FromSeconds (30);
            return GetServer (connectionString.ToString ());
        }

        /// <summary>
        /// Gets the MongoDb server instance using the provided connection string.
        /// </summary>
        /// <param name="connectionString">The connection string.</param>
        /// <returns></returns>
        public static MongoServer GetServer (string connectionString)
        {
            MongoClient client = new MongoClient (connectionString);
            return client.GetServer ();
        }

        /// <summary>
        /// Gets a database instance that is thread safe. <para/>
        /// Uses the static connection string set by <seealso cref="Configure"/> static method.
        /// </summary>
        /// <param name="dbName">Name of the db.</param>
        /// <returns></returns>
        public static MongoDatabase GetDatabase (string dbName)
        {
            return Server.GetDatabase (dbName);
        }

        /// <summary>
        /// Gets the database instance that is thread safe using the provided connection string.
        /// </summary>
        /// <param name="dbName">Name of the db.</param>
        /// <param name="connectionString">The connection string to the MongoDB instance/cluster.</param>
        /// <returns></returns>
        public static MongoDatabase GetDatabase (string dbName, MongoUrlBuilder connectionString)
        {
            return GetDatabase (dbName, connectionString.ToString ());
        }

        /// <summary>
        /// Gets the database instance that is thread safe using the provided connection string.
        /// </summary>
        /// <param name="dbName">Name of the db.</param>
        /// <param name="connectionString">The connection string to the MongoDB instance/cluster.</param>
        /// <returns></returns>
        public static MongoDatabase GetDatabase (string dbName, string connectionString)
        {
            MongoClient client = new MongoClient (connectionString);
            return client.GetServer ().GetDatabase (dbName);
        }
    }

    /// <summary>
    /// Some MongoDb helpers methods
    /// </summary>
    public static class MongoExtensions
    {
        /// <summary>
        /// Insert an item, retrying in case of connection errors..
        /// </summary>
        /// <typeparam name="T">The type of the item.</typeparam>
        /// <param name="col">The collection.</param>
        /// <param name="item">The item.</param>
        /// <param name="retryCount">The retry count in case of connection errors.</param>
        /// <param name="throwOnError">Throws an exception on error.</param>
        /// <param name="ignoreDuplicates">If the insert fails because of duplicated id, then returns as success.</param>
        /// <returns></returns>
        public static bool SafeInsert<T> (this MongoCollection col, T item, int retryCount = 2, bool throwOnError = false, bool ignoreDuplicates = false)
        {
            int done = 0;
            // try to update/insert and 
            // retry n times in case of connection errors            
            do
            {
                try
                {
                    if (col.Insert (item).Ok)
                        return true;
                }
                catch (MongoDuplicateKeyException dup)
                {
                    // duplicate id exception (no use to retry)
                    if (ignoreDuplicates) return true;
                    if (throwOnError) throw;
                    break;
                }
                catch (WriteConcernException wcEx)
                {
                    // duplicate id exception (no use to retry)
                    if (wcEx.CommandResult != null && wcEx.CommandResult.Code.HasValue &&
                        (wcEx.CommandResult.Code.Value == 11000 || wcEx.CommandResult.Code.Value == 11001))
                    {
                        if (throwOnError)
                            throw;
                        else
                            return ignoreDuplicates;
                    }
                    // retry limit
                    if (throwOnError && done > (retryCount - 1))
                        throw;
                }
                // System.IO.IOException ex
                catch
                {
                    if (throwOnError && done > (retryCount - 1))
                        throw;
                }
            }
            while (++done < retryCount);
            // if we got here, the operation have failled
            return false;
        }

        /// <summary>
        /// Update or insert an item, retrying in case of connection errors.
        /// </summary>
        /// <typeparam name="T">The type of the item.</typeparam>
        /// <param name="col">The collection.</param>
        /// <param name="item">The item.</param>
        /// <param name="retryCount">The retry count in case of connection errors.</param>
        /// <param name="throwOnError">Throws an exception on error.</param>
        /// <returns></returns>
        public static bool SafeSave<T> (this MongoCollection col, T item, int retryCount = 2, bool throwOnError = false)
        {
            int done = 0;
            // try to update/insert and 
            // retry n times in case of connection errors
            do
            {
                try
                {
                    if (col.Save (item).Ok)
                        return true;
                }
                // System.IO.IOException ex
                // WriteConcernException wcEx
                catch (System.Exception exAll)
                {
                    if (throwOnError && done > (retryCount - 1))
                        throw;
                }
            }
            while (++done < retryCount);
            // if we got here, the operation have failled
            return false;
        }

        /// <summary>
        /// Execute insert batch, retrying in case of connection errors.
        /// </summary>
        /// <typeparam name="T">The type of the item.</typeparam>
        /// <param name="col">The collection</param>
        /// <param name="items">list of items.</param>
        /// <param name="retryCount">The retry count in case of connection errors.</param>
        /// <param name="throwOnError">Throws an exception on error.</param>
        /// <param name="ignoreDuplicates">If the insert fails because of duplicated id, then returns as success.</param>
        /// <returns></returns>
        public static bool SafeInsertBatch<T> (this MongoCollection col, IList<T> items, int retryCount = 2, bool throwOnError = false, bool ignoreDuplicates = false)
        {
            // try to insertbatch
            try
            {
                col.InsertBatch (items);
            }
            catch (Exception ex)
            {
                // in case of a insertbatch faillure, 
                // update or insert each item individually
                if (ignoreDuplicates)
                {
                    for (int i = 0; i < items.Count; i++)
                    {
                        if (!col.SafeInsert (items[i], retryCount, throwOnError, ignoreDuplicates))
                        {
                            // in case of another faillure, give up saving items
                            return false;
                        }
                    }
                }
                else
                {
                    for (int i = 0; i < items.Count; i++)
                    {
                        if (!col.SafeSave (items[i], retryCount, throwOnError))
                        {
                            // in case of another faillure, give up saving items
                            return false;
                        }
                    }
                }
            }
            return true;
        }
    }
}
