using System.Text;
using InternalIntegration;
using Xunit;

namespace InternalIntegration.Tests;

public sealed class EngineTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "InternalIntegrationTests-" + Guid.NewGuid().ToString("N"));
    private readonly ProcessingOptions options;
    private readonly FileLogger logger;
    private PairEngine engine;
    private DateTimeOffset now = DateTimeOffset.UtcNow;
    private static readonly byte[] Pdf = "%PDF-1.7\nsynthetic\n%%EOF"u8.ToArray();

    public EngineTests()
    {
        options = new ProcessingOptions { InputDirectory = Path.Combine(root, "in"), OutputDirectory = Path.Combine(root, "out"), FileStabilitySeconds = 1 };
        options.Validate();
        logger = new FileLogger(Path.Combine(root, "logs"));
        engine = new(options, logger);
    }
    private string Input(string name) => Path.Combine(options.InputDirectory, name);
    private string Output(string name) => Path.Combine(options.OutputDirectory, name);
    private void Tick(int seconds = 1) { now = now.AddSeconds(seconds); engine.Scan(now, CancellationToken.None); }
    private void Restart() { engine.Dispose(); engine = new(options, logger); }
    private void PutPair(byte[]? xml = null) { File.WriteAllBytes(Input("a.xml"), xml ?? TransformerTests.Xml()); File.WriteAllBytes(Input("a.pdf"), Pdf); }
    private string LogText() => string.Join("\n", Directory.GetFiles(Path.Combine(root, "logs"), "*.log").Select(File.ReadAllText));

    [Fact] public void ParsesBeforeWaitingAndTimeoutDoesNotResetOnRepeatedScans()
    {
        Assert.Equal(10, options.PdfWaitTimeoutSeconds);
        File.WriteAllBytes(Input("a.xml"), TransformerTests.Xml());
        Tick(); Tick();
        Assert.Contains("requisitionID=synthetic-1 | Подготовлена замена", LogText());
        Assert.False(File.Exists(Output("a.xml")));
        for (var i = 0; i < 9; i++) Tick();
        Assert.False(File.Exists(Output("a.xml")));
        Tick();
        Assert.True(File.Exists(Output("a.xml")));
        Assert.Contains("IF_HIV_(2)_1", File.ReadAllText(Output("a.xml")));
        Assert.False(File.Exists(Output("a.pdf")));
        Assert.False(File.Exists(Input("a.xml")));
        Assert.Contains("requisitionID=synthetic-1 | XML опубликован без PDF", LogText());
    }
    [Fact] public void RestartStartsNewTimeout()
    {
        File.WriteAllBytes(Input("a.xml"), TransformerTests.Xml());
        Tick(); Tick(); Tick(9); Restart();
        Tick(); Tick(); Tick(9);
        Assert.False(File.Exists(Output("a.xml")));
        Tick(); Assert.True(File.Exists(Output("a.xml")));
        Assert.Empty(Directory.GetFiles(root, "*.json", SearchOption.AllDirectories));
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public void ArrivalInEitherOrderAndRepeatedScans(bool xmlFirst)
    {
        File.WriteAllBytes(Input(xmlFirst ? "a.xml" : "a.pdf"), xmlFirst ? TransformerTests.Xml() : Pdf);
        Tick(); Tick(); Tick();
        Assert.False(File.Exists(Output("a.xml")));
        File.WriteAllBytes(Input(xmlFirst ? "a.pdf" : "a.xml"), xmlFirst ? Pdf : TransformerTests.Xml());
        Tick(); Tick(); Tick();
        Assert.True(File.Exists(Output("a.xml")));
        Assert.Equal(Pdf, File.ReadAllBytes(Output("a.pdf")));
        var log = LogText();
        for (var i = 0; i < 10; i++) Tick();
        Assert.Equal(log, LogText());
        Assert.False(File.Exists(Input("a.xml"))); Assert.False(File.Exists(Input("a.pdf")));
    }
    [Fact] public void PdfSeenBeforeDeadlineWaitsUntilWriterCloses()
    {
        File.WriteAllBytes(Input("a.xml"), TransformerTests.Xml()); Tick(); Tick();
        using (var writer = new FileStream(Input("a.pdf"), FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            writer.Write(Pdf); writer.Flush(); Tick(); Tick(20); Tick(20);
            Assert.False(File.Exists(Output("a.xml")));
            writer.WriteByte(10);
        }
        Tick(); Tick();
        Assert.Equal(Pdf.Concat(new byte[] { 10 }), File.ReadAllBytes(Output("a.pdf")));
    }
    [Fact] public void XmlWritingMustFinishBeforeParsing()
    {
        using (var writer = new FileStream(Input("a.xml"), FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            writer.Write(TransformerTests.Xml()); writer.Flush(); Tick(); Tick(20);
            Assert.DoesNotContain("XML подготовлен", LogText());
        }
        Tick(); Assert.Contains("XML подготовлен", LogText());
        Assert.False(File.Exists(Output("a.xml")));
        Tick(10); Assert.True(File.Exists(Output("a.xml")));
    }
    [Fact] public void XmlChangedWhileWaitingIsPreparedAgain()
    {
        File.WriteAllBytes(Input("a.xml"), TransformerTests.Xml()); Tick(); Tick();
        var updated = TransformerTests.Xml("39"); File.WriteAllBytes(Input("a.xml"), updated);
        Tick(10); Assert.False(File.Exists(Output("a.xml")));
        Tick(); Tick(); Tick(10);
        Assert.Equal(updated, File.ReadAllBytes(Output("a.xml")));
        Assert.Contains("XML изменился во время ожидания", LogText());
    }
    [Theory] [InlineData("xml")] [InlineData("pdf")]
    public void ConflictPreservesSourcesAndDoesNotOverwrite(string extension)
    {
        PutPair(); File.WriteAllText(Output("a." + extension), "existing");
        Tick(); Tick(); Tick(); Tick(20);
        Assert.True(File.Exists(Input("a.xml"))); Assert.True(File.Exists(Input("a.pdf")));
        Assert.Equal("existing", File.ReadAllText(Output("a." + extension)));
        Assert.Contains("requisitionID=synthetic-1 | Окончательная ошибка: ConflictException", LogText());
    }
    [Fact] public void MalformedXmlIsRejectedImmediatelyWithoutBlockingOtherPairs()
    {
        File.WriteAllText(Input("a.xml"), "<SafirMessage><private-patient-secret></wrong>");
        File.WriteAllBytes(Input("b.xml"), TransformerTests.Xml()); File.WriteAllBytes(Input("b.pdf"), Pdf);
        Tick(); Tick();
        Assert.Contains("requisitionID=не определён | Окончательная ошибка: XmlException", LogText());
        Assert.DoesNotContain("private-patient-secret", LogText());
        Tick(); Assert.True(File.Exists(Input("a.xml"))); Assert.True(File.Exists(Output("b.xml")));
    }
    [Fact] public void PartialPublicationRequiresManualResolutionAfterRestart()
    {
        PutPair(); engine.Checkpoint = p => { if (p == "AfterPdf") throw new IOException(); };
        Tick(); Tick(); Tick();
        Assert.True(File.Exists(Output("a.pdf"))); Assert.False(File.Exists(Output("a.xml")));
        Assert.True(File.Exists(Input("a.xml"))); Assert.True(File.Exists(Input("a.pdf")));
        Assert.Contains("публикация прервана, требуется ручной разбор", LogText());
        Restart(); Tick(); Tick(); Tick();
        Assert.Contains("ConflictException", LogText());
        Assert.False(File.Exists(Output("a.xml")));
        Assert.True(File.Exists(Input("a.xml"))); Assert.True(File.Exists(Input("a.pdf")));
    }
    [Fact] public void TemporaryFailureBeforePublicationCanRetry()
    {
        PutPair(); engine.Checkpoint = p => { if (p == "Prepared") throw new IOException(); };
        Tick(); Tick(); Tick();
        Assert.False(File.Exists(Output("a.pdf"))); Assert.True(File.Exists(Input("a.xml")));
        Assert.Contains("Временная ошибка", LogText());
        engine.Checkpoint = null; Tick(options.RetryDelaySeconds);
        Assert.True(File.Exists(Output("a.xml"))); Assert.False(File.Exists(Input("a.xml")));
    }
    [Fact] public void PdfIsPublishedBeforeXmlAndSourcesSurviveUntilBothOutputsExist()
    {
        PutPair();
        engine.Checkpoint = p =>
        {
            Assert.True(File.Exists(Input("a.xml"))); Assert.True(File.Exists(Input("a.pdf")));
            if (p == "AfterPdf") { Assert.True(File.Exists(Output("a.pdf"))); Assert.False(File.Exists(Output("a.xml"))); }
            if (p == "Published") Assert.True(File.Exists(Output("a.xml")));
        };
        Tick(); Tick(); Tick();
        Assert.False(File.Exists(Input("a.xml")));
    }
    [Fact] public void NoChangeWithDoctypeAndPdfAreByteExact()
    {
        var source = Encoding.UTF8.GetBytes("<!DOCTYPE SafirMessage SYSTEM '10repw.dtd'>\r\n<SafirMessage>" + TransformerTests.Req("39", TransformerTests.Sample("1000097520IH")) + "</SafirMessage>");
        PutPair(source); Tick(); Tick(); Tick();
        Assert.Equal(source, File.ReadAllBytes(Output("a.xml")));
        Assert.Equal(Pdf, File.ReadAllBytes(Output("a.pdf")));
        Assert.Contains("Без изменений: AddressCode=39", LogText());
    }
    [Fact] public void ChangedXmlIgnoresUnavailableDtd()
    {
        var source = Encoding.UTF8.GetBytes("<!DOCTYPE SafirMessage SYSTEM 'file:///definitely-missing/10repw.dtd'><SafirMessage>" + TransformerTests.Req("32", TransformerTests.Sample("1000097520IH")) + "</SafirMessage>");
        var result = new XmlTransformer().Transform(source);
        Assert.Equal(1, result.Replacements);
        Assert.DoesNotContain("DOCTYPE", Encoding.UTF8.GetString(result.Bytes));
    }
    [Fact] public void LogsAllChangesAndScopesRequisitions()
    {
        var source = "<SafirMessage>" + TransformerTests.Req("32", TransformerTests.Sample("1000097520IG") + TransformerTests.Sample("1000097520IH") + TransformerTests.Sample("1000097520IH"), "one") +
            TransformerTests.Req("39", TransformerTests.Sample("1000097520IG"), "two") + "</SafirMessage>";
        PutPair(Encoding.UTF8.GetBytes(source)); Tick(); Tick(); Tick();
        Assert.Contains("requisitionID=one | Подготовлена замена: LaboratoryID=1000097520IH; IF_HIV_(2) -> IF_HIV_(2)_1; количество=2", LogText());
        Assert.Contains("LaboratoryID=1000097520IG; IF_HIV_(2) -> IF_HIV_(2)_2; количество=1", LogText());
        Assert.Contains("requisitionID=two | Без изменений: AddressCode=39", LogText());
        Assert.DoesNotContain("requisitionID=two | Подготовлена замена", LogText());
    }
    [Fact] public void WaitingForOnePdfDoesNotBlockOtherPair()
    {
        File.WriteAllBytes(Input("a.xml"), TransformerTests.Xml());
        File.WriteAllBytes(Input("b.xml"), TransformerTests.Xml()); File.WriteAllBytes(Input("b.pdf"), Pdf);
        Tick(); Tick(); Tick();
        Assert.True(File.Exists(Output("b.xml"))); Assert.False(File.Exists(Output("a.xml")));
    }
    [Fact] public void LatePdfIsLeftForManualHandling()
    {
        File.WriteAllBytes(Input("a.xml"), TransformerTests.Xml()); Tick(); Tick(); Tick(10);
        File.WriteAllBytes(Input("a.pdf"), Pdf); Tick(); Tick();
        Assert.True(File.Exists(Input("a.pdf"))); Assert.False(File.Exists(Output("a.pdf")));
    }
    [Fact] public void SecondInstanceIsRejected() => Assert.Throws<IOException>(() => new PairEngine(options, logger));
    [Fact] public void ZeroTimeoutPublishesPreparedXmlImmediately()
    {
        options.PdfWaitTimeoutSeconds = 0;
        File.WriteAllBytes(Input("a.xml"), TransformerTests.Xml()); Tick(); Tick();
        Assert.True(File.Exists(Output("a.xml"))); Assert.False(File.Exists(Output("a.pdf")));
    }
    [Fact] public void UppercaseExtensionsArePreserved()
    {
        File.WriteAllBytes(Input("a.XML"), TransformerTests.Xml()); File.WriteAllBytes(Input("a.PDF"), Pdf);
        Tick(); Tick(); Tick();
        var names = Directory.GetFiles(options.OutputDirectory).Select(Path.GetFileName);
        Assert.Contains("a.XML", names); Assert.Contains("a.PDF", names);
    }
    [Fact] public void RetryLimitBlocksUntilRestart()
    {
        options.MaxRetryAttempts = 2; PutPair();
        engine.Checkpoint = p => { if (p == "Prepared") throw new IOException(); };
        Tick(); Tick(); Tick(); Tick(options.RetryDelaySeconds);
        engine.Checkpoint = null; Tick(20);
        Assert.False(File.Exists(Output("a.xml"))); Assert.True(File.Exists(Input("a.xml")));
        Assert.Contains("Окончательная ошибка: IOException; попытка=2", LogText());
        Restart(); Tick(); Tick(); Tick(); Assert.True(File.Exists(Output("a.xml")));
    }
    [Fact] public void LogFailureAfterPublicationLeavesSourcesForManualResolution()
    {
        PutPair(); FileStream? logLock = null;
        engine.Checkpoint = p =>
        {
            if (p == "Published") logLock = new FileStream(Directory.GetFiles(Path.Combine(root, "logs"), "*.log").Single(), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        };
        try
        {
            Tick(); Tick(); Assert.Throws<FileLogException>(() => Tick());
            Assert.True(File.Exists(Input("a.xml"))); Assert.True(File.Exists(Input("a.pdf")));
            Assert.True(File.Exists(Output("a.xml")));
        }
        finally { logLock?.Dispose(); }
        Restart(); Tick(); Tick(); Tick();
        Assert.True(File.Exists(Input("a.xml"))); Assert.Contains("ConflictException", LogText());
    }
    [Fact] public void CancellationPreservesSources()
    {
        PutPair(); using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => engine.Scan(now, cts.Token));
        Assert.True(File.Exists(Input("a.xml"))); Assert.True(File.Exists(Input("a.pdf")));
    }
    [Fact] public async Task WorkerFindsFilesAndStopsGracefully()
    {
        engine.Dispose(); using var worker = new Worker(options, logger);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            PutPair(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (File.Exists(Input("a.xml"))) await Task.Delay(100, deadline.Token);
            Assert.Equal(Pdf, File.ReadAllBytes(Output("a.pdf")));
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }
    public void Dispose() { engine.Dispose(); Directory.Delete(root, true); }
}
