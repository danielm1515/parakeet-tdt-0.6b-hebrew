// MilaWin — mila-style local dictation for Windows, powered by the user's
// Parakeet STT engine (stt_parakeet.exe / VoiceAgentSTT service on :7877).
//
//   Ctrl+Alt+Space  -> start recording (overlay pill shows 🎤)
//   Ctrl+Alt+Space  -> stop, transcribe locally, paste at the cursor
//
// Tray icon menu: paste mode (paste / clipboard-only), self test, exit.
// Everything stays on the machine — audio goes only to 127.0.0.1.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NAudio.Wave;

namespace MilaWin;

static class Log
{
    static readonly string PathFile = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MilaWin", "log.txt");
    static readonly object L = new();

    static Log() => Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathFile)!);

    public static void W(string msg)
    {
        try
        {
            lock (L) File.AppendAllText(PathFile,
                $"{DateTime.Now:HH:mm:ss.fff} {msg}\r\n");
        }
        catch { }
    }
}

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Length > 0 && args[0] == "--test-file")
        {
            SelfTest(args.Length > 1 ? args[1] : "").GetAwaiter().GetResult();
            return;
        }
        if (args.Length > 0 && args[0] == "--test-loopback")
        {
            LoopbackTest(args.Length > 1 ? int.Parse(args[1]) : 6).GetAwaiter().GetResult();
            return;
        }

        // single instance — a second launch would fail to grab the hotkey
        using var mutex = new Mutex(true, "MilaWin_SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("MilaWin כבר רץ (חפש את האייקון במגש המערכת ליד השעון).",
                            "MilaWin", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.Run(new TrayApp());
    }

    // pipeline check without a microphone: wav file -> service -> stdout
    static async Task SelfTest(string wavPath)
    {
        AttachConsole(-1);
        var pcm = AudioUtil.WavFileToPcm16Mono16k(wavPath);
        Console.WriteLine($"\n[selftest] {pcm.Length} pcm bytes -> {SttClient.Url}");
        string text = await SttClient.Transcribe(pcm);
        Console.WriteLine($"[selftest] text: {text}");
    }

    // capture system output (loopback) for N seconds -> service -> stdout
    static async Task LoopbackTest(int seconds)
    {
        AttachConsole(-1);
        var cap = new WasapiLoopbackCapture();
        var wf = cap.WaveFormat;
        Console.WriteLine($"\n[loopback] capturing {seconds}s of system audio ({wf.SampleRate}Hz {wf.Channels}ch {wf.Encoding})...");
        var ms = new MemoryStream();
        double pos = 0;
        cap.DataAvailable += (_, e) =>
        {
            var pcm = AudioUtil.ToPcm16Mono16k(e.Buffer, e.BytesRecorded, wf, ref pos);
            ms.Write(pcm, 0, pcm.Length);
        };
        cap.StartRecording();
        await Task.Delay(seconds * 1000);
        cap.StopRecording();
        cap.Dispose();
        Console.WriteLine($"[loopback] {ms.Length} pcm bytes captured");
        if (ms.Length < 16000) { Console.WriteLine("[loopback] (almost) nothing played — no audio captured"); return; }
        string text = await SttClient.Transcribe(ms.ToArray());
        Console.WriteLine($"[loopback] text: {text}");
    }

    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
}

// ─────────────────────────── tray application ───────────────────────────
class TrayApp : ApplicationContext
{
    const int HOTKEY_ID = 0xB00B;
    const uint MOD_CONTROL = 0x2, MOD_ALT = 0x1;
    const uint VK_SPACE = 0x20;

    readonly NotifyIcon _tray;
    readonly HotkeyWindow _hotkeyWnd;
    readonly RecordingOverlay _overlay = new();
    IWaveIn? _wave;            // WaveInEvent (mic) or WasapiLoopbackCapture (system)
    bool _systemAudio;         // capture what the PC plays instead of the mic
    double _resPos;            // loopback resampler fractional position
    MemoryStream? _buf;
    readonly object _bufLock = new();
    enum Mode
    {
        Live,   // stream: type while speaking + VAD settles each sentence
        Vad,    // sentence-by-sentence: silence -> transcribe utterance -> paste
        Batch,  // accuracy: record everything, ONE transcription at the end
    }

    bool _recording;
    bool _pasteMode = true;    // false = clipboard only
    Mode _mode = Mode.Live;
    int _micDevice;            // WaveInEvent device index (persisted)
    string _typed = "";        // what live mode has typed so far

    static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MilaWin", "config.json");

    void SaveConfig()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new
            { mic = _micDevice, mode = (int)_mode, paste = _pasteMode, sys = _systemAudio }));
        }
        catch { }
    }

    void LoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;
            var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            _micDevice = doc.RootElement.TryGetProperty("mic", out var m) ? m.GetInt32() : 0;
            _mode = doc.RootElement.TryGetProperty("mode", out var md) ? (Mode)md.GetInt32() : Mode.Live;
            _pasteMode = !doc.RootElement.TryGetProperty("paste", out var p) || p.GetBoolean();
            _systemAudio = doc.RootElement.TryGetProperty("sys", out var sy) && sy.GetBoolean();
            if (_micDevice >= WaveInEvent.DeviceCount) _micDevice = 0;
        }
        catch { }
    }
    System.Windows.Forms.Timer? _liveTimer;
    bool _partialBusy;

    // VAD: silence for SIL_MS after speech finalizes the utterance (recording
    // keeps running for the next one — continuous dictation, like the agent)
    const double VAD_THRESHOLD = 0.012;
    const int SIL_MS = 900;
    DateTime _lastLoud = DateTime.MinValue;
    bool _hadSpeech;
    readonly Stopwatch _sincePartial = new();

    public TrayApp()
    {
        _tray = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = "MilaWin — Ctrl+Alt+Space להקלטה",
        };
        LoadConfig();
        var menu = new ContextMenuStrip();

        // source: system audio (loopback) instead of the mic
        var sysItem = new ToolStripMenuItem("🔊 תמלל אודיו מערכת (מה שמתנגן)")
        { Checked = _systemAudio, CheckOnClick = true };
        sysItem.CheckedChanged += (_, _) => { _systemAudio = sysItem.Checked; SaveConfig(); };
        menu.Items.Add(sysItem);

        // mic picker — rebuilt every time it opens (devices come and go)
        var micMenu = new ToolStripMenuItem("🎤 מיקרופון");
        micMenu.DropDownOpening += (_, _) =>
        {
            micMenu.DropDownItems.Clear();
            for (int i = 0; i < WaveInEvent.DeviceCount; i++)
            {
                int dev = i;
                var item = new ToolStripMenuItem(WaveInEvent.GetCapabilities(i).ProductName)
                { Checked = i == _micDevice };
                item.Click += (_, _) =>
                {
                    _micDevice = dev;
                    SaveConfig();
                    Log.W($"mic switched to [{dev}] {WaveInEvent.GetCapabilities(dev).ProductName}");
                };
                micMenu.DropDownItems.Add(item);
            }
        };
        micMenu.DropDownItems.Add("(נטען...)");   // placeholder so the arrow shows
        menu.Items.Add(micMenu);
        menu.Items.Add(new ToolStripSeparator());

        var liveItem = new ToolStripMenuItem("תמלול חי (סטרימינג)") { Checked = true };
        var vadItem = new ToolStripMenuItem("משפט-משפט (הפסקה = הדבקה)");
        var batchItem = new ToolStripMenuItem("דיוק מקסימלי (תמלול אחד בסוף)");
        void SelectMode(Mode m)
        {
            _mode = m;
            liveItem.Checked = m == Mode.Live;
            vadItem.Checked = m == Mode.Vad;
            batchItem.Checked = m == Mode.Batch;
            SaveConfig();
        }
        SelectMode(_mode);   // reflect the persisted mode in the menu
        liveItem.Click += (_, _) => SelectMode(Mode.Live);
        vadItem.Click += (_, _) => SelectMode(Mode.Vad);
        batchItem.Click += (_, _) => SelectMode(Mode.Batch);
        menu.Items.Add(liveItem);
        menu.Items.Add(vadItem);
        menu.Items.Add(batchItem);
        menu.Items.Add(new ToolStripSeparator());
        var pasteItem = new ToolStripMenuItem("הדבק אוטומטית במקום הסמן") { Checked = _pasteMode, CheckOnClick = true };
        pasteItem.CheckedChanged += (_, _) => { _pasteMode = pasteItem.Checked; SaveConfig(); };
        menu.Items.Add(pasteItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("📄 תמלל קובץ וידאו/אודיו (דיוק מקסימלי)...", null, (_, _) => TranscribeFile());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("יציאה", null, (_, _) => Exit());
        _tray.ContextMenuStrip = menu;

        _hotkeyWnd = new HotkeyWindow(OnHotkey);
        // try a chain of hotkeys until one registers (some may be taken by IMEs etc.)
        (string name, uint mods, uint vk)[] candidates =
        {
            ("Ctrl+Alt+Space", MOD_CONTROL | MOD_ALT, VK_SPACE),
            ("Ctrl+Shift+Space", MOD_CONTROL | 0x4 /*SHIFT*/, VK_SPACE),
            ("Ctrl+Alt+M", MOD_CONTROL | MOD_ALT, 0x4D /*M*/),
            ("F9", 0, 0x78 /*F9*/),
        };
        string? active = null;
        foreach (var (name, mods, vk) in candidates)
        {
            if (HotkeyWindow.RegisterHotKey(_hotkeyWnd.Handle, HOTKEY_ID, mods, vk))
            {
                active = name;
                break;
            }
        }
        if (active == null)
        {
            Log.W("HOTKEY: all candidates failed!");
            _tray.ShowBalloonTip(5000, "MilaWin", "כל מקשי הקיצור תפוסים!", ToolTipIcon.Error);
        }
        else
        {
            Log.W($"HOTKEY: registered {active}");
            _tray.Text = $"MilaWin — {active} להקלטה";
            // show the hotkey unmissably at startup (balloons get suppressed)
            _overlay.ShowMessage($"MilaWin מוכן — {active}", 2500);
        }
        int devs = WaveInEvent.DeviceCount;
        for (int i = 0; i < devs; i++)
            Log.W($"MIC[{i}]: {WaveInEvent.GetCapabilities(i).ProductName}" + (i == 0 ? "  <- default" : ""));
    }

    void OnHotkey()
    {
        Log.W($"hotkey pressed (recording={_recording}, mode={_mode})");
        if (!_recording) StartRecording();
        else _ = StopAndTranscribe();
    }

    double _lastRms;   // for the overlay level meter

    void StartRecording()
    {
        try
        {
            _buf = new MemoryStream();
            _typed = "";
            _hadSpeech = false;
            _lastLoud = DateTime.MinValue;
            _sincePartial.Restart();
            void IngestPcm16(byte[] pcm, int count)
            {
                lock (_bufLock) _buf?.Write(pcm, 0, count);
                double sum = 0;
                for (int i = 0; i + 1 < count; i += 2)
                {
                    short s = BitConverter.ToInt16(pcm, i);
                    sum += (s / 32768.0) * (s / 32768.0);
                }
                double rms = Math.Sqrt(sum / Math.Max(1, count / 2));
                _lastRms = rms;
                if (rms > VAD_THRESHOLD)
                {
                    _lastLoud = DateTime.UtcNow;
                    _hadSpeech = true;
                }
            }

            if (_systemAudio)
            {
                var cap = new WasapiLoopbackCapture();
                _resPos = 0;
                var wf = cap.WaveFormat;
                cap.DataAvailable += (_, e) =>
                {
                    var pcm = AudioUtil.ToPcm16Mono16k(e.Buffer, e.BytesRecorded, wf, ref _resPos);
                    if (pcm.Length > 0) IngestPcm16(pcm, pcm.Length);
                };
                _wave = cap;
                Log.W($"recording started (mode={_mode}, source=SYSTEM loopback {wf.SampleRate}Hz {wf.Channels}ch {wf.Encoding})");
            }
            else
            {
                if (_micDevice >= WaveInEvent.DeviceCount) _micDevice = 0;
                var mic = new WaveInEvent
                {
                    DeviceNumber = _micDevice,
                    WaveFormat = new WaveFormat(16000, 16, 1),
                    BufferMilliseconds = 50,
                };
                mic.DataAvailable += (_, e) => IngestPcm16(e.Buffer, e.BytesRecorded);
                _wave = mic;
                Log.W($"recording started (mode={_mode}, mic=[{_micDevice}] {WaveInEvent.GetCapabilities(_micDevice).ProductName})");
            }
            _wave.StartRecording();
            _recording = true;
            _overlay.ShowRecording();
            _liveTimer = new System.Windows.Forms.Timer { Interval = 250 };
            _liveTimer.Tick += async (_, _) => await OnTick();
            _liveTimer.Start();
        }
        catch (Exception ex)
        {
            _tray.ShowBalloonTip(4000, "MilaWin", $"שגיאת מיקרופון: {ex.Message}", ToolTipIcon.Error);
        }
    }

    async Task OnTick()
    {
        if (!_recording) return;
        _overlay.UpdateLevel(_lastRms, VAD_THRESHOLD);     // live mic meter
        if (_mode == Mode.Batch) return;                   // batch: nothing until stop
        // VAD: enough silence after speech -> settle this utterance, keep listening
        if (_hadSpeech && (DateTime.UtcNow - _lastLoud).TotalMilliseconds > SIL_MS)
        {
            await FinalizeUtterance();
            return;
        }
        if (_mode == Mode.Live && _sincePartial.ElapsedMilliseconds >= 700)
        {
            _sincePartial.Restart();
            await LivePartial();
        }
    }

    // silence detected: transcribe the utterance, settle its final text, and
    // reset the buffer so the next sentence starts fresh (continuous dictation)
    async Task FinalizeUtterance()
    {
        if (_partialBusy) return;   // an in-flight partial — retry next tick
        _partialBusy = true;
        try
        {
            byte[] snap;
            lock (_bufLock)
            {
                snap = _buf?.ToArray() ?? Array.Empty<byte>();
                _buf = new MemoryStream();
            }
            _hadSpeech = false;
            if (snap.Length < 12000) { _typed = ""; return; }
            string text = await SttClient.Transcribe(snap);
            if (text.Length == 0) { _typed = ""; return; }
            Clipboard.SetText(text);
            if (_mode == Mode.Live)
            {
                TypeDelta(text + " ");
                _typed = "";                       // next utterance appends fresh
            }
            else if (_pasteMode)
            {
                TextInjector.PasteViaCtrlV();
                TextInjector.TypeUnicode(" ");
            }
        }
        catch { /* service hiccup — keep recording */ }
        finally { _partialBusy = false; }
    }

    // live mode: transcribe the buffer-so-far and retype only the changed tail
    async Task LivePartial()
    {
        if (_partialBusy || !_recording) return;
        _partialBusy = true;
        try
        {
            byte[] snap;
            lock (_bufLock) snap = _buf?.ToArray() ?? Array.Empty<byte>();
            if (snap.Length < 16000) return;      // wait for ≥0.5s of audio
            string text = await SttClient.Transcribe(snap);
            Log.W($"partial: {snap.Length}b rms={_lastRms:F4} -> '{text}'");
            if (!_recording || text.Length == 0) return;   // final pass will handle
            TypeDelta(text);
        }
        catch (Exception ex) { Log.W($"partial FAILED: {ex.Message}"); }
        finally { _partialBusy = false; }
    }

    void TypeDelta(string newText)
    {
        int common = 0;
        while (common < _typed.Length && common < newText.Length && _typed[common] == newText[common])
            common++;
        Log.W($"type: bs={_typed.Length - common} +'{newText.Substring(common)}'");
        TextInjector.Backspace(_typed.Length - common);
        TextInjector.TypeUnicode(newText.Substring(common));
        _typed = newText;
    }

    async Task StopAndTranscribe()
    {
        _recording = false;
        _liveTimer?.Stop();
        _liveTimer?.Dispose();
        _liveTimer = null;
        try { _wave?.StopRecording(); _wave?.Dispose(); } catch { }
        _wave = null;
        while (_partialBusy) await Task.Delay(30);   // let an in-flight partial land

        // settle whatever is left in the buffer since the last VAD finalize
        byte[] pcm;
        lock (_bufLock) { pcm = _buf?.ToArray() ?? Array.Empty<byte>(); _buf = null; }
        if (pcm.Length < 12000)
        {
            _overlay.HideNow();
            return;
        }
        _overlay.ShowWorking();
        try
        {
            string text = await SttClient.Transcribe(pcm);
            _overlay.HideNow();
            if (text.Length == 0) return;
            Clipboard.SetText(text);
            if (_mode == Mode.Live)
            {
                TypeDelta(text + " ");
                _typed = "";
            }
            else if (_pasteMode)
            {
                TextInjector.PasteViaCtrlV();
            }
        }
        catch (Exception ex)
        {
            _overlay.HideNow();
            _tray.ShowBalloonTip(5000, "MilaWin",
                $"התמלול נכשל — ה-STT service רץ? ({ex.Message})", ToolTipIcon.Error);
        }
    }

    // pick a media file -> transcribe offline (whisper-ivrit, beam 5, SRT) ->
    // open the resulting .txt. Runs scripts/transcribe_file.py in venv_omnivoice.
    void TranscribeFile()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "בחר קובץ וידאו או אודיו לתמלול",
            Filter = "מדיה|*.mp4;*.mkv;*.avi;*.mov;*.webm;*.mp3;*.wav;*.m4a;*.ogg;*.flac|הכל|*.*",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        string media = dlg.FileName;
        Log.W($"transcribe-file: {media}");
        _overlay.ShowMessage("📄 מתמלל קובץ... (ההודעה תופיע בסיום)", 4000);

        Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo(
                    @"C:\developer\newtts\venv_omnivoice\Scripts\python.exe",
                    $"\"C:\\developer\\newtts\\voice_agent\\scripts\\transcribe_file.py\" \"{media}\" --engine whisper")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                  RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
                psi.EnvironmentVariables["PYTHONUTF8"] = "1";
                using var p = Process.Start(psi)!;
                string outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                Log.W($"transcribe-file exit {p.ExitCode}: {outp.Split('\n').LastOrDefault(l => l.Trim().Length > 0)}");
                string txt = Path.ChangeExtension(media, ".txt");
                if (p.ExitCode == 0 && File.Exists(txt))
                {
                    Process.Start(new ProcessStartInfo("notepad.exe", $"\"{txt}\"") { UseShellExecute = true });
                    _tray.ShowBalloonTip(4000, "MilaWin",
                        $"התמלול מוכן: {Path.GetFileName(txt)} (+SRT)", ToolTipIcon.Info);
                }
                else
                {
                    _tray.ShowBalloonTip(6000, "MilaWin", "תמלול הקובץ נכשל — ראה log.txt", ToolTipIcon.Error);
                }
            }
            catch (Exception ex)
            {
                Log.W($"transcribe-file FAILED: {ex.Message}");
            }
        });
    }

    void Exit()
    {
        HotkeyWindow.UnregisterHotKey(_hotkeyWnd.Handle, HOTKEY_ID);
        _tray.Visible = false;
        Application.Exit();
    }
}

// hidden window that receives WM_HOTKEY
class HotkeyWindow : NativeWindow
{
    const int WM_HOTKEY = 0x0312;
    readonly Action _onHotkey;

    public HotkeyWindow(Action onHotkey)
    {
        _onHotkey = onHotkey;
        CreateHandle(new CreateParams());
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY) _onHotkey();
        base.WndProc(ref m);
    }

    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

// small always-on-top pill at the bottom-centre of the screen
class RecordingOverlay : Form
{
    readonly Label _label;

    public RecordingOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = System.Drawing.Color.FromArgb(30, 30, 34);
        Size = new System.Drawing.Size(190, 44);
        _label = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
            ForeColor = System.Drawing.Color.White,
            Font = new System.Drawing.Font("Segoe UI", 11, System.Drawing.FontStyle.Bold),
        };
        Controls.Add(_label);
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new System.Drawing.Point(wa.Left + (wa.Width - Width) / 2, wa.Bottom - Height - 24);
    }

    protected override bool ShowWithoutActivation => true;  // never steal focus

    public void ShowRecording() { _label.Text = "🎤 מקליט..."; ShowNoActivate(); }
    public void ShowWorking()   { _label.Text = "⏳ מתמלל..."; ShowNoActivate(); }
    public void HideNow()       { Hide(); }

    public void ShowMessage(string text, int ms)
    {
        _label.Text = text;
        ShowNoActivate();
        var t = new System.Windows.Forms.Timer { Interval = ms };
        t.Tick += (_, _) => { t.Stop(); t.Dispose(); Hide(); };
        t.Start();
    }

    // live mic meter: filled bars relative to the VAD threshold — instantly
    // shows whether the default mic actually hears the user
    public void UpdateLevel(double rms, double threshold)
    {
        if (!Visible) return;
        int bars = (int)Math.Clamp(rms / threshold * 2, 0, 8);
        _label.Text = "🎤 " + new string('▮', bars) + new string('▯', 8 - bars);
    }

    void ShowNoActivate()
    {
        if (!Visible) NativeShow(Handle);
        Refresh();
    }

    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    void NativeShow(IntPtr h) => ShowWindow(h, 8 /*SW_SHOWNA*/);
}

// ─────────────────────────── helpers ───────────────────────────
static class SttClient
{
    public static string Url =
        Environment.GetEnvironmentVariable("STT_URL") ?? "http://127.0.0.1:7877/stt";
    static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public static async Task<string> Transcribe(byte[] pcm16Mono16k)
    {
        var resp = await _http.PostAsync(Url, new ByteArrayContent(pcm16Mono16k));
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("text").GetString() ?? "").Trim();
    }
}

static class TextInjector
{
    // Ctrl+V into the currently focused window (clipboard already holds the text)
    public static void PasteViaCtrlV()
    {
        var inputs = new INPUT[4];
        inputs[0] = Key(0x11, false);  // Ctrl down
        inputs[1] = Key(0x56, false);  // V down
        inputs[2] = Key(0x56, true);   // V up
        inputs[3] = Key(0x11, true);   // Ctrl up
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    // type arbitrary text (incl. Hebrew) via KEYEVENTF_UNICODE — no clipboard involved
    public static void TypeUnicode(string text)
    {
        if (text.Length == 0) return;
        var inputs = new INPUT[text.Length * 2];
        for (int i = 0; i < text.Length; i++)
        {
            inputs[i * 2] = Unicode(text[i], false);
            inputs[i * 2 + 1] = Unicode(text[i], true);
        }
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent == 0)
            Log.W("SendInput BLOCKED (0 sent) — target window elevated (admin)? UIPI blocks injection");
    }

    public static void Backspace(int count)
    {
        if (count <= 0) return;
        var inputs = new INPUT[count * 2];
        for (int i = 0; i < count; i++)
        {
            inputs[i * 2] = Key(0x08, false);   // VK_BACK down
            inputs[i * 2 + 1] = Key(0x08, true);
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    static INPUT Unicode(char c, bool up) => new()
    {
        type = 1,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = c, dwFlags = (up ? 2u : 0u) | 4u /*UNICODE*/ } },
    };

    static INPUT Key(ushort vk, bool up) => new()
    {
        type = 1,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? 2u : 0u } },
    };

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint n, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
}

static class AudioUtil
{
    // any WASAPI format (float/pcm16, any rate/channels) -> mono 16k pcm16.
    // resPos carries the fractional resample position across chunks.
    public static byte[] ToPcm16Mono16k(byte[] buffer, int bytes, WaveFormat wf, ref double resPos)
    {
        int ch = wf.Channels;
        bool isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat;
        int bytesPer = isFloat ? 4 : 2;
        int frames = bytes / (bytesPer * ch);
        if (frames == 0) return Array.Empty<byte>();
        var mono = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            float acc = 0;
            for (int c = 0; c < ch; c++)
            {
                int off = (i * ch + c) * bytesPer;
                acc += isFloat ? BitConverter.ToSingle(buffer, off)
                               : BitConverter.ToInt16(buffer, off) / 32768f;
            }
            mono[i] = acc / ch;
        }
        double step = wf.SampleRate / 16000.0;
        var outSamples = new List<short>((int)(frames / step) + 2);
        while (resPos < frames)
        {
            outSamples.Add((short)Math.Clamp(mono[(int)resPos] * 32767f,
                                             short.MinValue, short.MaxValue));
            resPos += step;
        }
        resPos -= frames;
        var outBytes = new byte[outSamples.Count * 2];
        for (int i = 0; i < outSamples.Count; i++)
            BitConverter.GetBytes(outSamples[i]).CopyTo(outBytes, i * 2);
        return outBytes;
    }

    public static byte[] WavFileToPcm16Mono16k(string path)
    {
        using var reader = new AudioFileReader(path);   // float32 samples
        var resampled = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(reader, 16000);
        var mono = resampled.WaveFormat.Channels > 1
            ? new NAudio.Wave.SampleProviders.StereoToMonoSampleProvider(resampled)
            : (NAudio.Wave.ISampleProvider)resampled;
        var floats = new List<float>();
        var buf = new float[4096];
        int n;
        while ((n = mono.Read(buf, 0, buf.Length)) > 0)
            for (int i = 0; i < n; i++) floats.Add(buf[i]);
        var pcm = new byte[floats.Count * 2];
        for (int i = 0; i < floats.Count; i++)
        {
            short s = (short)Math.Clamp(floats[i] * 32767f, short.MinValue, short.MaxValue);
            BitConverter.GetBytes(s).CopyTo(pcm, i * 2);
        }
        return pcm;
    }
}
