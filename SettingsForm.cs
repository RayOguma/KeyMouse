namespace KeyMouse;

public sealed class SettingsForm : Form
{
    const string ActivationTarget = "activation";

    readonly Engine engine;
    AppSettings work;
    object? captureTarget;

    readonly Label activationKeyLabel = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 12, 3) };
    readonly Button activationButton = new() { Text = "変更", AutoSize = true };
    readonly ComboBox modeBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
    readonly CheckBox tapBox = new() { Text = "起動キーだけを押して離したときは、本来のキーとして入力する", AutoSize = true };
    readonly DataGridView grid = new();
    readonly NumericUpDown minSpeed = Num(10, 20000, 10);
    readonly NumericUpDown maxSpeed = Num(10, 20000, 50);
    readonly NumericUpDown accelMs = Num(0, 5000, 50);
    readonly NumericUpDown slowPct = Num(5, 100, 5);
    readonly NumericUpDown fastPct = Num(100, 1000, 10);
    readonly NumericUpDown scrollStep = Num(10, 1200, 10);
    readonly NumericUpDown scrollInterval = Num(10, 1000, 10);
    readonly CheckBox overlayBox = new() { Text = "マウスモード中は画面右下に表示を出す", AutoSize = true };
    readonly CheckBox startupBox = new() { Text = "Windows の起動時に自動で起動する", AutoSize = true };
    readonly Label status = new() { AutoSize = true, ForeColor = Color.RoyalBlue, Margin = new Padding(3, 6, 3, 6) };

    public event Action<AppSettings>? Saved;

    public SettingsForm(Engine engine, AppSettings current)
    {
        this.engine = engine;
        work = current.Clone();

        Text = "KeyMouse 設定";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Yu Gothic UI", 9.5f);
        ClientSize = new Size(620, 860);
        MinimumSize = new Size(520, 600);

        Build();
        LoadToControls();
        FormClosed += (_, _) => engine.CancelCapture();
    }

    static NumericUpDown Num(int min, int max, int step) =>
        new() { Minimum = min, Maximum = max, Increment = step, Width = 100 };

    void Build()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(580, 0),
            Margin = new Padding(3, 0, 3, 8),
            Text = "起動キーを押しながら割り当てたキーを押すと、マウスを操作できます。\n" +
                   "左クリックのキーを押したまま移動するとドラッグになります。\n" +
                   "キーは「物理的な位置」で登録されるので、キー配列を変えていても押した場所で反応します。",
        });

        // 起動キー
        var act = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 3 };
        act.Controls.Add(new Label { Text = "起動キー:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        act.Controls.Add(activationKeyLabel, 1, 0);
        act.Controls.Add(activationButton, 2, 0);
        act.Controls.Add(new Label { Text = "動作:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        act.Controls.Add(modeBox, 1, 1);
        act.SetColumnSpan(modeBox, 2);
        act.Controls.Add(tapBox, 0, 2);
        act.SetColumnSpan(tapBox, 3);
        modeBox.Items.AddRange(["押している間だけマウス操作（おすすめ）", "押すたびに マウス操作 ON／OFF を切り替え"]);
        modeBox.SelectedIndexChanged += (_, _) => tapBox.Enabled = modeBox.SelectedIndex == 0;
        activationButton.Click += (_, _) => StartCapture(ActivationTarget);
        root.Controls.Add(Group("起動キー", act));

        // キー割り当て
        grid.Dock = DockStyle.Fill;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.RowHeadersVisible = false;
        grid.ReadOnly = true;
        grid.MultiSelect = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.BackgroundColor = SystemColors.Window;
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "動作", FillWeight = 58 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "キー", FillWeight = 22 });
        grid.Columns.Add(new DataGridViewButtonColumn { HeaderText = "", Text = "変更", UseColumnTextForButtonValue = true, FillWeight = 10 });
        grid.Columns.Add(new DataGridViewButtonColumn { HeaderText = "", Text = "解除", UseColumnTextForButtonValue = true, FillWeight = 10 });
        foreach (var a in Enum.GetValues<MouseAction>())
        {
            int i = grid.Rows.Add(ActionLabels.Of(a), "");
            grid.Rows[i].Tag = a;
        }
        grid.CellContentClick += OnGridClick;
        var keysGroup = Group("キー割り当て", grid);
        keysGroup.AutoSize = false;
        root.Controls.Add(keysGroup);

        // 速度
        var sp = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 4 };
        AddNum(sp, 0, 0, "動き始めの速さ (px/秒)", minSpeed);
        AddNum(sp, 2, 0, "最高速度 (px/秒)", maxSpeed);
        AddNum(sp, 0, 1, "最高速度までの時間 (ミリ秒)", accelMs);
        AddNum(sp, 2, 1, "ゆっくり移動の速さ (%)", slowPct);
        AddNum(sp, 0, 2, "すばやく移動の速さ (%)", fastPct);
        AddNum(sp, 2, 2, "スクロール量 (120=1目盛り)", scrollStep);
        AddNum(sp, 0, 3, "連続スクロールの間隔 (ミリ秒)", scrollInterval);
        root.Controls.Add(Group("速さ", sp));

        // その他
        var other = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown };
        other.Controls.Add(overlayBox);
        other.Controls.Add(startupBox);
        root.Controls.Add(Group("その他", other));

        root.Controls.Add(status);

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "キャンセル", AutoSize = true };
        var save = new Button { Text = "保存", AutoSize = true };
        var reset = new Button { Text = "初期設定に戻す", AutoSize = true };
        cancel.Click += (_, _) => Close();
        save.Click += (_, _) => SaveAndClose();
        reset.Click += (_, _) =>
        {
            work = AppSettings.CreateDefault();
            LoadToControls();
            status.Text = "初期設定に戻しました（「保存」で反映されます）";
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        buttons.Controls.Add(reset);
        root.Controls.Add(buttons);

        AcceptButton = save;
        CancelButton = cancel;
        Controls.Add(root);
    }

    static GroupBox Group(string title, Control content)
    {
        var g = new GroupBox
        {
            Text = title,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8),
        };
        g.Controls.Add(content);
        return g;
    }

    static void AddNum(TableLayoutPanel p, int col, int row, string label, NumericUpDown num)
    {
        p.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, col, row);
        p.Controls.Add(num, col + 1, row);
    }

    void LoadToControls()
    {
        activationKeyLabel.Text = work.ActivationKey?.DisplayName ?? "(なし)";
        activationKeyLabel.Font = new Font(Font, FontStyle.Bold);
        modeBox.SelectedIndex = work.Mode == ActivationMode.Hold ? 0 : 1;
        tapBox.Checked = work.TapPassThrough;
        tapBox.Enabled = work.Mode == ActivationMode.Hold;
        RefreshGrid();

        SetNum(minSpeed, work.MinSpeed);
        SetNum(maxSpeed, work.MaxSpeed);
        SetNum(accelMs, work.AccelMs);
        SetNum(slowPct, work.SlowPercent);
        SetNum(fastPct, work.FastPercent);
        SetNum(scrollStep, work.ScrollStep);
        SetNum(scrollInterval, work.ScrollIntervalMs);
        overlayBox.Checked = work.ShowOverlay;
        startupBox.Checked = Startup.IsEnabled();
    }

    static void SetNum(NumericUpDown n, int v) => n.Value = Math.Clamp(v, n.Minimum, n.Maximum);

    void RefreshGrid()
    {
        foreach (DataGridViewRow row in grid.Rows)
        {
            var a = (MouseAction)row.Tag!;
            row.Cells[1].Value = work.Bindings.GetValueOrDefault(a)?.DisplayName ?? "(なし)";
        }
        activationKeyLabel.Text = work.ActivationKey?.DisplayName ?? "(なし)";
    }

    void OnGridClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0) return;
        var a = (MouseAction)grid.Rows[e.RowIndex].Tag!;
        if (e.ColumnIndex == 2)
        {
            StartCapture(a);
        }
        else if (e.ColumnIndex == 3)
        {
            work.Bindings[a] = null;
            RefreshGrid();
            status.Text = $"「{ActionLabels.Of(a)}」の割り当てを解除しました";
        }
    }

    void StartCapture(object target)
    {
        captureTarget = target;
        string name = target is MouseAction a ? ActionLabels.Of(a) : "起動キー";
        status.Text = $"「{name}」に割り当てるキーを押してください（Esc でキャンセル）";
        // フックの中から呼ばれるので、画面の更新は後回しにする
        engine.BeginCapture(k => BeginInvoke(() => OnCaptured(k)));
    }

    void OnCaptured(KeyDef k)
    {
        var target = captureTarget;
        captureTarget = null;
        if (target == null || IsDisposed) return;
        if (k.Vk == 0x1B)
        {
            status.Text = "キャンセルしました";
            return;
        }

        var cleared = new List<string>();
        if (target is MouseAction action)
        {
            if (work.ActivationKey?.Id == k.Id)
            {
                status.Text = $"「{k.DisplayName}」は起動キーに使われているので割り当てられません";
                return;
            }
            foreach (var other in Enum.GetValues<MouseAction>())
            {
                if (other != action && work.Bindings.GetValueOrDefault(other)?.Id == k.Id)
                {
                    work.Bindings[other] = null;
                    cleared.Add(ActionLabels.Of(other));
                }
            }
            work.Bindings[action] = k;
            status.Text = $"「{ActionLabels.Of(action)}」を「{k.DisplayName}」にしました";
        }
        else
        {
            foreach (var other in Enum.GetValues<MouseAction>())
            {
                if (work.Bindings.GetValueOrDefault(other)?.Id == k.Id)
                {
                    work.Bindings[other] = null;
                    cleared.Add(ActionLabels.Of(other));
                }
            }
            work.ActivationKey = k;
            status.Text = $"起動キーを「{k.DisplayName}」にしました";
        }
        if (cleared.Count > 0) status.Text += $"（重複していた {string.Join("、", cleared)} は解除）";
        RefreshGrid();
    }

    void SaveAndClose()
    {
        work.Mode = modeBox.SelectedIndex == 0 ? ActivationMode.Hold : ActivationMode.Toggle;
        work.TapPassThrough = tapBox.Checked;
        work.MinSpeed = (int)minSpeed.Value;
        work.MaxSpeed = Math.Max((int)maxSpeed.Value, work.MinSpeed);
        work.AccelMs = (int)accelMs.Value;
        work.SlowPercent = (int)slowPct.Value;
        work.FastPercent = (int)fastPct.Value;
        work.ScrollStep = (int)scrollStep.Value;
        work.ScrollIntervalMs = (int)scrollInterval.Value;
        work.ShowOverlay = overlayBox.Checked;

        try
        {
            if (startupBox.Checked != Startup.IsEnabled()) Startup.Set(startupBox.Checked);
        }
        catch (Exception ex)
        {
            MessageBox.Show("自動起動の設定に失敗しました: " + ex.Message, "KeyMouse");
        }

        Saved?.Invoke(work);
        Close();
    }
}
