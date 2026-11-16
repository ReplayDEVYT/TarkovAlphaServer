using Newtonsoft.Json;
using ServerLib.Handlers;
using ServerLib.Json.Classes;
using ServerLib.Utilities;
using ServerLib.Utilities.Helpers;

namespace ServerLib.Controllers
{
    public class AccountController
    {
        static AccountController()
        {
            if (!Directory.Exists("profiles")) { Directory.CreateDirectory("profiles"); }

            Accounts = new();
            Accounts.Clear();
            ActiveAccountIds = new();
            ActiveAccountIds.Clear();
        }

        public static Dictionary<string, Account> Accounts;
        public static List<string> ActiveAccountIds;

        
        public static void Init()
        {
            ReloadAccounts();
            Debug.PrintInfo("Initialization Done!", "ACCOUNT");
        }

        public static void ReloadAccounts()
        {
            foreach (var dir in Directory.GetDirectories("profiles"))
            {
                foreach (var file in Directory.GetFiles(dir))
                {
                    if (file.Contains("account.json"))
                    { 
                        var acc = JsonConvert.DeserializeObject<Account>(File.ReadAllText(file));
                        if (acc != null)
                        {
                            //  Used to reflesh profile if already exists
                            if (Accounts.ContainsKey(acc.Aid))
                            {
                                //  Can be edited to check if account are same and if not then dont replace
                                Accounts[acc.Aid] = acc;
                            }
                            else
                                Accounts.TryAdd(acc.Aid, acc);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Remove Session from ActiveAccounts
        /// </summary>
        /// <param name="SessionId">SessionId/AccountId</param>
        public static void SessionLogout(string SessionId)
        {
            Utilities.Debug.PrintDebug($"User with ID {SessionId} has logged out");
            ActiveAccountIds.Remove(SessionId);
        }

        /// <summary>
        /// Login the Profile to the Server 
        /// </summary>
        /// <param name="JsonInfo">Json Serialized Profile</param>
        /// <returns>AccountId | FAILED</returns>
        public static string Login(Requests.Login login)
        {
            string ID = FindAccountIdByUsernameAndPassword(login.email, login.pass);

            if (ID == null)
            {
                Debug.PrintInfo("Login FAILED: " + ID);
                return "FAILED";
            }
            else
            {
                Debug.PrintInfo("Login Success: " + ID);
                if (!ActiveAccountIds.Contains(ID))
                {
                    ActiveAccountIds.Add(ID);
                }
                return ID;
            }
        }

        /// <summary>
        /// Register the Profile to the Server
        /// </summary>
        /// <param name="JsonInfo">Json Serialized Profile</param>
        /// <returns>AccountId | ALREADY_IN_USE</returns>
        public static string Register(Requests.Register register)
        {
            string ID = FindAccountIdByUsernameAndPassword(register.email, register.pass);

            if (IsEmailAlreadyInUse(register.email))
            {
                return "ALREADY_IN_USE";
            }
            if (ID == null)
            {
                string AccountID = Utils.CreateNewID();
                Account account = new()
                {
                    Aid = AccountID,
                    Id = Accounts.Count + 1,
                    Email = register.email,
                    Permission = Json.Enums.EPerms.User
                };
                // Stored the way Login compares it (useSha1 decides); it always hashed, so with useSha1 off
                // (the default) a registered account could never log in.
                account.Password = StoredPassword(register.pass);
                SaveHandler.SaveAccount(AccountID, account);
                Debug.PrintInfo("Register Success! " + AccountID);
                if (!ActiveAccountIds.Contains(AccountID))
                {
                    ActiveAccountIds.Add(AccountID);
                }
                return AccountID;
            }
            else
            {
                return ID;
            }
        }

        /// <summary>The password as Login compares it (the game client already sends an MD5 hex of what was typed).</summary>
        public static string StoredPassword(string pass)
        {
            return ConfigController.Configs.CustomSettings.Account.UseSha1 ? CryptoHelper.Hash(pass) : pass;
        }

        public static Account? FindAccountByEmail(string mail)
        {
            ReloadAccounts();
            return Accounts.Values.FirstOrDefault(a => string.Equals(a.Email, mail, StringComparison.OrdinalIgnoreCase));
        }

        private sealed class ResetCode
        {
            public string Code = "";
            public DateTime Expires;
            public int Attempts;
        }

        private static readonly Dictionary<string, ResetCode> ResetCodes = new(StringComparer.OrdinalIgnoreCase);
        private const int ResetCodeMinutes = 15;
        private const int ResetCodeAttempts = 5;

        /// <summary>
        /// Game login screen, "Forgot password": there is no mail server, so the reset code is printed on the server
        /// console (and log); whoever runs the server passes it on. Returns null on success, else the error text.
        /// </summary>
        public static string? RequestPasswordReset(string email)
        {
            var account = FindAccountByEmail(email);
            if (account == null)
                return "No account with this e-mail.";
            string code = Random.Shared.Next(0, 1000000).ToString("000000");
            lock (ResetCodes)
                ResetCodes[account.Email] = new ResetCode { Code = code, Expires = DateTime.UtcNow.AddMinutes(ResetCodeMinutes) };
            Debug.PrintInfo($"Password reset code for {account.Email}: {code} (valid {ResetCodeMinutes} minutes)", "ACCOUNT");
            return null;
        }

        /// <summary>Second step of the reset: code from the server console + new password. Null on success.</summary>
        public static string? ResetPassword(string email, string code, string pass)
        {
            var account = FindAccountByEmail(email);
            if (account == null)
                return "No account with this e-mail.";
            if (string.IsNullOrEmpty(pass))
                return "Enter a new password.";
            lock (ResetCodes)
            {
                if (!ResetCodes.TryGetValue(account.Email, out var entry) || entry.Expires < DateTime.UtcNow)
                {
                    ResetCodes.Remove(account.Email);
                    return "The reset code has expired. Request a new one.";
                }
                if (entry.Code != (code ?? "").Trim())
                {
                    if (++entry.Attempts >= ResetCodeAttempts)
                    {
                        ResetCodes.Remove(account.Email);
                        return "Wrong code. Too many attempts: request a new one.";
                    }
                    return "Wrong code.";
                }
                ResetCodes.Remove(account.Email);
            }
            account.Password = StoredPassword(pass);
            SaveHandler.SaveAccount(account.Aid, account);
            Debug.PrintInfo($"Password reset for {account.Email}", "ACCOUNT");
            return null;
        }

        /// <summary>
        /// Searching AccountId by Username and Password.
        /// </summary>
        /// <param name="name">UserName</param>
        /// <param name="passw">Password</param>
        /// <returns>AccountId | ""</returns>
        public static string FindAccountIdByUsernameAndPassword(string mail, string passw)
        {
            ReloadAccounts();
            foreach (var account in Accounts.Values)
            {
                if (ConfigController.Configs.CustomSettings.Account.UseSha1)
                {
                    if (account.Email == mail && account.Password == CryptoHelper.Hash(passw))
                    {
                        return account.Aid;
                    }
                }
                else
                {
                    if (account.Email == mail && account.Password == passw)
                    {
                        return account.Aid;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Check if already is Email is used by another user.
        /// </summary>
        /// <param name="name">Username</param>
        /// <returns>True | False</returns>
        public static bool IsEmailAlreadyInUse(string mail)
        {
            ReloadAccounts();
            foreach (var account in Accounts.Values)
            {
                if (account.Email == mail)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Check the Client character exist.
        /// </summary>
        /// <param name="SessionId">SessionId/AccountId</param>
        /// <returns>True | False</returns>
        public static bool ClientHasProfile(string SessionId)
        {
            ReloadAccounts();
            var profile = CharacterController.GetCharacter(SessionId);
            if (profile != null)
            {
                return true;
            }
            Debug.PrintInfo($"New account {SessionId} logged in!");
            return false;
        }

        /// <summary>
        /// Find account data in loaded account list
        /// </summary>
        /// <param name="SessionId">SessionId/AccountId</param>
        /// <returns>Account Data | null</returns>
        public static Account? FindAccount(string SessionId)
        {
            ReloadAccounts();
            foreach (var account in Accounts.Values)
            {
                if (account.Aid == SessionId)
                {
                    return account;
                }
            }
            return null;
        }

        /// <summary>
        /// Get the Reserved Nickname for the session/account.
        /// </summary>
        /// <param name="SessionId">SessionId/AccountId</param>
        /// <returns>Account Email</returns>
        public static string GetReservedNickname(string SessionId)
        {
            var Account = FindAccount(SessionId);
            if (Account == null) { new Exception("Account null!"); }
            return Account.Email;
        }

        /// <summary>
        /// Get if Nickname is taken
        /// </summary>
        /// <param name="JsonInfo">Json Serialized Request</param>
        /// <returns>False | True</returns>
        public static bool IsNicknameTaken(Requests.Nickname nickname)
        {
            var custom = ConfigController.Configs.CustomSettings;
            if (nickname == null) { return false; }
            if (custom.Account.CheckTakenNickname)
            {
                foreach (var acc in Accounts.Values)
                {
                    if (acc.Email.ToLower() == nickname.nickname.ToLower())
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Validate a Nickname
        /// </summary>
        /// <param name="JsonInfo">Json Serialized Request</param>
        /// <returns>tooshort | taken | OK</returns>
        public static string ValidateNickname(Requests.Nickname nickname)
        {
            if (nickname == null)
                return "taken";
            if (nickname.nickname.Length < 3)
                return "tooshort";
            if (IsNicknameTaken(nickname))
                return "taken";
            return "OK";
        }

        /// <summary>
        /// Check the Account are banned or not
        /// </summary>
        /// <param name="SessionId"></param>
        /// <returns>True if Account Permission is Blocked</returns>
        public static bool IsAccountBanned(string SessionId)
        {
            var Account = FindAccount(SessionId);
            if (Account == null) { return false; }
            return Account.Permission == Json.Enums.EPerms.Blocked;
        }

        /// <summary>
        /// Remove a directory if account exist
        /// </summary>
        /// <param name="SessionId">SessionId/AccountId</param>
        /// <returns>OK | FAILED</returns>
        public static string RemoveAccount(string SessionId)
        {
            var Account = FindAccount(SessionId);
            if (Account == null) { return "FAILED"; }
            Directory.Delete($"profiles/{SessionId}", true);
            return "OK";
        }

        /// <summary>
        /// Delete everything that this account was used for.
        /// </summary>
        /// <param name="SessionId">SessionId/AccountId</param>
        /// <returns>OK | FAILED</returns>
        public static string DeleteAccount(string SessionId)
        {
            if (Directory.Exists($"profiles/{SessionId}"))
            {
                if (Accounts.ContainsKey(SessionId))
                {
                    Accounts.Remove(SessionId);
                }
                if (ActiveAccountIds.Contains(SessionId))
                {
                    ActiveAccountIds.Remove(SessionId);
                }
                KeepAliveController.DeleteKeepAlive(SessionId);
                Directory.Delete($"profiles/{SessionId}", true);
                return "OK";
            }
            return "FAILED";
        }
    }
}
