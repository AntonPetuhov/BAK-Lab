using System.Security.Cryptography;
using System.Xml;

namespace InternalIntegration;

// Один последовательный обход папки
public sealed class PairEngine : IDisposable
{
    private readonly ProcessingOptions options;
    private readonly FileLogger logger;
    private readonly FileStream instanceLock;

    // Позволяет тесту прервать публикацию в нужном месте без остановки настоящей службы.
    public Action<string>? Checkpoint { get; set; }

    public PairEngine(ProcessingOptions options, FileLogger logger)
    {
        this.options = options;
        this.logger = logger;
        // Второй процесс не должен одновременно обрабатывать ту же входную папку.
        instanceLock = new FileStream(Path.Combine(options.InputDirectory, ".processor.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
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
        if(single_xmls.Count > 0)
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
        var xml_name = Path.GetFileName(xml);

        XmlTransformer xml_result = new XmlTransformer();

        xml_result?.ProcessXML(xml);


    }



    private void Process_(string xml, PendingXml item, Dictionary<string, string> files, CancellationToken token)
    {
        var name = Path.GetFileName(xml);
        if (item.Result == null)
        {
            if (!Ready(xml, now)) return;
            // Разбор выполняется ДО ожидания PDF. Исходный XML здесь не изменяется.
            using var source = OpenSource(xml);
            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            var bytes = buffer.ToArray();
            item.Result = new XmlTransformer().Transform(bytes);


            item.SourceHash = SHA256.HashData(bytes);
            item.PreparedAt = now;
            Log(name, item, "Начало обработки; XML подготовлен в памяти");
            foreach (var result in item.Result.Results)
            {
                if (result.Changes.Length == 0) logger.Info($"Без изменений: {result.Reason}", name, result.RequisitionId);
                foreach (var change in result.Changes)
                    logger.Info($"Подготовлена замена: LaboratoryID={change.LaboratoryId}; {change.OldCode} -> {change.NewCode}; количество={change.Count}", name, result.RequisitionId);
            }
            if (item.Result.Results.Length == 0) Log(name, item, "Без изменений: заявки не найдены");
            if (item.Result.AmbiguousTests > 0) Log(name, item, $"Неоднозначная принадлежность тестов: {item.Result.AmbiguousTests}; замены для них не выполняются");
        }

        var pdf = files.GetValueOrDefault(Path.ChangeExtension(name, ".pdf"));
        // PDF мог появиться уже после получения списка файлов в начале обхода.
        var expectedPdf = Path.ChangeExtension(xml, ".pdf");
        if (pdf == null && File.Exists(expectedPdf)) pdf = expectedPdf;
        // Запоминаем появление PDF, чтобы не отправить XML отдельно, пока PDF дописывается.
        item.PdfSeen |= pdf != null;
        if (item.PdfSeen)
        {
            if (pdf == null || !Ready(pdf, now)) return;
        }
        else if (now - item.PreparedAt < TimeSpan.FromSeconds(options.PdfWaitTimeoutSeconds)) return;

        token.ThrowIfCancellationRequested();


        Publish(xml, pdf, item, token);
    }

    private void Publish(string xml, string? pdf, PendingXml item, CancellationToken token)
    {
        var name = Path.GetFileName(xml);
        // На время публикации запрещаем запись в источники; разрешаем их итоговое удаление.
        using var xmlSource = OpenSource(xml, allowDelete: true);
        if (!SHA256.HashData(xmlSource).AsSpan().SequenceEqual(item.SourceHash))
        {
            // Если отправитель изменил XML во время ожидания, старый результат нельзя публиковать.
            Log(name, item, "XML изменился во время ожидания; подготовка и ожидание будут повторены");
            pending.Remove(name);
            observations.Remove(xml);
            return;
        }
        using var pdfSource = pdf == null ? null : OpenSource(pdf, allowDelete: true);
        var outputXml = Path.Combine(options.OutputDirectory, name);
        var outputPdf = Path.Combine(options.OutputDirectory, Path.GetFileName(pdf ?? Path.ChangeExtension(xml, ".pdf")));
        // Даже совпадающие байты не делают существующий файл нашей незавершенной публикацией.
        if (File.Exists(outputXml) || File.Exists(outputPdf) || Directory.Exists(outputXml) || Directory.Exists(outputPdf)) throw new ConflictException();
        var tempXml = Path.Combine(options.OutputDirectory, $".{Guid.NewGuid():N}.xml.tmp");
        var tempPdf = Path.Combine(options.OutputDirectory, $".{Guid.NewGuid():N}.pdf.tmp");
        try
        {
            using (var output = new FileStream(tempXml, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(item.Result!.Bytes); output.Flush(true); }
            if (pdfSource != null)
            {
                using var output = new FileStream(tempPdf, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                // PDF копируется как поток байтов, его содержимое не разбирается.
                pdfSource.CopyTo(output);
                output.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            Checkpoint?.Invoke("Prepared");
            if (pdf != null)
            {
                File.Move(tempPdf, outputPdf, false);
                item.PublicationStarted = true;
                Checkpoint?.Invoke("AfterPdf");
            }
            // Появление итогового XML означает, что PDF (если он есть) уже опубликован.
            File.Move(tempXml, outputXml, false);
            item.PublicationStarted = true;
            Checkpoint?.Invoke("Published");
            Log(name, item, (pdf == null ? "XML опубликован без PDF: срок ожидания истек" : "Пара XML/PDF опубликована") + $"; замен={item.Result!.Replacements}");
            // Только после успешной публикации всех результатов разрешено удалять исходники.
            File.Delete(xml);
            if (pdf != null) File.Delete(pdf);
            Log(name, item, "Обработка завершена; исходники удалены");
            pending.Remove(name);
        }
        finally
        {
            // При обычной ошибке убираем только свои временные файлы, итоговые не трогаем.
            // После аварийного завершения процесса временные файлы могут остаться на диске.
            File.Delete(tempXml);
            File.Delete(tempPdf);
        }
    }



    public void Dispose() => instanceLock.Dispose();


}

public sealed class ConflictException : IOException;
