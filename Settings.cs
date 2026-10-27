using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeyMouse;

public enum ActivationMode { Hold, Toggle }

public enum MouseAction
{
    MoveUp, MoveDown, MoveLeft, MoveRight,
    LeftClick, RightClick, MiddleClick, DragLock,
    ScrollUp, ScrollDown, ScrollLeft, ScrollRight,
    Slow, Fast,
}

public static class ActionLabels
{
    public static string Of(MouseAction a) => a switch
    {
        MouseAction.MoveUp => "上へ移動",
        MouseAction.MoveDown => "下へ移動",
        MouseAction.MoveLeft => "左へ移動",
        MouseAction.MoveRight => "右へ移動",
        MouseAction.LeftClick => "左クリック（押したまま移動でドラッグ）",
        MouseAction.RightClick => "右クリック",
        MouseAction.MiddleClick => "中クリック",
        MouseAction.DragLock => "ドラッグ固定（押すたびに つかむ／離す）",
        MouseAction.ScrollUp => "上スクロール",
        MouseAction.ScrollDown => "下スクロール",
        MouseAction.ScrollLeft => "左スクロール",
        MouseAction.ScrollRight => "右スクロール",
        MouseAction.Slow => "押している間ゆっくり移動",
        MouseAction.Fast => "押している間すばやく移動",
        _ => a.ToString(),
    };
}

/// <summary>キーは物理的な位置（スキャンコード）で識別する。配列（大西配列など）に左右されない。</summary>
public sealed class KeyDef
{
    public int Vk { get; set; }
    public int Scan { get; set; }
    public bool Extended { get; set; }

    [JsonIgnore]
    public int Id => Scan != 0 ? Scan | (Extended ? 0x100 : 0) : 0x10000 | Vk;

    [JsonIgnore]
    public string DisplayName => GetName();

    public static KeyDef Of(int vk, int scan, bool extended = false) => new() { Vk = vk, Scan = scan, Extended = extended };

    internal static KeyDef FromHook(in Native.KBDLLHOOKSTRUCT k) =>
        Of((int)k.vkCode, (int)k.scanCode, (k.flags & Native.LLKHF_EXTENDED) != 0);

    string GetName()
    {
        switch (Vk)
        {
            case 0x1D: return "無変換";
            case 0x1C: return "変換";
            case 0xF2: return "カタカナひらがな";
            case 0xF3: case 0xF4: return "半角/全角";
            case 0xF0: return "英数(CapsLock)";
        }
        if (Scan != 0)
        {
            var sb = new StringBuilder(64);
            int lParam = (Scan << 16) | (Extended ? 1 << 24 : 0);
            if (Native.GetKeyNameText(lParam, sb, sb.Capacity) > 0) return sb.ToString();
        }
        return ((Keys)Vk).ToString();
    }
}

public sealed class AppSettings
{
    public KeyDef? ActivationKey { get; set; }
    public ActivationMode Mode { get; set; } = ActivationMode.Hold;
    public bool TapPassThrough { get; set; } = true;
    public Dictionary<MouseAction, KeyDef?> Bindings { get; set; } = new();

    public int MinSpeed { get; set; } = 300;       // px/秒
    public int MaxSpeed { get; set; } = 2000;      // px/秒
    public int AccelMs { get; set; } = 350;        // 最高速に達するまでの時間
    public int SlowPercent { get; set; } = 25;
    public int FastPercent { get; set; } = 250;
    public int ScrollStep { get; set; } = 120;     // 120 = ホイール1目盛り
    public int ScrollIntervalMs { get; set; } = 80;
    public bool ShowOverlay { get; set; } = true;

    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeyMouse");
    public static string FilePath => Path.Combine(Dir, "settings.json");

    static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings CreateDefault()
    {
        var s = new AppSettings { ActivationKey = KeyDef.Of(0x1D, 0x7B) }; // 無変換
        var b = s.Bindings;
        foreach (var a in Enum.GetValues<MouseAction>()) b[a] = null;
        // 位置はQWERTYキーボードの刻印で表記（物理位置）
        b[MouseAction.MoveUp] = KeyDef.Of(0x57, 0x11);      // W
        b[MouseAction.MoveLeft] = KeyDef.Of(0x41, 0x1E);    // A
        b[MouseAction.MoveDown] = KeyDef.Of(0x53, 0x1F);    // S
        b[MouseAction.MoveRight] = KeyDef.Of(0x44, 0x20);   // D
        b[MouseAction.LeftClick] = KeyDef.Of(0x4A, 0x24);   // J
        b[MouseAction.RightClick] = KeyDef.Of(0x4B, 0x25);  // K
        b[MouseAction.MiddleClick] = KeyDef.Of(0x4C, 0x26); // L
        b[MouseAction.DragLock] = KeyDef.Of(0x48, 0x23);    // H
        b[MouseAction.ScrollUp] = KeyDef.Of(0x52, 0x13);    // R
        b[MouseAction.ScrollDown] = KeyDef.Of(0x46, 0x21);  // F
        b[MouseAction.ScrollLeft] = KeyDef.Of(0x51, 0x10);  // Q
        b[MouseAction.ScrollRight] = KeyDef.Of(0x45, 0x12); // E
        b[MouseAction.Slow] = KeyDef.Of(0xA0, 0x2A);        // 左Shift
        b[MouseAction.Fast] = KeyDef.Of(0x20, 0x39);        // Space
        return s;
    }

    public static AppSettings Load(out bool existed)
    {
        existed = File.Exists(FilePath);
        if (existed)
        {
            try
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Opts);
                if (s != null) { s.Normalize(); return s; }
            }
            catch { /* 壊れていたら初期設定で起動 */ }
        }
        return CreateDefault();
    }

    void Normalize()
    {
        Bindings ??= new();
        foreach (var a in Enum.GetValues<MouseAction>()) Bindings.TryAdd(a, null);
        ActivationKey ??= CreateDefault().ActivationKey;
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts));
    }

    public AppSettings Clone()
    {
        var c = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, Opts), Opts)!;
        c.Normalize();
        return c;
    }
}
