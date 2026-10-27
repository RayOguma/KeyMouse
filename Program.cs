namespace KeyMouse;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "KeyMouse_SingleInstance_8F3A", out bool created);
        if (!created)
        {
            MessageBox.Show("KeyMouse はすでに起動しています（タスクトレイを確認してください）。", "KeyMouse");
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}
