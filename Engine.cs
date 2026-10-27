using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KeyMouse;

/// <summary>
/// キーボードフックでキー入力を横取りし、マウス操作に変換する。
/// フックはUIスレッドで動き、カーソル移動とスクロールの連続処理は専用スレッドで行う。
/// </summary>
public sealed class Engine : IDisposable
{
    const int DirUp = 1, DirDown = 2, DirLeft = 4, DirRight = 8;
    const double ScrollRepeatDelay = 0.3; // 押しっぱなしでスクロールが連続し始めるまでの秒数
    const long RepeatGapMs = 1000;        // これより間が空いた「押下」はキーリピートではなく新しい押下とみなす

    sealed class Held { public MouseAction? Action; public long Last; }

    readonly object sync = new();
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly AutoResetEvent wake = new(false);
    readonly Native.LowLevelKeyboardProc proc;
    IntPtr hook;
    Thread? loop;
    volatile bool running;

    volatile AppSettings settings = null!;
    Dictionary<int, MouseAction> lookup = new();

    // UIスレッド専用の状態
    bool enabled = true;
    bool active, activationDown, usedSinceActivation, activeBeforeHold;
    bool leftButtonDown, dragLocked;
    readonly Dictionary<int, Held> held = new();
    readonly HashSet<int> swallowUp = new();
    Action<KeyDef>? capture;

    // 移動スレッドと共有する状態（sync でロック）
    int dirs;
    double moveStart;
    bool slow, fast;
    int scrollBits;
    readonly double[] nextScroll = new double[4];

    public event Action<bool>? ActiveChanged;

    public bool IsActive => active;

    public bool Enabled
    {
        get => enabled;
        set { if (enabled == value) return; enabled = value; if (!value) ResetAll(); }
    }

    public Engine(AppSettings s)
    {
        proc = HookProc;
        ApplySettings(s);
    }

    double Now => clock.Elapsed.TotalSeconds;

    public void Start()
    {
        hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, proc, Native.GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        running = true;
        loop = new Thread(Loop) { IsBackground = true, Name = "KeyMouse mover", Priority = ThreadPriority.AboveNormal };
        loop.Start();
    }

    public void ApplySettings(AppSettings s)
    {
        ResetAll();
        var map = new Dictionary<int, MouseAction>();
        foreach (var (a, k) in s.Bindings)
            if (k != null) map[k.Id] = a;
        lookup = map;
        settings = s;
    }

    /// <summary>次に押されたキーを1つだけ横取りして通知する（設定画面のキー登録用）。</summary>
    public void BeginCapture(Action<KeyDef> callback) => capture = callback;
    public void CancelCapture() => capture = null;

    /// <summary>押されているボタン・移動などをすべて解除する。</summary>
    public void ResetAll()
    {
        activationDown = false;
        usedSinceActivation = false;
        SetActive(false);
        if (dragLocked || leftButtonDown)
        {
            dragLocked = false;
            leftButtonDown = false;
            Button(MouseAction.LeftClick, false);
        }
        lock (sync) { dirs = 0; scrollBits = 0; slow = fast = false; }
    }

    /// <summary>画面ロックなどでキーを離した通知が届かなかった場合用。</summary>
    public void ForceReset()
    {
        ResetAll();
        held.Clear();
        swallowUp.Clear();
    }

    void SetActive(bool value)
    {
        if (active == value) return;
        active = value;
        if (!value)
        {
            foreach (var h in held.Values)
            {
                if (h.Action is { } a) ReleaseAction(a);
                h.Action = null; // キーを離した通知は引き続き横取りする
            }
        }
        ActiveChanged?.Invoke(value);
    }

    IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if (k.dwExtraInfo != Native.OurMarker)
            {
                int msg = (int)wParam;
                bool down = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
                bool up = msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP;
                if (down || up)
                {
                    bool suppress;
                    try { suppress = Process(KeyDef.FromHook(k), down); }
                    catch { suppress = false; }
                    if (suppress) return 1;
                }
            }
        }
        return Native.CallNextHookEx(hook, nCode, wParam, lParam);
    }

    /// <returns>true ならそのキー入力を他のアプリに渡さない</returns>
    bool Process(KeyDef k, bool down)
    {
        int id = k.Id;

        if (capture != null && down)
        {
            var cb = capture;
            capture = null;
            swallowUp.Add(id);
            cb(k);
            return true;
        }
        if (!down && swallowUp.Remove(id)) return true;
        if (!enabled) return false;

        var s = settings;

        // 起動キー
        if (s.ActivationKey != null && id == s.ActivationKey.Id)
        {
            if (down)
            {
                if (activationDown) return true; // キーリピート
                activationDown = true;
                usedSinceActivation = false;
                activeBeforeHold = active;
                SetActive(true);
                return true;
            }
            if (!activationDown) return true;
            activationDown = false;
            if (s.Mode == ActivationMode.Hold)
            {
                SetActive(false);
                if (!usedSinceActivation && s.TapPassThrough)
                    Native.SendKeyTap(k.Vk, k.Scan, k.Extended);
            }
            else
            {
                // 単独で押したら ON/OFF 切り替え。押しながら操作した場合は元の状態に戻す
                SetActive(usedSinceActivation ? activeBeforeHold : !activeBeforeHold);
            }
            return true;
        }

        if (down && activationDown) usedSinceActivation = true;

        if (!down)
        {
            if (held.Remove(id, out var h))
            {
                if (h.Action is { } a) ReleaseAction(a);
                return true;
            }
            return false;
        }

        long now = Environment.TickCount64;
        if (held.TryGetValue(id, out var cur))
        {
            bool isRepeat = now - cur.Last < RepeatGapMs;
            cur.Last = now;
            if (isRepeat) return true;
            // 離した通知を取りこぼしていた
            held.Remove(id);
            if (cur.Action is { } old) ReleaseAction(old);
        }

        if (!active) return false;
        if (!lookup.TryGetValue(id, out var action)) return false;

        held[id] = new Held { Action = PressAction(action) ? action : null, Last = now };
        return true;
    }

    static int DirBit(MouseAction a) => a switch
    {
        MouseAction.MoveUp => DirUp,
        MouseAction.MoveDown => DirDown,
        MouseAction.MoveLeft => DirLeft,
        MouseAction.MoveRight => DirRight,
        _ => 0,
    };

    static int ScrollIndex(MouseAction a) => a - MouseAction.ScrollUp;

    /// <returns>キーを離したときに ReleaseAction が必要なら true</returns>
    bool PressAction(MouseAction a)
    {
        switch (a)
        {
            case MouseAction.MoveUp or MouseAction.MoveDown or MouseAction.MoveLeft or MouseAction.MoveRight:
                lock (sync)
                {
                    if (dirs == 0) moveStart = Now;
                    dirs |= DirBit(a);
                }
                wake.Set();
                return true;

            case MouseAction.LeftClick:
                if (dragLocked)
                {
                    // ドラッグ固定中に左クリック → 離す
                    dragLocked = false;
                    leftButtonDown = false;
                    Button(a, false);
                    return false;
                }
                if (!leftButtonDown) { leftButtonDown = true; Button(a, true); }
                return true;

            case MouseAction.RightClick or MouseAction.MiddleClick:
                Button(a, true);
                return true;

            case MouseAction.DragLock:
                if (dragLocked)
                {
                    dragLocked = false;
                    if (leftButtonDown) { leftButtonDown = false; Button(MouseAction.LeftClick, false); }
                }
                else
                {
                    if (!leftButtonDown) { leftButtonDown = true; Button(MouseAction.LeftClick, true); }
                    dragLocked = true;
                }
                return false;

            case MouseAction.ScrollUp or MouseAction.ScrollDown or MouseAction.ScrollLeft or MouseAction.ScrollRight:
                int i = ScrollIndex(a);
                SendWheel(i, settings.ScrollStep);
                lock (sync)
                {
                    scrollBits |= 1 << i;
                    nextScroll[i] = Now + ScrollRepeatDelay;
                }
                wake.Set();
                return true;

            case MouseAction.Slow:
                lock (sync) slow = true;
                return true;

            case MouseAction.Fast:
                lock (sync) fast = true;
                return true;
        }
        return false;
    }

    void ReleaseAction(MouseAction a)
    {
        switch (a)
        {
            case MouseAction.MoveUp or MouseAction.MoveDown or MouseAction.MoveLeft or MouseAction.MoveRight:
                lock (sync) dirs &= ~DirBit(a);
                break;
            case MouseAction.LeftClick:
                if (!dragLocked && leftButtonDown) { leftButtonDown = false; Button(a, false); }
                break;
            case MouseAction.RightClick or MouseAction.MiddleClick:
                Button(a, false);
                break;
            case MouseAction.ScrollUp or MouseAction.ScrollDown or MouseAction.ScrollLeft or MouseAction.ScrollRight:
                lock (sync) scrollBits &= ~(1 << ScrollIndex(a));
                break;
            case MouseAction.Slow:
                lock (sync) slow = false;
                break;
            case MouseAction.Fast:
                lock (sync) fast = false;
                break;
        }
    }

    static void Button(MouseAction a, bool down)
    {
        uint flags = a switch
        {
            MouseAction.LeftClick => down ? Native.MOUSEEVENTF_LEFTDOWN : Native.MOUSEEVENTF_LEFTUP,
            MouseAction.RightClick => down ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_RIGHTUP,
            MouseAction.MiddleClick => down ? Native.MOUSEEVENTF_MIDDLEDOWN : Native.MOUSEEVENTF_MIDDLEUP,
            _ => 0,
        };
        if (flags != 0) Native.SendMouse(0, 0, 0, flags);
    }

    static void SendWheel(int index, int step)
    {
        // 0:上 1:下 2:左 3:右
        int amount = index is 0 or 3 ? step : -step;
        uint flags = index < 2 ? Native.MOUSEEVENTF_WHEEL : Native.MOUSEEVENTF_HWHEEL;
        Native.SendMouse(0, 0, unchecked((uint)amount), flags);
    }

    static void MoveTo(int x, int y, int vx, int vy, int vw, int vh)
    {
        // 仮想デスクトップ全体を 0〜65535 に正規化した絶対座標で送る
        int nx = (int)(((long)(x - vx) * 65536 + vw - 1) / vw);
        int ny = (int)(((long)(y - vy) * 65536 + vh - 1) / vh);
        Native.SendMouse(nx, ny, 0,
            Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK);
    }

    void Loop()
    {
        bool hiRes = false;
        bool posValid = false;
        double fx = 0, fy = 0, last = Now;

        while (running)
        {
            int d, sb;
            bool sl, fa;
            double start;
            lock (sync) { d = dirs; sb = scrollBits; sl = slow; fa = fast; start = moveStart; }

            if (d == 0 && sb == 0)
            {
                if (hiRes) { Native.timeEndPeriod(1); hiRes = false; }
                posValid = false;
                wake.WaitOne();
                last = Now;
                continue;
            }
            if (!hiRes) { Native.timeBeginPeriod(1); hiRes = true; }

            Thread.Sleep(4);
            double now = Now;
            double dt = Math.Min(now - last, 0.05);
            last = now;
            var s = settings;

            int x = ((d & DirRight) != 0 ? 1 : 0) - ((d & DirLeft) != 0 ? 1 : 0);
            int y = ((d & DirDown) != 0 ? 1 : 0) - ((d & DirUp) != 0 ? 1 : 0);
            if (x != 0 || y != 0)
            {
                double t = s.AccelMs <= 0 ? 1 : Math.Min(1, (now - start) * 1000 / s.AccelMs);
                double speed = s.MinSpeed + (s.MaxSpeed - s.MinSpeed) * t;
                if (sl) speed *= s.SlowPercent / 100.0;
                if (fa) speed *= s.FastPercent / 100.0;
                double len = Math.Sqrt(x * x + y * y);

                Native.GetCursorPos(out var p);
                // 実際のマウスで動かされた場合などは現在位置に合わせ直す
                if (!posValid || Math.Abs(p.X - Math.Round(fx)) > 2 || Math.Abs(p.Y - Math.Round(fy)) > 2)
                {
                    fx = p.X; fy = p.Y; posValid = true;
                }

                int vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
                int vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
                int vw = Math.Max(1, Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN));
                int vh = Math.Max(1, Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN));

                fx = Math.Clamp(fx + x / len * speed * dt, vx, vx + vw - 1);
                fy = Math.Clamp(fy + y / len * speed * dt, vy, vy + vh - 1);
                int ix = (int)Math.Round(fx), iy = (int)Math.Round(fy);
                if (ix != p.X || iy != p.Y) MoveTo(ix, iy, vx, vy, vw, vh);
            }
            else
            {
                posValid = false;
            }

            if (sb != 0)
            {
                double interval = Math.Max(10, s.ScrollIntervalMs) / 1000.0;
                for (int i = 0; i < 4; i++)
                {
                    if ((sb & (1 << i)) == 0) continue;
                    bool fire;
                    lock (sync)
                    {
                        fire = now >= nextScroll[i];
                        if (fire) nextScroll[i] = now + interval;
                    }
                    if (fire) SendWheel(i, s.ScrollStep);
                }
            }
        }
        if (hiRes) Native.timeEndPeriod(1);
    }

    public void Dispose()
    {
        ResetAll();
        running = false;
        wake.Set();
        if (hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
    }
}
