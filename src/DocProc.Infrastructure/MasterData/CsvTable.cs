using System.Text;

namespace DocProc.Infrastructure.MasterData;

/// <summary>
/// A small RFC 4180 reader for master-data uploads (docs/11 §14 "CSV or JSON plus a column map"): the first row is the
/// header; fields are separated by the delimiter (<c>,</c> by default, <c>;</c> when the header holds more of them,
/// as German exports do), quoted with <c>"</c> (a doubled quote inside), and may span lines inside quotes. Bounded:
/// at most <see cref="MaxRows"/> rows, <see cref="MaxColumns"/> columns and <see cref="MaxFieldLength"/> characters per
/// field; past a bound it throws <see cref="CsvFormatException"/> rather than reading on.
/// </summary>
public static class CsvTable
{
    public const int MaxRows = 5000;
    public const int MaxColumns = 100;
    public const int MaxFieldLength = 4000;

    /// <summary>The header and the rows (each as many fields as it has).</summary>
    public static (IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> Rows) Parse(string text, char? delimiter = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        var firstLine = text.Split('\n', 2)[0];
        var sep = delimiter ?? (firstLine.Count(c => c == ';') > firstLine.Count(c => c == ',') ? ';' : ',');
        var records = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var fieldStarted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        Append(field, '"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    Append(field, c);
                }

                continue;
            }

            if (c == '"' && !fieldStarted)
            {
                quoted = true;
                fieldStarted = true;
            }
            else if (c == sep)
            {
                EndField(row, field);
                fieldStarted = false;
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                EndField(row, field);
                fieldStarted = false;
                EndRow(records, ref row);
            }
            else
            {
                Append(field, c);
                fieldStarted = true;
            }
        }

        if (quoted)
        {
            throw new CsvFormatException("A quoted field is not closed.");
        }

        if (field.Length > 0 || row.Count > 0)
        {
            EndField(row, field);
            EndRow(records, ref row);
        }

        if (records.Count == 0)
        {
            throw new CsvFormatException("The file has no header row.");
        }

        var header = records[0].Select(h => h.Trim()).ToList();
        return (header, records.Skip(1).Cast<IReadOnlyList<string>>().ToList());
    }

    private static void Append(StringBuilder field, char c)
    {
        if (field.Length >= MaxFieldLength)
        {
            throw new CsvFormatException($"A field is longer than {MaxFieldLength} characters.");
        }

        field.Append(c);
    }

    private static void EndField(List<string> row, StringBuilder field)
    {
        if (row.Count >= MaxColumns)
        {
            throw new CsvFormatException($"A row has more than {MaxColumns} columns.");
        }

        row.Add(field.ToString());
        field.Clear();
    }

    private static void EndRow(List<List<string>> records, ref List<string> row)
    {
        if (row.Count == 1 && row[0].Length == 0)
        {
            row = [];
            return; // an empty line
        }

        if (records.Count > MaxRows)
        {
            throw new CsvFormatException($"The file has more than {MaxRows} rows.");
        }

        records.Add(row);
        row = [];
    }
}

/// <summary>A CSV upload that does not read.</summary>
public sealed class CsvFormatException(string message) : FormatException(message);
