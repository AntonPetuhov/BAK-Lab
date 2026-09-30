using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace InternalIntegration;

public sealed record TransformResult(byte[] Bytes, int Replacements, string[] RequisitionIds, int AmbiguousTests);

public sealed class XmlTransformer
{
    public TransformResult Transform(byte[] source)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var input = new MemoryStream(source);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = 64 * 1024 * 1024
        });
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new XmlException("Отсутствует корневой элемент.");
        if (root.Name.LocalName != "SafirMessage") throw new XmlException("Неизвестная структура XML.");
        var ns = root.Name.Namespace;
        var requisitions = root.Elements(ns + "Requisition").ToArray();
        var eligible = new HashSet<XElement>();
        var count = 0;
        foreach (var req in requisitions)
        {
            var units = req.Elements(ns + "ReqUnit").ToArray();
            if (units.Length != 1 || units[0].Attribute("AddressCode") == null) continue;
            foreach (var analysis in req.Elements(ns + "Reply").Elements(ns + "Sample").Elements(ns + "Analysis"))
            {
                eligible.Add(analysis);
                if ((string?)units[0].Attribute("AddressCode") != "32" ||
                    (string?)analysis.Attribute("TestMethodCode") != "IF_HIV_(2)") continue;
                var suffix = (string?)analysis.Parent!.Attribute("LaboratoryID") switch
                {
                    "1000097520IH" => "_1", "1000097520IG" => "_2", _ => null
                };
                if (suffix == null) continue;
                analysis.SetAttributeValue("TestMethodCode", "IF_HIV_(2)" + suffix);
                count++;
            }
        }
        var ambiguous = root.Descendants().Count(e => e.Name.LocalName == "Analysis" && !eligible.Contains(e));
        var ids = requisitions.Select(r => (string?)r.Attribute("RequisitionID") ?? "(отсутствует)").ToArray();
        if (count == 0) return new(source, 0, ids, ambiguous);
        var encoding = DetectEncoding(source, document.Declaration?.Encoding);
        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings
        {
            Encoding = encoding, Indent = false, OmitXmlDeclaration = document.Declaration == null,
            NewLineHandling = NewLineHandling.None, CloseOutput = false
        })) document.Save(writer);
        return new(output.ToArray(), count, ids, ambiguous);
    }

    private static Encoding DetectEncoding(byte[] b, string? declaration)
    {
        if (b.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })) return new UTF32Encoding(false, true, true);
        if (b.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) return new UTF32Encoding(true, true, true);
        if (b.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) return new UnicodeEncoding(false, true, true);
        if (b.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) return new UnicodeEncoding(true, true, true);
        if (b.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) return new UTF8Encoding(true, true);
        if (b.Length >= 4 && b[0] == 0 && b[1] == 0) return new UTF32Encoding(true, false, true);
        if (b.Length >= 4 && b[1] == 0 && b[2] == 0) return new UTF32Encoding(false, false, true);
        if (b.Length >= 2 && b[0] == 0) return new UnicodeEncoding(true, false, true);
        if (b.Length >= 2 && b[1] == 0) return new UnicodeEncoding(false, false, true);
        var enc = Encoding.GetEncoding(declaration ?? "utf-8", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        return enc.CodePage == 65001 ? new UTF8Encoding(false, true) : enc;
    }
}
