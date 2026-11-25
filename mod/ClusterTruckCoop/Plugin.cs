using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using HarmonyLib;
using UnityEngine;

namespace ClusterTruckCoop
{
    // Up to 7 players (host + 6) over direct UDP (LAN, Radmin VPN, ZeroTier, or a forwarded port).
    // Host owns the level choice and the trucks and relays every player's state to everyone; each player owns their body.
    // Unity 5.3 runs scripts in a child AppDomain created after BepInEx/Doorstop start, so a loader-based
    // plugin never ticks. install.ps1 instead injects Loader.Init() at the top of Manager.Awake.
    public static class Loader
    {
        static bool done;

        public static void Init()
        {
            if (done) return;
            done = true;
            try
            {
                var go = new GameObject("ClusterTruckCoop");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<CoopPlugin>();
            }
            catch (Exception e) { Debug.LogError("[ClusterTruckCoop] falha ao iniciar: " + e); }
        }
    }

    // Another player as seen from this machine.
    class Remote
    {
        public int id;
        public EndPoint ep;       // host side: where this client talks from
        public float lastRecv = -999f, recvAt;
        public int epoch = -1;
        public bool inLevel, dead, won;
        public int anim; // 0 idle, 1 walk, 2 run, 3 in the air
        public byte[] look = Look.Default();
        public Vector3 pos, vel;
        public float yaw, time, points;
        public string name = "";
        public Puppet puppet;
        public int pauseSeqSeen; // host side: last pause request processed from this client

        public bool Fresh => Time.unscaledTime - lastRecv < CoopPlugin.Timeout;
        public bool Here => Fresh && epoch == CoopPlugin.epoch && inLevel;   // in the same level as us
        public bool Running => Here && !dead && !won;
        public string Name => CoopPlugin.DisplayName(id, name);
    }

    public partial class CoopPlugin : MonoBehaviour
    {
        public enum Mode { Off, Host, Client }

        public const int MaxPlayers = 7;
        public const float Timeout = 5f;
        const float SendInterval = 1f / 30f;
        const int TrucksPerPacket = 12; // 12 x 109 bytes stays under one 1472-byte UDP datagram

        public static CoopPlugin I;
        public static Mode mode = Mode.Off;
        public static bool applyingRemote;
        public static int resetEpoch = -99; // epoch whose GameManager.ResetMe has run (the new round really began)

        // Host: incremented on every level (re)start. Client: the host epoch it has loaded.
        public static int epoch;
        static int hostLevel, hostSeasonal;
        public static int HostLevel => hostLevel;

        static string CfgPath => Path.Combine(Path.GetDirectoryName(Application.dataPath), "ClusterTruckCoop.cfg");
        string uiIp = "127.0.0.1", uiPort = "27015", myName = "";
        bool showPanel;

        Socket sock;
        EndPoint hostEp;                       // client only
        int myId;                              // host = 0, clients get 1..3 from the host
        readonly List<Remote> remotes = new List<Remote>();
        float nextSend, restartAt = -1f, nextQaLog, holdUntil = -1f, loadedAt = -999f, releasedAt = -99f;
        readonly byte[] rbuf = new byte[65536];
        readonly Buf w = new Buf();

        // client: what the host header says
        int hLevel, hSeasonal;
        float hostInMenuSince = -1f;

        // Leave the current level the way the pause menu's "Menu" button does.
        static void LeaveLevelToMenu()
        {
            var ps = UnityEngine.Object.FindObjectOfType<pauseScreen>();
            if (ps != null) { ps.Menu(true); return; }
            Singleton<DataRecorder>.Instance.StopRecording();
            Singleton<RecordedDataPrefabPlayer>.Instance.StopPlayback();
            info.paused = false;
            info.playing = false;
            Manager.Instance().OpenMainMenuFromGame();
            info.ResetTime();
        }

        // Host: start a campaign level for the whole room (same path as the game's level select).
        void StartForAll(int level)
        {
            var man = Manager.Instance();
            if (man == null || mode != Mode.Host) return;
            Qa("EVENT sala: host comecou a fase " + level);
            LevelSeletHandler.seasonal = 0;
            info.currentLevel = level;
            man.Play();
        }

        // Host: everybody back to the room (clients follow when they see the host in the menu).
        void BackToRoom()
        {
            if (mode != Mode.Host) return;
            Qa("EVENT host voltou todos para a sala");
            SetPaused(false, -1);
            LeaveLevelToMenu();
        }
        bool hHolding, frozenForStart;

        // Shared pause: the host owns the pause state; a pause/resume pressed anywhere goes to the host, which applies it
        // for everybody. The client only follows the host once the host has acknowledged its latest request (no flicker).
        bool lastPaused;
        int pauseSeq;          // client: local pause toggles so far
        int pausedBy = -1;     // player id who paused, -1 = not paused

        void SetPaused(bool p, int by)
        {
            info.paused = p;
            lastPaused = p;
            pausedBy = p ? by : -1;
        }

        // Notice pauses made by the game's own input/menu on this machine.
        void TrackLocalPause()
        {
            if (info.paused == lastPaused) return;
            lastPaused = info.paused;
            if (mode == Mode.Host) { pausedBy = info.paused ? 0 : -1; Qa("EVENT pausa local (host) = " + info.paused); }
            else if (mode == Mode.Client) { pauseSeq++; Qa("EVENT pausa local pedida = " + info.paused); }
        }

        // test hook: -coop-testpause pauses this player 5 s after a start and resumes 4 s later (once)
        int testPauseStep = -1;
        void TestPause()
        {
            if (testPauseStep < 0 || testPauseStep > 1 || releasedAt < 0f && mode == Mode.Client) return;
            float since = Time.unscaledTime - (mode == Mode.Client ? releasedAt : loadedAt);
            if (testPauseStep == 0 && since > 5f && since < 30f) { info.paused = true; testPauseStep = 1; }
            else if (testPauseStep == 1 && since > 9f) { info.paused = false; testPauseStep = 2; }
        }


        player localPlayer;
        Rigidbody localRig;
        static scoreHandler scoreCache;
        readonly List<Remote> hereBuf = new List<Remote>();
        float nextGfx, gfxToastUntil, nextTrucks;
        bool dummyTest, menuDumped, autoShop, buyTest;
        int shopTabArg;
        float shopYawArg = -1000f; // QA: fixed preview angle
        Remote dummy;
        public static bool qa; // any -coop-* test argument: periodic state logging (it costs frames, players never get it)

        // The local player of this level, looked up once per spawn instead of every frame.
        player Me(GameManager gm, bool inLevel)
        {
            if (!inLevel) return null;
            if (localPlayer == null || !localPlayer.transform.IsChildOf(gm.transform))
            {
                localPlayer = gm.GetComponentInChildren<player>();
                localRig = localPlayer != null ? localPlayer.GetComponent<Rigidbody>() : null;
            }
            return localPlayer;
        }
        int autoplayLevel = -1, waitPlayers = 2, startLogN, roomTest = -1;
        float roomTestAt;
        bool autoNext;
        float wonAt = -1f;
        static bool forceNext;

        public static bool Connected => mode != Mode.Off && I != null && I.remotes.Exists(r => r.Fresh && (mode == Mode.Host || r.id == 0));
        public static bool ClientLocked => mode == Mode.Client && !applyingRemote;
        public static string DisplayName(int id, string n) => string.IsNullOrEmpty(n) || n == "Jogador" ? "Jogador " + (id + 1) : n;
        string MyName => DisplayName(myId, myName);

        void Awake()
        {
            I = this;
            Application.runInBackground = true; // an unfocused window would stop simulating and sending
            StartPoints.TopUp();
            HumanModel.Load();
            LoadLook();
            try
            {
                if (File.Exists(CfgPath))
                    foreach (var line in File.ReadAllLines(CfgPath))
                    {
                        if (line.StartsWith("ip=")) uiIp = line.Substring(3).Trim();
                        if (line.StartsWith("port=")) uiPort = line.Substring(5).Trim();
                        if (line.StartsWith("name=")) myName = line.Substring(5).Trim();
                        int g;
                        if (line.StartsWith("gfx=") && int.TryParse(line.Substring(4).Trim(), out g)) Gfx.Set(g);
                    }
            }
            catch (Exception e) { Log("config ilegivel: " + e.Message); }
            new Harmony("coop.clustertruck").PatchAll(typeof(CoopPlugin).Assembly);

            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                string next = i + 1 < args.Length ? args[i + 1] : "";
                if (args[i].StartsWith("-coop-")) qa = true;
                if (args[i] == "-coop-gfx") Gfx.Set(int.Parse(next));
                if (args[i] == "-coop-dummy") dummyTest = true;
                if (args[i] == "-coop-buytest") buyTest = true;
                if (args[i] == "-coop-shopyaw") shopYawArg = float.Parse(next);
                if (args[i] == "-coop-look") myLook = Look.FromText(next); // QA: wear this without owning it
                if (args[i] == "-coop-shop") { autoShop = true; int.TryParse(next, out shopTabArg); } // QA: open the shop on a tab
                if (args[i] == "-coop-lag") lagMs = float.Parse(next);
                if (args[i] == "-coop-jitter") jitterMs = float.Parse(next);
                if (args[i] == "-coop-loss") loss = float.Parse(next, System.Globalization.CultureInfo.InvariantCulture);
                if (args[i] == "-coop-port") uiPort = next;
                if (args[i] == "-coop-name") myName = next;
                if (args[i] == "-coop-autoplay") autoplayLevel = int.Parse(next);
                if (args[i] == "-coop-players") waitPlayers = int.Parse(next);
                if (args[i] == "-coop-autonext") autoNext = true;
                if (args[i] == "-coop-testpause") testPauseStep = 0;
                if (args[i] == "-coop-roomtest") roomTest = 0;
                if (args[i] == "-coop-panel") showPanel = true; // test: start with the F8 window open
            }
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-coop-host") StartHost();
                if (args[i] == "-coop-join" && i + 1 < args.Length) { uiIp = args[i + 1]; StartClient(); }
            }
            Log("ClusterTruck Coop carregado (ate " + MaxPlayers + " jogadores). F8 abre o painel.");
        }

        static void Log(string msg) { Debug.Log("[ClusterTruckCoop] " + msg); }

        void SaveCfg()
        {
            try { File.WriteAllText(CfgPath, "ip=" + uiIp.Trim() + "\nport=" + Port() + "\nname=" + myName.Trim() + "\ngfx=" + Gfx.level + "\n"); }
            catch (Exception e) { Log("nao salvou config: " + e.Message); }
        }

        // ---------------- connection ----------------

        int Port() { int p; return int.TryParse(uiPort, out p) ? p : 27015; }

        Socket NewSocket(int bindPort)
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try { s.IOControl(-1744830452, new byte[] { 0 }, null); } catch { } // SIO_UDP_CONNRESET off: ICMP resets would kill receives on Windows
            s.Bind(new IPEndPoint(IPAddress.Any, bindPort));
            s.Blocking = false;
            return s;
        }

        void StartHost()
        {
            Stop();
            try
            {
                sock = NewSocket(Port());
                try { sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true); } catch { }
                beaconTargets = null;
                mode = Mode.Host;
                myId = 0;
                SaveCfg();
                Log("[COOP] Hospedando na porta UDP " + Port());
            }
            catch (Exception e) { Log("[COOP] Falha ao hospedar: " + e.Message); Stop(); }
        }

        void StartClient()
        {
            Stop();
            try
            {
                IPAddress addr;
                if (!IPAddress.TryParse(uiIp.Trim(), out addr))
                    addr = Array.Find(Dns.GetHostAddresses(uiIp.Trim()), a => a.AddressFamily == AddressFamily.InterNetwork);
                hostEp = new IPEndPoint(addr, Port());
                sock = NewSocket(0);
                mode = Mode.Client;
                myId = -1;
                epoch = -1;
                SaveCfg();
                Log("[COOP] Conectando a " + hostEp);
            }
            catch (Exception e) { Log("[COOP] Falha ao conectar: " + e.Message); Stop(); }
        }

        void Stop()
        {
            if (sock != null) sock.Close();
            sock = null;
            hostEp = null;
            mode = Mode.Off;
            foreach (var r in remotes) if (r.puppet != null) r.puppet.Destroy();
            remotes.Clear();
            foreach (var t in Trucks.list) t.ReleaseToPhysics();
        }

        void OnDestroy() { if (sock != null) sock.Close(); }

        // Firewall workaround for hosts without admin rights: Windows Firewall lets unicast replies to a broadcast in for
        // ~3 s (default "unicast response to multicast/broadcast" setting). Broadcasting from the host port every second
        // makes the clients' packets count as replies, so LAN / virtual-LAN play can work even if the firewall prompt was
        // cancelled. Harmless when the host is already allowed. shortcut: only /24 directed broadcasts + 255.255.255.255.
        List<EndPoint> beaconTargets;
        float nextBeacon;

        void Beacon()
        {
            if (Time.unscaledTime < nextBeacon) return;
            nextBeacon = Time.unscaledTime + 1f;
            if (beaconTargets == null)
            {
                beaconTargets = new List<EndPoint> { new IPEndPoint(IPAddress.Broadcast, Port()) };
                try
                {
                    foreach (var a in Dns.GetHostAddresses(Dns.GetHostName()))
                    {
                        if (a.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a)) continue;
                        var b = a.GetAddressBytes();
                        b[3] = 255;
                        beaconTargets.Add(new IPEndPoint(new IPAddress(b), Port()));
                    }
                }
                catch { }
            }
            Begin((byte)'B');
            foreach (var t in beaconTargets) SendTo(t);
        }

        bool hasFocus = true;
        void OnApplicationFocus(bool f) { hasFocus = f; }

        // ---------------- main loop ----------------

        void Update() { long t = Now; Tick(); tkUpdate += Now - t; PerfTick(); }

        void Tick()
        {
            if (Input.GetKeyDown(KeyCode.F8)) showPanel = !showPanel;
            if (Input.GetKeyDown(KeyCode.F9)) SetGfx((Gfx.level + 1) % Gfx.names.Length);
            { var g = LocalGm(); ShopTick(g != null && g.gameObject.activeInHierarchy); }
            if (autoShop && shopInjected && gameFont != null && Time.unscaledTime > 14f) { autoShop = false; if (buyTest) BuyTest(); OpenShop(); shopTab = shopTabArg; }
            if (Time.unscaledTime >= nextGfx) { nextGfx = Time.unscaledTime + 0.5f; Gfx.Enforce(); }
            TestPause();
            if (mode != Mode.Off) TrackLocalPause(); // before Receive, or a not-yet-acked host packet would undo the press
            Receive();
            DropStale();
            Autoplay();
            if (mode == Mode.Off) return;

            var gm = LocalGm();
            bool inLevel = gm != null && gm.gameObject.activeInHierarchy;
            // right after a (re)load the previous round's done/won flags linger for a frame: only trust them once the level settled
            bool ready = inLevel && resetEpoch == epoch && Time.unscaledTime - loadedAt > 0.6f;
            if (ready) Breakables.Scan(epoch);
            bool localDone = ready && Gm.IsDone(gm);
            bool localDead = localDone && !gm.won;

            if (mode == Mode.Client)
            {
                var host = Find(0);
                if (host != null && host.Fresh && host.inLevel && host.epoch != epoch) LoadHostLevel(host.epoch);
                // the host went back to the room: follow (debounced, the host is briefly "not in a level" while loading one)
                if (host != null && host.Fresh && inLevel && hostInMenuSince >= 0f && Time.unscaledTime - hostInMenuSince > 1.5f)
                {
                    Qa("EVENT host voltou para a sala -> voltando tambem");
                    LeaveLevelToMenu();
                }
            }

            // Race rules: the level ends only when everyone reached the goal. Once nobody is still running,
            // a death anywhere means not everyone made it -> the host restarts the level for all.
            var here = hereBuf;
            here.Clear();
            foreach (var r in remotes) if (r.Here) here.Add(r);
            if (mode == Mode.Host && ready)
            {
                bool allDone = localDone && here.TrueForAll(r => r.dead || r.won);
                if (allDone && (localDead || here.Exists(r => r.dead)))
                {
                    if (restartAt < 0f) restartAt = Time.unscaledTime + 2.5f;
                    else if (Time.unscaledTime > restartAt && Connected) { restartAt = -1f; Qa("EVENT nem todos chegaram -> reiniciando"); gm.RestartLevel(); }
                }
                else restartAt = -1f;
            }

            if (ready && gm.won && HostHere && here.Count > 0 && here.TrueForAll(r => r.won))
            {
                var entries = new List<Race.Entry> { new Race.Entry { name = MyName, time = GameManager.mapTime, points = LocalPoints(gm) } };
                foreach (var r in here) entries.Add(new Race.Entry { name = r.Name, time = r.time, points = r.points });
                Race.Finish(epoch, info.currentLevel, entries);
            }

            if (sock != null && Time.unscaledTime >= nextSend)
            {
                nextSend = Time.unscaledTime + SendInterval;
                // client says "in level" only after the game's spawn + reload settled; the host stays frozen until then
                bool reportInLevel = ready;
                if (mode == Mode.Host) Beacon();
                if (mode == Mode.Client) SendClientState(gm, reportInLevel, localDead);
                else
                {
                    SendHostStates(gm, reportInLevel, localDead);
                    if (inLevel) SendBreaks();
                    if (inLevel && Time.timeScale == 0f) SendTrucks(); // paused / start hold: no physics steps, but clients need fresh stamps or they extrapolate stale data on resume
                }
            }

            UpdatePuppets(gm);
            if (dummyTest && inLevel && Me(gm, true) != null)
            {
                // QA (-coop-dummy): a fake player in front of the camera cycling idle, walk, run, jump, death
                var me = Me(gm, true).transform;
                if (dummy == null) dummy = new Remote { id = 1, name = "Teste", puppet = new Puppet(1) };
                dummy.pos = me.position + me.forward * 3.5f + me.right * (((int)(Time.unscaledTime / 2.5f) / 5) % 2 == 0 ? 0.8f : -0.8f);
                dummy.yaw = me.eulerAngles.y + 150f;
                int step = (int)(Time.unscaledTime / 2.5f) % 5;
                dummy.anim = step % 4; dummy.dead = step == 4;
                dummy.look = myLook;
                dummy.recvAt = Time.unscaledTime;
                dummy.puppet.Update(dummy, true, false, null);
                dummy.puppet.Move(dummy);
            }
            QaTick(gm, inLevel, localDead);
        }

        // While the host restarts it is briefly "not here": without this the clients ranked the round among themselves.
        bool HostHere => mode == Mode.Host || (Find(0) != null && Find(0).Here);

        Remote Find(int id)
        {
            foreach (var r in remotes) if (r.id == id) return r;
            return null;
        }

        void DropStale()
        {
            for (int i = remotes.Count - 1; i >= 0; i--)
                if (!remotes[i].Fresh && Time.unscaledTime - remotes[i].lastRecv > Timeout * 2f)
                {
                    Log("[COOP] " + remotes[i].Name + " saiu (sem resposta)");
                    if (remotes[i].puppet != null) remotes[i].puppet.Destroy();
                    remotes.RemoveAt(i);
                }
        }

        // Host freezes a fresh level until every client has loaded it, so all start on the same truck positions.
        // Ready clients are frozen the same way, and all race clocks stay at 0 until the shared start.
        // LateUpdate so it wins over game code that sets timeScale in Update.
        void LateUpdate()
        {
            if (mode == Mode.Client)
            {
                var host = Find(0);
                bool hold = host != null && host.Fresh && hHolding && host.epoch == epoch && Time.unscaledTime - loadedAt > 0.6f;
                if (hold) { Time.timeScale = 0f; GameManager.mapTime = 0f; frozenForStart = true; }
                else if (frozenForStart) { Time.timeScale = 1f; frozenForStart = false; releasedAt = Time.unscaledTime; }
                return;
            }
            if (mode != Mode.Host || holdUntil < 0f) return;
            var waiting = remotes.FindAll(r => r.Fresh && !r.Here);
            if (waiting.Count == 0 || Time.unscaledTime > holdUntil)
            {
                holdUntil = -1f;
                Time.timeScale = 1f;
                GameManager.mapTime = 0f;
                Qa("EVENT largada (" + (remotes.FindAll(r => r.Here).Count + 1) + " jogadores, faltando " + waiting.Count + ")");
            }
            else { Time.timeScale = 0f; GameManager.mapTime = 0f; }
        }

        static float LocalPoints(GameManager gm)
        {
            if (scoreCache == null || !scoreCache.transform.IsChildOf(gm.transform)) scoreCache = gm.GetComponentInChildren<scoreHandler>(true) ?? scoreHandler.Instance;
            return scoreCache != null ? scoreCache.totalScore : 0f;
        }

        void FixedUpdate() { long t = Now; Step(); tkFixed += Now - t; }

        void Step()
        {
            foreach (var r in remotes) if (r.puppet != null) r.puppet.Move(r);
            if (mode == Mode.Off || !Connected) return;
            // trucks are sampled right after a physics step, so the timestamp matches the pose (sampling in Update was up to a step stale)
            if (mode == Mode.Host && Time.unscaledTime >= nextTrucks && LocalGm() != null && LocalGm().gameObject.activeInHierarchy)
            {
                nextTrucks = Time.unscaledTime + SendInterval;
                SendTrucks();
            }
            var gm = LocalGm();
            bool inLevel = gm != null && gm.gameObject.activeInHierarchy;
            Me(gm, inLevel);
            if (mode == Mode.Client)
            {
                Trucks.ApplyRemote();
                if (inLevel && Time.timeScale > 0f)
                {
                    string carry = Trucks.CarryPlayer(localPlayer);
                    // QA: the first seconds after the start are where a client death would show up
                    if (qa && Time.unscaledTime - releasedAt < 4f && ++startLogN % 10 == 0 && localPlayer != null)
                        Qa("START t=" + (Time.unscaledTime - releasedAt).ToString("F2") + " pos=" + localPlayer.transform.position + " carry=" + carry);
                }
            }
            if (inLevel) foreach (var r in remotes) if (r.puppet != null) r.puppet.Contact(localPlayer, r);
        }

        static Manager manCache;
        static UnityEngine.Object gmOwner;
        static GameManager gmCache;
        static float nextManLookup;

        static GameManager LocalGm()
        {
            // FindObjectOfType walks the whole scene (~7 ms in a level): doing it every Update/FixedUpdate/OnGUI cost ~70 ms a frame
            if (manCache == null)
            {
                if (Time.unscaledTime < nextManLookup) return null;
                nextManLookup = Time.unscaledTime + 1f;
                manCache = UnityEngine.Object.FindObjectOfType<Manager>();
                if (manCache == null) return null;
            }
            var pr = manCache.PlayerReference;
            if (pr == null) return null;
            if (pr != gmOwner) { gmOwner = pr; gmCache = pr.GetComponent<GameManager>(); }
            return gmCache;
        }

        void LoadHostLevel(int hostEpoch)
        {
            var man = Manager.Instance();
            if (man == null) return;
            Qa("EVENT carregando fase do host: level=" + hLevel + " epoch=" + hostEpoch);
            epoch = hostEpoch;
            applyingRemote = true;
            try
            {
                LevelSeletHandler.seasonal = hSeasonal;
                AccessTools.Property(typeof(info), "CurrentCustomMap").SetValue(null, null, null); // internal setter
                man.CloseMenu();
                // same teardown as Manager.RestartLevel; a still-running ghost recorder throws and aborts the player respawn
                Singleton<DataRecorder>.Instance.StopRecording();
                Singleton<RecordedDataPrefabPlayer>.Instance.StopPlayback();
                man.ClearLevel();
                // first load spawns the player, and the game then reloads once (newLevel) - same as on the host
                man.StartLevel(hLevel, man.PlayerReference == null);
            }
            finally { applyingRemote = false; }
            loadedAt = Time.unscaledTime;
        }

        // test hooks (command line only): -coop-autoplay N starts level N once -coop-players are connected;
        // -coop-autonext presses "next" 3 s after a win
        void Autoplay()
        {
            if (autoNext && mode == Mode.Host)
            {
                var gm = LocalGm();
                if (gm == null || !gm.won || !gm.gameObject.activeInHierarchy) wonAt = -1f;
                else if (wonAt < 0f) wonAt = Time.unscaledTime;
                else if (Time.unscaledTime - wonAt > 3f) { wonAt = -1f; Qa("EVENT autonext"); gm.NextLevel(); }
            }
            if (mode != Mode.Host || Time.unscaledTime < 12f || remotes.FindAll(r => r.Fresh).Count < waitPlayers - 1) return;
            // -coop-roomtest: start 1-1 from the room, once everyone finished go back to the room, then start 1-2
            if (roomTest >= 0)
            {
                var g = LocalGm();
                bool inLvl = g != null && g.gameObject.activeInHierarchy;
                bool allWon = inLvl && g.won && remotes.TrueForAll(r => r.Here && r.won);
                if (roomTest == 0) { roomTest = 1; StartForAll(1); }
                else if (roomTest == 1 && allWon) { roomTest = 2; roomTestAt = Time.unscaledTime; }
                else if (roomTest == 2 && Time.unscaledTime - roomTestAt > 3f) { roomTest = 3; roomTestAt = Time.unscaledTime; BackToRoom(); }
                else if (roomTest == 3 && Time.unscaledTime - roomTestAt > 6f) { roomTest = 4; StartForAll(2); }
                return;
            }
            if (autoplayLevel < 0) return;
            var man = Manager.Instance();
            if (man == null) return;
            info.currentLevel = autoplayLevel;
            autoplayLevel = -1;
            Qa("EVENT autoplay level " + info.currentLevel);
            man.Play();
        }

        // ---------------- packets ----------------
        // client -> host  'P' : state block
        // host -> client  'S' : yourId, level, seasonal, holding, count, state blocks (host + other clients)
        // host -> client  'T' : epoch, count, trucks

        void SendTo(EndPoint ep)
        {
            if (sock == null || ep == null) return;
            try { sock.SendTo(w.b, w.n, SocketFlags.None, ep); } catch (SocketException) { }
        }

        void Begin(byte type) { w.n = 0; w.Write(type); }

        void WriteLocal(GameManager gm, bool inLevel, bool localDead)
        {
            var p = Me(gm, inLevel);
            var rig = p != null ? localRig : null;
            Vector3 pos = p != null ? p.transform.position : Vector3.zero;
            Vector3 vel = rig != null && !rig.isKinematic ? rig.velocity : Vector3.zero;
            // the animation state comes from this player's own game: only it knows if the feet are on a (moving) truck
            int anim = p == null ? 0 : p.lastGrounded > 0.25f ? 3 : Gm.Running(p) ? 2 : Gm.Walking(p) ? 1 : 0;
            if (dummyTest) anim = (int)(Time.unscaledTime / 2f) % 4; // QA: cycle what the others see
            WriteBlock((byte)Mathf.Max(myId, 0), epoch, inLevel, localDead, inLevel && gm.won, anim, pos, p != null ? p.transform.eulerAngles.y : 0f, vel,
                inLevel ? GameManager.mapTime : 0f, inLevel ? LocalPoints(gm) : 0f, myName, myLook);
        }

        void WriteBlock(byte id, int ep, bool inLevel, bool dead, bool won, int anim, Vector3 pos, float yaw, Vector3 vel, float time, float points, string name, byte[] look)
        {
            w.Write(id);
            w.Write(ep);
            w.Write((byte)((inLevel ? 1 : 0) | (dead ? 2 : 0) | (won ? 4 : 0) | (anim << 3)));
            w.Write(pos.x); w.Write(pos.y); w.Write(pos.z); w.Write(yaw);
            w.Write(vel.x); w.Write(vel.y); w.Write(vel.z);
            w.Write(time);
            w.Write(points);
            w.Write(name.Length > 20 ? name.Substring(0, 20) : name);
            foreach (byte b in look) w.Write(b);
        }

        void ReadBlock(BinaryReader r, Remote m)
        {
            m.epoch = r.ReadInt32();
            byte f = r.ReadByte();
            m.inLevel = (f & 1) != 0; m.dead = (f & 2) != 0; m.won = (f & 4) != 0; m.anim = (f >> 3) & 3;
            m.pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            m.yaw = r.ReadSingle();
            m.vel = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            m.time = r.ReadSingle();
            m.points = r.ReadSingle();
            m.name = r.ReadString();
            var look = r.ReadBytes(Look.Size);
            if (look.Length == Look.Size) { Look.Sanitize(look); m.look = look; }
            m.recvAt = m.lastRecv = Time.unscaledTime;
            if (float.IsNaN(m.pos.x + m.pos.y + m.pos.z + m.vel.x + m.vel.y + m.vel.z)) { m.pos = Vector3.zero; m.vel = Vector3.zero; }
        }

        void SendClientState(GameManager gm, bool inLevel, bool localDead)
        {
            Begin((byte)'P');
            WriteLocal(gm, inLevel, localDead);
            w.Write(pauseSeq);
            w.Write((byte)(info.paused ? 1 : 0));
            SendTo(hostEp);
        }

        void SendHostStates(GameManager gm, bool inLevel, bool localDead)
        {
            foreach (var to in remotes)
            {
                if (!to.Fresh) continue;
                Begin((byte)'S');
                w.Write((byte)to.id);
                w.Write(hostLevel);
                w.Write(hostSeasonal);
                w.Write((byte)(holdUntil >= 0f ? 1 : 0));
                w.Write((byte)(info.paused ? 1 : 0));
                w.Write((sbyte)pausedBy);
                w.Write(to.pauseSeqSeen);
                w.Write((byte)(gm == null || !gm.gameObject.activeInHierarchy ? 1 : 0)); // host is in the menu (the room)
                int n = 1;
                foreach (var o in remotes) if (o != to && o.Fresh) n++;
                w.Write((byte)n);
                WriteLocal(gm, inLevel, localDead);
                foreach (var o in remotes) if (o != to && o.Fresh) WriteBlock((byte)o.id, o.epoch, o.inLevel, o.dead, o.won, o.anim, o.pos, o.yaw, o.vel, o.time, o.points, o.name, o.look);
                SendTo(to.ep);
            }
        }

        // Walls/props the host's trucks broke this round. The whole list goes every tick: a lost packet just arrives next time.
        void SendBreaks()
        {
            var l = Breakables.broken;
            for (int start = 0; start < l.Count; start += 80)
            {
                int n = Math.Min(80, l.Count - start);
                Begin((byte)'D');
                w.Write(epoch);
                w.Write((byte)n);
                for (int i = start; i < start + n; i++) { w.Write(l[i].Key); w.Write(l[i].Value.x); w.Write(l[i].Value.y); w.Write(l[i].Value.z); }
                foreach (var to in remotes) if (to.Fresh) SendTo(to.ep);
            }
        }

        void SendTrucks()
        {
            var l = Trucks.list;
            for (int start = 0; start < l.Count; start += TrucksPerPacket)
            {
                int n = Math.Min(TrucksPerPacket, l.Count - start);
                Begin((byte)'T');
                w.Write(epoch);
                w.Write(Time.unscaledTime);
                w.Write((byte)n);
                for (int i = start; i < start + n; i++) l[i].Write(w);
                foreach (var to in remotes) if (to.Fresh) SendTo(to.ep);
            }
        }

        // QA: fake internet (delay, jitter that reorders packets, loss) on received packets
        float lagMs, jitterMs, loss;
        struct Late { public byte[] d; public EndPoint from; public float at; }
        readonly List<Late> late = new List<Late>();

        void Receive()
        {
            if (sock == null) return;
            for (int i = late.Count - 1; i >= 0; i--)
                if (Time.unscaledTime >= late[i].at) { var l = late[i]; late.RemoveAt(i); Handle(l.d, l.d.Length, l.from); }
            for (int guard = 0; guard < 512; guard++)
            {
                int len;
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                try
                {
                    if (sock.Available <= 0) return;
                    len = sock.ReceiveFrom(rbuf, ref from);
                }
                catch (SocketException) { continue; }
                if (len < 1) continue;
                if (lagMs + jitterMs + loss > 0f)
                {
                    if (UnityEngine.Random.value < loss) continue;
                    var d = new byte[len];
                    Buffer.BlockCopy(rbuf, 0, d, 0, len);
                    late.Add(new Late { d = d, from = from, at = Time.unscaledTime + (lagMs + UnityEngine.Random.value * jitterMs) / 1000f });
                    continue;
                }
                Handle(rbuf, len, from);
            }
        }

        void Handle(byte[] buf, int len, EndPoint from)
        {
            try
            {
                var r = new BinaryReader(new MemoryStream(buf, 0, len));
                if (mode == Mode.Client) { if (from.Equals(hostEp)) HandleFromHost(r); }
                else HandleFromClient(r, from);
            }
            catch (Exception e) { Log("[COOP] pacote invalido: " + e.Message); }
        }

        void HandleFromClient(BinaryReader r, EndPoint from)
        {
            if (r.ReadByte() != 'P') return;
            var m = remotes.Find(x => x.ep.Equals(from));
            if (m == null)
            {
                if (remotes.Count >= MaxPlayers - 1) return; // full: the extra player just keeps "conectando..."
                int id = 1;
                while (Find(id) != null) id++;
                m = new Remote { id = id, ep = from };
                remotes.Add(m);
                Log("[COOP] Jogador " + (id + 1) + " conectou: " + from);
            }
            r.ReadByte(); // the client's own idea of its id; the host's table is authoritative
            ReadBlock(r, m);
            int seq = r.ReadInt32();
            bool want = r.ReadByte() != 0;
            if (seq > m.pauseSeqSeen)
            {
                m.pauseSeqSeen = seq;
                if (info.paused != want) { SetPaused(want, m.id); Qa("EVENT " + m.Name + (want ? " pausou" : " despausou") + " para todos"); }
            }
        }

        void HandleFromHost(BinaryReader r)
        {
            byte type = r.ReadByte();
            if (type == 'S')
            {
                myId = r.ReadByte();
                hLevel = r.ReadInt32();
                hSeasonal = r.ReadInt32();
                hHolding = r.ReadByte() != 0;
                bool hPaused = r.ReadByte() != 0;
                int hPausedBy = r.ReadSByte();
                int ack = r.ReadInt32();
                bool inMenu = r.ReadByte() != 0;
                if (!inMenu) hostInMenuSince = -1f;
                else if (hostInMenuSince < 0f) hostInMenuSince = Time.unscaledTime;
                if (ack >= pauseSeq && (info.paused != hPaused || pausedBy != hPausedBy))
                {
                    if (info.paused != hPaused) Qa("EVENT pausa do host aplicada = " + hPaused);
                    SetPaused(hPaused, hPausedBy);
                }
                int n = r.ReadByte();
                for (int i = 0; i < n; i++)
                {
                    int id = r.ReadByte();
                    var m = Find(id);
                    if (m == null) { m = new Remote { id = id }; remotes.Add(m); }
                    ReadBlock(r, m);
                }
            }
            else if (type == 'T')
            {
                if (r.ReadInt32() != epoch) return;
                float ht = r.ReadSingle();
                Trucks.Sample(ht);
                int n = r.ReadByte();
                for (int i = 0; i < n; i++) Trucks.ReadInto(r, ht);
            }
            else if (type == 'D')
            {
                if (r.ReadInt32() != epoch) return;
                int n = r.ReadByte();
                for (int i = 0; i < n; i++) Breakables.Apply(r.ReadInt32(), new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
            }
        }

        // ---------------- host level hooks ----------------

        public static void OnHostLevelStarted(int level)
        {
            epoch++;
            hostLevel = level;
            hostSeasonal = LevelSeletHandler.seasonal;
            if (I != null) I.loadedAt = Time.unscaledTime;
            if (I != null && Connected) I.holdUntil = Time.unscaledTime + 6f;
            if (I != null) I.Qa("EVENT host iniciou level=" + level + " epoch=" + epoch);
        }

        // Race rule: the host can't go to the next level while someone in it hasn't reached the goal.
        public static bool PartnerBlocksNext => !forceNext && mode == Mode.Host && I.remotes.Exists(r => r.Here && !r.won);

        public static bool PartnerAlive => I != null && I.remotes.Exists(r => r.Running);

        // ---------------- puppets ----------------

        void UpdatePuppets(GameManager gm)
        {
            var me = Me(gm, gm != null && gm.gameObject.activeInHierarchy);
            bool holding = mode == Mode.Host ? holdUntil >= 0f : hHolding;
            foreach (var r in remotes)
            {
                if (r.puppet == null) r.puppet = new Puppet(r.id);
                r.puppet.Update(r, r.Here, holding, me);
            }
        }

        // ---------------- QA log ----------------

        // perf: time the mod itself takes per frame + garbage collections, for the QA log and the F8 panel
        static long tkUpdate, tkFixed, tkGui;
        static int perfFrames, perfGc, dumpEpoch = -1;
        static float perfAt;
        public static string perfText = "";
        public static int fps;
        static long Now => System.Diagnostics.Stopwatch.GetTimestamp();

        static void PerfTick()
        {
            perfFrames++;
            float dt = Time.unscaledTime - perfAt;
            if (dt < 1f) return;
            double k = 1000.0 / System.Diagnostics.Stopwatch.Frequency / perfFrames;
            int gc = GC.CollectionCount(0);
            fps = (int)(perfFrames / dt);
            perfText = string.Format("fps={0:F0} mod ms/quadro: update={1:F2} fixed={2:F2} gui={3:F2} gc/s={4:F1}", perfFrames / dt, tkUpdate * k, tkFixed * k, tkGui * k, (gc - perfGc) / dt);
            perfFrames = 0; tkUpdate = tkFixed = tkGui = 0; perfAt = Time.unscaledTime; perfGc = gc;
        }

        public void Qa(string msg) { Log("[QA] " + msg); }

        void QaTick(GameManager gm, bool inLevel, bool localDead)
        {
            if (!qa || Time.unscaledTime < nextQaLog) return;
            nextQaLog = Time.unscaledTime + 2f;
            var p = Me(gm, inLevel);
            string rs = "";
            foreach (var r in remotes)
                rs += string.Format(" | #{0} {1} ep={2} here={3} dead={4} won={5} pos={6} {7}", r.id, r.Name, r.epoch, r.Here, r.dead, r.won, r.pos,
                    (r.puppet != null && r.puppet.Solid ? "solido" : "fantasma") + " anim=" + r.anim + " visual=" + Look.ToText(r.look));
            Qa(string.Format("STATE foco=" + hasFocus + " playing=" + info.playing + " pauseScreen=" + (UnityEngine.Object.FindObjectOfType<pauseScreen>() != null) + " selectMe=" + UnityEngine.Object.FindObjectsOfType<selectMe>().Length + " cursor=" + Cursor.lockState + "/" + (Cursor.visible ? "visivel" : "oculto") + " paused=" + info.paused + " mode={0} id={1} conn={2} epoch={3} lvl={4} inLevel={5} dead={6} won={7} pos={8}{9} | {10}",
                mode, myId, Connected, epoch, info.currentLevel, inLevel, localDead, inLevel && gm.won,
                p != null ? p.transform.position.ToString() : "-", rs, Trucks.Summary() + " | " + perfText));
            if (inLevel && dumpEpoch != epoch) { dumpEpoch = epoch; Qa(Gfx.Dump()); }
            if (!menuDumped && Time.unscaledTime > 12f && Manager.Instance() != null) { menuDumped = true; Qa("MENU" + MenuDump.Tree(Manager.Instance().mainMenu.transform.parent, 0)); }
        }
    }

    // Visible body of another player: capsule in that player's color with a name tag.
    // No physics collider: contact is a capped soft push in Contact(). A hard capsule lagging one packet behind
    // at truck speed knocked players off the trucks, and a solid head let stacked players fly over the goal trigger.
    class Puppet
    {
        static readonly Color[] colors = { new Color(1f, 0.45f, 0.05f), new Color(0.1f, 0.75f, 1f), new Color(0.3f, 0.9f, 0.2f), new Color(0.95f, 0.2f, 0.85f) };

        readonly GameObject go;
        readonly Rigidbody rb;
        readonly HumanAnim anim; // null = model file missing, plain capsule
        readonly Texture2D tex;
        readonly Transform[] holders; // one per bone: worn items hang here
        readonly int id;
        byte[] applied;
        int solidEpoch = -99;
        bool solid;
        float settledAt = -1f;

        public Puppet(int id)
        {
            go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = "CoopPlayer" + (id + 1);
            UnityEngine.Object.Destroy(go.GetComponent<Collider>());
            rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            if (HumanModel.ok)
            {
                // the capsule primitive is kept only for its material (a shader that is surely in the build)
                anim = new HumanAnim();
                go.GetComponent<MeshFilter>().mesh = anim.mesh;
                var mat = go.GetComponent<Renderer>().material;
                this.id = id;
                tex = new Texture2D(32, 32) { filterMode = FilterMode.Point };
                mat.mainTexture = tex;
                mat.color = Color.white;
                holders = new Transform[HumanModel.Bones];
                for (int i = 0; i < holders.Length; i++)
                {
                    holders[i] = new GameObject("bone" + i).transform;
                    holders[i].SetParent(go.transform, false);
                }
                UnityEngine.Object.DontDestroyOnLoad(go);
                return;
            }
            go.transform.localScale = new Vector3(0.9f, 1f, 0.9f);
            go.GetComponent<Renderer>().material.color = colors[id % colors.Length];
            var face = GameObject.CreatePrimitive(PrimitiveType.Cube); // shows facing direction
            UnityEngine.Object.Destroy(face.GetComponent<Collider>());
            face.transform.SetParent(go.transform, false);
            face.transform.localPosition = new Vector3(0f, 0.55f, 0.45f);
            face.transform.localScale = new Vector3(0.6f, 0.25f, 0.3f);
            face.GetComponent<Renderer>().material.color = Color.black;
            UnityEngine.Object.DontDestroyOnLoad(go);
        }

        public void Destroy() { if (go != null) UnityEngine.Object.Destroy(go); }

        static bool Same(byte[] a, byte[] b)
        {
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // Shop: stand still at a spot in front of the menu camera.
        public void Preview(Remote r, Vector3 pos, Quaternion rot)
        {
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = CoopPlugin.PreviewLayer; // only the shop camera and light see it
            go.transform.position = rb.position = pos;
            go.transform.rotation = rb.rotation = rot;
            Update(r, true, false, null);
        }

        public bool Solid => solid;
        bool SolidFor(Remote r) => solid && !r.dead && !r.won && go.activeSelf;

        public void Update(Remote r, bool show, bool holding, player me)
        {
            if (go.activeSelf != show) go.SetActive(show);
            if (!show) return;
            if (anim != null)
            {
                anim.Tick(r.anim, r.dead, Time.deltaTime);
                if (applied == null || !Same(applied, r.look))
                {
                    applied = (byte[])r.look.Clone();
                    Look.Paint(tex, applied, id);
                    Look.Dress(applied, holders, go.GetComponent<Renderer>().sharedMaterial);
                }
                for (int i = 0; i < holders.Length; i++) { holders[i].localPosition = anim.bonePos[i]; holders[i].localRotation = anim.boneRot[i]; }
            }
            // Everybody spawns on the same spot: stay a ghost until you have moved apart once this level,
            // judged only after the start hold (stale positions from the last round lie).
            if (solidEpoch != CoopPlugin.epoch) { solidEpoch = CoopPlugin.epoch; solid = false; settledAt = -1f; }
            if (holding) settledAt = -1f;
            else if (settledAt < 0f) settledAt = Time.unscaledTime;
            if (!solid && me != null && settledAt >= 0f && Time.unscaledTime - settledAt > 1f
                && Vector3.Distance(me.transform.position, go.transform.position) > 1.6f) solid = true;
        }

        // Extrapolate with the player's velocity so the body sits where they are now, not one packet ago.
        public void Move(Remote r)
        {
            if (!go.activeSelf) return;
            Vector3 target = r.pos + r.vel * Mathf.Min(Time.unscaledTime - r.recvAt, 0.2f);
            if ((rb.position - target).sqrMagnitude > 25f) rb.position = target;
            else rb.MovePosition(Vector3.Lerp(rb.position, target, 0.5f));
            rb.MoveRotation(Quaternion.Euler(0f, r.yaw, r.dead && anim == null ? 90f : 0f)); // the model has its own death animation
        }

        // Soft sideways push away from this player.
        public void Contact(player p, Remote r)
        {
            if (p == null || p.dead || !p.canMove || !SolidFor(r)) return;
            var rig = p.GetComponent<Rigidbody>();
            if (rig == null) return;
            Vector3 d = rig.position - rb.position;
            Vector3 h = new Vector3(d.x, 0f, d.z);
            float dist = h.magnitude;
            const float reach = 1f, maxSeparation = 1.5f;
            if (Mathf.Abs(d.y) > 1.8f || dist >= reach || dist < 1e-3f) return;
            // cap the separation speed instead of adding every step: with several players touching, adds stacked up
            // and launched people off the truck
            Vector3 n = h / dist;
            float sep = Vector3.Dot(rig.velocity - r.vel, n);
            float want = Mathf.Min((reach - dist) * 4f, maxSeparation);
            if (sep < want) rig.velocity += n * (want - sep);
        }

        static GUIStyle tag;

        public void DrawName(Remote r)
        {
            var cam = Camera.main;
            if (!go.activeSelf || cam == null) return;
            Vector3 s = cam.WorldToScreenPoint(go.transform.position + Vector3.up * 1.6f);
            if (s.z <= 0f || s.z > 150f) return;
            if (tag == null) tag = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            tag.normal.textColor = colors[r.id % colors.Length];
            GUI.Label(new Rect(s.x - 80f, Screen.height - s.y - 12f, 160f, 24f), r.Name, tag);
        }
    }

    static class Gm
    {
        static readonly AccessTools.FieldRef<GameManager, bool> done = AccessTools.FieldRefAccess<GameManager, bool>("done");
        static readonly AccessTools.FieldRef<GameManager, bool> levelOverUsed = AccessTools.FieldRefAccess<GameManager, bool>("levelOverUsed");

        static readonly AccessTools.FieldRef<player, bool> walking = AccessTools.FieldRefAccess<player, bool>("walking");
        static readonly AccessTools.FieldRef<player, bool> running = AccessTools.FieldRefAccess<player, bool>("running");
        public static bool Walking(player p) => walking(p);
        public static bool Running(player p) => running(p);

        public static bool IsDone(GameManager gm) => done(gm);
        public static void AllowAnotherPress(GameManager gm) => levelOverUsed(gm) = false;
    }
}
