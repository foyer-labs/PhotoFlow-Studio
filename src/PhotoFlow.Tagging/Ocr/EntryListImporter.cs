// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using PhotoFlow.Core.Catalog;

namespace PhotoFlow.Tagging.Ocr;

/// <summary>
/// Reads an entry list (spec §8) from CSV (separator <c>;</c>, <c>,</c> or tab, quotes, UTF-8 or Latin-1) or Excel
/// <c>.xlsx</c> (first sheet; read with the standard library, decision F4-2). Columns are recognized by their header in
/// Italian or English (number, driver or drivers — also several driver columns —, team, category, car); without a
/// recognizable header the order number, drivers, team, category, car is assumed. Several drivers in one cell are split
/// on <c>/</c>, <c>;</c>, <c>&amp;</c>, <c>+</c> or " e ". Rows without a number are skipped.
/// </summary>
public static class EntryListImporter
{
    private static readonly string[] NumberHeaders = ["numero", "num", "n", "no", "nr", "n°", "#", "number", "car number", "race number", "numero gara"];
    private static readonly string[] DriverHeaders = ["pilota", "piloti", "driver", "drivers", "conduttore", "conduttori", "pilota 1", "pilota 2", "pilota 3", "driver 1", "driver 2", "driver 3", "co-driver", "codriver", "copilota", "navigatore", "secondo pilota"];
    private static readonly string[] TeamHeaders = ["team", "scuderia", "squadra", "concorrente", "entrant"];
    private static readonly string[] CategoryHeaders = ["categoria", "classe", "class", "category", "gruppo", "group", "division"];
    private static readonly string[] CarHeaders = ["vettura", "auto", "car", "modello", "model", "vehicle", "moto", "bike"];

    public static async Task<IReadOnlyList<Entry>> ReadAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var rows = Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
            ? await Task.Run(() => ReadXlsx(path), ct).ConfigureAwait(false)
            : ReadCsv(Decode(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false)));
        return Parse(rows);
    }

    /// <summary>Entries from rows of cells (the first row may be a header).</summary>
    public static IReadOnlyList<Entry> Parse(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return [];
        }

        var header = rows[0].Select(Normalize).ToList();
        int Find(string[] names) => header.FindIndex(h => names.Contains(h));
        var number = Find(NumberHeaders);
        var drivers = header.Select((h, i) => (h, i)).Where(p => DriverHeaders.Contains(p.h)).Select(p => p.i).ToList();
        var team = Find(TeamHeaders);
        var category = Find(CategoryHeaders);
        var car = Find(CarHeaders);
        var hasHeader = number >= 0;
        if (!hasHeader)
        {
            (number, drivers, team, category, car) = (0, [1], 2, 3, 4);
        }

        var entries = new List<Entry>();
        foreach (var row in rows.Skip(hasHeader ? 1 : 0))
        {
            string? Cell(int index) => index >= 0 && index < row.Count && row[index].Trim().Length > 0 ? row[index].Trim() : null;
            if (Cell(number) is not { } value || !value.Any(char.IsDigit))
            {
                continue;
            }

            var names = drivers.Select(Cell).OfType<string>()
                .SelectMany(d => d.Split(['/', ';', '&', '+'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .SelectMany(d => d.Split(" e ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Where(d => d.Length > 0).ToList();
            entries.Add(new Entry(0, 0, new string(value.Where(char.IsDigit).ToArray()).TrimStart('0') is { Length: > 0 } digits ? digits : "0",
                names, Cell(team), Cell(category), Cell(car)));
        }

        return [.. entries.DistinctBy(e => e.Number)];
    }

    /// <summary>CSV with the separator that splits the first line the most; quoted fields with doubled quotes.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> ReadCsv(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var firstLine = text.Split('\n')[0];
        var separator = new[] { ';', ',', '\t' }.MaxBy(c => firstLine.Count(ch => ch == c));
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == separator)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c == '\n')
            {
                row.Add(field.ToString().TrimEnd('\r'));
                field.Clear();
                if (row.Any(f => f.Length > 0))
                {
                    rows.Add(row);
                }

                row = [];
            }
            else
            {
                field.Append(c);
            }
        }

        row.Add(field.ToString().TrimEnd('\r'));
        if (row.Any(f => f.Length > 0))
        {
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>The first sheet of an .xlsx: shared strings, inline strings and numbers, cells placed by their reference.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> ReadXlsx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var shared = zip.GetEntry("xl/sharedStrings.xml") is { } sharedEntry
            ? Load(sharedEntry).Root!.Elements(main + "si").Select(si => string.Concat(si.Descendants(main + "t").Select(t => t.Value))).ToList()
            : [];

        // The first sheet of the workbook, through its relationship.
        var workbook = Load(zip.GetEntry("xl/workbook.xml") ?? throw new InvalidDataException("Not an Excel workbook."));
        var relationId = workbook.Root!.Element(main + "sheets")?.Elements(main + "sheet").FirstOrDefault()?.Attribute(relationships + "id")?.Value;
        var sheetPath = "xl/worksheets/sheet1.xml";
        if (relationId is not null && zip.GetEntry("xl/_rels/workbook.xml.rels") is { } rels)
        {
            XNamespace package = "http://schemas.openxmlformats.org/package/2006/relationships";
            var target = Load(rels).Root!.Elements(package + "Relationship").FirstOrDefault(r => r.Attribute("Id")?.Value == relationId)?.Attribute("Target")?.Value;
            if (target is not null)
            {
                sheetPath = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
            }
        }

        var sheet = Load(zip.GetEntry(sheetPath) ?? throw new InvalidDataException("The workbook has no sheet."));
        var rows = new List<IReadOnlyList<string>>();
        foreach (var rowElement in sheet.Descendants(main + "row"))
        {
            var cells = new SortedDictionary<int, string>();
            foreach (var cell in rowElement.Elements(main + "c"))
            {
                var column = ColumnIndex(cell.Attribute("r")?.Value ?? string.Empty, cells.Count == 0 ? 0 : cells.Keys.Max() + 1);
                var type = cell.Attribute("t")?.Value;
                var raw = cell.Element(main + "v")?.Value;
                cells[column] = type switch
                {
                    "s" when int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i < shared.Count => shared[i],
                    "inlineStr" => string.Concat(cell.Descendants(main + "t").Select(t => t.Value)),
                    _ when double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d.ToString(CultureInfo.InvariantCulture),
                    _ => raw ?? string.Empty,
                };
            }

            if (cells.Count > 0)
            {
                var row = new string[cells.Keys.Max() + 1];
                Array.Fill(row, string.Empty);
                foreach (var (column, value) in cells)
                {
                    row[column] = value;
                }

                rows.Add(row);
            }
        }

        return rows;

        static XDocument Load(ZipArchiveEntry entry)
        {
            using var stream = entry.Open();
            return XDocument.Load(stream);
        }
    }

    private static int ColumnIndex(string reference, int fallback)
    {
        var letters = new string(reference.TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant();
        if (letters.Length == 0)
        {
            return fallback;
        }

        var index = 0;
        foreach (var c in letters)
        {
            index = (index * 26) + (c - 'A' + 1);
        }

        return index - 1;
    }

    private static string Decode(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static string Normalize(string header) => header.Trim().Trim('.', ':').ToLowerInvariant();
}
