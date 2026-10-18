// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text;
using PhotoFlow.Tagging.Ocr;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Analysis;

/// <summary>Entry lists from CSV (separators, quotes, Latin-1) and Excel .xlsx, Italian or English headers, crews, no header.</summary>
[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class EntryListImporterTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-entry-lists");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_csv_with_italian_headers_and_a_crew()
    {
        var path = Path.Combine(_temp.Path, "iscritti.csv");
        await File.WriteAllTextAsync(path, "Numero;Pilota;Copilota;Scuderia;Classe;Vettura\n27;Marco Ferri;;Lario Corse;Gr. A;Honda Civic\n112;Anna Riva;Bruno Sala;Gruppo Quattro;Gr. N;\"Renault Clio; RS\"\n;senza numero;;;;\n", Ct);
        var entries = await EntryListImporter.ReadAsync(path, Ct);
        Assert.Equal(2, entries.Count);
        var single = entries.Single(e => e.Number == "27");
        Assert.Equal(["Marco Ferri"], single.Drivers);
        Assert.Equal(("Lario Corse", "Gr. A", "Honda Civic"), (single.Team, single.Category, single.Car));
        var crew = entries.Single(e => e.Number == "112");
        Assert.True(crew.IsCrew);
        Assert.Equal(["Anna Riva", "Bruno Sala"], crew.Drivers);
        Assert.Equal("Renault Clio; RS", crew.Car);
    }

    [Fact]
    public async Task A_latin1_csv_with_commas_english_headers_and_drivers_in_one_cell()
    {
        var path = Path.Combine(_temp.Path, "entries.csv");
        await File.WriteAllBytesAsync(path, Encoding.Latin1.GetBytes("No.,Drivers,Team,Car\n#08,Nicolò Bianchi / Jürgen Weiß,Équipe Rossa,Alpine A110\n"), Ct);
        var entry = Assert.Single(await EntryListImporter.ReadAsync(path, Ct));
        Assert.Equal("8", entry.Number);
        Assert.Equal(["Nicolò Bianchi", "Jürgen Weiß"], entry.Drivers);
        Assert.Equal("Équipe Rossa", entry.Team);
    }

    [Fact]
    public void Without_a_header_the_usual_order_is_assumed()
    {
        var entries = EntryListImporter.Parse([["44", "Luca Sala", "Autodromo Racing", "GT", "Porsche 911"], ["8", "Sofia Moretti e Elena Conti", "Moretti Motorsport"]]);
        Assert.Equal(["44", "8"], entries.Select(e => e.Number));
        Assert.Equal(["Sofia Moretti", "Elena Conti"], entries[1].Drivers);
    }

    [Fact]
    public async Task An_xlsx_with_shared_and_inline_strings_and_numbers()
    {
        var path = Path.Combine(_temp.Path, "iscritti.xlsx");
        WriteXlsx(path);
        var entries = await EntryListImporter.ReadAsync(path, Ct);
        Assert.Equal(["27", "112"], entries.Select(e => e.Number));
        Assert.Equal(("Marco Ferri", "Lario Corse"), (entries[0].Drivers.Single(), entries[0].Team));
        Assert.Equal(["Anna Riva", "Bruno Sala"], entries[1].Drivers);
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>A minimal workbook as Excel writes it: workbook, relationships, shared strings, one sheet.</summary>
    private static void WriteXlsx(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string xml)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(xml);
        }

        const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        Add("xl/workbook.xml", $"""<workbook xmlns="{Main}" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Iscritti" sheetId="1" r:id="rId1"/></sheets></workbook>""");
        Add("xl/_rels/workbook.xml.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>""");
        Add("xl/sharedStrings.xml", $"""<sst xmlns="{Main}"><si><t>Numero</t></si><si><t>Pilota 1</t></si><si><t>Pilota 2</t></si><si><t>Team</t></si><si><t>Marco Ferri</t></si><si><t>Lario Corse</t></si><si><r><t>Anna </t></r><r><t>Riva</t></r></si></sst>""");
        Add("xl/worksheets/sheet1.xml", $"""
            <worksheet xmlns="{Main}"><sheetData>
            <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c><c r="C1" t="s"><v>2</v></c><c r="D1" t="s"><v>3</v></c></row>
            <row r="2"><c r="A2"><v>27</v></c><c r="B2" t="s"><v>4</v></c><c r="D2" t="s"><v>5</v></c></row>
            <row r="3"><c r="A3"><v>112</v></c><c r="B3" t="s"><v>6</v></c><c r="C3" t="inlineStr"><is><t>Bruno Sala</t></is></c><c r="D3" t="inlineStr"><is><t>Gruppo Quattro</t></is></c></row>
            </sheetData></worksheet>
            """);
    }
}
