using CSVReflection.Configuration;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace CSVReflection
{
    public class JsonToCSV
    {
        /// <summary>
        /// Configuration Settings of this Export
        /// </summary>
        private static ExportConfig _configuration;

        #region ** Public Attributes **

        public static string errorMessage;
        public static bool   error;

        /// <summary>
        /// List of Field Names - Delimited by the configured "Delimiter"
        /// </summary>
        public static String Fields 
        {
            get { return String.Join(_configuration.Delimiter, _configuration.Fields.Select (t => t.Name).ToList()); }
        }

        #endregion

        /// <summary>
        /// Extracts all the fields configured, from the BsonDocument received.
        /// All proper type-castings and checks are executed by this method.
        /// 
        /// This method returns a line with all the fields extracted from the BsonDocument,
        /// as a CSV line, using both delimiters specified on the "Configuration" object 
        /// (fields delimiter and list delimiter)
        /// </summary>
        /// <param name="bsonDocument">BsonDocument</param>
        /// <returns>"CSV" formated line</returns>
        public static String BsonToCSV (BsonDocument bsonDocument)
        {
            // Local Dictionary
            List<String> fieldValues = new List<String> ();
            
            // Json Writer Settings - To avoid problems with 10Gen types
            var jsonSettings = new JsonWriterSettings () { OutputMode = JsonOutputMode.Strict };
            
            // Auxiliar BsonDocument
            BsonDocument auxBsonDoc;

            // Trying to extract property values out of the object
            foreach (Field field in _configuration.Fields)
            {
                // Field Value placeholder
                string fieldValue = String.Empty;

                // Field Name Tmp (to avoid messing with the Object itself)
                string fieldName = field.Name;
                                
                // Checking for the situation where the field is nested
                if (fieldName.Contains ('.'))
                {
                    // Retrieving value of this field. (E.G: Score.Total will return the value of the "Total" field, within the "Score" one)
                    auxBsonDoc = ReachInnerDocument (bsonDocument, fieldName);

                    // Changing the "Field.Name" value to it's botton one
                    fieldName = fieldName.Split ('.').Last ();
                }
                else // Resetting "BsonDocument" reference
                {
                    auxBsonDoc = bsonDocument;
                }

                // Checking for "Field not found"
                if (!auxBsonDoc.Contains (fieldName))
                {
                    // Is this field mandatory ?
                    if (field.Mandatory)
                    {
                        // Aborts, because this field should be on the field
                        return null;
                        
                    }
                    else // If not, just set the string to empty and adds it to the output
                    {
                        fieldValue = String.Empty;
                    }
                }
                else
                {                                        
                    // Converting Field to it's proper type value
                    fieldValue = BsonToType (auxBsonDoc, fieldName);
                }

                // Adding Key and Value to the dictionary
                fieldValues.Add (fieldValue.Replace (_configuration.Delimiter, String.Empty));
            }

            // Returning Joined list as a big string
            return String.Join (_configuration.Delimiter, fieldValues);
        }
        
        /// <summary>
        /// Load and parse the "XML" file that configures
        /// this export
        /// </summary>
        /// <param name="xmlPath">Path of the XML configuration file</param>
        /// <returns>True if the Loading worked; False otherwise</returns>
        public static bool LoadExportLayout (string xmlPath)
        {
            // XML Deserializer
            XmlSerializer deserializer = new XmlSerializer (typeof (ExportConfig));
            StreamReader xmlReader     = new StreamReader (xmlPath);

            // Reading Content
            try
            {
                _configuration = (ExportConfig) deserializer.Deserialize (xmlReader);
                return true;
            }
            catch (Exception ex)
            {
                // Control Variables
                error = true;
                errorMessage = ex.Message;

                // Null Value - Error
                _configuration =  null;
                return false;
            }
        }

        /// <summary>
        /// Extracts the "BsonValue" out of a BsonDocument and
        /// casts it's value to string
        /// </summary>
        /// <param name="bsonDocument">BsonDocument</param>
        /// <param name="fieldName">Name of the field to be parsed</param>
        /// <returns>String version of the field's value</returns>
        private static string BsonToType (BsonDocument bsonDocument, String fieldName)
        {
            if (bsonDocument[fieldName].IsBsonArray)      // Type : Array
            {
                // Reading "Inner Array"
                BsonArray bsonArray = bsonDocument[fieldName].AsBsonArray;

                // String "Concatenator"
                List<String> docsList = new List<String> ();

                // Iterating over elements of the array - They will all be treated as a String
                foreach (var bsonDoc in bsonArray)
                {
                    docsList.Add (BsonValueToString (bsonDoc));
                }

                return String.Join (_configuration.ListDelimiter, docsList);
            }
            else
            {
                return BsonValueToString (bsonDocument[fieldName]);
            }
        }

        /// <summary>
        /// Converts a certain "BsonValue" to String.
        /// </summary>
        /// <param name="bsonValue">BsonValue field</param>
        /// <returns>String value of the "BsonValue" value</returns>
        private static string BsonValueToString (BsonValue bsonValue)
        {
            if (bsonValue.IsBsonNull)            // Type : Null
            {
                return String.Empty;
            }
            else if (bsonValue.IsBsonArray)      // Type : Array
            {
                // Reading "Inner Array"
                BsonArray bsonArray = bsonValue.AsBsonArray;

                // String "Concatenator"
                List<String> docsList = new List<String> ();

                // Iterating over elements of the array - They will all be treated as a String
                foreach (var bsonDoc in bsonArray)
                {
                    docsList.Add (bsonDoc.AsString);
                }

                return String.Join (_configuration.ListDelimiter, docsList);
            }
            else if (bsonValue.IsObjectId)      // Type : ObjectId
            {
                return (bsonValue.AsObjectId).ToString ();
            }
            else if (bsonValue.IsValidDateTime) // Type : DateTime
            {
                return bsonValue.ToUniversalTime ().ToString ("yyyy-MM-dd");
            }
            else if (bsonValue.IsDouble)        // Type : Double
            {
                return Convert.ToString (bsonValue.AsDouble);
            }
            else if (bsonValue.IsInt32)         // Type : Int32
            {
                return Convert.ToString (bsonValue.AsInt32);
            }
            else if (bsonValue.IsInt64)         // Type : Int64
            {
                return Convert.ToString (bsonValue.AsInt64);
            }
            else if (bsonValue.IsNumeric)       // Type : Numeric
            {
                return Convert.ToString (bsonValue.AsDouble);
            }
            else if (bsonValue.IsBoolean)       // Type : Boolean
            {
                return Convert.ToString (bsonValue.AsBoolean);
            }
            else                                // Type : String
            {
                return bsonValue.AsString;
            }
        }

        /// <summary>
        /// Drills down a certain "BsonDocument" for it's inner
        /// </summary>
        /// <param name="bsonDocument"></param>
        /// <param name="fieldName"></param>
        /// <returns></returns>
        private static BsonDocument ReachInnerDocument (BsonDocument bsonDocument, String fieldName)
        {
            // New Instance of "bDoc"
            BsonDocument bDoc   = bsonDocument;
            BsonValue    bValue = null;    

            // Splitting fields by hierarchy
            String[] fieldsHierarchy = fieldName.Split ('.');

            // "Drilling Down" the fields
            foreach (string fieldLevel in fieldsHierarchy)
            {
                try
                {
                    bValue = bDoc[fieldLevel];

                    // Is this Value a "Primitive" type, or is it still a composite one?
                    if (bValue.IsBsonDocument)
                    {
                        bDoc = bValue.AsBsonDocument;
                    }
                }
                catch (Exception ex)
                {
                    error        = true;
                    errorMessage = ex.Message;
                    return BsonDocument.Create (null);
                }
            }

            // Creating a new instance of "BsonDocument" containing the value found for this field
            return new BsonDocument (fieldsHierarchy.Last(), bValue);
        }
    }
}
