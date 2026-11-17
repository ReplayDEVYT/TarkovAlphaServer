using System.Diagnostics;
using Debug = ServerLib.Utilities.Debug;

namespace ServerLib.Controllers
{
    /// <summary>
    /// Starts one headless game server per dedicated match (the game exe with the reconstructed
    /// Assembly-CSharp, which contains EFT.Reconstruction.RaidServer) and stops it when the match is gone.
    /// Enabled by matchmaking.gameServerPath in server.json. The game server reports back through
    /// /server/match/ready, /server/match/leave and /server/match/end.
    /// </summary>
    public static class GameServerLauncher
    {
        static readonly Dictionary<string, Process> Running = new();

        public static bool Enabled => !string.IsNullOrEmpty(ConfigController.Configs.Server?.Matchmaking?.GameServerPath);

        public static bool Launch(MatchController.MatchData match, out string error)
        {
            error = "";
            var cfg = ConfigController.Configs.Server.Matchmaking;
            var exe = cfg.GameServerPath;
            if (!File.Exists(exe))
            {
                error = "the game server exe (matchmaking.gameServerPath) does not exist on the server PC";
                Debug.PrintError($"matchmaking.gameServerPath does not exist: {exe}", "GameServer");
                return false;
            }
            // the game server quits at once when it cannot listen; say why instead of a 90 s wait
            var portProblem = Utilities.NetAddress.UdpPortProblem(match.Port);
            if (portProblem != null)
            {
                error = portProblem;
                Debug.PrintError(portProblem, "GameServer");
                return false;
            }
            Directory.CreateDirectory("logs");
            var log = Path.GetFullPath(Path.Combine("logs", $"gameserver_{match.MatchId}.log"));
            // a backend listening on one LAN address does not answer on 127.0.0.1
            var backend = Web.HTTPServer.LocalUrl;
            var info = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                Arguments = $"-batchmode -nographics -gameserver={match.Port} -match={match.MatchId} -backend={backend} -logFile \"{log}\" {cfg.GameServerArgs}".Trim(),
                UseShellExecute = false,
                CreateNoWindow = true
            };
            // Keep BepInEx (and its plugins) out of the server process; it also avoids two processes
            // fighting over BepInEx\LogOutput.log when a client runs from the same folder.
            info.Environment["DOORSTOP_DISABLE"] = "TRUE";
            try
            {
                var process = Process.Start(info);
                if (process == null) return false;
                process.EnableRaisingEvents = true;
                var matchId = match.MatchId;
                process.Exited += (s, e) => OnExited(matchId, process);
                lock (Running) Running[match.MatchId] = process;
                Debug.PrintInfo($"Game server for match [{match.MatchId}] started on port {match.Port} (pid {process.Id}, log {log})", "GameServer");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Debug.PrintError($"Could not start the game server: {ex.Message}", "GameServer");
                return false;
            }
        }

        static void OnExited(string matchId, Process process)
        {
            lock (Running)
            {
                if (Running.TryGetValue(matchId, out var p) && p == process) Running.Remove(matchId);
            }
            Debug.PrintInfo($"Game server for match [{matchId}] exited (code {SafeExitCode(process)})", "GameServer");
            // before it reported ready = it never started; the players waiting for it are told why (/client/match/problem)
            MatchController.ServerFailed(matchId, $"The game server for this raid stopped while starting (exit code {SafeExitCode(process)}). " +
                $"Its log is logs/gameserver_{matchId}.log on the server PC.");
            // A server that dies takes its match with it; otherwise players stay "Busy" in a dead match.
            MatchController.EndMatch(matchId);
        }

        static string SafeExitCode(Process p)
        {
            try { return p.ExitCode.ToString(); } catch { return "?"; }
        }

        /// <summary>Called when a match is removed. Stops its server if it is still running.</summary>
        public static void Stop(string matchId)
        {
            Process? process;
            lock (Running)
            {
                if (!Running.TryGetValue(matchId, out process)) return;
                Running.Remove(matchId);
            }
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    Debug.PrintInfo($"Game server for match [{matchId}] stopped", "GameServer");
                }
            }
            catch (Exception ex)
            {
                Debug.PrintWarn($"Could not stop game server for [{matchId}]: {ex.Message}", "GameServer");
            }
        }

        public static void StopAll()
        {
            List<string> ids;
            lock (Running) ids = Running.Keys.ToList();
            foreach (var id in ids) Stop(id);
        }
    }
}
