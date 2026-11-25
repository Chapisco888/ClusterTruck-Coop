using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ClusterTruckCoop
{
    // The character shop: your character on the left, the menu on the right. Colours are free, clothes and accessories
    // cost the game's own points. Opened from a "LOJA" entry cloned into the game's main menu (same font and sounds).
    public partial class CoopPlugin
    {
        public static byte[] myLook = Look.Default();
        static int owned;            // bit per Look.Items index
        byte[] tryLook;              // what the preview wears: myLook plus items being tried on before buying
        bool shopOpen, shopInjected;
        int shopTab;
        float nextInject;
        Puppet shopPuppet;
        Camera shopCam;
        Vector2 shopScroll;
        public const int PreviewLayer = 31;
        static readonly Vector3 Stage = new Vector3(0f, -3000f, 0f);
        static float ShopWidth => Mathf.Min(440f, Screen.width * 0.52f);
        readonly Remote shopMe = new Remote { name = "" };
        Font gameFont;
        GUIStyle shopTitle, shopText, swatch;
        static readonly string[] tabs = { "CORES", "ROUPAS", "ACESSORIOS" };
        static readonly string[] partNames = { "Pele", "Olhos", "Cabelo", "Camisa", "Calca" };
        static readonly string[] slotNames = { "Cabeca", "Rosto", "Costas", "Roupa" };

        void LoadLook()
        {
            myLook = Look.FromText(PlayerPrefs.GetString("coopLook", ""));
            owned = PlayerPrefs.GetInt("coopOwned", 0);
            for (int s = 0; s < Look.Slots; s++) // never wear what isn't owned (edited save)
                if (myLook[5 + s] > 0 && (owned & (1 << (myLook[5 + s] - 1))) == 0) myLook[5 + s] = 0;
        }

        void SaveLook()
        {
            PlayerPrefs.SetString("coopLook", Look.ToText(myLook));
            PlayerPrefs.SetInt("coopOwned", owned);
        }

        // Adds "LOJA" right under "ABILITIES" and moves the entries below it down one line.
        void InjectShopButton()
        {
            var man = Manager.Instance();
            if (man == null || man.mainMenu == null) return;
            shopInjected = true;
            var main = man.mainMenu.transform;
            var ab = main.Find("abilities") as RectTransform;
            if (ab == null || main.Find("coopShop") != null) return;
            float y0 = ab.anchoredPosition.y;
            foreach (Transform ch in main)
            {
                var c = ch as RectTransform;
                if (c != null && c != ab && c.anchoredPosition.y < y0 && Mathf.Abs(c.anchoredPosition.x) < 1f) c.anchoredPosition += new Vector2(0f, -60f);
            }
            var go = (GameObject)Instantiate(ab.gameObject);
            go.name = "coopShop";
            var rt = (RectTransform)go.transform;
            rt.SetParent(main, false);
            rt.SetSiblingIndex(ab.GetSiblingIndex() + 1);
            rt.anchoredPosition = new Vector2(ab.anchoredPosition.x, y0 - 60f);
            rt.sizeDelta = new Vector2(170f, ab.sizeDelta.y);
            var text = go.GetComponentInChildren<Text>();
            text.text = "LOJA";
            gameFont = text.font;
            foreach (var et in go.GetComponents<EventTrigger>()) // keep the hover/click sounds, drop "open the abilities menu"
                foreach (var e in et.triggers)
                    if (e.callback.GetPersistentEventCount() > 0 && e.callback.GetPersistentMethodName(0) == "ChangeMenu") { Destroy(et); break; }
            var b = go.GetComponent<Button>();
            b.onClick = new Button.ButtonClickedEvent();
            b.onClick.AddListener(OpenShop);
        }

        void OpenShop()
        {
            if (shopOpen || !HumanModel.ok) return;
            shopOpen = true;
            showPanel = false;
            tryLook = (byte[])myLook.Clone();
            Manager.Instance().MainCanvas.SetActive(false);
        }

        void CloseShop(bool backToMenu)
        {
            shopOpen = false;
            SaveLook();
            if (shopPuppet != null) shopPuppet.Update(shopMe, false, false, null);
            if (shopCam != null) shopCam.gameObject.SetActive(false);
            if (backToMenu) Manager.Instance().MainCanvas.SetActive(true);
        }

        void ShopTick(bool inLevel)
        {
            if (!shopInjected && Time.unscaledTime >= nextInject) { nextInject = Time.unscaledTime + 1f; InjectShopButton(); }
            if (!shopOpen) return;
            if (inLevel) { CloseShop(false); return; } // the host started a level while we were shopping
            // Own camera drawn over the left of the menu: the menu camera's depth-of-field blurred the character.
            // The stage is far below the map so nothing else is in view.
            if (shopCam == null)
            {
                shopCam = new GameObject("CoopShopCam").AddComponent<Camera>();
                DontDestroyOnLoad(shopCam.gameObject);
                shopCam.clearFlags = CameraClearFlags.Depth;
                shopCam.depth = 60f;
                shopCam.fieldOfView = 30f;
                shopCam.transform.position = Stage + new Vector3(0f, 0.05f, 4.7f);
                shopCam.transform.rotation = Quaternion.LookRotation(Vector3.back);
                // own white light on the character only: the menu scene is lit blue (night), which turned every skin tone green
                shopCam.cullingMask = 1 << PreviewLayer;
                var light = new GameObject("CoopShopLight").AddComponent<Light>();
                light.transform.SetParent(shopCam.transform, false);
                light.transform.localRotation = Quaternion.Euler(25f, -25f, 0f);
                light.type = LightType.Directional;
                light.intensity = 0.9f;
                light.cullingMask = 1 << PreviewLayer;
            }
            shopCam.gameObject.SetActive(true);
            shopCam.rect = new Rect(0f, 0f, 1f - (ShopWidth + 12f) / Screen.width, 1f);
            if (shopPuppet == null) shopPuppet = new Puppet(Mathf.Max(myId, 0));
            shopMe.id = Mathf.Max(myId, 0);
            shopMe.look = tryLook;
            shopPuppet.Preview(shopMe, Stage, Quaternion.Euler(0f, shopYawArg > -999f ? shopYawArg : Mathf.Sin(Time.unscaledTime * 0.7f) * 60f, 0f));
        }

        void DrawShop()
        {
            if (shopTitle == null)
            {
                shopTitle = new GUIStyle(GUI.skin.label) { font = gameFont, fontSize = 34, alignment = TextAnchor.MiddleCenter };
                shopText = new GUIStyle(GUI.skin.label) { font = gameFont, fontSize = 20 };
                var px = new Texture2D(1, 1);
                px.SetPixel(0, 0, Color.white);
                px.Apply();
                swatch = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold, fontSize = 13, margin = new RectOffset(1, 1, 1, 1), padding = new RectOffset(0, 0, 0, 0) };
                swatch.normal.background = swatch.hover.background = swatch.active.background = px;
            }
            float w = ShopWidth;
            var area = new Rect(Screen.width - w - 12f, 12f, w, Screen.height - 24f);
            Panel(area);
            GUILayout.BeginArea(new Rect(area.x + 12, area.y + 6, area.width - 24, area.height - 12));
            GUILayout.Label("LOJA", shopTitle);
            GUILayout.Label("PONTOS: " + pointsHandler.Points.ToString("F0"), shopText);
            shopTab = GUILayout.Toolbar(shopTab, tabs, GUILayout.Height(30));
            GUILayout.Space(8);
            shopScroll = GUILayout.BeginScrollView(shopScroll);
            if (shopTab == 0)
            {
                GUILayout.Label("As cores sao gratis.", small);
                for (int part = 0; part < 5; part++) ColorRow(part);
            }
            else if (shopTab == 1) { ItemList(3); ItemList(2); }
            else { ItemList(0); ItemList(1); }
            GUILayout.EndScrollView();
            if (GUILayout.Button("VOLTAR", bigButton)) CloseShop(true);
            GUILayout.EndArea();
        }

        void ColorRow(int part)
        {
            var pal = part == 0 ? Look.skins : Look.colors;
            GUILayout.Label(partNames[part], shopText);
            GUILayout.BeginHorizontal();
            if (part == 3) Swatch(part, Look.Auto, uiColors[Mathf.Max(myId, 0) % uiColors.Length], "A"); // the colour of your player slot
            for (int i = 0; i < pal.Length; i++) Swatch(part, (byte)i, pal[i], "");
            GUILayout.EndHorizontal();
        }

        static float SwatchSize => Mathf.Min(28f, (ShopWidth - 44f) / 13f - 2f); // 12 colours + the automatic one in a row

        void Swatch(int part, byte value, Color c, string label)
        {
            var old = GUI.backgroundColor;
            GUI.backgroundColor = c;
            GUI.contentColor = c.grayscale > 0.5f ? Color.black : Color.white;
            if (GUILayout.Button(myLook[part] == value ? "X" : label, swatch, GUILayout.Width(SwatchSize), GUILayout.Height(SwatchSize)))
            {
                myLook[part] = tryLook[part] = value;
                SaveLook();
            }
            GUI.backgroundColor = old;
            GUI.contentColor = Color.white;
        }

        void Buy(int i)
        {
            var it = Look.Items[i];
            if ((owned & (1 << i)) != 0 || pointsHandler.Points < it.price) return;
            pointsHandler.AddPoints(-it.price);
            owned |= 1 << i;
            myLook[5 + it.slot] = (byte)(i + 1);
            if (tryLook != null) tryLook[5 + it.slot] = (byte)(i + 1);
            SaveLook();
            Qa("EVENT loja: comprou " + it.name + " por " + it.price + ", pontos=" + pointsHandler.Points + " visual=" + Look.ToText(myLook));
        }

        // QA (-coop-buytest): buy the cheapest item for real, check the result, then undo it so the player's save is untouched
        void BuyTest()
        {
            float before = pointsHandler.Points;
            int ownedBefore = owned;
            var lookBefore = (byte[])myLook.Clone();
            owned &= ~1;
            Buy(0);
            bool ok = pointsHandler.Points == before - Look.Items[0].price && (owned & 1) != 0 && myLook[5] == 1;
            Buy(0); // second time must do nothing
            ok &= pointsHandler.Points == before - Look.Items[0].price;
            pointsHandler.AddPoints(Look.Items[0].price);
            owned = ownedBefore;
            myLook = lookBefore;
            SaveLook();
            Qa("EVENT loja teste de compra: " + (ok ? "OK" : "FALHOU") + " pontos antes=" + before + " depois de desfazer=" + pointsHandler.Points);
        }

        void ItemList(int slot)
        {
            GUILayout.Label(slotNames[slot], shopText);
            for (int i = 0; i < Look.Items.Length; i++)
            {
                var it = Look.Items[i];
                if (it.slot != slot) continue;
                byte id = (byte)(i + 1);
                bool have = (owned & (1 << i)) != 0, worn = myLook[5 + slot] == id, trying = tryLook[5 + slot] == id;
                GUILayout.BeginHorizontal();
                GUILayout.Label(it.name, GUILayout.Width(ShopWidth * 0.34f));
                if (have)
                {
                    if (GUILayout.Button(worn ? "TIRAR" : "USAR")) { myLook[5 + slot] = tryLook[5 + slot] = worn ? (byte)0 : id; SaveLook(); }
                }
                else
                {
                    if (GUILayout.Button(trying ? "TIRAR" : "PROVAR", GUILayout.Width(ShopWidth * 0.22f))) tryLook[5 + slot] = trying ? myLook[5 + slot] : id;
                    GUI.enabled = pointsHandler.Points >= it.price;
                    if (GUILayout.Button(it.price + " pts")) Buy(i);
                    GUI.enabled = true;
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(6);
        }
    }
}
