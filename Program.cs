namespace FloatTube;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        try
        {
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            MessageBox.Show("EXotica Floatube crashed:\n\n" + ex, "EXotica Floatube",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
