using System.Linq.Expressions;
using System.Reflection.Metadata;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace InternalIntegration;

public class XmlTransformer
{
    public static string ProcessXML(string xml_file)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore, // если DTD не нужен
            XmlResolver = null
        };

        XDocument xdoc;
        // Загружаем xml
        using (var reader = XmlReader.Create(xml_file, settings))
        {
            xdoc = XDocument.Load(reader);
        }

        // Загружаем xml
        //xdoc = XDocument.Load(xml_file);
        // получаем корневой узел
        var root = xdoc.Root;
        // количество замен в файле
        var changes_count = 0;

        if (root is not null)
        {
            var requisitions = root.Elements("Requisition").ToArray();
            foreach (var requisition in requisitions)
            {
                // ReqUnit
                XElement? requnit = requisition.Element("ReqUnit"); // отделение
                var addressCode = (string?)requnit?.Attribute("AddressCode");
                if (addressCode != "32")
                    continue;

                // Sample
                var samples = requisition.Elements("Reply").Elements("Sample").ToArray();
                foreach(XElement sample in samples)
                {
                    var labId = (string?)sample.Attribute("LaboratoryID");

                    // Analysis
                    var analysisElements = sample.Elements("Analysis");
                    foreach (XElement analysis in analysisElements)
                    {
                        var testMethodCode = (string?)analysis.Attribute("TestMethodCode");
                        // Преобразовывать нужно только подтверждающие тесты для ВИЧ, остальные - без изменений
                        if (testMethodCode != "IF_HIV_(2)")
                            continue;
                        // проверяем нужно ли добавлять суффикс
                        var suffix = labId switch
                        {
                            null => null,
                            _ when labId.EndsWith("IG", StringComparison.Ordinal) => "_1",
                            _ when labId.EndsWith("IH", StringComparison.Ordinal) => "_2",
                            _ => null
                        };
                        if (suffix == null) 
                            continue;

                        // Если есть суффикс, обновляем код теста
                        analysis.SetAttributeValue("TestMethodCode", "IF_HIV_(2)" + suffix);
                        changes_count++; // количество изменений
                    }
                }
            }

            // Замен нет - файл не трогаем, возвращаем исходный путь.
            if (changes_count == 0) 
                return xml_file;

            // сохраняем
            xdoc.Save(xml_file);
            return xml_file;

        }
        else { return xml_file; }
    }

}
