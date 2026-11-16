using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Globalization;

namespace ServerLib.Json.Classes
{
    public class ItemPrice
    {
        public partial class Base
        {
            [JsonProperty("_tpl")]
            public string Tpl { get; set; }

            [JsonProperty("count")]
            public int Price { get; set; }
        }

        public partial class Base
        {
            public static Base FromJson(string json) => JsonConvert.DeserializeObject<Base>(json, Converter.Settings);
        }

        internal static class Converter
        {
            public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
            {
                MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
                DateParseHandling = DateParseHandling.None,
                Converters = {
                new IsoDateTimeConverter { DateTimeStyles = DateTimeStyles.AssumeUniversal }
            },
            };
        }
    }
}
