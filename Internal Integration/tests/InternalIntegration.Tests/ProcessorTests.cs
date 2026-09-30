using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using InternalIntegration;

namespace InternalIntegration.Tests;

public sealed class TransformerTests
{
    [Theory] [InlineData("same")] [InlineData("nested")] [InlineData("root")]
    public void RejectsOverlappingDirectories(string kind)
    {
        var path = Path.Combine(Path.GetTempPath(), "synthetic-validation");
        var options = new ProcessingOptions
        {
            InputDirectory = kind == "root" ? Path.GetPathRoot(path)! : path,
            OutputDirectory = kind == "same" ? path : Path.Combine(path, "output"),
            StateDirectory = Path.Combine(Path.GetTempPath(), "synthetic-validation-state")
        };
        Assert.Throws<ArgumentException>(options.Validate);
    }
    internal static string Sample(string lab, string code = "IF_HIV_(2)") => $"<Sample LaboratoryID='{lab}'><Analysis TestMethodCode='{code}' /></Sample>";
    internal static string Req(string address, string samples, string id = "synthetic-1") => $"<Requisition RequisitionID='{id}'><ReqUnit AddressCode='{address}'/><Reply>{samples}</Reply></Requisition>";
    internal static byte[] Xml(string address = "32", string? samples = null) => Encoding.UTF8.GetBytes("<?xml version='1.0' encoding='utf-8'?>\r\n<SafirMessage>" + Req(address, samples ?? Sample("1000097520IG") + Sample("1000097520IH")) + "</SafirMessage>");
    [Fact] public void ReplacesAllTestsByLaboratoryRatherThanPosition()
    {
        var result = new XmlTransformer().Transform(Xml(samples: Sample("1000097520IG") + Sample("1000097520IH") + Sample("1000097520IG")));
        Assert.Equal(3, result.Replacements);
        var codes = XDocument.Parse(Encoding.UTF8.GetString(result.Bytes)).Descendants("Analysis").Select(a => (string?)a.Attribute("TestMethodCode"));
        Assert.Equal(new[] { "IF_HIV_(2)_2", "IF_HIV_(2)_1", "IF_HIV_(2)_2" }, codes);
    }
    [Theory]
    [InlineData("39", "1000097520IH", "IF_HIV_(2)")]
    [InlineData("32", "unknown", "IF_HIV_(2)")]
    [InlineData("32", "1000097520IH", "OTHER")]
    [InlineData("32", "1000097520IH", "IF_HIV_(2)_1")]
    [InlineData("32", "1000097520IG", "IF_HIV_(2)_2")]
    public void NoChangesPreserveExactBytes(string address, string lab, string code)
    {
        var bytes = Xml(address, Sample(lab, code));
        var result = new XmlTransformer().Transform(bytes);
        Assert.Same(bytes, result.Bytes);
        Assert.Equal(0, result.Replacements);
    }
    [Fact] public void MultipleRequisitionsAndNamespaceAreScoped()
    {
        var source = "<SafirMessage xmlns='urn:synthetic'>" + Req("32", Sample("1000097520IH"), "one") + Req("39", Sample("1000097520IG"), "two") + "</SafirMessage>";
        var result = new XmlTransformer().Transform(Encoding.UTF8.GetBytes(source));
        Assert.Equal(1, result.Replacements);
        XNamespace ns = "urn:synthetic";
        var doc = XDocument.Parse(Encoding.UTF8.GetString(result.Bytes));
        Assert.Equal("IF_HIV_(2)", (string?)doc.Descendants(ns + "Analysis").Last().Attribute("TestMethodCode"));
        Assert.Null(doc.Declaration);
    }
    [Fact] public void AmbiguousAndUnexpectedPathsRemainUnchanged()
    {
        var xml = "<SafirMessage><Requisition><ReqUnit AddressCode='32'/><ReqUnit AddressCode='39'/><Reply>" + Sample("1000097520IH") + "</Reply></Requisition><Other>" + Sample("1000097520IG") + "</Other></SafirMessage>";
        var bytes = Encoding.UTF8.GetBytes(xml);
        var result = new XmlTransformer().Transform(bytes);
        Assert.Equal(2, result.AmbiguousTests);
        Assert.Same(bytes, result.Bytes);
    }
    [Theory] [InlineData("utf-8")] [InlineData("utf-16")] [InlineData("windows-1251")]
    public void KeepsEncodingDeclarationNamespacesAndOtherValues(string encodingName)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(encodingName);
        var source = $"<?xml version='1.0' encoding='{encodingName}' standalone='yes'?>\r\n<SafirMessage xmlns='urn:test'><!--комментарий-->" + Req("32", Sample("1000097520IH")) + "</SafirMessage>";
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(source)).ToArray();
        var result = new XmlTransformer().Transform(bytes);
        using var stream = new MemoryStream(result.Bytes);
        var doc = XDocument.Load(stream);
        Assert.Equal(encodingName, doc.Declaration!.Encoding, ignoreCase: true);
        Assert.Equal("yes", doc.Declaration.Standalone);
        Assert.Contains("комментарий", encoding.GetString(result.Bytes));
        Assert.Equal("urn:test", doc.Root!.Name.NamespaceName);
        Assert.Equal(1, result.Replacements);
    }
    [Theory] [InlineData("<broken>")] [InlineData("<!DOCTYPE SafirMessage [<!ENTITY x SYSTEM 'file:///secret'>]><SafirMessage>&x;</SafirMessage>")]
    public void RejectsMalformedAndDtd(string xml) => Assert.Throws<XmlException>(() => new XmlTransformer().Transform(Encoding.UTF8.GetBytes(xml)));
}

public sealed class EngineTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "XmlPairTests-" + Guid.NewGuid().ToString("N"));
    private readonly ProcessingOptions options;
    private PairEngine engine;
    private DateTimeOffset now = DateTimeOffset.UtcNow;
    private static readonly byte[] Pdf = "%PDF-1.7\nsynthetic\n%%EOF"u8.ToArray();
    public EngineTests()
    {
        options = new ProcessingOptions { InputDirectory = Path.Combine(root, "in"), OutputDirectory = Path.Combine(root, "out"), StateDirectory = Path.Combine(root, "state"), FileStabilitySeconds = 1, PdfWaitTimeoutSeconds = 10 };
        options.Validate();
        engine = NewEngine();
    }
    private PairEngine NewEngine() => new(options, NullLogger.Instance);
    private string Input(string name) => Path.Combine(options.InputDirectory, name);
    private string Output(string name) => Path.Combine(options.OutputDirectory, name);
    private async Task Tick(int seconds = 1) { now = now.AddSeconds(seconds); await engine.ScanAsync(now, CancellationToken.None); }
    private void Restart() { engine.Dispose(); engine = NewEngine(); }
    private void PutPair(byte[]? xml = null) { File.WriteAllBytes(Input("a.xml"), xml ?? TransformerTests.Xml()); File.WriteAllBytes(Input("a.pdf"), Pdf); }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task ArrivalInEitherOrderAndRepeatedEvents(bool xmlFirst)
    {
        File.WriteAllBytes(Input(xmlFirst ? "a.xml" : "a.pdf"), xmlFirst ? TransformerTests.Xml() : Pdf);
        await Tick(); await Tick(); await Tick();
        Assert.False(File.Exists(Output("a.xml")));
        File.WriteAllBytes(Input(xmlFirst ? "a.pdf" : "a.xml"), xmlFirst ? Pdf : TransformerTests.Xml());
        await Tick(); await Tick(); await Tick();
        Assert.True(File.Exists(Output("a.xml")));
        Assert.Equal(Pdf, File.ReadAllBytes(Output("a.pdf")));
        var bytes = File.ReadAllBytes(Output("a.xml"));
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => engine.ScanAsync(now, CancellationToken.None)));
        Assert.Equal(bytes, File.ReadAllBytes(Output("a.xml")));
        Assert.False(File.Exists(Input("a.xml")));
        Assert.False(File.Exists(Input("a.pdf")));
    }
    [Fact] public async Task OtherDepartmentIsByteExactForBothFiles()
    {
        var bytes = TransformerTests.Xml("39"); PutPair(bytes);
        await Tick(); await Tick(); await Tick();
        Assert.Equal(bytes, File.ReadAllBytes(Output("a.xml")));
        Assert.Equal(Pdf, File.ReadAllBytes(Output("a.pdf")));
    }
    [Fact] public async Task TimeoutSurvivesRestartAndRepeatedEvents()
    {
        File.WriteAllBytes(Input("a.xml"), TransformerTests.Xml());
        await Tick(); await Tick();
        await Tick(6); Restart();
        await Tick(); await Tick();
        Assert.False(File.Exists(Output("a.xml")));
        await Tick(3);
        Assert.True(File.Exists(Output("a.xml")));
        Assert.False(File.Exists(Output("a.pdf")));
    }
    [Fact] public async Task PdfSeenBeforeDeadlineWaitsWhileLockedEvenAfterRestart()
    {
        File.WriteAllBytes(Input("a.xml"), TransformerTests.Xml());
        await Tick(); await Tick();
        using (var writer = new FileStream(Input("a.pdf"), FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            writer.Write(Pdf); writer.Flush();
            await Tick(); Restart();
            await Tick(20); await Tick(20); await Tick(20);
            Assert.False(File.Exists(Output("a.xml")));
            writer.WriteByte(10);
        }
        await Tick(); await Tick();
        Assert.Equal(Pdf.Concat(new byte[] { 10 }), File.ReadAllBytes(Output("a.pdf")));
    }
    [Theory] [InlineData("xml")] [InlineData("pdf")]
    public async Task ConflictPreservesSourcePair(string extension)
    {
        PutPair(); File.WriteAllText(Output("a." + extension), "existing");
        await Tick(); await Tick(); await Tick();
        Assert.True(File.Exists(Input("a.xml"))); Assert.True(File.Exists(Input("a.pdf")));
        Assert.Equal("existing", File.ReadAllText(Output("a." + extension)));
        Assert.Contains("\"Blocked\":true", File.ReadAllText(Directory.GetFiles(options.StateDirectory, "*.json").Single()));
    }
    [Fact] public async Task MalformedDoesNotStopOtherPairs()
    {
        PutPair("<broken>"u8.ToArray());
        File.WriteAllBytes(Input("b.xml"), TransformerTests.Xml()); File.WriteAllBytes(Input("b.pdf"), Pdf);
        await Tick(); await Tick(); await Tick();
        Assert.True(File.Exists(Input("a.xml"))); Assert.True(File.Exists(Input("a.pdf")));
        Assert.True(File.Exists(Output("b.xml")));
    }
    [Theory] [InlineData("Prepared")] [InlineData("pdfIntent")] [InlineData("AfterPdf")] [InlineData("xmlIntent")] [InlineData("Published")] [InlineData("XmlDeleted")]
    public async Task RecoversFromInterruption(string point)
    {
        PutPair();
        engine.Checkpoint = checkpoint => { if (checkpoint == point) throw new SimulatedCrash(); };
        await Tick(); await Tick();
        await Assert.ThrowsAsync<SimulatedCrash>(() => Tick());
        Restart(); await Tick();
        Assert.True(File.Exists(Output("a.xml"))); Assert.Equal(Pdf, File.ReadAllBytes(Output("a.pdf")));
        Assert.False(File.Exists(Input("a.xml"))); Assert.False(File.Exists(Input("a.pdf")));
        Assert.Empty(Directory.GetFiles(options.StateDirectory, "*.json"));
    }
    [Fact] public async Task ChangedPublishedPdfStopsRecoveryWithoutSourceLoss()
    {
        PutPair(); engine.Checkpoint = p => { if (p == "AfterPdf") throw new SimulatedCrash(); };
        await Tick(); await Tick(); await Assert.ThrowsAsync<SimulatedCrash>(() => Tick());
        File.WriteAllText(Output("a.pdf"), "external change"); Restart(); await Tick();
        Assert.True(File.Exists(Input("a.xml"))); Assert.True(File.Exists(Input("a.pdf")));
        Assert.False(File.Exists(Output("a.xml")));
    }
    [Fact] public void SecondInstanceIsRejected() => Assert.Throws<IOException>(() => NewEngine());
    [Fact] public async Task IdenticalExternalFileIsConflictEvenWithPublicationIntent()
    {
        PutPair(); engine.Checkpoint = p => { if (p == "pdfIntent") throw new SimulatedCrash(); };
        await Tick(); await Tick(); await Assert.ThrowsAsync<SimulatedCrash>(() => Tick());
        File.WriteAllBytes(Output("a.pdf"), Pdf);
        Restart(); await Tick();
        Assert.False(File.Exists(Output("a.xml")));
        Assert.True(File.Exists(Input("a.xml"))); Assert.True(File.Exists(Input("a.pdf")));
    }
    [Fact] public async Task UppercaseExtensionsArePreserved()
    {
        File.WriteAllBytes(Input("a.XML"), TransformerTests.Xml()); File.WriteAllBytes(Input("a.PDF"), Pdf);
        await Tick(); await Tick(); await Tick();
        Assert.Contains("a.XML", Directory.GetFiles(options.OutputDirectory).Select(Path.GetFileName));
        Assert.Contains("a.PDF", Directory.GetFiles(options.OutputDirectory).Select(Path.GetFileName));
    }
    [Fact] public async Task ChangingXmlRestartsStabilityObservation()
    {
        PutPair(); await Tick();
        File.AppendAllText(Input("a.xml"), " ");
        await Tick(); Assert.False(File.Exists(Output("a.xml")));
        await Tick(); await Tick(); Assert.True(File.Exists(Output("a.xml")));
    }
    [Fact] public async Task CorruptJournalStopsProcessingAndPreservesSources()
    {
        PutPair(); File.WriteAllText(Path.Combine(options.StateDirectory, "broken.json"), "{");
        await Tick(); await Tick(); await Tick();
        Assert.False(File.Exists(Output("a.xml"))); Assert.True(File.Exists(Input("a.xml")));
    }
    [Fact] public async Task TemporaryPublicationFailureRetriesWithoutLosingSources()
    {
        PutPair(); engine.Checkpoint = p => { if (p == "AfterPdf") throw new IOException("synthetic temporary error"); };
        await Tick(); await Tick(); await Tick();
        Assert.True(File.Exists(Input("a.xml"))); Assert.True(File.Exists(Input("a.pdf")));
        Assert.True(File.Exists(Output("a.pdf"))); Assert.False(File.Exists(Output("a.xml")));
        engine.Checkpoint = null;
        await Tick(); Assert.False(File.Exists(Output("a.xml")));
        await Tick(options.RetryDelaySeconds);
        Assert.True(File.Exists(Output("a.xml"))); Assert.False(File.Exists(Input("a.xml")));
    }
    [Fact] public async Task WorkerScansExistingInputAndStopsGracefully()
    {
        engine.Dispose(); options.ScanIntervalSeconds = 1;
        PutPair();
        using var worker = new Worker(options, NullLogger<Worker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (File.Exists(Input("a.xml"))) await Task.Delay(100, deadline.Token);
            Assert.Equal(Pdf, File.ReadAllBytes(Output("a.pdf")));
        }
        finally { await worker.StopAsync(CancellationToken.None); }
        // Dispose the worker before reacquiring the input lock.
        worker.Dispose(); engine = NewEngine();
    }
    [Fact] public async Task CancellationPreservesSources()
    {
        PutPair(); using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.ScanAsync(now, cts.Token));
        Assert.True(File.Exists(Input("a.xml"))); Assert.True(File.Exists(Input("a.pdf")));
    }
    public void Dispose() { engine.Dispose(); Directory.Delete(root, true); }
    private sealed class SimulatedCrash : Exception;
}
