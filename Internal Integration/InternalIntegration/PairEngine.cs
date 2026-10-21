using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace InternalIntegration;

// Один последовательный обход папки
public class PairEngine //: IDisposable
{
    private readonly ProcessingOptions options;
    private readonly FileLogger logger;

    // Позволяет тесту прервать публикацию в нужном месте без остановки настоящей службы.
    public Action<string>? Checkpoint { get; set; }

    public PairEngine(ProcessingOptions options, FileLogger logger)
    {
        this.options = options;
        this.logger = logger;
    }

    public void Scan(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var allFiles = Directory.GetFiles(options.InputDirectory);
        // ключ - имя файла, значение - полный путь к файлу
        var files = allFiles.ToDictionary(filePath => Path.GetFileName(filePath), filePath => filePath, StringComparer.OrdinalIgnoreCase);

        // отберем только xml файлы
        var xmlFiles = allFiles.Where(path => string.Equals(Path.GetExtension(path), ".xml", StringComparison.OrdinalIgnoreCase));

        var single_xmls = new List<string>(); // xml без пары pdf

        logger.Service($"Получаем список файлов...");

        // проходимся по каждому
        foreach (var xml in xmlFiles)
        {
            token.ThrowIfCancellationRequested();

            var pdfPath = Path.ChangeExtension(xml, ".pdf"); // одноименный pdf файл

            // сначала обрабатываем те файлы, для которых есть парный pdf
            if (File.Exists(pdfPath))
            {
                Process(xml, pdfPath, token);
            }
            else
            {
                single_xmls.Add(xml);
                continue;
            }
        }

        // Если есть файлы xml без pdf, еще раз пройдемся по ним
        if (single_xmls.Count > 0)
        {
            foreach (var xml in single_xmls)
            {
                Task.Delay(TimeSpan.FromSeconds(options.RetryDelaySeconds), token); // еще ждем немного

                var pdfPath = Path.ChangeExtension(xml, ".pdf"); // одноименный pdf файл
                // перемещаем, даже если не дождались pdf
                if (File.Exists(pdfPath))
                    Process(xml, pdfPath, token);
                else
                    Process(xml, null, token);
            }
        }
    }

    // начинаем работу с файлом 
    private void Process(string xml, string? pdf, CancellationToken token)
    {
        logger.Service($"Обработка файла: {xml}");

        string xml_name = Path.GetFileName(xml);
        var xml_result = XmlTransformer.ProcessXML(xml);

        var outputXml = Path.Combine(options.OutputDirectory, xml_name);

        try
        {
            if (Directory.Exists(options.OutputDirectory))
            {
                if (pdf is not null)
                {
                    string? pdf_name = Path.GetFileName(pdf);
                    var outputPdf = Path.Combine(options.OutputDirectory, pdf_name);

                    logger.Service($"Копирование файла {xml_name} из: {options.InputDirectory} в: {options.CopyToDirectory}");
                    logger.Service($"Копирование файла {pdf_name} из: {options.InputDirectory} в: {options.CopyToDirectory}");
                    // копируем файлы в папку
                    var copyXml = Path.Combine(options.CopyToDirectory, xml_name);
                    var copyPdf = Path.Combine(options.CopyToDirectory, pdf_name);
                    File.Copy(xml, copyXml, true);
                    File.Copy(pdf, copyPdf, true);

                    logger.Service($"Перемещение файла {xml_name} из: {options.InputDirectory} в: {options.OutputDirectory}");
                    logger.Service($"Перемещение файла {pdf_name} из: {options.InputDirectory} в: {options.OutputDirectory}");
                    // перемещаем XML и PDF в папку для миса  
                    File.Move(xml, outputXml, true);
                    File.Move(pdf, outputPdf, true);
                    logger.Service($"Пара файлов xml/pdf успешно перенесена.");
                }
                else
                {
                    logger.Service($"Копирование файла {xml_name} из: {options.InputDirectory} в: {options.CopyToDirectory}");
                    var copyXml = Path.Combine(options.CopyToDirectory, xml_name);
                    File.Copy(xml, copyXml, true);
                    logger.Service($"Перемещение файла {xml_name} из: {options.InputDirectory} в: {options.OutputDirectory}");
                    //перемещаем XML и PDF в папку для миса  
                    File.Move(xml, outputXml, true);
                    logger.Service($"Файл xml успешно перенесен.");
                }
            }
            else
            {
                logger.Service("Целевой директории не существует. Требуется создать папку папку.");
            }
        }
        catch (Exception ex)
        {
            logger.Service(ex.ToString());
        }

    }
}
