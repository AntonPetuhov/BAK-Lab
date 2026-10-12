using System.Text;
using System.Xml;
using System.Xml.Linq;
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
            OutputDirectory = kind == "same" ? path : Path.Combine(path, "output")
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
    public void RejectsMalformedAndUnresolvedEntities(string xml) => Assert.Throws<XmlException>(() => new XmlTransformer().Transform(Encoding.UTF8.GetBytes(xml)));
}


