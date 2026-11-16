using Newtonsoft.Json;
using ServerLib.Handlers;
using ServerLib.Json.Enums;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using DB = ServerLib.Utilities.Debug;

namespace ServerLib.Controllers
{
    public class CommandsController
    {
        public static Dictionary<string, Action<object>> Commands = new()
        {
            { "help" , Help },
            { "restart" , Restart },
            { "reload" , Reload },
            { "stop" , Stop },
            { "op" , Op },
            { "deop" , DeOp },
            { "setpermission" , SetPerm },
            { "ban" , Ban },
            { "unban" , UnBan },
            { "debug" , DebugEnable },
            { "listmatches" , ListMatches },
            { "deletematches" , DeleteMatches },
            { "startmatch" , StartMatch },
            { "endmatch" , EndMatch },
            { "hostport" , HostPort },
            { "registeruser", RegisterUser },
            { "traders" , Traders },
            { "restock" , Restock },
            { "sale" , Sale }
        };
        public static Dictionary<string, EPerms> CommandsPermission = new()
        {
            { "help" , EPerms.User },
            { "restart" , EPerms.Admin },
            { "reload" , EPerms.Admin },
            { "stop" , EPerms.Console },
            { "op" , EPerms.Mod },
            { "deop" , EPerms.Mod },
            { "setpermission" , EPerms.Mod },
            { "ban" , EPerms.Mod },
            { "unban" , EPerms.Mod },
            { "debug" , EPerms.Console },
            { "listmatches" , EPerms.User },
            { "deletematches" , EPerms.Mod },
            { "startmatch" , EPerms.Mod },
            { "endmatch" , EPerms.Mod },
            { "hostport" , EPerms.Mod },
            { "registeruser", EPerms.User },
            { "traders" , EPerms.User },
            { "restock" , EPerms.Mod },
            { "sale" , EPerms.Mod }
        };

        public static void Run(string CommandName)
        {
            var splitted = CommandName.Split(" ");
            CommandName = splitted[0];
            var Parameter = splitted[1..];
            Console.WriteLine(string.Join("," , Parameter));
            if (Commands.TryGetValue(CommandName.Replace("!", ""), out var action))
            {
                action(Parameter);
            }
        }

        public static void Nothing()
        {

        }


        public static void DebugEnable(object obj)
        {
            ArgumentHandler.Debug = true;
            DB.PrintInfo("Debug has been enabled!");
        }

        public static void Restart(object obj)
        {
            var path = AppDomain.CurrentDomain.FriendlyName;
            ProcessStartInfo Info = new ProcessStartInfo();
            Info.Arguments = "/C timeout /T 3 && \"" + path + "\"";
            Info.FileName = "cmd.exe";
            Process.Start(Info);
            Console.Clear();
            Environment.Exit(0);
        }

        public static void Reload(object obj)
        {
            ServerLibClass.Stop();
            ServerLibClass.Init();
        }

        public static void Stop(object obj)
        {
            ServerLibClass.Stop();
        }

        public static void Help(object obj)
        {
            /*
            { "help" , EPerms.User },
            { "restart" , EPerms.Admin },
            { "reload" , EPerms.Admin },
            { "stop" , EPerms.Console },
            { "op" , EPerms.Mod },
            { "deop" , EPerms.Mod },
            { "setpermission" , EPerms.Mod },
            { "ban" , EPerms.Mod },
            { "unban" , EPerms.Mod },
            { "debug" , EPerms.Console },
            { "listmatches" , EPerms.User },
            { "deletematches" , EPerms.Mod },
            { "registeruser", EPerms.User }
            */
            Console.WriteLine("Commands List: " + string.Join(", ", Commands.Keys.ToList()));
            Console.WriteLine();
            Console.WriteLine("reload:\t\t\t\tRestart the server");
            Console.WriteLine("restart:\t\t\tRestart the current app (Console)");
            Console.WriteLine("stop:\t\t\t\tStops the server");
            Console.WriteLine("op <AID>:\t\t\tGives admin to the given AID");
            Console.WriteLine("deop <AID>:\t\t\tRemoves admin from the given AID");
            Console.WriteLine("setpermission <AID> <PermId>:\tSets permissions for the given AID");
            Console.WriteLine("ban <AID>:\t\t\tBan the given AID");
            Console.WriteLine("unban <AID>:\t\t\tUnban the given AID");
            Console.WriteLine("debug:\t\t\t\tEnable Debug options");
            Console.WriteLine("listmatches:\t\t\tList all matches");
            Console.WriteLine("deletematches:\t\t\tDelete all matches");
            Console.WriteLine("startmatch <matchId>:\t\tStart a waiting match now");
            Console.WriteLine("endmatch <matchId>:\t\tEnd a match and free its port");
            Console.WriteLine("hostport <nickname> <port>:\tPlayer hosts their next match on this port (0 = stop)");
            Console.WriteLine("registeruser <mail> <pass>:\t\tRegister designed user");
            Console.WriteLine();
            Console.WriteLine("AID stands for 'Account ID'. You can find a user's AID in ConsoleApp's 'profiles' folder.");
            Console.WriteLine("The name of the folder is the user's AID.");
        }

        public static void Op(object obj)
        {
            var x = (string[])obj;
            if (x.Length == 0)
            {
                DB.PrintWarn("No User to OP, use: !op <AID>");
                return;
            }               
            var AID = x[x.Length-1];
            var account = AccountController.FindAccount(AID);
            if (account == null)
            {
                DB.PrintWarn("Profile null, cannot give OP to a non-existent profile!");
                return;
            }
            account.Permission = EPerms.Admin;
            SaveHandler.SaveAccount(AID, account);
            DB.PrintInfo($"User {AID} is now {EPerms.Admin}");
        }

        public static void SetPerm(object obj)
        {
            var x = (string[])obj; 
            if (x.Length > 2)
            {
                DB.PrintWarn("No User or Permission defined. Use: !setpermission <AID>");
                return;
            }
            var AID = x[0];
            var perm = int.Parse(x[1]);
            var account = AccountController.FindAccount(AID);
            if (account == null)
            {
                DB.PrintWarn("Profile null, cannot set permission for a non-existent profile!");
                return;
            }
            account.Permission = (EPerms)perm;
            SaveHandler.SaveAccount(AID, account);
            DB.PrintInfo($"User {AID} is now {(EPerms)perm}");
        }

        public static void DeOp(object obj)
        {
            var x = (string[])obj;
            if (x.Length == 0)
            {
                DB.PrintWarn("No User to DEOP, use: !deop <AID>");
                return;
            }
            var AID = x[0];
            var account = AccountController.FindAccount(AID);
            if (account == null)
            {
                DB.PrintWarn("Profile null, cannot remove OP from non-existent profile!");
                return;
            }
            account.Permission = EPerms.User;
            SaveHandler.SaveAccount(AID, account);
            DB.PrintInfo($"User {AID} is now {EPerms.User}");
        }

        public static void Ban(object obj)
        {
            var x = (string[])obj;
            var AID = x[0];
            var account = AccountController.FindAccount(AID);
            if (account == null)
            {
                DB.PrintWarn("Profile null, cannot ban non-existent profile!");
                return;
            }
            account.Permission = EPerms.Blocked;
            SaveHandler.SaveAccount(AID, account);
            DB.PrintInfo($"User {AID} is now Banned");
        }

        public static void UnBan(object obj)
        {
            var x = (string[])obj;
            var AID = x[0];
            var account = AccountController.FindAccount(AID);
            if (account == null)
            {
                DB.PrintWarn("Profile null, cannot unban non-existent profile!");
                return;
            }
            account.Permission = EPerms.User;
            SaveHandler.SaveAccount(AID, account);
            DB.PrintInfo($"User {AID} unbanned");
        }

        static IEnumerable<string> TraderIds() =>
            Directory.Exists("Files/assort") ? Directory.GetFiles("Files/assort", "*.json").Select(f => Path.GetFileNameWithoutExtension(f)!) : Enumerable.Empty<string>();

        /// <summary>!traders: restock countdown and running sale of every trader.</summary>
        public static void Traders(object obj)
        {
            foreach (var id in TraderIds())
            {
                long left = TraderStock.SecondsToRestock(id);
                int sale = TraderStock.SalePercent(id);
                DB.PrintInfo($"{id}: restock in {(left > 0 ? TimeSpan.FromSeconds(left).ToString() : "never")}{(sale > 0 ? $", sale -{sale}%" : "")}");
            }
        }

        /// <summary>!restock [traderId|all]: restock now (also rolls a sale).</summary>
        public static void Restock(object obj)
        {
            var x = (string[])obj;
            var ids = x.Length < 1 || x[0] == "all" ? TraderIds().ToList() : new List<string> { x[0] };
            foreach (var id in ids) TraderStock.RestockNow(id);
            DB.PrintInfo($"Restocked {ids.Count} trader(s). Players see it when they reopen or reload the trader.");
        }

        /// <summary>!sale <traderId|all> <percent>: start a sale until the next restock (0 ends it).</summary>
        public static void Sale(object obj)
        {
            var x = (string[])obj;
            if (x.Length < 2 || !int.TryParse(x[1], out int percent)) { DB.PrintWarn("Use: !sale <traderId|all> <percent>"); return; }
            var ids = x[0] == "all" ? TraderIds().ToList() : new List<string> { x[0] };
            foreach (var id in ids) TraderStock.SetSale(id, percent);
            DB.PrintInfo(percent > 0 ? $"Sale -{percent}% at {ids.Count} trader(s) until their next restock." : $"Sale ended at {ids.Count} trader(s).");
        }

        public static void ListMatches(object obj)
        {
            var matches = MatchController.Snapshot();
            if (matches.Count == 0) { DB.PrintInfo("No matches."); return; }
            foreach (var m in matches)
                DB.PrintInfo($"[{m.MatchId}] {m.Location}/{m.Mode} {(m.IsStarted ? "started" : "waiting")} {m.Ip}:{m.Port} ({m.HostType}) players: {m.Users.Count}");
        }
        public static void DeleteMatches(object obj)
        {
            MatchController.ClearAll();
            DB.PrintInfo($"Matches Cleared!");
        }

        public static void StartMatch(object obj)
        {
            var x = (string[])obj;
            if (x.Length < 1) { DB.PrintWarn("Use: !startmatch <matchId>"); return; }
            DB.PrintInfo(MatchController.ForceStart(x[0]) ? $"Match {x[0]} started." : $"No match {x[0]}.");
        }

        public static void EndMatch(object obj)
        {
            var x = (string[])obj;
            if (x.Length < 1) { DB.PrintWarn("Use: !endmatch <matchId>"); return; }
            DB.PrintInfo(MatchController.EndMatch(x[0]) ? $"Match {x[0]} ended." : $"No match {x[0]}.");
        }

        /// <summary>!hostport &lt;nickname&gt; &lt;port&gt;: that player hosts their next match on this port (0 = stop).</summary>
        public static void HostPort(object obj)
        {
            var x = (string[])obj;
            if (x.Length < 2 || !int.TryParse(x[1], out int port) || port < 0 || port > 65535)
            {
                DB.PrintWarn("Use: !hostport <nickname> <port>   (port 0 = stop hosting)");
                return;
            }
            var found = CharacterController.SearchNickname(x[0]);
            if (found.Count == 0) { DB.PrintWarn($"No character named {x[0]}."); return; }
            MatchController.SetHostPort(found[0].Aid, port);
            DB.PrintInfo(port == 0 ? $"{x[0]} no longer hosts." : $"{x[0]} hosts their next match on port {port}.");
        }

        public static void RegisterUser(object obj)
        {
            var x = (string[])obj;
            var email = x[0];
            var pass = x[1];
            if (x.Length < 1)
            {
                DB.PrintWarn("Command not complete. Use: !registeruser <mail> <pass> (with <mail> and <pass> being your username and password)");
                return;
            }
            var account = AccountController.Register(new()
            { 
                email = email,
                pass = pass,
            });
            DB.PrintInfo($"AID {account} created!");
        }
    }
}
