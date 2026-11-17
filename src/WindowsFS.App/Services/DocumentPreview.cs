using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace WindowsFS.App.Services;

/// <summary>Extracts plain text from allowlisted ZIP/XML parts. Never evaluates formulas, links, or macros.</summary>
internal sealed class DocumentPreview(ZipArchive archive, CancellationToken token)
{
    private const int TextLimit = 64 * 1024;
    private const int PartLimit = 2 * 1024 * 1024;
    private long _expandedBytes;
    private readonly StringBuilder _text = new();
    private bool _truncated;

    public static string Read(Stream source, string extension, CancellationToken token)
    {
        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > 20_000) throw new InvalidDataException("Too many document parts.");
        var preview = new DocumentPreview(archive, token);
        switch (extension.ToLowerInvariant())
        {
            case ".docx": case ".docm": preview.Paragraphs(preview.Xml("word/document.xml")); break;
            case ".xlsx": case ".xlsm": preview.Workbook(); break;
            case ".pptx": case ".pptm": preview.Presentation(); break;
            case ".hwpx":
                var sections = archive.Entries.Where(entry => entry.FullName.StartsWith("Contents/section", StringComparison.Ordinal) &&
                    entry.FullName.EndsWith(".xml", StringComparison.Ordinal)).OrderBy(entry => PartNumber(entry.FullName)).ToArray();
                if (sections.Length == 0) throw new InvalidDataException("Missing document body.");
                foreach (var section in sections.Take(20)) { if (preview.Full) break; preview.Paragraphs(preview.Xml(section.FullName)); }
                preview._truncated |= sections.Length > 20;
                break;
            default: preview.OpenDocument(); break;
        }
        return preview._text.Length == 0 ? "문서 텍스트 없음" :
            "[문서 텍스트 · 서식 제외]\n\n" + preview._text + (preview._truncated ? "\n[미리보기 범위 제한]" : "");
    }

    private bool Full => _text.Length >= TextLimit;
    private void Append(string value)
    {
        token.ThrowIfCancellationRequested();
        var remaining = TextLimit - _text.Length;
        if (value.Length > remaining) _truncated = true;
        _text.Append(value.AsSpan(0, Math.Min(value.Length, remaining)));
    }

    private XDocument Xml(string name)
    {
        token.ThrowIfCancellationRequested();
        var entry = archive.GetEntry(name) ?? throw new InvalidDataException("Missing document part.");
        if (entry.Length > PartLimit || _expandedBytes + entry.Length > 8L * 1024 * 1024)
            throw new InvalidDataException("Expanded document limit exceeded.");
        using var input = entry.Open();
        using var memory = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            token.ThrowIfCancellationRequested();
            if (memory.Length + count > PartLimit) throw new InvalidDataException("Expanded part limit exceeded.");
            if ((_expandedBytes += count) > 8L * 1024 * 1024) throw new InvalidDataException("Expanded document limit exceeded.");
            memory.Write(buffer, 0, count);
        }
        memory.Position = 0;
        using var reader = XmlReader.Create(memory, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = PartLimit,
            MaxCharactersFromEntities = 0
        });
        return XDocument.Load(reader);
    }

    private void Paragraphs(XDocument document)
    {
        foreach (var paragraph in document.Descendants().Where(node => node.Name.LocalName == "p"))
        {
            if (Full) { _truncated = true; break; }
            foreach (var part in paragraph.Descendants())
            {
                if (part.Name.LocalName == "t") Append(part.Value);
                else if (part.Name.LocalName == "tab") Append("\t");
                else if (part.Name.LocalName is "br" or "lineBreak") Append("\n");
            }
            Append("\n");
        }
    }

    private Dictionary<string, string> Relationships(string owner)
    {
        var slash = owner.LastIndexOf('/');
        var directory = owner[..(slash + 1)];
        var document = Xml(directory + "_rels/" + owner[(slash + 1)..] + ".rels");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var relationship in document.Root?.Elements() ?? [])
        {
            var id = (string?)relationship.Attribute("Id");
            var target = (string?)relationship.Attribute("Target");
            if (id is null || target is null || (string?)relationship.Attribute("TargetMode") == "External" || target.Contains(':') || target.Contains('\\')) continue;
            var parts = new List<string>();
            foreach (var part in (target.StartsWith('/') ? target[1..] : directory + target).Split('/'))
            {
                if (part is "" or ".") continue;
                if (part == "..") { if (parts.Count == 0) throw new InvalidDataException("Invalid part path."); parts.RemoveAt(parts.Count - 1); }
                else parts.Add(part);
            }
            result[id] = string.Join('/', parts);
        }
        return result;
    }

    private void Workbook()
    {
        var shared = archive.GetEntry("xl/sharedStrings.xml") is null ? [] : Xml("xl/sharedStrings.xml")
            .Descendants().Where(node => node.Name.LocalName == "si")
            .Select(node => string.Concat(node.Descendants().Where(child => child.Name.LocalName == "t").Select(child => child.Value))).ToArray();
        var relationships = Relationships("xl/workbook.xml");
        var sheets = Xml("xl/workbook.xml").Descendants().Where(node => node.Name.LocalName == "sheet").ToArray();
        foreach (var sheet in sheets.Take(10))
        {
            if (Full) break;
            var id = sheet.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "id")?.Value;
            if (id is null || !relationships.TryGetValue(id, out var path)) continue;
            Append($"[{(string?)sheet.Attribute("name") ?? "시트"}]\n");
            var rows = Xml(path).Descendants().Where(node => node.Name.LocalName == "row").ToArray();
            foreach (var row in rows.Take(200))
            {
                if (Full) break;
                var cells = row.Elements().Where(node => node.Name.LocalName == "c").ToArray();
                foreach (var cell in cells.Take(50))
                {
                    var value = cell.Elements().FirstOrDefault(node => node.Name.LocalName == "v")?.Value ?? "";
                    if ((string?)cell.Attribute("t") == "s") value = int.TryParse(value, out var index) && index >= 0 && index < shared.Length ? shared[index] : "[문자열 없음]";
                    else if ((string?)cell.Attribute("t") == "inlineStr") value = string.Concat(cell.Descendants().Where(node => node.Name.LocalName == "t").Select(node => node.Value));
                    // Only cached formula values are displayed; no formula engine or external workbook access.
                    Append($"{(string?)cell.Attribute("r")}: {value}\t");
                }
                _truncated |= cells.Length > 50;
                Append("\n");
            }
            _truncated |= rows.Length > 200;
            Append("\n");
        }
        _truncated |= sheets.Length > 10 || Full;
    }

    private void Presentation()
    {
        var relationships = Relationships("ppt/presentation.xml");
        var slides = Xml("ppt/presentation.xml").Descendants().Where(node => node.Name.LocalName == "sldId").ToArray();
        var number = 0;
        foreach (var slide in slides.Take(20))
        {
            if (Full) break;
            var id = slide.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "id" && attribute.Name.NamespaceName.Length > 0)?.Value;
            if (id is null || !relationships.TryGetValue(id, out var path)) continue;
            Append($"[슬라이드 {++number}]\n");
            Paragraphs(Xml(path));
            Append("\n");
        }
        _truncated |= slides.Length > 20 || Full;
    }

    private void OpenDocument()
    {
        foreach (var paragraph in Xml("content.xml").Descendants().Where(node => node.Name.LocalName is "p" or "h"))
        {
            if (Full) { _truncated = true; break; }
            Append(paragraph.Value + "\n");
        }
    }

    private static int PartNumber(string name)
    {
        var digits = new string(Path.GetFileNameWithoutExtension(name).Where(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, out var number) ? number : int.MaxValue;
    }
}
