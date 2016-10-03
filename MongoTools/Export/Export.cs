using CSVReflection;
using CSVReflection.Configuration;
using MongoToolsLib;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;
using NLog;
using MongoToolsLib.SimpleHelpers;

namespace Export
{
    class Export
    {
        public static FlexibleOptions ProgramOptions { get; private set; }
        public static Logger          Logger  = LogManager.GetCurrentClassLogger ();
        public static DateTime        Started = DateTime.UtcNow;

        // Enum of Possible "Export Formats"
        private enum ExportFormat { JSON = 0, CSV, WRONG_FORMAT }
        
        static void Main (string[] args)
        {
            // set error exit code
            System.Environment.ExitCode = -50;
            try
            {
                // load configurations
                ProgramOptions = ConsoleUtils.Initialize (args, true);

                // start execution
                Execute (ProgramOptions);

                // check before ending for waitForKeyBeforeExit option
                if (ProgramOptions.Get ("waitForKeyBeforeExit", false))
                    ConsoleUtils.WaitForAnyKey ();
            }
            catch (Exception ex)
            {
                LogManager.GetCurrentClassLogger ().Fatal (ex);

                // check before ending for waitForKeyBeforeExit option
                if (ProgramOptions.Get ("waitForKeyBeforeExit", false))
                    ConsoleUtils.WaitForAnyKey ();

                ConsoleUtils.CloseApplication (-60, true);
            }

            // set success exit code
            ConsoleUtils.CloseApplication (0, false);
        }

        private static void Execute (FlexibleOptions options)
        {
            Logger.Info ("Start");
           
            // Args Sanity Check
            if (options.Options == null || options.Options.Count == 0)
            {
                Console.WriteLine ("No arguments received.");
                System.Environment.Exit (-101);
            }
            
            // Prompts for user Input
            Console.WriteLine ("Is the configuration correct ? Y/N");
            var key = Console.ReadKey ().Key;

            // Checking Key
            if (key == ConsoleKey.N) // N = "NO"
            {
                Console.WriteLine (" => 'NO' : Aborting");
                System.Environment.Exit (-102);
            }
            else if (key != ConsoleKey.Y) // Anything other than "N" and "Y" is an error.
            {
                Console.WriteLine (" => 'Wrong Key Pressed' : Expected either 'Y' or 'N'");
                System.Environment.Exit (-102);
            }

            Console.WriteLine (" => Proceeding with Export.");

            // Sanity Check of Config and Arguments
            if (!ValidateConfig (options))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine ("Missing MongoDB Configuration Parameter. (Server, Database, Collection and Credentials are Mandatory)");
                Console.ForegroundColor = ConsoleColor.White;
                System.Environment.Exit (-103);
            }

            // Creating instance of MongoDB
            String sourceConnString      = MongoDbContext.BuildConnectionString (options["sourceUsername"], options["sourcePassword"], options["sourceServer"], options["authDatabaseName"]);

            // Reaching Databases
            MongoDatabase sourceDatabase = MongoDbContext.GetServer (sourceConnString).GetDatabase (options["sourceDatabaseName"]);

            // Assembling "Query" to MongoDB, if any query text was provided
            QueryDocument query          = String.IsNullOrWhiteSpace (options["mongoQuery"]) ? null : new QueryDocument (QueryDocument.Parse (options["mongoQuery"]));

            // Checking if the provided Collection Exists
            if (!sourceDatabase.CollectionExists (options["collection"]))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine ("Collection [ " + options["collection"] + " ] does not exists on the specified database");
                Console.ForegroundColor = ConsoleColor.White;
                System.Environment.Exit (-104);
            }

            if (options["format"].ToUpper() == "CSV")
            {
                // Loading Export Configuration from XML File
                if (!JsonToCSV.LoadExportLayout (options["layoutFile"]))
                {
                    // Error Checking
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine ("Error Loading Export Layout");
                    Console.WriteLine ("Message : " + JsonToCSV.errorMessage);
                    Console.ForegroundColor = ConsoleColor.White;
                    System.Environment.Exit (-105);
                }
            }

            // Setting up MongoDB Cursor
            MongoCursor cursor = sourceDatabase.GetCollection<BsonDocument> (options["collection"]).Find (query);
            cursor.SetFlags (QueryFlags.NoCursorTimeout);

            // Checking for the need to apply limit
            int _limit = options.Get<int>("limit", -1);
            if (_limit != -1)
            {
                cursor.SetLimit (_limit);
            }
            
            // Counters
            int recordsProcessed = 0;

            // JSON Settings to keep the "JSON" output as "Strict"
            var jsonSettings = new JsonWriterSettings () { OutputMode = JsonOutputMode.Strict };

            // File Writer
            using (StreamWriter fWriter = new StreamWriter (options["outputFile"], false, Encoding.UTF8))
            {
                // Auto Flush
                fWriter.AutoFlush = true;

                // Output File Line
                string fileLine = String.Empty;

                // Should we add headers to the output CSV file?
                if (options["format"].ToUpper () == "CSV" && options.Get<bool>("addHeader", false))
                {
                    // Writing Headers
                    fWriter.WriteLine (JsonToCSV.Fields);
                }

                // Iterating over documents found using the query
                foreach (BsonDocument document in cursor)
                {
                    // Picking which export method will be used
                    if (options["format"].ToUpper () == "CSV")
                    {
                        // Extracting data from it
                        fileLine = JsonToCSV.BsonToCSV (document);
                    }
                    else
                    {
                        fileLine = document.ToJson (jsonSettings);
                    }

                    // Checking for errors
                    if (String.IsNullOrWhiteSpace (fileLine))
                    {
                        continue;
                    }

                    // Writing to output csv
                    fWriter.WriteLine (fileLine.Replace(System.Environment.NewLine, "<br>"));

                    // Counting
                    if (recordsProcessed++ % 100 == 0)
                    {
                        Console.WriteLine ("Processed : " + recordsProcessed);
                    }
                }
            }			
			
			Logger.Info ("End");
        }

        /// <summary>
        /// Checks the minimum parameters of this application
        /// </summary>
        /// <returns></returns>
        private static bool ValidateConfig (FlexibleOptions options)
        {
            if (String.IsNullOrEmpty (options["sourceServer"]) 
             || String.IsNullOrEmpty (options["sourceUsername"])
             || String.IsNullOrEmpty (options["sourcePassword"])
             || String.IsNullOrEmpty (options["sourceDatabaseName"]))
            {
                return false;
            }

            return true;
        }
    }
}
