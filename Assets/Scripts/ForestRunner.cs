using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
#endif

// Forest Platform Runner - fully procedural: no external assets, prefabs or tags needed.
// Auto-boots in ANY scene when you press Play.
public class ForestRunner : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (FindFirstObjectByType<ForestRunner>() == null)
            new GameObject("ForestRunner").AddComponent<ForestRunner>();
    }

    enum S { Menu, Play, Over }
    class Item { public GameObject go; public int kind; public float x, y, w, h, baseY; public bool dead; }
    // kinds: 0 coin, 1 gem, 2 crate, 3 spike, 4 boulder, 5 bat, 6 magnet, 7 shield, 8 double coin, 9 speed boost

    S state = S.Menu; bool paused;
    Sprite sq, ci, tri; Camera cam; Transform world, pl, legs; GameObject shieldGo;
    readonly List<Item> items = new List<Item>();
    readonly List<Vector3> segs = new List<Vector3>();
    readonly List<Vector3> plats = new List<Vector3>();
    float curTop, camY = 1.5f, lastGround;
    readonly List<Transform> hills = new List<Transform>();
    float px, py, vy, speed, dist, genX, invul, tMagnet, tX2, tSpeed, anim;
    int jumps, hearts, coins, gems, best, bonus; bool shield, grounded;
    GUIStyle big, mid, btn;

    int Score { get { return (int)dist + bonus; } }

    Sprite MakeSprite(int n, System.Func<float, float, bool> f)
    {
        var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
        var c = new Color[n * n];
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
            c[y * n + x] = f((x + .5f) / n, (y + .5f) / n) ? Color.white : Color.clear;
        t.SetPixels(c); t.Apply();
        return Sprite.Create(t, new Rect(0, 0, n, n), new Vector2(.5f, .5f), n);
    }

    GameObject Spr(Sprite s, Color c, float x, float y, float w, float h, int order, Transform parent)
    {
        var g = new GameObject("s");
        g.transform.SetParent(parent, false);
        g.transform.localPosition = new Vector3(x, y, 0);
        g.transform.localScale = new Vector3(w, h, 1);
        var r = g.AddComponent<SpriteRenderer>();
        r.sprite = s; r.color = c; r.sortingOrder = order;
        return g;
    }

    void Awake()
    {
        sq = MakeSprite(8, (u, v) => true);
        ci = MakeSprite(64, (u, v) => (u - .5f) * (u - .5f) + (v - .5f) * (v - .5f) < .25f);
        tri = MakeSprite(64, (u, v) => Mathf.Abs(u - .5f) < (1f - v) * .5f);
        cam = Camera.main;
        if (cam == null) { var g = new GameObject("Main Camera"); g.tag = "MainCamera"; cam = g.AddComponent<Camera>(); }
        cam.orthographic = true; cam.orthographicSize = 5;
        cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(.45f, .75f, .95f);
        best = PlayerPrefs.GetInt("fr_best", 0); gems = PlayerPrefs.GetInt("fr_gems", 6);
        Application.targetFrameRate = 60;
        Build();
    }

    void Build()
    {
        if (world != null) Destroy(world.gameObject);
        world = new GameObject("World").transform;
        items.Clear(); segs.Clear(); plats.Clear(); hills.Clear();
        for (int i = 0; i < 8; i++)
            hills.Add(Spr(ci, Color.Lerp(new Color(.3f, .6f, .5f), new Color(.2f, .45f, .4f), i % 3 / 2f), 0, -1.5f, 9, 6 + i % 3 * 2, -20, world).transform);
        pl = new GameObject("Player").transform; pl.SetParent(world);
        legs = Spr(sq, new Color(.2f, .2f, .35f), 0, .2f, .5f, .4f, 5, pl).transform;
        Spr(sq, new Color(.85f, .25f, .15f), 0, .65f, .6f, .6f, 6, pl);
        Spr(ci, new Color(1f, .8f, .65f), .05f, 1.05f, .45f, .45f, 7, pl);
        Spr(ci, new Color(.4f, .2f, .1f), -.05f, 1.2f, .5f, .35f, 8, pl);
        shieldGo = Spr(ci, new Color(.3f, .6f, 1f, .35f), 0, .65f, 1.7f, 1.7f, 9, pl);
        px = 0; py = 0; vy = 0; speed = 6; dist = 0; bonus = 0; hearts = 3; coins = 0;
        shield = false; tMagnet = tX2 = tSpeed = invul = 0; jumps = 0; paused = false;
        curTop = 0; lastGround = 0; camY = 1.5f; AddSeg(-8, 16, 0); genX = 16; Gen();
    }

    void AddSeg(float x0, float x1, float top)
    {
        float w = x1 - x0, m = (x0 + x1) / 2;
        Spr(sq, new Color(.45f, .3f, .15f), m, top - 6f, w, 12, 1, world);
        Spr(sq, new Color(.3f, .7f, .2f), m, top - .15f, w, .3f, 2, world);
        Spr(sq, new Color(.5f, .85f, .3f), m, top - .03f, w, .08f, 3, world);
        segs.Add(new Vector3(x0, x1, top));
        for (float t = x0 + 1; t < x1 - 1; t += Random.Range(3f, 6f))
        {
            float h = Random.Range(2f, 3.2f);
            Spr(sq, new Color(.35f, .22f, .1f), t, top + h / 2, .4f, h, -6, world);
            Spr(ci, Color.Lerp(new Color(.15f, .5f, .15f), new Color(.25f, .65f, .2f), Random.value), t, top + h + .6f, 2.3f, 2.3f, -5, world);
        }
    }

    // floating one-way grass platform (land on it from above, jump up through it)
    void AddPlat(float x0, float x1, float y)
    {
        float w = x1 - x0, m = (x0 + x1) / 2;
        Spr(sq, new Color(.4f, .27f, .13f), m, y - .5f, w, .8f, 1, world);
        Spr(sq, new Color(.3f, .7f, .2f), m, y - .12f, w, .26f, 2, world);
        Spr(sq, new Color(.5f, .85f, .3f), m, y - .02f, w, .06f, 3, world);
        plats.Add(new Vector3(x0, x1, y));
    }

    Item Add(int kind, float x, float y, float w, float h)
    {
        var it = new Item { kind = kind, x = x, y = y, w = w, h = h, baseY = y };
        var g = new GameObject("item"); g.transform.SetParent(world, false); g.transform.position = new Vector3(x, y, 0);
        it.go = g;
        switch (kind)
        {
            case 0: Spr(ci, new Color(1f, .85f, .1f), 0, 0, .5f, .5f, 10, g.transform); Spr(ci, new Color(1f, .95f, .5f), 0, 0, .3f, .3f, 11, g.transform); break;
            case 1: { var d = Spr(sq, new Color(.2f, .85f, 1f), 0, 0, .45f, .45f, 10, g.transform); d.transform.rotation = Quaternion.Euler(0, 0, 45); break; }
            case 2: Spr(sq, new Color(.35f, .2f, .08f), 0, 0, .9f, .9f, 10, g.transform); Spr(sq, new Color(.75f, .5f, .25f), 0, 0, .72f, .72f, 11, g.transform); break;
            case 3: for (int i = -1; i <= 1; i++) Spr(tri, new Color(.75f, .78f, .82f), i * .38f, 0, .5f, .6f, 10, g.transform); break;
            case 4: Spr(ci, new Color(.4f, .4f, .42f), 0, 0, 1.2f, 1.2f, 10, g.transform); Spr(ci, new Color(.55f, .55f, .58f), -.2f, .2f, .4f, .4f, 11, g.transform); break;
            case 5: Spr(ci, new Color(.35f, .15f, .35f), 0, 0, .7f, .5f, 10, g.transform); Spr(tri, new Color(.5f, .2f, .45f), -.45f, .1f, .5f, .4f, 9, g.transform); Spr(tri, new Color(.5f, .2f, .45f), .45f, .1f, .5f, .4f, 9, g.transform); break;
            default:
                Color[] cs = { new Color(.9f, .2f, .2f), new Color(.25f, .5f, 1f), new Color(1f, .7f, .1f), new Color(.2f, .85f, .4f) };
                Spr(ci, cs[kind - 6], 0, 0, .8f, .8f, 10, g.transform); Spr(ci, Color.white, 0, 0, .35f, .35f, 11, g.transform); break;
        }
        items.Add(it); return it;
    }

    void Gen()
    {
        while (genX < px + 32)
        {
            float pt = curTop;
            float top = Mathf.Clamp(pt + Random.Range(-1.8f, 1.5f), -1.5f, 2.5f); // every island has a different height
            float gap = Mathf.Max(2.2f, Random.Range(2.5f, 3.2f + (speed - 6f) * .3f) - Mathf.Max(0f, top - pt) * .3f);
            float x0 = genX + gap, x1 = x0 + Random.Range(8f, 14f), m = (x0 + x1) / 2, t = top;
            if (Random.value < .5f) // stepping-stone platform inside the gap
            {
                float sy = (pt + top) / 2f + .2f, gm = genX + gap / 2f;
                AddPlat(gm - .9f, gm + .9f, sy); Add(0, gm, sy + .9f, .5f, .5f);
            }
            curTop = top; AddSeg(x0, x1, top); genX = x1;
            switch (Random.Range(0, 5))
            {
                case 0: for (int i = 0; i < 6; i++) Add(0, x0 + 2.5f + i, t + 1.2f, .5f, .5f); break;
                case 1: Add(2, m, t + .45f, .9f, .9f); for (int i = -2; i <= 2; i++) Add(0, m + i * .8f, t + 2.2f - Mathf.Abs(i) * .25f, .5f, .5f); break;
                case 2: Add(3, m, t + .3f, 1.1f, .6f); for (int i = -2; i <= 2; i++) Add(0, m + i * .8f, t + 1.8f, .5f, .5f); break;
                case 3: Add(4, m, t + .6f, 1.1f, 1.1f); for (int i = -1; i <= 1; i++) Add(0, m + i, t + 2.4f, .5f, .5f); break;
                default: Add(5, m, t + 1.5f, .8f, .5f); for (int i = 0; i < 4; i++) Add(0, x0 + 2 + i, t + .9f, .5f, .5f); break;
            }
            if (Random.value < .65f) // floating platform above the ground, at a random height
            {
                float pw = Random.Range(3f, 4.5f), p0 = x0 + Random.Range(1.5f, 3f), py2 = t + Random.Range(2.3f, 3.1f);
                AddPlat(p0, p0 + pw, py2);
                for (int i = 0; i < (int)pw; i++) Add(0, p0 + .6f + i, py2 + .8f, .5f, .5f);
            }
            if (Random.value < .25f) Add(1, x0 + 2.2f, t + 2.8f, .45f, .45f);
            if (Random.value < .18f) Add(Random.Range(6, 10), x1 - 2.2f, t + 1.3f, .8f, .8f);
        }
    }

    bool JumpPressed()
    {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        var k = Keyboard.current; var m = Mouse.current; var t = Touchscreen.current;
        return (k != null && (k.spaceKey.wasPressedThisFrame || k.upArrowKey.wasPressedThisFrame || k.wKey.wasPressedThisFrame))
            || (m != null && m.leftButton.wasPressedThisFrame) || (t != null && t.primaryTouch.press.wasPressedThisFrame);
#else
        return Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W) || Input.GetMouseButtonDown(0);
#endif
    }

    bool PausePressed()
    {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        var k = Keyboard.current; return k != null && (k.pKey.wasPressedThisFrame || k.escapeKey.wasPressedThisFrame);
#else
        return Input.GetKeyDown(KeyCode.P) || Input.GetKeyDown(KeyCode.Escape);
#endif
    }

    void Update()
    {
        if (state != S.Play) return;
        if (PausePressed()) paused = !paused;
        if (paused) return;
        float dt = Mathf.Min(Time.deltaTime, .033f);
        speed = Mathf.Min(6f + dist / 60f, 11f);
        float sp = speed + (tSpeed > 0 ? 3f : 0f);
        float prevY = py;
        px += sp * dt; dist += sp * dt; anim += dt * sp * 2f;
        if (JumpPressed() && jumps < 2) { vy = jumps == 0 ? 12.5f : 11f; jumps++; grounded = false; }
        vy -= 32f * dt; py += vy * dt; grounded = false;
        if (vy <= 0)
        {
            foreach (var s in segs) Land(s, prevY, true);
            foreach (var s in plats) Land(s, prevY, false);
        }
        foreach (var s in segs) if (px > s.x - .1f && px < s.x + .5f && py < s.z - .3f) px = s.x - .1f; // can't run through a taller cliff
        if (py < -6f) { Hurt(true); }
        tMagnet -= dt; tX2 -= dt; tSpeed -= dt; invul -= dt;

        foreach (var it in items)
        {
            if (it.dead) continue;
            if (it.kind <= 1 && tMagnet > 0 && Mathf.Abs(it.x - px) < 5f)
            {
                Vector2 d = new Vector2(px - it.x, py + .6f - it.y);
                Vector2 mv = d.normalized * 14f * dt; it.x += mv.x; it.y += mv.y;
            }
            if (it.kind == 5) it.y = it.baseY + Mathf.Sin(Time.time * 3f + it.baseY + it.x) * .4f;
            it.go.transform.position = new Vector3(it.x, it.y, 0);
            if (Mathf.Abs(it.x - px) < (it.w + .6f) / 2 && Mathf.Abs(it.y - (py + .6f)) < (it.h + 1.1f) / 2)
            {
                if (it.kind >= 2 && it.kind <= 5) { Hurt(false); continue; }
                it.dead = true; Destroy(it.go);
                switch (it.kind)
                {
                    case 0: coins++; bonus += tX2 > 0 ? 20 : 10; break;
                    case 1: gems++; bonus += 50; PlayerPrefs.SetInt("fr_gems", gems); break;
                    case 6: tMagnet = 8; break;
                    case 7: shield = true; break;
                    case 8: tX2 = 8; break;
                    case 9: tSpeed = 5; invul = Mathf.Max(invul, 5f); break;
                }
            }
            else if (it.x < px - 15f) { it.dead = true; Destroy(it.go); }
        }
        items.RemoveAll(i => i.dead);
        Gen();
    }

    void Land(Vector3 s, float prevY, bool ground)
    {
        if (px > s.x - .15f && px < s.y + .15f && prevY >= s.z - .05f && py <= s.z)
        { py = s.z; vy = 0; jumps = 0; grounded = true; if (ground) lastGround = s.z; }
    }

    void LateUpdate()
    {
        float vw = cam.orthographicSize * 2f * cam.aspect, cx = px + vw * .25f;
        camY = Mathf.Lerp(camY, 1.5f + lastGround * .8f, 1f - Mathf.Exp(-4f * Time.deltaTime));
        cam.transform.position = new Vector3(cx, camY, -10);
        float span = hills.Count * 7f;
        for (int i = 0; i < hills.Count; i++)
            hills[i].position = new Vector3(cx + Mathf.Repeat(i * 7f - cx * .5f, span) - span / 2f, camY - 3f, 0);
        pl.position = new Vector3(px, py, 0);
        legs.localPosition = new Vector3(grounded ? Mathf.Sin(anim) * .14f : 0f, .2f, 0);
        shieldGo.SetActive(shield);
        pl.gameObject.SetActive(!(invul > 0 && (int)(Time.time * 12f) % 2 == 0));
    }

    void Hurt(bool fell)
    {
        if (!fell && invul > 0) return;
        if (!fell && shield) { shield = false; invul = 1f; return; }
        hearts--; invul = 1.5f;
        if (hearts <= 0) { End(); return; }
        if (fell) Respawn();
    }

    void Respawn()
    {
        py = lastGround;
        foreach (var s in segs) if (s.y > px + 3f) { px = Mathf.Max(px, s.x + 1f); py = s.z; break; }
        vy = 0; jumps = 0;
        foreach (var it in items) if (it.kind >= 2 && it.kind <= 5 && Mathf.Abs(it.x - px) < 3.5f) { it.dead = true; Destroy(it.go); }
        items.RemoveAll(i => i.dead);
    }

    void End()
    {
        state = S.Over;
        if (Score > best) best = Score;
        PlayerPrefs.SetInt("fr_best", best); PlayerPrefs.SetInt("fr_gems", gems); PlayerPrefs.Save();
    }

    void Panel(Rect r, float a) { GUI.color = new Color(0, 0, 0, a); GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = Color.white; }

    void OnGUI()
    {
        if (big == null)
        {
            big = new GUIStyle(GUI.skin.label) { fontSize = 64, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            mid = new GUIStyle(GUI.skin.label) { fontSize = 30, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            btn = new GUIStyle(GUI.skin.button) { fontSize = 32, fontStyle = FontStyle.Bold };
        }
        float s = Screen.height / 720f, W = Screen.width / s;
        GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1));
        float cx = W / 2f;

        if (state == S.Menu)
        {
            Panel(new Rect(cx - 330, 130, 660, 440), .5f);
            GUI.Label(new Rect(cx - 330, 160, 660, 90), "FOREST RUNNER", big);
            GUI.Label(new Rect(cx - 330, 260, 660, 120), "Space / Up / Click / Tap = Jump\nDouble jump in the air!\nCollect coins, gems and power-ups", mid);
            GUI.Label(new Rect(cx - 330, 390, 660, 40), "Best: " + best.ToString("D5") + "     Gems: " + gems, mid);
            if (GUI.Button(new Rect(cx - 130, 450, 260, 80), "PLAY", btn)) { Build(); state = S.Play; }
            return;
        }

        Panel(new Rect(16, 16, 320, 126), .45f);
        GUI.Label(new Rect(24, 18, 300, 34), "Coins  " + coins, mid);
        GUI.Label(new Rect(24, 50, 300, 34), "Score  " + Score.ToString("D5"), mid);
        GUI.Label(new Rect(24, 82, 300, 34), "Gems  " + gems, mid);
        for (int i = 0; i < 3; i++)
        { GUI.color = i < hearts ? new Color(.95f, .15f, .2f) : new Color(.3f, .3f, .3f); GUI.DrawTexture(new Rect(24 + i * 34, 116, 28, 22), Texture2D.whiteTexture); }
        GUI.color = Color.white;
        string pu = (tMagnet > 0 ? "MAGNET " : "") + (shield ? "SHIELD " : "") + (tX2 > 0 ? "2X " : "") + (tSpeed > 0 ? "SPEED " : "");
        if (pu != "") GUI.Label(new Rect(cx - 250, 20, 500, 40), pu, mid);

        if (state == S.Play)
        {
            if (GUI.Button(new Rect(W - 90, 16, 70, 70), paused ? ">" : "II", btn)) paused = !paused;
            if (paused)
            {
                Panel(new Rect(cx - 250, 200, 500, 300), .6f);
                GUI.Label(new Rect(cx - 250, 210, 500, 80), "PAUSED", big);
                if (GUI.Button(new Rect(cx - 130, 320, 260, 70), "RESUME", btn)) paused = false;
                if (GUI.Button(new Rect(cx - 130, 400, 260, 70), "HOME", btn)) { Build(); state = S.Menu; }
            }
        }
        else
        {
            Panel(new Rect(cx - 280, 90, 560, 540), .65f);
            GUI.Label(new Rect(cx - 280, 100, 560, 80), "GAME OVER", big);
            GUI.Label(new Rect(cx - 280, 190, 560, 40), "Score  " + Score.ToString("D5"), mid);
            GUI.Label(new Rect(cx - 280, 230, 560, 40), "Best Score  " + best.ToString("D5"), mid);
            GUI.enabled = gems >= 2;
            if (GUI.Button(new Rect(cx - 150, 300, 300, 70), "REVIVE (2 gems)", btn))
            { gems -= 2; hearts = 3; invul = 2f; PlayerPrefs.SetInt("fr_gems", gems); Respawn(); state = S.Play; }
            GUI.enabled = true;
            if (GUI.Button(new Rect(cx - 150, 385, 300, 70), "HOME", btn)) { Build(); state = S.Menu; }
            if (GUI.Button(new Rect(cx - 150, 470, 300, 70), "RETRY", btn)) { Build(); state = S.Play; }
        }
    }
}
