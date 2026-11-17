using ComponentAce.Compression.Libs.zlib;
using ServerLib.Controllers;

namespace ServerLib.Web
{
    public class ResponseControl
    {
        public static string NoBody(string Data)
        {
            return Utilities.Utils.ClearString(Data);
        }
        public static string GetBody(string Data, int errorcode = 0, string errormsg = "null")
        {
            var Stuff = "{\"err\":" + errorcode + ",\"errmsg\":" + ErrMsg(errormsg) + ",\"data\":" + Data + "}";
            return Stuff;
        }
        public static string GetBodyCRC(string Data, int errorcode = 0, string errormsg = "null", uint crc = 0)
        {
            var Stuff = "{\"err\":" + errorcode + ",\"errmsg\":" + ErrMsg(errormsg) + ",\"data\":" + Data + ",\"crc\":" + crc + "}";
            return Stuff;
        }

        /// <summary>
        /// Reconstruction fix: most callers pass plain text ("Character not found"), which went into the JSON unquoted, so
        /// the game could not read the answer and showed a parse error instead of the message. Already quoted text
        /// (JsonConvert.SerializeObject) and null stay as they are.
        /// </summary>
        static string ErrMsg(string? errormsg)
        {
            if (errormsg == null || errormsg == "null") return "null";
            string t = errormsg.Trim();
            if (t.Length >= 2 && t[0] == '"' && t[t.Length - 1] == '"') return errormsg;
            return Newtonsoft.Json.JsonConvert.SerializeObject(errormsg);
        }
        public static string BodyWithCheckUserAccess(string SessionId, string Body)
        {
            if (AccountController.IsAccountBanned(SessionId))
            {
                return GetBody("", 205, "You are banned.");
            }
            return GetBody(Body);
        }
        public static string NullResponse()
        {
            return GetBody("null");
        }
        public static string EmptyArrayResponse()
        {
            return GetBody("[]");
        }
        public static byte[] CompressRsp(string data)
        {
            return SimpleZlib.CompressToBytes(data, 6);
        }
        public static string DeCompressReq(byte[] data)
        {
            return SimpleZlib.Decompress(data);
        }
    }
}