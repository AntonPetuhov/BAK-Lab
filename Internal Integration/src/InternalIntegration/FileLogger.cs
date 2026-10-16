using System.Text;

namespace InternalIntegration;

public sealed class FileLogger
{
    private readonly string root;
    private readonly object locker = new();

    public FileLogger(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) 
            throw new ArgumentException("Не задан каталог логов.");
        this.root = Path.GetFullPath(root, AppContext.BaseDirectory);
        Directory.CreateDirectory(this.root);
    }

    public void Service(string message) => Write("Service", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string category, string message)
    {
        lock (locker)
        {

            string directory = Path.Combine(root, category);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"{category}_{DateTime.Now:yyyy-MM-dd}.log");
            File.AppendAllText(path, $"{DateTime.Now} {message}{Environment.NewLine}", Encoding.UTF8);
        }
    }
}


