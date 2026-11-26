using UnityEngine;

namespace ClusterTruckCoop
{
    public partial class CoopPlugin
    {
        readonly Puppet[] lobbyModels = new Puppet[MaxPlayers];
        readonly Remote[] lobbyPlayers = new Remote[MaxPlayers];
        readonly Rect[] lobbyNames = new Rect[MaxPlayers], lobbyFeet = new Rect[MaxPlayers];
        readonly Remote lobbyMe = new Remote();
        bool lobbyVisible;
        int lobbyCount;
        Texture2D lobbyRing;
        GUIStyle nameTag;

        void LobbyTick(bool visible, bool inLevel)
        {
            if (!visible && !lobbyVisible) return;
            var man = Manager.Instance();
            if (!visible || man == null || man.MainCanvas == null || !shopInjected)
            {
                if (!lobbyVisible) return;
                lobbyVisible = false;
                foreach (var model in lobbyModels) if (model != null) model.Update(lobbyMe, false, false, null);
                if (shopCam != null) shopCam.gameObject.SetActive(false);
                if (inLevel) showPanel = false;
                else if (!shopOpen && man != null && man.MainCanvas != null) man.MainCanvas.SetActive(true);
                return;
            }
            if (!lobbyVisible)
            {
                man.MainCanvas.SetActive(false);
                Cursor.visible = true;
                lobbyVisible = true;
            }
            PreparePreviewCamera();
            lobbyMe.id = mode == Mode.Off || myId < 0 || myId >= MaxPlayers ? 0 : myId;
            lobbyMe.name = MyName;
            lobbyMe.look = myLook;
            lobbyPlayers[0] = lobbyMe;
            lobbyCount = 1;
            for (int id = 0; id < MaxPlayers; id++)
            {
                if (id == lobbyMe.id) continue;
                var member = Find(id);
                if (member != null && member.Fresh) lobbyPlayers[lobbyCount++] = member;
                else if (lobbyModels[id] != null) lobbyModels[id].Update(lobbyMe, false, false, null);
            }
            for (int i = 0; i < lobbyCount; i++) PlaceMember(i);
        }

        void PlaceMember(int slot)
        {
            var r = lobbyPlayers[slot];
            bool front = slot == 0 || lobbyCount == 2;
            bool middle = slot == 1 || slot == 2;
            int backCount = Mathf.Max(1, lobbyCount - 3);
            float x = lobbyCount == 2 ? (slot == 0 ? 0.32f : 0.68f) :
                front ? 0.5f : middle ? (slot == 1 ? 0.23f : 0.77f) : (slot - 3 + 0.5f) / backCount;
            float foot = front ? 0.07f : middle ? 0.32f : 0.60f;
            float height = front ? (lobbyCount == 1 ? 0.64f : 0.40f) : middle ? 0.31f : 0.25f;
            height = Mathf.Min(height, shopCam.pixelWidth / (float)shopCam.pixelHeight * (front ? 0.70f : 0.44f));
            float depth = 1.9f / (2f * height * Mathf.Tan(shopCam.fieldOfView * Mathf.Deg2Rad / 2f));
            Vector3 basePos = shopCam.ViewportToWorldPoint(new Vector3(x, foot, depth));
            if (lobbyModels[r.id] == null) lobbyModels[r.id] = new Puppet(r.id);
            lobbyModels[r.id].Preview(r, basePos + Vector3.up, Quaternion.Euler(0f, (0.5f - x) * 22f, 0f), true);
            float cx = x * shopCam.pixelWidth, fy = (1f - foot) * Screen.height;
            float w = shopCam.pixelWidth * (front ? 0.48f : middle ? 0.42f : 1f / backCount);
            lobbyNames[slot] = new Rect(cx - w / 2f, fy - height * Screen.height * 1.22f - 26f, w, 26f);
            float ring = height * Screen.height * 0.52f;
            lobbyFeet[slot] = new Rect(cx - ring / 2f, fy - ring * 0.08f, ring, ring * 0.16f);
        }

        void DrawLobbyNames()
        {
            if (Event.current.type != EventType.Repaint) return;
            if (nameTag == null)
            {
                nameTag = new GUIStyle(GUI.skin.label) { font = gameFont, alignment = TextAnchor.MiddleCenter, clipping = TextClipping.Clip };
                lobbyRing = new Texture2D(96, 24);
                for (int y = 0; y < 24; y++)
                    for (int x = 0; x < 96; x++)
                    {
                        float radius = new Vector2((x - 47.5f) / 47f, (y - 11.5f) / 11f).magnitude;
                        lobbyRing.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01((1f - Mathf.Abs(radius - 0.88f) * 9f) * 1.5f)));
                    }
                lobbyRing.Apply();
            }
            var tint = GUI.color;
            for (int i = 0; i < lobbyCount; i++)
            {
                var r = lobbyPlayers[i];
                GUI.color = uiColors[r.id];
                GUI.DrawTexture(lobbyFeet[i], lobbyRing);
                GUI.color = tint;
                string name = r.Name + (i == 0 ? " · Tu" : "");
                var rect = lobbyNames[i];
                nameTag.fontSize = Mathf.Clamp((int)(rect.width / Mathf.Max(1, name.Length) * 1.45f), 10, i == 0 ? 22 : 17);
                nameTag.normal.textColor = Color.black;
                GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), name, nameTag);
                nameTag.normal.textColor = Color.white;
                GUI.Label(rect, name, nameTag);
            }
        }
    }
}
