using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace CSVReflection.Configuration
{
    [Serializable ()]
    public class ExportConfig
    {
        public string Id                 { get; set; }
        public bool   ValidateFieldTypes { get; set; }
        public string Delimiter          { get; set; }
        public string ListDelimiter      { get; set; }
        public Field[] Fields            { get; set; }
    }

    [Serializable ()]
    public class Field
    {
        public string Name               { get; set; }
        public bool   Mandatory          { get; set; }

    }
}
