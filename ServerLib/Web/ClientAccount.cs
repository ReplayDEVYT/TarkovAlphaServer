using NetCoreServer;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ServerLib.Controllers;
using ServerLib.Utilities;
using static ServerLib.Web.HTTPServer;

namespace ServerLib.Web
{
    /// <summary>
    /// Account requests from the game's login screen (before login, so no session): create an account and reset a
    /// forgotten password. Bodies come zlib-compressed like every /client request; "pass" is the MD5 hex the game
    /// makes of the typed password (LoginScreen.method_4), the same value /client/game/login receives.
    /// Answers: {"err":0,"data":"..."} on success, err 1 with errmsg on failure.
    /// </summary>
    public class ClientAccount
    {
        static JObject? ReadBody(HttpRequest request)
        {
            try
            {
                return JObject.Parse(ResponseControl.DeCompressReq(request.BodyBytes));
            }
            catch
            {
                try { return JObject.Parse(request.Body); } catch { return null; }
            }
        }

        static bool Answer(HttpsBackendSession session, string? error, string data = "ok")
        {
            string rsp = error == null
                ? ResponseControl.GetBody(JsonConvert.SerializeObject(data))
                : ResponseControl.GetBody("null", 1, JsonConvert.SerializeObject(error));
            Utils.SendUnityResponse(session, rsp);
            return true;
        }

        /// <summary>{"email","pass"} -> the new account id. The game then logs in and creates the character.</summary>
        [HTTP("POST", "/client/account/register")]
        public static bool Register(HttpRequest request, HttpsBackendSession session)
        {
            var body = ReadBody(request);
            string email = body?["email"]?.ToString().Trim() ?? "";
            string pass = body?["pass"]?.ToString() ?? "";
            if (email.Length == 0 || pass.Length == 0)
                return Answer(session, "Enter an e-mail and a password.");
            if (AccountController.IsEmailAlreadyInUse(email))
                return Answer(session, "An account with this e-mail already exists.");
            string id = AccountController.Register(new Json.Classes.Requests.Register { email = email, pass = pass });
            Utilities.Debug.PrintInfo($"Account created from the game: {email} ({id})", "ACCOUNT");
            return Answer(session, null, id);
        }

        /// <summary>{"email"}: a reset code is printed on the server console.</summary>
        [HTTP("POST", "/client/account/reset/request")]
        public static bool ResetRequest(HttpRequest request, HttpsBackendSession session)
        {
            var body = ReadBody(request);
            string email = body?["email"]?.ToString().Trim() ?? "";
            return Answer(session, email.Length == 0 ? "Enter your e-mail." : AccountController.RequestPasswordReset(email));
        }

        /// <summary>{"email","code","pass"}: sets the new password.</summary>
        [HTTP("POST", "/client/account/reset/confirm")]
        public static bool ResetConfirm(HttpRequest request, HttpsBackendSession session)
        {
            var body = ReadBody(request);
            string email = body?["email"]?.ToString().Trim() ?? "";
            return Answer(session, AccountController.ResetPassword(email, body?["code"]?.ToString() ?? "", body?["pass"]?.ToString() ?? ""));
        }
    }
}
