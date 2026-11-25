using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

namespace ClusterTruckCoop
{
    // The multiplayer window. It never touches the cursor: in menus the game already frees it, and inside a level the
    // interactive parts only show while the game is paused (ESC), when the game frees it too. Forcing the cursor every
    // frame fought the game's own lock and left the camera unable to turn.
    public partial class CoopPlugin
    {
        static readonly Color[] uiColors = { new Color(1f, 0.55f, 0.15f), new Color(0.3f, 0.8f, 1f), new Color(0.4f, 0.95f, 0.3f), new Color(1f, 0.4f, 0.9f), new Color(1f, 0.9f, 0.2f), new Color(0.6f, 0.4f, 1f), new Color(0.95f, 0.95f, 0.95f) };

        int pickWorld = 1, pickLevel = 1;
        bool showHelp;
        List<KeyValuePair<string, string>> myIps;
        GUIStyle title, small, bigButton, colored;
        static GUIStyle pauseStyle;

        string PausedByName => pausedBy == myId ? MyName : pausedBy == -1 ? "" : (Find(pausedBy) != null ? Find(pausedBy).Name : "Jogador " + (pausedBy + 1));

        void Styles()
        {
            if (title != null) return;
            title = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            small = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true };
            bigButton = new GUIStyle(GUI.skin.button) { fontSize = 18, fontStyle = FontStyle.Bold, fixedHeight = 42 };
            colored = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold };
            pauseStyle = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        }

        void OnGUI() { long t = Now; Gui(); tkGui += Now - t; }

        void Gui()
        {
            Styles();
            var gm = LocalGm();
            bool inLevel = gm != null && gm.gameObject.activeInHierarchy;

            if (inLevel) DrawInLevel(gm);
            else if (shopOpen) DrawShop();
            else DrawMenu();

            foreach (var r in remotes) if (r.puppet != null) r.puppet.DrawName(r);
            if (Time.unscaledTime < gfxToastUntil) GUI.Label(new Rect(12, 12, 400, 24), "Graficos: " + Gfx.names[Gfx.level] + "   (" + fps + " FPS)", colored);
        }

        // ---------------- menus: create / join / room ----------------

        void DrawMenu()
        {
            if (mode == Mode.Off && !showPanel)
            {
                if (GUI.Button(new Rect(Screen.width - 202, 12, 190, 40), "MULTIPLAYER  (F8)", bigButton)) showPanel = true;
                return;
            }
            if (mode == Mode.Off) { Window(380, 510, "MULTIPLAYER", DrawStart); return; }
            Window(400, mode == Mode.Host ? 595 : 455, mode == Mode.Host ? "SUA SALA" : "SALA", DrawRoom);
        }

        static Texture2D panelBg;

        // Opaque panel (the game's menu text showed through a plain GUI.Box), kept inside the screen.
        internal static void Panel(Rect area)
        {
            if (panelBg == null)
            {
                panelBg = new Texture2D(1, 1);
                panelBg.SetPixel(0, 0, new Color(0.08f, 0.09f, 0.12f, 0.97f));
                panelBg.Apply();
            }
            GUI.DrawTexture(area, panelBg);
            GUI.Box(area, "");
        }

        void Window(float w, float h, string name, Action body)
        {
            h = Mathf.Min(h, Screen.height - 24f);
            // right side: the game's own menu is centered/left
            var area = new Rect(Mathf.Max(12f, Screen.width - w - 12f), 12, w, h);
            Panel(area);
            GUILayout.BeginArea(new Rect(area.x + 12, area.y + 8, w - 24, h - 16));
            GUILayout.Label(name, title);
            body();
            GUILayout.EndArea();
        }

        void DrawStart()
        {
            GUILayout.Label("Seu nome:");
            myName = GUILayout.TextField(myName, 20);
            GUILayout.Space(10);

            GUILayout.Label("1) Para jogar com amigos, UM de voces cria a sala:", small);
            if (GUILayout.Button("CRIAR SALA", bigButton)) StartHost();
            GUILayout.Label("Voce escolhe as fases. Os outros entram na sua sala.", small);
            GUILayout.Space(12);

            GUILayout.Label("2) Os outros entram com o IP que aparece na sala de quem criou:", small);
            GUILayout.BeginHorizontal();
            GUILayout.Label("IP:", GUILayout.Width(24));
            uiIp = GUILayout.TextField(uiIp);
            GUILayout.EndHorizontal();
            if (GUILayout.Button("ENTRAR NA SALA", bigButton)) StartClient();
            GUILayout.Space(8);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Porta (avancado):", small, GUILayout.Width(120));
            uiPort = GUILayout.TextField(uiPort, GUILayout.Width(70));
            GUILayout.EndHorizontal();
            if (GUILayout.Button(showHelp ? "Fechar ajuda" : "Precisa estar na mesma rede?")) showHelp = !showHelp;
            if (showHelp) HelpText();
            GUILayout.Space(8);
            GfxRow();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Fechar")) showPanel = false;
        }

        void HelpText()
        {
            GUILayout.Label(
                "- Mesma casa (mesmo Wi-Fi/roteador): funciona direto. Use o IP de \"Rede local\" da sala.\n" +
                "- Pela internet: todos instalam o Radmin VPN (gratis), entram na mesma rede (nome+senha que o host cria la) " +
                "e usam o IP \"Radmin VPN\" (26.x.x.x) da sala. ZeroTier/Tailscale tambem servem.\n" +
                "- Sem VPN: o host abre a porta UDP " + Port() + " no roteador e passa o IP publico dele.", small);
        }

        void DrawRoom()
        {
            bool waiting = !Connected;
            var host = Find(0);
            string hostName = mode == Mode.Host ? MyName : host != null ? host.Name : "...";
            int count = remotes.FindAll(r => r.Fresh).Count + 1;
            GUILayout.Label("Sala de " + hostName + "   (" + (waiting && mode == Mode.Client ? "?" : count.ToString()) + "/" + MaxPlayers + ")", colored);

            if (mode == Mode.Client && waiting)
            {
                GUILayout.Label("Procurando a sala em " + uiIp + ":" + Port() + " ...", colored);
                GUILayout.Label("Se demorar: confira o IP, se o amigo ja clicou em CRIAR SALA e se voces estao na mesma rede (veja a ajuda).", small);
                HelpText();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Cancelar")) Stop();
                return;
            }

            DrawPlayerList(false);
            GfxRow();
            GUILayout.Space(8);

            if (mode == Mode.Host)
            {
                GUILayout.Label("Passe um destes IPs para os amigos:", small);
                if (myIps == null) myIps = LocalIps();
                foreach (var ip in myIps)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(ip.Key + ":", small, GUILayout.Width(150));
                    GUILayout.Label(ip.Value, colored, GUILayout.Width(130));
                    if (GUILayout.Button("Copiar", GUILayout.Width(60))) GUIUtility.systemCopyBuffer = ip.Value;
                    GUILayout.EndHorizontal();
                }
                if (myIps.Count == 0) GUILayout.Label("(nenhum IP encontrado)", small);
                GUILayout.Label("Se o Windows pedir permissao do firewall: clique Permitir. Sem admin pode cancelar e tentar " +
                    "mesmo assim (na rede local costuma funcionar); se ninguem conseguir entrar, outro jogador cria a sala.", small);
                if (GUILayout.Button(showHelp ? "Fechar ajuda" : "Precisa estar na mesma rede?")) showHelp = !showHelp;
                if (showHelp) HelpText();
                GUILayout.Space(8);

                GUILayout.Label("Escolha a fase:", small);
                GUILayout.BeginHorizontal();
                GUILayout.Label("Mundo", GUILayout.Width(50));
                if (GUILayout.Button("<", GUILayout.Width(28))) pickWorld = Mathf.Max(1, pickWorld - 1);
                GUILayout.Label(pickWorld.ToString(), colored, GUILayout.Width(24));
                if (GUILayout.Button(">", GUILayout.Width(28))) pickWorld = Mathf.Min(9, pickWorld + 1);
                GUILayout.Space(16);
                GUILayout.Label("Fase", GUILayout.Width(40));
                if (GUILayout.Button("<", GUILayout.Width(28))) pickLevel = Mathf.Max(1, pickLevel - 1);
                GUILayout.Label(pickLevel.ToString(), colored, GUILayout.Width(24));
                if (GUILayout.Button(">", GUILayout.Width(28))) pickLevel = Mathf.Min(10, pickLevel + 1);
                GUILayout.EndHorizontal();
                if (GUILayout.Button("COMECAR FASE " + pickWorld + "-" + pickLevel + " PARA TODOS", bigButton))
                    StartForAll((pickWorld - 1) * 10 + pickLevel);
                GUILayout.Label("(Ou use o menu normal do jogo: todos entram na fase que voce escolher.)", small);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Fechar sala")) Stop();
            }
            else
            {
                GUILayout.Label("Voce esta na sala. So o host escolhe a fase:\naguardando " + hostName + " comecar...", colored);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Sair da sala")) Stop();
            }
        }

        // Graphics preset (F9 cycles it anywhere, also without the mouse).
        void GfxRow()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Graficos (F9):", GUILayout.Width(95));
            if (GUILayout.Button("<", GUILayout.Width(28))) SetGfx(Gfx.level - 1);
            GUILayout.Label(Gfx.names[Gfx.level], colored, GUILayout.Width(78));
            if (GUILayout.Button(">", GUILayout.Width(28))) SetGfx(Gfx.level + 1);
            GUILayout.Label(fps + " FPS", small);
            GUILayout.EndHorizontal();
        }

        void SetGfx(int l)
        {
            Gfx.Set(l);
            gfxToastUntil = Time.unscaledTime + 2.5f;
            SaveCfg();
        }

        void DrawPlayerList(bool withState)
        {
            DrawPlayerRow(myId < 0 ? 0 : myId, MyName + " (voce)", withState ? StateText(null) : (mode == Mode.Host ? "host" : ""));
            foreach (var r in remotes)
                if (r.Fresh) DrawPlayerRow(r.id, r.Name, withState ? StateText(r) : (r.id == 0 ? "host" : r.inLevel ? "na fase" : "na sala"));
        }

        void DrawPlayerRow(int id, string name, string state)
        {
            colored.normal.textColor = uiColors[id % uiColors.Length];
            GUILayout.BeginHorizontal();
            GUILayout.Label("● " + name, colored, GUILayout.Width(220));
            GUILayout.Label(state, small);
            GUILayout.EndHorizontal();
            colored.normal.textColor = Color.white;
        }

        string StateText(Remote r)
        {
            if (r == null)
            {
                var gm = LocalGm();
                if (gm == null || !gm.gameObject.activeInHierarchy) return "";
                return gm.won ? "chegou (" + Race.FormatTime(GameManager.mapTime) + ")" : Gm.IsDone(gm) ? "MORREU" : "correndo";
            }
            if (!r.Here) return r.inLevel ? "carregando" : "na sala";
            return r.dead ? "MORREU" : r.won ? "chegou (" + Race.FormatTime(r.time) + ")" : "correndo";
        }

        // ---------------- inside a level ----------------

        void DrawInLevel(GameManager gm)
        {
            if (mode == Mode.Off) return;
            string status = "";
            if (!Connected) status = mode == Mode.Host ? "Sala aberta, sem outros jogadores" : "Conexao com o host perdida...";
            else if (holdUntil >= 0f || (mode == Mode.Client && frozenForStart)) status = "Largada: esperando todos carregarem";
            else if (Gm.IsDone(gm))
            {
                if (PartnerAlive) status = "Esperando todos chegarem ao goal";
                else if (!gm.won || remotes.Exists(r => r.Here && r.dead)) status = "Nem todos chegaram: reiniciando...";
                else if (mode == Mode.Client) status = "O host avanca para a proxima fase";
            }
            if (status != "") GUI.Label(new Rect(12, Screen.height - 30, Screen.width - 24, 24), status, colored);

            if (Connected && info.paused && pausedBy >= 0)
                GUI.Label(new Rect(0, 20, Screen.width, 34), "PAUSADO PARA TODOS por " + PausedByName, pauseStyle);

            if (Connected && gm.won && HostHere && remotes.Exists(r => r.Here) && remotes.TrueForAll(r => !r.Here || r.won)) Race.DrawRanking(mode == Mode.Host);

            // F8 = scoreboard (read only, no cursor needed); while paused it also carries the room controls
            if (!showPanel && !info.paused) return;
            float h = 120 + 26 * (remotes.Count + 1) + (info.paused ? (mode == Mode.Host ? 185 : 85) : 0);
            var area = new Rect(Screen.width - 352, 12, 340, h);
            Panel(area);
            GUILayout.BeginArea(new Rect(area.x + 10, area.y + 6, area.width - 20, area.height - 12));
            GUILayout.Label("JOGADORES", title);
            DrawPlayerList(true);
            if (info.paused)
            {
                GfxRow();
                GUILayout.Space(8);
                if (mode == Mode.Host)
                {
                    if (GUILayout.Button("Reiniciar fase (todos)")) { Qa("EVENT host reiniciou pelo painel"); SetPaused(false, -1); Manager.Instance().RestartLevel(); }
                    if (GUILayout.Button("Proxima fase mesmo assim")) { Qa("EVENT host forcou proxima fase"); SetPaused(false, -1); forceNext = true; gm.NextLevel(); forceNext = false; }
                    if (GUILayout.Button("Voltar todos para a sala")) BackToRoom();
                }
                else if (GUILayout.Button("Sair da sala")) { Stop(); LeaveLevelToMenu(); }
            }
            else GUILayout.Label("F8 fecha. Pause (ESC) para opcoes da sala.", small);
            GUILayout.EndArea();
        }

        // ---------------- helpers ----------------

        // This PC's IPv4 addresses, labelled by the usual ranges (VPN apps use fixed ranges).
        static List<KeyValuePair<string, string>> LocalIps()
        {
            var list = new List<KeyValuePair<string, string>>();
            try
            {
                foreach (var a in Dns.GetHostAddresses(Dns.GetHostName()))
                {
                    if (a.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a)) continue;
                    byte[] b = a.GetAddressBytes();
                    string kind =
                        b[0] == 26 ? "Radmin VPN" :
                        b[0] == 25 ? "Hamachi" :
                        b[0] == 100 && b[1] >= 64 && b[1] <= 127 ? "Tailscale" :
                        b[0] == 169 && b[1] == 254 ? null :
                        b[0] == 192 && b[1] == 168 || b[0] == 10 || b[0] == 172 && b[1] >= 16 && b[1] <= 31 ? "Rede local / ZeroTier" :
                        "IP publico";
                    if (kind != null) list.Add(new KeyValuePair<string, string>(kind, a.ToString()));
                }
            }
            catch (Exception e) { Log("nao listou IPs: " + e.Message); }
            return list;
        }
    }
}
